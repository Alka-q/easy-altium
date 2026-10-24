using EasyAltium.Core.Bridge;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Library;
using EasyAltium.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyAltium.Core.Engine;

/// <summary>
/// Tam bileşen aktarım iş akışı sonucunu temsil eder.
/// </summary>
public sealed record PartImportWorkflowResult(
    bool IsSuccess,
    LcscPartMetadata Part,
    IpcPartContract Contract,
    AltiumTriggerResult TriggerResult,
    bool UsedFallback,
    bool UsedPassiveTemplate,
    string SummaryMessage
);

/// <summary>
/// LCSC Meta Veri Çekme -> npnp CLI / ASCII Fallback -> Pasif Sembol Filtresi ->
/// Mod A (MasterLib) veya Mod B (SQLite .DbLib) Kaydı -> current_part.ini Yazımı ->
/// Altium Designer 26.10.1 CLI Tetiklemesi adımlarını uçtan uca yöneten orkestratör.
/// </summary>
public sealed class PartImportOrchestrator
{
    private readonly AppConfig _config;
    private readonly LcscApiClient _apiClient;
    private readonly NpnpCliWrapper _npnpWrapper;
    private readonly AsciiFallbackHandler _fallbackHandler;
    private readonly PassiveSymbolFilter _passiveFilter;
    private readonly MasterLibraryManager _masterLibManager;
    private readonly SqliteDbLibManager _sqliteDbLibManager;
    private readonly DbLibFileGenerator _dbLibGenerator;
    private readonly IpcContractWriter _ipcWriter;
    private readonly AltiumCliTrigger _altiumTrigger;
    private readonly ILogger<PartImportOrchestrator> _logger;

    public PartImportOrchestrator(
        AppConfig config,
        LcscApiClient? apiClient = null,
        NpnpCliWrapper? npnpWrapper = null,
        AsciiFallbackHandler? fallbackHandler = null,
        PassiveSymbolFilter? passiveFilter = null,
        MasterLibraryManager? masterLibManager = null,
        SqliteDbLibManager? sqliteDbLibManager = null,
        DbLibFileGenerator? dbLibGenerator = null,
        IpcContractWriter? ipcWriter = null,
        AltiumCliTrigger? altiumTrigger = null,
        ILogger<PartImportOrchestrator>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _apiClient = apiClient ?? new LcscApiClient();
        _npnpWrapper = npnpWrapper ?? new NpnpCliWrapper(_config);
        _fallbackHandler = fallbackHandler ?? new AsciiFallbackHandler(_config);
        _passiveFilter = passiveFilter ?? new PassiveSymbolFilter();
        _masterLibManager = masterLibManager ?? new MasterLibraryManager(_config);
        _sqliteDbLibManager = sqliteDbLibManager ?? new SqliteDbLibManager(_config);
        _dbLibGenerator = dbLibGenerator ?? new DbLibFileGenerator(_config);
        _ipcWriter = ipcWriter ?? new IpcContractWriter();
        _altiumTrigger = altiumTrigger ?? new AltiumCliTrigger(_config, _ipcWriter);
        _logger = logger ?? NullLogger<PartImportOrchestrator>.Instance;
    }

    public Task<LcscPartMetadata> PreviewPartAsync(
        string lcscId,
        CancellationToken cancellationToken = default) =>
        _apiClient.FetchPartMetadataAsync(lcscId, cancellationToken);

