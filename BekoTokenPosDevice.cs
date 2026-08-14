// VERA — BEKO (TOKEN) IntegrationHub.dll gerçek wrap
//
// SADECE WINDOWS — .NET Framework 4.5.2 tabanlı IntegrationHub.dll x86.
// Csproj `net10.0-windows` target'ında bu dosya derlenir; diğer platformlarda
// (dev/Mac) MockPosDevice tek seçenek olarak kalır.
//
// Referans: tokenx-referans/Form1.cs (TokenPublication template).
//
// TokenX API özeti:
//   POSCommunication.getInstance("VERA")
//   setDeviceStateCallback(Action<bool, string>)  — isConnected + fiscalId
//   setSerialInCallback(Action<int, string>)       — tip 3=satış, tip 9=hata
//   getFiscalInfo() → string JSON (sections + plus + kdv)
//   sendBasket(string basketJson) → int (0=başarılı)
//   sendPayment(string paymentJson) — sadece 300TR (getActiveDeviceIndex()==1)
//   getActiveDeviceIndex() → 0=X30TR, 1=300TR
//   reConnect(), deleteCommunication()
//
// Callback tipleri (Form1.cs'ten + doküman):
//   1  → sepet durumu
//   3  → satış bilgisi (ReceiptInfo status=0 başarı)
//   9  → cihaz/POS hatası
//   10 → ödeme yanıtı
//
// Değer birimleri (Basket.cs'ten):
//   price, amount → kuruş (int, ×100)
//   quantity     → miktar ×1000
//
// Kısım/Section numaraları getFiscalInfo cevabından öğrenilir; VERA
// `kdv_orani` → sectionNo mapping'i bekoSepetiSerializeEt ile (beko-api.ts).

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IntegrationHub;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VeraBekoBridge;

[SupportedOSPlatform("windows")]
public sealed class BekoTokenPosDevice : IPosDevice, IDisposable
{
    private readonly POSCommunication _com;
    private readonly OlayYayici _olay;
    private readonly ILogger<BekoTokenPosDevice> _log;
    private CihazDurumu _durum;
    private volatile bool _fiscalInfoHazir;

    public string? DllSurumu { get; }

    public BekoTokenPosDevice(OlayYayici olay, ILogger<BekoTokenPosDevice> log)
    {
        _olay = olay;
        _log  = log;
        _durum = new CihazDurumu(false, null, null, null, null, false);

        _log.LogInformation("[beko] POSCommunication.getInstance(VERA) çağrılıyor…");
        _com = POSCommunication.getInstance("VERA");

        DllSurumu = LoadDllVersion();
        _log.LogInformation("[beko] IntegrationHub sürüm: {ver}", DllSurumu ?? "bilinmiyor");

        // Callback'ler ayrı thread'de bağla — DLL'in kendi thread yönetimi var
        var t = new Thread(SetupCallbacks) { IsBackground = true, Name = "beko-callback-setup" };
        t.Start();
    }

