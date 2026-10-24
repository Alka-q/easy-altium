using System.Text;
using EasyAltium.Core.Configuration;

namespace EasyAltium.Core.Bridge;

/// <summary>
/// Altium Designer üst araç çubuğu (Top Toolbar) ve menü kurulum sonucunu temsil eder.
/// </summary>
public sealed record ToolbarInstallResult(
    bool IsSuccess,
    IReadOnlyList<string> UpdatedProfileDirectories,
    string ToolbarIconPath,
    string Message
);

/// <summary>
/// Sürümden bağımsız olarak (%APPDATA%\Altium\Altium Designer {*}) tüm aktif Altium Designer
/// kullanıcı profillerindeki DXP.RCS yapılandırmasına EasyAltium üst araç çubuğu (Toolbar)
/// çip ikonunu ve menü kısayollarını kurar.
/// </summary>
public sealed class AltiumToolbarInstaller
{
    private readonly AppConfig _config;

    public AltiumToolbarInstaller(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string ToolbarAssetsDirectory => Path.Combine(_config.WorkspaceRoot, "altium-bridge", "assets");
    public string ToolbarPngIconPath => Path.Combine(ToolbarAssetsDirectory, "EasyAltium.png");
    public string ToolbarHiDpiPngIconPath => Path.Combine(ToolbarAssetsDirectory, "EasyAltium_x2.png");
    public string ToolbarSmallPngIconPath => Path.Combine(ToolbarAssetsDirectory, "EasyAltium_16.png");
    public string AppIcoPath => Path.Combine(ToolbarAssetsDirectory, "EasyAltium.ico");
    public string SourceJfifPath => Path.Combine(_config.WorkspaceRoot, "doc", "EasyAltium-componentimge.jfif");

    /// <summary>
    /// Sistemdeki aktif Altium Designer AppData profil klasörlerini (sürümden bağımsız) bulur.
    /// </summary>
    public static IReadOnlyList<string> DiscoverAltiumProfileDirectories()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            return [];

        string altiumRoamingRoot = Path.Combine(appData, "Altium");
        if (!Directory.Exists(altiumRoamingRoot))
            return [];

        return Directory.GetDirectories(altiumRoamingRoot, "Altium Designer {*}")
            .Where(dir =>
            {
                string name = Path.GetFileName(dir);
                return name.EndsWith('}') && !name.Contains(".backup", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Altium Designer kapanırken belleğindeki eski DXP.RCS dosyasını diske yazarak
    /// EasyAltium toolbar kayıtlarını ezdiyse bunu tespit eder ve anında onarır.
    /// </summary>
    public bool EnsureToolbarInstalledIfNeeded()
    {
        var profiles = DiscoverAltiumProfileDirectories();
        if (profiles.Count == 0)
            return false;

        string expectedProjectPath = NormalizePathDriveLetter(Path.GetFullPath(_config.BridgeScriptProjectPath));
        bool anyRepaired = false;

        foreach (string profileDir in profiles)
        {
            string rcsPath = Path.Combine(profileDir, "DXP.RCS");
            if (!File.Exists(rcsPath))
            {
                anyRepaired = true;
                break;
            }

            try
            {
                string content = File.ReadAllText(rcsPath, Encoding.UTF8);
                if (content.Contains("Hint=", StringComparison.OrdinalIgnoreCase) ||
                    !content.Contains("_UserEasyAltiumDXP", StringComparison.OrdinalIgnoreCase) ||
                    !content.Contains(expectedProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    anyRepaired = true;
                    break;
                }
            }
            catch
            {
                // Dosya o an Altium tarafından yazılıyorsa bir sonraki döngüde kontrol edilir
            }
        }

        if (anyRepaired)
        {
            try
            {
                InstallToolbarAndMenus();
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Tüm Altium Designer profillerindeki DXP.RCS dosyalarına üst panel barı (Active Bar + Toolbar)
    /// ve menü butonlarını kurar.
    /// </summary>
    public ToolbarInstallResult InstallToolbarAndMenus()
    {
        var profiles = DiscoverAltiumProfileDirectories();
        if (profiles.Count == 0)
        {
            return new ToolbarInstallResult(
                IsSuccess: false,
                UpdatedProfileDirectories: [],
                ToolbarIconPath: ToolbarPngIconPath,
                Message: "No Altium Designer user profile found under %APPDATA%\\Altium.");
        }

        string projectPath = NormalizePathDriveLetter(Path.GetFullPath(_config.BridgeScriptProjectPath));
        string iconPath = NormalizePathDriveLetter(Path.GetFullPath(ToolbarPngIconPath));

        var updatedProfiles = new List<string>();

        foreach (string profileDir in profiles)
        {
            string rcsPath = Path.Combine(profileDir, "DXP.RCS");
            if (File.Exists(rcsPath))
            {
                string backupPath = Path.Combine(profileDir, "DXP.RCS.easyaltium.bak");
                if (!File.Exists(backupPath))
                    File.Copy(rcsPath, backupPath, overwrite: false);
            }

            string existingContent = File.Exists(rcsPath)
                ? File.ReadAllText(rcsPath, Encoding.UTF8)
                : string.Empty;

            string updatedContent = BuildUpdatedDxpRcsContent(existingContent, projectPath, iconPath);
            File.WriteAllText(rcsPath, updatedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            updatedProfiles.Add(profileDir);
        }

        return new ToolbarInstallResult(
            IsSuccess: true,
            UpdatedProfileDirectories: updatedProfiles,
            ToolbarIconPath: iconPath,
            Message: $"EasyAltium top toolbar (Active Bar & Toolbar) icon installed across {updatedProfiles.Count} Altium profile(s).");
    }

    public static string BuildUpdatedDxpRcsContent(
        string existingContent,
        string bridgeProjectFullPath,
        string iconFullPath)
    {
        // Mevcut DXP.RCS içerisindeki eski EasyAltium bloklarını temizle, diğer kullanıcı özelleştirmelerini koru
        var preservedLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(existingContent))
        {
            string[] lines = existingContent.Replace("\r\n", "\n").Split('\n');
            bool skippingEasyAltiumInsertion = false;

            foreach (string rawLine in lines)
            {
                string trimmed = rawLine.Trim();
                if (trimmed.StartsWith("PL EasyAltium", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (trimmed.StartsWith("Insertion _UserEasyAltium", StringComparison.OrdinalIgnoreCase))
                {
                    skippingEasyAltiumInsertion = true;
                    continue;
                }

                if (skippingEasyAltiumInsertion)
                {
                    if (string.Equals(trimmed, "End", StringComparison.OrdinalIgnoreCase))
                        skippingEasyAltiumInsertion = false;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(rawLine))
                    preservedLines.Add(rawLine);
            }
        }

        var sb = new StringBuilder();

        if (!preservedLines.Any(l => l.Contains("Tree PCBCustom", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("Tree PCBCustom Caption='[Custom]' TopLevel='True'  End");

        if (!preservedLines.Any(l => l.Contains("Tree SchCustom", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine("Tree SchCustom Caption='[Custom]' TopLevel='True'  End");

        foreach (string line in preservedLines)
            sb.AppendLine(line);

        string cleanProjectPath = NormalizePathDriveLetter(bridgeProjectFullPath);
        string cleanIconPath = NormalizePathDriveLetter(iconFullPath);

        // 1. Komut Tanımları (PL EasyAltium & PL EasyAltiumPlace)
        // ÖNEMLİ: X2.EXE içerisindeki TProcessLauncher .RCS ayrıştırıcısı yalnızca
        // Command, Params, Caption, Image, Description, DefaultChecked özniteliklerini kabul eder.
        // 'Hint' özniteliği veya ASCII dışı karakterler tüm DXP.RCS dosyasının reddedilmesine yol açar.
        sb.AppendLine(
            $"PL EasyAltium Command='ScriptingSystem:RunScript' " +
            $"Params='ProjectName={cleanProjectPath}|ProcName=Bridge.pas>LaunchOrShowEasyAltium' " +
            $"Caption='EasyAltium - LCSC' " +
            $"Image='{cleanIconPath}' DefaultChecked=0  End");

        sb.AppendLine(
            $"PL EasyAltiumPlace Command='ScriptingSystem:RunScript' " +
            $"Params='ProjectName={cleanProjectPath}|ProcName=Bridge.pas>PlaceOrLoadComponent' " +
            $"Caption='EasyAltium - Place Last Part' " +
            $"Image='{cleanIconPath}' DefaultChecked=0  End");

        // 2. Üst Sol Başlık Çubuğu (Quick Access Bar: Save / Open / Undo / Redo yanına çip ikonu)
        sb.AppendLine("Insertion _UserEasyAltiumQA TargetID='DXPQuickAccessBar' InsertType='After' RefID0='DXPQuickAccessBar090' ");
        sb.AppendLine("    Link _UserEasyAltiumQALink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        // 3. Üst Sağ Sistem İkon Çubuğu (DXPToolbar: Home ve Ayarlar çark ikonu yanına çip ikonu)
        sb.AppendLine("Insertion _UserEasyAltiumDXP TargetID='DXPToolbar' InsertType='Before' RefID0='DXPToolbar030' ");
        sb.AppendLine("    Link _UserEasyAltiumDXPLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        // 4. Modern Altium Üst Orta Active Bar (TopBarSchematic & TopBarPCB: Place Part / Components yanına çip ikonu)
        sb.AppendLine("Insertion _UserEasyAltiumActiveSch TargetID='TopBarSchematic' InsertType='After' RefID0='TopBarSchematic010' ");
        sb.AppendLine("    Link _UserEasyAltiumActiveSchLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumActivePcb TargetID='TopBarPCB' InsertType='After' RefID0='TopBarPCB010' ");
        sb.AppendLine("    Link _UserEasyAltiumActivePcbLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        // 5. Şematik ve PCB Üst Menü Barları (Help yanına) + Tools ve File Menüleri (İkonlu Menü Öğesi)
        sb.AppendLine("Insertion _UserEasyAltium1 TargetID='MNSchematicMenu' InsertType='After' RefID0='MNSchematic_Help10' ");
        sb.AppendLine("    Link _UserEasyAltium2 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium3 TargetID='MNSchematic_Tools10' RefID0='SchDoc_Tools' ");
        sb.AppendLine("    Link _UserEasyAltium4 PLID='EasyAltium'      End");
        sb.AppendLine("    Link _UserEasyAltium4B PLID='EasyAltiumPlace'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumFileSch TargetID='MNSchematic_File' RefID0='SchDoc_File' ");
        sb.AppendLine("    Link _UserEasyAltiumFileSchLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium5 TargetID='MNPCBMenu' InsertType='After' RefID0='MNPCB_Help10' ");
        sb.AppendLine("    Link _UserEasyAltium6 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium7 TargetID='MNPCB_Tools10' RefID0='PcbDoc_Tools' ");
        sb.AppendLine("    Link _UserEasyAltium8 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumFilePcb TargetID='MNPCB_File' RefID0='PcbDoc_File' ");
        sb.AppendLine("    Link _UserEasyAltiumFilePcbLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumFileNoDoc TargetID='MNNoDocument_File' RefID0='NoDoc' ");
        sb.AppendLine("    Link _UserEasyAltiumFileNoDocLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        // 6. Klasik Araç Çubukları (Açık ise)
        sb.AppendLine("Insertion _UserEasyAltiumTB1 TargetID='TLWiringTools' InsertType='After' RefID0='TBSchematic_ToolsWiring090' ");
        sb.AppendLine("    Link _UserEasyAltiumTB2 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumTB3 TargetID='TLSchematicTools' InsertType='After' RefID0='TBSchematic_ToolsStandard280' ");
        sb.AppendLine("    Link _UserEasyAltiumTB4 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumTB5 TargetID='TLPCBToolbar' InsertType='After' RefID0='TBPCB_StandardToolbar780' ");
        sb.AppendLine("    Link _UserEasyAltiumTB6 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        return sb.ToString();
    }

    private static string NormalizePathDriveLetter(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && path.Length >= 2 && path[1] == ':' && char.IsLower(path[0]))
        {
            return char.ToUpperInvariant(path[0]) + path[1..];
        }
        return path;
    }
}
