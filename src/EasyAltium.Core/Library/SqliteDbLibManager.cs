using System.Globalization;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;
using Microsoft.Data.Sqlite;

namespace EasyAltium.Core.Library;

/// <summary>
/// Mod B (Profesyonel SQLite .DbLib Modu) veritabanı yöneticisi.
/// lcsc_components.sqlite içinde 7 kategori tablosunu (Resistors, Capacitors, Inductors, ICs,
/// Connectors, Discrete, Misc) oluşturur ve bileşen verilerini UPSERT olarak yazar.
/// </summary>
public sealed class SqliteDbLibManager
{
    public static readonly IReadOnlyList<ComponentCategory> AllCategories =
        Enum.GetValues<ComponentCategory>();

    private readonly AppConfig _config;

    public SqliteDbLibManager(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _config.SqliteDatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true
    }.ToString();

    /// <summary>
    /// SQLite veritabanını ve 7 kategori tablosunu (Resistors, Capacitors, Inductors, ICs,
    /// Connectors, Discrete, Misc) oluşturur.
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        _config.EnsureDirectoriesExist();

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (ComponentCategory category in AllCategories)
        {
            string tableName = GetSafeTableName(category);
            string createSql = $"""
                CREATE TABLE IF NOT EXISTS [{tableName}] (
                    [LCSC_Part]       TEXT PRIMARY KEY NOT NULL,
                    [MPN]             TEXT NOT NULL,
                    [Manufacturer]    TEXT NOT NULL DEFAULT '',
                    [Description]     TEXT NOT NULL DEFAULT '',
                    [Package]         TEXT NOT NULL DEFAULT '',
                    [JLCPCB_Class]    TEXT NOT NULL DEFAULT 'Extended',
                    [Datasheet_URL]   TEXT NOT NULL DEFAULT '',
                    [Library Ref]     TEXT NOT NULL,
                    [Library Path]    TEXT NOT NULL,
                    [Footprint Ref]   TEXT NOT NULL,
                    [Footprint Path]  TEXT NOT NULL,
                    [Value]           TEXT NOT NULL DEFAULT '',
                    [Stock]           INTEGER NOT NULL DEFAULT 0,
                    [Unit_Price_USD]   TEXT NOT NULL DEFAULT '0.0000',
                    [Step_Model_Path] TEXT NOT NULL DEFAULT '',
                    [Updated_At_Utc]  TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS [IX_{tableName}_MPN] ON [{tableName}] ([MPN]);
                """;

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = createSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Bileşeni kendi kategori tablosuna UPSERT (INSERT ... ON CONFLICT DO UPDATE) olarak kaydeder.
    /// </summary>
    public async Task UpsertComponentAsync(
        LcscPartMetadata part,
        string libraryRef,
        string libraryPath,
        string footprintRef,
        string footprintPath,
        string stepModelPath = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);

        string tableName = GetSafeTableName(part.Category);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        string upsertSql = $"""
            INSERT INTO [{tableName}] (
                [LCSC_Part],
                [MPN],
                [Manufacturer],
                [Description],
                [Package],
                [JLCPCB_Class],
                [Datasheet_URL],
                [Library Ref],
                [Library Path],
                [Footprint Ref],
                [Footprint Path],
                [Value],
                [Stock],
                [Unit_Price_USD],
                [Step_Model_Path],
                [Updated_At_Utc]
            )
            VALUES (
                $lcscPart,
                $mpn,
                $manufacturer,
                $description,
                $package,
                $jlcpcbClass,
                $datasheetUrl,
                $libraryRef,
                $libraryPath,
                $footprintRef,
                $footprintPath,
                $value,
                $stock,
                $unitPriceUsd,
                $stepModelPath,
                $updatedAtUtc
            )
            ON CONFLICT([LCSC_Part]) DO UPDATE SET
                [MPN]             = excluded.[MPN],
                [Manufacturer]    = excluded.[Manufacturer],
                [Description]     = excluded.[Description],
                [Package]         = excluded.[Package],
                [JLCPCB_Class]    = excluded.[JLCPCB_Class],
                [Datasheet_URL]   = excluded.[Datasheet_URL],
                [Library Ref]     = excluded.[Library Ref],
                [Library Path]    = excluded.[Library Path],
                [Footprint Ref]   = excluded.[Footprint Ref],
                [Footprint Path]  = excluded.[Footprint Path],
                [Value]           = excluded.[Value],
                [Stock]           = excluded.[Stock],
                [Unit_Price_USD]  = excluded.[Unit_Price_USD],
                [Step_Model_Path] = excluded.[Step_Model_Path],
                [Updated_At_Utc]  = excluded.[Updated_At_Utc];
            """;

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = upsertSql;
        cmd.Parameters.AddWithValue("$lcscPart", part.LcscId);
        cmd.Parameters.AddWithValue("$mpn", part.Mpn);
        cmd.Parameters.AddWithValue("$manufacturer", part.Manufacturer);
        cmd.Parameters.AddWithValue("$description", LcscPartMetadata.NormalizeEngineeringUnitsForAltium(part.Description));
        cmd.Parameters.AddWithValue("$package", part.Package);
        cmd.Parameters.AddWithValue("$jlcpcbClass", part.JlcpcbClass);
        cmd.Parameters.AddWithValue("$datasheetUrl", part.DatasheetUrl);
        cmd.Parameters.AddWithValue("$libraryRef", libraryRef);
        cmd.Parameters.AddWithValue("$libraryPath", libraryPath);
        cmd.Parameters.AddWithValue("$footprintRef", footprintRef);
        cmd.Parameters.AddWithValue("$footprintPath", footprintPath);
        cmd.Parameters.AddWithValue("$value", LcscPartMetadata.NormalizeEngineeringUnitsForAltium(part.Value));
        cmd.Parameters.AddWithValue("$stock", part.Stock);
        cmd.Parameters.AddWithValue("$unitPriceUsd", part.UnitPriceUsd.ToString("0.0000", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$stepModelPath", stepModelPath ?? string.Empty);
        cmd.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tüm kategori tablolarındaki toplam kayıtlı bileşen sayısını döndürür.
    /// </summary>
    public async Task<int> GetTotalComponentCountAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_config.SqliteDatabasePath))
            return 0;

        await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        int total = 0;
        foreach (ComponentCategory category in AllCategories)
        {
            string tableName = GetSafeTableName(category);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(1) FROM [{tableName}];";
            object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is long count)
                total += (int)count;
        }

