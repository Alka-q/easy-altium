using System.Text.RegularExpressions;

namespace EasyAltium.Core.Models;

/// <summary>
/// LCSC ve JLCPCB üzerinden çekilen elektronik bileşenin tüm meta verilerini ve
/// kütüphane eşleştirmelerini temsil eder.
/// </summary>
public sealed partial class LcscPartMetadata
{
    /// <summary>
    /// LCSC Parça Kodu doğrulama düzenli ifadesi: ^C[1-9]\d{2,}$ (Örn: C2040, C14663)
    /// </summary>
    [GeneratedRegex(@"^C[1-9]\d{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex LcscIdPattern();

    /// <summary>
    /// Metin içerisinden LCSC parça kodunu yakalamak için kullanılan düzenli ifade.
    /// </summary>
    [GeneratedRegex(@"\b(C[1-9]\d{2,})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex LcscIdExtractPattern();

    public required string LcscId { get; init; }
    public string Mpn { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;

    /// <summary>
    /// JLCPCB SMT sınıfı: "Basic" veya "Extended"
    /// </summary>
    public string JlcpcbClass { get; set; } = "Extended";

    public string DatasheetUrl { get; set; } = string.Empty;
    public ComponentCategory Category { get; set; } = ComponentCategory.Misc;
    public string RawCategoryName { get; set; } = string.Empty;
    public int Stock { get; set; }
    public decimal UnitPriceUsd { get; set; }

    /// <summary>
    /// .SchLib içindeki sembol referans adı (Library Ref)
    /// </summary>
    public string LibRef { get; set; } = string.Empty;

    /// <summary>
    /// .PcbLib içindeki kılıf referans adı (Footprint Ref)
    /// </summary>
    public string FootprintRef { get; set; } = string.Empty;

    /// <summary>
    /// Şematik Designator öneki (Örn: R?, C?, L?, U?, J?, D?, Q?)
    /// </summary>
    public string DesignatorPrefix { get; set; } = "U?";

    /// <summary>
    /// Şematik Comment alanı (MPN veya pasif eleman değeri, Örn: 10k 1%)
    /// </summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// Çok parçalı (multi-part) bileşenlerde alt parça sayısı (Standart = 1)
    /// </summary>
    public int PartCount { get; set; } = 1;

    /// <summary>
    /// Kapasitör ise kutuplu (elektrolitik/tantal) olup olmadığı
    /// </summary>
    public bool IsPolarizedCapacitor { get; set; }

    /// <summary>
    /// Pasif elemanlarda değer (Örn: 10kΩ, 100nF, 4.7uH)
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// 3D STEP model UUID (EasyEDA üzerinden)
    /// </summary>
    public string StepModelUuid { get; set; } = string.Empty;

    /// <summary>
    /// EasyEDA outline3D (SVGNODE) üzerinden gelen 3B model X ekseni dönüş açısı (Derece)
    /// </summary>
    public double Model3DRotX { get; set; }

    /// <summary>
    /// EasyEDA outline3D (SVGNODE) üzerinden gelen 3B model Y ekseni dönüş açısı (Derece)
    /// </summary>
    public double Model3DRotY { get; set; }

    /// <summary>
    /// EasyEDA outline3D (SVGNODE) üzerinden gelen 3B model Z ekseni dönüş açısı (Derece)
    /// </summary>
    public double Model3DRotZ { get; set; }

    /// <summary>
    /// Ek BOM parametreleri (Tolerance, Voltage_Rating, Power_Rating, RoHS vb.)
    /// </summary>
    public Dictionary<string, string> ExtraParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Verilen metnin geçerli bir LCSC parça kodu (^C[1-9]\d{2,}$) olup olmadığını kontrol eder.
    /// </summary>
    public static bool IsValidLcscId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        return LcscIdPattern().IsMatch(candidate.Trim());
    }

    /// <summary>
    /// Pano metninden geçerli bir LCSC parça kodu ayıklar (Tam eşleşme önceliklidir).
    /// </summary>
    public static string? TryExtractLcscId(string? clipboardText)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
            return null;

        string trimmed = clipboardText.Trim();
        if (LcscIdPattern().IsMatch(trimmed))
            return trimmed;

        // Tek satırlık kısa metin veya LCSC URL'si içerisinden Cxxxx yakalama
        if (trimmed.Length <= 256)
        {
            var match = LcscIdExtractPattern().Match(trimmed);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Altium'a aktarılacak tüm BOM parametrelerini birleştirilmiş sözlük olarak döndürür.
    /// </summary>
    public Dictionary<string, string> BuildBomParameterDictionary()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LCSC_Part"] = LcscId,
            ["MPN"] = Mpn,
            ["Manufacturer"] = Manufacturer,
            ["Package"] = Package,
            ["JLCPCB_Class"] = JlcpcbClass,
            ["Datasheet_URL"] = DatasheetUrl,
            ["Supplier"] = "LCSC",
            ["Supplier_Part"] = LcscId,
            ["Stock"] = Stock.ToString(),
            ["Unit_Price_USD"] = UnitPriceUsd.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(Value))
            dict["Value"] = Value;

        foreach (var kvp in ExtraParameters)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                dict[kvp.Key] = kvp.Value;
        }

        return dict;
    }
}
