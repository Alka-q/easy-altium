using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using EasyAltium.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyAltium.Core.Engine;

/// <summary>
/// LCSC (wmsc.lcsc.com) ve EasyEDA/JLCPCB (easyeda.com/api/products) uç noktalarından
/// bileşen meta verilerini (MPN, Üretici, Kılıf, Stok, Fiyat, Basic/Extended, Sembol/Footprint adları) çeker.
/// </summary>
public sealed class LcscApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LcscApiClient> _logger;

    public LcscApiClient(HttpClient? httpClient = null, ILogger<LcscApiClient>? logger = null)
    {
        _logger = logger ?? NullLogger<LcscApiClient>.Instance;
        if (httpClient is not null)
        {
            _httpClient = httpClient;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("EasyAltiumLoader", "1.0"));
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    /// <summary>
    /// LCSC parça koduna (Örn: C2040) ait tüm meta verileri asenkron olarak sorgular.
    /// </summary>
    public async Task<LcscPartMetadata> FetchPartMetadataAsync(
        string lcscId,
        CancellationToken cancellationToken = default)
    {
        if (!LcscPartMetadata.IsValidLcscId(lcscId))
            throw new ArgumentException($"Geçersiz LCSC parça kodu formatı: '{lcscId}'. Beklenen format: ^C[1-9]\\d{{2,}}$", nameof(lcscId));

        string normalizedId = lcscId.Trim().ToUpperInvariant();
        var metadata = new LcscPartMetadata
        {
            LcscId = normalizedId,
            LibRef = normalizedId,
            Comment = normalizedId
        };

        // 1. EasyEDA Component API sorgusu (Sembol, Footprint, 3D UUID, JLCPCB Basic/Extended, c_para)
        await TryEnrichFromEasyEdaApiAsync(metadata, cancellationToken).ConfigureAwait(false);

        // 2. LCSC WMSSC Product Detail API sorgusu (Gerçek zamanlı Stok, Birim Fiyat, Kategori, Datasheet)
        await TryEnrichFromLcscProductApiAsync(metadata, cancellationToken).ConfigureAwait(false);

        // 3. Eksik alanlar için akıllı varsayılan tamamlama ve kategori sınıflandırması
        FinalizeMetadataDefaults(metadata);

        return metadata;
    }

    private async Task TryEnrichFromEasyEdaApiAsync(
        LcscPartMetadata metadata,
        CancellationToken cancellationToken)
    {
        string url = $"https://easyeda.com/api/products/{metadata.LcscId}/components?version=6.5.46";
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("EasyEDA API HTTP {StatusCode} döndürdü ({LcscId}).", (int)response.StatusCode, metadata.LcscId);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // Çok parçalı (multi-part) sembol kontrolü
            if (result.TryGetProperty("subParts", out JsonElement subParts) &&
                subParts.ValueKind == JsonValueKind.Array)
            {
                int subCount = subParts.GetArrayLength();
                if (subCount > 1)
                    metadata.PartCount = subCount;
            }

            if (result.TryGetProperty("title", out JsonElement titleProp) &&
                titleProp.ValueKind == JsonValueKind.String)
            {
                string title = titleProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    metadata.Mpn = title;
                    metadata.LibRef = SanitizeAltiumIdentifier(title);
                }
            }

            if (result.TryGetProperty("description", out JsonElement descProp) &&
                descProp.ValueKind == JsonValueKind.String)
            {
                metadata.Description = descProp.GetString() ?? string.Empty;
            }

            // dataStr.head.c_para üzerinden detaylı parametreleri oku
            if (result.TryGetProperty("dataStr", out JsonElement dataStr) &&
                dataStr.ValueKind == JsonValueKind.Object &&
                dataStr.TryGetProperty("head", out JsonElement head) &&
                head.ValueKind == JsonValueKind.Object &&
                head.TryGetProperty("c_para", out JsonElement cPara) &&
                cPara.ValueKind == JsonValueKind.Object)
            {
                ReadEasyEdaCPara(metadata, cPara);
            }

            // packageDetail.title -> FootprintRef
            if (result.TryGetProperty("packageDetail", out JsonElement pkgDetail) &&
                pkgDetail.ValueKind == JsonValueKind.Object)
            {
                if (pkgDetail.TryGetProperty("title", out JsonElement pkgTitle) &&
                    pkgTitle.ValueKind == JsonValueKind.String)
                {
                    string fpName = pkgTitle.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(fpName))
                        metadata.FootprintRef = SanitizeAltiumIdentifier(fpName);
                }

                // 3D Model UUID ve 3D Model Dönüş Açıları (SVGNODE -> attrs.c_rotation)
                if (pkgDetail.TryGetProperty("dataStr", out JsonElement pkgDataStr) &&
                    pkgDataStr.ValueKind == JsonValueKind.Object)
                {
                    if (pkgDataStr.TryGetProperty("head", out JsonElement pkgHead) &&
                        pkgHead.ValueKind == JsonValueKind.Object &&
                        pkgHead.TryGetProperty("c_para", out JsonElement pkgCPara) &&
                        pkgCPara.ValueKind == JsonValueKind.Object &&
                        pkgCPara.TryGetProperty("3DModel", out JsonElement modelProp) &&
                        modelProp.ValueKind == JsonValueKind.String)
                    {
                        metadata.StepModelUuid = modelProp.GetString() ?? string.Empty;
                    }

                    if (pkgDataStr.TryGetProperty("shape", out JsonElement pkgShapes) &&
                        pkgShapes.ValueKind == JsonValueKind.Array)
                    {
                        ReadEasyEdaOutline3DFromShapes(metadata, pkgShapes);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "EasyEDA API sorgusu sırasında hata oluştu ({LcscId}).", metadata.LcscId);
        }
    }

    /// <summary>
    /// EasyEDA packageDetail.dataStr.shape dizisi içindeki SVGNODE~{...} (outline3D) kaydından
    /// 3B model dönüş açılarını (c_rotation = "rx,ry,rz") ve varsa UUID bilgisini ayıklar.
    /// </summary>
    public static void ReadEasyEdaOutline3DFromShapes(LcscPartMetadata metadata, JsonElement pkgShapes)
    {
        foreach (JsonElement shapeItem in pkgShapes.EnumerateArray())
        {
            if (shapeItem.ValueKind != JsonValueKind.String)
                continue;

            string? rawShape = shapeItem.GetString();
            if (string.IsNullOrWhiteSpace(rawShape) ||
                !rawShape.StartsWith("SVGNODE~", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryParseOutline3DSvgNode(rawShape, out double rx, out double ry, out double rz, out string? uuid))
            {
                metadata.Model3DRotX = rx;
                metadata.Model3DRotY = ry;
                metadata.Model3DRotZ = rz;

                if (string.IsNullOrWhiteSpace(metadata.StepModelUuid) && !string.IsNullOrWhiteSpace(uuid))
                {
                    metadata.StepModelUuid = uuid;
                }

                break;
            }
        }
    }

    /// <summary>
    /// Tek bir "SVGNODE~{...}" dizgesinden outline3D dönüş açılarını (rx, ry, rz) ve UUID değerini çözümler.
    /// </summary>
    public static bool TryParseOutline3DSvgNode(
        string svgNodeShape,
        out double rotX,
        out double rotY,
        out double rotZ,
        out string? uuid)
    {
        rotX = 0;
        rotY = 0;
        rotZ = 0;
        uuid = null;

        if (string.IsNullOrWhiteSpace(svgNodeShape))
            return false;

        int tildeIndex = svgNodeShape.IndexOf('~');
        string jsonPart = tildeIndex >= 0 ? svgNodeShape[(tildeIndex + 1)..].Trim() : svgNodeShape.Trim();
        if (string.IsNullOrWhiteSpace(jsonPart) || !jsonPart.StartsWith('{'))
            return false;

        try
        {
            using var svgDoc = JsonDocument.Parse(jsonPart);
            if (!svgDoc.RootElement.TryGetProperty("attrs", out JsonElement attrs) ||
                attrs.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (attrs.TryGetProperty("c_etype", out JsonElement etypeEl) &&
                etypeEl.ValueKind == JsonValueKind.String)
            {
                string? etype = etypeEl.GetString();
                if (!string.IsNullOrWhiteSpace(etype) &&
                    !string.Equals(etype, "outline3D", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (attrs.TryGetProperty("uuid", out JsonElement uuidEl) &&
                uuidEl.ValueKind == JsonValueKind.String)
            {
                uuid = uuidEl.GetString();
            }

            if (attrs.TryGetProperty("c_rotation", out JsonElement rotEl) &&
                rotEl.ValueKind == JsonValueKind.String)
            {
                string? rotStr = rotEl.GetString();
                if (!string.IsNullOrWhiteSpace(rotStr))
                {
                    string[] parts = rotStr.Split(',');
                    if (parts.Length >= 1 &&
                        double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                    {
                        rotX = x;
                    }

                    if (parts.Length >= 2 &&
                        double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    {
                        rotY = y;
                    }

                    if (parts.Length >= 3 &&
                        double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    {
                        rotZ = z;
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ReadEasyEdaCPara(LcscPartMetadata metadata, JsonElement cPara)
    {
        foreach (JsonProperty prop in cPara.EnumerateObject())
        {
            string key = prop.Name;
            string val = prop.Value.ValueKind == JsonValueKind.String
                ? (prop.Value.GetString() ?? string.Empty).Trim()
                : prop.Value.ToString().Trim();

            if (string.IsNullOrWhiteSpace(val))
                continue;

            switch (key.ToLowerInvariant())
            {
                case "name":
                    if (string.IsNullOrWhiteSpace(metadata.Mpn))
                        metadata.Mpn = val;
                    metadata.LibRef = SanitizeAltiumIdentifier(val);
                    break;
                case "package":
                    metadata.Package = val;
                    if (string.IsNullOrWhiteSpace(metadata.FootprintRef))
                        metadata.FootprintRef = SanitizeAltiumIdentifier(val);
                    break;
                case "pre":
                    metadata.DesignatorPrefix = val.EndsWith('?') ? val : val + "?";
                    break;
                case "manufacturer":
                case "bom_manufacturer":
                    metadata.Manufacturer = val;
                    break;
                case "manufacturer part":
                case "bom_manufacturer part":
                    metadata.Mpn = val;
                    break;
                case "datasheet":
                    metadata.DatasheetUrl = val;
                    break;
                case "value":
                    metadata.Value = val;
                    break;
                case "jlcpcb part class":
                case "componentlibrarytype":
                    metadata.JlcpcbClass = val.Contains("base", StringComparison.OrdinalIgnoreCase) ||
                                           val.Contains("basic", StringComparison.OrdinalIgnoreCase)
                        ? "Basic"
                        : "Extended";
                    break;
                case "tolerance":
                    metadata.ExtraParameters["Tolerance"] = val;
                    break;
                case "voltage rating":
                case "rated voltage":
                    metadata.ExtraParameters["Voltage_Rating"] = val;
                    break;
            }
        }
    }

    private async Task TryEnrichFromLcscProductApiAsync(
        LcscPartMetadata metadata,
        CancellationToken cancellationToken)
    {
        string url = $"https://wmsc.lcsc.com/ftps/wm/product/detail?productCode={metadata.LcscId}";
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("result", out JsonElement res) ||
                res.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (res.TryGetProperty("productModel", out JsonElement modelProp) &&
                modelProp.ValueKind == JsonValueKind.String)
            {
                string mpn = modelProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(mpn))
                    metadata.Mpn = mpn;
            }

            if (res.TryGetProperty("brandNameEn", out JsonElement brandProp) &&
                brandProp.ValueKind == JsonValueKind.String)
            {
                string brand = brandProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(brand))
                    metadata.Manufacturer = brand;
            }

            if (res.TryGetProperty("encapStandard", out JsonElement encapProp) &&
                encapProp.ValueKind == JsonValueKind.String)
            {
                string pkg = encapProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pkg))
                    metadata.Package = pkg;
            }

            if (res.TryGetProperty("productIntroEn", out JsonElement introProp) &&
                introProp.ValueKind == JsonValueKind.String)
            {
                string desc = introProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(desc))
                    metadata.Description = desc;
            }

            if (res.TryGetProperty("pdfUrl", out JsonElement pdfProp) &&
                pdfProp.ValueKind == JsonValueKind.String)
            {
                string pdf = pdfProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pdf))
                    metadata.DatasheetUrl = pdf;
            }

            if (res.TryGetProperty("stockNumber", out JsonElement stockProp) &&
                stockProp.TryGetInt32(out int stock))
            {
                metadata.Stock = stock;
            }

            if (res.TryGetProperty("parentCatalogName", out JsonElement parentCatProp) &&
                parentCatProp.ValueKind == JsonValueKind.String)
            {
                string parentCat = parentCatProp.GetString() ?? string.Empty;
                string childCat = res.TryGetProperty("catalogName", out JsonElement catProp) &&
                                  catProp.ValueKind == JsonValueKind.String
                    ? (catProp.GetString() ?? string.Empty)
                    : string.Empty;

                metadata.RawCategoryName = $"{parentCat} / {childCat}".Trim(' ', '/');
            }

            // En düşük kademe birim fiyatını (productPriceList) al
            if (res.TryGetProperty("productPriceList", out JsonElement priceList) &&
                priceList.ValueKind == JsonValueKind.Array &&
                priceList.GetArrayLength() > 0)
            {
                JsonElement firstTier = priceList[0];
                if (firstTier.TryGetProperty("usdPrice", out JsonElement usdProp))
                {
                    if (usdProp.ValueKind == JsonValueKind.Number && usdProp.TryGetDecimal(out decimal price))
                        metadata.UnitPriceUsd = price;
                    else if (usdProp.ValueKind == JsonValueKind.String &&
                             decimal.TryParse(usdProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsedPrice))
                        metadata.UnitPriceUsd = parsedPrice;
                }
            }

            // paramVOList üzerinden ek teknik parametreleri çek
            if (res.TryGetProperty("paramVOList", out JsonElement paramList) &&
                paramList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement paramItem in paramList.EnumerateArray())
                {
                    string pName = paramItem.TryGetProperty("paramNameEn", out JsonElement nEl) && nEl.ValueKind == JsonValueKind.String
                        ? (nEl.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    string pVal = paramItem.TryGetProperty("paramValueEn", out JsonElement vEl) && vEl.ValueKind == JsonValueKind.String
                        ? (vEl.GetString() ?? string.Empty).Trim()
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(pName) || string.IsNullOrWhiteSpace(pVal) || pVal == "-")
                        continue;

                    string cleanKey = SanitizeParameterKey(pName);
                    metadata.ExtraParameters[cleanKey] = pVal;

                    if (pName.Contains("Resistance", StringComparison.OrdinalIgnoreCase) ||
                        pName.Contains("Capacitance", StringComparison.OrdinalIgnoreCase) ||
                        pName.Contains("Inductance", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(metadata.Value))
                            metadata.Value = pVal;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LCSC WMSSC API sorgusu sırasında hata oluştu ({LcscId}).", metadata.LcscId);
        }
    }

    private static void FinalizeMetadataDefaults(LcscPartMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.Mpn))
            metadata.Mpn = metadata.LcscId;

        if (string.IsNullOrWhiteSpace(metadata.LibRef))
            metadata.LibRef = SanitizeAltiumIdentifier(metadata.Mpn);

        if (string.IsNullOrWhiteSpace(metadata.FootprintRef))
        {
            metadata.FootprintRef = !string.IsNullOrWhiteSpace(metadata.Package)
                ? SanitizeAltiumIdentifier(metadata.Package)
                : $"{metadata.LcscId}_FP";
        }

        metadata.Category = ClassifyCategory(metadata);

        if (metadata.DesignatorPrefix == "U?")
        {
            metadata.DesignatorPrefix = metadata.Category switch
            {
                ComponentCategory.Resistors => "R?",
                ComponentCategory.Capacitors => "C?",
                ComponentCategory.Inductors => "L?",
                ComponentCategory.Connectors => "J?",
                ComponentCategory.Discrete => "Q?",
                _ => "U?"
            };
        }

        if (metadata.Category == ComponentCategory.Capacitors)
        {
            string combined = $"{metadata.RawCategoryName} {metadata.Description}";
            metadata.IsPolarizedCapacitor =
                combined.Contains("Electrolytic", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Tantalum", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Aluminum", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("Polymer", StringComparison.OrdinalIgnoreCase);
        }

        metadata.Comment = !string.IsNullOrWhiteSpace(metadata.Value)
            ? metadata.Value
            : metadata.Mpn;

        if (string.IsNullOrWhiteSpace(metadata.DatasheetUrl))
            metadata.DatasheetUrl = $"https://www.lcsc.com/search?q={metadata.LcscId}";
    }

    public static ComponentCategory ClassifyCategory(LcscPartMetadata metadata)
    {
        string haystack = $"{metadata.RawCategoryName} {metadata.Description} {metadata.DesignatorPrefix}".ToUpperInvariant();

        if (metadata.DesignatorPrefix.StartsWith("R", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("RESISTOR"))
            return ComponentCategory.Resistors;

        if (metadata.DesignatorPrefix.StartsWith("C", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("CAPACITOR") || haystack.Contains("MLCC"))
            return ComponentCategory.Capacitors;

        if (metadata.DesignatorPrefix.StartsWith("L", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("INDUCTOR") || haystack.Contains("FERRITE BEAD") || haystack.Contains("COIL"))
            return ComponentCategory.Inductors;

        if (metadata.DesignatorPrefix.StartsWith("J", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("CONNECTOR") || haystack.Contains("HEADER") ||
            haystack.Contains("TERMINAL") || haystack.Contains("SOCKET") || haystack.Contains("USB"))
            return ComponentCategory.Connectors;

        if (metadata.DesignatorPrefix.StartsWith("D", StringComparison.OrdinalIgnoreCase) ||
            metadata.DesignatorPrefix.StartsWith("Q", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("DIODE") || haystack.Contains("MOSFET") ||
            haystack.Contains("TRANSISTOR") || haystack.Contains("BJT") || haystack.Contains("LED"))
            return ComponentCategory.Discrete;

        if (metadata.DesignatorPrefix.StartsWith("U", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("MICROCONTROLLER") || haystack.Contains("MCU") ||
            haystack.Contains("AMPLIFIER") || haystack.Contains("REGULATOR") ||
            haystack.Contains("INTERFACE") || haystack.Contains("CONVERTER") || haystack.Contains("IC"))
            return ComponentCategory.ICs;

        return ComponentCategory.Misc;
    }

    public static string SanitizeAltiumIdentifier(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "UNKNOWN_PART";

        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Trim()
            .Select(ch => invalid.Contains(ch) || ch == '|' || ch == ';' ? '_' : ch)
            .ToArray();

        return new string(chars);
    }

    private static string SanitizeParameterKey(string rawKey)
    {
        var chars = rawKey
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();

        string cleaned = new string(chars).Trim('_');
        while (cleaned.Contains("__", StringComparison.Ordinal))
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(cleaned) ? "Param" : cleaned;
    }
}
