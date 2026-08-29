// VERA — Cihaz soyutlaması
//
// IPosDevice — bridge'in konuştuğu tek arayüz. Gerçek implementasyon
// (BekoTokenPosDevice) IntegrationHub.dll wrap eder ve Windows'ta çalışır.
// MockPosDevice — cihaz olmadan Mac/Linux'ta bridge'i geliştirmek için.
//
// Aktif implementasyon `IPosDevice`'ın DI kaydına göre seçilir
// (bkz. Program.cs).

using System.Threading.Channels;

namespace VeraBekoBridge;

/// <summary>
/// TokenX IntegrationHub.dll'in soyutlaması. Tüm çağrılar async.
/// </summary>
public interface IPosDevice
{
    /// <summary>Cihaz + fiscal info hazır mı, seri no, mali no vb.</summary>
    Task<CihazDurumu> GetCihazDurumuAsync(CancellationToken ct);

    /// <summary>getFiscalInfo() çağırır — sepet göndermeden önce ZORUNLU.</summary>
    Task<FiscalYanit> RefreshFiscalInfoAsync(CancellationToken ct);

    /// <summary>Cihaza KDV/kısım tablosunu push eder.</summary>
    Task<BasitBasariYanit> PushKdvAsync(KisimDto[] kisimlar, CancellationToken ct);

    /// <summary>Sepeti cihaza gönderir. Sonuç SSE üzerinden gelir; bu çağrı sadece kabul.</summary>
    Task<BasketYanit> SendBasketAsync(BasketIstek sepet, CancellationToken ct);

    /// <summary>Cihazdaki asılı/bekleyen sepeti iptal eder.</summary>
    Task<BasketCancelYanit> CancelPendingBasketAsync(CancellationToken ct);

    /// <summary>Kısmi ödeme (sadece 300TR). X30TR'de destek yok.</summary>
    Task<BasitBasariYanit> SendPaymentAsync(PaymentIstek istek, CancellationToken ct);

    /// <summary>Bağlı cihazları listele (şimdilik tek cihaz senaryosu).</summary>
    Task<CihazListYanit> ListDevicesAsync(CancellationToken ct);

    /// <summary>
    /// Kesilmiş bağlantıyı yeniden kur — TokenX v2.0.1 `reConnect()`.
    /// Kablo yeniden takıldığında SDK otomatik reconnect eder ama garanti yok.
    /// VERA sağlık check'i cihaz bağlı değil dönüşünde çağırabilir.
    /// </summary>
    Task<BasitBasariYanit> ReConnectAsync(CancellationToken ct);

    /// <summary>DLL sürümü (health endpoint için).</summary>
    string? DllSurumu { get; }
}

/// <summary>
/// SSE stream'ini besleyen basit pub/sub. Her /events aboneliği kendi
/// Channel'ını alır; broker producer yayınladığında tüm abonelere yazar.
/// Gerçek cihaz callback'leri IPosDevice implementasyonu tarafından
/// buraya yayınlanır.
/// </summary>
public sealed class OlayYayici
{
    private readonly List<Channel<OlayMesaji>> _aboneler = new();
    private readonly Lock _kilit = new();

    public IDisposable Abone(out ChannelReader<OlayMesaji> reader)
    {
        var ch = Channel.CreateBounded<OlayMesaji>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        lock (_kilit) _aboneler.Add(ch);
        reader = ch.Reader;
        return new AbonelikSahibi(this, ch);
    }

    public void Yayinla(string olayAdi, object payload)
    {
        var mesaj = new OlayMesaji(olayAdi, payload);
        lock (_kilit)
        {
            foreach (var ch in _aboneler)
                ch.Writer.TryWrite(mesaj);
        }
    }

    private void CikarAbone(Channel<OlayMesaji> ch)
    {
        lock (_kilit) _aboneler.Remove(ch);
        ch.Writer.TryComplete();
    }

