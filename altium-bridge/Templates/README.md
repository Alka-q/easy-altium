# Pasif Eleman Standart Sembol Şablonları (`Standard_Passives.SchLib`)

Bu dizin, **Pasif Eleman Sembol Filtresi** ("Kendi Standart Sembolümü Kullan" seçeneği) aktif olduğunda kullanılır.

## Desteklenen Standart Sembol Referansları (`PassiveTemplateLibRef`)
- `RES_STD` : Standart Direnç Sembolü (2 Pin: `1`, `2`)
- `CAP_NP_STD` : Kutupsuz Kapasitör Sembolü (2 Pin: `1`, `2`)
- `CAP_POL_STD` : Kutuplu / Elektrolitik Kapasitör Sembolü (2 Pin: `1`, `2`)
- `IND_STD` : Standart İndüktör / Bobin Sembolü (2 Pin: `1`, `2`)

Kullanıcı kendi kurumsal `Standard_Passives.SchLib` dosyasını bu dizine koyduğunda veya Ayarlar penceresinden seçtiğinde, `Bridge.pas` sembol grafiğini bu şablondan alır; **Footprint**, **3D STEP Model** ve **LCSC/JLCPCB BOM Parametrelerini** ise indirilen LCSC parçasından otomatik eşleştirir.
