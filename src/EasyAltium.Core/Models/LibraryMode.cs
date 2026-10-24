namespace EasyAltium.Core.Models;

/// <summary>
/// Library management mode (Mode A: SamacSys-style consolidated library, Mode B: Categorized SQLite .DbLib).
/// </summary>
public enum LibraryMode
{
    /// <summary>
    /// Mode A: Appends components into consolidated LCSC_Master.SchLib and LCSC_Master.PcbLib files.
    /// </summary>
    MasterLib = 0,

    /// <summary>
    /// Mode B: Uses a categorized SQLite database (lcsc_components.sqlite) and an Altium .DbLib file.
    /// </summary>
    SqliteDbLib = 1
}

/// <summary>
/// Standard component category tables inside the SQLite .DbLib database.
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
/// Action type executed by the Altium DelphiScript bridge (Bridge.pas).
/// </summary>
public enum BridgeAction
{
    /// <summary>
    /// Installs/refreshes libraries and places the component if an active schematic (.SchDoc) is open.
    /// </summary>
    PlaceOrLoad,

    /// <summary>
    /// Only installs/refreshes the library (.SchLib, .PcbLib, .DbLib) in Altium Designer.
    /// </summary>
    LoadOnly,

    /// <summary>
    /// Runs MakeLibrary from EasyEDA ASCII .SchDoc/.PcbDoc when npnp requires fallback.
    /// </summary>
    FallbackMakeLibrary
}

/// <summary>
/// Component placement method when an active schematic document is open.
/// </summary>
public enum PlacementMethod
{
    /// <summary>
    /// Instantiates the component with all BOM parameters and attaches it to the user cursor (Sch:MoveSelectedObjects).
    /// </summary>
    InteractiveCursor,

    /// <summary>
    /// Places the component directly at the target coordinates (TargetX_Mils, TargetY_Mils) on the active sheet.
    /// </summary>
    DirectToSheet
}
