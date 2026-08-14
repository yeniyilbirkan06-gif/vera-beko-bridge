# vera-beko-bridge — Windows Kurulum Rehberi

**Hedef ortam:** Windows 10/11 x86_64 (VERA'nın kurulu olduğu PC)
**Beklenen süre:** 30-45 dakika (birinci kez), sonra 5 dk

Bu rehber ESEN eczanesi test PC'si için birebir sıralanmıştır.
Sırayı takip et — atlarsan sonraki adım çalışmaz.

---

## ADIM 1 — Prerequisites (VC++ Redistributable)

Windows'un IntegrationHub.dll'i yükleyebilmesi için Microsoft Visual C++ 2015-2022 Redistributable gerekli. **İkisi de** (x64 + x86) şart:

1. **x64** indir + kur: https://aka.ms/vs/17/release/vc_redist.x64.exe
2. **x86** indir + kur: https://aka.ms/vs/17/release/vc_redist.x86.exe
3. **PC'yi yeniden başlat** (şart — kurulum tamamlansın diye)

Doğrulama: Denetim Masası → Programlar → "Microsoft Visual C++ 2015-2022 Redistributable (x64)" ve "(x86)" listede görünmeli.

---

## ADIM 2 — BEKO Cihaz Sürücüsü

1. TokenX'in geliştirici portalından indir:
   `TokenX Connect (Wired) Sürücü Kurulum Aracı v2.9.3.zip` (55MB)
   Kaynak: `developer.tokeninc.com` → `X-Platform → Hızlı Başlangıç` sayfası
2. Zip'i sağ tıkla → Tümünü ayıkla
3. `setup.exe` (veya benzeri) çalıştır — **internet bağlantısı açık olmalı**
4. Kurulum sırasında çıkan **tüm izin popup'larını onayla**
5. Kurulum bitince "başarılı" mesajı görmelisin. Hata varsa sihirbazı tekrar çalıştır.

**X30TR için özel not:** kurulumu yaparken cihaz ADB modu **kapalı** olmalı. Cihaz Ayarları → Geliştirici Seçenekleri → USB debugging → OFF.

---

## ADIM 3 — Cihaz(lar)ı USB'ye Tak

### X30TR
1. Type-C kablo ile PC'ye tak
2. Cihazın menüsünde: **Satış Uygulamaları → "TokenX Connect Uygulaması (Kablolu)"** seç
3. Cihaz ekranında **"izin ver" popup'ı** çıkar → **onayla**
4. Cihaz ekranında **mali numara sarı kutuda** görünmeli (başarı işareti)
5. Cihaz seri numarasını not al — **arkasında `AV` ile başlar**

### 300TR (opsiyonel)
1. Kradle üzerinden RS232 → USB ile PC'ye tak
2. Cihazda: **Menü → Ayar → Harici Cihaz Modu → GMP3 → TOKENX CONNECT** seç
3. Cihaz satış ekranında olmalı
4. Menü → Ayar → Cihaz Ayarları → Sistem Bilgisi → Sertifika Bilgisi — **sertifika verisi görünüyor** mu? (yoksa cihaz aktive edilmemiş)
5. Seri numarasını not al — **arkasında `AT` ile başlar**

### Doğrulama (Aygıt Yöneticisi)

Windows tuşu + X → Aygıt Yöneticisi → cihazlar "USB Aygıtları" veya "Ports (COM & LPT)" altında **sarı ünlem işareti OLMADAN** görünmeli.

**Görünmüyorsa:** kabloyu değiştir, farklı USB port dene, ADIM 2'yi tekrar et.

---

## ADIM 4 — Bridge'i İndir

### Seçenek A — Git clone (önerilen, güncelleme kolay)

Powershell aç:
```powershell
cd $env:LOCALAPPDATA
mkdir VERA -Force
cd VERA
git clone https://github.com/yeniyilbirkan06-gif/vera-beko-bridge.git beko-bridge
cd beko-bridge
```

Sonra Windows publish (`.NET 10 SDK` kurulu olması gerek — https://dotnet.microsoft.com/download/dotnet/10.0):
```powershell
# Powershell'de:
dotnet publish -c Release -r win-x86 --self-contained true -o publish\win-x86
# lib klasörünü publish çıktısına kopyala
Copy-Item -Recurse lib publish\win-x86\
```

Bridge exe: `%LOCALAPPDATA%\VERA\beko-bridge\publish\win-x86\vera-beko-bridge.exe`

### Seçenek B — Hazır exe (Mac'ten publish edilmiş)

Ben Mac'te publish ettim (`publish/win-x86/` — 129MB). Bu klasörü zip'leyip senin PC'ne aktarabilirim (SMB / OneDrive / USB). ESEN PC'de:
```powershell
# Zip'i aç:
Expand-Archive vera-beko-bridge-0.2.0-win-x86.zip -DestinationPath $env:LOCALAPPDATA\VERA\beko-bridge
```

---

## ADIM 5 — Bridge Konfigürasyonu

`%LOCALAPPDATA%\VERA\beko-bridge\publish\win-x86\appsettings.json` dosyasını Notepad ile aç:

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" }
  },
  "Bridge": {
    "Port": "38701",
    "Secret": "BURAYA-RASTGELE-32-KARAKTER-YAZ"
  }
}
```

**Secret üretimi** (Powershell'de rastgele 32 karakter):
```powershell
-join ((1..32) | ForEach-Object { [char](Get-Random -Min 65 -Max 90) })
```

Örnek çıktı: `KJHFDPQMVBNCXZAWERTYUIOPLKJHGFDS`

Bu değeri hem appsettings.json'a hem VERA ayarlarına yaz (ADIM 8'de).

---

## ADIM 6 — Bridge'i Başlat

### İlk deneme (manuel)

Powershell'de:
```powershell
cd $env:LOCALAPPDATA\VERA\beko-bridge\publish\win-x86
.\vera-beko-bridge.exe
```

Beklenen çıktı:
```
[bridge] Cihaz sürücüsü: BEKO IntegrationHub (Windows)
[bridge] vera-beko-bridge 0.2.0 başladı: http://127.0.0.1:38701
[bridge] Secret: ***
[beko] POSCommunication.getInstance(VERA) çağrılıyor…
[beko] IntegrationHub sürüm: 1.0.0.0
[beko] Callback'ler bağlandı
[beko] deviceState isConnected=True id=AV12345678
```

Cihaz bağlı görünüyorsa ✅ tamam. Görünmüyorsa ADIM 3'e dön.

### Kalıcı başlatma (Windows Startup)

Bridge PC her açıldığında otomatik çalışsın diye Windows Startup klasörüne kısayol koy:

```powershell
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\vera-beko-bridge.lnk")
$Shortcut.TargetPath = "$env:LOCALAPPDATA\VERA\beko-bridge\publish\win-x86\vera-beko-bridge.exe"
$Shortcut.WorkingDirectory = "$env:LOCALAPPDATA\VERA\beko-bridge\publish\win-x86"
$Shortcut.WindowStyle = 7  # Minimize
$Shortcut.Save()
```

Test: PC restart → bridge otomatik başlar mı?

---

## ADIM 7 — Sağlık Testi (Powershell)

Bridge çalışırken:

```powershell
# Sağlık check
Invoke-RestMethod http://127.0.0.1:38701/health | ConvertTo-Json -Depth 5
```

Beklenen: `hazir: True`, `cihaz.bagli: True`, `cihaz.seriNo: "AV..."`

```powershell
# Fiscal info çek
$s = "BURAYA-SECRET-YAZ"
Invoke-RestMethod -Method Post http://127.0.0.1:38701/fiscal-info/refresh -Headers @{"X-Bridge-Secret"=$s} | ConvertTo-Json -Depth 5
```

Beklenen: `basarili: True`, `kisimlar` listesi dolu (4 kısım: İLAÇ/İTRİYAT/GIDA/MUAYENE)

---

## ADIM 8 — VERA Tarafını Ayarla

1. VERA'yı aç
2. Sağ üst kullanıcı menüsü → **Entegrasyonlar**
3. Sol listeden **YNÖKÇ** seç
4. Firma grid'inden **BEKO** kartına tıkla
5. Form doldur:
   - **Model:** X30TR veya 300TR (cihazına göre)
   - **Seri No:** AV.../AT... (ADIM 3'te not aldığın)
   - **USB'ye Bağlı PC:** bu PC'nin hostname'i (Powershell: `hostname`)
6. "İleri Ayarlar"ı aç:
   - **Bridge URL:** `http://127.0.0.1:38701` (default, değiştirme)
   - **Bridge Secret:** ADIM 5'te ürettiğin secret'ı yapıştır
