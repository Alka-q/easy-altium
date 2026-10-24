using System.Text;
using System.Text.Json;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyAltium.Core.Engine;

/// <summary>
/// Yedek (Fallback) akışı sonucunu temsil eder.
/// </summary>
public sealed record AsciiFallbackResult(
    bool IsSuccess,
    string FallbackSchDocPath,
    string FallbackPcbDocPath,
    string StepModelPath,
    string? ErrorMessage = null
);

/// <summary>
/// Çok parçalı (multi-part) karmaşık sembollerde veya npnp aracının hata döndürdüğü durumlarda,
/// EasyEDA verisinden Altium ASCII (.SchDoc / .PcbDoc) dosyalarını üretip Altium'un
/// MakeLibrary komutunu (Sch:MakeLibrary / PCB:MakeLibrary) tetikleyecek yedek akışı yürütür.
/// </summary>
public sealed class AsciiFallbackHandler
{
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AsciiFallbackHandler> _logger;

    public AsciiFallbackHandler(
        AppConfig config,
        HttpClient? httpClient = null,
        ILogger<AsciiFallbackHandler>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _logger = logger ?? NullLogger<AsciiFallbackHandler>.Instance;
    }

    public async Task<AsciiFallbackResult> PrepareFallbackDocumentsAsync(
        LcscPartMetadata part,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        _config.EnsureDirectoriesExist();

        string safeMpn = LcscApiClient.SanitizeAltiumIdentifier(part.Mpn);
        string schDocPath = Path.Combine(_config.FallbackDirectory, $"{part.LcscId}_{safeMpn}_Raw.SchDoc");
        string pcbDocPath = Path.Combine(_config.FallbackDirectory, $"{part.LcscId}_{safeMpn}_Raw.PcbDoc");
        string stepPath = Path.Combine(_config.StepDirectory, $"{part.LcscId}_{safeMpn}.step");

        try
        {
            _logger.LogInformation("Fallback MakeLibrary hazırlığı başlatılıyor ({LcscId})...", part.LcscId);

            // 1. 3D STEP Modeli mevcutsa EasyEDA sunucusundan doğrudan indir
            string downloadedStepPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(part.StepModelUuid))
            {
                downloadedStepPath = await TryDownloadStepModelAsync(part.StepModelUuid, stepPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 2. EasyEDA sembol pinlerini çek ve Altium ASCII .SchDoc üret
            var pins = await FetchEasyEdaSymbolPinsAsync(part.LcscId, cancellationToken).ConfigureAwait(false);
            string asciiSchContent = BuildAltiumAsciiSchDoc(part, pins);
            await File.WriteAllTextAsync(schDocPath, asciiSchContent, Encoding.ASCII, cancellationToken)
                .ConfigureAwait(false);

            // 3. Altium ASCII .PcbDoc taşıyıcı dosyasını üret
            string asciiPcbContent = BuildAltiumAsciiPcbDoc(part);
            await File.WriteAllTextAsync(pcbDocPath, asciiPcbContent, Encoding.ASCII, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("Fallback ASCII dökümanları üretildi: {SchDoc}", schDocPath);

            return new AsciiFallbackResult(
                IsSuccess: true,
                FallbackSchDocPath: schDocPath,
                FallbackPcbDocPath: pcbDocPath,
                StepModelPath: downloadedStepPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Fallback ASCII üretimi başarısız oldu ({LcscId}).", part.LcscId);
            return new AsciiFallbackResult(
                IsSuccess: false,
                FallbackSchDocPath: string.Empty,
                FallbackPcbDocPath: string.Empty,
                StepModelPath: string.Empty,
                ErrorMessage: ex.Message);
        }
    }

    private async Task<string> TryDownloadStepModelAsync(
        string stepUuid,
        string targetStepPath,
        CancellationToken cancellationToken)
    {
        string url = $"https://modules.easyeda.com/qAxj6KHrDKw4blvCG8QJPs7Y/{stepUuid}";
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return string.Empty;

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > 32)
            {
                await File.WriteAllBytesAsync(targetStepPath, bytes, cancellationToken).ConfigureAwait(false);
                return targetStepPath;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "STEP modeli indirilemedi (UUID={StepUuid}).", stepUuid);
        }

        return string.Empty;
    }

    private async Task<List<(string Number, string Name, int PartId)>> FetchEasyEdaSymbolPinsAsync(
        string lcscId,
        CancellationToken cancellationToken)
    {
        var pins = new List<(string Number, string Name, int PartId)>();
        string url = $"https://easyeda.com/api/products/{lcscId}/components?version=6.5.46";

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return pins;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("result", out JsonElement result) ||
                result.ValueKind != JsonValueKind.Object)
            {
                return pins;
            }

            if (result.TryGetProperty("dataStr", out JsonElement dataStr) &&
                dataStr.ValueKind == JsonValueKind.Object &&
                dataStr.TryGetProperty("shape", out JsonElement shapeArr) &&
                shapeArr.ValueKind == JsonValueKind.Array)
            {
                ExtractPinsFromShapeArray(shapeArr, partId: 1, pins);
            }

            if (result.TryGetProperty("subParts", out JsonElement subParts) &&
                subParts.ValueKind == JsonValueKind.Array)
            {
                int partIdx = 1;
                foreach (JsonElement sub in subParts.EnumerateArray())
                {
                    if (sub.TryGetProperty("dataStr", out JsonElement subData) &&
                        subData.ValueKind == JsonValueKind.Object &&
                        subData.TryGetProperty("shape", out JsonElement subShapes) &&
                        subShapes.ValueKind == JsonValueKind.Array)
                    {
                        ExtractPinsFromShapeArray(subShapes, partIdx, pins);
                    }
                    partIdx++;
                }
            }
        }
        catch
        {
            // Pin parse hatasında varsayılan 2 pin üretilir
        }

