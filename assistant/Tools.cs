using System.Collections.Concurrent;
using System.Text.Json;

namespace Halisland.Assistant;

/// <summary>
/// Aracın sonucu: modele geri giden metin ve arayüzde gösterilecek kısa not.
/// <c>Ok</c> false: iş yapılmadı (bulunamadı, reddedildi, hata); arayüz notu gri gösterir.
/// </summary>
public sealed record ToolResult(string ForModel, string ForUser, bool Ok = true)
{
    public static ToolResult Fail(string forModel, string forUser) => new(forModel, forUser, Ok: false);
}

/// <summary>
/// Modelin çağırabildiği araçlar (her sağlayıcı kendi biçimine çevirir).
/// Faz 22: hafıza (hatirla). Faz 25: dosya arama/açma, uygulama açma, notlar,
/// medya tuşları, Spotify'da arama, tarayıcıda adres/arama açma.
/// Faz 26: YouTube'da ilk videoyu açma.
/// Riskli olan (dosya açma) kullanıcıya sorulur; bkz. <see cref="Confirmations"/>.
/// </summary>
public sealed class ToolRegistry(Store store, Confirmations confirmations, DesktopTools desktop)
{
    public const int MaxMemoryLength = 300;

    /// <summary>Kullanıcı bu süre içinde onaylamazsa işlem yapılmaz.</summary>
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(2);

