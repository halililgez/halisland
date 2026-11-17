using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Halisland.Assistant;

/// <summary>
/// Bilgisayarda iş yapan araçlar (Faz 25): dosya arama ve açma, uygulama
/// açma, notlar. Asistan yalnızca Windows'ta çalışır.
/// </summary>
/// <remarks>
/// Dosya arama Windows Search dizinini değil, kullanıcı klasörlerini tarar:
/// dizine OLE DB ile erişmek COM ister, kırpılmış (trimmed) exe'de COM kapalı.
/// Tarama süre ve sayı sınırlıdır.
/// </remarks>
public sealed class DesktopTools(ILogger<DesktopTools> log)
{
    private const int MaxResults = 10;
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(4);
    private const int MaxScanned = 300_000;

    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Taramada girilmeyen klasörler (çok büyük ya da kullanıcı dosyası değil).</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".vs", ".idea", "bin", "obj", "target", "dist", "AppData",
        "$RECYCLE.BIN", "__pycache__", ".venv", "venv", "packages", ".nuget", ".cargo", ".rustup",
    };

    /// <summary>
    /// Açılmayan türler: programlar, betikler, kısayollar ve kurulum dosyaları.
    /// Dosya adları modele gider; adına yerleştirilmiş bir yönlendirmeyle
    /// zararlı bir dosyanın açtırılmasının önüne geçer (ayrıca her açma onaylı).
    /// </summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".ps1xml", ".psc1", ".vbs", ".vbe", ".vb",
        ".js", ".jse", ".wsf", ".wsh", ".ws", ".msi", ".msp", ".mst", ".scr", ".pif", ".lnk", ".url", ".reg",
        ".hta", ".cpl", ".jar", ".appref-ms", ".application", ".inf", ".sys", ".dll", ".ocx", ".msc",
        ".gadget", ".settingcontent-ms", ".library-ms", ".search-ms", ".searchconnector-ms", ".diagcab",
        ".appx", ".appxbundle", ".msix", ".msixbundle", ".scf", ".xll", ".xlam", ".ppam", ".iqy", ".slk",
        ".chm", ".cab", ".sct", ".wsc", ".shb", ".shs", ".mht", ".mhtml", ".py", ".pyw",
    };

    // ---------- Dosyalar ----------

    /// <summary>Aranan kullanıcı klasörleri (iç içe olanlar tekrar taranmaz).</summary>
    public static IReadOnlyList<string> Roots { get; } = FindRoots();

    private static List<string> FindRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
        };
        var roots = new List<string>();
        foreach (var c in candidates.Where(c => c.Length > 0 && Directory.Exists(c)).Select(Path.GetFullPath))
        {
            if (roots.Any(r => IsUnder(c, r))) continue;
            roots.RemoveAll(r => IsUnder(r, c));
            roots.Add(c);
        }
        return roots;
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string text, string word) =>
        Tr.CompareInfo.IndexOf(text, word, Loose) >= 0;

    private static string[] Words(string query) =>
        query.Split([' ', ',', ';', '*', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.TrimStart('.'))
            .Where(w => w.Length > 0)
            .ToArray();

    /// <summary>"masaustu", "belgeler"... → klasör yolu (bilinmiyorsa null: tüm kökler).</summary>
    private static string? Place(string? place)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = place?.Trim().ToLowerInvariant() switch
        {
            "masaustu" or "masaüstü" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "belgeler" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "indirilenler" => Path.Combine(home, "Downloads"),
            "resimler" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "muzik" or "müzik" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videolar" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null,
        };
        return path is { Length: > 0 } && Directory.Exists(path) ? path : null;
    }

    public ToolResult SearchFiles(string? query, string? kind, string? place, CancellationToken ct)
    {
        var words = Words(query ?? "");
        if (words.Length == 0) return ToolResult.Fail("Aranacak kelime verilmedi.", "Aranacak kelime yok");
        bool? wantDir = kind?.Trim().ToLowerInvariant() switch
        {
            "klasor" or "klasör" => true,
            "dosya" => false,
            _ => null,
        };
        var start = Place(place);

        var watch = Stopwatch.StartNew();
        var found = new List<(string Path, DateTime Modified, bool Dir)>();
        var scanned = 0;
        var complete = true;
        var queue = new Queue<string>(start is null ? Roots : [start]);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        while (queue.Count > 0)
        {
            if (watch.Elapsed > SearchBudget || scanned > MaxScanned || ct.IsCancellationRequested)
            {
                complete = false;
                break;
            }
            var dir = queue.Dequeue();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    scanned++;
                    var isDir = entry is DirectoryInfo;
                    if (isDir && !SkipDirs.Contains(entry.Name)) queue.Enqueue(entry.FullName);
                    if ((wantDir is null || wantDir == isDir) && words.All(w => Contains(entry.Name, w)))
                        found.Add((entry.FullName, entry.LastWriteTime, isDir));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Erişilemeyen klasör atlanır.
            }
        }
        log.LogInformation("dosya_ara '{Query}': {Count} sonuç, {Scanned} öğe, {Ms} ms", query, found.Count, scanned, watch.ElapsedMilliseconds);

        var label = string.Join(' ', words);
        if (found.Count == 0)
        {
            var note = complete ? "" : " (tarama süre sınırına takıldı, her yere bakılamadı)";
            return ToolResult.Fail(
                $"'{label}' adında {(wantDir == true ? "klasör" : "dosya")} bulunamadı{note}. Aranan klasörler: {(start is null ? string.Join(", ", Roots) : start)}.",
                $"Dosya bulunamadı: {label}");
        }

        // Adı aranan kelimelerle tam aynı olanlar başa, sonra en yeniler.
        var top = found
            .OrderByDescending(f => Tr.CompareInfo.Compare(Path.GetFileNameWithoutExtension(f.Path), label, Loose) == 0)
            .ThenByDescending(f => f.Modified)
            .Take(MaxResults)
            .ToList();
        var sb = new StringBuilder($"{found.Count} sonuç");
        if (found.Count > MaxResults) sb.Append($" (en yeni {MaxResults} tanesi)");
        if (!complete) sb.Append(" (tarama süre sınırına takıldı)");
        sb.AppendLine(":");
        foreach (var (path, modified, dir) in top)
            sb.Append("- ").Append(path).Append(dir ? " [klasör]" : "").Append(" (").Append(modified.ToString("d MMM yyyy", Tr)).AppendLine(")");
        return new ToolResult(sb.ToString(), $"{found.Count} dosya bulundu: {label}");
    }

    public sealed record OpenCheck(string? Path, bool IsDirectory, string? Error);

    /// <summary>Açılabilir mi: var, kullanıcı klasörlerinde ve program/betik değil.</summary>
    public OpenCheck CheckOpen(string? raw)
    {
        var path = raw?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) return new OpenCheck(null, false, "Yol verilmedi.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            return new OpenCheck(null, false, "Tam bir yerel yol gerekli; önce dosya_ara ile bul.");
        path = Path.GetFullPath(path);
        if (!Roots.Any(r => IsUnder(path, r)))
            return new OpenCheck(null, false, "Yalnızca kullanıcı klasörlerindeki (Masaüstü, Belgeler, İndirilenler...) dosyalar açılabilir.");

        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) return new OpenCheck(null, false, $"Böyle bir dosya yok: {path}");
        if (!isDir && BlockedExtensions.Contains(Path.GetExtension(path)))
            return new OpenCheck(null, false,
                $"Açılmadı: {Path.GetExtension(path)} dosyalarını (program, betik, kısayol) asistan güvenlik için hiç açmaz; " +
                $"onay da değiştirmez. Kullanıcıya bunu kendisinin açması gerektiğini söyle. Yol: {path}");
        return new OpenCheck(path, isDir, null);
    }

    public void Open(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
        else
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    // ---------- Uygulamalar ----------

    private sealed record StartApp(string Name, string AppId);

    private List<StartApp>? _apps;
    private DateTime _appsAt;
    private readonly SemaphoreSlim _appsLock = new(1, 1);

    /// <summary>Başlat menüsündeki uygulamalar (Store uygulamaları dahil), 10 dk önbellekte.</summary>
    private async Task<List<StartApp>> AppsAsync(CancellationToken ct)
    {
        await _appsLock.WaitAsync(ct);
        try
        {
            if (_apps is not null && DateTime.UtcNow - _appsAt < TimeSpan.FromMinutes(10)) return _apps;
            var psi = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell başlatılamadı.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);

            var list = new List<StartApp>();
            using var doc = JsonDocument.Parse(output.Length > 0 ? output : "[]");
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var item in items)
            {
                var name = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
                var id = item.TryGetProperty("AppID", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (Contains(name, "uninstall") || Contains(name, "kaldır")) continue;
                list.Add(new StartApp(name, id));
            }
            _apps = list;
            _appsAt = DateTime.UtcNow;
            return list;
        }
        finally
        {
            _appsLock.Release();
        }
    }

    public async Task<ToolResult> OpenAppAsync(string? query, CancellationToken ct)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0) return ToolResult.Fail("Uygulama adı verilmedi.", "Uygulama adı yok");

        var apps = await AppsAsync(ct);
        var words = Words(q);
        var best = apps
            .Select(a => (App: a, Score: Score(a.Name, q, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.App.Name.Length)
            .FirstOrDefault();
        if (best.App is null)
        {
            var similar = apps.Where(a => words.Any(w => Contains(a.Name, w))).Select(a => a.Name).Take(5).ToList();
            return ToolResult.Fail(
                $"'{q}' adında bir uygulama bulunamadı." + (similar.Count > 0 ? $" Benzerleri: {string.Join(", ", similar)}." : ""),
                $"Uygulama bulunamadı: {q}");
        }

        // shell:AppsFolder hem masaüstü programlarını hem Store uygulamalarını açar.
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{best.App.AppId}") { UseShellExecute = false })?.Dispose();
        log.LogInformation("uygulama_ac '{Query}' → {Name} ({Id})", q, best.App.Name, best.App.AppId);
        return new ToolResult($"Açıldı: {best.App.Name}", $"Açıldı: {best.App.Name}");
    }

    private static int Score(string name, string query, string[] words)
    {
        if (Tr.CompareInfo.Compare(name, query, Loose) == 0) return 4;
        if (Tr.CompareInfo.IsPrefix(name, query, Loose)) return 3;
        if (Contains(name, query)) return 2;
        return words.Length > 0 && words.All(w => Contains(name, w)) ? 1 : 0;
    }

    // ---------- Medya ve ses ----------

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    private const uint KeyExtended = 0x1, KeyUp = 0x2;

    /// <summary>
    /// Klavyedeki medya ve ses tuşlarına basılmış gibi yapar; o an çalan
    /// oynatıcı (Spotify, tarayıcı, ...) hangisiyse o tepki verir.
    /// </summary>
    public ToolResult Media(string? action, string? steps)
    {
        var n = int.TryParse(steps, out var s) ? Math.Clamp(s, 1, 25) : 5;
        (byte Vk, int Times, string Done)? key = action?.Trim().ToLowerInvariant() switch
        {
            "oynat_duraklat" => (0xB3, 1, "Oynat/duraklat tuşuna basıldı"),
            "sonraki" => (0xB0, 1, "Sonraki parçaya geçildi"),
            "onceki" => (0xB1, 1, "Önceki parçaya geçildi"),
            "ses_ac" => (0xAF, n, $"Ses yaklaşık {n * 2} puan açıldı"),
            "ses_kis" => (0xAE, n, $"Ses yaklaşık {n * 2} puan kısıldı"),
            "sessiz" => (0xAD, 1, "Sessiz tuşuna basıldı"),
            _ => null,
        };
        if (key is not { } k)
            return ToolResult.Fail($"Bilinmeyen işlem: {action}", "Bilinmeyen medya işlemi");

        for (var i = 0; i < k.Times; i++)
        {
            keybd_event(k.Vk, 0, KeyExtended, 0);
            keybd_event(k.Vk, 0, KeyExtended | KeyUp, 0);
        }
        log.LogInformation("medya {Action} x{Times}", action, k.Times);
        var note = action switch
        {
            "oynat_duraklat" => " Aynı tuş çalıyorsa duraklatır, duraklatılmışsa sürdürür. Kullanıcıya kısaca " +
                                "\"Oynat/duraklat tuşuna bastım\" de; çalıp çalmadığını bilemezsin, tahmin etme.",
            "sessiz" => " Aynı tuş sesi kapatır ya da geri açar.",
            "ses_ac" or "ses_kis" => " Şu anki ses seviyesini bilemezsin; seviye söyleme, tahmin etme.",
            _ => "",
        };
        var forUser = action switch { "ses_ac" => "Ses açıldı", "ses_kis" => "Ses kısıldı", _ => k.Done };
        return new ToolResult(k.Done + "." + note, forUser);
    }

    // ---------- Spotify ve web ----------

    /// <summary>Spotify masaüstü uygulaması kurulu mu (spotify: adresleri ona gider).</summary>
    private static bool HasSpotifyProtocol()
    {
        if (!OperatingSystem.IsWindows()) return false;
        // Store sürümünde shell\open\command yok; "URL Protocol" değeri her iki sürümde de var.
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("spotify");
        return key?.GetValue("URL Protocol") is not null;
    }

    public ToolResult SpotifySearch(string? query)
    {
        var q = string.Join(' ', (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (q.Length == 0) return ToolResult.Fail("Aranacak bir şey verilmedi.", "Arama boş");
        if (q.Length > 200) q = q[..200];

        var escaped = Uri.EscapeDataString(q);
        var app = HasSpotifyProtocol();
        var target = app ? $"spotify:search:{escaped}" : $"https://open.spotify.com/search/{escaped}";
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        log.LogInformation("spotify_ara '{Query}' ({Where})", q, app ? "uygulama" : "web");
        return new ToolResult(
            $"Spotify{(app ? "" : " web sitesi tarayıcıda")} '{q}' aramasının sonuçlarıyla açıldı. Şarkıyı kendiliğinden " +
            "çalmaz; kullanıcıya (sen diye) sonuçlardan istediğine tıklamasını söyle.",
            $"Spotify'da arandı: {q}");
    }

    private static readonly Dictionary<string, string> SearchSites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google"] = "https://www.google.com/search?q=",
        ["youtube"] = "https://www.youtube.com/results?search_query=",
        ["vikipedi"] = "https://tr.wikipedia.org/w/index.php?search=",
        ["github"] = "https://github.com/search?q=",
        ["harita"] = "https://www.google.com/maps/search/",
    };

    /// <summary>Varsayılan tarayıcıda bir adresi ya da bir sitede aramayı açar. Yalnızca http/https.</summary>
    public ToolResult OpenWeb(string? address, string? query, string? site)
    {
        string url;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            if (q.Length > 300) q = q[..300];
            var prefix = SearchSites.GetValueOrDefault(site?.Trim() ?? "", SearchSites["google"]);
            url = prefix + Uri.EscapeDataString(q);
        }
        else
        {
            var a = address?.Trim() ?? "";
            if (a.Length == 0) return ToolResult.Fail("Adres ya da arama verilmedi.", "Adres yok");
            if (!a.Contains("://", StringComparison.Ordinal)) a = "https://" + a;
            if (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !uri.Host.Contains('.'))
                return ToolResult.Fail($"Geçerli bir web adresi değil: {address}", "Geçersiz adres");
            url = uri.AbsoluteUri;
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        log.LogInformation("web_ac {Url}", url);
        return new ToolResult(
            $"Varsayılan tarayıcıda yeni sekmede açıldı: {url} (sayfanın içeriğini okuyamazsın; bunu kullanıcıya ayrıca söyleme).",
            $"Tarayıcıda açıldı: {ShortUrl(url)}");
    }

    private static readonly HttpClient Web = CreateWeb();

    private static HttpClient CreateWeb()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9");
        // Avrupa'daki çerez onayı sayfası yerine sonuçlar gelsin (yalnızca gerekli çerezler).
        http.DefaultRequestHeaders.Add("Cookie", "SOCS=CAI");
        return http;
    }

    private static readonly System.Text.RegularExpressions.Regex FirstVideo = new(
        "\"videoRenderer\":\\{\"videoId\":\"([A-Za-z0-9_-]{11})\"");

    private static readonly System.Text.RegularExpressions.Regex VideoTitle = new(
        "\"title\":\\{\"runs\":\\[\\{\"text\":\"((?:[^\"\\\\]|\\\\.)*)\"");

    /// <summary>
    /// YouTube'da aramadaki ilk videoyu doğrudan açar (video kendiliğinden başlar).
    /// Arama sayfası okunur; bulunamazsa arama sonuçları açılır.
    /// </summary>
    public async Task<ToolResult> YouTubeAsync(string? query, CancellationToken ct)
    {
        var q = string.Join(' ', (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (q.Length == 0) return ToolResult.Fail("Aranacak bir şey verilmedi.", "Arama boş");
        if (q.Length > 200) q = q[..200];
        var search = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q);

        string? id = null, title = null;
        try
        {
            var html = await Web.GetStringAsync(search + "&hl=tr&gl=TR", ct);
            var m = FirstVideo.Match(html);
            if (m.Success)
            {
                id = m.Groups[1].Value;
                var t = VideoTitle.Match(html, m.Index, Math.Min(4000, html.Length - m.Index));
                if (t.Success)
                {
                    using var doc = JsonDocument.Parse("\"" + t.Groups[1].Value + "\"");
                    title = doc.RootElement.GetString();
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("YouTube araması okunamadı: {Message}", e.Message);
        }

        if (id is null)
        {
            Process.Start(new ProcessStartInfo(search) { UseShellExecute = true })?.Dispose();
            return new ToolResult(
                $"İlk video bulunamadı; '{q}' için YouTube arama sonuçları tarayıcıda açıldı. Kullanıcı istediğine tıklamalı.",
                $"YouTube'da arandı: {q}");
        }
        var url = $"https://www.youtube.com/watch?v={id}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        log.LogInformation("youtube_ac '{Query}' → {Id} {Title}", q, id, title);
        var name = title ?? q;
        return new ToolResult(
            $"YouTube'da ilk sonuç tarayıcıda açıldı ve kendiliğinden başlar: \"{name}\" ({url}). Aradığı video bu " +
            "değilse kullanıcı arama sonuçlarından seçebilir.",
            $"YouTube'da açıldı: {name}");
    }

    private static string ShortUrl(string url)
    {
        var u = new Uri(url);
        var s = u.Host.StartsWith("www.", StringComparison.Ordinal) ? u.Host[4..] : u.Host;
        var rest = Uri.UnescapeDataString(u.PathAndQuery);
        if (rest is not "/") s += rest;
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    // ---------- Notlar ----------

    /// <summary>BelgelerHalislandNotlar.md (denemede HALISLAND_NOTES_FILE ile değişir).</summary>
    public static string NotesPath { get; } =
        Environment.GetEnvironmentVariable("HALISLAND_NOTES_FILE") is { Length: > 0 } f
            ? f
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Halisland", "Notlar.md");

    private static readonly Lock NotesLock = new();

    public ToolResult AddNote(string? text)
    {
        var note = string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (note.Length == 0) return ToolResult.Fail("Boş not eklenmedi.", "Boş not eklenmedi");
        if (note.Length > 1000) note = note[..1000];

        lock (NotesLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotesPath)!);
            if (!File.Exists(NotesPath)) File.WriteAllText(NotesPath, "# Halisland notları\n\n", new UTF8Encoding(false));
            File.AppendAllText(NotesPath, $"- {DateTime.Now.ToString("d MMMM yyyy HH:mm", Tr)}: {note}\n", new UTF8Encoding(false));
        }
        return new ToolResult($"Not eklendi ({NotesPath}).", $"Not alındı: {note}");
    }

    public ToolResult ReadNotes()
    {
        if (!File.Exists(NotesPath)) return new ToolResult("Henüz hiç not yok.", "Henüz not yok");
        var notes = File.ReadAllLines(NotesPath).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        if (notes.Count == 0) return new ToolResult("Henüz hiç not yok.", "Henüz not yok");
        var last = notes.TakeLast(15).ToList();
        return new ToolResult(
            $"Son {last.Count} not (toplam {notes.Count}, eskiden yeniye):\n{string.Join('\n', last)}",
            $"{last.Count} not okundu");
    }
}