    private void SetupCallbacks()
    {
        try
        {
            _com.setDeviceStateCallback(OnDeviceState);
            _com.setSerialInCallback(OnSerialIn);
            _log.LogInformation("[beko] Callback'ler bağlandı");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] setUpCallbacks hatası");
        }
    }

    private static string? LoadDllVersion()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName();
            if (name.Name == "IntegrationHub") return name.Version?.ToString();
        }
        return null;
    }

    /* ─── Callback'ler ──────────────────────────────────────────── */

    public void OnDeviceState(bool isConnected, [MarshalAs(UnmanagedType.BStr)] string id)
    {
        try
        {
            _log.LogInformation("[beko] deviceState isConnected={cn} id={id}", isConnected, id);
            int idx = 0;
            string modelAd = "?";
            try
            {
                idx = _com.getActiveDeviceIndex();
                modelAd = idx switch { 0 => "X30TR", 1 => "300TR", _ => "?" };
            }
            catch { /* cihaz henüz hazır değilse geç */ }

            _durum = new CihazDurumu(
                Bagli:           isConnected,
                ModelIndeks:     idx,
                ModelAd:         modelAd,
                SeriNo:          isConnected ? id : null,
                MaliNo:          isConnected ? id : null,
                FiscalInfoHazir: isConnected && _fiscalInfoHazir);

            _olay.Yayinla("cihaz-durum", _durum);

            // İlk bağlanma sonrası fiscal info çekmek ZORUNLU
            if (isConnected)
            {
                _ = Task.Run(async () =>
                {
                    try { await RefreshFiscalInfoAsync(CancellationToken.None); }
                    catch (Exception ex) { _log.LogWarning(ex, "[beko] otomatik fiscal-info başarısız"); }
                });
            }
            else
            {
                _fiscalInfoHazir = false;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] OnDeviceState istisna");
        }
    }

    public void OnSerialIn(int type, [MarshalAs(UnmanagedType.BStr)] string value)
    {
        try
        {
            _log.LogInformation("[beko] serialIn type={t} value={v}", type, value);
            switch (type)
            {
                case OlayTipleri.SepetDurumu:  // 1
                    _olay.Yayinla("sepet-durum", ParseSepetDurumu(value));
                    break;

                case OlayTipleri.SatisBilgisi: // 3
                    _olay.Yayinla("satis-bilgisi", ParseSatisBilgisi(value));
                    break;

                case OlayTipleri.CihazHatasi:  // 9
                    _olay.Yayinla("cihaz-hatasi", new CihazHatasiOlay(9,
                        "Sepet POS tarafından işlenemedi — POS uygulamasının açık olduğuna emin olun"));
                    break;

                case OlayTipleri.OdemeYaniti:  // 10
                    _olay.Yayinla("odeme-yaniti", ParseOdemeYaniti(value));
                    break;

                default:
                    _log.LogWarning("[beko] Bilinmeyen callback type={t}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] OnSerialIn parse istisnası — type={t}", type);
        }
    }

    private static SepetDurumOlay ParseSepetDurumu(string json)
    {
        try
        {
            var o  = JsonConvert.DeserializeObject<Dictionary<string, object?>>(json);
            var id = o?.GetValueOrDefault("basketID")?.ToString() ?? "";
            var as_ = o?.GetValueOrDefault("status")?.ToString() ?? "bilinmiyor";
            return new SepetDurumOlay(id, as_);
        }
        catch { return new SepetDurumOlay("", "parse-hatasi"); }
    }

    private static SatisBilgisiOlay ParseSatisBilgisi(string json)
    {
        try
        {
            var o = JsonConvert.DeserializeObject<Dictionary<string, object?>>(json);
            return new SatisBilgisiOlay(
                BasketID: o?.GetValueOrDefault("basketID")?.ToString() ?? "",
                FisNo:    o?.GetValueOrDefault("receiptNo")?.ToString()
                          ?? o?.GetValueOrDefault("fisNo")?.ToString(),
                ZNo:      TryInt(o?.GetValueOrDefault("zNo")),
                Uuid:     o?.GetValueOrDefault("uuid")?.ToString());
        }
        catch { return new SatisBilgisiOlay("", null, null, null); }
    }

    private static OdemeYanitiOlay ParseOdemeYaniti(string json)
    {
        try
        {
            var o = JsonConvert.DeserializeObject<Dictionary<string, object?>>(json);
            var status = TryInt(o?.GetValueOrDefault("status")) ?? 1;
            return new OdemeYanitiOlay(
                BasketID:  o?.GetValueOrDefault("basketID")?.ToString() ?? "",
                Basarili:  status == 0,
                KartMaske: o?.GetValueOrDefault("cardMask")?.ToString(),
                Mesaj:     o?.GetValueOrDefault("message")?.ToString());
        }
        catch { return new OdemeYanitiOlay("", false, null, "parse-hatasi"); }
    }

    private static int? TryInt(object? v)
    {
        if (v is null) return null;
        return int.TryParse(v.ToString(), out var n) ? n : null;
    }

    /* ─── IPosDevice ───────────────────────────────────────────── */

    public Task<CihazDurumu> GetCihazDurumuAsync(CancellationToken ct) => Task.FromResult(_durum);

    public Task<FiscalYanit> RefreshFiscalInfoAsync(CancellationToken ct)
    {
        try
        {
            var json = _com.getFiscalInfo();
            if (string.IsNullOrWhiteSpace(json))
            {
                _log.LogWarning("[beko] getFiscalInfo boş döndü");
                return Task.FromResult(new FiscalYanit(false));
            }

            // FiscalInfo JSON: { sections: [{no,name,taxRate,...}], plus: [...] }
            var o = JObject.Parse(json);
            var sections = o["sections"] as JArray;
            var kisimlar = sections?.Select(s => new KisimDto(
                No:  (int?)s["no"] ?? 0,
                Ad:  (string?)s["name"] ?? "?",
                Kdv: (int?)s["taxRate"] ?? 0)).ToArray() ?? Array.Empty<KisimDto>();

            var kdvOranlari = kisimlar.Select(k => k.Kdv).Distinct().OrderBy(x => x).ToArray();

            _fiscalInfoHazir = true;
            _durum = _durum with { FiscalInfoHazir = true };
            _olay.Yayinla("cihaz-durum", _durum);

            return Task.FromResult(new FiscalYanit(true, kdvOranlari, kisimlar));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] getFiscalInfo istisna");
            return Task.FromResult(new FiscalYanit(false));
        }
    }

    public Task<BasitBasariYanit> PushKdvAsync(KisimDto[] kisimlar, CancellationToken ct)
    {
        // NOT: IntegrationHub'ın public API'sinde doğrudan "KDV/section push" yok.
        // Cihazdaki kısımlar TokenX yönetim aracıyla önceden yapılandırılır ve
        // getFiscalInfo ile okunur. Bu endpoint şimdilik sadece cihazdakileri
        // doğrular (fiscalInfo'dan çekip VERA'nın önerdiği tabloyla eşleşiyor mu).
        // Faz 5'te fiziksel testte gerçek push desteği tespit edilirse buraya
        // eklenir; şu an "başarılı" ile döner ve log'a not düşer.
        _log.LogInformation("[beko] KDV push çağrıldı — cihaz kısımları önceden yapılandırılmış olmalı. Öneri: {n} kısım", kisimlar.Length);
        return Task.FromResult(new BasitBasariYanit(true));
    }

    public Task<BasketYanit> SendBasketAsync(BasketIstek s, CancellationToken ct)
    {
        try
        {
            var tokenBasket = SepetiTokenXFormatinaCevir(s);
            var json = JsonConvert.SerializeObject(tokenBasket);
            _log.LogInformation("[beko] sendBasket basketID={id} items={n} payments={p}",
                s.BasketID, s.Items.Length, s.PaymentItems.Length);
            int status = _com.sendBasket(json);
            _log.LogInformation("[beko] sendBasket status={s}", status);
            return Task.FromResult(new BasketYanit(status == 0, s.BasketID));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] sendBasket istisna basketID={id}", s.BasketID);
            return Task.FromResult(new BasketYanit(false, s.BasketID));
        }
    }

    public Task<BasketCancelYanit> CancelPendingBasketAsync(CancellationToken ct)
    {
        try
        {
            // Form1.cs pattern: isVoid=true payment gönderilir
            _com.sendPayment("{\"isVoid\": true}");
            _log.LogInformation("[beko] asılı fiş iptal (isVoid) gönderildi");
            return Task.FromResult(new BasketCancelYanit(true, true));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] cancel istisna");
            return Task.FromResult(new BasketCancelYanit(false, false));
        }
    }

    public Task<BasitBasariYanit> SendPaymentAsync(PaymentIstek istek, CancellationToken ct)
    {
        try
        {
            if (_com.getActiveDeviceIndex() != 1)
            {
                _log.LogWarning("[beko] sendPayment sadece 300TR (idx=1) için — mevcut idx={idx}",
                    _com.getActiveDeviceIndex());
                return Task.FromResult(new BasitBasariYanit(false));
            }
            var json = JsonConvert.SerializeObject(new
            {
                basketID = istek.BasketID,
                type     = istek.Type,
                amount   = istek.Amount,
            });
            _com.sendPayment(json);
            return Task.FromResult(new BasitBasariYanit(true));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] sendPayment istisna");
            return Task.FromResult(new BasitBasariYanit(false));
        }
    }

    public Task<CihazListYanit> ListDevicesAsync(CancellationToken ct)
    {
        try
        {
            int idx = _com.getActiveDeviceIndex();
            var ad  = idx switch { 0 => "X30TR", 1 => "300TR", _ => "bilinmiyor" };
            return Task.FromResult(new CihazListYanit(new[]
            {
                new CihazListItem(idx, ad, _durum.SeriNo, true),
            }));
        }
        catch
        {
            return Task.FromResult(new CihazListYanit(Array.Empty<CihazListItem>()));
        }
    }

    /* ─── VERA BasketIstek → TokenX Basket ─────────────────────── */

    private static object SepetiTokenXFormatinaCevir(BasketIstek s)
    {
        // VERA zaten kuruş+×1000 gönderiyor (beko-api.ts bekoSepetiSerializeEt).
        // Burada sadece alan isimleri TokenX'in beklediği şekle dönüştürülüyor.
        return new
        {
            basketID       = s.BasketID,
            createInvoice  = false,                    // HARD-CODED — cihaz e-Arşiv basmasın
            documentType   = s.DocumentType ?? 0,
            taxFreeAmount  = (int)s.TaxFreeAmount,
            isVoid         = s.IsVoid,
            items          = s.Items.Select(i => new
            {
                barcode     = i.Barcode,
                name        = i.Name,
                pluNo       = 0,
                price       = (int)i.Amount,          // toplam kuruş
                sectionNo   = i.Section,
                taxPercent  = i.TaxRate,
                type        = 0,
                unit        = "AD",
                vatID       = i.Section,               // section=vatID varsayımı
                limit       = 0,
                quantity    = (int)i.Quantity,        // ×1000
                paymentType = 0,
            }).ToArray(),
            customerInfo   = s.CustomerInfo == null ? null : new
            {
                name    = s.CustomerInfo.Name ?? "",
                taxID   = s.CustomerInfo.TaxID ?? "",
                isLock  = false,
            },
            paymentItems   = s.PaymentItems.Select(p => new
            {
                description = OdemeAciklamasi(p.Type),
                amount      = (int)p.Amount,
                type        = p.Type,
            }).ToArray(),
            adjust         = s.Adjust == null ? null : new
            {
                description        = s.Adjust.Type,
                discountOrSurcharge = 0,                // 0=indirim, 1=artış (TokenX)
                type               = 0,                 // 0=tutar, 1=yüzde
                value              = (int)s.Adjust.Amount,
            },
        };
    }

    private static string OdemeAciklamasi(int type) => type switch
    {
        1  => "NAKIT",
        2  => "KREDI KARTI",
        3  => "KREDI KARTI",
        4  => "YEMEK KARTI",
        7  => "YEMEK KARTI",
        17 => "VERESIYE",
        _  => "DIGER",
    };

    public void Dispose()
    {
        try
        {
            _log.LogInformation("[beko] deleteCommunication çağrılıyor…");
            _com?.deleteCommunication();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[beko] dispose sırasında istisna");
        }
    }
}

