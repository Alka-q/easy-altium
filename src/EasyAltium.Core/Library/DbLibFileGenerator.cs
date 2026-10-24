using System.Text;
using EasyAltium.Core.Configuration;
using EasyAltium.Core.Models;

namespace EasyAltium.Core.Library;

/// <summary>
/// Altium Designer 26.10.1 (64-bit) ile tam uyumlu SQLite .DbLib yapılandırma dosyasını üretir.
/// </summary>
public sealed class DbLibFileGenerator
{
    private readonly AppConfig _config;

    public DbLibFileGenerator(AppConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// EasyAltium_LCSC.DbLib dosyasını oluşturur veya günceller.
    /// </summary>
    public async Task<string> EnsureDbLibFileAsync(CancellationToken cancellationToken = default)
    {
        _config.EnsureDirectoriesExist();

        string dbLibPath = _config.DbLibFilePath;
        string sqlitePath = _config.SqliteDatabasePath;

        string content = BuildDbLibContent(sqlitePath);
        await File.WriteAllTextAsync(dbLibPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        return dbLibPath;
    }

    public static string BuildDbLibContent(string sqliteDatabaseFullPath)
    {
        // Altium 26.10.1 (64-bit) SQLite3 ODBC bağlantı dizesi
        string connectionString =
            $"Provider=MSDASQL.1;Persist Security Info=False;" +
            $"Extended Properties=\"Driver={{SQLite3 ODBC Driver}};Database={sqliteDatabaseFullPath};StepAPI=0;SyncPragma=NORMAL;NoTXN=0;Timeout=10000;ShortNames=0;LongNames=0;NoCreat=0;NoWCHAR=0;FKSupport=0;JournalMode=;OEMCP=0;LoadExt=;BigInt=0;JDConv=0;\"";

        var sb = new StringBuilder();
        sb.AppendLine("[OutputDatabaseLinkFile]");
        sb.AppendLine("Version=1.0");
        sb.AppendLine();
        sb.AppendLine("[DatabaseLinks]");
        sb.AppendLine($"ConnectionString={connectionString}");
        sb.AppendLine("AddMode=3");
        sb.AppendLine("RemoveMode=1");
        sb.AppendLine("UpdateMode=2");
        sb.AppendLine("ViewMode=0");
        sb.AppendLine("LeftQuote=[");
        sb.AppendLine("RightQuote=]");
        sb.AppendLine("QuoteTableNames=1");
        sb.AppendLine("UseTableSchemaName=0");
        sb.AppendLine("DefaultColumnType=VARCHAR(255)");
        sb.AppendLine("LibraryDatabaseType=");
        sb.AppendLine("LibraryDatabasePath=");
        sb.AppendLine("DatabasePathRelative=0");
        sb.AppendLine("TopPanelCollapsed=0");
        sb.AppendLine("LibrarySearchPath=");
        sb.AppendLine("OrcadMultiValueDelimiter=,");
        sb.AppendLine("SearchSubDirectories=1");
        sb.AppendLine("SchemaName=");
        sb.AppendLine("LastFocusedTable=ICs");
        sb.AppendLine();

        // Tablo ve Alan Eşleştirmeleri (Field Mappings)
        (string DbField, string AltiumField, int FieldType)[] fieldMappings =
        [
            ("LCSC_Part",       "LCSC_Part",        0), // 0 = Key / General
            ("Library Ref",     "[Library Ref]",    1), // 1 = Symbol Ref
            ("Library Path",    "[Library Path]",   2), // 2 = Symbol Path
            ("Footprint Ref",   "[Footprint Ref]",  3), // 3 = Footprint Ref
            ("Footprint Path",  "[Footprint Path]", 4), // 4 = Footprint Path
            ("MPN",             "MPN",              5), // 5 = Parameter
            ("Manufacturer",    "Manufacturer",     5),
            ("Description",     "[Description]",    5),
            ("Package",         "Package",          5),
            ("JLCPCB_Class",    "JLCPCB_Class",     5),
            ("Datasheet_URL",   "Datasheet_URL",    5),
            ("Value",           "Value",            5),
            ("Stock",           "Stock",            5),
            ("Unit_Price_USD",  "Unit_Price_USD",   5)
        ];

        int tableIndex = 1;
        foreach (ComponentCategory category in SqliteDbLibManager.AllCategories)
        {
            string tableName = SqliteDbLibManager.GetSafeTableName(category);
            sb.AppendLine($"[Table{tableIndex}]");
            sb.AppendLine("SchemaName=");
            sb.AppendLine($"TableName={tableName}");
            sb.AppendLine("Enabled=True");
            sb.AppendLine("UserWhere=0");
            sb.AppendLine("UserWhereText=");
            sb.AppendLine("Key=LCSC_Part");
            sb.AppendLine("Symbols=");
            sb.AppendLine("Footprints=");
            sb.AppendLine();

            for (int f = 0; f < fieldMappings.Length; f++)
            {
                var map = fieldMappings[f];
                sb.AppendLine($"[FieldMap{tableIndex}_{f + 1}]");
                sb.AppendLine($"Options=FieldName={tableName}.{map.DbField}|TableNameOnly={tableName}|FieldNameOnly={map.DbField}|FieldType={map.FieldType}|ParameterName={map.AltiumField}|VisibleOnAdd=False|AddMode=3|RemoveMode=1|UpdateMode=2");
            }

            sb.AppendLine();
            tableIndex++;
        }

        return sb.ToString();
    }
}
