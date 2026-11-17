using System.Text.Json.Serialization;

namespace Halisland.Assistant;

// ---------- Arayüzle konuşulan türler (camelCase) ----------

/// <summary>Sohbetteki tek mesaj. Rol: "system", "user" ya da "assistant".</summary>
public sealed record ChatMessage(string Role, string Content);

public sealed record ChatRequest(ChatMessage[] Messages, bool Think = false);

public sealed record HealthResponse(
    bool Ok,
    string Version,
    bool Ollama,
    string Model,
    bool ModelInstalled,
    string[] Models);

/// <summary>
/// /chat akışındaki tek satır (NDJSON). Tür: "thinking" (düşünce parçası),
/// "token" (cevap parçası), "done" (bitti, istatistikler) ya da "error".
/// </summary>
public sealed record StreamEvent(
    string Type,
    string? Text = null,
    int? Tokens = null,
    double? TokensPerSec = null,
    string? Message = null);

// ---------- Ollama API türleri (snake_case) ----------

public sealed record OllamaOptions(
    [property: JsonPropertyName("num_ctx")] int NumCtx);

public sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("think")] bool Think,
    [property: JsonPropertyName("keep_alive")] string KeepAlive,
    [property: JsonPropertyName("options")] OllamaOptions Options);

/// <summary>Modeli belleğe yükler (boş istem); ilk cevabı beklemek zorunda kalmayalım.</summary>
public sealed record OllamaLoadRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("keep_alive")] string KeepAlive,
    [property: JsonPropertyName("options")] OllamaOptions Options);

public sealed record OllamaMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("thinking")] string? Thinking);

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
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaLoadRequest))]
[JsonSerializable(typeof(OllamaChatChunk))]
[JsonSerializable(typeof(OllamaTags))]
[JsonSerializable(typeof(OllamaError))]
internal partial class AppJson : JsonSerializerContext;
