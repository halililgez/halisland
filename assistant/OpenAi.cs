using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halisland.Assistant;

/// <summary>ChatGPT (OpenAI Chat Completions API).</summary>
public sealed class OpenAiProvider : CloudProvider
{
    private readonly HttpClient _http = CreateHttp("https://api.openai.com/v1/");

    public OpenAiProvider(AssistantOptions options) : base(options)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public override string Id => "openai";
    public override string Label => "ChatGPT";

    /// <summary>Akıl yürüten modeller (gpt-5, o3...) reasoning_effort alır; diğerleri reddeder.</summary>
    private bool Reasoning => Model.StartsWith("gpt-5", StringComparison.Ordinal) || Model.StartsWith('o');

    protected override async Task<string[]> ListModelsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("models", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(Explain((int)res.StatusCode, ErrorMessage(body)));

        using var doc = JsonDocument.Parse(body);
        return
        [
            .. doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(IsChatModel)
                .OrderByDescending(id => id, StringComparer.Ordinal),
        ];
    }

    /// <summary>Listede ses, görsel, gömme gibi sohbet dışı modeller de var.</summary>
    internal static bool IsChatModel(string id) =>
        (id.StartsWith("gpt-", StringComparison.Ordinal) || id.StartsWith("chatgpt-", StringComparison.Ordinal)
         || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1])))
        && !System.Text.RegularExpressions.Regex.IsMatch(id, "audio|realtime|tts|transcribe|image|search|instruct|embedding|codex|moderation");

    public override async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var body = Request(messages, think: false, tools: [], stream: false);
        using var res = await _http.PostAsync("chat/completions", Json(body), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(Explain((int)res.StatusCode, ErrorMessage(text)));
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString()?.Trim() ?? "";
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        bool think,
        IReadOnlyList<ToolDef> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = Request(messages, think, tools, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = Json(body) };
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ProviderException(Explain((int)res.StatusCode, ErrorMessage(await res.Content.ReadAsStringAsync(ct))));

        // Araç çağrıları parça parça gelir (index'e göre birleştirilir).
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        Stopwatch? sinceFirst = null;
        int? outputTokens = null;
        await foreach (var data in ReadSseAsync(res, ct))
        {
            var chunk = JsonNode.Parse(data);
            if (chunk?["usage"]?["completion_tokens"]?.GetValue<int>() is { } used) outputTokens = used;
            var delta = chunk?["choices"]?[0]?["delta"];
            if (delta is null) continue;
            sinceFirst ??= Stopwatch.StartNew();

            if (delta["content"]?.GetValue<string>() is { Length: > 0 } text) yield return new ChatDelta(Text: text);
            if (delta["tool_calls"] is JsonArray tc)
            {
                foreach (var c in tc)
                {
                    var i = c?["index"]?.GetValue<int>() ?? 0;
                    if (!calls.TryGetValue(i, out var call))
                        calls[i] = call = (c?["id"]?.GetValue<string>() ?? $"call_{i}", "", new StringBuilder());
                    if (c?["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
                        calls[i] = call = (call.Id, name, call.Args);
                    call.Args.Append(c?["function"]?["arguments"]?.GetValue<string>());
                }
            }
        }

        var result = calls.Values
            .Select(c => new ToolCall(c.Id, c.Name, ParseArgs(c.Args.ToString())))
            .ToArray();
        yield return new ChatDelta(ToolCalls: result.Length > 0 ? result : null, Speed: Speed(outputTokens, sinceFirst));
    }

    private JsonObject Request(IReadOnlyList<ChatMessage> messages, bool think, IReadOnlyList<ToolDef> tools, bool stream)
    {
        var list = new JsonArray();
        foreach (var m in messages)
        {
            var msg = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.ToolCalls is { Length: > 0 } tc)
            {
                msg["tool_calls"] = new JsonArray([
                    .. tc.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.GetRawText() },
                    }),
                ]);
            }
            if (m.Role == "tool") msg["tool_call_id"] = m.ToolCallId;
            list.Add((JsonNode)msg);
        }

        var body = new JsonObject { ["model"] = Model, ["messages"] = list, ["stream"] = stream };
        if (stream) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (Reasoning) body["reasoning_effort"] = think ? "medium" : "low";
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray([
                .. tools.Select(t => (JsonNode)new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = JsonNode.Parse(t.Schema.GetRawText()),
                    },
                }),
            ]);
        }
        return body;
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonElement ParseArgs(string json)
    {
        try
        {
            return JsonDocument.Parse(json.Length > 0 ? json : "{}").RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}
