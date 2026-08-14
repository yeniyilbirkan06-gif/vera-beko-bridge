// VERA — vera-beko-bridge sidecar (BEKO/TOKEN YNÖKÇ HTTP+SSE köprüsü)
//
// Kontrat: [[project_beko_bridge_endpoint_kontrati]]
// - Bind:  127.0.0.1:38701 (default, override edilebilir)
// - Auth:  X-Bridge-Secret header (SSE'de query ?secret=)
// - CORS:  tauri://*, http://tauri.localhost origin'lerine izin
// - SSE:   GET /events (Server-Sent Events stream)
//
// Faz 3 (bugün): Mock cihaz. Windows'ta gerçek IntegrationHub.dll wrap Faz 3.5.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using VeraBekoBridge;

var builder = WebApplication.CreateBuilder(args);

/* ─── Konfigürasyon ─────────────────────────────────────────── */

var portStr = builder.Configuration["Bridge:Port"] ?? "38701";
var port    = int.TryParse(portStr, out var p) ? p : 38701;
var secret  = builder.Configuration["Bridge:Secret"] ?? "";

if (string.IsNullOrWhiteSpace(secret))
{
    Console.WriteLine("[bridge] UYARI: Bridge:Secret ayarlanmamış — bridge güvensiz modda çalışıyor (dev only).");
}

// Sadece localhost'a bind — dış erişim engelli
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenLocalhost(port);
});

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(policy => policy
        .SetIsOriginAllowed(origin =>
        {
            if (string.IsNullOrEmpty(origin)) return true;
            // Tauri WebView origin'leri + development için http://localhost:*
            return origin.StartsWith("tauri://",       StringComparison.OrdinalIgnoreCase)
                || origin.StartsWith("http://tauri.",  StringComparison.OrdinalIgnoreCase)
                || origin.StartsWith("https://tauri.", StringComparison.OrdinalIgnoreCase)
                || origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                || origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);
        })
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// JSON: camelCase VERA tarafıyla uyumlu
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy   = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Servisler — mock cihaz (Faz 3.5'te gerçek BekoTokenPosDevice ile değişir)
builder.Services.AddSingleton<OlayYayici>();
builder.Services.AddSingleton<IPosDevice, MockPosDevice>();

var app = builder.Build();

/* ─── Middleware ────────────────────────────────────────────── */

app.UseCors();

// Bridge secret kontrolü — HEALTH ve EVENTS hariç tüm endpoint'ler için
app.Use(async (ctx, next) =>
{
    // OPTIONS preflight'ı geçir
    if (HttpMethods.IsOptions(ctx.Request.Method)) { await next(); return; }

    var yol = ctx.Request.Path.Value ?? "";

    // /events için query string ?secret=... beklenir (EventSource header desteklemez)
    if (yol.StartsWith("/events", StringComparison.Ordinal))
    {
        var qSecret = ctx.Request.Query["secret"].ToString();
        if (!string.IsNullOrEmpty(secret) && qSecret != secret)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new HataYanit(new HataDetay("YETKI", "Bridge secret uyuşmadı")));
            return;
        }
        await next();
        return;
    }

    // Health kontrol için secret opsiyonel — VERA sağlık check'i uzaktan yapabilsin
    if (yol.Equals("/health", StringComparison.Ordinal))
    {
        await next();
        return;
    }

    var headerSecret = ctx.Request.Headers["X-Bridge-Secret"].ToString();
    if (!string.IsNullOrEmpty(secret) && headerSecret != secret)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new HataYanit(new HataDetay("YETKI", "Bridge secret uyuşmadı")));
        return;
    }

    await next();
});

/* ─── Endpoint'ler ──────────────────────────────────────────── */

// Bridge sürümü (csproj Version'dan)
var bridgeVer = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

