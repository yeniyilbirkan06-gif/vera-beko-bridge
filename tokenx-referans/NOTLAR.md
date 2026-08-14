# TokenX Referans Kod (Faz 3.5 için)

Kaynak: `github.com/TokenPublication/hizlisepet-clienttemplate`
Son alım: 2026-08-14

Bu klasördeki dosyalar TokenX'in **resmi referans template'inden** indirildi.
Sadece **okunacak kaynak** — bu bridge'in build'ine dahil DEĞİL. Faz 3.5'te
`BekoTokenPosDevice : IPosDevice` yazılırken IntegrationHub kullanım
pattern'lerine bakmak için burada tutuluyor.

## Dosyalar

| Dosya                 | İçerik                                              |
|-----------------------|-----------------------------------------------------|
| `Program.cs`          | `POSCommunication.getInstance("TOKEN FINTECH")` init |
| `Form1.cs` (45KB)     | Ana kullanım — callback'ler, sendBasket, sendPayment, getFiscalInfo, getActiveDeviceIndex, reConnect, deleteCommunication |
| `Basket.cs`           | Basket DTO (items, paymentItems, customerInfo, adjust) |
| `FiscalInfo.cs`       | FiscalInfo response DTO                              |
| `ReceiptInfo.cs`      | Receipt info DTO                                     |
| `InfoReceiptInfo.cs`  | InfoReceipt DTO                                      |
| `ExBasketForms.cs`    | Ekstra form (görsel, ilgisiz)                        |
| `README-orig.md`      | TokenX'in orijinal README'si                         |

## Kritik API özeti (Form1.cs'ten çıkarıldı)

```csharp
// Init (singleton)
var communication = IntegrationHub.POSCommunication.getInstance("VERA");

// Callback bağla
communication.setDeviceStateCallback((bool isConnected, string id) => { ... });
communication.setSerialInCallback(serialInCallback);  // tip 1/3/9/10

// Aktif cihaz index (0=X30TR, 1=300TR)
int idx = communication.getActiveDeviceIndex();

// Fiscal info çek (isConnected=true sonrası ZORUNLU)
string fiscalJson = communication.getFiscalInfo();

// Sepet gönder — JSON string olarak
int status = communication.sendBasket(basketJsonString);

// Kısmi ödeme (SADECE 300TR)
communication.sendPayment(paymentJsonString);

// Void
communication.sendPayment("{\"isVoid\": true}");

// Yeniden bağlan / temizle
communication.reConnect();
communication.deleteCommunication();
```

## Faz 3.5 Yapılacaklar

`vera-beko-bridge/BekoTokenPosDevice.cs` (yeni):

```csharp
using IntegrationHub;

public sealed class BekoTokenPosDevice : IPosDevice
{
    private readonly POSCommunication _com;
    private readonly OlayYayici _olay;
    private CihazDurumu _durum = /* default: bağlı değil */;

    public BekoTokenPosDevice(OlayYayici olay)
    {
        _olay = olay;
        _com = POSCommunication.getInstance("VERA");
        _com.setDeviceStateCallback(OnDeviceState);
        _com.setSerialInCallback(OnSerialIn);
    }

    // deviceStateCallback → OlayYayici → "cihaz-durum" SSE event
    private void OnDeviceState(bool bagli, string id) { ... }

    // serialInCallback → tip 1/3/9/10 parse → uygun SSE event
    private void OnSerialIn(...) { ... }

    public async Task<BasketYanit> SendBasketAsync(BasketIstek s, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(BasketDto.From(s));
        int status = _com.sendBasket(json);
        return new BasketYanit(status == 0, s.BasketID);
    }
    // ... diğer IPosDevice metotları
}
```

`Program.cs`'te (mock yerine):
```csharp
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<IPosDevice, BekoTokenPosDevice>();
else
    builder.Services.AddSingleton<IPosDevice, MockPosDevice>();
```

## x86 vs x64 Uyarısı

`lib/IntegrationHub.dll` = **PE32 (x86)** — sidecar publish'te:
```
dotnet publish -c Release -r win-x86 --self-contained true /p:PublishSingleFile=true
```
`win-x64` yerine `win-x86` seçilmeli — DLL 64-bit süreçte load edemez.

## Lisans Notu

TokenPublication template'i public repo — kod referans için serbest.
IntegrationHub.dll ticari kullanımı BEKO/TOKEN sözleşmesi kapsamında.
VERA Yazılım Ltd. Şti. 2026-08-14 sözleşmesi ile lisanslı.
