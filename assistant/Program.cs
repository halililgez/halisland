using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Halisland.Assistant;

// Halisland (src-tauri/src/assistant.rs) bu süreci şu ortam değişkenleriyle başlatır:
//   HALISLAND_ASSISTANT_PORT   dinlenecek port (varsayılan 47822)
//   HALISLAND_ASSISTANT_TOKEN  arayüzün her istekte göndereceği anahtar
//   HALISLAND_PARENT_PID       Halisland kapanınca bu süreç de kapanır
// Elle `dotnet run` ile açılırsa anahtar istenmez (yalnızca geliştirme için).

var port = int.TryParse(Environment.GetEnvironmentVariable("HALISLAND_ASSISTANT_PORT"), out var p) ? p : 47822;
var token = Environment.GetEnvironmentVariable("HALISLAND_ASSISTANT_TOKEN") is { Length: > 0 } t ? t : null;
var options = AssistantOptions.FromEnvironment();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<OllamaClient>(c =>
{
    c.BaseAddress = new Uri(options.OllamaUrl);
    // Akış uzun sürebilir (düşünme açıkken dakikalarca); iptal isteğe bağlı.
    c.Timeout = Timeout.InfiniteTimeSpan;
});
// Yalnızca Halisland'in kendi penceresi (ve geliştirmede Vite) erişebilsin.
// Başka bir web sayfası istek atarsa tarayıcı ön kontrolde (preflight) durdurur.
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .WithOrigins("http://tauri.localhost", "https://tauri.localhost", "tauri://localhost", "http://localhost:1420")
    .WithHeaders("content-type", "authorization")
    .WithMethods("GET", "POST")));

var app = builder.Build();
app.UseCors();

if (token is not null)
{
    var expected = $"Bearer {token}";
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Headers.Authorization != expected)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(ctx);
    });
}

WatchParent(app);

var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

app.MapGet("/health", async (OllamaClient ollama, CancellationToken ct) =>
{
    var models = await ollama.ListModelsAsync(ct);
    return new HealthResponse(
        Ok: true,
        Version: version,
        Ollama: models is not null,
        Model: options.Model,
        ModelInstalled: models?.Any(m => SameModel(m, options.Model)) ?? false,
        Models: models ?? []);
});

app.MapPost("/warmup", async (OllamaClient ollama, CancellationToken ct) =>
{
    try
    {
        await ollama.WarmupAsync(ct);
        return Results.NoContent();
    }
    catch (Exception e) when (e is OllamaException or HttpRequestException)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/chat", async (ChatRequest req, OllamaClient ollama, HttpContext ctx) =>
{
    var ct = ctx.RequestAborted;
    ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";

    var messages = Conversation.Build(req.Messages, options.NumCtx);
    try
    {
        await foreach (var chunk in ollama.ChatAsync(messages, req.Think, ct))
        {
            if (chunk.Error is { } error)
            {
                await Write(ctx, new StreamEvent("error", Message: error), ct);
                return;
            }
            if (chunk.Message?.Thinking is { Length: > 0 } thinking)
                await Write(ctx, new StreamEvent("thinking", Text: thinking), ct);
            if (chunk.Message?.Content is { Length: > 0 } content)
                await Write(ctx, new StreamEvent("token", Text: content), ct);
            if (chunk.Done)
            {
                double? speed = chunk is { EvalCount: > 0, EvalDuration: > 0 }
                    ? Math.Round(chunk.EvalCount.Value / (chunk.EvalDuration.Value / 1e9), 1)
                    : null;
                await Write(ctx, new StreamEvent("done", Tokens: chunk.EvalCount, TokensPerSec: speed), ct);
            }
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Kullanıcı durdurdu; Ollama isteği de iptal edildi.
    }
    catch (Exception e) when (e is OllamaException or HttpRequestException or JsonException)
    {
        var message = e is HttpRequestException ? "Ollama'ya ulaşılamadı. Çalıştığından emin ol." : e.Message;
        await Write(ctx, new StreamEvent("error", Message: message), ct);
    }
});

app.Run();
return;

static async Task Write(HttpContext ctx, StreamEvent e, CancellationToken ct)
{
    await JsonSerializer.SerializeAsync(ctx.Response.Body, e, AppJson.Default.StreamEvent, ct);
    await ctx.Response.Body.WriteAsync("\n"u8.ToArray(), ct);
    await ctx.Response.Body.FlushAsync(ct);
}

// "qwen3:8b" ile "qwen3:8b" eşleşir; etiketsiz ad ("qwen3") ":latest" sayılır.
static bool SameModel(string installed, string wanted)
{
    static string Norm(string s) => s.Contains(':') ? s : $"{s}:latest";
    return string.Equals(Norm(installed), Norm(wanted), StringComparison.OrdinalIgnoreCase);
}

// Halisland çökse ya da zorla kapatılsa bile bu süreç arkada kalmasın.
static void WatchParent(WebApplication app)
{
    if (!int.TryParse(Environment.GetEnvironmentVariable("HALISLAND_PARENT_PID"), out var pid)) return;
    Process parent;
    try
    {
        parent = Process.GetProcessById(pid);
    }
    catch (ArgumentException)
    {
        Environment.Exit(0);
        return;
    }
    _ = Task.Run(async () =>
    {
        await parent.WaitForExitAsync();
        await app.StopAsync();
    });
}

/// <summary>Modele gidecek mesaj listesi: sistem istemi + sığdığı kadar son mesaj.</summary>
internal static class Conversation
{
    /// <summary>Kabaca token tahmini; Türkçe metinde ~3 karakter bir token.</summary>
    private static int Estimate(string s) => s.Length / 3 + 4;

    public static List<ChatMessage> Build(IEnumerable<ChatMessage> history, int numCtx)
    {
        var system = new ChatMessage("system", SystemPrompt());
        // Cevaba yer kalsın: bağlamın dörtte biri cevap için ayrılır.
        var budget = numCtx * 3 / 4 - Estimate(system.Content);

        var kept = new List<ChatMessage>();
        foreach (var m in history.Reverse())
        {
            if (m.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(m.Content)) continue;
            budget -= Estimate(m.Content);
            if (budget < 0 && kept.Count > 0) break;
            kept.Add(m);
        }
        kept.Reverse();
        kept.Insert(0, system);
        return kept;
    }

    private static string SystemPrompt()
    {
        var tr = CultureInfo.GetCultureInfo("tr-TR");
        var now = DateTime.Now.ToString("d MMMM yyyy dddd, HH:mm", tr);
        return $"""
            Sen Halisland'in yerel asistanısın. Kullanıcının bilgisayarında, internete çıkmadan çalışıyorsun.
            Türkçe, kısa ve net cevap ver; gerekmedikçe uzun liste ya da başlık kullanma.
            Bilmediğin bir şeyi uydurma, emin değilsen söyle.
            Şu an: {now}.
            """;
    }
}
