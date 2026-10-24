using System.Globalization;
using System.Text;
using EasyAltium.Core.Models;

namespace EasyAltium.Core.Bridge;

/// <summary>
/// İstemci ile Altium DelphiScript (Bridge.pas) arasındaki current_part.ini
/// dosyasını atomik olarak yazar ve [Result] bölümünü okur.
/// </summary>
public sealed class IpcContractWriter
{
    public async Task WriteContractAsync(
        string iniFilePath,
        IpcPartContract contract,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(iniFilePath);
        ArgumentNullException.ThrowIfNull(contract);

        string content = SerializeToIni(contract);
        await WriteAtomicallyAsync(iniFilePath, content, cancellationToken).ConfigureAwait(false);

        // %LOCALAPPDATA%\EasyAltium\ipc\current_part.ini yedek kopyasını da güncel tut
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                string mirrorPath = Path.Combine(localAppData, "EasyAltium", "ipc", "current_part.ini");
                if (!string.Equals(Path.GetFullPath(iniFilePath), Path.GetFullPath(mirrorPath), StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAtomicallyAsync(mirrorPath, content, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Yedek dizin yazılamazsa ana akışı kesme
        }
    }

    public static string SerializeToIni(IpcPartContract contract)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; ============================================================================");
        sb.AppendLine("; EasyAltium (LCSC Loader) - Host <-> Altium Designer IPC Data Contract");
        sb.AppendLine("; ============================================================================");
        sb.AppendLine();

        sb.AppendLine("[Transaction]");
        sb.AppendLine($"RequestId={contract.RequestId}");
        sb.AppendLine($"TimestampUtc={contract.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Action={contract.Action}");
        sb.AppendLine($"LibraryMode={contract.LibraryMode}");
        sb.AppendLine($"PlacementMethod={contract.PlacementMethod}");
        sb.AppendLine($"TargetX_Mils={contract.TargetXMils}");
        sb.AppendLine($"TargetY_Mils={contract.TargetYMils}");
        sb.AppendLine($"OpenLibraryIfNoSchDoc={(contract.OpenLibraryIfNoSchDoc ? 1 : 0)}");
        sb.AppendLine($"SilentMode={(contract.SilentMode ? 1 : 0)}");
        sb.AppendLine($"LogFilePath={SanitizeIniLine(contract.LogFilePath)}");
        sb.AppendLine();

        sb.AppendLine("[LibraryPaths]");
        sb.AppendLine($"SchLibPath={SanitizeIniLine(contract.SchLibPath)}");
        sb.AppendLine($"PcbLibPath={SanitizeIniLine(contract.PcbLibPath)}");
        sb.AppendLine($"StepModelPath={SanitizeIniLine(contract.StepModelPath)}");
        sb.AppendLine($"DbLibPath={SanitizeIniLine(contract.DbLibPath)}");
        sb.AppendLine($"DbTableName={SanitizeIniLine(contract.DbTableName)}");
        sb.AppendLine($"UsePassiveTemplateSymbol={(contract.UsePassiveTemplateSymbol ? 1 : 0)}");
        sb.AppendLine($"PassiveTemplateSchLibPath={SanitizeIniLine(contract.PassiveTemplateSchLibPath)}");
        sb.AppendLine($"PassiveTemplateLibRef={SanitizeIniLine(contract.PassiveTemplateLibRef)}");
        sb.AppendLine($"FallbackSchDocPath={SanitizeIniLine(contract.FallbackSchDocPath)}");
        sb.AppendLine($"FallbackPcbDocPath={SanitizeIniLine(contract.FallbackPcbDocPath)}");
        sb.AppendLine();

        sb.AppendLine("[Component]");
        sb.AppendLine($"LcscId={SanitizeIniLine(contract.LcscId)}");
        sb.AppendLine($"LibRef={SanitizeIniLine(contract.LibRef)}");
        sb.AppendLine($"FootprintRef={SanitizeIniLine(contract.FootprintRef)}");
        sb.AppendLine($"DesignatorPrefix={SanitizeIniLine(contract.DesignatorPrefix)}");
        sb.AppendLine($"Comment={SanitizeIniLine(contract.Comment)}");
        sb.AppendLine($"Description={SanitizeIniLine(contract.Description)}");
        sb.AppendLine($"PartCount={Math.Max(1, contract.PartCount)}");
        sb.AppendLine($"Category={SanitizeIniLine(contract.Category)}");
        sb.AppendLine();

        sb.AppendLine("[Parameters]");
        foreach (var kvp in contract.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key))
                sb.AppendLine($"{SanitizeIniKey(kvp.Key)}={SanitizeIniLine(kvp.Value)}");
        }
        sb.AppendLine();

        sb.AppendLine("[Result]");
        sb.AppendLine($"State={SanitizeIniLine(contract.ResultState)}");
        sb.AppendLine($"ExecutedAction={SanitizeIniLine(contract.ExecutedAction)}");
        sb.AppendLine($"TargetDocument={SanitizeIniLine(contract.TargetDocument)}");
        sb.AppendLine($"ErrorMessage={SanitizeIniLine(contract.ErrorMessage)}");
        sb.AppendLine($"CompletedAt={SanitizeIniLine(contract.CompletedAt)}");

        return sb.ToString();
    }

    public async Task<(string State, string ExecutedAction, string TargetDocument, string ErrorMessage)> ReadResultSectionAsync(
        string iniFilePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(iniFilePath))
            return ("UNKNOWN", "None", "None", "current_part.ini bulunamadı.");

        string[] lines = await File.ReadAllLinesAsync(iniFilePath, cancellationToken).ConfigureAwait(false);
        bool inResult = false;
        string state = "PENDING";
        string action = "None";
        string target = "None";
        string error = string.Empty;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inResult = string.Equals(line, "[Result]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inResult)
                continue;

            int eqIdx = line.IndexOf('=');
            if (eqIdx <= 0)
                continue;

            string key = line[..eqIdx].Trim();
            string val = line[(eqIdx + 1)..].Trim();

            if (string.Equals(key, "State", StringComparison.OrdinalIgnoreCase))
                state = val;
            else if (string.Equals(key, "ExecutedAction", StringComparison.OrdinalIgnoreCase))
                action = val;
            else if (string.Equals(key, "TargetDocument", StringComparison.OrdinalIgnoreCase))
                target = val;
            else if (string.Equals(key, "ErrorMessage", StringComparison.OrdinalIgnoreCase))
                error = val;
        }

        return (state, action, target, error);
    }

    private static async Task WriteAtomicallyAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = targetPath + ".tmp";
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(tempPath, content, utf8NoBom, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, targetPath, overwrite: true);
    }

    private static string SanitizeIniKey(string key) =>
        key.Replace("=", "_", StringComparison.Ordinal)
           .Replace("\r", string.Empty, StringComparison.Ordinal)
           .Replace("\n", string.Empty, StringComparison.Ordinal)
           .Trim();

    private static string SanitizeIniLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
