using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halisland.Assistant;

// ---------- Arayüzle konuşulan türler (camelCase) ----------

/// <summary>Yeni bir kullanıcı mesajı. Sohbet kimliği yoksa yeni sohbet açılır.</summary>
public sealed record ChatRequest(string Text, long? ConversationId = null, bool Think = false);

/// <summary>
/// Backend ve sağlayıcının durumu. <c>Reachable</c>: sağlayıcıya ulaşıldı (Ollama
/// açık ya da bulut anahtarı geçerli); değilse <c>Error</c> nedenini söyler.
/// </summary>
public sealed record HealthResponse(
    bool Ok,
    string Version,
    string Provider,
    bool Local,
    bool Reachable,
    string? Error,
    string Model,
    bool ModelInstalled,
    string[] Models);

/// <summary>Geçmiş listesindeki sohbet. <c>Updated</c>: Unix ms.</summary>
public sealed record ConversationInfo(long Id, string Title, long Updated, int Count);

public sealed record StoredMessage(
    long Id,
    string Role,
    string Content,
    string? Thinking,
    string? Error,
    double? Speed);

/// <summary>Sohbetler arası hatırlanan bilgi.</summary>
public sealed record Memory(long Id, string Text, long Created);

/// <summary>
/// /chat akışındaki tek satır (NDJSON). Tür:
/// "conversation" (sohbet kimliği; ilk satır), "thinking" (düşünce parçası),
/// "token" (cevap parçası), "tool" (araç çalıştı; Text kullanıcıya gösterilecek özet; Approved false ise iş yapılmadı),
/// "confirm" (araç kullanıcı onayı bekliyor: Id, Message soru, Text ayrıntı),
/// "confirm-end" (onay cevaplandı ya da süresi doldu: Id, Approved),
/// "retry" (model araç çağırmadan bir iş yaptığını söyledi; yazılan cevap silinir, yenisi gelir),
/// "done" (cevap bitti, istatistikler), "title" (sohbetin yeni başlığı) ya da "error".
/// </summary>
public sealed record StreamEvent(
    string Type,
    string? Text = null,
    long? ConversationId = null,
    int? Tokens = null,
    double? TokensPerSec = null,
    string? Message = null,
    string? Id = null,
    bool? Approved = null);

/// <summary>Onay bekleyen araca kullanıcının cevabı (POST /confirm/{id}).</summary>
public sealed record ConfirmRequest(bool Approved);

// ---------- Ollama API türleri (snake_case) ----------

public sealed record OllamaOptions(
    [property: JsonPropertyName("num_ctx")] int NumCtx);

public sealed record OllamaFunctionCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] JsonElement Arguments);

public sealed record OllamaToolCall(
    [property: JsonPropertyName("function")] OllamaFunctionCall Function);

/// <summary>Ollama'ya giden ve gelen mesaj (araç çağrıları dahil).</summary>
public sealed record OllamaMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("thinking")] string? Thinking = null,
    [property: JsonPropertyName("tool_calls")] OllamaToolCall[]? ToolCalls = null,
    [property: JsonPropertyName("tool_name")] string? ToolName = null);

public sealed record OllamaFunctionDef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")] JsonElement Parameters);

public sealed record OllamaTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OllamaFunctionDef Function);

public sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("think")] bool Think,
    [property: JsonPropertyName("keep_alive")] string KeepAlive,
    [property: JsonPropertyName("options")] OllamaOptions Options,
    [property: JsonPropertyName("tools")] OllamaTool[]? Tools = null);

/// <summary>Modeli belleğe yükler (boş istem); ilk cevabı beklemek zorunda kalmayalım.</summary>
public sealed record OllamaLoadRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("keep_alive")] string KeepAlive,
    [property: JsonPropertyName("options")] OllamaOptions Options);

public sealed record OllamaChatChunk(
    [property: JsonPropertyName("message")] OllamaMessage? Message,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("eval_count")] int? EvalCount,
    [property: JsonPropertyName("eval_duration")] long? EvalDuration,
    [property: JsonPropertyName("error")] string? Error);

public sealed record OllamaModel([property: JsonPropertyName("name")] string Name);

public sealed record OllamaTags([property: JsonPropertyName("models")] OllamaModel[] Models);

public sealed record OllamaError([property: JsonPropertyName("error")] string? Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ConfirmRequest))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(ConversationInfo[]))]
[JsonSerializable(typeof(StoredMessage[]))]
[JsonSerializable(typeof(Memory[]))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaLoadRequest))]
[JsonSerializable(typeof(OllamaChatChunk))]
[JsonSerializable(typeof(OllamaTags))]
[JsonSerializable(typeof(OllamaError))]
[JsonSerializable(typeof(JsonElement))]
internal partial class AppJson : JsonSerializerContext;