    public static readonly ToolDef[] Definitions =
    [
        // Silme aracı bilerek yok: denemede model "unutma" (= hatırla) isteğinde
        // silme aracını çağırıp başka bir bilgiyi sildi. Silme yalnızca Hafıza listesinden.
        Tool(
            "hatirla",
            "Kullanıcı hakkında kalıcı bir bilgiyi gelecekteki sohbetlerde kullanmak üzere kaydeder. " +
            "Kullanıcı kendisiyle ilgili bir bilgiyi saklamanı istediğinde MUTLAKA çağır: 'hatırla', 'unutma', " +
            "'aklında tut', 'aklında olsun' gibi. Yapılacak iş, alışveriş listesi gibi notlar için not_al'ı kullan. " +
            "Kullanıcı istemeden kendiliğinden çağırma.",
            """
            {"type":"object","properties":{"bilgi":{"type":"string","description":"Kaydedilecek bilgi; kısa, tek cümle, üçüncü tekil kişiyle (ör. 'Kullanıcının adı Halil.')"}},"required":["bilgi"]}
            """),
        Tool(
            "dosya_ara",
            "Kullanıcının klasörlerinde (Masaüstü, Belgeler, İndirilenler, Resimler, Müzik, Videolar, OneDrive) " +
            "adında verilen kelimelerin hepsi geçen dosya ve klasörleri arar; en yeni 10 tanesini tam yoluyla döndürür. " +
            "Dosya içeriğine bakmaz.",
            """
            {"type":"object","properties":{"ad":{"type":"string","description":"Dosya adında geçen kelimeler, ör. 'fatura ekim' ya da 'cv pdf'"},"tur":{"type":"string","enum":["dosya","klasor"],"description":"Yalnızca dosyalar ya da yalnızca klasörler; kullanıcı belirtmediyse verme"},"yer":{"type":"string","enum":["masaustu","belgeler","indirilenler","resimler","muzik","videolar"],"description":"Kullanıcı bir klasör söylediyse yalnızca orada ara"}},"required":["ad"]}
            """),
        Tool(
            "dosya_ac",
            "Bir dosyayı varsayılan uygulamasıyla ya da bir klasörü Dosya Gezgini'nde açar. Yolu bilmiyorsan önce " +
            "dosya_ara ile bul. Kullanıcıya onay sorulur; programlar ve betikler (.exe, .bat, .ps1...) açılamaz.",
            """
            {"type":"object","properties":{"yol":{"type":"string","description":"dosya_ara'nın verdiği tam yol"}},"required":["yol"]}
            """),
        Tool(
            "uygulama_ac",
            "Başlat menüsündeki bir uygulamayı adıyla açar (ör. 'Spotify', 'Hesap Makinesi', 'Not Defteri', 'Chrome').",
            """
            {"type":"object","properties":{"ad":{"type":"string","description":"Uygulamanın adı"}},"required":["ad"]}
            """),
        Tool(
            "medya",
            "Klavyedeki medya ve ses tuşlarına basar; o an çalan oynatıcı (Spotify, YouTube, ...) tepki verir. " +
            "Müziği durdurmak ya da sürdürmek için oynat_duraklat (aynı tuş), sonraki/önceki parça, ses açma/kısma, sessiz.",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["oynat_duraklat","sonraki","onceki","ses_ac","ses_kis","sessiz"]},"adim":{"type":"integer","description":"Yalnızca ses_ac/ses_kis için; her adım yaklaşık %2, varsayılan 5"}},"required":["islem"]}
            """),
        Tool(
            "youtube_ac",
            "YouTube'da arar ve ilk videoyu tarayıcıda doğrudan açar (kendiliğinden başlar).",
            """
            {"type":"object","properties":{"sorgu":{"type":"string","description":"Ör. 'lofi hip hop' ya da 'Hergele İyi Uykular klip'"}},"required":["sorgu"]}
            """),
        Tool(
            "spotify_ara",
            "Spotify'ı verilen aramanın sonuçlarıyla açar (şarkı, sanatçı, albüm). Şarkıyı kendiliğinden çalmaz; " +
            "kullanıcı sonuçlardan tıklar.",
            """
            {"type":"object","properties":{"sorgu":{"type":"string","description":"Ör. 'Hergele İyi Uykular'"}},"required":["sorgu"]}
            """),
        Tool(
            "web_ac",
            "Varsayılan tarayıcıda yeni sekmede bir web adresi ya da bir sitede arama açar. Ya adres ya da arama ver. " +
            "Sayfanın içeriğini göremezsin.",
            """
            {"type":"object","properties":{"adres":{"type":"string","description":"Ör. 'github.com' ya da tam adres"},"arama":{"type":"string","description":"Aranacak metin"},"site":{"type":"string","enum":["google","youtube","vikipedi","github","harita"],"description":"Arama nerede yapılsın; varsayılan google"}}}
            """),
        Tool(
            "not_al",
            "Kullanıcının not dosyasına (Belgeler\\Halisland\\Notlar.md) tarihli bir not ekler. Kullanıcı 'not al', " +
            "'not et', 'şunu yaz', 'listeye ekle' dediğinde çağır.",
            """
            {"type":"object","properties":{"metin":{"type":"string","description":"Notun metni, kullanıcının söylediği gibi"}},"required":["metin"]}
            """),
        Tool(
            "notlari_oku",
            "Kullanıcının not dosyasındaki son notları okur.",
            """
            {"type":"object","properties":{}}
            """),
    ];

    private static ToolDef Tool(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    /// <summary>
    /// Aracı çalıştırır. Onay gereken araçta akışa "confirm" olayı yazar ve
    /// kullanıcının cevabını (POST /confirm/{id}) bekler.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ToolCall call, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        try
        {
            var args = call.Arguments;
            return call.Name switch
            {
                "hatirla" => Remember(GetString(args, "bilgi")),
                "dosya_ara" => desktop.SearchFiles(GetString(args, "ad"), GetString(args, "tur"), GetString(args, "yer"), ct),
                "dosya_ac" => await OpenFileAsync(GetString(args, "yol"), write, ct),
                "uygulama_ac" => await desktop.OpenAppAsync(GetString(args, "ad"), ct),
                "medya" => desktop.Media(GetString(args, "islem"), GetString(args, "adim")),
                "spotify_ara" => desktop.SpotifySearch(GetString(args, "sorgu")),
                "youtube_ac" => await desktop.YouTubeAsync(GetString(args, "sorgu"), ct),
                "web_ac" => desktop.OpenWeb(GetString(args, "adres"), GetString(args, "arama"), GetString(args, "site")),
                "not_al" => desktop.AddNote(GetString(args, "metin")),
                "notlari_oku" => desktop.ReadNotes(),
                _ => ToolResult.Fail($"Böyle bir araç yok: {call.Name}", $"Bilinmeyen araç: {call.Name}"),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException
                                      or IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            return ToolResult.Fail($"Araç çalışmadı: {e.Message}", "Araç çalışmadı");
        }
    }

    private async Task<ToolResult> OpenFileAsync(string? path, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var check = desktop.CheckOpen(path);
        if (check.Error is not null)
            return ToolResult.Fail(check.Error, check.Path is null && check.Error.StartsWith("Açılmadı", StringComparison.Ordinal)
                ? "Programlar ve betikler asistan tarafından açılmaz"
                : check.Error);

        var id = Guid.NewGuid().ToString("N");
        await write(new StreamEvent("confirm", Text: check.Path, Id: id, Message: check.IsDirectory ? "Klasör açılsın mı?" : "Dosya açılsın mı?"));
        var approved = await confirmations.AskAsync(id, ConfirmTimeout, ct);
        await write(new StreamEvent("confirm-end", Id: id, Approved: approved));
        if (!approved)
            return ToolResult.Fail(
                "Kullanıcı onay vermedi (Vazgeç dedi ya da cevap vermedi); dosya açılmadı.",
                $"Açılmadı: {Path.GetFileName(check.Path)}");

        desktop.Open(check.Path!);
        return new ToolResult($"Açıldı: {check.Path}", $"Açıldı: {Path.GetFileName(check.Path)}");
    }

    public ToolResult Remember(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return ToolResult.Fail("Boş bilgi kaydedilmedi.", "Boş bilgi kaydedilmedi");
        if (text.Length > MaxMemoryLength) text = text[..MaxMemoryLength];

        var existing = store.ListMemories()
            .FirstOrDefault(m => string.Equals(m.Text, text, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null)
            return new ToolResult($"Bu bilgi zaten kayıtlı ([{existing.Id}]).", $"Zaten hatırlıyorum: {text}");

        var m = store.AddMemory(text);
        return new ToolResult($"Kaydedildi ([{m.Id}]).", $"Hatırlayacağım: {text}");
    }

    private static string? GetString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
            : null;
}

/// <summary>
/// Kullanıcı onayı bekleyen işlemler. Akış "confirm" olayını yazar, arayüz
/// <c>POST /confirm/{id}</c> ile cevap verir. Cevap gelmezse, süre dolarsa ya
/// da kullanıcı yanıtı durdurursa işlem reddedilmiş sayılır.
/// </summary>
public sealed class Confirmations
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();

    public async Task<bool> AskAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await using var reg = limit.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Bekleyen bir onayı cevaplar; böyle bir onay yoksa false.</summary>
    public bool Resolve(string id, bool approved) =>
        _pending.TryGetValue(id, out var tcs) && tcs.TrySetResult(approved);
}
