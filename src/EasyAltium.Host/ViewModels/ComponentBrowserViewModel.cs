using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Engine;
using EasyAltium.Core.Library;
using EasyAltium.Core.Models;

namespace EasyAltium.Host.ViewModels;

/// <summary>
/// Represents an entry in the left-most library source column (System, Recent, Favorite, Project).
/// </summary>
public sealed class CatalogSourceItemViewModel : ObservableObject
{
    private bool _isSelected;

    public CatalogSourceMode Mode { get; init; }

    public string Title { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// Represents a category or subcategory node in the left category tree.
/// </summary>
public sealed class CatalogCategoryNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isVisible = true;

    public int CatalogId { get; init; }

    public int ParentId { get; init; }

    public string Title { get; init; } = string.Empty;

    public ComponentCategory? LocalDbCategory { get; init; }

    public ObservableCollection<CatalogCategoryNodeViewModel> Children { get; } = new();

    public bool HasChildren => Children.Count > 0;

    public bool IsSubcategory => ParentId > 1;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}

/// <summary>
/// Selectable option inside a dynamic parametric filter column.
/// </summary>
public sealed class CatalogFilterOptionViewModel : ObservableObject
{
    private readonly Action<CatalogFilterOptionViewModel>? _onSelectionChanged;
    private bool _isSelected;

    public CatalogFilterOptionViewModel(
        string columnKey,
        string columnTitle,
        CatalogFilterOption option,
        Action<CatalogFilterOptionViewModel>? onSelectionChanged = null)
    {
        ColumnKey = columnKey;
        ColumnTitle = columnTitle;
        Name = option.Name;
        Value = option.Value;
        Id = option.Id;
        _onSelectionChanged = onSelectionChanged;
    }

    public string ColumnKey { get; }

    public string ColumnTitle { get; }

    public string Name { get; }

    public string Value { get; }

    public string? Id { get; }

    public string ChipDisplayText => $"{ColumnTitle}: {Name}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onSelectionChanged?.Invoke(this);
            }
        }
    }

    public void SetSelectedSilently(bool selected)
    {
        SetProperty(ref _isSelected, selected, nameof(IsSelected));
    }
}

/// <summary>
/// Dynamic parametric filter column (e.g., Package, Manufacturer, Features, Gain, Capacitance).
/// </summary>
public sealed class CatalogFilterColumnViewModel : ObservableObject
{
    public const int MaxInitialRenderedOptions = 250;

    private readonly List<CatalogFilterOptionViewModel> _allOptions;
    private string _searchText = string.Empty;

    public CatalogFilterColumnViewModel(
        CatalogFilterColumn model,
        Action<CatalogFilterOptionViewModel> onOptionToggled)
    {
        Title = model.Title;
        Key = model.Key;
        _allOptions = model.Options
            .Select(o => new CatalogFilterOptionViewModel(model.Key, model.Title, o, onOptionToggled))
            .ToList();

        FilteredOptions = new ObservableCollection<CatalogFilterOptionViewModel>(
            _allOptions.Take(MaxInitialRenderedOptions));
    }

    public string Title { get; }

    public string Key { get; }

    public IReadOnlyList<CatalogFilterOptionViewModel> AllOptions => _allOptions;

    public ObservableCollection<CatalogFilterOptionViewModel> FilteredOptions { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilteredOptions();
            }
        }
    }

    private void RefreshFilteredOptions()
    {
        FilteredOptions.Clear();
        string q = (_searchText ?? string.Empty).Trim();
        int count = 0;
        foreach (CatalogFilterOptionViewModel opt in _allOptions)
        {
            if (string.IsNullOrEmpty(q) || opt.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || opt.IsSelected)
            {
                FilteredOptions.Add(opt);
                count++;
                if (count >= MaxInitialRenderedOptions)
                    break;
            }
        }
    }
}

/// <summary>
/// Represents a single component card row in the Component Browser result list.
/// </summary>
public sealed class CatalogPartItemViewModel : ObservableObject
{
    private bool _isFavorite;
    private bool _isPlacing;

    public CatalogPartItemViewModel(
        CatalogPartItem model,
        bool isFavorite,
        Func<CatalogPartItemViewModel, BridgeAction, Task> executePartActionAsync,
        Action<CatalogPartItemViewModel> toggleFavoriteAction,
        Action<string> openUrlAction)
    {
        Model = model;
        _isFavorite = isFavorite;

        PlaceCommand = new AsyncRelayCommand(_ => executePartActionAsync(this, BridgeAction.PlaceOrLoad), _ => !IsPlacing);
        AddToLibraryCommand = new AsyncRelayCommand(_ => executePartActionAsync(this, BridgeAction.LoadOnly), _ => !IsPlacing);
        ToggleFavoriteCommand = new RelayCommand(_ => toggleFavoriteAction(this));
        OpenProductUrlCommand = new RelayCommand(_ => openUrlAction(Model.ProductUrl));
        OpenDatasheetCommand = new RelayCommand(_ => openUrlAction(Model.DatasheetUrl));
    }

