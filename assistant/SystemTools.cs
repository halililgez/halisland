using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Halisland.Assistant;

/// <summary>
/// Faz 27: güç işlemleri (kilitle, uyku, oturumu kapat, yeniden başlat,
/// kapat), panoya metin yazma ve pil durumu. Yalnızca P/Invoke (budanmış
/// derlemede COM kapalı).
/// </summary>
public sealed class SystemTools(ILogger<SystemTools> log)
{
    // ---------- Güç ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public static readonly Dictionary<string, string> PowerLabels = new()
    {
        ["kilitle"] = "Ekran kilitlensin mi?",
        ["uyku"] = "Bilgisayar uyku moduna geçsin mi?",
        ["oturumu_kapat"] = "Oturum kapatılsın mı? Kaydedilmemiş işler kaybolabilir.",
        ["yeniden_baslat"] = "Bilgisayar yeniden başlatılsın mı? Kaydedilmemiş işler kaybolabilir.",
        ["kapat"] = "Bilgisayar kapatılsın mı? Kaydedilmemiş işler kaybolabilir.",
    };

    public ToolResult Power(string action)
    {
        log.LogInformation("guc {Action}", action);
        switch (action)
        {
            case "kilitle":
                return LockWorkStation()
                    ? new ToolResult("Ekran kilitlendi.", "Ekran kilitlendi")
                    : ToolResult.Fail("Ekran kilitlenemedi.", "Kilitlenemedi");
            case "uyku":
                // Cevap yazılabilsin diye biraz bekleyip uyut.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    SetSuspendState(false, false, false);
                });
                return new ToolResult("Bilgisayar birkaç saniye içinde uyku moduna geçiyor.", "Uyku moduna geçiyor");
            case "oturumu_kapat":
                Shutdown("/l");
                return new ToolResult("Oturum kapatılıyor.", "Oturum kapatılıyor");
            case "yeniden_baslat":
                Shutdown("/r /t 5");
                return new ToolResult("Bilgisayar 5 saniye içinde yeniden başlıyor.", "Yeniden başlıyor");
            case "kapat":
                Shutdown("/s /t 5");
                return new ToolResult("Bilgisayar 5 saniye içinde kapanıyor.", "Kapanıyor");
            default:
                return ToolResult.Fail($"Bilinmeyen güç işlemi: {action}", "Bilinmeyen işlem");
        }
    }

    private static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });

    // ---------- Pano ----------

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    private const uint CfUnicodeText = 13, GmemMoveable = 0x2;
    public const int MaxClipboard = 20_000;

    /// <summary>Metni panoya yazar (Halisland'in pano geçmişine de düşer).</summary>
    public ToolResult CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ToolResult.Fail("Kopyalanacak metin boş.", "Metin boş");
        if (text.Length > MaxClipboard) text = text[..MaxClipboard];
        // Pano başka bir uygulamada açıksa birkaç kez dene.
        var opened = false;
        for (var i = 0; i < 10 && !(opened = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(30);
        if (!opened) return ToolResult.Fail("Pano şu an kullanımda; kopyalanamadı.", "Kopyalanamadı");
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return ToolResult.Fail("Bellek ayrılamadı.", "Kopyalanamadı");
            var p = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(CfUnicodeText, h) == IntPtr.Zero)
            {
                GlobalFree(h);
                return ToolResult.Fail("Panoya yazılamadı.", "Kopyalanamadı");
            }
        }
        finally
        {
            CloseClipboard();
        }
        log.LogInformation("pano_kopyala {Length} karakter", text.Length);
        return new ToolResult("Metin panoya kopyalandı; kullanıcı Ctrl+V ile yapıştırabilir.", "Panoya kopyalandı");
    }

    // ---------- Pil ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    /// <summary>Ör. "Pil: %64, şarj oluyor"; pil yoksa null.</summary>
    public static string? Battery()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryLifePercent == 255) return null;
        var text = $"Pil: %{s.BatteryLifePercent}";
        if (s.AcLineStatus == 1) text += (s.BatteryFlag & 8) != 0 ? ", şarj oluyor" : ", prize takılı";
        else if (s.BatteryLifeTime > 0) text += $", yaklaşık {TimeSpan.FromSeconds(s.BatteryLifeTime):h\\:mm} kaldı";
        if (s.SystemStatusFlag == 1) text += ", pil tasarrufu açık";
        return text;
    }
}
