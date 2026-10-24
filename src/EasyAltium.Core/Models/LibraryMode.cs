namespace EasyAltium.Core.Models;

/// <summary>
/// Kütüphane çalışma modu (Mod A: SamacSys tarzı ortak kütüphane, Mod B: Profesyonel SQLite .DbLib)
/// </summary>
public enum LibraryMode
{
    /// <summary>
    /// Mod A: Parçaları ortak LCSC_Master.SchLib ve LCSC_Master.PcbLib içine ekler.
    /// </summary>
    MasterLib = 0,

    /// <summary>
    /// Mod B: Kategorize edilmiş SQLite veritabanı (lcsc_components.sqlite) ve .DbLib kullanır.
    /// </summary>
    SqliteDbLib = 1
}

/// <summary>
/// SQLite .DbLib içerisindeki standart bileşen kategorisi tabloları.
/// </summary>
public enum ComponentCategory
{
    Resistors,
    Capacitors,
    Inductors,
    ICs,
    Connectors,
    Discrete,
    Misc
}

/// <summary>
/// Altium DelphiScript (Bridge.pas) tarafından yürütülecek işlem türü.
/// </summary>
public enum BridgeAction
{
    /// <summary>
    /// Kütüphaneyi yükler ve aktif şematik (.SchDoc) açıksa bileşeni yerleştirir.
    /// </summary>
    PlaceOrLoad,

    /// <summary>
    /// Sadece kütüphaneyi (.SchLib, .PcbLib, .DbLib) Altium'a ekler/yeniler.
    /// </summary>
    LoadOnly,

    /// <summary>
    /// npnp başarısız olduğunda EasyEDA ASCII .SchDoc/.PcbDoc üzerinden MakeLibrary çalıştırır.
    /// </summary>
    FallbackMakeLibrary
}

/// <summary>
/// Aktif şematik açıkken bileşenin yerleştirilme yöntemi.
/// </summary>
public enum PlacementMethod
{
    /// <summary>
    /// Bileşeni tüm BOM parametreleriyle oluşturur ve fare imlecinin ucuna bağlar (Sch:MoveSelection).
    /// </summary>
    InteractiveCursor,

    /// <summary>
    /// Bileşeni doğrudan aktif şematik sayfasında belirtilen koordinata (TargetX_Mils, TargetY_Mils) yerleştirir.
    /// </summary>
    DirectToSheet
}
