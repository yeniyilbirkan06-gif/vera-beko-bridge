# vera-beko-bridge

VERA Eczane Otomasyonu için **BEKO (TOKEN) YNÖKÇ HTTP+SSE köprüsü**.
Tauri (Rust + WebView) IntegrationHub.dll'e (C# .NET managed) doğrudan
bağlanamadığı için bu sidecar Windows'ta ayrı bir süreç olarak çalışır.

```
┌─────────────────┐    HTTP/SSE localhost:38701     ┌──────────────────────┐    USB    ┌─────────────┐
│  VERA (Tauri)   │ ─────────────────────────────▶  │ vera-beko-bridge.exe │ ────────▶ │ BEKO X30TR  │
│  beko-api.ts    │                                 │ (.NET 10, ASP.NET)   │           │ / 300TR     │
│                 │ ◀───────── SSE events ────────  │ IntegrationHub.dll   │           │             │
└─────────────────┘                                 └──────────────────────┘           └─────────────┘
```

## Fazlar

| Faz  | İş                                                             | Durum        |
|------|----------------------------------------------------------------|--------------|
| 3    | Skeleton (endpoint stub'ları + MockPosDevice)                  | ✅ Bu tur    |
| 3.5  | IntegrationHub.dll wrap (BekoTokenPosDevice) — Windows only    | ⏳ Sonraki   |
| 4    | Installer bundling (Tauri config + MSI/NSIS prerequisites)     | ⏳           |
| 5    | ESEN Windows PC'de fiziksel test (X30TR + 300TR)               | ⏳           |

## Endpoint Kontratı

Tam şema: VERA memory `project_beko_bridge_endpoint_kontrati`.
Özet:

| Metot | Yol                       | İş                                        |
|-------|---------------------------|-------------------------------------------|
| GET   | `/health`                 | Bridge + cihaz durum + DLL sürümü         |
| POST  | `/fiscal-info/refresh`    | Cihaz kısımlar/KDV/ürünler çek (ZORUNLU)  |
| POST  | `/kdv-push`               | KDV/kısım tablosunu cihaza yaz            |
| POST  | `/basket`                 | Sepet gönder (kabul, sonuç SSE'den)       |
| POST  | `/basket/cancel`          | Asılı/bekleyen fişi iptal                 |
| POST  | `/payment`                | Kısmi ödeme (sadece 300TR)                |
| GET   | `/devices`                | Bağlı cihaz listesi                       |
| GET   | `/events`                 | SSE stream (secret query string ile)      |

### SSE Event tipleri

| event            | payload           | TokenX tip |
|------------------|-------------------|-----------:|
| `cihaz-durum`    | `CihazDurumu`     | -          |
| `sepet-durum`    | `SepetDurumOlay`  | 1          |
| `satis-bilgisi`  | `SatisBilgisiOlay`| 3          |
| `cihaz-hatasi`   | `CihazHatasiOlay` | 9          |
| `odeme-yaniti`   | `OdemeYanitiOlay` | 10         |

## Auth

- HTTP: `X-Bridge-Secret: <secret>` header
- SSE:  `?secret=<secret>` query (EventSource native header desteklemez)
- Health: secret opsiyonel (VERA sağlık check'i)
- Bind: sadece `127.0.0.1` — dış erişim engelli

## Geliştirme (Mac / Linux)

```bash
# Mock cihaz ile çalıştır
dotnet run
# → http://127.0.0.1:38701 dinlemede
# Development mode secret: "dev-mock-secret-degistir"

# Health check
curl http://127.0.0.1:38701/health

# KDV push (mock)
curl -X POST http://127.0.0.1:38701/kdv-push \
  -H "Content-Type: application/json" \
  -H "X-Bridge-Secret: dev-mock-secret-degistir" \
  -d '{"kisimlar":[{"no":1,"ad":"İLAÇ","kdv":10}]}'

# SSE dinle
curl "http://127.0.0.1:38701/events?secret=dev-mock-secret-degistir"
```

## Windows Build (self-contained)

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:PublishTrimmed=false \
  -o ./publish/win-x64
```

Çıktı: `publish/win-x64/vera-beko-bridge.exe` (~60-80 MB, .NET runtime dahil).

## Faz 3.5 — IntegrationHub.dll Entegrasyonu (TODO)

1. TokenX'in referans template'i klonla: `git clone https://github.com/TokenPublication/hizlisepet-clienttemplate`
2. IntegrationHub.dll'i cihaz sürücüsü sihirbazından al (bkz. developer.tokeninc.com)
3. `PosDevice.cs`'te `BekoTokenPosDevice : IPosDevice` yaz:
   - `POSCommunication.getInstance("VERA")`
   - `setDeviceStateCallback` → OlayYayici → SSE
   - `setSerialInCallback` → tip 1/3/9/10 → SSE event isimleri
   - `sendBasket(basketID, jsonString)` → BasketIstek serialization (kuruş+×1000)
4. `Program.cs`'te `AddSingleton<IPosDevice, BekoTokenPosDevice>()` (mock yerine)
5. VC++ Redist prerequisite installer + zadig-x30tr driver kurulum sihirbazı

## Yapılandırma

`appsettings.json`:

```json
{
  "Bridge": {
    "Port": "38701",
    "Secret": ""
  }
}
```

Production'da `Bridge:Secret` ayarlanmalı — installer sırasında rastgele
üretilir ve hem bu dosyaya hem VERA `ayarlar.beko_bridge_secret`'a
yazılır (bkz. VERA memory `project_beko_ynokc_entegrasyon`).

## İlgili Memory

- `project_beko_ynokc_entegrasyon` — genel mimari, kararlar
- `project_beko_bridge_endpoint_kontrati` — tam endpoint şeması
- `project_okc_vuk507` — VUK 507 vs 483 çift-fatura kontrolü
