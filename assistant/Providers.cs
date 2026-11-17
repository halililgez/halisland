using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Halisland.Assistant;

// Faz 24: asistan yerel modelle (Ollama) ya da bir bulut sağlayıcıyla
// (Claude, ChatGPT, Gemini) konuşabilir. ChatService yalnızca buradaki ortak
// türleri bilir; her sağlayıcı kendi API'sinin biçimine çevirir.

/// <summary>Modele giden mesaj (sağlayıcıdan bağımsız).</summary>
/// <param name="Role">"system", "user", "assistant" ya da "tool".</param>
/// <param name="ToolCallId">Araç sonucunun ait olduğu çağrı (role "tool").</param>
/// <param name="Raw">
/// Sağlayıcıya özgü asistan içeriği (ör. Claude'un imzalı düşünce blokları,
/// Gemini'nin thoughtSignature'ları). Araç turunda aynen geri gönderilir.
/// </param>
public sealed record ChatMessage(
    string Role,
    string Content,
    ToolCall[]? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null,
    object? Raw = null);

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolDef(string Name, string Description, JsonElement Schema);

/// <summary>Akıştaki bir parça. Son parça <see cref="Raw"/> ve hızı taşıyabilir.</summary>
public sealed record ChatDelta(
    string? Text = null,
    string? Thinking = null,
    ToolCall[]? ToolCalls = null,
    double? Speed = null,
    object? Raw = null);

/// <summary>
/// Sağlayıcının durumu. <c>Reachable</c> false ise <c>Error</c> nedenini söyler
/// (Ollama kapalı, anahtar geçersiz, ağ yok...).
/// </summary>
public sealed record ProviderStatus(bool Reachable, string[] Models, string? Error = null);

/// <summary>Kullanıcıya gösterilecek hata (sağlayıcının mesajı ya da çevirisi).</summary>
public sealed class ProviderException(string message) : Exception(message);

public interface IChatProvider
{
    /// <summary>"ollama", "anthropic", "openai" ya da "gemini".</summary>
    string Id { get; }

    /// <summary>Arayüzde ve hata mesajlarında görünen ad.</summary>
    string Label { get; }

    string Model { get; }

    /// <summary>Yerel model mi (internete çıkmaz)?</summary>
    bool Local { get; }

    /// <summary>Bağlam bütçesi (token). Geçmiş ve özet buna göre kırpılır.</summary>
    int ContextTokens { get; }

    Task<ProviderStatus> StatusAsync(CancellationToken ct);

    /// <summary>Modeli belleğe yükler (yalnızca yerel modelde anlamlı).</summary>
    Task WarmupAsync(CancellationToken ct);

    /// <summary>Akışsız, düşünmesiz kısa iş (başlık, özet). Cevabın metnini döndürür.</summary>
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);

    IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        bool think,
        IReadOnlyList<ToolDef> tools,
        CancellationToken ct);
}

/// <summary>Bulut sağlayıcıları için ortak parçalar.</summary>
public abstract class CloudProvider(AssistantOptions options) : IChatProvider
{
    /// <summary>Bulut modellerinde bağlam bol; maliyet için geçmiş bu kadarla sınırlı.</summary>
    public const int CloudContext = 32_000;

    /// <summary>Model listesi bu süre önbellekte kalır (arayüz sık sık sorar).</summary>
    private static readonly TimeSpan ListCacheTime = TimeSpan.FromMinutes(2);

    private ProviderStatus? _cached;
    private DateTime _cachedAt;

    protected AssistantOptions Options { get; } = options;
    protected string ApiKey => Options.ApiKey ?? "";

    public abstract string Id { get; }
    public abstract string Label { get; }
    public string Model => Options.Model;
    public bool Local => false;
    public int ContextTokens => CloudContext;

    public async Task<ProviderStatus> StatusAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Options.ApiKey))
            return new ProviderStatus(false, [], $"{Label} için API anahtarı girilmemiş.");
        if (_cached is not null && DateTime.UtcNow - _cachedAt < ListCacheTime) return _cached;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var models = await ListModelsAsync(timeout.Token);
            _cached = new ProviderStatus(true, models);
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
        catch (ProviderException e)
        {
            return new ProviderStatus(false, [], e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ProviderStatus(false, [], $"{Label} sunucusuna ulaşılamadı. İnternet bağlantını kontrol et.");
        }
    }

    public Task WarmupAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Kullanılabilir sohbet modelleri; anahtar geçersizse <see cref="ProviderException"/>.</summary>
    protected abstract Task<string[]> ListModelsAsync(CancellationToken ct);

    public abstract Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);

    public abstract IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        bool think,
        IReadOnlyList<ToolDef> tools,
        CancellationToken ct);

    /// <summary>HTTP durum kodunu kullanıcıya anlaşılır bir mesaja çevirir.</summary>
    protected string Explain(int status, string? detail) => status switch
    {
        401 or 403 => $"{Label} API anahtarı geçersiz ya da yetkisiz.",
        404 => $"{Options.Model} modeli {Label}'da bulunamadı.",
        429 => $"{Label} istek sınırı ya da kota doldu. Biraz sonra yeniden dene.",
        >= 500 => $"{Label} şu an cevap vermiyor ({status}).",
        _ => detail is { Length: > 0 } ? $"{Label}: {detail}" : $"{Label} {status} döndürdü.",
    };

    /// <summary>Hata gövdesinden mesajı çıkarır (OpenAI ve Gemini: {"error":{"message":...}}).</summary>
    protected static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.TryGetProperty("message", out var m)) return m.GetString();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>Server-Sent Events akışındaki "data:" satırları.</summary>
    protected static async IAsyncEnumerable<string> ReadSseAsync(
        HttpResponseMessage res,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data == "[DONE]") yield break;
            yield return data;
        }
    }

    /// <summary>Çıkan token sayısı ve ilk parçadan beri geçen süreden hız.</summary>
    protected static double? Speed(int? tokens, System.Diagnostics.Stopwatch? sinceFirst) =>
        tokens is > 0 && sinceFirst is { Elapsed.TotalSeconds: > 0.2 } w
            ? Math.Round(tokens.Value / w.Elapsed.TotalSeconds, 1)
            : null;

    /// <summary>Sağlayıcılar için ortak, uzun ömürlü HTTP istemcisi.</summary>
    protected static HttpClient CreateHttp(string baseUrl) => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        BaseAddress = new Uri(baseUrl),
        // Akış uzun sürebilir (düşünme açıkken dakikalarca); iptal isteğe bağlı.
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
