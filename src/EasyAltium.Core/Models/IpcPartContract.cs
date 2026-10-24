namespace EasyAltium.Core.Models;

/// <summary>
/// Represents the current_part.ini IPC data contract between the desktop host
/// and the Altium DelphiScript bridge (Bridge.pas).
/// </summary>
public sealed class IpcPartContract
{
    // [Transaction]
    public string RequestId { get; set; } = Guid.NewGuid().ToString("D");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public BridgeAction Action { get; set; } = BridgeAction.PlaceOrLoad;
    public LibraryMode LibraryMode { get; set; } = LibraryMode.SqliteDbLib;
    public PlacementMethod PlacementMethod { get; set; } = PlacementMethod.InteractiveCursor;
    public int TargetXMils { get; set; } = 2000;
    public int TargetYMils { get; set; } = 2000;
    public bool OpenLibraryIfNoSchDoc { get; set; } = true;
    public bool SilentMode { get; set; } = true;
    public string LogFilePath { get; set; } = string.Empty;

    // [LibraryPaths]
    public string SchLibPath { get; set; } = string.Empty;
    public string PcbLibPath { get; set; } = string.Empty;
    public string StepModelPath { get; set; } = string.Empty;
    public string DbLibPath { get; set; } = string.Empty;
    public string DbTableName { get; set; } = nameof(ComponentCategory.Misc);
    public bool UsePassiveTemplateSymbol { get; set; }
    public string PassiveTemplateSchLibPath { get; set; } = string.Empty;
    public string PassiveTemplateLibRef { get; set; } = string.Empty;
    public string FallbackSchDocPath { get; set; } = string.Empty;
    public string FallbackPcbDocPath { get; set; } = string.Empty;

    // [Component]
    public string LcscId { get; set; } = string.Empty;
    public string LibRef { get; set; } = string.Empty;
    public string FootprintRef { get; set; } = string.Empty;
    public string DesignatorPrefix { get; set; } = "U?";
    public string Comment { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int PartCount { get; set; } = 1;
    public string Category { get; set; } = nameof(ComponentCategory.Misc);

    // [Parameters]
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // [Result]
    public string ResultState { get; set; } = "PENDING";
    public string ExecutedAction { get; set; } = "None";
    public string TargetDocument { get; set; } = "None";
    public string ErrorMessage { get; set; } = string.Empty;
    public string CompletedAt { get; set; } = string.Empty;
}