    public CatalogPartItem Model { get; }

    public string LcscId => Model.LcscId;

    public string Mpn => Model.Mpn;

    public string FootprintDisplay => string.IsNullOrWhiteSpace(Model.FootprintTitle) ? "-" : Model.FootprintTitle;

    public string BrandDisplay => string.IsNullOrWhiteSpace(Model.Manufacturer) ? "-" : Model.Manufacturer;

    public string PartNameDisplay => !string.IsNullOrWhiteSpace(Model.LcscPartName)
        ? Model.LcscPartName
        : Model.Description;

    public string? ImageUrl => string.IsNullOrWhiteSpace(Model.ImageUrl) ? null : Model.ImageUrl;

    public bool HasImage => !string.IsNullOrWhiteSpace(Model.ImageUrl);

    public IReadOnlyList<CatalogPriceTier> PriceTiers => Model.PriceTiers;

    public string LcscStockDisplay => Model.LcscStock.ToString(CultureInfo.InvariantCulture);

    public string JlcpcbStockDisplay => Model.JlcpcbStock.ToString(CultureInfo.InvariantCulture);

    public string JlcpcbClassDisplay => Model.JlcpcbPartClass;

    public bool IsBasicPart => Model.JlcpcbPartClass.Contains("Basic", StringComparison.OrdinalIgnoreCase);

