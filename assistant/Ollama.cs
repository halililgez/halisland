using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Halisland.Assistant;

public sealed class AssistantOptions
{
    /// <summary>
    /// 8 GB VRAM'de qwen3:8b 8192 bağlamla tamamen GPU'da kalıyor; 16384'te
    /// CPU'ya taşıyor ve çok yavaşlıyor. Bu yüzden üst sınır 8192.
    /// </summary>
    public const int MaxContext = 8192;

    public static readonly string[] Providers = ["ollama", "anthropic", "openai", "gemini"];

    /// <summary>"ollama" (yerel), "anthropic", "openai" ya da "gemini".</summary>
    public string Provider { get; init; } = "ollama";

    /// <summary>Bulut sağlayıcının anahtarı (Halisland Windows Kimlik Bilgileri'nden okur).</summary>
    public string? ApiKey { get; init; }

    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";
    public string Model { get; init; } = "qwen3:8b";
    public int NumCtx { get; init; } = MaxContext;
    public string KeepAlive { get; init; } = "10m";

    /// <summary>Model seçilmemişse kullanılan model.</summary>
    public static string DefaultModel(string provider) => provider switch
    {
        "anthropic" => "claude-opus-5-5",
        "openai" => "gpt-5-mini",
        "gemini" => "gemini-2.5-flash",
        _ => "qwen3:8b",
    };

    public static AssistantOptions FromEnvironment()
    {
        static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

        var provider = Env("HALISLAND_PROVIDER")?.ToLowerInvariant() is { } p && Providers.Contains(p) ? p : "ollama";
        var ctx = int.TryParse(Env("HALISLAND_NUM_CTX"), out var n) ? n : MaxContext;
        return new AssistantOptions
        {
            Provider = provider,
            ApiKey = Env("HALISLAND_API_KEY"),
            OllamaUrl = Env("HALISLAND_OLLAMA_URL") ?? "http://127.0.0.1:11434",
            Model = Env("HALISLAND_MODEL") ?? DefaultModel(provider),
            NumCtx = Math.Clamp(ctx, 2048, MaxContext),
        };
    }
}

/// <summary>Yerel model (Ollama, http://127.0.0.1:11434).</summary>
public sealed class OllamaProvider(AssistantOptions options) : IChatProvider
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(options.OllamaUrl),
        // Akış uzun sürebilir (düşünme açıkken dakikalarca); iptal isteğe bağlı.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public string Id => "ollama";
    public string Label => "Ollama";
    public string Model => options.Model;
    public bool Local => true;
    public int ContextTokens => options.NumCtx;

    /// <summary>Kurulu modeller; Ollama'ya ulaşılamazsa Reachable false.</summary>
    public async Task<ProviderStatus> StatusAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var tags = await _http.GetFromJsonAsync("/api/tags", AppJson.Default.OllamaTags, timeout.Token);
            return new ProviderStatus(true, tags?.Models.Select(m => m.Name).ToArray() ?? []);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ProviderStatus(false, [], "Ollama kapalı ya da kurulu değil.");
        }
    }

    /// <summary>Modeli GPU'ya yükler. Sekme açılınca çağrılır.</summary>
    public async Task WarmupAsync(CancellationToken ct)
    {
        var body = new OllamaLoadRequest(options.Model, options.KeepAlive, new OllamaOptions(options.NumCtx));
        using var res = await _http.PostAsJsonAsync("/api/generate", body, AppJson.Default.OllamaLoadRequest, ct);
        if (!res.IsSuccessStatusCode)
            throw new ProviderException(await ReadErrorAsync(res, ct));
    }

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var body = new OllamaChatRequest(
            options.Model,
            Convert(messages),
            Stream: false,
            Think: false,
            KeepAlive: options.KeepAlive,
            Options: new OllamaOptions(options.NumCtx));
        using var res = await _http.PostAsJsonAsync("/api/chat", body, AppJson.Default.OllamaChatRequest, ct);
        if (!res.IsSuccessStatusCode)
            throw new ProviderException(await ReadErrorAsync(res, ct));
        var chunk = await res.Content.ReadFromJsonAsync(AppJson.Default.OllamaChatChunk, ct);
        return chunk?.Message?.Content?.Trim() ?? "";
    }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        bool think,
        IReadOnlyList<ToolDef> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new OllamaChatRequest(
            options.Model,
            Convert(messages),
            Stream: true,
            Think: think,
            KeepAlive: options.KeepAlive,
            Options: new OllamaOptions(options.NumCtx),
            Tools: tools.Count > 0
                ? [.. tools.Select(t => new OllamaTool("function", new OllamaFunctionDef(t.Name, t.Description, t.Schema)))]
                : null);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(body, AppJson.Default.OllamaChatRequest),
        };
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new ProviderException("Ollama'ya ulaşılamadı. Çalıştığından emin ol.");
        }
        using var _ = res;
        if (!res.IsSuccessStatusCode)
            throw new ProviderException(await ReadErrorAsync(res, ct));

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var n = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var chunk = JsonSerializer.Deserialize(line, AppJson.Default.OllamaChatChunk);
            if (chunk is null) continue;
            if (chunk.Error is { } e) throw new ProviderException(e);

            ToolCall[]? calls = chunk.Message?.ToolCalls is { Length: > 0 } tc
                ? [.. tc.Select(c => new ToolCall($"call_{n++}", c.Function.Name, c.Function.Arguments))]
                : null;
            double? speed = chunk is { Done: true, EvalCount: > 0, EvalDuration: > 0 }
                ? Math.Round(chunk.EvalCount.Value / (chunk.EvalDuration.Value / 1e9), 1)
                : null;
            yield return new ChatDelta(
                Text: chunk.Message?.Content is { Length: > 0 } c ? c : null,
                Thinking: chunk.Message?.Thinking is { Length: > 0 } th ? th : null,
                ToolCalls: calls,
                Speed: speed);
            if (chunk.Done) yield break;
        }
    }

    private static List<OllamaMessage> Convert(IReadOnlyList<ChatMessage> messages) =>
    [
        .. messages.Select(m => new OllamaMessage(
            m.Role,
            m.Content,
            ToolCalls: m.ToolCalls is { Length: > 0 } tc
                ? [.. tc.Select(c => new OllamaToolCall(new OllamaFunctionCall(c.Name, c.Arguments)))]
                : null,
            ToolName: m.ToolName)),
    ];

    private static async Task<string> ReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        try
        {
            if (JsonSerializer.Deserialize(text, AppJson.Default.OllamaError)?.Error is { } e)
                return e;
        }
        catch (JsonException)
        {
        }
        return $"Ollama {(int)res.StatusCode} döndürdü.";
    }
}
