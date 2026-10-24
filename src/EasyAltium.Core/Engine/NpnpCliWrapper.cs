using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Library;
using EasyAltium.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyAltium.Core.Engine;

/// <summary>
/// npnp CLI çalıştırma sonucunu temsil eder.
/// </summary>
public sealed record NpnpExportResult(
    bool IsSuccess,
    bool RequiresFallback,
    string SchLibPath,
    string PcbLibPath,
    string StepModelPath,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? ErrorReason = null
);

/// <summary>
/// Rust tabanlı npnp CLI aracını subprocess olarak çalıştıran ve üretilen
/// .SchLib, .PcbLib ve .step dosyalarını yerel kütüphane yapısına yerleştiren sarmalayıcı.
/// </summary>
public sealed class NpnpCliWrapper
{
    private readonly AppConfig _config;
    private readonly ILogger<NpnpCliWrapper> _logger;

    public NpnpCliWrapper(AppConfig config, ILogger<NpnpCliWrapper>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? NullLogger<NpnpCliWrapper>.Instance;
    }

    public async Task<NpnpExportResult> ExportComponentAsync(
        LcscPartMetadata part,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        _config.EnsureDirectoriesExist();

        string stagingDir = Path.Combine(_config.LibraryRootDirectory, ".staging", $"{part.LcscId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            string arguments = _config.NpnpArgumentsTemplate
                .Replace("{LCSC_ID}", part.LcscId, StringComparison.OrdinalIgnoreCase)
                .Replace("{OUTPUT_DIR}", stagingDir, StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation("npnp başlatılıyor: {Exe} {Args}", _config.NpnpExecutablePath, arguments);

            var psi = new ProcessStartInfo
            {
                FileName = _config.NpnpExecutablePath,
                Arguments = arguments,
                WorkingDirectory = stagingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            var stdOutBuilder = new StringBuilder();
            var stdErrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stdOutBuilder.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stdErrBuilder.AppendLine(e.Data);
            };

            try
            {
                if (!process.Start())
                {
                    return new NpnpExportResult(
                        IsSuccess: false,
                        RequiresFallback: true,
                        SchLibPath: string.Empty,
                        PcbLibPath: string.Empty,
                        StepModelPath: string.Empty,
                        ExitCode: -1,
                        StandardOutput: string.Empty,
                        StandardError: "npnp süreci başlatılamadı.",
                        ErrorReason: "npnp süreci başlatılamadı.");
                }
            }
            catch (Exception startEx)
            {
                _logger.LogWarning(startEx, "npnp yürütülebilir dosyası çalıştırılamadı ({Exe}). Fallback akışına geçilecek.", _config.NpnpExecutablePath);
                return new NpnpExportResult(
                    IsSuccess: false,
                    RequiresFallback: true,
                    SchLibPath: string.Empty,
                    PcbLibPath: string.Empty,
                    StepModelPath: string.Empty,
                    ExitCode: -1,
                    StandardOutput: string.Empty,
                    StandardError: startEx.Message,
                    ErrorReason: $"npnp bulunamadı veya başlatılamadı: {startEx.Message}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _config.NpnpTimeoutSeconds)));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                return new NpnpExportResult(
                    IsSuccess: false,
                    RequiresFallback: true,
                    SchLibPath: string.Empty,
                    PcbLibPath: string.Empty,
                    StepModelPath: string.Empty,
                    ExitCode: -2,
                    StandardOutput: stdOutBuilder.ToString(),
                    StandardError: stdErrBuilder.ToString(),
                    ErrorReason: $"npnp {_config.NpnpTimeoutSeconds} saniye içinde tamamlanamadı (Zaman aşımı).");
            }

            string stdOut = stdOutBuilder.ToString();
            string stdErr = stdErrBuilder.ToString();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "npnp hata kodu ile çıktı (ExitCode={ExitCode}, LCSC={LcscId}). StdErr: {StdErr}",
                    process.ExitCode, part.LcscId, stdErr);

                return new NpnpExportResult(
                    IsSuccess: false,
                    RequiresFallback: true,
                    SchLibPath: string.Empty,
                    PcbLibPath: string.Empty,
                    StepModelPath: string.Empty,
                    ExitCode: process.ExitCode,
                    StandardOutput: stdOut,
                    StandardError: stdErr,
                    ErrorReason: $"npnp çıkış kodu {process.ExitCode}: {stdErr.Trim()}");
            }

            // 3D STEP modelini de indir (npnp model <LCSC_ID> -o "<stagingDir>" --force)
            await TryExportStepModelAsync(part.LcscId, stagingDir, cancellationToken).ConfigureAwait(false);

            // Üretilen .SchLib, .PcbLib ve .step dosyalarını hedef dizinlere taşı
            return OrganizeExportedFiles(part, stagingDir, stdOut, stdErr);
        }
        finally
        {
            TryCleanupStagingDirectory(stagingDir);
        }
    }

    private NpnpExportResult OrganizeExportedFiles(
        LcscPartMetadata part,
        string stagingDir,
        string stdOut,
        string stdErr)
    {
        var schFiles = Directory.GetFiles(stagingDir, "*.SchLib", SearchOption.AllDirectories);
        var pcbFiles = Directory.GetFiles(stagingDir, "*.PcbLib", SearchOption.AllDirectories);
        var stepFiles = Directory.GetFiles(stagingDir, "*.step", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(stagingDir, "*.stp", SearchOption.AllDirectories))
            .ToArray();

        if (schFiles.Length == 0 && pcbFiles.Length == 0)
        {
            return new NpnpExportResult(
                IsSuccess: false,
                RequiresFallback: true,
                SchLibPath: string.Empty,
                PcbLibPath: string.Empty,
                StepModelPath: string.Empty,
                ExitCode: 0,
                StandardOutput: stdOut,
                StandardError: stdErr,
                ErrorReason: "npnp tamamlandı ancak .SchLib veya .PcbLib dosyası üretilmedi.");
        }

        string safeMpn = LcscApiClient.SanitizeAltiumIdentifier(part.Mpn);
        string targetSchLib = _config.LibraryMode == LibraryMode.MasterLib
            ? _config.MasterSchLibPath
            : Path.Combine(_config.SchLibDirectory, $"{part.LcscId}_{safeMpn}.SchLib");

        string targetPcbLib = _config.LibraryMode == LibraryMode.MasterLib
            ? _config.MasterPcbLibPath
            : Path.Combine(_config.PcbLibDirectory, $"{part.LcscId}_{safeMpn}.PcbLib");

        string targetStep = Path.Combine(_config.StepDirectory, $"{part.LcscId}_{safeMpn}.step");

        if (schFiles.Length > 0)
        {
            File.Copy(schFiles[0], targetSchLib, overwrite: true);
            string? extractedLibRef = TryExtractLibRefFromSchLib(targetSchLib, part.LcscId);
            if (!string.IsNullOrWhiteSpace(extractedLibRef))
                part.LibRef = extractedLibRef;
        }

        if (pcbFiles.Length > 0)
        {
            File.Copy(pcbFiles[0], targetPcbLib, overwrite: true);
            PcbLib3DModelPatcher.TryPatchPcbLib3DRotation(targetPcbLib, part);
            string? extractedFootprint = TryExtractFootprintName(targetSchLib, targetPcbLib, part.LcscId);
            if (!string.IsNullOrWhiteSpace(extractedFootprint))
                part.FootprintRef = extractedFootprint;
        }

        string resolvedStepPath = string.Empty;
        if (stepFiles.Length > 0)
        {
            File.Copy(stepFiles[0], targetStep, overwrite: true);
            resolvedStepPath = targetStep;
        }

        return new NpnpExportResult(
            IsSuccess: true,
            RequiresFallback: false,
            SchLibPath: File.Exists(targetSchLib) ? targetSchLib : string.Empty,
            PcbLibPath: File.Exists(targetPcbLib) ? targetPcbLib : string.Empty,
            StepModelPath: resolvedStepPath,
            ExitCode: 0,
            StandardOutput: stdOut,
            StandardError: stdErr);
    }

    private async Task TryExportStepModelAsync(
        string lcscId,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.NpnpExecutablePath,
                Arguments = $"model {lcscId} -o \"{stagingDir}\" --force",
                WorkingDirectory = stagingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // STEP modeli opsiyoneldir
        }
    }

    private static string? TryExtractLibRefFromSchLib(string schLibPath, string lcscId)
    {
        try
        {
            if (!File.Exists(schLibPath))
                return null;

            byte[] bytes = File.ReadAllBytes(schLibPath);
            string latin1 = Encoding.Latin1.GetString(bytes);

            var utf8Matches = Regex.Matches(latin1, @"\|%UTF8%LIBREF\d+=([^|\x00]+)");
            if (utf8Matches.Count > 0)
            {
                string raw = utf8Matches[^1].Groups[1].Value;
                byte[] utf8Bytes = Encoding.Latin1.GetBytes(raw);
                return Encoding.UTF8.GetString(utf8Bytes).Trim();
            }

            var plainMatches = Regex.Matches(latin1, @"\|LIBREF\d+=([^|\x00]+)");
            if (plainMatches.Count > 0)
            {
                return WebUtility.HtmlDecode(plainMatches[^1].Groups[1].Value).Trim();
            }

            string fileNameNoExt = Path.GetFileNameWithoutExtension(schLibPath);
            if (fileNameNoExt.Contains($"__{lcscId}", StringComparison.OrdinalIgnoreCase))
                return fileNameNoExt;
        }
        catch
        {
        }

        return null;
    }

    private static string? TryExtractFootprintName(string schLibPath, string pcbLibPath, string lcscId)
    {
        try
        {
            foreach ((string path, string key) in new[] { (schLibPath, "MODELNAME"), (pcbLibPath, "PATTERN") })
            {
                if (!File.Exists(path))
                    continue;

                byte[] bytes = File.ReadAllBytes(path);
                string latin1 = Encoding.Latin1.GetString(bytes);

                var utf8Matches = Regex.Matches(latin1, $@"%UTF8%{key}=([^|\x00]*__{Regex.Escape(lcscId)})", RegexOptions.IgnoreCase);
                if (utf8Matches.Count > 0)
                {
                    byte[] utf8Bytes = Encoding.Latin1.GetBytes(utf8Matches[^1].Groups[1].Value);
                    return Encoding.UTF8.GetString(utf8Bytes).Trim();
                }

                var plainMatches = Regex.Matches(latin1, $@"\|{key}=([^|\x00]*__{Regex.Escape(lcscId)})", RegexOptions.IgnoreCase);
                if (plainMatches.Count > 0)
                {
                    return WebUtility.HtmlDecode(plainMatches[^1].Groups[1].Value).Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Süreç zaten kapanmış olabilir
        }
    }

    private static void TryCleanupStagingDirectory(string stagingDir)
    {
        try
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
        catch
        {
            // Geçici klasör silinemezse yoksay
        }
    }
}
