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
/// Faz 27: Halisland'in kendi özellikleri (ayarlar, durum, odak, hatırlatıcı,
/// yapılacaklar, pano; arayüzde çalışır, bkz. <see cref="ClientTools"/>),
/// dosya okuma/taşıma/silme, klasör düzenleme, uygulama kapatma, güç,
/// hesaplama ve döviz.
/// Riskli olanlar kullanıcıya sorulur; bkz. <see cref="Confirmations"/>.
/// </summary>
public sealed class ToolRegistry(
    Store store,
    Confirmations confirmations,
    ClientTools client,
    DesktopTools desktop,
    FileTools files,
    SystemTools system)
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
            "'aklında tut', 'aklında olsun' gibi. Kullanıcı istemeden kendiliğinden çağırma.",
            """
            {"type":"object","properties":{"bilgi":{"type":"string","description":"Kaydedilecek bilgi; kısa, tek cümle, üçüncü tekil kişiyle (ör. 'Kullanıcının adı Halil.')"}},"required":["bilgi"]}
            """),
        Tool(
            "dosya_ara",
            "Kullanıcı klasörlerinde (Masaüstü, Belgeler, İndirilenler, Resimler, Müzik, Videolar, OneDrive) " +
            "adında verilen kelimelerin hepsi geçen dosya ve klasörleri arar; en yeni 10 tanesini tam yoluyla döndürür.",
            """
            {"type":"object","properties":{"ad":{"type":"string","description":"Dosya adında geçen kelimeler, ör. 'fatura ekim'"},"tur":{"type":"string","enum":["dosya","klasor"]},"yer":{"type":"string","enum":["masaustu","belgeler","indirilenler","resimler","muzik","videolar"]}},"required":["ad"]}
            """),
        Tool(
            "dosya_ac",
            "Bir dosyayı varsayılan uygulamasıyla ya da klasörü Dosya Gezgini'nde açar (onay sorulur). " +
            "Yolu bilmiyorsan önce dosya_ara.",
            """
            {"type":"object","properties":{"yol":{"type":"string","description":"Tam yol"}},"required":["yol"]}
            """),
        Tool(
            "dosya_oku",
            "Bir metin dosyasının ya da Word belgesinin (.docx) içeriğini okur; özetlemek, soru cevaplamak için. " +
            "PDF okunamaz. Onay sorulur.",
            """
            {"type":"object","properties":{"yol":{"type":"string","description":"Tam yol"}},"required":["yol"]}
            """),
        Tool(
            "dosya_islem",
            "Bir dosyayı ya da klasörü başka bir klasöre taşır veya Geri Dönüşüm Kutusu'na gönderir (sil). Onay sorulur.",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["tasi","sil"]},"yol":{"type":"string","description":"Tam yol"},"hedef":{"type":"string","description":"tasi için hedef klasörün tam yolu ya da masaustu, belgeler, indirilenler, resimler, muzik, videolar"}},"required":["islem","yol"]}
            """),
        Tool(
            "klasor_duzenle",
            "İndirilenler ya da Masaüstü'ndeki dağınık dosyaları türe (Resimler, Belgeler, Videolar...) ya da aya göre " +
            "alt klasörlere taşır. Kullanıcıya plan gösterilip onay sorulur.",
            """
            {"type":"object","properties":{"yer":{"type":"string","enum":["indirilenler","masaustu"]},"gore":{"type":"string","enum":["tur","ay"],"description":"Varsayılan tur"}},"required":["yer"]}
            """),
        Tool(
            "uygulama_ac",
            "Başlat menüsündeki bir uygulamayı adıyla açar (ör. 'Spotify', 'Hesap Makinesi', 'Chrome').",
            """
            {"type":"object","properties":{"ad":{"type":"string"}},"required":["ad"]}
            """),
        Tool(
            "uygulama_kapat",
            "Açık bir uygulamanın pencerelerini kapatır (ör. 'Chrome', 'Not Defteri'). Kullanıcı kapatmanı isteyince hemen " +
            "çağır; onay penceresini araç kendisi gösterir, sen onay isteme.",
            """
            {"type":"object","properties":{"ad":{"type":"string"}},"required":["ad"]}
            """),
        Tool(
            "medya",
            "Medya tuşları: o an çalan oynatıcıyı durdurur/sürdürür (oynat_duraklat), sonraki/önceki parça, " +
            "sesi biraz açar/kısar, sessize alır. Sesi belli bir seviyeye getirmek için ayarla.",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["oynat_duraklat","sonraki","onceki","ses_ac","ses_kis","sessiz"]},"adim":{"type":"integer","description":"ses_ac/ses_kis için adım (~%2), varsayılan 5"}},"required":["islem"]}
            """),
        Tool(
            "youtube_ac",
            "YouTube'da arar ve ilk videoyu tarayıcıda doğrudan açar.",
            """
            {"type":"object","properties":{"sorgu":{"type":"string"}},"required":["sorgu"]}
            """),
        Tool(
            "spotify_ara",
            "Spotify'ı verilen aramanın sonuçlarıyla açar. Şarkıyı kendiliğinden çalmaz.",
            """
            {"type":"object","properties":{"sorgu":{"type":"string"}},"required":["sorgu"]}
            """),
        Tool(
            "web_ac",
            "Tarayıcıda yeni sekmede bir adres ya da bir sitede arama açar. Sayfanın içeriğini göremezsin.",
            """
            {"type":"object","properties":{"adres":{"type":"string"},"arama":{"type":"string"},"site":{"type":"string","enum":["google","youtube","vikipedi","github","harita"]}}}
            """),
        Tool(
            "not_al",
            "Not dosyasına (Belgeler\\Halisland\\Notlar.md) tarihli bir not ekler.",
            """
            {"type":"object","properties":{"metin":{"type":"string"}},"required":["metin"]}
            """),
        Tool(
            "notlari_oku",
            "Not dosyasındaki son notları okur.",
            """
            {"type":"object","properties":{}}
            """),
        // ---------- Faz 27: Halisland'in kendi özellikleri (arayüzde çalışır) ----------
        Tool(
            "ayarla",
            "Bir ayarı değiştirir: ses seviyesi, sessiz, ekran parlaklığı, gece ışığı, Wi-Fi, Bluetooth, ses çıkış cihazı.",
            """
            {"type":"object","properties":{"ne":{"type":"string","enum":["ses","sessiz","parlaklik","gece_isigi","wifi","bluetooth","ses_cihazi"]},"deger":{"type":"string","description":"ses/parlaklik: 0-100; sessiz, gece_isigi, wifi, bluetooth: ac ya da kapat; ses_cihazi: cihaz adı"}},"required":["ne","deger"]}
            """),
        Tool(
            "durum",
            "Anlık durumu okur: sistem (pil, işlemci, bellek, disk, en çok kullanan uygulamalar), ses, ekran, " +
            "kablosuz (Wi-Fi, Bluetooth), ses_cihazlari, hava (hava durumu), claude (Claude Code), odak (sayaç).",
            """
            {"type":"object","properties":{"konu":{"type":"string","enum":["sistem","ses","ekran","kablosuz","ses_cihazlari","hava","claude","odak"]}},"required":["konu"]}
            """),
        Tool(
            "odak",
            "Halisland'in odak (pomodoro) sayacını başlatır, duraklatır ya da sıfırlar.",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["baslat","duraklat","sifirla"]},"dakika":{"type":"integer","description":"baslat için süre; verilmezse ayardaki süre"}},"required":["islem"]}
            """),
        Tool(
            "hatirlatici",
            "Zamanlayıcı ve hatırlatıcı: belli dakika sonra ya da belli saatte adada uyarı gösterir. Listeler, iptal eder.",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["kur","listele","iptal"]},"dakika":{"type":"number","description":"Kaç dakika sonra (ör. 10, 0.5)"},"saat":{"type":"string","description":"Ya da saat, SS:DD (ör. 14:30)"},"mesaj":{"type":"string","description":"Ör. 'Çayı al'"},"no":{"type":"integer","description":"iptal için listedeki numara"}},"required":["islem"]}
            """),
        Tool(
            "yapilacak",
            "Halisland'in Bugün sekmesindeki yapılacaklar listesi: listeler, ekler, tamamlar, siler.",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["listele","ekle","tamamla","sil"]},"metin":{"type":"string","description":"ekle için; birden çok madde varsa her biri ayrı satırda"},"no":{"type":"integer","description":"tamamla/sil için listedeki numara"}},"required":["islem"]}
            """),
        Tool(
            "pano",
            "Pano: oku (son kopyalanan metin), ara (pano geçmişinde arar), kopyala (yalnızca kullanıcı isterse metni panoya yazar).",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["oku","ara","kopyala"]},"sorgu":{"type":"string","description":"ara için"},"metin":{"type":"string","description":"kopyala için; yalnızca kullanıcı kopyalamanı isterse"}},"required":["islem"]}
            """),
        Tool(
            "guc",
            "Ekranı kilitler, uyku moduna alır, oturumu kapatır, yeniden başlatır ya da kapatır. Kilitle dışındakiler için onay sorulur.",
            """
            {"type":"object","properties":{"islem":{"type":"string","enum":["kilitle","uyku","oturumu_kapat","yeniden_baslat","kapat"]}},"required":["islem"]}
            """),
        Tool(
            "hesapla",
            "Bir matematik ifadesini kesin olarak hesaplar: + - * / ^ % parantez, sqrt, sin, log... Ör. '1250*0.18', '(3+4)^2'.",
            """
            {"type":"object","properties":{"ifade":{"type":"string"}},"required":["ifade"]}
            """),
        Tool(
            "doviz",
            "Güncel kurla para birimi çevirir (Avrupa Merkez Bankası kuru; internet gerekir).",
            """
            {"type":"object","properties":{"miktar":{"type":"number"},"kaynak":{"type":"string","description":"ör. USD, EUR, TRY"},"hedef":{"type":"string","description":"varsayılan TRY"}},"required":["kaynak"]}
            """),
    ];

    /// <summary>Araç tanımlarının modelde kapladığı yaklaşık yer (token).</summary>
    public static readonly int DefinitionTokens =
        Definitions.Sum(d => ChatService.Estimate(d.Name + d.Description + d.Schema.GetRawText()) + 10);

    /// <summary>Arayüzde (Halisland'in kendisinde) çalışan araçlar.</summary>
    private static readonly HashSet<string> ClientSide = ["ayarla", "durum", "odak", "hatirlatici", "yapilacak", "pano"];

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
            if (call.Name == "pano" && GetString(args, "islem") == "kopyala")
                return system.CopyText(GetString(args, "metin"));
            if (ClientSide.Contains(call.Name))
            {
                var r = await client.RunAsync(call.Name, args, write, ct);
                // Pil durumu arayüzde yok; burada eklenir.
                if (call.Name == "durum" && GetString(args, "konu") == "sistem" && r.Ok && SystemTools.Battery() is { } battery)
                    r = r with { ForModel = $"{battery}\n{r.ForModel}" };
                return r;
            }
            return call.Name switch
            {
                "hatirla" => Remember(GetString(args, "bilgi")),
                "dosya_ara" => desktop.SearchFiles(GetString(args, "ad"), GetString(args, "tur"), GetString(args, "yer"), ct),
                "dosya_ac" => await OpenFileAsync(GetString(args, "yol"), write, ct),
                "dosya_oku" => await ReadFileAsync(GetString(args, "yol"), write, ct),
                "dosya_islem" => await FileOpAsync(GetString(args, "islem"), GetString(args, "yol"), GetString(args, "hedef"), write, ct),
                "klasor_duzenle" => await OrganizeAsync(GetString(args, "yer"), GetString(args, "gore"), write, ct),
                "uygulama_ac" => await desktop.OpenAppAsync(GetString(args, "ad"), ct),
                "uygulama_kapat" => await CloseAppAsync(GetString(args, "ad"), write, ct),
                "medya" => desktop.Media(GetString(args, "islem"), GetString(args, "adim")),
                "spotify_ara" => desktop.SpotifySearch(GetString(args, "sorgu")),
                "youtube_ac" => await desktop.YouTubeAsync(GetString(args, "sorgu"), ct),
                "web_ac" => desktop.OpenWeb(GetString(args, "adres"), GetString(args, "arama"), GetString(args, "site")),
                "not_al" => desktop.AddNote(GetString(args, "metin")),
                "notlari_oku" => desktop.ReadNotes(),
                "guc" => await PowerAsync(GetString(args, "islem"), write, ct),
                "hesapla" => FileTools.Calculate(GetString(args, "ifade")),
                "doviz" => await files.ConvertAsync(GetString(args, "miktar"), GetString(args, "kaynak"), GetString(args, "hedef"), ct),
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

    /// <summary>Kullanıcıya sorar; onay kartı arayüzde gösterilir.</summary>
    private async Task<bool> ConfirmAsync(string question, string detail, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        await write(new StreamEvent("confirm", Text: detail, Id: id, Message: question));
        var approved = await confirmations.AskAsync(id, ConfirmTimeout, ct);
        await write(new StreamEvent("confirm-end", Id: id, Approved: approved));
        return approved;
    }

    private const string Denied = "Kullanıcı onay vermedi (Vazgeç dedi ya da cevap vermedi); işlem yapılmadı.";

    private async Task<ToolResult> OpenFileAsync(string? path, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var check = desktop.CheckOpen(path);
        if (check.Error is not null)
            return ToolResult.Fail(check.Error, check.Path is null && check.Error.StartsWith("Açılmadı", StringComparison.Ordinal)
                ? "Programlar ve betikler asistan tarafından açılmaz"
                : check.Error);

        if (!await ConfirmAsync(check.IsDirectory ? "Klasör açılsın mı?" : "Dosya açılsın mı?", check.Path!, write, ct))
            return ToolResult.Fail(Denied, $"Açılmadı: {Path.GetFileName(check.Path)}");

        desktop.Open(check.Path!);
        return new ToolResult($"Açıldı: {check.Path}", $"Açıldı: {Path.GetFileName(check.Path)}");
    }

    private async Task<ToolResult> ReadFileAsync(string? path, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var check = DesktopTools.CheckPath(path);
        if (check.Error is not null) return ToolResult.Fail(check.Error, check.Error);
        if (FileTools.CheckReadable(check.Path!) is { } why) return ToolResult.Fail(why, $"Okunamadı: {Path.GetFileName(check.Path)}");
        if (!await ConfirmAsync("Dosya asistana okutulsun mu?", check.Path!, write, ct))
            return ToolResult.Fail(Denied, $"Okunmadı: {Path.GetFileName(check.Path)}");
        return files.Read(check.Path!);
    }

    private async Task<ToolResult> FileOpAsync(string? op, string? path, string? target, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var check = DesktopTools.CheckPath(path);
        if (check.Error is not null) return ToolResult.Fail(check.Error, check.Error);
        var p = check.Path!;
        var name = Path.GetFileName(p);
        if (DesktopTools.IsRootFolder(p))
            return ToolResult.Fail("Kullanıcı klasörlerinin kendisi (Masaüstü, Belgeler...) taşınamaz, silinemez.", "Bu klasöre dokunulmaz");

        if (op == "sil")
        {
            if (!await ConfirmAsync(check.IsDirectory ? "Klasör Geri Dönüşüm Kutusu'na gönderilsin mi?" : "Dosya Geri Dönüşüm Kutusu'na gönderilsin mi?", p, write, ct))
                return ToolResult.Fail(Denied, $"Silinmedi: {name}");
            return files.Recycle(p);
        }
        if (op != "tasi") return ToolResult.Fail("islem tasi ya da sil olmalı.", "Bilinmeyen işlem");

        var dir = DesktopTools.Place(target) ?? target?.Trim().Trim('"');
        var dest = DesktopTools.CheckPath(dir, mustExist: false);
        if (dest.Error is not null) return ToolResult.Fail("Hedef: " + dest.Error, "Hedef klasör geçersiz");
        if (File.Exists(dest.Path)) return ToolResult.Fail("Hedef bir klasör olmalı.", "Hedef klasör geçersiz");
        if (check.IsDirectory && (dest.Path!.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                  dest.Path.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            return ToolResult.Fail("Klasör kendi içine taşınamaz.", "Taşınamadı");
        if (!await ConfirmAsync("Taşınsın mı?", $"{p}\n→ {dest.Path}", write, ct))
            return ToolResult.Fail(Denied, $"Taşınmadı: {name}");
        return files.Move(p, dest.Path!);
    }

    private async Task<ToolResult> OrganizeAsync(string? place, string? by, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var root = DesktopTools.Place(place is "masaustu" ? "masaustu" : "indirilenler");
        if (root is null) return ToolResult.Fail("Klasör bulunamadı.", "Klasör bulunamadı");
        var plan = FileTools.PlanOrganize(root, by == "ay");
        var label = place is "masaustu" ? "Masaüstü" : "İndirilenler";
        if (plan.Moves.Count == 0)
            return new ToolResult($"{label} klasöründe düzenlenecek dosya yok (alt klasörlere ve kısayollara dokunulmaz).", $"{label} zaten düzenli");
        if (!await ConfirmAsync($"{label} düzenlensin mi? {plan.Moves.Count} dosya alt klasörlere taşınacak.", plan.Summary, write, ct))
            return ToolResult.Fail(Denied, $"{label} düzenlenmedi");
        return files.Organize(plan);
    }

    private async Task<ToolResult> CloseAppAsync(string? name, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return ToolResult.Fail("Uygulama adı verilmedi.", "Uygulama adı yok");
        var apps = FileTools.FindApps(name);
        if (apps.Count == 0)
            return ToolResult.Fail($"'{name}' adında açık bir pencere bulunamadı.", $"Açık değil: {name}");
        var titles = string.Join("\n", apps.Take(5).Select(a => string.IsNullOrWhiteSpace(a.Title) ? a.Name : a.Title));
        if (!await ConfirmAsync($"{apps[0].Name} kapatılsın mı?", titles, write, ct))
            return ToolResult.Fail(Denied, $"Kapatılmadı: {apps[0].Name}");
        return files.CloseApps(apps);
    }

    private async Task<ToolResult> PowerAsync(string? action, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var a = action?.Trim() ?? "";
        if (!SystemTools.PowerLabels.TryGetValue(a, out var question))
            return ToolResult.Fail($"Bilinmeyen güç işlemi: {a}", "Bilinmeyen işlem");
        if (a != "kilitle" && !await ConfirmAsync(question, "", write, ct))
            return ToolResult.Fail(Denied, "Vazgeçildi");
        return system.Power(a);
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

/// <summary>
/// Halisland'in arayüzünde çalışan araçlar (ses, parlaklık, odak, hatırlatıcı,
/// yapılacaklar...): bunlar zaten Tauri komutları ve Vue tarafındaki durumla
/// yapılıyor. Akışa "client-tool" olayı yazılır (Id, Message araç adı, Text
/// argümanlar JSON), arayüz işi yapıp <c>POST /tool-result/{id}</c> ile sonucu döner.
/// </summary>
public sealed class ClientTools
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClientToolResult?>> _pending = new();

    public async Task<ToolResult> RunAsync(string name, JsonElement args, Func<StreamEvent, Task> write, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ClientToolResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await write(new StreamEvent("client-tool", Text: args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText(), Id: id, Message: name));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            await using var reg = limit.Token.Register(() => tcs.TrySetResult(null));
            var r = await tcs.Task;
            ct.ThrowIfCancellationRequested();
            if (r is null) return ToolResult.Fail("Halisland cevap vermedi; iş yapılmadı.", "Halisland cevap vermedi");
            return new ToolResult(r.Text, string.IsNullOrWhiteSpace(r.Note) ? name : r.Note, r.Ok);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public bool Resolve(string id, ClientToolResult result) =>
        _pending.TryGetValue(id, out var tcs) && tcs.TrySetResult(result);
}
