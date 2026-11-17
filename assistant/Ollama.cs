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

    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";
    public string Model { get; init; } = "qwen3:8b";
    public int NumCtx { get; init; } = MaxContext;
    public string KeepAlive { get; init; } = "10m";

    public static AssistantOptions FromEnvironment()
    {
        static string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

        var ctx = int.TryParse(Env("HALISLAND_NUM_CTX"), out var n) ? n : MaxContext;
        return new AssistantOptions
        {
            OllamaUrl = Env("HALISLAND_OLLAMA_URL") ?? "http://127.0.0.1:11434",
            Model = Env("HALISLAND_MODEL") ?? "qwen3:8b",
            NumCtx = Math.Clamp(ctx, 2048, MaxContext),
        };
    }
}

public sealed class OllamaException(string message) : Exception(message);

public sealed class OllamaClient(HttpClient http, AssistantOptions options)
{
    /// <summary>Kurulu modeller; Ollama'ya ulaşılamazsa null.</summary>
    public async Task<string[]?> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var tags = await http.GetFromJsonAsync("/api/tags", AppJson.Default.OllamaTags, timeout.Token);
            return tags?.Models.Select(m => m.Name).ToArray() ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Modeli GPU'ya yükler. Sekme açılınca çağrılır.</summary>
    public async Task WarmupAsync(CancellationToken ct)
    {
        var body = new OllamaLoadRequest(options.Model, options.KeepAlive, new OllamaOptions(options.NumCtx));
        using var res = await http.PostAsJsonAsync("/api/generate", body, AppJson.Default.OllamaLoadRequest, ct);
        if (!res.IsSuccessStatusCode)
            throw new OllamaException(await ReadErrorAsync(res, ct));
    }

    public async IAsyncEnumerable<OllamaChatChunk> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        bool think,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new OllamaChatRequest(
            options.Model,
            messages,
            Stream: true,
            Think: think,
            KeepAlive: options.KeepAlive,
            Options: new OllamaOptions(options.NumCtx));

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(body, AppJson.Default.OllamaChatRequest),
        };
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new OllamaException(await ReadErrorAsync(res, ct));

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var chunk = JsonSerializer.Deserialize(line, AppJson.Default.OllamaChatChunk);
            if (chunk is null) continue;
            yield return chunk;
            if (chunk.Done || chunk.Error is not null) yield break;
        }
    }

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
