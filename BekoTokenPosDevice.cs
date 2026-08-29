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
//   sendBasket(string basketJson) → int (1=başarılı, 0=başarısız)
//     Referans: TokenPublication template Form1.cs:294 `if (basketStatus == 1)`
//     Wire envanter §2 [Sepet & Ödeme]. VERA <2026-08-29 tersine yazılıydı (BUG#30).
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
            // Wire envanter §3 BASKET_COMPLETED: status 0=başarılı, -1=iptal, 99=fiş iptali.
            // Bu alan olmayan durumlar (eski cihaz firmware'i vs) için null → VERA
            // tarafında null=başarılı fallback (mevcut davranış).
            return new SatisBilgisiOlay(
                BasketID: o?.GetValueOrDefault("basketID")?.ToString() ?? "",
                FisNo:    o?.GetValueOrDefault("receiptNo")?.ToString()
                          ?? o?.GetValueOrDefault("fisNo")?.ToString(),
                ZNo:      TryInt(o?.GetValueOrDefault("zNo")),
                Uuid:     o?.GetValueOrDefault("uuid")?.ToString()
                          ?? o?.GetValueOrDefault("UUID")?.ToString(),
                Status:   TryInt(o?.GetValueOrDefault("status")));
        }
        catch { return new SatisBilgisiOlay("", null, null, null, null); }
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

            // FiscalInfo JSON: { sections: [{no,name,taxRate,...}], plus: [...],
            //                    receiptLimit, eDocumentStatus }
            var o = JObject.Parse(json);
            var sections = o["sections"] as JArray;
            var kisimlar = sections?.Select(s => new KisimDto(
                No:  (int?)s["no"] ?? 0,
                Ad:  (string?)s["name"] ?? "?",
                Kdv: (int?)s["taxRate"] ?? 0)).ToArray() ?? Array.Empty<KisimDto>();

            var kdvOranlari = kisimlar.Select(k => k.Kdv).Distinct().OrderBy(x => x).ToArray();

            // Faz 6c — cihaz e-belge modu (VUK 593 rejimi).
            // Portal §mimari-ve-is-akislari: eDocumentStatus=1 (aktif) ise cihaz
            // kendi e-arşiv/e-fatura keser. Field yoksa false (Bilgi Fişi rejimi
            // devam eder — mevcut Faz 6b davranışı).
            var eDocumentAktif = (int?)o["eDocumentStatus"] == 1;
            var receiptLimit = (long?)o["receiptLimit"];

            _fiscalInfoHazir = true;
            _durum = _durum with {
                FiscalInfoHazir = true,
                EDocumentAktif  = eDocumentAktif,
                ReceiptLimit    = receiptLimit,
            };
            _olay.Yayinla("cihaz-durum", _durum);

            _log.LogInformation(
                "[beko] fiscal info: {n} kısım, e-belge modu={mod}, receipt limit={lim}",
                kisimlar.Length,
                eDocumentAktif ? "AKTİF (cihaz kendi e-belge keser)" : "PASİF (Bilgi Fişi)",
                receiptLimit?.ToString() ?? "yok");

            return Task.FromResult(new FiscalYanit(true, kdvOranlari, kisimlar, eDocumentAktif, receiptLimit));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] getFiscalInfo istisna");
            return Task.FromResult(new FiscalYanit(false));
        }
    }

    public async Task<BasitBasariYanit> PushKdvAsync(KisimDto[] kisimlar, CancellationToken ct)
    {
        // IntegrationHub public API'sinde doğrudan "section push" yok. Cihazdaki
        // kısımlar TokenX Yönetim Aracı + mali PIN ile bayı tarafından yüklenir.
        // Bu endpoint doğrulama görevi görür: cihazdakileri getFiscalInfo ile
        // çeker + VERA'nın önerdiği tabloyla diff'i log'a yazar.
        // (VERA UI'da bu buton disable edildi + tooltip eklendi — bkz. BekoAyarlari.tsx)
        var mevcut = await RefreshFiscalInfoAsync(ct);
        if (!mevcut.Basarili || mevcut.Kisimlar is null)
        {
            _log.LogWarning("[beko] KDV push doğrulama: cihaz fiscal info alınamadı");
            return new BasitBasariYanit(false);
        }

        var mevcutMap = mevcut.Kisimlar.ToDictionary(k => k.No, k => k);
        var uyumsuzluk = new List<string>();
        foreach (var onerilen in kisimlar)
        {
            if (!mevcutMap.TryGetValue(onerilen.No, out var m))
            {
                uyumsuzluk.Add($"section {onerilen.No}={onerilen.Ad} cihazda YOK");
            }
            else if (m.Kdv != onerilen.Kdv * 100 && m.Kdv != onerilen.Kdv)
            {
                // Cihaz taxRate genelde ×100 (KDV %10 → 1000). VERA tarafında yüzde.
                uyumsuzluk.Add($"section {onerilen.No}: cihaz KDV={m.Kdv}, VERA={onerilen.Kdv}%");
            }
        }

        if (uyumsuzluk.Count == 0)
        {
            _log.LogInformation("[beko] KDV push doğrulama BAŞARILI — {n} kısım cihazda beklenen konfigürasyonda", kisimlar.Length);
        }
        else
        {
            _log.LogWarning("[beko] KDV push doğrulama UYUMSUZ ({n} fark): {list}",
                uyumsuzluk.Count, string.Join(" | ", uyumsuzluk));
        }
        return new BasitBasariYanit(true);  // Kontrat: her zaman true (VERA UI zaten disable)
    }

    public Task<BasketYanit> SendBasketAsync(BasketIstek s, CancellationToken ct)
    {
        try
        {
            // Wire envanter §1: "fiscal bilgi alınmadan satış yasak". DeviceState
            // callback isConnected=true'da RefreshFiscalInfoAsync otomatik tetikleniyor
            // ama sepet çağrısı bundan önce gelirse cihaz reject eder. Kısa devre:
            if (!_fiscalInfoHazir)
            {
                _log.LogWarning("[beko] sendBasket engellendi — fiscal info hazır değil (cihaz henüz getFiscalInfo dönmedi)");
                return Task.FromResult(new BasketYanit(false, s.BasketID));
            }
            var tokenBasket = SepetiTokenXFormatinaCevir(s);
            var json = JsonConvert.SerializeObject(tokenBasket);
            _log.LogInformation("[beko] sendBasket basketID={id} items={n} payments={p}",
                s.BasketID, s.Items.Length, s.PaymentItems.Length);
            int status = _com.sendBasket(json);
            _log.LogInformation("[beko] sendBasket status={s} (1=başarılı, 0=başarısız)", status);
            return Task.FromResult(new BasketYanit(status == 1, s.BasketID));
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
            // IntegrationHub pattern: isVoid=true payment gönderilir (Form1.cs).
            // NOT: Bu fire-and-forget — cihaz gerçekten iptal ettiğinde SSE
            // `sepet-durum` event'i ile bildirir. Kontrat gereği Basarili=komut
            // gönderildi, IptalEdildi=cihaz kabul etti anlamında.
            _com.sendPayment("{\"isVoid\": true}");
            _log.LogInformation("[beko] iptal komutu gönderildi (sepet-durum SSE onayı bekle)");
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

    public Task<BasitBasariYanit> ReConnectAsync(CancellationToken ct)
    {
        try
        {
            _log.LogInformation("[beko] reConnect() çağrılıyor…");
            _com.reConnect();
            _log.LogInformation("[beko] reConnect() döndü — deviceState callback bekleniyor");
            return Task.FromResult(new BasitBasariYanit(true));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[beko] reConnect istisna");
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
        // int32 sınırı: ~21M kuruş = 214.748 TL. Bir sepetin taxFreeAmount veya
        // tek kalem tutarı bu sınırı aşarsa TokenX int32 alanı taşar — throw et,
        // sessizce bozuk gönderim yapma.
        long IntSafe(long v, string alan)
        {
            if (v < int.MinValue || v > int.MaxValue)
                throw new OverflowException(
                    $"BEKO alan '{alan}' değeri {v} kuruş (~{v / 100} TL) int32 sınırını aşıyor. " +
                    "Tek sepette bu kadar büyük satış cihazın da kabul etmediği bir senaryo.");
            return v;
        }

        return new
        {
            basketID       = s.BasketID,
            createInvoice  = false,                    // HARD-CODED — cihaz e-Arşiv basmasın
            documentType   = s.DocumentType ?? 0,
            taxFreeAmount  = (int)IntSafe(s.TaxFreeAmount, "taxFreeAmount"),
            isVoid         = s.IsVoid,
            items          = s.Items.Select(i => new
            {
                barcode     = i.Barcode,
                name        = i.Name,
                pluNo       = 0,
                // Wire envanter §4 items[] (doküman esas):
                //   price      = BİRİM fiyat kuruş (5 TL/adet → 500), toplam DEĞİL
                //   taxPercent = KDV ×100 (%10 → 1000, %20 → 2000)
                // <2026-08-29 sürümü BUG#18: price=Amount (toplam) + taxPercent=raw idi.
                // ESEN'de fark edilmedi çünkü tek adetli test (Amount==Price).
                // Prev audit "false positive" değerlendirmesi Basket.cs iç
                // calculatePrice() hesabına dayanıyordu — cihazın price alanı
                // semantiği ≠ template'in iç sepet-toplam formülü.
                price       = (int)IntSafe(i.Price, $"items[{i.Barcode}].price"),   // birim kuruş
                sectionNo   = i.Section,
                taxPercent  = i.TaxRate * 100,        // %10=1000, %20=2000
                type        = 0,
                unit        = "AD",
                vatID       = i.Section,               // section=vatID varsayımı
                limit       = 0,
                quantity    = (int)IntSafe(i.Quantity, $"items[{i.Barcode}].quantity"),  // ×1000
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
                amount      = (int)IntSafe(p.Amount, $"paymentItems[type={p.Type}].amount"),
                type        = p.Type,
            }).ToArray(),
            adjust         = s.Adjust == null ? null : new
            {
                description        = s.Adjust.Type,
                discountOrSurcharge = 0,                // 0=indirim, 1=artış (TokenX)
                type               = 0,                 // 0=tutar, 1=yüzde
                value              = (int)IntSafe(s.Adjust.Amount, "adjust.value"),
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