7. **Kaydet** butonuna bas
8. **Bağlantı Testi** ikonuna bas (sağ üst)

Beklenen toast: `BEKO X30TR bağlı — bridge 0.2.0`

Aşağıda **BRIDGE DURUMU: BAĞLI · X30TR · AV...** rozeti görünmeli.

---

## ADIM 9 — İlk Fiziksel Test Satışı

1. **Fiscal Info Yenile** butonuna bas → toast: `Cihaz fiscal bilgileri yenilendi`
2. VERA'da **Satış Merkezi** aç
3. Bir ürün ekle (barkod okut veya elle)
4. Ödeme modalına geç → **Nakit** seç → Onayla
5. **Cihazda ne oluyor?**
   - X30TR: ekranda satış özeti çıkmalı, "onay" beklemeli → onayla → fiş basılır
   - 300TR: kağıt yazar kasa fişi çıkar

6. VERA'da:
   - Toast: `Satış tamamlandı`
   - Perakende Geçmişi'nde satış görünmeli
   - **Fatura Merkezi:** e-Arşiv otomatik kesilmiş olmalı (`durum: gonderildi` veya `bekliyor`)

---

## Sorun Giderme

### Bridge başladı ama cihaz "bağlı değil" diyor
- USB kabloyu değiştir/yeniden tak
- Cihazda TokenX Connect uygulaması açık mı?
- Cihaz ekranında **mali numara sarı kutuda** mı?
- Aygıt Yöneticisi'nde sarı ünlem var mı? (varsa ADIM 2 sürücü kurulum sorunu)

