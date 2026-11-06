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

// 6. Engineering Unit Normalization (Ohm, +/-, u, degC) & Mojibake Prevention Tests
AssertTrue(
    LcscPartMetadata.NormalizeEngineeringUnitsForAltium("100kΩ ±1% -55℃~+155℃") == "100kOhm +/-1% -55degC~+155degC" &&
    LcscPartMetadata.NormalizeEngineeringUnitsForAltium("100kÎ© Â±1% 4.7ÂµF") == "100kOhm +/-1% 4.7uF" &&
    LcscPartMetadata.NormalizeEngineeringUnitsForAltium("100k&#937; &#8451;") == "100kOhm degC",
    "NormalizeEngineeringUnitsForAltium converts Ω/℃/±/µ and mojibake/HTML entities to clean ANSI");

// 7. Category Classification, DesignatorPrefix & Comment/Value Disambiguation Tests
var mosfetMeta = new LcscPartMetadata
{
    LcscId = "C15127",
    Mpn = "AO3401A",
    RawCategoryName = "Transistors/Thyristors / Single FETs, MOSFETs",
    Description = "30V 4A Surface Mount SOT-23",
    DesignatorPrefix = "Q?"
};
mosfetMeta.ExtraParameters["Output_Capacitance_Coss"] = "80pF";
mosfetMeta.ExtraParameters["RDS_on"] = "47mOhm@10V";
AssertTrue(
    LcscApiClient.ClassifyCategory(mosfetMeta) == ComponentCategory.Discrete &&
    LcscApiClient.ResolveCanonicalDesignatorPrefix(mosfetMeta) == "Q?",
    "MOSFET (AO3401A) classified as Discrete with Q? designator");

var connMeta = new LcscPartMetadata
{
    LcscId = "C157991",
    Mpn = "B5B-XH-A(LF)(SN)",
    RawCategoryName = "Connectors / Headers, Male Pins",
    Description = "Connector Header 5 position 2.5mm Pitch",
    DesignatorPrefix = "CN?"
};
AssertTrue(
    LcscApiClient.ClassifyCategory(connMeta) == ComponentCategory.Connectors &&
    LcscApiClient.ResolveCanonicalDesignatorPrefix(connMeta) == "J?",
    "Connector with CN? prefix classified as Connectors (not Capacitors) with J? designator");

var chokeMeta = new LcscPartMetadata
{
    LcscId = "C2932174",
    Mpn = "PSCIAQ4532-101Z",
    RawCategoryName = "Filters / Common Mode Chokes",
    Description = "100uH@100kHz 2Line Common Mode Choke",
    DesignatorPrefix = "U?"
};
AssertTrue(
    LcscApiClient.ClassifyCategory(chokeMeta) == ComponentCategory.Inductors &&
    LcscApiClient.ResolveCanonicalDesignatorPrefix(chokeMeta) == "L?",
    "Common Mode Choke with U? prefix classified as Inductors with L? designator");

var fuseMeta = new LcscPartMetadata
{
    LcscId = "C70068",
    Mpn = "nSMD025",
    RawCategoryName = "Circuit Protection / PTC Resettable Fuses",
    Description = "Polymeric PTC Resettable Fuse 16V 250mA",
    DesignatorPrefix = "F?"
};
AssertTrue(
    LcscApiClient.ClassifyCategory(fuseMeta) == ComponentCategory.Misc &&
    LcscApiClient.ResolveCanonicalDesignatorPrefix(fuseMeta) == "F?",
    "PTC Resettable Fuse classified as Misc (not ICs) with F? designator");

var buckIcMeta = new LcscPartMetadata
{
    LcscId = "C311983",
    Mpn = "TPS54302DDCR",
    RawCategoryName = "Power Management (PMIC) / Voltage Regulators - DC DC Switching Regulators",
    Description = "Buck Switching Regulator IC Adjustable 1 Output 3A TSOT-23-6",
    DesignatorPrefix = "U?"
};
AssertTrue(
    LcscApiClient.ClassifyCategory(buckIcMeta) == ComponentCategory.ICs &&
    LcscApiClient.ResolveCanonicalDesignatorPrefix(buckIcMeta) == "U?",
    "Buck Switching Regulator IC (TPS54302DDCR) preserves U? designator (not SW?)");

Console.WriteLine($"=== Results: {passed} Passed, {failed} Failed ===");
return failed == 0 ? 0 : 1;
