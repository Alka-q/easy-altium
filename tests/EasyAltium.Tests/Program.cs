using EasyAltium.Core.Bridge;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Engine;
using EasyAltium.Core.Library;
using EasyAltium.Core.Models;

Console.WriteLine("=== EasyAltium Unit & Integration Test Suite ===");

int passed = 0;
int failed = 0;

void AssertTrue(bool condition, string testName)
{
    if (condition)
    {
        Console.WriteLine($"  [PASS] {testName}");
        passed++;
    }
    else
    {
        Console.Error.WriteLine($"  [FAIL] {testName}");
        failed++;
    }
}

// 1. LCSC Regex (^C[1-9]\d{2,}$) Validation Tests
AssertTrue(LcscPartMetadata.IsValidLcscId("C2040"), "Regex: C2040 must be valid");
AssertTrue(LcscPartMetadata.IsValidLcscId("C14663"), "Regex: C14663 must be valid");
AssertTrue(!LcscPartMetadata.IsValidLcscId("C0123"), "Regex: C0123 (leading zero) must be invalid");
AssertTrue(!LcscPartMetadata.IsValidLcscId("C12"), "Regex: C12 (2 digits) must be invalid");
AssertTrue(LcscPartMetadata.TryExtractLcscId("https://www.lcsc.com/product-detail/C2040.html") == "C2040", "Clipboard URL must extract C2040");

// 2. Passive Component Symbol Filter Tests
var config = AppConfig.LoadOrCreate();
bool origUsePassive = config.UsePassiveTemplateSymbol;
config.UsePassiveTemplateSymbol = true;
var passiveFilter = new PassiveSymbolFilter();

var resistorPart = new LcscPartMetadata
{
    LcscId = "C25804",
    Mpn = "0603WAF1002T5E",
    Category = ComponentCategory.Resistors,
    DesignatorPrefix = "R?",
    Value = "10kΩ"
};
var resMatch = passiveFilter.Evaluate(resistorPart, config, config.MasterSchLibPath);
AssertTrue(resMatch.IsPassiveMatched && resMatch.EffectiveLibRef == PassiveSymbolFilter.StandardResistorSymbol,
    "Passive Filter: Resistor must match RES_STD symbol");

var mcuPart = new LcscPartMetadata
{
    LcscId = "C2040",
    Mpn = "RP2040",
    Category = ComponentCategory.ICs,
    DesignatorPrefix = "U?"
};
var mcuMatch = passiveFilter.Evaluate(mcuPart, config, config.MasterSchLibPath);
AssertTrue(!mcuMatch.IsPassiveMatched && mcuMatch.EffectiveLibRef == "RP2040",
    "Passive Filter: IC part must preserve its own symbol");
config.UsePassiveTemplateSymbol = origUsePassive;

// 3. SQLite .DbLib Manager (7-Table Creation + UPSERT) & .DbLib File Generation Test
var sqliteManager = new SqliteDbLibManager(config);
await sqliteManager.InitializeDatabaseAsync();
await sqliteManager.UpsertComponentAsync(
    part: mcuPart,
    libraryRef: "RP2040",
    libraryPath: config.MasterSchLibPath,
    footprintRef: "QFN-56_L7.0-W7.0-P0.40-TL-EP3.2",
    footprintPath: config.MasterPcbLibPath,
    stepModelPath: Path.Combine(config.StepDirectory, "C2040_RP2040.step"));

// Upsert the same part again with updated stock (must not create duplicate rows)
mcuPart.Stock = 52100;
await sqliteManager.UpsertComponentAsync(
    part: mcuPart,
    libraryRef: "RP2040",
    libraryPath: config.MasterSchLibPath,
    footprintRef: "QFN-56_L7.0-W7.0-P0.40-TL-EP3.2",
    footprintPath: config.MasterPcbLibPath,
    stepModelPath: Path.Combine(config.StepDirectory, "C2040_RP2040.step"));

int totalCount = await sqliteManager.GetTotalComponentCountAsync();
AssertTrue(totalCount >= 1, $"SQLite .DbLib UPSERT succeeded (Total records: {totalCount})");

var dbLibGen = new DbLibFileGenerator(config);
string dbLibPath = await dbLibGen.EnsureDbLibFileAsync();
AssertTrue(File.Exists(dbLibPath), $".DbLib configuration file generated: {dbLibPath}");

