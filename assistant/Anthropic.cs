using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halisland.Assistant;

/// <summary>
/// Claude (Anthropic Messages API). Resmi C# SDK kırpmaya (trim) uygun
/// olmadığı için tek dosyalık sürüm derlemesine giremiyor; API doğrudan HTTP
/// ile kullanılır.
/// </summary>
public sealed class AnthropicProvider : CloudProvider
{
    private const string ApiVersion = "2026-10-21";
    private const int MaxTokens = 16_000;

    private readonly HttpClient _http = CreateHttp("https://api.anthropic.com/v1/");

    public AnthropicProvider(AssistantOptions options) : base(options)
    {
        _http.DefaultRequestHeaders.Add("x-api-key", ApiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
    }

    public override string Id => "anthropic";
    public override string Label => "Claude";

    protected override async Task<string[]> ListModelsAsync(CancellationToken ct)
    {
        using var res = await _http.GetAsync("models?limit=1000", ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(Explain((int)res.StatusCode, ErrorMessage(body)));
        using var doc = JsonDocument.Parse(body);
        // API en yeni modeli başta verir.
        return [.. doc.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")];
    }

    public override async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var body = Request(messages, think: false, tools: [], stream: false, maxTokens: 1024);
        using var res = await _http.PostAsync("messages", Json(body), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new ProviderException(Explain((int)res.StatusCode, ErrorMessage(text)));
        using var doc = JsonDocument.Parse(text);
        return string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text")
            .Select(b => b.GetProperty("text").GetString())).Trim();
    }

    public override async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        bool think,
        IReadOnlyList<ToolDef> tools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = Request(messages, think, tools, stream: true, maxTokens: MaxTokens);
        using var req = new HttpRequestMessage(HttpMethod.Post, "messages") { Content = Json(body) };
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ProviderException(Explain((int)res.StatusCode, ErrorMessage(await res.Content.ReadAsStringAsync(ct))));

        // Bloklar (düşünce imzalarıyla) araç turunda aynen geri gönderilir.
        var blocks = new SortedDictionary<int, Block>();
        Stopwatch? sinceFirst = null;
        int? outputTokens = null;
        await foreach (var data in ReadSseAsync(res, ct))
        {
            var e = JsonNode.Parse(data);
            switch (e?["type"]?.GetValue<string>())
            {
                case "content_block_start":
                {
                    var cb = e["content_block"];
                    blocks[e["index"]!.GetValue<int>()] = new Block
                    {
                        Type = cb?["type"]?.GetValue<string>() ?? "text",
                        Id = cb?["id"]?.GetValue<string>(),
                        Name = cb?["name"]?.GetValue<string>(),
                        Data = cb?["data"]?.GetValue<string>(),
                    };
                    break;
                }
                case "content_block_delta":
                {
                    if (!blocks.TryGetValue(e["index"]!.GetValue<int>(), out var b)) break;
                    sinceFirst ??= Stopwatch.StartNew();
                    var d = e["delta"];
                    switch (d?["type"]?.GetValue<string>())
                    {
                        case "text_delta":
                            var text = d["text"]?.GetValue<string>() ?? "";
                            b.Text.Append(text);
                            yield return new ChatDelta(Text: text);
                            break;
                        case "thinking_delta":
                            var th = d["thinking"]?.GetValue<string>() ?? "";
                            b.Text.Append(th);
                            yield return new ChatDelta(Thinking: th);
                            break;
                        case "signature_delta":
                            b.Signature += d["signature"]?.GetValue<string>();
                            break;
                        case "input_json_delta":
                            b.Text.Append(d["partial_json"]?.GetValue<string>());
                            break;
                    }
                    break;
                }
                case "message_delta":
                    if (e["usage"]?["output_tokens"]?.GetValue<int>() is { } used) outputTokens = used;
                    break;
                case "error":
                    throw new ProviderException(
                        $"{Label}: {e["error"]?["message"]?.GetValue<string>() ?? "bilinmeyen hata"}");
            }
        }

        var calls = blocks.Values
            .Where(b => b.Type == "tool_use")
            .Select(b => new ToolCall(b.Id ?? "", b.Name ?? "", ParseJson(b.Text.ToString())))
            .ToArray();
        yield return new ChatDelta(
            ToolCalls: calls.Length > 0 ? calls : null,
            Speed: Speed(outputTokens, sinceFirst),
            Raw: calls.Length > 0 ? ToContent(blocks.Values) : null);
    }

    private sealed class Block
    {
        public string Type = "text";
        public string? Id;
        public string? Name;
        public string? Data;
        public string Signature = "";
        public readonly StringBuilder Text = new();
    }

    /// <summary>Akışta biriken bloklardan asistan mesajının içeriği.</summary>
    private static JsonArray ToContent(IEnumerable<Block> blocks)
    {
        var list = new JsonArray();
        foreach (var b in blocks)
        {
            JsonObject? block = b.Type switch
            {
                "thinking" => new JsonObject
                {
                    ["type"] = "thinking", ["thinking"] = b.Text.ToString(), ["signature"] = b.Signature,
                },
                "redacted_thinking" => new JsonObject { ["type"] = "redacted_thinking", ["data"] = b.Data },
                "tool_use" => new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = b.Id, ["name"] = b.Name,
                    ["input"] = JsonNode.Parse(ParseJson(b.Text.ToString()).GetRawText()),
                },
                "text" when b.Text.Length > 0 => new JsonObject { ["type"] = "text", ["text"] = b.Text.ToString() },
                _ => null,
            };
            if (block is not null) list.Add((JsonNode)block);
        }
        return list;
    }

    private static JsonElement ParseJson(string json)
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

    /// <summary>
    /// Sistem istemi ayrı alana gider; ardışık araç sonuçları tek kullanıcı
    /// mesajında toplanır (API her tool_use için bir tool_result ister).
    /// </summary>
    private JsonObject Request(IReadOnlyList<ChatMessage> messages, bool think, IReadOnlyList<ToolDef> tools, bool stream, int maxTokens)
    {
        var list = new JsonArray();
        JsonArray? results = null;
        foreach (var m in messages)
        {
            if (m.Role == "system") continue;
            if (m.Role == "tool")
            {
                results ??= [];
                results.Add((JsonNode)new JsonObject
                {
                    ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content,
                });
                continue;
            }
            if (results is not null)
            {
                list.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = results });
                results = null;
            }
            if (m.Role == "assistant" && m.Raw is JsonArray raw)
                list.Add((JsonNode)new JsonObject { ["role"] = "assistant", ["content"] = raw.DeepClone() });
            else if (m.Content.Length > 0)
                list.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }
        if (results is not null) list.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = results });

        var body = new JsonObject { ["model"] = Model, ["max_tokens"] = maxTokens, ["messages"] = list };
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        if (system.Length > 0) body["system"] = system;
        if (stream) body["stream"] = true;
        // Düşünce özeti istenmezse yeni modellerde boş gelir. Kapalıyken alan hiç
        // gönderilmez (Opus 5.5'te düşünme hep açık; "disabled" 400 döndürür).
        if (think) body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray([
                .. tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = JsonNode.Parse(t.Schema.GetRawText()),
                }),
            ]);
        }
        return body;
    }

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
