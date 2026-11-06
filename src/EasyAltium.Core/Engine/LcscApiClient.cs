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

        // 1. EasyEDA Component API ve 2. LCSC WMSSC Product Detail API sorgularını paralel çalıştır
        var easyEdaMetadata = new LcscPartMetadata { LcscId = normalizedId };
        var lcscMetadata = new LcscPartMetadata { LcscId = normalizedId };

        await Task.WhenAll(
            TryEnrichFromEasyEdaApiAsync(easyEdaMetadata, cancellationToken),
            TryEnrichFromLcscProductApiAsync(lcscMetadata, cancellationToken)
        ).ConfigureAwait(false);

        MergeParallelMetadata(metadata, easyEdaMetadata, lcscMetadata);

        // 3. Eksik alanlar için akıllı varsayılan tamamlama ve kategori sınıflandırması
        FinalizeMetadataDefaults(metadata);

        return metadata;
    }

    private static void MergeParallelMetadata(
        LcscPartMetadata target,
        LcscPartMetadata easyEda,
        LcscPartMetadata lcsc)
    {
        // Önce EasyEDA alanlarını uygula
        if (easyEda.PartCount > 1)
            target.PartCount = easyEda.PartCount;
        if (!string.IsNullOrWhiteSpace(easyEda.Mpn))
            target.Mpn = easyEda.Mpn;
        if (!string.IsNullOrWhiteSpace(easyEda.LibRef))
            target.LibRef = easyEda.LibRef;
        if (!string.IsNullOrWhiteSpace(easyEda.Description))
            target.Description = easyEda.Description;
        if (!string.IsNullOrWhiteSpace(easyEda.Package))
            target.Package = easyEda.Package;
        if (!string.IsNullOrWhiteSpace(easyEda.FootprintRef))
            target.FootprintRef = easyEda.FootprintRef;
        if (easyEda.DesignatorPrefix != "U?")
            target.DesignatorPrefix = easyEda.DesignatorPrefix;
        if (!string.IsNullOrWhiteSpace(easyEda.Manufacturer))
            target.Manufacturer = easyEda.Manufacturer;
        if (!string.IsNullOrWhiteSpace(easyEda.DatasheetUrl))
            target.DatasheetUrl = easyEda.DatasheetUrl;
        if (!string.IsNullOrWhiteSpace(easyEda.Value))
            target.Value = easyEda.Value;
        target.JlcpcbClass = easyEda.JlcpcbClass;
        target.StepModelUuid = easyEda.StepModelUuid;
        target.Model3DRotX = easyEda.Model3DRotX;
        target.Model3DRotY = easyEda.Model3DRotY;
        target.Model3DRotZ = easyEda.Model3DRotZ;

        foreach (var kvp in easyEda.ExtraParameters)
            target.ExtraParameters[kvp.Key] = kvp.Value;

        // Ardından LCSC WMSSC alanlarıyla zenginleştir (LCSC daha güncel MPN, Üretici, Stok, Fiyat, Kategori sağlar)
        if (!string.IsNullOrWhiteSpace(lcsc.Mpn))
            target.Mpn = lcsc.Mpn;
        if (!string.IsNullOrWhiteSpace(lcsc.Manufacturer))
            target.Manufacturer = lcsc.Manufacturer;
        if (!string.IsNullOrWhiteSpace(lcsc.Package))
            target.Package = lcsc.Package;
        if (!string.IsNullOrWhiteSpace(lcsc.Description))
            target.Description = lcsc.Description;
        if (!string.IsNullOrWhiteSpace(lcsc.DatasheetUrl))
            target.DatasheetUrl = lcsc.DatasheetUrl;
        if (lcsc.Stock > 0)
            target.Stock = lcsc.Stock;
        if (lcsc.UnitPriceUsd > 0)
            target.UnitPriceUsd = lcsc.UnitPriceUsd;
        if (!string.IsNullOrWhiteSpace(lcsc.RawCategoryName))
            target.RawCategoryName = lcsc.RawCategoryName;

        foreach (var kvp in lcsc.ExtraParameters)
            target.ExtraParameters[kvp.Key] = kvp.Value;
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
                    string cleanedPre = val.Trim().TrimEnd('?');
                    if (!string.IsNullOrWhiteSpace(cleanedPre))
                        metadata.DesignatorPrefix = cleanedPre.ToUpperInvariant() + "?";
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
                    if (val != "-")
                        metadata.Value = LcscPartMetadata.NormalizeEngineeringUnitsForAltium(val);
                    break;
                case "jlcpcb part class":
                case "componentlibrarytype":
                    metadata.JlcpcbClass = val.Contains("base", StringComparison.OrdinalIgnoreCase) ||
                                           val.Contains("basic", StringComparison.OrdinalIgnoreCase)
                        ? "Basic"
                        : "Extended";
                    break;
                case "tolerance":
                    metadata.ExtraParameters["Tolerance"] = LcscPartMetadata.NormalizeEngineeringUnitsForAltium(val);
                    break;
                case "voltage rating":
                case "rated voltage":
                    metadata.ExtraParameters["Voltage_Rating"] = LcscPartMetadata.NormalizeEngineeringUnitsForAltium(val);
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
                    string normalizedVal = LcscPartMetadata.NormalizeEngineeringUnitsForAltium(pVal);
                    metadata.ExtraParameters[cleanKey] = normalizedVal;
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

        metadata.Description = LcscPartMetadata.NormalizeEngineeringUnitsForAltium(metadata.Description);
        metadata.Category = ClassifyCategory(metadata);
        metadata.DesignatorPrefix = ResolveCanonicalDesignatorPrefix(metadata);

        // Yalnızca gerçek pasif bileşenlerin (Direnç, Kapasitör, İndüktör/Ferrit Bead) Value değeri atanmalı;
        // MOSFET/Diyot/IC gibi yarıiletkenlerde Output_Capacitance_Coss (80pF) veya RDS_on (47mOhm) Value/Comment olmamalı.
        if (metadata.Category is ComponentCategory.Resistors or ComponentCategory.Capacitors or ComponentCategory.Inductors)
        {
            if (string.IsNullOrWhiteSpace(metadata.Value) || metadata.Value == "-")
            {
                metadata.Value = ResolvePassivePrimaryValue(metadata);
            }
            else
            {
                metadata.Value = LcscPartMetadata.NormalizeEngineeringUnitsForAltium(metadata.Value);
            }
        }
        else
        {
            // Aktif/Yarıiletken, Konnektör veya Koruma (Sigorta/Varistör) bileşenlerinde yanlışlıkla yakalanmış parazitik kapasitans/direnç değerini temizle
            metadata.Value = string.Empty;
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

        // Pasiflerde (Direnç, Kapasitör, İndüktör) şematik Comment alanında Value (örn. 100kOhm, 100nF, 4.7uH) gösterilir;
        // diğer tüm bileşenlerde (IC, MOSFET, Diyot, Konnektör, Sigorta vb.) MPN (örn. AO3401A, RP2040) gösterilir.
        if (metadata.Category is ComponentCategory.Resistors or ComponentCategory.Capacitors or ComponentCategory.Inductors &&
            !string.IsNullOrWhiteSpace(metadata.Value))
        {
            metadata.Comment = metadata.Value;
        }
        else
        {
            metadata.Comment = metadata.Mpn;
        }

        if (string.IsNullOrWhiteSpace(metadata.DatasheetUrl))
            metadata.DatasheetUrl = $"https://www.lcsc.com/search?q={metadata.LcscId}";
    }

    private static string ResolvePassivePrimaryValue(LcscPartMetadata metadata)
    {
        if (metadata.Category == ComponentCategory.Resistors)
        {
            foreach (string key in new[] { "Resistance", "Resistance_Ohms" })
            {
                if (metadata.ExtraParameters.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) && v != "-")
                    return LcscPartMetadata.NormalizeEngineeringUnitsForAltium(v);
            }
        }
        else if (metadata.Category == ComponentCategory.Capacitors)
        {
            foreach (string key in new[] { "Capacitance" })
            {
                if (metadata.ExtraParameters.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) && v != "-")
                    return LcscPartMetadata.NormalizeEngineeringUnitsForAltium(v);
            }
        }
        else if (metadata.Category == ComponentCategory.Inductors)
        {
            foreach (string key in new[] { "Inductance", "Impedance_100MHz", "Impedance_at_Frequency", "Impedance" })
            {
                if (metadata.ExtraParameters.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) && v != "-")
                    return LcscPartMetadata.NormalizeEngineeringUnitsForAltium(v);
            }
        }

        return string.Empty;
    }

    public static string ResolveCanonicalDesignatorPrefix(LcscPartMetadata metadata)
    {
        string haystack = $"{metadata.RawCategoryName} {metadata.Description}".ToUpperInvariant();
        string rawPre = (metadata.DesignatorPrefix ?? string.Empty).Trim().ToUpperInvariant().TrimEnd('?');

        // 1. Özel alt kategorileri önce kontrol et (Sigorta, Varistör, Kristal, Diyot, Transistör, Ferrit Bead, Bobin, Konnektör)
        if (haystack.Contains("FUSE") || haystack.Contains("PTC RESETTABLE") || rawPre == "F")
            return "F?";

        if (haystack.Contains("VARISTOR") || haystack.Contains("MOVS") || rawPre == "RV")
            return "RV?";

        if (haystack.Contains("CRYSTAL") || haystack.Contains("OSCILLATOR") || haystack.Contains("RESONATOR") || rawPre is "Y" or "X" or "XTAL")
            return "Y?";

        if (haystack.Contains("COMMON MODE CHOKE") || haystack.Contains("FERRITE BEAD") || haystack.Contains("INDUCTOR") || haystack.Contains("COIL"))
            return rawPre == "FB" ? "FB?" : "L?";

        if (haystack.Contains("CONNECTOR") || haystack.Contains("HEADER") || haystack.Contains("TERMINAL") || haystack.Contains("SOCKET") || haystack.Contains("USB JACK") || rawPre is "CN" or "J" or "CON" or "P")
            return "J?";

        if (haystack.Contains("MOSFET") || haystack.Contains("TRANSISTOR") || haystack.Contains("BJT") || haystack.Contains("SINGLE FETS") || haystack.Contains("IGBT"))
            return "Q?";

        if (haystack.Contains("DIODE") || haystack.Contains("RECTIFIER") || haystack.Contains("ZENER") || haystack.Contains("SCHOTTKY") || haystack.Contains("TVS") || haystack.Contains("LED"))
            return "D?";

        // Mekanik anahtarlar (Tactile Switch, Pushbutton, Slide/DIP/Toggle Switch) — "SWITCHING REGULATOR" veya "ANALOG SWITCH IC" ile karışmamalı!
        if ((haystack.Contains("TACTILE") || haystack.Contains("PUSHBUTTON") || haystack.Contains("SLIDE SWITCH") ||
             haystack.Contains("DIP SWITCH") || haystack.Contains("TOGGLE SWITCH") || haystack.Contains("MICRO SWITCH") ||
             haystack.Contains("KEY SWITCH") || rawPre is "SW" or "S") &&
            !haystack.Contains("REGULATOR") && !haystack.Contains("CONVERTER") && !haystack.Contains("CONTROLLER"))
            return "SW?";

        if (haystack.Contains("RELAY") || rawPre == "K")
            return "K?";

        // 2. Ana kategori tablosuna göre standart önek ata
        return metadata.Category switch
        {
            ComponentCategory.Resistors => "R?",
            ComponentCategory.Capacitors => "C?",
            ComponentCategory.Inductors => "L?",
            ComponentCategory.Connectors => "J?",
            ComponentCategory.Discrete => rawPre == "D" ? "D?" : "Q?",
            ComponentCategory.ICs => "U?",
            _ => !string.IsNullOrWhiteSpace(rawPre) ? $"{rawPre}?" : "U?"
        };
    }

    public static ComponentCategory ClassifyCategory(LcscPartMetadata metadata)
    {
        string catAndDesc = $"{metadata.RawCategoryName} {metadata.Description}".ToUpperInvariant();
        string rawPre = (metadata.DesignatorPrefix ?? string.Empty).Trim().ToUpperInvariant().TrimEnd('?');

        // 1. Önce açık kategori/açıklama anahtar kelimelerine bak (DesignatorPrefix="C?" veya "CN?" yüzünden yanlış sınıflandırmayı önler!)
        if (catAndDesc.Contains("CONNECTOR") || catAndDesc.Contains("HEADER") ||
            catAndDesc.Contains("TERMINAL") || catAndDesc.Contains("SOCKET") ||
            catAndDesc.Contains("USB") || rawPre is "J" or "CN" or "CON")
            return ComponentCategory.Connectors;

        if (catAndDesc.Contains("FUSE") || catAndDesc.Contains("VARISTOR") ||
            catAndDesc.Contains("MOVS") || catAndDesc.Contains("CIRCUIT PROTECTION") && !catAndDesc.Contains("TVS") && !catAndDesc.Contains("DIODE"))
            return ComponentCategory.Misc;

        if (catAndDesc.Contains("COMMON MODE CHOKE") || catAndDesc.Contains("INDUCTOR") ||
            catAndDesc.Contains("FERRITE BEAD") || catAndDesc.Contains("COIL"))
            return ComponentCategory.Inductors;

        if (catAndDesc.Contains("CAPACITOR") || catAndDesc.Contains("MLCC"))
            return ComponentCategory.Capacitors;

        if (catAndDesc.Contains("RESISTOR") && !catAndDesc.Contains("VARISTOR"))
            return ComponentCategory.Resistors;

        if (catAndDesc.Contains("DIODE") || catAndDesc.Contains("RECTIFIER") ||
            catAndDesc.Contains("MOSFET") || catAndDesc.Contains("TRANSISTOR") ||
            catAndDesc.Contains("BJT") || catAndDesc.Contains("SINGLE FETS") ||
            catAndDesc.Contains("THYRISTOR") || catAndDesc.Contains("IGBT") ||
            catAndDesc.Contains("LED"))
            return ComponentCategory.Discrete;

        if (catAndDesc.Contains("MICROCONTROLLER") || catAndDesc.Contains("MCU") ||
            catAndDesc.Contains("PROCESSOR") || catAndDesc.Contains("AMPLIFIER") ||
            catAndDesc.Contains("REGULATOR") || catAndDesc.Contains("INTERFACE") ||
            catAndDesc.Contains("CONVERTER") || catAndDesc.Contains("TRANSCEIVER") ||
            catAndDesc.Contains("DRIVER") || catAndDesc.Contains("MEMORY") ||
            catAndDesc.Contains("SENSOR") || catAndDesc.Contains("OPAMP"))
            return ComponentCategory.ICs;

        // 2. Yalnızca kategori metninden çıkarılamadıysa tam DesignatorPrefix eşleşmesine bak ("CN" != "C")
        return rawPre switch
        {
            "R" => ComponentCategory.Resistors,
            "C" => ComponentCategory.Capacitors,
            "L" or "FB" => ComponentCategory.Inductors,
            "J" or "CN" or "CON" => ComponentCategory.Connectors,
            "D" or "Q" => ComponentCategory.Discrete,
            "U" or "IC" => ComponentCategory.ICs,
            _ => ComponentCategory.Misc
        };
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
