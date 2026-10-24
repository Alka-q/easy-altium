using System.Text.Json;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;

namespace EasyAltium.Core.Library;

/// <summary>
/// Mod A (SamacSys / Hızlı Kütüphane Modu) yöneticisi.
/// Parçaları ortak LCSC_Master.SchLib ve LCSC_Master.PcbLib dosyaları üzerinden yönetir
/// ve eklenen parçaların indeksini tutar.
/// </summary>
public sealed class MasterLibraryManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public MasterLibraryManager(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string IndexFilePath => Path.Combine(_config.LibraryRootDirectory, "lcsc_master_index.json");

    public async Task<(string SchLibPath, string PcbLibPath)> RegisterComponentInMasterLibraryAsync(
        LcscPartMetadata part,
        string exportedSchLibPath,
        string exportedPcbLibPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        _config.EnsureDirectoriesExist();

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string masterSch = _config.MasterSchLibPath;
            string masterPcb = _config.MasterPcbLibPath;

            if (!string.IsNullOrWhiteSpace(exportedSchLibPath) &&
                File.Exists(exportedSchLibPath) &&
                !string.Equals(Path.GetFullPath(exportedSchLibPath), Path.GetFullPath(masterSch), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(exportedSchLibPath, masterSch, overwrite: true);
            }

            if (!string.IsNullOrWhiteSpace(exportedPcbLibPath) &&
                File.Exists(exportedPcbLibPath) &&
                !string.Equals(Path.GetFullPath(exportedPcbLibPath), Path.GetFullPath(masterPcb), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(exportedPcbLibPath, masterPcb, overwrite: true);
            }

            if (File.Exists(masterPcb))
            {
                PcbLib3DModelPatcher.TryPatchPcbLib3DRotation(masterPcb, part);
            }

            var entries = await LoadIndexInternalAsync(cancellationToken).ConfigureAwait(false);
            entries.RemoveAll(x => string.Equals(x.LcscId, part.LcscId, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, new MasterLibraryEntry
            {
                LcscId = part.LcscId,
                Mpn = part.Mpn,
                Manufacturer = part.Manufacturer,
                Package = part.Package,
                JlcpcbClass = part.JlcpcbClass,
                LibRef = part.LibRef,
                FootprintRef = part.FootprintRef,
                SchLibPath = masterSch,
                PcbLibPath = masterPcb,
                UpdatedAtUtc = DateTime.UtcNow
            });

            string json = JsonSerializer.Serialize(entries, JsonOptions);
            await File.WriteAllTextAsync(IndexFilePath, json, cancellationToken).ConfigureAwait(false);

            return (masterSch, masterPcb);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<IReadOnlyList<MasterLibraryEntry>> GetRegisteredEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadIndexInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<List<MasterLibraryEntry>> LoadIndexInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(IndexFilePath))
            return [];

        try
        {
            string json = await File.ReadAllTextAsync(IndexFilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<MasterLibraryEntry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public sealed class MasterLibraryEntry
{
    public string LcscId { get; set; } = string.Empty;
    public string Mpn { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    public string JlcpcbClass { get; set; } = "Extended";
    public string LibRef { get; set; } = string.Empty;
    public string FootprintRef { get; set; } = string.Empty;
    public string SchLibPath { get; set; } = string.Empty;
    public string PcbLibPath { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
