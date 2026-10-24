using System.Text.Json;
using System.Text.Json.Serialization;
using EasyAltium.Core.Models;

namespace EasyAltium.Core.Configuration;

/// <summary>
/// EasyAltium istemci ve çekirdek motor yapılandırma ayarları.
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Çalışma alanı kök dizini.
    /// </summary>
    public string WorkspaceRoot { get; set; } = ResolveDefaultWorkspaceRoot();

    /// <summary>
    /// Altium Designer 26.10.1 yürütülebilir dosya yolu.
    /// </summary>
    public string AltiumExecutablePath { get; set; } = @"C:\Program Files\Altium\AD26\X2.EXE";

    /// <summary>
    /// LCSC_Bridge.PrjScr tam dosya yolu.
    /// </summary>
    public string BridgeScriptProjectPath { get; set; } = string.Empty;

    /// <summary>
    /// DelphiScript içerisinde çağrılacak prosedür adı.
    /// </summary>
    public string BridgeProcedureName { get; set; } = "Bridge.pas>PlaceOrLoadComponent";

    /// <summary>
    /// current_part.ini IPC dosyasının tam yolu.
    /// </summary>
    public string IpcIniFilePath { get; set; } = string.Empty;

    /// <summary>
    /// bridge_status.log dosyasının tam yolu.
    /// </summary>
    public string BridgeLogFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Kütüphane çıktı kök dizini (./Library).
    /// </summary>
    public string LibraryRootDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Rust tabanlı npnp CLI aracının yürütülebilir dosya yolu veya komut adı.
    /// </summary>
    public string NpnpExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// npnp CLI argüman şablonu ({LCSC_ID} ve {OUTPUT_DIR} yer tutucularını destekler).
    /// </summary>
    public string NpnpArgumentsTemplate { get; set; } = "altium export {LCSC_ID} --full --output \"{OUTPUT_DIR}\"";

    /// <summary>
    /// Kütüphane yönetim modu: Mod A (MasterLib) veya Mod B (SqliteDbLib).
    /// </summary>
    public LibraryMode LibraryMode { get; set; } = LibraryMode.SqliteDbLib;

    /// <summary>
    /// Pasif elemanlarda (R, C, L) yerel standart sembol şablonunu kullan.
    /// </summary>
    public bool UsePassiveTemplateSymbol { get; set; } = false;

    /// <summary>
    /// Yerel standart pasif sembol şablon kütüphanesi (.SchLib) yolu.
    /// </summary>
    public string PassiveTemplateSchLibPath { get; set; } = string.Empty;

    /// <summary>
    /// Şematik yerleştirme yöntemi (InteractiveCursor veya DirectToSheet).
    /// </summary>
    public PlacementMethod PlacementMethod { get; set; } = PlacementMethod.InteractiveCursor;

    /// <summary>
    /// Aktif .SchDoc açık değilse ilgili .SchLib dosyasını Altium'da aç.
    /// </summary>
    public bool OpenLibraryIfNoSchDoc { get; set; } = true;

    /// <summary>
    /// Windows panosunu (Clipboard) otomatik dinle.
    /// </summary>
    public bool AutoWatchClipboard { get; set; } = true;

    /// <summary>
    /// Mini arama/durum penceresini her zaman üstte (Always-on-Top) tut.
    /// </summary>
    public bool AlwaysOnTopMiniWindow { get; set; } = true;

    /// <summary>
    /// npnp CLI zaman aşımı süresi (saniye).
    /// </summary>
    public int NpnpTimeoutSeconds { get; set; } = 45;

    // Hesaplanan Alt Dizin Yolları
    [JsonIgnore]
    public string SchLibDirectory => Path.Combine(LibraryRootDirectory, "SchLib");

    [JsonIgnore]
    public string PcbLibDirectory => Path.Combine(LibraryRootDirectory, "PcbLib");

    [JsonIgnore]
    public string StepDirectory => Path.Combine(LibraryRootDirectory, "STEP");

    [JsonIgnore]
    public string DbLibDirectory => Path.Combine(LibraryRootDirectory, "DbLib");

    [JsonIgnore]
    public string FallbackDirectory => Path.Combine(LibraryRootDirectory, "Fallback");

    [JsonIgnore]
    public string MasterSchLibPath => Path.Combine(SchLibDirectory, "LCSC_Master.SchLib");

    [JsonIgnore]
    public string MasterPcbLibPath => Path.Combine(PcbLibDirectory, "LCSC_Master.PcbLib");

    [JsonIgnore]
    public string SqliteDatabasePath => Path.Combine(DbLibDirectory, "lcsc_components.sqlite");

    [JsonIgnore]
    public string DbLibFilePath => Path.Combine(DbLibDirectory, "EasyAltium_LCSC.DbLib");

    public AppConfig()
    {
        InitializeDefaultPaths();
    }

    public void InitializeDefaultPaths()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceRoot))
            WorkspaceRoot = ResolveDefaultWorkspaceRoot();

        if (string.IsNullOrWhiteSpace(BridgeScriptProjectPath))
            BridgeScriptProjectPath = Path.Combine(WorkspaceRoot, "altium-bridge", "LCSC_Bridge.PrjScr");

        if (string.IsNullOrWhiteSpace(IpcIniFilePath))
            IpcIniFilePath = Path.Combine(WorkspaceRoot, "ipc", "current_part.ini");

        if (string.IsNullOrWhiteSpace(BridgeLogFilePath))
            BridgeLogFilePath = Path.Combine(WorkspaceRoot, "ipc", "bridge_status.log");

        if (string.IsNullOrWhiteSpace(LibraryRootDirectory))
            LibraryRootDirectory = Path.Combine(WorkspaceRoot, "Library");

        if (string.IsNullOrWhiteSpace(PassiveTemplateSchLibPath))
            PassiveTemplateSchLibPath = Path.Combine(WorkspaceRoot, "altium-bridge", "Templates", "Standard_Passives.SchLib");

        if (string.IsNullOrWhiteSpace(NpnpExecutablePath))
        {
            string localNpnp = Path.Combine(WorkspaceRoot, "tools", "npnp", "npnp.exe");
            NpnpExecutablePath = File.Exists(localNpnp) ? localNpnp : "npnp";
        }

        if (!File.Exists(AltiumExecutablePath))
        {
            string? discovered = TryDiscoverAltiumExecutable();
            if (!string.IsNullOrWhiteSpace(discovered))
                AltiumExecutablePath = discovered;
        }
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(SchLibDirectory);
        Directory.CreateDirectory(PcbLibDirectory);
        Directory.CreateDirectory(StepDirectory);
        Directory.CreateDirectory(DbLibDirectory);
        Directory.CreateDirectory(FallbackDirectory);

        string? ipcDir = Path.GetDirectoryName(IpcIniFilePath);
        if (!string.IsNullOrWhiteSpace(ipcDir))
            Directory.CreateDirectory(ipcDir);
    }

    public static AppConfig LoadOrCreate(string? configFilePath = null)
    {
        string path = configFilePath ?? GetDefaultConfigFilePath();
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.InitializeDefaultPaths();
                    loaded.EnsureDirectoriesExist();
                    return loaded;
                }
            }
            catch
            {
                // Bozuk yapılandırma durumunda varsayılanlara dön
            }
        }

        var config = new AppConfig();
        config.InitializeDefaultPaths();
        config.EnsureDirectoriesExist();
        config.Save(path);
        return config;
    }

    public void Save(string? configFilePath = null)
    {
        string path = configFilePath ?? GetDefaultConfigFilePath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static string GetDefaultConfigFilePath()
    {
        string root = ResolveDefaultWorkspaceRoot();
        return Path.Combine(root, "ipc", "appsettings.json");
    }

    private static string ResolveDefaultWorkspaceRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "altium-bridge")) ||
                File.Exists(Path.Combine(dir.FullName, "EasyAltium.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        const string fallbackWorkspace = @"D:\wb\antigravity\easyaltium-q";
        if (Directory.Exists(fallbackWorkspace))
            return fallbackWorkspace;

        return Directory.GetCurrentDirectory();
    }

    private static string? TryDiscoverAltiumExecutable()
    {
        string[] candidatePaths =
        [
            @"C:\Program Files\Altium\AD26\X2.EXE",
            @"C:\Program Files\Altium\AD25\X2.EXE",
            @"C:\Program Files\Altium\AD24\X2.EXE"
        ];

        foreach (string candidate in candidatePaths)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        const string altiumBase = @"C:\Program Files\Altium";
        if (Directory.Exists(altiumBase))
        {
            var adDirs = Directory.GetDirectories(altiumBase, "AD*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

            foreach (string adDir in adDirs)
            {
                string exe = Path.Combine(adDir, "X2.EXE");
                if (File.Exists(exe))
                    return exe;
            }
        }

        return null;
    }
}