    public async Task<PartImportWorkflowResult> ImportAndSendToAltiumAsync(
        LcscPartMetadata part,
        BridgeAction requestedAction = BridgeAction.PlaceOrLoad,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        _config.EnsureDirectoriesExist();

        progress?.Report($"[{part.LcscId}] npnp dönüştürücü çalıştırılıyor...");
        NpnpExportResult npnpResult = await _npnpWrapper.ExportComponentAsync(part, cancellationToken)
            .ConfigureAwait(false);

        bool usedFallback = false;
        string schLibPath = npnpResult.SchLibPath;
        string pcbLibPath = npnpResult.PcbLibPath;
        string stepPath = npnpResult.StepModelPath;
        string fallbackSchDoc = string.Empty;
        string fallbackPcbDoc = string.Empty;
        BridgeAction effectiveAction = requestedAction;

        if (!npnpResult.IsSuccess || npnpResult.RequiresFallback)
        {
            progress?.Report($"[{part.LcscId}] npnp tamamlanamadı ({npnpResult.ErrorReason}). ASCII Fallback devreye alınıyor...");
            AsciiFallbackResult fallback = await _fallbackHandler.PrepareFallbackDocumentsAsync(part, cancellationToken)
                .ConfigureAwait(false);

            usedFallback = true;
            fallbackSchDoc = fallback.FallbackSchDocPath;
            fallbackPcbDoc = fallback.FallbackPcbDocPath;
            if (!string.IsNullOrWhiteSpace(fallback.StepModelPath))
                stepPath = fallback.StepModelPath;

            string safeMpn = LcscApiClient.SanitizeAltiumIdentifier(part.Mpn);
            if (string.IsNullOrWhiteSpace(schLibPath))
            {
                schLibPath = _config.LibraryMode == LibraryMode.MasterLib
                    ? _config.MasterSchLibPath
                    : Path.Combine(_config.SchLibDirectory, $"{part.LcscId}_{safeMpn}.SchLib");
            }

            if (string.IsNullOrWhiteSpace(pcbLibPath))
            {
                pcbLibPath = _config.LibraryMode == LibraryMode.MasterLib
                    ? _config.MasterPcbLibPath
                    : Path.Combine(_config.PcbLibDirectory, $"{part.LcscId}_{safeMpn}.PcbLib");
            }

            // Eğer yerel standart pasif sembol şablonu kullanılmıyorsa ve .SchLib henüz yoksa FallbackMakeLibrary tetikle
            if (!_config.UsePassiveTemplateSymbol && !File.Exists(schLibPath) && fallback.IsSuccess)
            {
                effectiveAction = BridgeAction.FallbackMakeLibrary;
            }
        }

        // Pasif Eleman Sembol Filtresi değerlendirmesi
        PassiveSymbolMatchResult passiveMatch = _passiveFilter.Evaluate(part, _config, schLibPath);
        string dbSymbolRef = passiveMatch.IsPassiveMatched ? passiveMatch.EffectiveLibRef : part.LibRef;
        string dbSymbolPath = passiveMatch.IsPassiveMatched ? passiveMatch.EffectiveSchLibPath : schLibPath;

        // Kütüphane ve Veritabanı Kaydı (Mod A veya Mod B)
        if (_config.LibraryMode == LibraryMode.MasterLib)
        {
            progress?.Report($"[{part.LcscId}] Mod A (LCSC_Master) kütüphanesine kaydediliyor...");
            (schLibPath, pcbLibPath) = await _masterLibManager
                .RegisterComponentInMasterLibraryAsync(part, schLibPath, pcbLibPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(pcbLibPath) && File.Exists(pcbLibPath))
            {
                PcbLib3DModelPatcher.TryPatchPcbLib3DRotation(pcbLibPath, part);
            }

            progress?.Report($"[{part.LcscId}] Mod B (SQLite .DbLib -> {part.Category}) veritabanına UPSERT yapılıyor...");
            await _sqliteDbLibManager.UpsertComponentAsync(
                part: part,
                libraryRef: dbSymbolRef,
                libraryPath: dbSymbolPath,
                footprintRef: part.FootprintRef,
                footprintPath: pcbLibPath,
                stepModelPath: stepPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await _dbLibGenerator.EnsureDbLibFileAsync(cancellationToken).ConfigureAwait(false);
        }

        // IPC Sözleşmesini (current_part.ini) Oluştur ve Yaz
        progress?.Report($"[{part.LcscId}] current_part.ini veri sözleşmesi yazılıyor...");
        var contract = new IpcPartContract
        {
            RequestId = Guid.NewGuid().ToString("D"),
            TimestampUtc = DateTime.UtcNow,
            Action = effectiveAction,
            LibraryMode = _config.LibraryMode,
            PlacementMethod = _config.PlacementMethod,
            OpenLibraryIfNoSchDoc = _config.OpenLibraryIfNoSchDoc,
            SilentMode = true,
            LogFilePath = _config.BridgeLogFilePath,
            SchLibPath = schLibPath,
            PcbLibPath = pcbLibPath,
            StepModelPath = stepPath,
            DbLibPath = _config.DbLibFilePath,
            DbTableName = SqliteDbLibManager.GetSafeTableName(part.Category),
            UsePassiveTemplateSymbol = passiveMatch.IsPassiveMatched,
            PassiveTemplateSchLibPath = _config.PassiveTemplateSchLibPath,
            PassiveTemplateLibRef = passiveMatch.PassiveTemplateLibRef ?? string.Empty,
            FallbackSchDocPath = fallbackSchDoc,
            FallbackPcbDocPath = fallbackPcbDoc,
            LcscId = part.LcscId,
            LibRef = part.LibRef,
            FootprintRef = part.FootprintRef,
            DesignatorPrefix = part.DesignatorPrefix,
            Comment = part.Comment,
            Description = part.Description,
            PartCount = part.PartCount,
            Category = SqliteDbLibManager.GetSafeTableName(part.Category),
            Parameters = part.BuildBomParameterDictionary()
        };

        await _ipcWriter.WriteContractAsync(_config.IpcIniFilePath, contract, cancellationToken)
            .ConfigureAwait(false);

        // Altium Designer 26.10.1 CLI Köprüsünü Tetikle
        progress?.Report($"[{part.LcscId}] Altium Designer 26.10.1 köprüsü tetikleniyor...");
        AltiumTriggerResult triggerResult = await _altiumTrigger
            .TriggerAltiumBridgeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        string summary = triggerResult.IsTriggered
            ? $"{part.LcscId} ({part.Mpn}) Altium'a gönderildi [{triggerResult.BridgeState}]."
            : $"{part.LcscId} ({part.Mpn}) kütüphaneye ve current_part.ini'ye işlendi ({triggerResult.ErrorMessage}).";

        progress?.Report(summary);

        return new PartImportWorkflowResult(
            IsSuccess: true,
            Part: part,
            Contract: contract,
            TriggerResult: triggerResult,
            UsedFallback: usedFallback,
            UsedPassiveTemplate: passiveMatch.IsPassiveMatched,
            SummaryMessage: summary);
    }
}