    public void UpdateJlcpcbStockAndClass(int stock, string partClass)
    {
        Model.JlcpcbStock = stock;
        if (!string.IsNullOrWhiteSpace(partClass))
            Model.JlcpcbPartClass = partClass;

        OnPropertyChanged(nameof(JlcpcbStockDisplay));
        OnPropertyChanged(nameof(JlcpcbClassDisplay));
        OnPropertyChanged(nameof(IsBasicPart));
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteStarText));
            }
        }
    }

    public string FavoriteStarText => IsFavorite ? "★" : "☆";

    public bool IsPlacing
    {
        get => _isPlacing;
        set
        {
            if (SetProperty(ref _isPlacing, value))
            {
                OnPropertyChanged(nameof(PlaceButtonText));
                PlaceCommand.RaiseCanExecuteChanged();
                AddToLibraryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PlaceButtonText => IsPlacing ? "Placing..." : "Place";

    public AsyncRelayCommand PlaceCommand { get; }

    public AsyncRelayCommand AddToLibraryCommand { get; }

    public RelayCommand ToggleFavoriteCommand { get; }

    public RelayCommand OpenProductUrlCommand { get; }

    public RelayCommand OpenDatasheetCommand { get; }
}

/// <summary>
/// ViewModel powering the EasyEDA-style Component Browser ("Device/Reuse Block") window.
/// </summary>
public sealed class ComponentBrowserViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly MainViewModel _mainViewModel;
    private readonly EasyEdaCatalogClient _catalogClient;
    private readonly SqliteDbLibManager _sqliteManager;

    private CancellationTokenSource? _queryCts;
    private List<CatalogCategoryNode> _rawSystemCategories = new();
    private CatalogSourceMode _selectedSourceMode = CatalogSourceMode.System;
    private CatalogCategoryNodeViewModel? _selectedCategory;

    private string _sourceFilterText = string.Empty;
    private string _categoryFilterText = string.Empty;
    private string _searchKeyword = string.Empty;
    private bool _isInSearchMode;

    private bool _isLoadingCategories;
    private bool _isLoadingFilters;
    private bool _isLoadingProducts;
    private bool _isFilterExpanded;

    private CatalogSortField _sortField = CatalogSortField.None;
    private CatalogSortDirection _sortDirection = CatalogSortDirection.None;

    private int _currentPage = 1;
    private int _pageSize = 10;
    private int _totalCount;
    private int _totalPages = 1;
    private string _statusMessage = "Ready — Select a category or search LCSC Electronics catalog.";

    public event EventHandler? OpenMiniWindowRequested;
    public event EventHandler? MinimizeForPlacementRequested;

    public ComponentBrowserViewModel(
        AppConfig config,
        MainViewModel mainViewModel,
        EasyEdaCatalogClient? catalogClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _catalogClient = catalogClient ?? new EasyEdaCatalogClient();
        _sqliteManager = new SqliteDbLibManager(_config);

        AllSources = new List<CatalogSourceItemViewModel>
        {
            new() { Mode = CatalogSourceMode.System, Title = "System", IsSelected = true },
            new() { Mode = CatalogSourceMode.Recent, Title = "Recent" },
            new() { Mode = CatalogSourceMode.Favorite, Title = "Favorite" },
            new() { Mode = CatalogSourceMode.LocalDbLib, Title = "Project" }
        };

        FilteredSources = new ObservableCollection<CatalogSourceItemViewModel>(AllSources);
        Categories = new ObservableCollection<CatalogCategoryNodeViewModel>();
        FilterColumns = new ObservableCollection<CatalogFilterColumnViewModel>();
        SelectedFilterOptions = new ObservableCollection<CatalogFilterOptionViewModel>();
        ProductItems = new ObservableCollection<CatalogPartItemViewModel>();
        PageSizeOptions = new ObservableCollection<string> { "10 /page", "20 /page", "50 /page" };

        SearchCommand = new AsyncRelayCommand(_ => ExecuteKeywordSearchAsync());
        ClearSearchCommand = new AsyncRelayCommand(_ => ClearSearchAndReloadCategoryAsync());
        ApplyFiltersCommand = new AsyncRelayCommand(_ => ApplySelectedFiltersAsync());
        ClearFiltersCommand = new AsyncRelayCommand(_ => ClearSelectedFiltersAsync());
        RemoveSingleFilterCommand = new RelayCommand(opt =>
        {
            if (opt is CatalogFilterOptionViewModel vm)
                vm.IsSelected = false;
        });
        ToggleFilterExpandCommand = new RelayCommand(_ => IsFilterExpanded = !IsFilterExpanded);
        ToggleSortCommand = new AsyncRelayCommand(param => ToggleSortAsync(param as string));
        PrevPageCommand = new AsyncRelayCommand(_ => ChangePageAsync(CurrentPage - 1), _ => CurrentPage > 1 && !IsLoadingProducts);
        NextPageCommand = new AsyncRelayCommand(_ => ChangePageAsync(CurrentPage + 1), _ => CurrentPage < TotalPages && !IsLoadingProducts);
        SelectSourceCommand = new AsyncRelayCommand(src => SelectSourceAsync(src as CatalogSourceItemViewModel));
        OpenMiniWindowCommand = new RelayCommand(_ => OpenMiniWindowRequested?.Invoke(this, EventArgs.Empty));
        OpenSettingsCommand = _mainViewModel.OpenSettingsCommand;
    }

    public MainViewModel MainVm => _mainViewModel;

    public List<CatalogSourceItemViewModel> AllSources { get; }

    public ObservableCollection<CatalogSourceItemViewModel> FilteredSources { get; }

    public ObservableCollection<CatalogCategoryNodeViewModel> Categories { get; }

    public ObservableCollection<CatalogFilterColumnViewModel> FilterColumns { get; }

    public ObservableCollection<CatalogFilterOptionViewModel> SelectedFilterOptions { get; }

    public ObservableCollection<CatalogPartItemViewModel> ProductItems { get; }

    public ObservableCollection<string> PageSizeOptions { get; }

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand ClearSearchCommand { get; }

    public AsyncRelayCommand ApplyFiltersCommand { get; }

    public AsyncRelayCommand ClearFiltersCommand { get; }

    public RelayCommand RemoveSingleFilterCommand { get; }

    public RelayCommand ToggleFilterExpandCommand { get; }

    public AsyncRelayCommand ToggleSortCommand { get; }

    public AsyncRelayCommand PrevPageCommand { get; }

    public AsyncRelayCommand NextPageCommand { get; }

    public AsyncRelayCommand SelectSourceCommand { get; }

    public RelayCommand OpenMiniWindowCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    public bool IsSystemSource => _selectedSourceMode == CatalogSourceMode.System;

    public string SourceFilterText
    {
        get => _sourceFilterText;
        set
        {
            if (SetProperty(ref _sourceFilterText, value))
            {
                RefreshFilteredSources();
            }
        }
    }

    public string CategoryFilterText
    {
        get => _categoryFilterText;
        set
        {
            if (SetProperty(ref _categoryFilterText, value))
            {
                ApplyCategoryTreeFilter();
            }
        }
    }

    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }

    public bool IsLoadingCategories
    {
        get => _isLoadingCategories;
        private set => SetProperty(ref _isLoadingCategories, value);
    }

    public bool IsLoadingFilters
    {
        get => _isLoadingFilters;
        private set
        {
            if (SetProperty(ref _isLoadingFilters, value))
                OnPropertyChanged(nameof(ShowEmptyFiltersMessage));
        }
    }

    public bool IsLoadingProducts
    {
        get => _isLoadingProducts;
        private set
        {
            if (SetProperty(ref _isLoadingProducts, value))
            {
                OnPropertyChanged(nameof(ShowEmptyProductsMessage));
                PrevPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowEmptyFiltersMessage => !IsLoadingFilters && FilterColumns.Count == 0 && IsSystemSource;

    public bool ShowEmptyProductsMessage => !IsLoadingProducts && ProductItems.Count == 0;

    public bool IsFilterExpanded
    {
        get => _isFilterExpanded;
        set
        {
            if (SetProperty(ref _isFilterExpanded, value))
            {
                OnPropertyChanged(nameof(FilterColumnHeight));
                OnPropertyChanged(nameof(FilterExpandChevron));
            }
        }
    }

    public double FilterColumnHeight => IsFilterExpanded ? 205.0 : 120.0;

    public string FilterExpandChevron => IsFilterExpanded ? "▲" : "▼";

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                PrevPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int TotalPages
    {
        get => _totalPages;
        private set
        {
            if (SetProperty(ref _totalPages, value))
            {
                OnPropertyChanged(nameof(PaginationSummaryText));
                PrevPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetProperty(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(PaginationSummaryText));
            }
        }
    }

    public string PaginationSummaryText => $"Total {TotalCount} Items | {TotalPages} pages";

    public string SelectedPageSizeOption
    {
        get => $"{_pageSize} /page";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                string numPart = value.Split(' ')[0];
                if (int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                    parsed != _pageSize)
                {
                    _pageSize = parsed;
                    OnPropertyChanged();
                    _ = ReloadCurrentViewAsync(needAggs: false, resetPage: true);
                }
            }
        }
    }

    public string PriceSortLabel => _sortField == CatalogSortField.Price
        ? (_sortDirection == CatalogSortDirection.Asc ? "Price ▲" : "Price ▼")
        : "Price ⬍";

    public string StockSortLabel => _sortField == CatalogSortSortStockCheck()
        ? (_sortDirection == CatalogSortDirection.Asc ? "Stock ▲" : "Stock ▼")
        : "Stock ⬍";

    public string SalesSortLabel => _sortField == CatalogSortField.Sales && _sortDirection == CatalogSortDirection.Desc
        ? "Sales ▼"
        : "Sales ▾";

    public bool IsPriceSortActive => _sortField == CatalogSortField.Price;

    public bool IsStockSortActive => _sortField == CatalogSortField.Stock;

    public bool IsSalesSortActive => _sortField == CatalogSortField.Sales;

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private CatalogSortField CatalogSortSortStockCheck() => CatalogSortField.Stock;

    /// <summary>
    /// Initializes the category tree and loads the first category on initial window open.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_rawSystemCategories.Count > 0)
            return;

        IsLoadingCategories = true;
        IsLoadingFilters = true;
        IsLoadingProducts = true;
        StatusMessage = "Loading LCSC Electronics category tree...";
        var treeSw = Stopwatch.StartNew();

        try
        {
            IReadOnlyList<CatalogCategoryNode> tree = await _catalogClient.FetchCategoryTreeAsync();
            _rawSystemCategories = tree.ToList();
            RebuildSystemCategoryViewModels();
            treeSw.Stop();
            AppendBrowserDiagnosticLog($"Category tree loaded in {treeSw.ElapsedMilliseconds} ms ({_rawSystemCategories.Count} top-level categories).");

            // Select the first subcategory (e.g., Amplifiers/Comparators -> Programmable / Variable Gain Amplifiers)
            CatalogCategoryNodeViewModel? firstParent = Categories.FirstOrDefault();
            if (firstParent is not null)
            {
                firstParent.IsExpanded = true;
                CatalogCategoryNodeViewModel target = firstParent.Children.FirstOrDefault() ?? firstParent;
                await SelectCategoryNodeAsync(target);
            }
        }
        catch (Exception ex)
        {
            treeSw.Stop();
            StatusMessage = $"Category load error ({treeSw.ElapsedMilliseconds} ms): {ex.Message}";
            AppendBrowserDiagnosticLog($"Category load error after {treeSw.ElapsedMilliseconds} ms: {ex.Message}", isError: true);
            IsLoadingFilters = false;
            IsLoadingProducts = false;
        }
        finally
        {
            IsLoadingCategories = false;
        }
    }

    public async Task SelectSourceAsync(CatalogSourceItemViewModel? sourceItem)
    {
        if (sourceItem is null)
            return;

        foreach (var s in AllSources)
            s.IsSelected = ReferenceEquals(s, sourceItem);

        _selectedSourceMode = sourceItem.Mode;
        OnPropertyChanged(nameof(IsSystemSource));
        OnPropertyChanged(nameof(ShowEmptyFiltersMessage));

        ClearSelectedFilterStateOnly();

        if (_selectedSourceMode == CatalogSourceMode.System)
        {
            RebuildSystemCategoryViewModels();
            CatalogCategoryNodeViewModel? firstParent = Categories.FirstOrDefault();
            if (firstParent is not null)
            {
                firstParent.IsExpanded = true;
                var target = firstParent.Children.FirstOrDefault() ?? firstParent;
                await SelectCategoryNodeAsync(target);
            }
        }
        else if (_selectedSourceMode == CatalogSourceMode.LocalDbLib)
        {
            RebuildLocalDbCategoryViewModels();
            var first = Categories.FirstOrDefault();
            if (first is not null)
                await SelectCategoryNodeAsync(first);
        }
        else
        {
            Categories.Clear();
            FilterColumns.Clear();
            await ReloadLocalSourceItemsAsync();
        }
    }

    public async Task SelectCategoryNodeAsync(CatalogCategoryNodeViewModel? node, bool toggleExpandIfParent = false)
    {
        if (node is null)
            return;

        if (node.HasChildren && toggleExpandIfParent)
        {
            node.IsExpanded = !node.IsExpanded;
        }

        SetSelectedCategoryFlagRecursive(Categories, node);
        _selectedCategory = node;
        _isInSearchMode = false;

        ClearSelectedFilterStateOnly();
        ResetSortState();

        await ReloadCurrentViewAsync(needAggs: true, resetPage: true);
    }

    public async Task ExecuteKeywordSearchAsync()
    {
        string kw = (SearchKeyword ?? string.Empty).Trim();
        if (_selectedSourceMode == CatalogSourceMode.System)
        {
            if (kw.Length < 2)
            {
                StatusMessage = "Please enter at least 2 characters to search.";
                return;
            }

            _isInSearchMode = true;
            ClearSelectedFilterStateOnly();
            ResetSortState();
            await ReloadCurrentViewAsync(needAggs: true, resetPage: true);
        }
        else
        {
            await ReloadLocalSourceItemsAsync();
        }
    }

    public async Task ClearSearchAndReloadCategoryAsync()
    {
        SearchKeyword = string.Empty;
        if (_isInSearchMode)
        {
            _isInSearchMode = false;
            ClearSelectedFilterStateOnly();
            await ReloadCurrentViewAsync(needAggs: true, resetPage: true);
        }
        else if (_selectedSourceMode != CatalogSourceMode.System)
        {
            await ReloadLocalSourceItemsAsync();
        }
    }

    public async Task ApplySelectedFiltersAsync()
    {
        if (!IsSystemSource)
            return;

        await ReloadCurrentViewAsync(needAggs: false, resetPage: true);
    }

    public async Task ClearSelectedFiltersAsync()
    {
        if (!IsSystemSource)
            return;

        foreach (CatalogFilterColumnViewModel col in FilterColumns)
        {
            foreach (CatalogFilterOptionViewModel opt in col.AllOptions)
            {
                opt.SetSelectedSilently(false);
            }
        }
        SelectedFilterOptions.Clear();

        await ReloadCurrentViewAsync(needAggs: false, resetPage: true);
    }

    private async Task ToggleSortAsync(string? sortKey)
    {
        if (string.IsNullOrWhiteSpace(sortKey))
            return;

        CatalogSortField targetField = sortKey.ToLowerInvariant() switch
        {
            "price" => CatalogSortField.Price,
            "stock" => CatalogSortField.Stock,
            "sales" => CatalogSortField.Sales,
            _ => CatalogSortField.None
        };

        if (targetField == CatalogSortField.None)
            return;

        if (_sortField != targetField)
        {
            _sortField = targetField;
            _sortDirection = targetField == CatalogSortField.Price
                ? CatalogSortDirection.Asc
                : CatalogSortDirection.Desc;
        }
        else
        {
            if (targetField == CatalogSortField.Sales)
            {
                _sortField = CatalogSortField.None;
                _sortDirection = CatalogSortDirection.None;
            }
            else if (targetField == CatalogSortField.Price)
            {
                if (_sortDirection == CatalogSortDirection.Asc)
                    _sortDirection = CatalogSortDirection.Desc;
                else
                {
                    _sortField = CatalogSortField.None;
                    _sortDirection = CatalogSortDirection.None;
                }
            }
            else
            {
                if (_sortDirection == CatalogSortDirection.Desc)
                    _sortDirection = CatalogSortDirection.Asc;
                else
                {
                    _sortField = CatalogSortField.None;
                    _sortDirection = CatalogSortDirection.None;
                }
            }
        }

        NotifySortProperties();

        if (IsSystemSource)
            await ReloadCurrentViewAsync(needAggs: false, resetPage: true);
        else
            await ReloadLocalSourceItemsAsync();
    }

    private async Task ChangePageAsync(int newPage)
    {
        if (newPage < 1 || newPage > TotalPages || newPage == CurrentPage)
            return;

        CurrentPage = newPage;
        await ReloadCurrentViewAsync(needAggs: false, resetPage: false);
    }

    private async Task ReloadCurrentViewAsync(bool needAggs, bool resetPage)
    {
        if (_selectedSourceMode != CatalogSourceMode.System)
        {
            await ReloadLocalSourceItemsAsync();
            return;
        }

        _queryCts?.Cancel();
        _queryCts = new CancellationTokenSource();
        CancellationToken token = _queryCts.Token;

        if (resetPage)
            CurrentPage = 1;

        if (needAggs)
        {
            IsLoadingFilters = true;
            FilterColumns.Clear();
            TotalCount = 0;
        }

        IsLoadingProducts = true;
        ProductItems.Clear();

        var request = new CatalogQueryRequest
        {
            CatalogId = _isInSearchMode ? null : _selectedCategory?.CatalogId,
            Keyword = _isInSearchMode ? SearchKeyword.Trim() : null,
            Page = CurrentPage,
            PageSize = _pageSize,
            NeedAggs = needAggs,
            SortField = _sortField,
            SortDirection = _sortDirection,
            SelectedFilters = SelectedFilterOptions.Select(o => new CatalogSelectedFilter
            {
                ColumnKey = o.ColumnKey,
                ColumnTitle = o.ColumnTitle,
                OptionName = o.Name,
                OptionValue = o.Value,
                OptionId = o.Id
            }).ToList()
        };

        string targetLabel = _isInSearchMode
            ? $"Search '{request.Keyword}'"
            : (_selectedCategory?.Title ?? "Catalog");

        StatusMessage = $"Loading {targetLabel} (Page {CurrentPage})...";
        var totalSw = Stopwatch.StartNew();

        try
        {
            CatalogQueryResult result = await _catalogClient.QueryCatalogAsync(request, token);
            if (token.IsCancellationRequested)
                return;

            var uiSw = Stopwatch.StartNew();
            TotalCount = result.TotalCount;
            TotalPages = result.TotalPages;

            if (needAggs)
            {
                FilterColumns.Clear();
                foreach (CatalogFilterColumn col in result.FilterColumns)
                {
                    FilterColumns.Add(new CatalogFilterColumnViewModel(col, OnFilterOptionToggled));
                }
            }

            ProductItems.Clear();
            var createdRows = new List<CatalogPartItemViewModel>(result.Items.Count);
            foreach (CatalogPartItem item in result.Items)
            {
                var rowVm = CreatePartItemViewModel(item);
                createdRows.Add(rowVm);
                ProductItems.Add(rowVm);
            }

            IsLoadingFilters = false;
            IsLoadingProducts = false;
            uiSw.Stop();
            totalSw.Stop();

            string sourceTag = result.IsFromCache ? "Cache" : $"API: {result.ApiElapsedMs} ms";
            string timingSummary = $"{totalSw.ElapsedMilliseconds} ms ({sourceTag}, UI: {uiSw.ElapsedMilliseconds} ms)";
            StatusMessage = $"{targetLabel} — {TotalCount:N0} parts (Page {CurrentPage}/{TotalPages}) | Loaded in {timingSummary}";
            AppendBrowserDiagnosticLog($"Catalog '{targetLabel}' Page {CurrentPage} loaded in {timingSummary} | TotalCount={TotalCount}, FilterCols={FilterColumns.Count}");

            // Enrich JLCPCB SMT stock asynchronously in the background without blocking the UI
            if (createdRows.Count > 0 && !result.IsFromCache)
            {
                _ = EnrichProductsSmtStockInBackgroundAsync(createdRows, targetLabel, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer category selection or search query
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            StatusMessage = $"Query error ({totalSw.ElapsedMilliseconds} ms): {ex.Message}";
            AppendBrowserDiagnosticLog($"Catalog '{targetLabel}' query error after {totalSw.ElapsedMilliseconds} ms: {ex.Message}", isError: true);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingFilters = false;
                IsLoadingProducts = false;
            }
        }
    }

    private async Task EnrichProductsSmtStockInBackgroundAsync(
        List<CatalogPartItemViewModel> rows,
        string targetLabel,
        CancellationToken cancellationToken)
    {
        var smtSw = Stopwatch.StartNew();
        try
        {
            var lookup = await _catalogClient.FetchJlcpcbSmtStockAsync(
                rows.Select(r => r.LcscId),
                cancellationToken);

            smtSw.Stop();
            if (cancellationToken.IsCancellationRequested || lookup.Count == 0)
                return;

            foreach (CatalogPartItemViewModel row in rows)
            {
                if (lookup.TryGetValue(row.LcscId, out var smt))
                {
                    row.UpdateJlcpcbStockAndClass(smt.Stock, smt.PartClass);
                }
            }

            AppendBrowserDiagnosticLog($"Background JLCPCB SMT stock enriched for '{targetLabel}' ({lookup.Count} parts) in {smtSw.ElapsedMilliseconds} ms.");
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void AppendBrowserDiagnosticLog(string message, bool isError = false)
    {
        _mainViewModel.AppendLog(message);
        try
        {
            string logPath = _config.BridgeLogFilePath;
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                string level = isError ? "ERROR" : "PERF";
                string line = $"[{DateTime.Now:d.M.yyyy HH:mm:ss}] [{level}] [CatalogBrowser] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, line);
            }
        }
        catch
        {
        }
    }

    private async Task ReloadLocalSourceItemsAsync()
    {
        IsLoadingFilters = false;
        IsLoadingProducts = true;
        FilterColumns.Clear();
        ProductItems.Clear();

        try
        {
            List<CatalogPartItem> items;
            string kw = (SearchKeyword ?? string.Empty).Trim();

            if (_selectedSourceMode == CatalogSourceMode.Recent)
            {
                items = (_config.RecentCatalogParts ?? new List<CatalogPartItem>())
                    .Where(p => MatchesKeyword(p, kw))
                    .ToList();
            }
            else if (_selectedSourceMode == CatalogSourceMode.Favorite)
            {
                items = (_config.FavoriteCatalogParts ?? new List<CatalogPartItem>())
                    .Where(p => MatchesKeyword(p, kw))
                    .ToList();
            }
            else
            {
                ComponentCategory? dbCat = _selectedCategory?.LocalDbCategory;
                items = await _sqliteManager.QueryStoredCatalogPartsAsync(dbCat, kw);
            }

            if (_sortField == CatalogSortField.Price)
            {
                items = (_sortDirection == CatalogSortDirection.Asc
                    ? items.OrderBy(i => i.PriceTiers.FirstOrDefault()?.UnitPriceUsd ?? decimal.MaxValue)
                    : items.OrderByDescending(i => i.PriceTiers.FirstOrDefault()?.UnitPriceUsd ?? 0m)).ToList();
            }
            else if (_sortField == CatalogSortField.Stock)
            {
                items = (_sortDirection == CatalogSortDirection.Asc
                    ? items.OrderBy(i => i.LcscStock)
                    : items.OrderByDescending(i => i.LcscStock)).ToList();
            }

            TotalCount = items.Count;
            TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / _pageSize));
            CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);

            foreach (CatalogPartItem item in items.Skip((CurrentPage - 1) * _pageSize).Take(_pageSize))
            {
                ProductItems.Add(CreatePartItemViewModel(item));
            }

            StatusMessage = $"{_selectedSourceMode} — {TotalCount} local component(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Local library error: {ex.Message}";
        }
        finally
        {
            IsLoadingProducts = false;
        }
    }

    private CatalogPartItemViewModel CreatePartItemViewModel(CatalogPartItem item)
    {
        bool isFav = _config.IsFavoritePart(item.LcscId);
        return new CatalogPartItemViewModel(
            item,
            isFav,
            ExecutePartImportActionAsync,
            ToggleFavoritePart,
            OpenExternalUrl);
    }

    private async Task ExecutePartImportActionAsync(CatalogPartItemViewModel itemVm, BridgeAction action)
    {
        itemVm.IsPlacing = true;
        StatusMessage = $"Importing {itemVm.LcscId} ({itemVm.Mpn}) into Altium Designer...";

        try
        {
            _config.RecordRecentPart(itemVm.Model);
            var preview = await _mainViewModel.FetchPartPreviewAsync(itemVm.LcscId);
            if (preview is null)
            {
                StatusMessage = $"Failed to fetch metadata for {itemVm.LcscId}.";
                return;
            }

            if (action == BridgeAction.PlaceOrLoad)
            {
                // Minimize Component Browser before dispatching to Altium so the schematic canvas is unobstructed
                MinimizeForPlacementRequested?.Invoke(this, EventArgs.Empty);
            }

            var result = await _mainViewModel.ExecuteImportAsync(action);
            if (result is not null)
            {
                StatusMessage = result.SummaryMessage;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import error ({itemVm.LcscId}): {ex.Message}";
        }
        finally
        {
            itemVm.IsPlacing = false;
        }
    }

    private void ToggleFavoritePart(CatalogPartItemViewModel itemVm)
    {
        bool nowFav = _config.ToggleFavoritePart(itemVm.Model);
        itemVm.IsFavorite = nowFav;
        StatusMessage = nowFav
            ? $"Added {itemVm.Mpn} ({itemVm.LcscId}) to Favorites."
            : $"Removed {itemVm.Mpn} ({itemVm.LcscId}) from Favorites.";

        if (_selectedSourceMode == CatalogSourceMode.Favorite && !nowFav)
        {
            ProductItems.Remove(itemVm);
            TotalCount = Math.Max(0, TotalCount - 1);
        }
    }

    private void OnFilterOptionToggled(CatalogFilterOptionViewModel optionVm)
    {
        if (optionVm.IsSelected)
        {
            if (!SelectedFilterOptions.Contains(optionVm))
                SelectedFilterOptions.Add(optionVm);
        }
        else
        {
            SelectedFilterOptions.Remove(optionVm);
        }
    }

    private void ClearSelectedFilterStateOnly()
    {
        SelectedFilterOptions.Clear();
    }

    private void ResetSortState()
    {
        _sortField = CatalogSortField.None;
        _sortDirection = CatalogSortDirection.None;
        NotifySortProperties();
    }

    private void NotifySortProperties()
    {
        OnPropertyChanged(nameof(PriceSortLabel));
        OnPropertyChanged(nameof(StockSortLabel));
        OnPropertyChanged(nameof(SalesSortLabel));
        OnPropertyChanged(nameof(IsPriceSortActive));
        OnPropertyChanged(nameof(IsStockSortActive));
        OnPropertyChanged(nameof(IsSalesSortActive));
    }

    private void RebuildSystemCategoryViewModels()
    {
        ApplyCategoryTreeFilter();
    }

    private void RebuildLocalDbCategoryViewModels()
    {
        Categories.Clear();
        Categories.Add(new CatalogCategoryNodeViewModel
        {
            CatalogId = 0,
            Title = "All Local DbLib Parts",
            LocalDbCategory = null,
            IsSelected = true
        });

        foreach (ComponentCategory cat in SqliteDbLibManager.AllCategories)
        {
            Categories.Add(new CatalogCategoryNodeViewModel
            {
                CatalogId = (int)cat + 1,
                Title = SqliteDbLibManager.GetSafeTableName(cat),
                LocalDbCategory = cat
            });
        }
    }

    private void RefreshFilteredSources()
    {
        FilteredSources.Clear();
        string q = (_sourceFilterText ?? string.Empty).Trim();
        foreach (var src in AllSources)
        {
            if (string.IsNullOrEmpty(q) || src.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredSources.Add(src);
        }
    }

    private void ApplyCategoryTreeFilter()
    {
        if (_selectedSourceMode != CatalogSourceMode.System)
        {
            string localQuery = (_categoryFilterText ?? string.Empty).Trim();
            foreach (CatalogCategoryNodeViewModel node in Categories)
            {
                node.IsVisible = string.IsNullOrEmpty(localQuery) ||
                                 node.Title.Contains(localQuery, StringComparison.OrdinalIgnoreCase);
            }
            return;
        }

        string q = (_categoryFilterText ?? string.Empty).Trim();
        int? selectedId = _selectedCategory?.CatalogId;
        var filteredNodes = new List<CatalogCategoryNodeViewModel>(_rawSystemCategories.Count);

        foreach (CatalogCategoryNode parent in _rawSystemCategories)
        {
            bool hasFilter = !string.IsNullOrEmpty(q);
            bool parentMatches = !hasFilter || parent.Title.Contains(q, StringComparison.OrdinalIgnoreCase);

            var matchingChildren = new List<CatalogCategoryNodeViewModel>();
            bool containsSelectedChild = false;

            foreach (CatalogCategoryNode child in parent.Children)
            {
                bool childMatches = !hasFilter || parentMatches || child.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!childMatches)
                    continue;

                bool isChildSelected = !hasFilter && selectedId.HasValue && child.CatalogId == selectedId.Value;
                if (isChildSelected)
                    containsSelectedChild = true;

                matchingChildren.Add(new CatalogCategoryNodeViewModel
                {
                    CatalogId = child.CatalogId,
                    ParentId = child.ParentId,
                    Title = child.Title,
                    IsSelected = isChildSelected
                });
            }

            if (hasFilter && !parentMatches && matchingChildren.Count == 0)
                continue;

            bool isParentSelected = !hasFilter && selectedId.HasValue && parent.CatalogId == selectedId.Value;
            var parentVm = new CatalogCategoryNodeViewModel
            {
                CatalogId = parent.CatalogId,
                ParentId = parent.ParentId,
                Title = parent.Title,
                IsSelected = isParentSelected,
                IsExpanded = (hasFilter && matchingChildren.Count > 0) || containsSelectedChild
            };

            foreach (var childVm in matchingChildren)
            {
                parentVm.Children.Add(childVm);
            }

            filteredNodes.Add(parentVm);
        }

        Categories.Clear();
        foreach (var node in filteredNodes)
        {
            Categories.Add(node);
        }
    }

    private static void SetSelectedCategoryFlagRecursive(
        IEnumerable<CatalogCategoryNodeViewModel> nodes,
        CatalogCategoryNodeViewModel target)
    {
        foreach (var n in nodes)
        {
            n.IsSelected = ReferenceEquals(n, target);
            if (n.Children.Count > 0)
                SetSelectedCategoryFlagRecursive(n.Children, target);
        }
    }

    private static bool MatchesKeyword(CatalogPartItem item, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return item.LcscId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.Mpn.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.Manufacturer.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               item.Package.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open link: {ex.Message}";
        }
    }
}
