using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halisland.Assistant;

/// <summary>Kimlik Bilgileri'nde saklanan Spotify bağlantısı.</summary>
public sealed record SpotifySecret(string ClientId, string? RefreshToken);

/// <summary>Arayüzün gösterdiği Spotify durumu.</summary>
public sealed record SpotifyStatus(
    bool Configured,
    bool Connected,
    bool Pending,
    string? User,
    string? ClientId,
    string? Error,
    string RedirectUri);

public sealed record SpotifyLoginRequest(string? ClientId);

/// <summary>
/// Spotify Web API: kullanıcı kendi geliştirici uygulamasının Client ID'sini
/// girer, PKCE ile (gizli anahtar olmadan) izin verir. Yenileme anahtarı Windows
/// Kimlik Bilgileri'nde, erişim anahtarı yalnızca bellekte. Çalmayı kontrol
/// etmek Premium ister; geliştirme modundaki uygulamada da sahibinin Premium
/// olması gerekir.
/// </summary>
public sealed class SpotifyService(int port, ILogger<SpotifyService> log)
{
    private const string Target = "Halisland/spotify";
    private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-private";
    private const string Api = "https://api.spotify.com/v1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public string RedirectUri { get; } = $"http://127.0.0.1:{port}/spotify/callback";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private SpotifySecret? _secret;
    private bool _loaded;
    private string? _accessToken;
    private DateTime _expires;
    private string? _state, _verifier;
    private DateTime _stateAt;
    private string? _user;
    private string? _error;

    private SpotifySecret? Secret
    {
        get
        {
            if (_loaded) return _secret;
            _loaded = true;
            try
            {
                var raw = SecretStore.Read(Target);
                _secret = raw is null ? null : JsonSerializer.Deserialize(raw, AppJson.Default.SpotifySecret);
            }
            catch (JsonException)
            {
                _secret = null;
            }
            return _secret;
        }
    }

    private void Save(SpotifySecret? secret)
    {
        _secret = secret;
        _loaded = true;
        if (secret is null) SecretStore.Delete(Target);
        else SecretStore.Write(Target, JsonSerializer.Serialize(secret, AppJson.Default.SpotifySecret));
    }

    private bool Pending => _state is not null && DateTime.UtcNow - _stateAt < TimeSpan.FromMinutes(10);

    public async Task<SpotifyStatus> StatusAsync(CancellationToken ct)
    {
        var s = Secret;
        var connected = s?.RefreshToken is not null;
        if (connected && _user is null)
        {
            var (code, doc) = await CallAsync(HttpMethod.Get, "/me", null, ct);
            using (doc)
            {
                if (code == 200 && doc is not null)
                    _user = Str(doc.RootElement, "display_name") ?? Str(doc.RootElement, "id");
            }
            connected = Secret?.RefreshToken is not null; // yenileme başarısızsa düşer
        }
        return new SpotifyStatus(s is not null, connected, Pending, connected ? _user : null, s?.ClientId, _error, RedirectUri);
    }

    // ---------- Bağlanma (PKCE) ----------

    /// <summary>Tarayıcıda Spotify izin sayfasını açar. Hata varsa açıklaması döner.</summary>
    public string? BeginLogin(string? clientId)
    {
        var id = (clientId ?? Secret?.ClientId)?.Trim();
        if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-fA-F]{32}$"))
            return "Client ID 32 karakterlik bir kod olmalı (Spotify geliştirici panelinde, uygulamanın Settings sayfasında).";

