using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Halisland.Assistant;

// Halisland (src-tauri/src/assistant.rs) bu süreci şu ortam değişkenleriyle başlatır:
//   HALISLAND_ASSISTANT_PORT   dinlenecek port (varsayılan 47822)
//   HALISLAND_ASSISTANT_TOKEN  arayüzün her istekte göndereceği anahtar
//   HALISLAND_PARENT_PID       Halisland kapanınca bu süreç de kapanır
//   HALISLAND_PROVIDER         ollama (varsayılan), anthropic, openai ya da gemini
//   HALISLAND_MODEL            sağlayıcının modeli (yoksa varsayılanı)
//   HALISLAND_API_KEY          bulut sağlayıcının anahtarı (Windows Kimlik Bilgileri'nden)
//   HALISLAND_DATA_DIR         veritabanı klasörü (varsayılan %APPDATA%\com.halil.halisland)
// Elle `dotnet run` ile açılırsa anahtar istenmez (yalnızca geliştirme için).

var port = int.TryParse(Environment.GetEnvironmentVariable("HALISLAND_ASSISTANT_PORT"), out var p) ? p : 47822;
var token = Environment.GetEnvironmentVariable("HALISLAND_ASSISTANT_TOKEN") is { Length: > 0 } t ? t : null;
var options = AssistantOptions.FromEnvironment();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new Store(Store.DefaultDataDir()));
builder.Services.AddSingleton<Confirmations>();
builder.Services.AddSingleton<DesktopTools>();
builder.Services.AddSingleton(sp => new SpotifyService(port, sp.GetRequiredService<ILogger<SpotifyService>>()));
builder.Services.AddSingleton<ToolRegistry>();
// Sağlayıcı açılışta seçilir; değişince Halisland backend'i yeniden başlatır.
builder.Services.AddSingleton<IChatProvider>(options.Provider switch
{
    "anthropic" => new AnthropicProvider(options),
    "openai" => new OpenAiProvider(options),
    "gemini" => new GeminiProvider(options),
    _ => new OllamaProvider(options),
});
builder.Services.AddTransient<ChatService>();
// Yalnızca Halisland'in kendi penceresi (ve geliştirmede Vite) erişebilsin.
// Başka bir web sayfası istek atarsa tarayıcı ön kontrolde (preflight) durdurur.
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .WithOrigins("http://tauri.localhost", "https://tauri.localhost", "tauri://localhost", "http://localhost:1420")
    .WithHeaders("content-type", "authorization")
    .WithMethods("GET", "POST", "DELETE")));

var app = builder.Build();
app.UseCors();

if (token is not null)
{
    var expected = $"Bearer {token}";
    app.Use(async (ctx, next) =>
    {
        // Spotify izin sayfası tarayıcıyı buraya yönlendirir (anahtar taşıyamaz);
        // istek tek kullanımlık "state" ile doğrulanır.
        if (ctx.Request.Path != "/spotify/callback" && ctx.Request.Headers.Authorization != expected)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(ctx);
    });
}

WatchParent(app);

var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

app.MapGet("/health", async (IChatProvider provider, CancellationToken ct) =>
{
    var s = await provider.StatusAsync(ct);
    return new HealthResponse(
        Ok: true,
        Version: version,
        Provider: provider.Id,
        Local: provider.Local,
        Reachable: s.Reachable,
        Error: s.Error,
        Model: provider.Model,
        ModelInstalled: s.Models.Any(m => SameModel(m, provider.Model)),
        Models: s.Models);
});

// Halisland asistanı kapatınca çağırır (assistant.rs). `dotnet run` ile
// açıldığında asıl uygulama ayrı bir süreçtir; dıştaki süreç öldürülünce arkada kalırdı.
app.MapPost("/shutdown", (IHostApplicationLifetime life) =>
{
    life.StopApplication();
    return Results.NoContent();
});

app.MapPost("/warmup", async (IChatProvider provider, CancellationToken ct) =>
{
    try
    {
        await provider.WarmupAsync(ct);
        return Results.NoContent();
    }
    catch (Exception e) when (e is ProviderException or HttpRequestException)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/chat", async (ChatRequest req, ChatService chat, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Text))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var ct = ctx.RequestAborted;
    ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
    ctx.Response.Headers.CacheControl = "no-cache";
    try
    {
        await chat.RunAsync(req, e => Write(ctx, e, ct), ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // İstemci bağlantıyı kapattı.
    }
});

// Araç onayı (ör. dosya açma): akıştaki "confirm" olayına kullanıcının cevabı.
app.MapGet("/spotify", (SpotifyService s, CancellationToken ct) => s.StatusAsync(ct));
app.MapPost("/spotify/login", (SpotifyLoginRequest req, SpotifyService s) =>
    s.BeginLogin(req.ClientId) is { } error ? Results.BadRequest(error) : Results.NoContent());
app.MapDelete("/spotify", (SpotifyService s) =>
{
    s.Disconnect();
    return Results.NoContent();
});
app.MapGet("/spotify/callback", async (string? code, string? state, string? error, SpotifyService s, CancellationToken ct) =>
    Results.Content(await s.CallbackAsync(code, state, error, ct), "text/html; charset=utf-8"));

app.MapPost("/confirm/{id}", (string id, ConfirmRequest req, Confirmations confirmations) =>
    confirmations.Resolve(id, req.Approved) ? Results.NoContent() : Results.NotFound());

// ---------- Geçmiş ----------

app.MapGet("/conversations", (Store store) => store.ListConversations());

app.MapGet("/conversations/{id:long}/messages", (long id, Store store) =>
    store.ConversationExists(id) ? Results.Ok(store.GetMessages(id)) : Results.NotFound());

app.MapDelete("/conversations/{id:long}", (long id, Store store) =>
{
    store.DeleteConversation(id);
    return Results.NoContent();
});

app.MapDelete("/conversations", (Store store) =>
{
    store.DeleteAllConversations();
    return Results.NoContent();
});

// ---------- Hatırlananlar ----------

app.MapGet("/memories", (Store store) => store.ListMemories());

app.MapDelete("/memories/{id:long}", (long id, Store store) =>
    store.DeleteMemory(id) ? Results.NoContent() : Results.NotFound());

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
