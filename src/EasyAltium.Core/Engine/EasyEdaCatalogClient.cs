using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EasyAltium.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyAltium.Core.Engine;

/// <summary>
/// Queries the EasyEDA Pro &amp; LCSC catalog endpoints (/api/eda/catalog/tree, /api/eda/product/list,
/// /api/eda/product/search, and /api/components/getSmtPartInfo) to power the EasyEDA-style Component Browser.
/// </summary>
public sealed class EasyEdaCatalogClient
{
    public const string CatalogTreeUrl = "https://pro.easyeda.com/api/eda/catalog/tree?path=1";
    public const string ProductListUrl = "https://pro.easyeda.com/api/eda/product/list";
    public const string ProductSearchUrl = "https://pro.easyeda.com/api/eda/product/search";
    public const string SmtPartInfoUrl = "https://pro.easyeda.com/api/components/getSmtPartInfo";

    private readonly HttpClient _httpClient;
    private readonly ILogger<EasyEdaCatalogClient> _logger;
    private readonly ConcurrentDictionary<string, CatalogQueryResult> _categoryResponseCache = new(StringComparer.Ordinal);
    private IReadOnlyList<CatalogCategoryNode>? _cachedCategories;

    public EasyEdaCatalogClient(HttpClient? httpClient = null, ILogger<EasyEdaCatalogClient>? logger = null)
    {
        _logger = logger ?? NullLogger<EasyEdaCatalogClient>.Instance;
        if (httpClient is not null)
        {
            _httpClient = httpClient;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 8
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) EasyAltium/1.0");
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    public void ClearCache()
    {
        _categoryResponseCache.Clear();
    }

    /// <summary>
    /// Fetches the hierarchical LCSC / EasyEDA category tree (52 top-level categories and subcategories).
    /// </summary>
    public async Task<IReadOnlyList<CatalogCategoryNode>> FetchCategoryTreeAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cachedCategories is { Count: > 0 })
            return _cachedCategories;

        using var response = await _httpClient.GetAsync(CatalogTreeUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var tree = ParseCategoryTreeJson(doc.RootElement);
        if (tree.Count > 0)
            _cachedCategories = tree;

        return tree;
    }

    /// <summary>
    /// Parses the JSON response from /api/eda/catalog/tree into hierarchical <see cref="CatalogCategoryNode"/> items.
    /// </summary>
    public static List<CatalogCategoryNode> ParseCategoryTreeJson(JsonElement root)
    {
        var nodes = new List<CatalogCategoryNode>();
        if (!root.TryGetProperty("result", out JsonElement resultArr) ||
            resultArr.ValueKind != JsonValueKind.Array)
        {
            return nodes;
        }

        foreach (JsonElement item in resultArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            int catalogId = TryReadInt(item, "catalogId");
            if (catalogId <= 0)
                continue;

            string titleEn = TryReadString(item, "catalogNameEn");
            string titleCn = TryReadString(item, "catalogName");
            int parentId = TryReadInt(item, "parentId", 1);

            var parentNode = new CatalogCategoryNode
            {
                CatalogId = catalogId,
                ParentId = parentId,
                Title = !string.IsNullOrWhiteSpace(titleEn) ? titleEn : titleCn,
                TitleCn = titleCn
            };

            if (item.TryGetProperty("sonList", out JsonElement sonList) &&
                sonList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in sonList.EnumerateArray())
                {
                    if (child.ValueKind != JsonValueKind.Object)
                        continue;

                    int childId = TryReadInt(child, "catalogId");
                    if (childId <= 0)
                        continue;

                    string childTitleEn = TryReadString(child, "catalogNameEn");
                    string childTitleCn = TryReadString(child, "catalogName");

                    parentNode.Children.Add(new CatalogCategoryNode
                    {
                        CatalogId = childId,
                        ParentId = TryReadInt(child, "parentId", catalogId),
                        Title = !string.IsNullOrWhiteSpace(childTitleEn) ? childTitleEn : childTitleCn,
                        TitleCn = childTitleCn
                    });
                }
            }

            nodes.Add(parentNode);
        }

