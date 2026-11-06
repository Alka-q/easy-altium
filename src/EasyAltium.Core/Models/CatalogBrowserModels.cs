using System.Globalization;

namespace EasyAltium.Core.Models;

/// <summary>
/// Left-hand source selector in the EasyEDA-style Component Browser.
/// </summary>
public enum CatalogSourceMode
{
    System,
    Recent,
    Favorite,
    LocalDbLib
}

/// <summary>
/// Sort field for LCSC / EasyEDA catalog queries.
/// </summary>
public enum CatalogSortField
{
    None,
    Price,
    Stock,
    Sales
}

/// <summary>
/// Sort direction for LCSC / EasyEDA catalog queries.
/// </summary>
public enum CatalogSortDirection
{
    None,
    Asc,
    Desc
}

/// <summary>
/// Represents a category or subcategory in the EasyEDA / LCSC catalog tree.
/// </summary>
public sealed class CatalogCategoryNode
{
    public int CatalogId { get; set; }

    public int ParentId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string TitleCn { get; set; } = string.Empty;

    public List<CatalogCategoryNode> Children { get; set; } = new();
}

/// <summary>
/// Single selectable parameter value inside a dynamic filter column (e.g., "SOIC-8", "ADI", "0dB~20dB").
/// </summary>
public sealed class CatalogFilterOption
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string? Id { get; set; }
}

/// <summary>
/// Dynamic parametric filter column returned for the active category or search query
/// (e.g., Package, Manufacturer, Features, Gain, Dual Supply, Capacitance, Voltage Rating).
/// </summary>
public sealed class CatalogFilterColumn
{
    public string Title { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public List<CatalogFilterOption> Options { get; set; } = new();
}

/// <summary>
/// Represents a user-selected filter value to apply to the catalog query.
/// </summary>
public sealed class CatalogSelectedFilter
{
    public string ColumnKey { get; set; } = string.Empty;

    public string ColumnTitle { get; set; } = string.Empty;

    public string OptionName { get; set; } = string.Empty;

    public string OptionValue { get; set; } = string.Empty;

    public string? OptionId { get; set; }
}

/// <summary>
/// Quantity price break tier (e.g., 1+ : $11.5609).
/// </summary>
public sealed class CatalogPriceTier
{
    public int MinQuantity { get; set; }

    public decimal UnitPriceUsd { get; set; }

    public string LabelText => $"{MinQuantity}+ : ";

    public string PriceText => $"${UnitPriceUsd.ToString("0.0000", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Represents a single component row inside the EasyEDA-style Component Browser.
/// </summary>
public sealed class CatalogPartItem
{
    public string LcscId { get; set; } = string.Empty;

    public string Mpn { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string Package { get; set; } = string.Empty;

    public string FootprintTitle { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string LcscPartName { get; set; } = string.Empty;

    public string DatasheetUrl { get; set; } = string.Empty;

    public string ProductUrl { get; set; } = string.Empty;

    public string ImageUrl { get; set; } = string.Empty;

    public int LcscStock { get; set; }

    public int JlcpcbStock { get; set; }

    public string JlcpcbPartClass { get; set; } = "Extended Part";

    public List<CatalogPriceTier> PriceTiers { get; set; } = new();
}

/// <summary>
/// Encapsulates category, keyword, parametric filter, sort, and pagination parameters.
/// </summary>
public sealed class CatalogQueryRequest
{
    public int? CatalogId { get; set; }

    public string? Keyword { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 10;

    public bool NeedAggs { get; set; } = true;

    public List<CatalogSelectedFilter> SelectedFilters { get; set; } = new();

    public CatalogSortField SortField { get; set; } = CatalogSortField.None;

    public CatalogSortDirection SortDirection { get; set; } = CatalogSortDirection.None;
}

/// <summary>
/// Result returned from EasyEDA Pro / LCSC catalog endpoints.
/// </summary>
public sealed class CatalogQueryResult
{
    public int TotalCount { get; set; }

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = 10;

    public int TotalPages => PageSize > 0
        ? Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize))
        : 1;

    public bool IsFromCache { get; set; }

    public long ApiElapsedMs { get; set; }

    public List<CatalogFilterColumn> FilterColumns { get; set; } = new();

    public List<CatalogPartItem> Items { get; set; } = new();
}
