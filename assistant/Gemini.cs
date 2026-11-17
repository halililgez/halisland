using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halisland.Assistant;

/// <summary>Gemini (Google Generative Language API).</summary>
public sealed class GeminiProvider : CloudProvider
{
    private readonly HttpClient _http = CreateHttp("https://generativelanguage.googleapis.com/v1beta/");

    public GeminiProvider(AssistantOptions options) : base(options)
    {
        _http.DefaultRequestHeaders.Add("x-goog-api-key", ApiKey);
    }

    public override string Id => "gemini";
    public override string Label => "Gemini";

    protected override async Task<string[]> ListModelsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("models?pageSize=1000", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw Fail(res, body);

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("models", out var models)) return [];
        return
        [
            .. models.EnumerateArray()
                .Where(m => m.TryGetProperty("supportedGenerationMethods", out var g)
                            && g.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                .Select(m => (m.GetProperty("name").GetString() ?? "").Replace("models/", "", StringComparison.Ordinal))
                .Where(IsChatModel)
                .OrderByDescending(id => id, StringComparer.Ordinal),
        ];
    }

    internal static bool IsChatModel(string id) =>
        id.StartsWith("gemini-", StringComparison.Ordinal)
        && !System.Text.RegularExpressions.Regex.IsMatch(id, "embedding|tts|image|live|audio|robotics|computer-use");

    public override async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var body = Request(messages, think: false, tools: []);
        using var res = await _http.PostAsync($"models/{Model}:generateContent", Json(body), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw Fail(res, text);
        var parts = JsonNode.Parse(text)?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
        return string.Concat(parts?
            .Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>()) ?? []).Trim();
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        bool think,
        IReadOnlyList<ToolDef> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = Request(messages, think, tools);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"models/{Model}:streamGenerateContent?alt=sse")
        {
            Content = Json(body),
        };
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw Fail(res, await res.Content.ReadAsStringAsync(ct));

        // Modelin parçaları (thoughtSignature'larıyla) araç turunda aynen geri gönderilir.
        var raw = new JsonArray();
        var calls = new List<ToolCall>();
        Stopwatch? sinceFirst = null;
        int? outputTokens = null;
        await foreach (var data in ReadSseAsync(res, ct))
        {
            var chunk = JsonNode.Parse(data);
            if (chunk?["usageMetadata"]?["candidatesTokenCount"]?.GetValue<int>() is { } used) outputTokens = used;
            if (chunk?["candidates"]?[0]?["content"]?["parts"] is not JsonArray parts) continue;
            sinceFirst ??= Stopwatch.StartNew();
            foreach (var part in parts)
            {
                if (part is null) continue;
                raw.Add(part.DeepClone());
                var text = part["text"]?.GetValue<string>();
                if (part["thought"]?.GetValue<bool>() == true)
                {
                    if (text is { Length: > 0 }) yield return new ChatDelta(Thinking: text);
                }
                else if (text is { Length: > 0 })
                {
                    yield return new ChatDelta(Text: text);
                }
                if (part["functionCall"] is JsonObject fc)
                {
                    var args = fc["args"]?.ToJsonString() ?? "{}";
                    calls.Add(new ToolCall(
                        $"call_{calls.Count}",
                        fc["name"]?.GetValue<string>() ?? "",
                        JsonDocument.Parse(args).RootElement.Clone()));
                }
            }
        }

        yield return new ChatDelta(
            ToolCalls: calls.Count > 0 ? [.. calls] : null,
            Speed: Speed(outputTokens, sinceFirst),
            Raw: calls.Count > 0 ? raw : null);
    }

    /// <summary>
    /// Önceki turlardan (veritabanından) gelen araç çağrıları. Gerçek imza yok;
    /// Google'ın bu durum için belgelediği atlama değeri konur.
    /// </summary>
    private static JsonArray PastCalls(string text, ToolCall[] calls)
    {
        var parts = new JsonArray();
        if (text.Length > 0) parts.Add((JsonNode)new JsonObject { ["text"] = text });
        foreach (var call in calls)
            parts.Add((JsonNode)new JsonObject
            {
                ["functionCall"] = new JsonObject
                {
                    ["name"] = call.Name, ["args"] = JsonNode.Parse(call.Arguments.GetRawText()),
                },
                ["thoughtSignature"] = "skip_thought_signature_validator",
            });
        return parts;
    }

    private static JsonObject Request(IReadOnlyList<ChatMessage> messages, bool think, IReadOnlyList<ToolDef> tools)
    {
        var contents = new JsonArray();
        JsonArray? results = null;
        foreach (var m in messages)
        {
            if (m.Role == "system") continue;
            if (m.Role == "tool")
            {
                results ??= [];
                results.Add((JsonNode)new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = m.ToolName,
                        ["response"] = new JsonObject { ["content"] = m.Content },
                    },
                });
                continue;
            }
            if (results is not null)
            {
                contents.Add((JsonNode)new JsonObject { ["role"] = "user", ["parts"] = results });
                results = null;
            }
            JsonArray parts = m.Raw is JsonArray raw
                ? (JsonArray)raw.DeepClone()
                : m.ToolCalls is { Length: > 0 } past
                    ? PastCalls(m.Content, past)
                    : new JsonArray(new JsonObject { ["text"] = m.Content });
            if (m.Content.Length == 0 && m.Raw is null && m.ToolCalls is not { Length: > 0 }) continue;
            contents.Add((JsonNode)new JsonObject { ["role"] = m.Role == "assistant" ? "model" : "user", ["parts"] = parts });
        }
        if (results is not null) contents.Add((JsonNode)new JsonObject { ["role"] = "user", ["parts"] = results });

        var body = new JsonObject { ["contents"] = contents };
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        if (system.Length > 0)
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) };
        if (think)
            body["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["includeThoughts"] = true } };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray([
                    .. tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.Schema.GetRawText()),
                    }),
                ]),
            });
        }
        return body;
    }

    /// <summary>Gemini geçersiz anahtarda 401 değil 400 döndürür.</summary>
    private ProviderException Fail(HttpResponseMessage res, string body) =>
        new(body.Contains("API_KEY_INVALID", StringComparison.Ordinal)
            ? Explain(401, null)
            : Explain((int)res.StatusCode, ErrorMessage(body)));

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