app.MapGet("/health", async (IPosDevice cihaz, CancellationToken ct) =>
{
    CihazDurumu? durum = null;
    string? sonHata = null;
    try { durum = await cihaz.GetCihazDurumuAsync(ct); }
    catch (Exception e) { sonHata = e.Message; }
    return Results.Ok(new HealthYanit(
        Hazir:          durum?.Bagli == true,
        BridgeSurumu:   bridgeVer,
        DllSurumu:      cihaz.DllSurumu,
        VcRedistKurulu: OperatingSystem.IsWindows(),  // Windows dışında true kabul (mock)
        Cihaz:          durum,
        SonHata:        sonHata));
});

app.MapPost("/fiscal-info/refresh", async (IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.RefreshFiscalInfoAsync(ct)));

app.MapPost("/kdv-push", async (KdvPushIstek istek, IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.PushKdvAsync(istek.Kisimlar, ct)));

app.MapPost("/basket", async (BasketIstek sepet, IPosDevice cihaz, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(sepet.BasketID))
        return Results.BadRequest(new HataYanit(new HataDetay("SEPET_ID_YOK", "basketID zorunlu")));
    if (sepet.Items is null || sepet.Items.Length == 0)
        return Results.BadRequest(new HataYanit(new HataDetay("KALEM_YOK", "En az bir item olmalı")));
    var y = await cihaz.SendBasketAsync(sepet, ct);
    return Results.Ok(y);
});

app.MapPost("/basket/cancel", async (IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.CancelPendingBasketAsync(ct)));

app.MapPost("/payment", async (PaymentIstek istek, IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.SendPaymentAsync(istek, ct)));

app.MapGet("/devices", async (IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.ListDevicesAsync(ct)));

// SSE stream — abone olur, olay geldikçe yazar
app.MapGet("/events", async (HttpContext ctx, OlayYayici yayici, IPosDevice cihaz, CancellationToken ct) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.ContentType  = "text/event-stream";
    ctx.Response.Headers.Connection   = "keep-alive";
    // Reverse-proxy varsa Nginx X-Accel-Buffering off (localhost'ta zaten yok, dokümantasyon)
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    // İlk mesaj: mevcut cihaz durumunu snapshot olarak yolla
    try
    {
        var durum = await cihaz.GetCihazDurumuAsync(ct);
        await SseYaz(ctx, "cihaz-durum", durum, ct);
    }
    catch { /* mock/gerçek cihaz getirişte hata olursa sessizce geç */ }

    using var abonelik = yayici.Abone(out var reader);

    // 15 sn'de bir keep-alive comment
    var keepAliveTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
    var keepAliveTask = Task.Run(async () =>
    {
        try
        {
            while (await keepAliveTimer.WaitForNextTickAsync(ct))
            {
                await ctx.Response.WriteAsync(": keep-alive\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch { /* iptal edildi */ }
    }, ct);

    try
    {
        await foreach (var mesaj in reader.ReadAllAsync(ct))
        {
            await SseYaz(ctx, mesaj.OlayAdi, mesaj.Payload, ct);
        }
    }
    catch (OperationCanceledException) { /* client kapattı */ }
    finally
    {
        keepAliveTimer.Dispose();
        try { await keepAliveTask; } catch { /* yoksay */ }
    }
});

// Root — quick sanity check
app.MapGet("/", () => Results.Text(
    $"vera-beko-bridge {bridgeVer} — sağlıklı endpoint: /health\n" +
    "Kontrat: project_beko_bridge_endpoint_kontrati (VERA memory)"));

Console.WriteLine($"[bridge] vera-beko-bridge {bridgeVer} başladı: http://127.0.0.1:{port}");
Console.WriteLine($"[bridge] Mod: {(builder.Services.Any(s => s.ImplementationType == typeof(MockPosDevice)) ? "MOCK" : "GERÇEK")}");
Console.WriteLine($"[bridge] Secret: {(string.IsNullOrEmpty(secret) ? "AYARLANMAMIŞ (dev only)" : "***")}");

app.Run();

/* ─── SSE yazma helper ──────────────────────────────────────── */
static async Task SseYaz(HttpContext ctx, string olayAdi, object payload, CancellationToken ct)
{
    var jsonOpts = new JsonSerializerOptions
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    var json = JsonSerializer.Serialize(payload, jsonOpts);
    await ctx.Response.WriteAsync($"event: {olayAdi}\ndata: {json}\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
}
