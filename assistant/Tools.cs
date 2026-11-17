using System.Text.Json;

namespace Halisland.Assistant;

/// <summary>Aracın sonucu: modele geri giden metin ve arayüzde gösterilecek kısa not.</summary>
public sealed record ToolResult(string ForModel, string ForUser);

/// <summary>
/// Modelin çağırabildiği araçlar (Ollama tool calling). Faz 22: yalnızca
/// hafıza (hatirla, unut). Faz 23'te dosya, uygulama ve not araçları eklenecek.
/// </summary>
public sealed class ToolRegistry(Store store)
{
    public const int MaxMemoryLength = 300;

    public static readonly OllamaTool[] Definitions =
    [
        // Silme aracı bilerek yok: denemede model "unutma" (= hatırla) isteğinde
        // silme aracını çağırıp başka bir bilgiyi sildi. Silme yalnızca Hafıza listesinden.
        Tool(
            "hatirla",
            "Kullanıcı hakkında kalıcı bir bilgiyi gelecekteki sohbetlerde kullanmak üzere kaydeder. " +
            "Kullanıcı bir bilgiyi saklamanı istediğinde MUTLAKA çağır: 'hatırla', 'unutma', 'aklında tut', " +
            "'aklında olsun', 'not al', 'kaydet' gibi. Kullanıcı istemeden kendiliğinden çağırma.",
            """
            {"type":"object","properties":{"bilgi":{"type":"string","description":"Kaydedilecek bilgi; kısa, tek cümle, üçüncü tekil kişiyle (ör. 'Kullanıcının adı Halil.')"}},"required":["bilgi"]}
            """),
    ];

    private static OllamaTool Tool(string name, string description, string schema) =>
        new("function", new OllamaFunctionDef(name, description, JsonDocument.Parse(schema).RootElement.Clone()));

    public ToolResult Execute(OllamaFunctionCall call)
    {
        try
        {
            return call.Name switch
            {
                "hatirla" => Remember(GetString(call.Arguments, "bilgi")),
                _ => new ToolResult($"Böyle bir araç yok: {call.Name}", $"Bilinmeyen araç: {call.Name}"),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
        {
            return new ToolResult($"Araç çalışmadı: {e.Message}", "Araç çalışmadı");
        }
    }

    public ToolResult Remember(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return new ToolResult("Boş bilgi kaydedilmedi.", "Boş bilgi kaydedilmedi");
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