        if (pins.Count == 0)
        {
            pins.Add(("1", "1", 1));
            pins.Add(("2", "2", 1));
        }

        return pins;
    }

    private static void ExtractPinsFromShapeArray(
        JsonElement shapeArr,
        int partId,
        List<(string Number, string Name, int PartId)> pins)
    {
        foreach (JsonElement item in shapeArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            string rawShape = item.GetString() ?? string.Empty;
            if (!rawShape.StartsWith("P~", StringComparison.Ordinal))
                continue;

            // EasyEDA Pin formatı: P~show~electric~pinNum~x~y~rot~id^^...^^pinName~...
            string[] segments = rawShape.Split("^^", StringSplitOptions.None);
            string[] headerFields = segments[0].Split('~');
            string pinNum = headerFields.Length > 3 && !string.IsNullOrWhiteSpace(headerFields[3])
                ? headerFields[3].Trim()
                : (pins.Count + 1).ToString();

            string pinName = pinNum;
            if (segments.Length > 4)
            {
                string[] nameFields = segments[4].Split('~');
                if (nameFields.Length > 4 && !string.IsNullOrWhiteSpace(nameFields[4]))
                    pinName = nameFields[4].Trim();
            }

            pins.Add((pinNum, pinName, partId));
        }
    }

    private static string BuildAltiumAsciiSchDoc(
        LcscPartMetadata part,
        List<(string Number, string Name, int PartId)> pins)
    {
        var sb = new StringBuilder();
        sb.AppendLine("|HEADER=Protel for Windows - Schematic Capture Ascii File Version 5.0|WEIGHT=10");
        sb.AppendLine("|RECORD=31|FONTIDCOUNT=1|SIZE1=10|FONTNAME1=Times New Roman|SYSTEMFONT=1|BORDERON=T|TITLEBLOCKON=T|SHEETNUMBERSPACESIZE=4|AREACOLOR=16317695|SNAPGRIDON=T|SNAPGRIDSIZE=10|VISIBLEGRIDON=T|VISIBLEGRIDSIZE=10|CUSTOMX=1500|CUSTOMY=1000|USECUSTOMSHEET=T");

        int totalPins = Math.Max(2, pins.Count);
        int halfPins = (totalPins + 1) / 2;
        int boxHeight = Math.Max(40, (halfPins + 1) * 10);

        // RECORD=1: SchComponent
        sb.AppendLine(
            $"|RECORD=1|LIBREFERENCE={part.LibRef}|COMPONENTDESCRIPTION={SanitizeAsciiValue(part.Description)}" +
            $"|PARTCOUNT={part.PartCount + 1}|DISPLAYMODECOUNT=1|INDEXINSHEET=1|OWNERPARTID=-1" +
            $"|LOCATION.X=400|LOCATION.Y=400|CURRENTPARTID=1|SOURCELIBRARYNAME=*|TARGETFILENAME=*");

        // RECORD=14: Dikdörtgen gövde
        sb.AppendLine(
            $"|RECORD=14|OWNERINDEX=1|ISNOTACCESIBLE=T|INDEXINSHEET=1|OWNERPARTID=1" +
            $"|LOCATION.X=350|LOCATION.Y={400 - boxHeight}|CORNER.X=450|CORNER.Y=400" +
            $"|LINEWIDTH=1|COLOR=128|AREACOLOR=11599871|ISSOLID=T");

        // RECORD=2: Pinler
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            bool isLeftSide = i < halfPins;
            int rowIdx = isLeftSide ? i : (i - halfPins);
            int pinX = isLeftSide ? 350 : 450;
            int pinY = 390 - (rowIdx * 10);
            int orientation = isLeftSide ? 2 : 0; // 2=Left (180 deg), 0=Right (0 deg)

            sb.AppendLine(
                $"|RECORD=2|OWNERINDEX=1|OWNERPARTID={Math.Max(1, pin.PartId)}" +
                $"|FORMALTYPE=1|ELECTRICAL=4|PINCONGLOMERATE={32 + orientation}" +
                $"|PINLENGTH=20|LOCATION.X={pinX}|LOCATION.Y={pinY}" +
                $"|NAME={SanitizeAsciiValue(pin.Name)}|DESIGNATOR={SanitizeAsciiValue(pin.Number)}");
        }

        // RECORD=34: Designator
        sb.AppendLine(
            $"|RECORD=34|OWNERINDEX=1|INDEXINSHEET=-1|OWNERPARTID=-1" +
            $"|LOCATION.X=350|LOCATION.Y=405|COLOR=8388608|FONTID=1|TEXT={part.DesignatorPrefix}|NAME=Designator");

        // RECORD=41: Comment ve BOM Parametreleri
        sb.AppendLine(
            $"|RECORD=41|OWNERINDEX=1|INDEXINSHEET=-1|OWNERPARTID=-1" +
            $"|LOCATION.X=350|LOCATION.Y={390 - boxHeight}|COLOR=8388608|FONTID=1|TEXT={SanitizeAsciiValue(part.Comment)}|NAME=Comment");

        int paramIndex = 2;
        foreach (var kvp in part.BuildBomParameterDictionary())
        {
            sb.AppendLine(
                $"|RECORD=41|OWNERINDEX=1|INDEXINSHEET={paramIndex++}|OWNERPARTID=-1" +
                $"|LOCATION.X=350|LOCATION.Y=350|ISHIDDEN=T|COLOR=8388608|FONTID=1" +
                $"|NAME={SanitizeAsciiValue(kvp.Key)}|TEXT={SanitizeAsciiValue(kvp.Value)}");
        }

        // RECORD=45: Footprint Implementation
        sb.AppendLine(
            $"|RECORD=45|OWNERINDEX=1|INDEXINSHEET=-1|MODELNAME={SanitizeAsciiValue(part.FootprintRef)}" +
            $"|MODELTYPE=PCBLIB|DATAFILECOUNT=1|MODELDATAFILEENTITY0={SanitizeAsciiValue(part.FootprintRef)}" +
            $"|MODELDATAFILEKIND0=PCBLib|ISCURRENT=T");

        return sb.ToString();
    }

    private static string BuildAltiumAsciiPcbDoc(LcscPartMetadata part)
    {
        var sb = new StringBuilder();
        sb.AppendLine("|RECORD=Board|FILENAME=" + SanitizeAsciiValue(part.LcscId) + "_Raw.PcbDoc|VERSION=5.0");
        sb.AppendLine(
            $"|RECORD=Component|PATTERN={SanitizeAsciiValue(part.FootprintRef)}" +
            $"|SOURCEDESIGNATOR={part.DesignatorPrefix}|X=2000mil|Y=2000mil|LAYER=TOP" +
            $"|COMMENT={SanitizeAsciiValue(part.Comment)}|SOURCEDESCRIPTION={SanitizeAsciiValue(part.Description)}");
        return sb.ToString();
    }

    private static string SanitizeAsciiValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value
            .Replace("|", "/", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