        if (Secret?.ClientId != id) Save(new SpotifySecret(id, null));
        _error = null;
        _verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        _state = Base64Url(RandomNumberGenerator.GetBytes(16));
        _stateAt = DateTime.UtcNow;
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier)));
        var url = "https://accounts.spotify.com/authorize" +
                  $"?client_id={id}&response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                  $"&code_challenge_method=S256&code_challenge={challenge}&state={_state}" +
                  $"&scope={Uri.EscapeDataString(Scopes)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return null;
    }

    /// <summary>Spotify'ın tarayıcıyı geri yönlendirdiği adres; sonuç sayfası HTML'i döner.</summary>
    public async Task<string> CallbackAsync(string? code, string? state, string? error, CancellationToken ct)
    {
        if (!Pending || state is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(_state!)))
            return Page("Bu bağlantı isteği geçersiz ya da süresi doldu.", "Halisland'de Bağlan'a yeniden bas.");
        var verifier = _verifier!;
        _state = null;
        _verifier = null;

        if (error is not null || code is null)
        {
            _error = error == "access_denied" ? "Spotify izni verilmedi." : $"Spotify izin vermedi ({error}).";
            return Page(_error, "Halisland'e dönebilirsin.");
        }

        var clientId = Secret?.ClientId;
        if (clientId is null) return Page("Client ID bulunamadı.", "Halisland'de yeniden dene.");
        var (ok, message) = await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, ct);
        if (!ok) return Page(message ?? "Spotify'a bağlanılamadı.", "Halisland'de yeniden dene.");
        _user = null;
        await StatusAsync(ct);
        return Page($"Spotify bağlandı{(_user is null ? "" : $": {_user}")}.",
            "Bu sekmeyi kapatabilirsin. Artık asistana \"Spotify'da ... çal\" diyebilirsin.");
    }

    public void Disconnect()
    {
        Save(null);
        _accessToken = null;
        _user = null;
        _error = null;
        _state = null;
    }

    /// <summary>Anahtar alır ya da yeniler; yenileme anahtarı değiştiyse kaydeder.</summary>
    private async Task<(bool Ok, string? Error)> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = TryParse(body);
        if (!res.IsSuccessStatusCode || doc is null)
        {
            var err = doc is null ? null : Str(doc.RootElement, "error");
            log.LogWarning("Spotify anahtar hatası {Status}: {Error}", (int)res.StatusCode, err);
            if (err == "invalid_grant")
            {
                // Yenileme anahtarının süresi dolmuş ya da izin geri alınmış.
                if (Secret is { } s) Save(s with { RefreshToken = null });
                _error = "Spotify bağlantısının süresi doldu; yeniden bağlan.";
            }
            else if (err == "invalid_client")
                _error = "Client ID geçersiz.";
            else
                _error = $"Spotify anahtar vermedi ({err ?? ((int)res.StatusCode).ToString()}).";
            return (false, _error);
        }
        _accessToken = Str(doc.RootElement, "access_token");
        var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
        _expires = DateTime.UtcNow.AddSeconds(seconds - 60);
        if (Str(doc.RootElement, "refresh_token") is { } refresh && Secret is { } current && current.RefreshToken != refresh)
            Save(current with { RefreshToken = refresh });
        _error = null;
        return (_accessToken is not null, null);
    }

    private async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTime.UtcNow < _expires) return _accessToken;
            if (Secret is not { RefreshToken: { } refresh } s) return null;
            var (ok, _) = await TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = s.ClientId,
            }, ct);
            return ok ? _accessToken : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------- API ----------

    /// <summary>Spotify API çağrısı; bağlı değilse durum 0 döner.</summary>
    private async Task<(int Status, JsonDocument? Doc)> CallAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct);
        if (token is null) return (0, null);
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) _accessToken = null;
        return ((int)res.StatusCode, TryParse(text));
    }

    private static readonly Dictionary<string, string> Types = new()
    {
        ["sarki"] = "track", ["album"] = "album", ["sanatci"] = "artist", ["calma_listesi"] = "playlist",
    };

    /// <summary>
    /// Aramadaki ilk sonucu çalar (ya da şarkıyı sıraya ekler). Hesap bağlı
    /// değilse null döner; araç o zaman arama sonuçlarını açar.
    /// </summary>
    public async Task<ToolResult?> PlayAsync(string? query, string? kind, bool queue, CancellationToken ct)
    {
        var q = string.Join(' ', (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (q.Length == 0) return ToolResult.Fail("Çalınacak bir şey verilmedi.", "Arama boş");
        if (Secret?.RefreshToken is null) return null;
        var type = Types.GetValueOrDefault(kind?.Trim() ?? "", "track");

        var (status, doc) = await CallAsync(HttpMethod.Get, $"/search?q={Uri.EscapeDataString(q)}&type={type}&limit=5", null, ct);
        if (status == 0) return null; // bağlantı düştü
        string? uri, title;
        using (doc)
        {
            if (status != 200 || doc is null) return ApiFail(status, doc, "Spotify'da arama yapılamadı");
            (uri, title) = First(doc.RootElement, type);
        }
        if (uri is null) return ToolResult.Fail($"Spotify'da '{q}' için sonuç bulunamadı.", $"Spotify'da bulunamadı: {q}");

        var device = await DeviceAsync(ct);
        if (device.Id is null)
            return ToolResult.Fail(
                "Spotify açıldı ama çalabileceği bir cihaz görünmüyor. Kullanıcıdan Spotify uygulamasında bir kez " +
                "oynat'a basmasını iste, sonra tekrar dene.",
                "Spotify cihazı bulunamadı");

        if (queue && type == "track")
        {
            var (qs, qd) = await CallAsync(HttpMethod.Post,
                $"/me/player/queue?uri={Uri.EscapeDataString(uri)}&device_id={device.Id}", null, ct);
            using (qd)
            {
                if (qs is 200 or 204) return new ToolResult($"Sıraya eklendi: {title} ({device.Name}).", $"Sıraya eklendi: {title}");
                // Çalan bir şey yoksa sıra çalışmaz; doğrudan çalınır.
                if (qs != 404) return ApiFail(qs, qd, "Sıraya eklenemedi");
            }
        }

        var body = type == "track"
            ? new JsonObject { ["uris"] = new JsonArray((JsonNode)uri) }
            : new JsonObject { ["context_uri"] = uri };
        var (ps, pd) = await CallAsync(HttpMethod.Put, $"/me/player/play?device_id={device.Id}", body, ct);
        using (pd)
        {
            if (ps is 200 or 202 or 204)
            {
                log.LogInformation("spotify_cal '{Query}' → {Uri} ({Device})", q, uri, device.Name);
                return new ToolResult($"Çalıyor: {title} ({device.Name}).", $"Çalıyor: {title}");
            }
            return ApiFail(ps, pd, "Çalınamadı");
        }
    }

    private static (string? Uri, string? Title) First(JsonElement root, string type)
    {
        if (!root.TryGetProperty(type + "s", out var group) || !group.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return (null, null);
        foreach (var item in items.EnumerateArray())
        {
            // Spotify çalma listesi sonuçlarında boş (null) öğe döndürebiliyor.
            if (item.ValueKind != JsonValueKind.Object || Str(item, "uri") is not { } uri) continue;
            var name = Str(item, "name") ?? "?";
            var by = type switch
            {
                "track" or "album" when item.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array =>
                    string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")).OfType<string>()),
                "playlist" when item.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object =>
                    Str(o, "display_name"),
                _ => null,
            };
            var label = type switch { "album" => " (albüm)", "artist" => " (sanatçı)", "playlist" => " (çalma listesi)", _ => "" };
            return (uri, (string.IsNullOrEmpty(by) ? name : $"{name} – {by}") + label);
        }
        return (null, null);
    }

    /// <summary>
    /// Çalınacak cihaz: etkin olan, yoksa bu bilgisayar, yoksa ilki. Hiç yoksa
    /// Spotify uygulamasını açıp görünmesini bekler.
    /// </summary>
    private async Task<(string? Id, string? Name)> DeviceAsync(CancellationToken ct)
    {
        var opened = false;
        for (var i = 0; i < 15; i++)
        {
            var (status, doc) = await CallAsync(HttpMethod.Get, "/me/player/devices", null, ct);
            using (doc)
            {
                if (status == 200 && doc is not null && doc.RootElement.TryGetProperty("devices", out var list))
                {
                    var devices = list.EnumerateArray()
                        .Where(d => Str(d, "id") is not null && !(d.TryGetProperty("is_restricted", out var r) && r.GetBoolean()))
                        .ToList();
                    var pick = devices.FirstOrDefault(d => d.TryGetProperty("is_active", out var a) && a.GetBoolean());
                    if (pick.ValueKind == JsonValueKind.Undefined)
                        pick = devices.FirstOrDefault(d =>
                            string.Equals(Str(d, "name"), Environment.MachineName, StringComparison.OrdinalIgnoreCase));
                    if (pick.ValueKind == JsonValueKind.Undefined)
                        pick = devices.FirstOrDefault(d => Str(d, "type") == "Computer");
                    if (pick.ValueKind == JsonValueKind.Undefined && devices.Count > 0) pick = devices[0];
                    if (pick.ValueKind != JsonValueKind.Undefined) return (Str(pick, "id"), Str(pick, "name"));
                }
            }
            if (!opened)
            {
                // Spotify kapalıysa açılır; cihaz listesine birkaç saniyede düşer.
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true })?.Dispose();
                opened = true;
            }
            await Task.Delay(1000, ct);
        }
        return (null, null);
    }

    private ToolResult ApiFail(int status, JsonDocument? doc, string what)
    {
        string? reason = null, message = null;
        if (doc?.RootElement.TryGetProperty("error", out var e) == true && e.ValueKind == JsonValueKind.Object)
        {
            reason = Str(e, "reason");
            message = Str(e, "message");
        }
        log.LogWarning("Spotify {What}: {Status} {Reason} {Message}", what, status, reason, message);
        var text = (status, reason) switch
        {
            (403, "PREMIUM_REQUIRED") => "Bunun için Spotify Premium gerekiyor.",
            (403, _) when message?.Contains("allowlist", StringComparison.OrdinalIgnoreCase) == true ||
                          message?.Contains("registered", StringComparison.OrdinalIgnoreCase) == true =>
                "Bu Spotify hesabı geliştirici uygulamasının kullanıcı listesinde değil (Spotify panelinde User Management).",
            (403, _) => $"Spotify izin vermedi ({message ?? reason ?? "403"}).",
            (404, _) => "Spotify'da etkin bir cihaz bulunamadı; Spotify uygulamasını açıp bir kez oynat'a bas.",
            (429, _) => "Spotify istek sınırına takıldı; biraz sonra tekrar dene.",
            _ => $"Spotify hatası ({status}{(message is null ? "" : $": {message}")}).",
        };
        return ToolResult.Fail($"{what}: {text}", $"{what}: {text}");
    }

    // ---------- Yardımcılar ----------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonDocument? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Page(string title, string detail) => $$"""
        <!doctype html>
        <html lang="tr"><head><meta charset="utf-8"><title>Halisland · Spotify</title>
        <style>
          body { font-family: "Segoe UI", system-ui, sans-serif; background: #0b0d10; color: #e8eaed;
                 display: grid; place-items: center; min-height: 100vh; margin: 0; }
          main { text-align: center; padding: 24px; }
          h1 { font-size: 20px; margin: 0 0 8px; } p { color: #9aa0a6; margin: 0; }
        </style></head>
        <body><main><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(detail)}}</p></main></body></html>
        """;
}
