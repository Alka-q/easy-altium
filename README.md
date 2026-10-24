# EasyAltium — LCSC & JLCPCB Library Loader for Altium Designer 26.10.1

**EasyAltium**, Altium Designer 26.10.1 (64-bit) için SamacSys (Altium Library Loader) benzeri çalışan, LCSC ve JLCPCB üzerindeki elektronik bileşenleri (**Sembol, Footprint, 3D STEP Model ve BOM Parametreleri**) tek tıkla veya Windows panosu (`Clipboard`) dinleme yöntemiyle Altium'a aktaran hibrit mimarili açık kaynaklı bir masaüstü uygulamasıdır.

---

## Sistem Mimarisi

1. **`EasyAltium.Host` (.NET 8 WPF İstemcisi):**
   - Sistem tepsisinde (`System Tray`) çalışır ve her zaman üstte (`Always-on-Top`) kompakt bir arama/durum penceresi sunar.
   - Win32 `AddClipboardFormatListener` API'si ile panoyu dinler; `^C[1-9]\d{2,}$` (Örn: `C2040`, `C14663`) formatında bir LCSC parça kodu kopyalandığında sağ altta bildirim/onay penceresi (`PartPreviewPopup`) açar.
   - Parçanın MPN, Üretici, Kılıf, Stok, Fiyat ve JLCPCB `Basic`/`Extended` sınıfını önizleme olarak gösterir.

2. **`EasyAltium.Core` (.NET 8 Çekirdek Motor):**
   - **Birincil Motor (`NpnpCliWrapper`):** Rust tabanlı `npnp` CLI aracını arka planda çağırarak `.SchLib`, `.PcbLib` ve `.step` dosyalarını `./Library/SchLib`, `./Library/PcbLib`, `./Library/STEP` dizinlerine çıkarır.
   - **Pasif Eleman Sembol Filtresi (`PassiveSymbolFilter`):** Direnç, Kapasitör ve İndüktörlerde isteğe bağlı olarak yerel standart sembol şablonunu (`RES_STD`, `CAP_NP_STD`, `CAP_POL_STD`, `IND_STD`) kullanır; Footprint, 3D Model ve BOM parametrelerini LCSC'den eşleştirir.
   - **Yedek Mekanizma (`AsciiFallbackHandler`):** Çok parçalı karmaşık sembollerde veya `npnp` hata döndürdüğünde Altium Protel ASCII `.SchDoc` / `.PcbDoc` dosyalarını üretip Altium'un `MakeLibrary` komutunu tetikler.
   - **Mod A (`MasterLibraryManager`):** Parçaları ortak `LCSC_Master.SchLib` ve `LCSC_Master.PcbLib` içine kaydeder.
   - **Mod B (`SqliteDbLibManager` & `DbLibFileGenerator`):** Kategorize edilmiş 7 tablolu (`Resistors`, `Capacitors`, `Inductors`, `ICs`, `Connectors`, `Discrete`, `Misc`) `lcsc_components.sqlite` veritabanına `UPSERT` yapar ve Altium 26.10.1 uyumlu `EasyAltium_LCSC.DbLib` dosyasını üretir.

3. **`altium-bridge` (Altium Designer 26.10.1 DelphiScript Köprüsü):**
   - `LCSC_Bridge.PrjScr` ve `Bridge.pas` üzerinden `ipc/current_part.ini` veri sözleşmesini okur.
   - Kütüphaneleri `IntegratedLibraryManager` ile Altium'a yükler/yeniler.
   - Aktif bir `.SchDoc` açıksa `SchServer` API'sini kullanarak bileşeni tüm BOM parametreleri ve Footprint bağlantısıyla oluşturup kullanıcının fare imlecinin ucuna (`InteractiveCursor`) yerleştirir.

---

## Hızlı Başlangıç

```powershell
# Çözümü derle
dotnet build EasyAltium.sln

# Entegrasyon ve birim doğrulama testlerini çalıştır
dotnet run --project tests/EasyAltium.Tests/EasyAltium.Tests.csproj

# WPF Masaüstü İstemcisini başlat
dotnet run --project src/EasyAltium.Host/EasyAltium.Host.csproj
```

### Altium CLI Tetikleme Formatı
```powershell
"C:\Program Files\Altium\AD26\X2.EXE" -RScriptingSystem:RunScript(ProjectName="D:\wb\antigravity\easyaltium-q\altium-bridge\LCSC_Bridge.PrjScr"|ProcName="Bridge.pas>PlaceOrLoadComponent")
```
