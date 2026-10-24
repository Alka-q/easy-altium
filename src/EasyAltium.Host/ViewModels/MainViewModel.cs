using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Engine;
using EasyAltium.Core.Library;
using EasyAltium.Core.Models;

namespace EasyAltium.Host.ViewModels;

/// <summary>
/// ViewModel managing the main mini search window and clipboard capture workflows.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly PartImportOrchestrator _orchestrator;
    private readonly SqliteDbLibManager _sqliteManager;

    private string _searchLcscId = "C2040";
    private LcscPartMetadata? _currentPart;
    private bool _isBusy;
    private string _statusMessage = "Ready — Enter an LCSC part code or copy one from your browser (e.g., C2040).";
    private bool _isClipboardListening;
    private bool _alwaysOnTop;
    private bool _isSqliteMode;
    private bool _usePassiveTemplateSymbol;
    private int _databaseComponentCount;

    public event EventHandler<bool>? ClipboardListeningChanged;
    public event EventHandler? OpenSettingsRequested;

    public MainViewModel(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _orchestrator = new PartImportOrchestrator(_config);
        _sqliteManager = new SqliteDbLibManager(_config);

        _isClipboardListening = _config.AutoWatchClipboard;
        _alwaysOnTop = _config.AlwaysOnTopMiniWindow;
        _isSqliteMode = _config.LibraryMode == LibraryMode.SqliteDbLib;
        _usePassiveTemplateSymbol = _config.UsePassiveTemplateSymbol;

        ActivityLogs = new ObservableCollection<string>();

        FetchPartCommand = new AsyncRelayCommand(_ => FetchPartPreviewAsync(SearchLcscId), _ => !IsBusy);
        PlaceInAltiumCommand = new AsyncRelayCommand(_ => ExecuteImportAsync(BridgeAction.PlaceOrLoad), _ => !IsBusy && HasCurrentPart);
        LoadLibraryOnlyCommand = new AsyncRelayCommand(_ => ExecuteImportAsync(BridgeAction.LoadOnly), _ => !IsBusy && HasCurrentPart);
        OpenDatasheetCommand = new RelayCommand(_ => OpenCurrentDatasheet(), _ => HasCurrentPart);
        OpenSettingsCommand = new RelayCommand(_ => OpenSettingsRequested?.Invoke(this, EventArgs.Empty));

        _ = RefreshDatabaseStatsAsync();
    }

    public AppConfig Config => _config;

    public string AltiumVersionBadgeText => _config.DetectedAltiumVersionLabel;

    public ObservableCollection<string> ActivityLogs { get; }

    public AsyncRelayCommand FetchPartCommand { get; }
    public AsyncRelayCommand PlaceInAltiumCommand { get; }
    public AsyncRelayCommand LoadLibraryOnlyCommand { get; }
    public RelayCommand OpenDatasheetCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }

    public string SearchLcscId
    {
        get => _searchLcscId;
        set => SetProperty(ref _searchLcscId, value);
    }

    public LcscPartMetadata? CurrentPart
    {
        get => _currentPart;
        private set
        {
            if (SetProperty(ref _currentPart, value))
            {
                OnPropertyChanged(nameof(HasCurrentPart));
                OnPropertyChanged(nameof(PartMpnDisplay));
                OnPropertyChanged(nameof(PartManufacturerDisplay));
                OnPropertyChanged(nameof(PartPackageDisplay));
                OnPropertyChanged(nameof(PartStockDisplay));
                OnPropertyChanged(nameof(PartPriceDisplay));
                OnPropertyChanged(nameof(PartClassDisplay));
                OnPropertyChanged(nameof(IsBasicPart));
                OnPropertyChanged(nameof(PartDescriptionDisplay));
                OnPropertyChanged(nameof(PartCategoryDisplay));
                OnPropertyChanged(nameof(PartFootprintDisplay));
                PlaceInAltiumCommand.RaiseCanExecuteChanged();
                LoadLibraryOnlyCommand.RaiseCanExecuteChanged();
                OpenDatasheetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasCurrentPart => CurrentPart is not null;

    public string PartMpnDisplay => CurrentPart?.Mpn ?? "-";
    public string PartManufacturerDisplay => string.IsNullOrWhiteSpace(CurrentPart?.Manufacturer) ? "-" : CurrentPart.Manufacturer;
    public string PartPackageDisplay => string.IsNullOrWhiteSpace(CurrentPart?.Package) ? "-" : CurrentPart.Package;
    public string PartStockDisplay => CurrentPart is not null ? CurrentPart.Stock.ToString("N0", CultureInfo.InvariantCulture) : "-";
    public string PartPriceDisplay => CurrentPart is not null ? $"${CurrentPart.UnitPriceUsd:0.0000}" : "-";
    public string PartClassDisplay => CurrentPart?.JlcpcbClass ?? "-";
    public bool IsBasicPart => string.Equals(CurrentPart?.JlcpcbClass, "Basic", StringComparison.OrdinalIgnoreCase);
    public string PartDescriptionDisplay => string.IsNullOrWhiteSpace(CurrentPart?.Description) ? "-" : CurrentPart.Description;
    public string PartCategoryDisplay => CurrentPart is not null ? CurrentPart.Category.ToString() : "-";
    public string PartFootprintDisplay => string.IsNullOrWhiteSpace(CurrentPart?.FootprintRef) ? "-" : CurrentPart.FootprintRef;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                FetchPartCommand.RaiseCanExecuteChanged();
                PlaceInAltiumCommand.RaiseCanExecuteChanged();
                LoadLibraryOnlyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsClipboardListening
    {
        get => _isClipboardListening;
        set
        {
            if (SetProperty(ref _isClipboardListening, value))
            {
                _config.AutoWatchClipboard = value;
                _config.Save();
                ClipboardListeningChanged?.Invoke(this, value);
                AppendLog(value ? "Clipboard watcher enabled." : "Clipboard watcher paused.");
            }
        }
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (SetProperty(ref _alwaysOnTop, value))
            {
                _config.AlwaysOnTopMiniWindow = value;
                _config.Save();
            }
        }
    }

    public bool IsSqliteMode
    {
        get => _isSqliteMode;
        set
        {
            if (SetProperty(ref _isSqliteMode, value))
            {
                _config.LibraryMode = value ? LibraryMode.SqliteDbLib : LibraryMode.MasterLib;
                _config.Save();
                OnPropertyChanged(nameof(LibraryModeBadgeText));
                AppendLog(value
                    ? "Library mode: Mode B (Categorized SQLite .DbLib) selected."
                    : "Library mode: Mode A (Consolidated LCSC_Master) selected.");
            }
        }
    }

    public string LibraryModeBadgeText => IsSqliteMode
        ? $"Mode B: SQLite .DbLib ({DatabaseComponentCount} Parts)"
        : "Mode A: LCSC_Master.SchLib";

    public bool UsePassiveTemplateSymbol
    {
        get => _usePassiveTemplateSymbol;
        set
        {
            if (SetProperty(ref _usePassiveTemplateSymbol, value))
            {
                _config.UsePassiveTemplateSymbol = value;
                _config.Save();
                AppendLog(value
                    ? "Passive symbol filter: Local Standard Symbol enabled."
                    : "Passive symbol filter: Original EasyEDA symbol enabled.");
            }
        }
    }

    public int DatabaseComponentCount
    {
        get => _databaseComponentCount;
        private set
        {
            if (SetProperty(ref _databaseComponentCount, value))
                OnPropertyChanged(nameof(LibraryModeBadgeText));
        }
    }

    public async Task<LcscPartMetadata?> FetchPartPreviewAsync(string? rawCode)
    {
        string? normalized = LcscPartMetadata.TryExtractLcscId(rawCode);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            StatusMessage = "Invalid LCSC part code! Enter e.g. C2040 or C14663.";
            AppendLog($"Error: '{rawCode}' is not a valid LCSC part code (^C[1-9]\\d{{2,}}$).");
            return null;
        }

        SearchLcscId = normalized;
        IsBusy = true;
        StatusMessage = $"Fetching metadata for {normalized} from LCSC & JLCPCB...";
        AppendLog($"Querying {normalized}...");

        try
        {
            LcscPartMetadata metadata = await _orchestrator.PreviewPartAsync(normalized);
            CurrentPart = metadata;
            StatusMessage = $"{metadata.LcscId} — {metadata.Mpn} ({metadata.Package}) | Stock: {metadata.Stock:N0} | JLCPCB {metadata.JlcpcbClass}";
            AppendLog($"Preview ready: {metadata.LcscId} -> {metadata.Mpn} [{metadata.Category}]");
            return metadata;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Query error: {ex.Message}";
            AppendLog($"Error ({normalized}): {ex.Message}");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<PartImportWorkflowResult?> ExecuteImportAsync(BridgeAction action)
    {
        if (CurrentPart is null)
            return null;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(msg =>
            {
                StatusMessage = msg;
                AppendLog(msg);
            });

            PartImportWorkflowResult result = await _orchestrator.ImportAndSendToAltiumAsync(
                CurrentPart,
                action,
                progress);

            await RefreshDatabaseStatsAsync();
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import error: {ex.Message}";
            AppendLog($"CRITICAL ERROR: {ex.Message}");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshDatabaseStatsAsync()
    {
        try
        {
            int count = await _sqliteManager.GetTotalComponentCountAsync();
            DatabaseComponentCount = count;
        }
        catch
        {
        }
    }

    public void SyncFromConfig()
    {
        _isClipboardListening = _config.AutoWatchClipboard;
        _alwaysOnTop = _config.AlwaysOnTopMiniWindow;
        _isSqliteMode = _config.LibraryMode == LibraryMode.SqliteDbLib;
        _usePassiveTemplateSymbol = _config.UsePassiveTemplateSymbol;

        OnPropertyChanged(nameof(IsClipboardListening));
        OnPropertyChanged(nameof(AlwaysOnTop));
        OnPropertyChanged(nameof(IsSqliteMode));
        OnPropertyChanged(nameof(UsePassiveTemplateSymbol));
        OnPropertyChanged(nameof(LibraryModeBadgeText));
        OnPropertyChanged(nameof(AltiumVersionBadgeText));
    }

    public void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => InsertLogLine(line));
        }
        else
        {
            InsertLogLine(line);
        }
    }

    private void InsertLogLine(string line)
    {
        ActivityLogs.Insert(0, line);
        while (ActivityLogs.Count > 100)
            ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
    }

    private void OpenCurrentDatasheet()
    {
        if (CurrentPart is null || string.IsNullOrWhiteSpace(CurrentPart.DatasheetUrl))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = CurrentPart.DatasheetUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Could not open datasheet: {ex.Message}");
        }
    }
}