    private sealed class AbonelikSahibi : IDisposable
    {
        private readonly OlayYayici _p;
        private readonly Channel<OlayMesaji> _ch;
        private bool _kapali;
        public AbonelikSahibi(OlayYayici p, Channel<OlayMesaji> ch) { _p = p; _ch = ch; }
        public void Dispose() { if (_kapali) return; _kapali = true; _p.CikarAbone(_ch); }
    }
}

public sealed record OlayMesaji(string OlayAdi, object Payload);

/// <summary>
/// Cihaz olmadan bridge'i geliştirmek için mock. Rastgele başarı/hata
/// üretmez — hep deterministik "başarılı" cevaplar döner ki VERA
/// tarafında happy-path testi güvenilir olsun.
/// </summary>
public sealed class MockPosDevice : IPosDevice
{
    private readonly OlayYayici _olayYayici;
    private CihazDurumu _durum;

    public string? DllSurumu => "MOCK-0.1";

    public MockPosDevice(OlayYayici olayYayici)
    {
        _olayYayici = olayYayici;
        _durum = new CihazDurumu(
            Bagli:            true,
            ModelIndeks:      0,
            ModelAd:          "X30TR (MOCK)",
            SeriNo:           "AV00000000",
            MaliNo:           "MOCK-MALI-000",
            FiscalInfoHazir:  false);
    }

    public Task<CihazDurumu> GetCihazDurumuAsync(CancellationToken ct) => Task.FromResult(_durum);

    public Task<FiscalYanit> RefreshFiscalInfoAsync(CancellationToken ct)
    {
        _durum = _durum with { FiscalInfoHazir = true };
        _olayYayici.Yayinla("cihaz-durum", _durum);
        return Task.FromResult(new FiscalYanit(
            Basarili:     true,
            KdvOranlari:  new[] { 0, 1, 10, 20 },
            Kisimlar:     new[]
            {
                new KisimDto(1, "İLAÇ", 10),
                new KisimDto(2, "İTRİYAT", 20),
                new KisimDto(3, "GIDA TAKVİYESİ", 1),
                new KisimDto(4, "MUAYENE ÜCRETİ", 0),
            }));
    }

    public Task<BasitBasariYanit> PushKdvAsync(KisimDto[] kisimlar, CancellationToken ct)
        => Task.FromResult(new BasitBasariYanit(true));

    public Task<BasketYanit> SendBasketAsync(BasketIstek sepet, CancellationToken ct)
    {
        // Mock: kabul et, sonra 500ms içinde sepet-durum + odeme + satis-bilgisi yayınla
        _ = Task.Run(async () =>
        {
            _olayYayici.Yayinla("sepet-durum", new SepetDurumOlay(sepet.BasketID, "sepet-alindi"));
            await Task.Delay(200);
            _olayYayici.Yayinla("odeme-yaniti", new OdemeYanitiOlay(sepet.BasketID, true, "**** 0000", "MOCK onayı"));
            await Task.Delay(200);
            _olayYayici.Yayinla("satis-bilgisi", new SatisBilgisiOlay(
                BasketID: sepet.BasketID,
                FisNo:    "MOCK-" + DateTime.UtcNow.ToString("HHmmss"),
                ZNo:      1,
                Uuid:     Guid.NewGuid().ToString(),
                Status:   0));
        });
        return Task.FromResult(new BasketYanit(true, sepet.BasketID));
    }

    public Task<BasketCancelYanit> CancelPendingBasketAsync(CancellationToken ct)
        => Task.FromResult(new BasketCancelYanit(true, true));

    public Task<BasitBasariYanit> SendPaymentAsync(PaymentIstek istek, CancellationToken ct)
        => Task.FromResult(new BasitBasariYanit(true));

    public Task<BasitBasariYanit> ReConnectAsync(CancellationToken ct)
        => Task.FromResult(new BasitBasariYanit(true));

    public Task<CihazListYanit> ListDevicesAsync(CancellationToken ct)
        => Task.FromResult(new CihazListYanit(new[]
        {
            new CihazListItem(0, "X30TR (MOCK)", "AV00000000", true),
        }));
}
