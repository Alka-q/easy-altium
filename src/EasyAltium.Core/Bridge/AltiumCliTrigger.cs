using System.Diagnostics;
using EasyAltium.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyAltium.Core.Bridge;

/// <summary>
/// Altium CLI tetikleme sonucunu temsil eder.
/// </summary>
public sealed record AltiumTriggerResult(
    bool IsTriggered,
    string CommandLine,
    string BridgeState,
    string ExecutedAction,
    string TargetDocument,
    string? ErrorMessage = null
);

/// <summary>
/// Açık olan veya başlatılacak Altium Designer 26.10.1 (X2.EXE) oturumuna
/// -RScriptingSystem:RunScript(ProjectName="...\LCSC_Bridge.PrjScr"|ProcName="Bridge.pas>PlaceOrLoadComponent")
/// komut satırı parametresini gönderen tetikleyici.
/// </summary>
public sealed class AltiumCliTrigger
{
    private readonly AppConfig _config;
    private readonly IpcContractWriter _contractWriter;
    private readonly ILogger<AltiumCliTrigger> _logger;

    public AltiumCliTrigger(
        AppConfig config,
        IpcContractWriter? contractWriter = null,
        ILogger<AltiumCliTrigger>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _contractWriter = contractWriter ?? new IpcContractWriter();
        _logger = logger ?? NullLogger<AltiumCliTrigger>.Instance;
    }

    /// <summary>
    /// Altium Designer 26.10.1 için tam CLI argüman dizgesini üretir.
    /// Örn: -RScriptingSystem:RunScript(ProjectName="D:\...\LCSC_Bridge.PrjScr"|ProcName="Bridge.pas>PlaceOrLoadComponent")
    /// </summary>
    public string BuildScriptingArguments(string? customProcedureName = null)
    {
        string projectPath = Path.GetFullPath(_config.BridgeScriptProjectPath);
        string procName = string.IsNullOrWhiteSpace(customProcedureName)
            ? _config.BridgeProcedureName
            : customProcedureName;

        return $"-RScriptingSystem:RunScript(ProjectName=\"{projectPath}\"|ProcName=\"{procName}\")";
    }

    /// <summary>
    /// X2.EXE sürecinin şu anda sistemde çalışıp çalışmadığını kontrol eder.
    /// </summary>
    public static bool IsAltiumRunning()
    {
        try
        {
            return Process.GetProcessesByName("X2").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Altium Designer 26.10.1'e RunScript komutunu gönderir ve INI [Result] durumunu takip eder.
    /// </summary>
    public async Task<AltiumTriggerResult> TriggerAltiumBridgeAsync(
        string? customProcedureName = null,
        TimeSpan? pollTimeout = null,
        CancellationToken cancellationToken = default)
    {
        string arguments = BuildScriptingArguments(customProcedureName);
        string fullCommandLine = $"\"{_config.AltiumExecutablePath}\" {arguments}";

        if (!File.Exists(_config.AltiumExecutablePath))
        {
            string msg = $"Altium yürütülebilir dosyası bulunamadı: {_config.AltiumExecutablePath}. " +
                         "current_part.ini hazırlandı; Altium içerisinden Bridge.pas>PlaceOrLoadComponent çalıştırılabilir.";
            _logger.LogWarning("{Message}", msg);

            return new AltiumTriggerResult(
                IsTriggered: false,
                CommandLine: fullCommandLine,
                BridgeState: "READY_ON_DISK",
                ExecutedAction: "ContractWritten",
                TargetDocument: _config.IpcIniFilePath,
                ErrorMessage: msg);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config.AltiumExecutablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            _logger.LogInformation("Altium CLI tetikleniyor: {CommandLine}", fullCommandLine);
            using var proc = Process.Start(psi);

            // Bridge.pas'ın current_part.ini [Result] bölümünü güncellemesini bekle
            TimeSpan maxWait = pollTimeout ?? TimeSpan.FromSeconds(10);
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < maxWait && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                var (state, action, target, error) = await _contractWriter
                    .ReadResultSectionAsync(_config.IpcIniFilePath, cancellationToken)
                    .ConfigureAwait(false);

                if (string.Equals(state, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    return new AltiumTriggerResult(
                        IsTriggered: true,
                        CommandLine: fullCommandLine,
                        BridgeState: state,
                        ExecutedAction: action,
                        TargetDocument: target);
                }

                if (string.Equals(state, "ERROR", StringComparison.OrdinalIgnoreCase))
                {
                    return new AltiumTriggerResult(
                        IsTriggered: true,
                        CommandLine: fullCommandLine,
                        BridgeState: state,
                        ExecutedAction: action,
                        TargetDocument: target,
                        ErrorMessage: error);
                }
            }

            return new AltiumTriggerResult(
                IsTriggered: true,
                CommandLine: fullCommandLine,
                BridgeState: "DISPATCHED",
                ExecutedAction: "SentToAltium",
                TargetDocument: "ActiveSession");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Altium X2.EXE tetiklenirken hata oluştu.");
            return new AltiumTriggerResult(
                IsTriggered: false,
                CommandLine: fullCommandLine,
                BridgeState: "TRIGGER_ERROR",
                ExecutedAction: "None",
                TargetDocument: "None",
                ErrorMessage: ex.Message);
        }
    }
}
