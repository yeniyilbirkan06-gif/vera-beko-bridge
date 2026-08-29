// VERA — vera-beko-bridge DTO'lar
//
// Kontrat kaynağı: [[project_beko_bridge_endpoint_kontrati]]
// Tutarlar kuruş (long), miktarlar ×1000. Zaman ISO 8601 UTC.
//
// Bu dosya sadece istek/cevap şemalarını tanımlar; iş mantığı IPosDevice'a
// delege edilir. VERA (Tauri) tarafındaki src/lib/beko-api.ts ile birebir
// uyumlu tutulmalıdır.

using System.Text.Json.Serialization;

namespace VeraBekoBridge;

/* ─── Hata ─────────────────────────────────────────────────── */
public sealed record HataDetay(string Kod, string Mesaj, string? Detay = null);
public sealed record HataYanit([property: JsonPropertyName("hata")] HataDetay Hata);

/* ─── Health ───────────────────────────────────────────────── */
public sealed record CihazDurumu(
    bool Bagli,
    int? ModelIndeks,
    string? ModelAd,
    string? SeriNo,
    string? MaliNo,
    bool FiscalInfoHazir);

public sealed record HealthYanit(
    bool Hazir,
    string BridgeSurumu,
    string? DllSurumu,
    bool VcRedistKurulu,
    CihazDurumu? Cihaz,
    string? SonHata);

/* ─── Fiscal Info ──────────────────────────────────────────── */
public sealed record FiscalYanit(
    bool Basarili,
    int[]? KdvOranlari = null,
    KisimDto[]? Kisimlar = null);

/* ─── KDV Push ─────────────────────────────────────────────── */
public sealed record KisimDto(int No, string Ad, int Kdv);
public sealed record KdvPushIstek(KisimDto[] Kisimlar);
public sealed record BasitBasariYanit(bool Basarili);

/* ─── Basket ───────────────────────────────────────────────── */
public sealed record CustomerInfoDto(string? TaxID, string? Name, string? Address);

public sealed record ItemDto(
    string Name,
    string Barcode,
    long Price,        // kuruş
    long Quantity,     // ×1000
    int TaxRate,       // yüzde
    int Section,       // 1=İLAÇ, 2=İTRİYAT, 3=GIDA, 4=MUAYENE
    long Amount);      // kuruş

public sealed record PaymentItemDto(int Type, long Amount);  // Type: 1=nakit, 2=kredi, 17=veresiye
public sealed record AdjustDto(string Type, long Amount);

public sealed record BasketIstek(
    string BasketID,
    int? DocumentType,
    bool CreateInvoice,   // VERA'dan hep false gelir — cihaz e-Arşiv basmasın
    bool IsVoid,
    CustomerInfoDto? CustomerInfo,
    ItemDto[] Items,
    PaymentItemDto[] PaymentItems,
    AdjustDto? Adjust,
    long TaxFreeAmount);

public sealed record BasketYanit(bool Kabul, string BasketID);
public sealed record BasketCancelYanit(bool Basarili, bool IptalEdildi);

/* ─── Payment (300TR için — kısmi ödeme) ───────────────────── */
public sealed record PaymentIstek(string BasketID, int Type, long Amount);

/* ─── Devices listesi ──────────────────────────────────────── */
public sealed record CihazListItem(int Indeks, string Ad, string? SeriNo, bool Aktif);
public sealed record CihazListYanit(CihazListItem[] Cihazlar);

/* ─── SSE Event Payload'ları ───────────────────────────────── */
// TokenX IntegrationHub callback tip kodları
public static class OlayTipleri
{
    public const int SepetDurumu = 1;
    public const int SatisBilgisi = 3;
    public const int CihazHatasi = 9;
    public const int OdemeYaniti = 10;
}

public sealed record SepetDurumOlay(string BasketID, string Asama);
/// <summary>
/// Wire envanter §3: type=3 satış bilgisi payload'ı.
/// Status: 0=başarılı, -1=iptal, 99=fiş iptali (envanter §3 BASKET_COMPLETED)
/// </summary>
public sealed record SatisBilgisiOlay(string BasketID, string? FisNo, int? ZNo, string? Uuid, int? Status);
public sealed record CihazHatasiOlay(int Kod, string Mesaj);
public sealed record OdemeYanitiOlay(string BasketID, bool Basarili, string? KartMaske, string? Mesaj);