// 4. IPC Contract (current_part.ini) & Altium CLI Argument Generation Test
var ipcWriter = new IpcContractWriter();
var contract = new IpcPartContract
{
    LcscId = "C2040",
    LibRef = "RP2040",
    FootprintRef = "QFN-56_L7.0-W7.0-P0.40-TL-EP3.2",
    SchLibPath = config.MasterSchLibPath,
    PcbLibPath = config.MasterPcbLibPath,
    DbLibPath = dbLibPath,
    DbTableName = "ICs",
    LogFilePath = config.BridgeLogFilePath,
    Parameters = mcuPart.BuildBomParameterDictionary()
};
await ipcWriter.WriteContractAsync(config.IpcIniFilePath, contract);
var (state, _, _, _) = await ipcWriter.ReadResultSectionAsync(config.IpcIniFilePath);
AssertTrue(state == "PENDING", "current_part.ini [Result] State=PENDING written and verified");

var trigger = new AltiumCliTrigger(config, ipcWriter);
string cliArgs = trigger.BuildScriptingArguments();
AssertTrue(cliArgs.Contains("-RScriptingSystem:RunScript(ProjectName=\"") &&
           cliArgs.Contains("LCSC_Bridge.PrjScr\"|ProcName=\"Bridge.pas>PlaceOrLoadComponent\")"),
    $"Altium Designer CLI command verified: {cliArgs}");

// 5. EasyEDA SVGNODE (outline3D -> c_rotation) & PcbLib 3D Model Rotation Patcher Tests
string sampleSvgNode = """SVGNODE~{"gId":"g1","nodeName":"g","nodeType":1,"attrs":{"c_etype":"outline3D","uuid":"35c6ec85a824426093fa2bfcfef9eb10","c_origin":"420,310","z":"0","c_rotation":"0,0,90"}}""";
bool svgParsed = LcscApiClient.TryParseOutline3DSvgNode(sampleSvgNode, out double rx, out double ry, out double rz, out string? parsedUuid);
AssertTrue(svgParsed && rx == 0 && ry == 0 && rz == 90 && parsedUuid == "35c6ec85a824426093fa2bfcfef9eb10",
    "SVGNODE outline3D c_rotation (0,0,90) and UUID parsed accurately");

AssertTrue(
    PcbLib3DModelPatcher.FormatFixedWidthAngle(0) == "0.000" &&
    PcbLib3DModelPatcher.FormatFixedWidthAngle(90) == "90.00" &&
    PcbLib3DModelPatcher.FormatFixedWidthAngle(180) == "180.0" &&
    PcbLib3DModelPatcher.FormatFixedWidthAngle(-90) == "270.0",
    "PcbLib3DModelPatcher 5-byte fixed-width angle formatting verified");

string tempPcbLib = Path.Combine(Path.GetTempPath(), $"test_patch_{Guid.NewGuid():N}.PcbLib");
try
{
    string mockStream = "HEADER|EMBED=TRUE|ROTX=0.000|ROTY=0.000|ROTZ=0.000|DZ=0|BODY|MODEL.2D.ROTATION=0.000|MODEL.3D.ROTX=0.000|MODEL.3D.ROTY=0.000|MODEL.3D.ROTZ=0.000|MODEL.3D.DZ=0mil|END";
    byte[] origBytes = System.Text.Encoding.Latin1.GetBytes(mockStream);
    File.WriteAllBytes(tempPcbLib, origBytes);

    bool patched = PcbLib3DModelPatcher.TryPatchPcbLib3DRotation(tempPcbLib, 0, 0, 90);
    byte[] newBytes = File.ReadAllBytes(tempPcbLib);
    string patchedText = System.Text.Encoding.Latin1.GetString(newBytes);

    AssertTrue(
        patched &&
        newBytes.Length == origBytes.Length &&
        patchedText.Contains("|ROTZ=90.00|") &&
        patchedText.Contains("|MODEL.3D.ROTZ=90.00|") &&
        patchedText.Contains("|MODEL.2D.ROTATION=0.000|"),
        "PcbLib3DModelPatcher updated ROTZ and MODEL.3D.ROTZ to 90.00 without changing binary file size");
}
finally
{
    if (File.Exists(tempPcbLib))
        File.Delete(tempPcbLib);
}

Console.WriteLine($"=== Results: {passed} Passed, {failed} Failed ===");
return failed == 0 ? 0 : 1;