        return total;
    }

    /// <summary>
    /// Yerel SQLite .DbLib veritabanındaki kayıtlı bileşenleri Component Browser formatında sorgular.
    /// </summary>
    public async Task<List<CatalogPartItem>> QueryStoredCatalogPartsAsync(
        ComponentCategory? filterCategory = null,
        string? searchKeyword = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CatalogPartItem>();
        if (!File.Exists(_config.SqliteDatabasePath))
            return results;

        await InitializeDatabaseAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<ComponentCategory> categoriesToScan = filterCategory.HasValue
            ? new[] { filterCategory.Value }
            : AllCategories;

        foreach (ComponentCategory category in categoriesToScan)
        {
            string tableName = GetSafeTableName(category);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    [LCSC_Part],
                    [MPN],
                    [Manufacturer],
                    [Description],
                    [Package],
                    [JLCPCB_Class],
                    [Datasheet_URL],
                    [Footprint Ref],
                    [Stock],
                    [Unit_Price_USD]
                FROM [{tableName}]
                ORDER BY [Updated_At_Utc] DESC;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string lcscId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                string mpn = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                string mfr = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                string desc = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                string pkg = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                string jlcClass = reader.IsDBNull(5) ? "Extended Part" : reader.GetString(5);
                string dsUrl = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                string fpRef = reader.IsDBNull(7) ? pkg : reader.GetString(7);
                int stock = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
                string priceStr = reader.IsDBNull(9) ? "0" : reader.GetString(9);

                if (!string.IsNullOrWhiteSpace(searchKeyword))
                {
                    string kw = searchKeyword.Trim();
                    if (!lcscId.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !mpn.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !mfr.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !desc.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !pkg.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                var tiers = new List<CatalogPriceTier>();
                if (decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal unitPrice) && unitPrice > 0)
                {
                    tiers.Add(new CatalogPriceTier { MinQuantity = 1, UnitPriceUsd = unitPrice });
                }

                results.Add(new CatalogPartItem
                {
                    LcscId = lcscId,
                    Mpn = mpn,
                    Manufacturer = mfr,
                    Description = desc,
                    LcscPartName = desc,
                    Package = pkg,
                    FootprintTitle = !string.IsNullOrWhiteSpace(fpRef) ? fpRef : pkg,
                    JlcpcbPartClass = jlcClass,
                    DatasheetUrl = !string.IsNullOrWhiteSpace(dsUrl) ? dsUrl : $"https://www.lcsc.com/search?q={lcscId}",
                    ProductUrl = $"https://www.lcsc.com/search?q={lcscId}",
                    LcscStock = stock,
                    JlcpcbStock = stock,
                    PriceTiers = tiers
                });
            }
        }

        return results;
    }

    public static string GetSafeTableName(ComponentCategory category) => category switch
    {
        ComponentCategory.Resistors => "Resistors",
        ComponentCategory.Capacitors => "Capacitors",
        ComponentCategory.Inductors => "Inductors",
        ComponentCategory.ICs => "ICs",
        ComponentCategory.Connectors => "Connectors",
        ComponentCategory.Discrete => "Discrete",
        _ => "Misc"
    };
}
