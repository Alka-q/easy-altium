using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EasyAltium.Core.Models;

namespace EasyAltium.Core.Library;

/// <summary>
/// npnp CLI tarafından üretilen .PcbLib dosyalarında sabit 0.000 olarak yazılan
/// 3B STEP model dönüş açılarını (ROTX, ROTY, ROTZ ve MODEL.3D.ROTX/Y/Z),
/// EasyEDA outline3D (SVGNODE -> c_rotation) verisine göre yerinde (in-place) günceller.
/// OLE Compound File ve Altium kayıt uzunluk başlıklarını bozmamak için 5 baytlık sabit genişlik kullanır.
/// </summary>
public static partial class PcbLib3DModelPatcher
{
    [GeneratedRegex(@"(?<=\|(?:MODEL\.3D\.)?ROTX=)[0-9\.\-]{5}(?=\|)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RotXPattern();

    [GeneratedRegex(@"(?<=\|(?:MODEL\.3D\.)?ROTY=)[0-9\.\-]{5}(?=\|)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RotYPattern();

    [GeneratedRegex(@"(?<=\|(?:MODEL\.3D\.)?ROTZ=)[0-9\.\-]{5}(?=\|)", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex RotZPattern();

    /// <summary>
    /// Verilen açı değerini [0, 360) aralığına normalize eder ve Altium .PcbLib ikili akış uzunluğunu
    /// koruyacak şekilde tam 5 ASCII karakterlik ("0.000", "90.00", "180.0", "270.0") dizgeye dönüştürür.
    /// </summary>
    public static string FormatFixedWidthAngle(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees))
            return "0.000";

        double norm = ((degrees % 360.0) + 360.0) % 360.0;
        if (norm < 1e-3 || Math.Abs(norm - 360.0) < 1e-3)
            return "0.000";

        string formatted = norm switch
        {
            < 10.0 => norm.ToString("0.000", CultureInfo.InvariantCulture),
            < 100.0 => norm.ToString("00.00", CultureInfo.InvariantCulture),
            _ => norm.ToString("000.0", CultureInfo.InvariantCulture)
        };

        return formatted.Length == 5
            ? formatted
            : formatted.PadRight(5, '0')[..5];
    }

    /// <summary>
    /// Bileşene ait 3B model dönüş açılarını hedef .PcbLib dosyasına uygular.
    /// </summary>
    public static bool TryPatchPcbLib3DRotation(string pcbLibPath, LcscPartMetadata part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return TryPatchPcbLib3DRotation(pcbLibPath, part.Model3DRotX, part.Model3DRotY, part.Model3DRotZ);
    }

    /// <summary>
    /// Verilen X, Y, Z dönüş açılarını (derece) hedef .PcbLib dosyasındaki
    /// Library/Models/Data (ROTX/Y/Z) ve Footprint/Data (MODEL.3D.ROTX/Y/Z) kayıtlarına uygular.
    /// </summary>
    public static bool TryPatchPcbLib3DRotation(string pcbLibPath, double rotX, double rotY, double rotZ)
    {
        if (string.IsNullOrWhiteSpace(pcbLibPath) || !File.Exists(pcbLibPath))
            return false;

        string rxStr = FormatFixedWidthAngle(rotX);
        string ryStr = FormatFixedWidthAngle(rotY);
        string rzStr = FormatFixedWidthAngle(rotZ);

        byte[] rawBytes = File.ReadAllBytes(pcbLibPath);
        string content = Encoding.Latin1.GetString(rawBytes);

        int patchCount = 0;

        content = RotXPattern().Replace(content, m =>
        {
            if (m.Value != rxStr)
                patchCount++;
            return rxStr;
        });

        content = RotYPattern().Replace(content, m =>
        {
            if (m.Value != ryStr)
                patchCount++;
            return ryStr;
        });

        content = RotZPattern().Replace(content, m =>
        {
            if (m.Value != rzStr)
                patchCount++;
            return rzStr;
        });

        if (patchCount == 0)
            return false;

        byte[] patchedBytes = Encoding.Latin1.GetBytes(content);
        if (patchedBytes.Length != rawBytes.Length)
            throw new InvalidOperationException("PcbLib ikili boyut bütünlüğü koruma hatası: Yamalanan dosya boyutu değişti.");

        File.WriteAllBytes(pcbLibPath, patchedBytes);
        return true;
    }
}
