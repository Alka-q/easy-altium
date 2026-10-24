using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;

namespace EasyAltium.Core.Engine;

/// <summary>
/// Pasif Eleman Sembol Filtresi sonuç modeli.
/// </summary>
public sealed record PassiveSymbolMatchResult(
    bool IsPassiveMatched,
    string EffectiveSchLibPath,
    string EffectiveLibRef,
    string? PassiveTemplateLibRef
);

/// <summary>
/// Çekilen parça standart bir Direnç, Kapasitör veya İndüktör ise ve kullanıcı
/// "Kendi Standart Sembolümü Kullan" seçeneğini işaretlediyse; EasyEDA sembolü yerine
/// yerel şablon .SchLib içindeki standart sembolü (RES_STD, CAP_NP_STD, CAP_POL_STD, IND_STD)
/// referans alır, ancak Footprint, 3D Model ve BOM parametrelerini LCSC'den korur.
/// </summary>
public sealed class PassiveSymbolFilter
{
    public const string StandardResistorSymbol = "RES_STD";
    public const string StandardNonPolarizedCapSymbol = "CAP_NP_STD";
    public const string StandardPolarizedCapSymbol = "CAP_POL_STD";
    public const string StandardInductorSymbol = "IND_STD";

    public PassiveSymbolMatchResult Evaluate(
        LcscPartMetadata part,
        AppConfig config,
        string defaultSchLibPath)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(config);

        string defaultLibRef = !string.IsNullOrWhiteSpace(part.LibRef)
            ? part.LibRef
            : (!string.IsNullOrWhiteSpace(part.Mpn) ? part.Mpn : part.LcscId);

        if (!config.UsePassiveTemplateSymbol || part.PartCount > 1)
        {
            return new PassiveSymbolMatchResult(
                IsPassiveMatched: false,
                EffectiveSchLibPath: defaultSchLibPath,
                EffectiveLibRef: defaultLibRef,
                PassiveTemplateLibRef: null);
        }

        string? templateRef = ResolvePassiveTemplateSymbolName(part);
        if (string.IsNullOrWhiteSpace(templateRef))
        {
            return new PassiveSymbolMatchResult(
                IsPassiveMatched: false,
                EffectiveSchLibPath: defaultSchLibPath,
                EffectiveLibRef: defaultLibRef,
                PassiveTemplateLibRef: null);
        }

        return new PassiveSymbolMatchResult(
            IsPassiveMatched: true,
            EffectiveSchLibPath: config.PassiveTemplateSchLibPath,
            EffectiveLibRef: templateRef,
            PassiveTemplateLibRef: templateRef);
    }

    public static string? ResolvePassiveTemplateSymbolName(LcscPartMetadata part)
    {
        // Ağ (array) direnç veya kapasitörler (4x, 8-pin vb.) standart 2-pinli sembolle eşleşmemelidir
        if (part.Description.Contains("Array", StringComparison.OrdinalIgnoreCase) ||
            part.Description.Contains("Network", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return part.Category switch
        {
            ComponentCategory.Resistors => StandardResistorSymbol,
            ComponentCategory.Capacitors => part.IsPolarizedCapacitor
                ? StandardPolarizedCapSymbol
                : StandardNonPolarizedCapSymbol,
            ComponentCategory.Inductors => StandardInductorSymbol,
            _ => null
        };
    }
}