        return nodes;
    }

    /// <summary>
    /// Queries either /api/eda/product/list (when browsing by category) or /api/eda/product/search
    /// (when searching by keyword) with dynamic parametric filters and sorting.
    /// </summary>
    public async Task<CatalogQueryResult> QueryCatalogAsync(
        CatalogQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Dictionary<string, string> queryParams = BuildFilterAndSortParameters(request);
        bool isKeywordSearch = !string.IsNullOrWhiteSpace(request.Keyword) && request.Keyword.Trim().Length >= 2;
        bool isCacheableCategoryLoad = !isKeywordSearch &&
                                       request.CatalogId.HasValue &&
                                       request.Page == 1 &&
                                       request.NeedAggs &&
                                       request.SelectedFilters.Count == 0 &&
                                       request.SortField == CatalogSortField.None;

        string cacheKey = isCacheableCategoryLoad && request.CatalogId.HasValue
            ? $"cat:{request.CatalogId.Value}:size:{request.PageSize}"
            : string.Empty;

        if (isCacheableCategoryLoad && _categoryResponseCache.TryGetValue(cacheKey, out CatalogQueryResult? cached))
        {
            return new CatalogQueryResult
            {
                TotalCount = cached.TotalCount,
                CurrentPage = cached.CurrentPage,
                PageSize = cached.PageSize,
                IsFromCache = true,
                ApiElapsedMs = 0,
                FilterColumns = cached.FilterColumns,
                Items = cached.Items
            };
        }

        var apiSw = Stopwatch.StartNew();
        HttpResponseMessage response;
        if (isKeywordSearch)
        {
            queryParams["keyword"] = request.Keyword!.Trim();
            queryParams["path"] = "1";
            using var formContent = new FormUrlEncodedContent(queryParams);
            response = await _httpClient.PostAsync(ProductSearchUrl, formContent, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string url = BuildUrlWithQueryString(ProductListUrl, queryParams);
            response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            apiSw.Stop();

            CatalogQueryResult parsed = ParseCatalogQueryResponseJson(doc.RootElement, request.Page, request.PageSize);
            parsed.IsFromCache = false;
            parsed.ApiElapsedMs = apiSw.ElapsedMilliseconds;

            if (isCacheableCategoryLoad && parsed.TotalCount > 0)
            {
                _categoryResponseCache[cacheKey] = parsed;
            }

            return parsed;
        }
    }

    /// <summary>
    /// Builds the EasyEDA Pro query/form parameter dictionary from <see cref="CatalogQueryRequest"/>.
    /// </summary>
    public static Dictionary<string, string> BuildFilterAndSortParameters(CatalogQueryRequest request)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["currPage"] = Math.Max(1, request.Page).ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = Math.Clamp(request.PageSize, 1, 150).ToString(CultureInfo.InvariantCulture)
        };

        if (request.NeedAggs)
        {
            dict["needAggs"] = "true";
        }

        if (request.CatalogId.HasValue && request.CatalogId.Value > 0)
        {
            dict["catalog"] = request.CatalogId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (request.SelectedFilters is { Count: > 0 })
        {
            var packages = new List<string>();
            var brands = new List<string>();
            var brandIds = new List<string>();
            var categories = new List<string>();
            var technicalParams = new List<string>();

            foreach (CatalogSelectedFilter filter in request.SelectedFilters)
            {
                if (string.IsNullOrWhiteSpace(filter.OptionValue))
                    continue;

                string key = filter.ColumnKey?.Trim() ?? string.Empty;
                if (string.Equals(key, "Package", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "queryProductStandard", StringComparison.OrdinalIgnoreCase))
                {
                    packages.Add(filter.OptionValue);
                }
                else if (string.Equals(key, "Manufacturer", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "queryProductGradePlateId", StringComparison.OrdinalIgnoreCase))
                {
                    brands.Add(filter.OptionValue);
                    if (!string.IsNullOrWhiteSpace(filter.OptionId))
                        brandIds.Add(filter.OptionId!);
                }
                else if (string.Equals(key, "Categories", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "queryProductTypeCode", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(filter.OptionValue);
                }
                else if (!string.IsNullOrWhiteSpace(key))
                {
                    technicalParams.Add($"{filter.OptionValue}@{key}");
                }
            }

            if (categories.Count > 0)
                dict["catalog"] = string.Join("$", categories);

            if (packages.Count > 0)
                dict["standard"] = string.Join("$", packages);

            if (brands.Count > 0)
                dict["brand"] = string.Join("$", brands);

            if (brandIds.Count > 0)
                dict["brandId"] = string.Join("$", brandIds);

            if (technicalParams.Count > 0)
                dict["param"] = string.Join("$", technicalParams);
        }

        if (request.SortField != CatalogSortField.None &&
            request.SortDirection != CatalogSortDirection.None)
        {
            string sortField = request.SortField switch
            {
                CatalogSortField.Price => "price",
                CatalogSortField.Stock => "stock",
                CatalogSortField.Sales => "sales",
                _ => string.Empty
            };

            string sortType = request.SortDirection == CatalogSortDirection.Asc ? "asc" : "desc";
            if (!string.IsNullOrEmpty(sortField))
            {
                dict["sortField"] = sortField;
                dict["sortType"] = sortType;

                int sortSign = (request.SortField, request.SortDirection) switch
                {
                    (CatalogSortField.Price, CatalogSortDirection.Asc) => 1,
                    (CatalogSortField.Price, CatalogSortDirection.Desc) => 2,
                    (CatalogSortField.Stock, CatalogSortDirection.Desc) => 8,
                    (CatalogSortField.Stock, CatalogSortDirection.Asc) => 9,
                    (CatalogSortField.Sales, _) => 6,
                    _ => 0
                };

                if (sortSign > 0)
                    dict["querySortBySign"] = sortSign.ToString(CultureInfo.InvariantCulture);
            }
        }

        return dict;
    }

    /// <summary>
    /// Parses the JSON response from /api/eda/product/list or /api/eda/product/search.
    /// </summary>
    public static CatalogQueryResult ParseCatalogQueryResponseJson(
        JsonElement root,
        int currentPage = 1,
        int pageSize = 10)
    {
        var queryResult = new CatalogQueryResult
        {
            CurrentPage = Math.Max(1, currentPage),
            PageSize = Math.Max(1, pageSize)
        };

        if (!root.TryGetProperty("result", out JsonElement res) ||
            res.ValueKind != JsonValueKind.Object)
        {
            return queryResult;
        }

        queryResult.TotalCount = TryReadInt(res, "total", 0);

        // 1. Parse dynamic parametric filter columns (paramList)
        if (res.TryGetProperty("paramList", out JsonElement paramList) &&
            paramList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement paramCol in paramList.EnumerateArray())
            {
                if (paramCol.ValueKind != JsonValueKind.Object)
                    continue;

                string paramName = TryReadString(paramCol, "parameterName");
                string paramCode = TryReadString(paramCol, "parameterCode");
                if (string.IsNullOrWhiteSpace(paramName))
                    continue;

                var column = new CatalogFilterColumn
                {
                    Title = paramName,
                    Key = !string.IsNullOrWhiteSpace(paramCode) ? paramCode : paramName
                };

                var idList = new List<string>();
                if (paramCol.TryGetProperty("parameterIdList", out JsonElement idsEl) &&
                    idsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement idItem in idsEl.EnumerateArray())
                    {
                        idList.Add(idItem.ValueKind == JsonValueKind.String
                            ? (idItem.GetString() ?? string.Empty)
                            : idItem.ToString());
                    }
                }

                if (paramCol.TryGetProperty("parameterValueList", out JsonElement valsEl) &&
                    valsEl.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (JsonElement valItem in valsEl.EnumerateArray())
                    {
                        string rawVal = valItem.ValueKind == JsonValueKind.String
                            ? (valItem.GetString() ?? string.Empty)
                            : valItem.ToString();

                        if (!string.IsNullOrWhiteSpace(rawVal))
                        {
                            string[] parts = rawVal.Split('$', 2);
                            string optName = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : rawVal;
                            string optValue = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : rawVal;
                            string? optId = idx < idList.Count && !string.IsNullOrWhiteSpace(idList[idx]) ? idList[idx] : null;

                            column.Options.Add(new CatalogFilterOption
                            {
                                Name = optName,
                                Value = optValue,
                                Id = optId
                            });
                        }
                        idx++;
                    }
                }

                if (column.Options.Count > 0)
                    queryResult.FilterColumns.Add(column);
            }
        }

        // 2. Parse component items (productList)
        if (res.TryGetProperty("productList", out JsonElement prodList) &&
            prodList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement prod in prodList.EnumerateArray())
            {
                if (prod.ValueKind != JsonValueKind.Object)
                    continue;

                string lcscId = TryReadString(prod, "number").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(lcscId))
                    lcscId = TryReadString(prod, "code").Trim().ToUpperInvariant();

                if (string.IsNullOrWhiteSpace(lcscId))
                    continue;

                string mpn = TryReadString(prod, "mpn");
                string pkg = TryReadString(prod, "package");
                string mfr = TryReadString(prod, "manufacturer");
                int lcscStock = TryReadInt(prod, "stock", TryReadInt(prod, "stockNumber", 0));
                string relUrl = TryReadString(prod, "url");
                string jlcClass = TryReadString(prod, "JLCPCB Part Class");

                var item = new CatalogPartItem
                {
                    LcscId = lcscId,
                    Mpn = !string.IsNullOrWhiteSpace(mpn) ? mpn : lcscId,
                    Package = pkg,
                    FootprintTitle = pkg,
                    Manufacturer = mfr,
                    LcscStock = lcscStock,
                    JlcpcbStock = lcscStock,
                    JlcpcbPartClass = !string.IsNullOrWhiteSpace(jlcClass) ? jlcClass : "Extended Part",
                    ProductUrl = BuildFullLcscUrl(relUrl, lcscId),
                    ImageUrl = ExtractBestProductImageUrl(prod),
                    PriceTiers = ExtractPriceTiers(prod)
                };

                // Extract device_info.attributes if present (Footprint, Datasheet, LCSC Part Name, Description)
                if (prod.TryGetProperty("device_info", out JsonElement devInfo) &&
                    devInfo.ValueKind == JsonValueKind.Object)
                {
                    string devDesc = TryReadString(devInfo, "Description");
                    if (string.IsNullOrWhiteSpace(devDesc))
                        devDesc = TryReadString(devInfo, "description");
                    if (!string.IsNullOrWhiteSpace(devDesc))
                        item.Description = devDesc;

                    if (devInfo.TryGetProperty("attributes", out JsonElement attrs) &&
                        attrs.ValueKind == JsonValueKind.Object)
                    {
                        string fpAttr = TryReadString(attrs, "Supplier Footprint");
                        if (!string.IsNullOrWhiteSpace(fpAttr))
                            item.FootprintTitle = fpAttr;

                        string dsUrl = TryReadString(attrs, "Datasheet");
                        if (!string.IsNullOrWhiteSpace(dsUrl))
                            item.DatasheetUrl = dsUrl;

                        string lcscPartName = TryReadString(attrs, "LCSC Part Name");
                        if (!string.IsNullOrWhiteSpace(lcscPartName))
                            item.LcscPartName = lcscPartName;

                        string attrClass = TryReadString(attrs, "JLCPCB Part Class");
                        if (!string.IsNullOrWhiteSpace(attrClass))
                            item.JlcpcbPartClass = attrClass;
                    }
                }

                if (string.IsNullOrWhiteSpace(item.Description))
                    item.Description = !string.IsNullOrWhiteSpace(item.LcscPartName) ? item.LcscPartName : $"{item.Manufacturer} {item.Mpn} ({item.Package})";

                if (string.IsNullOrWhiteSpace(item.DatasheetUrl))
                    item.DatasheetUrl = item.ProductUrl;

                queryResult.Items.Add(item);
            }
        }

        return queryResult;
    }

    /// <summary>
    /// Fetches real-time JLCPCB SMT stock and part class (Basic / Extended) for the given LCSC part codes.
    /// Can be called in the background after initial UI rendering so it never blocks category switching.
    /// </summary>
    public async Task<Dictionary<string, (int Stock, string PartClass)>> FetchJlcpcbSmtStockAsync(
        IEnumerable<string> lcscCodes,
        CancellationToken cancellationToken = default)
    {
        var lookup = new Dictionary<string, (int Stock, string PartClass)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string[] codes = lcscCodes
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (codes.Length == 0)
                return lookup;

            string payloadJson = JsonSerializer.Serialize(new
            {
                numbers = codes,
                path = "1"
            });

            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(SmtPartInfoUrl, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return lookup;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("result", out JsonElement resArr) ||
                resArr.ValueKind != JsonValueKind.Array)
            {
                return lookup;
            }

            foreach (JsonElement smtItem in resArr.EnumerateArray())
            {
                if (smtItem.ValueKind != JsonValueKind.Object)
                    continue;

                string code = TryReadString(smtItem, "component_code");
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                int onSale = TryReadInt(smtItem, "onSale", 1);
                int stockNum = onSale == 0 ? 0 : TryReadInt(smtItem, "stock_num", 0);
                string pasteType = TryReadString(smtItem, "paste_type");
                string partClass = string.Equals(pasteType, "base", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(pasteType, "Basic Part", StringComparison.OrdinalIgnoreCase)
                    ? "Basic Part"
                    : "Extended Part";

                lookup[code] = (stockNum, partClass);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "JLCPCB SMT stock enrichment skipped.");
        }

        return lookup;
    }

    private static string ExtractBestProductImageUrl(JsonElement prod)
    {
        if (!prod.TryGetProperty("image", out JsonElement imgEl))
            return string.Empty;

        if (imgEl.ValueKind == JsonValueKind.String)
            return imgEl.GetString() ?? string.Empty;

        if (imgEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement elem in imgEl.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.String)
                {
                    string? s = elem.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
                else if (elem.ValueKind == JsonValueKind.Object)
                {
                    foreach (string key in new[] { "224x224", "96x96", "900x900" })
                    {
                        string url = TryReadString(elem, key);
                        if (!string.IsNullOrWhiteSpace(url))
                            return url;
                    }
                }
            }
        }

        return string.Empty;
    }

    private static List<CatalogPriceTier> ExtractPriceTiers(JsonElement prod)
    {
        var tiers = new List<CatalogPriceTier>();

        if (prod.TryGetProperty("price", out JsonElement priceArr) &&
            priceArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tierEl in priceArr.EnumerateArray())
            {
                if (tierEl.ValueKind == JsonValueKind.Array && tierEl.GetArrayLength() >= 2)
                {
                    int qty = tierEl[0].ValueKind == JsonValueKind.Number && tierEl[0].TryGetInt32(out int q)
                        ? q
                        : (int.TryParse(tierEl[0].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pq) ? pq : 1);

                    string priceStr = tierEl[1].ValueKind == JsonValueKind.String
                        ? (tierEl[1].GetString() ?? string.Empty)
                        : tierEl[1].ToString();

                    if (decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal usdPrice) &&
                        usdPrice > 0)
                    {
                        tiers.Add(new CatalogPriceTier
                        {
                            MinQuantity = qty,
                            UnitPriceUsd = usdPrice
                        });
                    }
                }
            }
        }

        return tiers.Take(4).ToList();
    }

    private static string BuildFullLcscUrl(string relativeOrFullUrl, string lcscId)
    {
        if (string.IsNullOrWhiteSpace(relativeOrFullUrl))
            return $"https://www.lcsc.com/search?q={lcscId}";

        if (relativeOrFullUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            relativeOrFullUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return relativeOrFullUrl;
        }

        return $"https://www.lcsc.com/{relativeOrFullUrl.TrimStart('/')}";
    }

    private static string BuildUrlWithQueryString(string baseUrl, Dictionary<string, string> queryParams)
    {
        if (queryParams.Count == 0)
            return baseUrl;

        var sb = new StringBuilder(baseUrl);
        sb.Append('?');
        bool first = true;
        foreach (var kvp in queryParams)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
                continue;

            if (!first)
                sb.Append('&');
            first = false;

            sb.Append(Uri.EscapeDataString(kvp.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kvp.Value));
        }
        return sb.ToString();
    }

    private static string TryReadString(JsonElement obj, string propName)
    {
        if (obj.TryGetProperty(propName, out JsonElement el))
        {
            return el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? string.Empty).Trim()
                : el.ToString().Trim();
        }
        return string.Empty;
    }

    private static int TryReadInt(JsonElement obj, string propName, int defaultValue = 0)
    {
        if (obj.TryGetProperty(propName, out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int val))
                return val;
            if (el.ValueKind == JsonValueKind.String &&
                int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;
        }
        return defaultValue;
    }
}
