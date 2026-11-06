using System.Text.RegularExpressions;

namespace EasyAltium.Core.Models;

/// <summary>
/// Represents all metadata and library mappings for an electronic component
/// fetched from LCSC and JLCPCB / EasyEDA.
/// </summary>
public sealed partial class LcscPartMetadata
{
    /// <summary>
    /// LCSC part number validation regex: ^C[1-9]\d{2,}$ (e.g., C2040, C14663)
    /// </summary>
    [GeneratedRegex(@"^C[1-9]\d{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex LcscIdPattern();

    /// <summary>
    /// Regex used to extract an LCSC part code from arbitrary text or URLs.
    /// </summary>
    [GeneratedRegex(@"\b(C[1-9]\d{2,})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    public static partial Regex LcscIdExtractPattern();

    public required string LcscId { get; init; }
    public string Mpn { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;

    /// <summary>
    /// JLCPCB SMT assembly class: "Basic" or "Extended"
    /// </summary>
    public string JlcpcbClass { get; set; } = "Extended";

    public string DatasheetUrl { get; set; } = string.Empty;
    public ComponentCategory Category { get; set; } = ComponentCategory.Misc;
    public string RawCategoryName { get; set; } = string.Empty;
    public int Stock { get; set; }
    public decimal UnitPriceUsd { get; set; }

    /// <summary>
    /// Symbol reference name inside the .SchLib (Library Ref)
    /// </summary>
    public string LibRef { get; set; } = string.Empty;

    /// <summary>
    /// Footprint reference name inside the .PcbLib (Footprint Ref)
    /// </summary>
    public string FootprintRef { get; set; } = string.Empty;

    /// <summary>
    /// Schematic designator prefix (e.g., R?, C?, L?, U?, J?, D?, Q?)
    /// </summary>
    public string DesignatorPrefix { get; set; } = "U?";

    /// <summary>
    /// Schematic Comment field (MPN or passive value such as 10k 1%)
    /// </summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// Sub-part count for multi-part components (Default = 1)
    /// </summary>
    public int PartCount { get; set; } = 1;

    /// <summary>
    /// Indicates whether a capacitor is polarized (electrolytic/tantalum)
    /// </summary>
    public bool IsPolarizedCapacitor { get; set; }

    /// <summary>
    /// Component value for passives (e.g., 10kΩ, 100nF, 4.7uH)
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// 3D STEP model UUID from EasyEDA
    /// </summary>
    public string StepModelUuid { get; set; } = string.Empty;

    /// <summary>
    /// 3D model X-axis rotation angle in degrees from EasyEDA outline3D (SVGNODE -> c_rotation)
    /// </summary>
    public double Model3DRotX { get; set; }

    /// <summary>
    /// 3D model Y-axis rotation angle in degrees from EasyEDA outline3D (SVGNODE -> c_rotation)
    /// </summary>
    public double Model3DRotY { get; set; }

    /// <summary>
    /// 3D model Z-axis rotation angle in degrees from EasyEDA outline3D (SVGNODE -> c_rotation)
    /// </summary>
    public double Model3DRotZ { get; set; }

    /// <summary>
    /// Additional technical BOM parameters (Tolerance, Voltage_Rating, Power_Rating, RoHS, etc.)
    /// </summary>
    public Dictionary<string, string> ExtraParameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks whether the candidate string is a valid LCSC part code (^C[1-9]\d{2,}$).
    /// </summary>
    public static bool IsValidLcscId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        return LcscIdPattern().IsMatch(candidate.Trim());
    }

    /// <summary>
    /// Extracts a valid LCSC part code from clipboard text or URLs (exact match preferred).
    /// </summary>
    public static string? TryExtractLcscId(string? clipboardText)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
            return null;

        string trimmed = clipboardText.Trim();
        if (LcscIdPattern().IsMatch(trimmed))
            return trimmed;

        if (trimmed.Length <= 256)
        {
            var match = LcscIdExtractPattern().Match(trimmed);
            if (match.Success)
                return match.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// Builds the unified dictionary of BOM parameters to transfer into Altium Designer.
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

        if (!string.IsNullOrWhiteSpace(Value) && Value != "-")
            dict["Value"] = NormalizeEngineeringUnitsForAltium(Value);

        foreach (var kvp in ExtraParameters)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                dict[kvp.Key] = NormalizeEngineeringUnitsForAltium(kvp.Value);
        }

        return dict;
    }

    /// <summary>
    /// Converts multi-byte Unicode engineering symbols (Ω, ℃, μ) and any mojibake sequences
    /// (Î©, Â±, Âµ, â„ƒ) or HTML entities (&#937;, &#8451;) into clean 8-bit ANSI/Windows-1252
    /// compatible engineering notation for Altium Designer DelphiScript (Bridge.pas) and SchLib files.
    /// </summary>
    public static string NormalizeEngineeringUnitsForAltium(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string s = System.Net.WebUtility.HtmlDecode(input);

        // Fix common UTF-8-as-ANSI mojibake first
        s = s.Replace("Î©", "Ohm", StringComparison.Ordinal)
             .Replace("â„¦", "Ohm", StringComparison.Ordinal)
             .Replace("â„ƒ", "degC", StringComparison.Ordinal)
             .Replace("â„‰", "degF", StringComparison.Ordinal)
             .Replace("Â±", "+/-", StringComparison.Ordinal)
             .Replace("Âµ", "u", StringComparison.Ordinal)
             .Replace("Î¼", "u", StringComparison.Ordinal)
             .Replace("Â°C", "degC", StringComparison.Ordinal)
             .Replace("Â°", "deg", StringComparison.Ordinal);

        // Replace multi-byte Unicode symbols that are outside Windows-1252 or corrupt in DelphiScript ANSI INI reader
        s = s.Replace("\u03A9", "Ohm", StringComparison.Ordinal) // Greek Capital Letter Omega (Ω)
             .Replace("\u2126", "Ohm", StringComparison.Ordinal) // Ohm Sign (Ω)
             .Replace("\u03C9", "Ohm", StringComparison.Ordinal) // Greek Small Letter Omega (ω)
             .Replace("\u2103", "degC", StringComparison.Ordinal) // Degree Celsius (℃)
             .Replace("\u2109", "degF", StringComparison.Ordinal) // Degree Fahrenheit (℉)
             .Replace("\u00B1", "+/-", StringComparison.Ordinal) // Plus-Minus Sign (±)
             .Replace("\u00B5", "u", StringComparison.Ordinal)   // Micro Sign (µ)
             .Replace("\u03BC", "u", StringComparison.Ordinal)   // Greek Small Letter Mu (μ)
             .Replace("°C", "degC", StringComparison.Ordinal);

        return s.Trim();
    }
}
