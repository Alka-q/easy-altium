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

        string expectedProjectPath = Path.GetFullPath(_config.BridgeScriptProjectPath);
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
                if (!content.Contains("_UserEasyAltiumActiveSch", StringComparison.OrdinalIgnoreCase) ||
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
                Message: "%APPDATA%\\Altium altında Altium Designer kullanıcı profili bulunamadı.");
        }

        string projectPath = Path.GetFullPath(_config.BridgeScriptProjectPath);
        string iconPath = Path.GetFullPath(ToolbarPngIconPath);

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
            Message: $"EasyAltium üst araç çubuğu (Active Bar & Toolbar) ikonu {updatedProfiles.Count} Altium profiline kuruldu.");
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

        // 1. Komut Tanımları (PL EasyAltium & PL EasyAltiumPlace)
        sb.AppendLine(
            $"PL EasyAltium Command='ScriptingSystem:RunScript' " +
            $"Params='ProjectName={bridgeProjectFullPath}|ProcName=Bridge.pas>LaunchOrShowEasyAltium' " +
            $"Caption='EasyAltium - LCSC' " +
            $"Description='EasyAltium LCSC & JLCPCB Library Loader Penceresini Aç' " +
            $"Hint='EasyAltium LCSC & JLCPCB Library Loader' " +
            $"Image='{iconFullPath}' DefaultChecked=0  End");

        sb.AppendLine(
            $"PL EasyAltiumPlace Command='ScriptingSystem:RunScript' " +
            $"Params='ProjectName={bridgeProjectFullPath}|ProcName=Bridge.pas>PlaceOrLoadComponent' " +
            $"Caption='EasyAltium - Son Parçayı Yerleştir' " +
            $"Description='Son indirilen LCSC bileşenini doğrudan imlece al' " +
            $"Hint='EasyAltium - Son Parçayı Yerleştir' " +
            $"Image='{iconFullPath}' DefaultChecked=0  End");

        // 2. Modern Altium Üst Active Bar (TopBarSchematic, TopBarPCB, TopBarSchLib) — Doğrudan Çip İkonu Gösterir
        sb.AppendLine("Insertion _UserEasyAltiumActiveSch TargetID='TopBarSchematic' InsertType='After' RefID0='TopBarSchematic010'");
        sb.AppendLine("    Link _UserEasyAltiumActiveSchLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumActivePcb TargetID='TopBarPCB' InsertType='After' RefID0='TopBarPCB010'");
        sb.AppendLine("    Link _UserEasyAltiumActivePcbLink PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumActiveSchLib TargetID='TopBarSchLib' InsertType='After' RefID0='TopBarSchLib180'");
        sb.AppendLine("    Link _UserEasyAltiumActiveSchLibLink PLID='EasyAltium'   End");
        sb.AppendLine("End");

        // 3. Klasik Şematik ve PCB Üst Araç Çubukları (Top Toolbars: Wiring, Schematic Standard, PCB Standard)
        sb.AppendLine("Insertion _UserEasyAltiumTB1 TargetID='TLWiringTools' InsertType='After' RefID0='TBSchematic_ToolsWiring090'");
        sb.AppendLine("    Link _UserEasyAltiumTB2 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumTB3 TargetID='TLSchematicTools' InsertType='After' RefID0='TBSchematic_ToolsStandard280'");
        sb.AppendLine("    Link _UserEasyAltiumTB4 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltiumTB5 TargetID='TLPCBToolbar' InsertType='After' RefID0='TBPCB_StandardToolbar780'");
        sb.AppendLine("    Link _UserEasyAltiumTB6 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        // 4. Üst Menü Barları (Şematik, PCB, Home/Engagement, Boş Pencere) + Tools Menüsü
        sb.AppendLine("Insertion _UserEasyAltium1 TargetID='MNSchematicMenu' InsertType='After' RefID0='MNSchematic_Help10'");
        sb.AppendLine("    Link _UserEasyAltium2 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium3 TargetID='MNSchematic_Tools10' RefID0='SchDoc_Tools'");
        sb.AppendLine("    Link _UserEasyAltium4 PLID='EasyAltium'      End");
        sb.AppendLine("    Link _UserEasyAltium4B PLID='EasyAltiumPlace' End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium5 TargetID='MNPCBMenu' InsertType='After' RefID0='MNPCB_Help10'");
        sb.AppendLine("    Link _UserEasyAltium6 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium7 TargetID='MNPCB_Tools10' RefID0='PcbDoc_Tools'");
        sb.AppendLine("    Link _UserEasyAltium8 PLID='EasyAltium'      End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium9 TargetID='MNEngagementMenu' InsertType='After' RefID0='MNEngagementHelpMenu'");
        sb.AppendLine("    Link _UserEasyAltium10 PLID='EasyAltium'     End");
        sb.AppendLine("End");

        sb.AppendLine("Insertion _UserEasyAltium11 TargetID='MNNoDocument' InsertType='After' RefID0='MNNoDocument_Help'");
        sb.AppendLine("    Link _UserEasyAltium12 PLID='EasyAltium'     End");
        sb.AppendLine("End");

        return sb.ToString();
    }
}