### "sendBasket status != 0" hatası
- Cihaz üzerinde başka bir işlem asılı olabilir → BekoAyarlari'nda **"Cihazdaki Asılı Fişi İptal Et"** bas
- ADIM 7'de fiscal-info başarılı mı? Değilse cihaz henüz hazır değil

### VERA "BRIDGE ÇALIŞMIYOR" diyor
- `Get-Process | Where-Object { $_.Name -like "vera-beko-bridge*" }` — çalışıyor mu?
- Bridge log ekranında "Kestrel started at http://127.0.0.1:38701" satırı var mı?
- Firewall bridge port 38701 engellemiyor mu? Localhost için normalde sorun olmaz

### `IntegrationHub.dll not found`
- ADIM 4'te lib/ dizini publish klasörüne kopyalandı mı?
- `Get-ChildItem $env:LOCALAPPDATA\VERA\beko-bridge\publish\win-x86\lib\IntegrationHub.dll` var mı?

### `.NET runtime bulunamadı`
- `--self-contained true` publish edildi mi? Publish klasöründe 100+ .dll olmalı (~100MB)

---

## Sonraki Adımlar (bu turdan sonra)

- **Faz 4:** VERA installer'a bridge bundle (msi ile kurulur, manuel publish gerekmez)
- **Faz 5:** Multi-PC senaryosu (yan PC bridge'e yönlendirme)
- **Faz 6:** Windows service olarak çalıştırma (tray icon yerine)

Bu adımlar mevcut kurulumu bozmaz — ayrı iyileştirmeler.
