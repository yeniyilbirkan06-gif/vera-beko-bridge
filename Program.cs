// VERA — vera-beko-bridge sidecar (BEKO/TOKEN YNÖKÇ HTTP+SSE köprüsü)
//
// Kontrat: [[project_beko_bridge_endpoint_kontrati]]
// - Bind:  127.0.0.1:38701 (default, override edilebilir)
// - Auth:  X-Bridge-Secret header (SSE'de query ?secret=)
// - CORS:  tauri://*, http://tauri.localhost origin'lerine izin
// - SSE:   GET /events (Server-Sent Events stream)
//
// Faz 3 (bugün): Mock cihaz. Windows'ta gerçek IntegrationHub.dll wrap Faz 3.5.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using VeraBekoBridge;

// Windows console default CP1254 → Türkçe log'da 'YİYECEK' → 'Y¦YECEK'.
// Payload'da (DLL'e giden string) etki YOK (UTF-16), ama debug'ı zehirliyor.
// Bu satırın en başta olması ŞART — sonraki tüm Console.WriteLine çıktıları UTF-8.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

/* ─── Konfigürasyon ─────────────────────────────────────────── */
/* Faz 5 Sidecar (2026-09-01): Secret 3 kaynaktan okunabilir (ASP.NET Core
 *   Configuration default sıralaması):
 *   1. Environment Variable `Bridge__Secret` — VERA sidecar bunu enjekte eder
 *      (Rust'ta Command::new_sidecar().env("Bridge__Secret", random)).
 *      Task Manager'da CLI args'ta görünmez → güvenli (Gemini §5 önerisi).
 *   2. CLI arg `--Bridge:Secret=xxx` — dev/debug için.
 *   3. appsettings.json `{ "Bridge": { "Secret": "..." } }` — legacy standalone
 *      kurulum için fallback (Faz 4 zip modeli).
 * Faz 5 sonrası prod'da (1) esas, appsettings.json artık gizli olmayabilir
 *   (installer secret üretip env var ile geçirir). */

var portStr = builder.Configuration["Bridge:Port"] ?? "38701";
var port    = int.TryParse(portStr, out var p) ? p : 38701;
var secret  = builder.Configuration["Bridge:Secret"] ?? "";

if (string.IsNullOrWhiteSpace(secret))
{
    // Production'da secret zorunlu — bridge'e sadece VERA erişebilsin (aynı PC'de
    // başka bir process localhost'a HTTP atarsa 401 alsın). Development'ta hata
    // ayıklama kolaylığı için sadece uyarı.
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "Bridge:Secret bulunamadı. VERA sidecar modunda Bridge__Secret env var " +
            "ile enjekte eder (Faz 5). Standalone modda appsettings.json 'Secret' " +
            "alanına 32 karakter rastgele değer yazın.");
    }
    Console.WriteLine("[bridge] UYARI: Bridge:Secret ayarlanmamış — DEV mode only.");
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

// Servisler
builder.Services.AddSingleton<OlayYayici>();

// Cihaz sürücüsü: Windows'ta gerçek IntegrationHub.dll wrap, aksi hâlde mock.
// Ortam değişkeni BEKO_MOCK=1 ile Windows'ta da mock zorlanabilir (dev/CI).
var mockZorunlu = string.Equals(builder.Configuration["BEKO_MOCK"], "1", StringComparison.Ordinal)
                || string.Equals(Environment.GetEnvironmentVariable("BEKO_MOCK"), "1", StringComparison.Ordinal);

if (OperatingSystem.IsWindows() && !mockZorunlu)
{
    builder.Services.AddSingleton<IPosDevice, BekoTokenPosDevice>();
    Console.WriteLine("[bridge] Cihaz sürücüsü: BEKO IntegrationHub (Windows)");
}
else
{
    builder.Services.AddSingleton<IPosDevice, MockPosDevice>();
    Console.WriteLine($"[bridge] Cihaz sürücüsü: MOCK ({(OperatingSystem.IsWindows() ? "BEKO_MOCK=1" : "Windows dışı")})");
}

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

    // Health dahil TÜM endpoint secret gerektirir. Eski bypass VERA secret bug'ını
    // maskeleyip false-positive "sağlık OK" gösteriyordu (ESEN teşhisi zorlaştı).
    // Localhost bind + secret kombinasyonu defense-in-depth: aynı PC'deki başka
    // proses (browser, malware) bridge'e istek atsa 401 alsın.
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

// /basket idempotency cache — VERA retry ederse (network hatası, timeout) aynı
// basketID ile 2. POST cihaza gitmemeli, cached yanıtı dön (kontrat gereği).
// TTL 30 sn: normal bir sepet-tamamlama akışı için yeterli, sonra evict edilir.
var basketCache = new ConcurrentDictionary<string, (DateTime Ts, BasketYanit Yanit)>();

app.MapPost("/basket", async (BasketIstek sepet, IPosDevice cihaz, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(sepet.BasketID))
        return Results.BadRequest(new HataYanit(new HataDetay("SEPET_ID_YOK", "basketID zorunlu")));
    if (sepet.Items is null || sepet.Items.Length == 0)
        return Results.BadRequest(new HataYanit(new HataDetay("KALEM_YOK", "En az bir item olmalı")));

    // Idempotency check
    if (basketCache.TryGetValue(sepet.BasketID, out var cached))
    {
        if (DateTime.UtcNow - cached.Ts < TimeSpan.FromSeconds(30))
        {
            Console.WriteLine($"[bridge] /basket idempotency-hit basketID={sepet.BasketID}");
            return Results.Ok(cached.Yanit);
        }
        basketCache.TryRemove(sepet.BasketID, out _);
    }

    var y = await cihaz.SendBasketAsync(sepet, ct);
    basketCache[sepet.BasketID] = (DateTime.UtcNow, y);

    // Cache temizlik — 60 sn'den eski girdileri sil (fire-and-forget)
    if (basketCache.Count > 100)
    {
        _ = Task.Run(() =>
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(60);
            foreach (var kv in basketCache)
                if (kv.Value.Ts < cutoff) basketCache.TryRemove(kv.Key, out _);
        });
    }

    return Results.Ok(y);
});

// G1 fix (2026-08-31) — cancel body opsiyonel `{basketID}` alır.
// X30TR path'i `sendBasket({basketID, isVoid:true})` gerektirir, 300TR path'i
// basketID'siz `sendPayment({isVoid:true})` ile çalışır. Body yoksa X30TR
// iptal reddedilir; VERA basketID göndersin.
app.MapPost("/basket/cancel", async (BasketCancelIstek? istek, IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.CancelPendingBasketAsync(istek?.BasketID, ct)));

// TokenX v2.0.1 reConnect() — kablo çekilip takıldığında SDK otomatik reconnect
// eder ama garanti yok; VERA sağlık check "cihaz bağlı değil" gördüğünde çağırır.
app.MapPost("/reconnect", async (IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.ReConnectAsync(ct)));

app.MapPost("/payment", async (PaymentIstek istek, IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.SendPaymentAsync(istek, ct)));

// G2 (2026-08-31) — 300TR split-payment orchestration.
// VERA aynı basketID ile önce /basket POST edip sonra buraya çağırıyor.
// Bridge sırayla her ödemeyi gönderir + type=10 ACK bekler.
app.MapPost("/payment/split", async (SplitPaymentIstek istek, IPosDevice cihaz, CancellationToken ct) =>
    Results.Ok(await cihaz.SplitPaymentAsync(istek, ct)));

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
