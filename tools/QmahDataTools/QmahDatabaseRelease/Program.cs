using System.Collections;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;

using QMAH.DataTools;
using QMAH.Infrastructure.Data;

return await DatabaseReleaseProgram.RunAsync(args);

internal static class DatabaseReleaseProgram
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            if (args.Any(argument =>
                    string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase)))
            {
                PrintUsage();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(1));

            switch (command)
            {
                case "export-sql":
                    await ExportSqlAsync(
                        Require(options, "connection"),
                        Require(options, "database"),
                        Require(options, "output"));
                    break;
                case "compare":
                    await CompareAsync(
                        Require(options, "source"),
                        Require(options, "target"),
                        options.GetValueOrDefault("report"));
                    break;
                case "validate-ef":
                    await ValidateEfAsync(Require(options, "connection"));
                    break;
                case "scan-data":
                    await ScanDataAsync(
                        Require(options, "connection"),
                        options.GetValueOrDefault("report"));
                    break;
                case "restore-backup":
                    RestoreBackup(
                        Require(options, "connection"),
                        Require(options, "backup"),
                        Require(options, "database"),
                        Require(options, "data-directory"));
                    break;
                case "reset-password":
                    await IdentityCommands.ResetPasswordAsync(
                        Require(options, "connection"),
                        Require(options, "email"),
                        options.GetValueOrDefault("password"),
                        options.GetValueOrDefault("credentials"),
                        options.GetValueOrDefault("backup"));
                    break;
                case "seed-showcase-users":
                    await IdentityCommands.SeedShowcaseUsersAsync(
                        Require(options, "connection"),
                        options.GetValueOrDefault("credentials"),
                        options.GetValueOrDefault("backup"));
                    break;
                case "generate-showcase-data":
                    await ShowcaseDataCommands.GenerateAsync(
                        Require(options, "connection"),
                        ParseIntOption(options, "post-count", 288, 1, 512),
                        ParseIntOption(options, "order-count", 160, 1, 512),
                        ParseIntOption(options, "seed", 173, 0, int.MaxValue),
                        ParseIntOption(options, "activity-days", 30, 0, 3650),
                        ParseIntOption(options, "point-transaction-count", 80, 0, 10000),
                        ParseIntOption(options, "key-transaction-count", 80, 0, 10000),
                        ParseIntOption(options, "key-progress-transaction-count", 80, 0, 10000));
                    break;
                case "generate-showcase-ledger":
                {
                    await using var ledgerDb = CreateDbContext(Require(options, "connection"));
                    if (!await ledgerDb.Database.CanConnectAsync())
                        throw new InvalidOperationException("無法連線到 QMAH。請先建立同版本的本機資料庫。");

                    await using var ledgerTransaction = await ledgerDb.Database.BeginTransactionAsync();
                    var ledgerResult = await ShowcaseLedgerCommands.GenerateAsync(
                        ledgerDb,
                        ParseIntOption(options, "activity-days", 30, 0, 3650),
                        ParseIntOption(options, "point-transaction-count", 80, 0, 10000),
                        ParseIntOption(options, "key-transaction-count", 80, 0, 10000),
                        ParseIntOption(options, "key-progress-transaction-count", 80, 0, 10000),
                        ParseIntOption(options, "seed", 173, 0, int.MaxValue));
                    await ledgerDb.SaveChangesAsync();
                    await ledgerTransaction.CommitAsync();
                    Console.WriteLine(
                        $"SHOWCASE_LEDGER_GENERATED|daily-activities:{ledgerResult.DailyActivityCount}|login-activities:{ledgerResult.LoginActivityCount}|check-in-activities:{ledgerResult.CheckInActivityCount}|point-transactions:{ledgerResult.PointTransactionCount}|key-transactions:{ledgerResult.KeyTransactionCount}|key-progress-transactions:{ledgerResult.KeyProgressTransactionCount}|login-achievements:{ledgerResult.LoginAchievementCount}");
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown command: {command}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task ExportSqlAsync(string connectionString, string databaseName, string outputPath)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        EnsureDatabaseName(connection, databaseName);

        var serverConnection = new ServerConnection(connection);
        var server = new Server(serverConnection);
        var database = server.Databases[databaseName]
            ?? throw new InvalidOperationException($"Database '{databaseName}' was not found.");

        var tables = database.Tables.Cast<Table>()
            .Where(IsProjectTable)
            .OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Name, StringComparer.Ordinal)
            .ToArray();
        var sequences = database.Sequences.Cast<Sequence>()
            .OrderBy(sequence => sequence.Schema, StringComparer.Ordinal)
            .ThenBy(sequence => sequence.Name, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder(1024 * 1024);
        AppendHeader(builder, databaseName, database.Collation);
        AppendSchemas(builder, tables.Select(table => table.Schema).Concat(sequences.Select(sequence => sequence.Schema)));

        AppendSection(builder, "SEQUENCES");
        var sequenceOptions = CreateScriptingOptions();
        foreach (var sequence in sequences)
        {
            AppendScript(builder, sequence.Script(sequenceOptions));
        }

        var tableOptions = CreateScriptingOptions();
        tableOptions.DriPrimaryKey = true;
        tableOptions.DriUniqueKeys = false;
        tableOptions.DriDefaults = true;
        tableOptions.DriChecks = true;
        tableOptions.DriForeignKeys = false;
        tableOptions.Indexes = false;
        tableOptions.ClusteredIndexes = false;
        tableOptions.NonClusteredIndexes = false;
        tableOptions.Triggers = false;

        foreach (var table in tables)
        {
            AppendScript(builder, table.Script(tableOptions));
        }

        AppendSection(builder, "DATA");
        foreach (var table in tables)
        {
            await AppendTableDataAsync(connection, table, builder);
        }

        AppendSection(builder, "INDEXES");
        var indexOptions = CreateScriptingOptions();
        foreach (var table in tables)
        {
            foreach (Microsoft.SqlServer.Management.Smo.Index uniqueConstraint in table.Indexes.Cast<Microsoft.SqlServer.Management.Smo.Index>()
                         .Where(index => index.IndexKeyType == IndexKeyType.DriUniqueKey)
                         .OrderBy(index => index.Name, StringComparer.Ordinal))
            {
                AppendScript(builder, uniqueConstraint.Script(indexOptions));
            }

            foreach (Microsoft.SqlServer.Management.Smo.Index index in table.Indexes.Cast<Microsoft.SqlServer.Management.Smo.Index>()
                         .Where(index => !index.IsSystemObject && index.IndexKeyType == IndexKeyType.None)
                         .OrderBy(index => index.Name, StringComparer.Ordinal))
            {
                AppendScript(builder, index.Script(indexOptions));
            }
        }

        AppendSection(builder, "FOREIGN KEYS");
        var foreignKeyOptions = CreateScriptingOptions();
        foreignKeyOptions.DriForeignKeys = true;
        foreach (var table in tables)
        {
            foreach (ForeignKey foreignKey in table.ForeignKeys.Cast<ForeignKey>()
                         .OrderBy(key => key.Name, StringComparer.Ordinal))
            {
                AppendScript(builder, foreignKey.Script(foreignKeyOptions));
            }
        }

        AppendProgrammableObjects(database, builder);
        AppendSection(builder, "TRIGGERS");
        var triggerOptions = CreateScriptingOptions();
        foreach (var table in tables)
        {
            foreach (Trigger trigger in table.Triggers.Cast<Trigger>()
                         .OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                AppendScript(builder, trigger.Script(triggerOptions));
            }
        }

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, builder.ToString(), Utf8WithoutBom);
        Console.WriteLine($"SQL_EXPORT={fullPath}");
        Console.WriteLine($"TABLE_COUNT={tables.Length}");
    }

    private static void RestoreBackup(
        string connectionString,
        string backupPath,
        string databaseName,
        string dataDirectory)
    {
        var fullBackupPath = Path.GetFullPath(backupPath);
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullDataDirectory);

        using var sqlConnection = new SqlConnection(connectionString);
        var serverConnection = new ServerConnection(sqlConnection);
        var server = new Server(serverConnection);
        if (server.Databases.Contains(databaseName))
        {
            server.KillDatabase(databaseName);
        }

        var restore = new Restore
        {
            Action = RestoreActionType.Database,
            Database = databaseName,
            NoRecovery = false,
            ReplaceDatabase = true
        };
        restore.Devices.Add(new BackupDeviceItem(fullBackupPath, DeviceType.File));

        var fileList = restore.ReadFileList(server);
        var dataIndex = 0;
        var logIndex = 0;
        foreach (DataRow row in fileList.Rows)
        {
            var logicalName = Convert.ToString(row["LogicalName"], CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("Backup contains a file without a logical name.");
            var type = Convert.ToString(row["Type"], CultureInfo.InvariantCulture);
            var extension = type == "L" ? ".ldf" : ".mdf";
            var index = type == "L" ? logIndex++ : dataIndex++;
            var suffix = index == 0 ? string.Empty : $"-{index}";
            var physicalPath = Path.Combine(fullDataDirectory, databaseName + suffix + extension);
            restore.RelocateFiles.Add(new RelocateFile(logicalName, physicalPath));
        }

        restore.SqlRestore(server);
        Console.WriteLine($"RESTORED_DATABASE={databaseName}");
    }

    private static void AppendHeader(StringBuilder builder, string databaseName, string collation)
    {
        var quotedDatabase = QuoteName(databaseName);
        var literalDatabase = SqlUnicodeLiteral(databaseName);

        builder.AppendLine("SET NOCOUNT ON;");
        builder.AppendLine("SET XACT_ABORT ON;");
        builder.AppendLine("GO");
        builder.AppendLine($"IF DB_ID({literalDatabase}) IS NOT NULL");
        builder.AppendLine($"    THROW 51000, 'Database {EscapeSqlText(databaseName)} already exists. Drop or rename it before running this script.', 1;");
        builder.AppendLine("GO");
        builder.AppendLine($"CREATE DATABASE {quotedDatabase} COLLATE {collation};");
        builder.AppendLine("GO");
        builder.AppendLine($"ALTER DATABASE {quotedDatabase} SET RECOVERY SIMPLE;");
        builder.AppendLine("GO");
        builder.AppendLine($"USE {quotedDatabase};");
        builder.AppendLine("GO");
    }

    private static void AppendSchemas(StringBuilder builder, IEnumerable<string> schemaNames)
    {
        AppendSection(builder, "SCHEMAS");
        foreach (var schema in schemaNames
                     .Where(schema => !schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(schema => schema, StringComparer.Ordinal))
        {
            builder.AppendLine($"CREATE SCHEMA {QuoteName(schema)} AUTHORIZATION [dbo];");
            builder.AppendLine("GO");
        }
    }

    private static async Task AppendTableDataAsync(SqlConnection connection, Table table, StringBuilder builder)
    {
        var insertColumns = table.Columns.Cast<Column>()
            .Where(column => !column.Computed && !IsRowVersion(column))
            .OrderBy(column => column.ID)
            .ToArray();

        if (insertColumns.Length == 0)
        {
            return;
        }

        var fullName = $"{QuoteName(table.Schema)}.{QuoteName(table.Name)}";
        var orderColumns = GetStableOrderColumns(table, insertColumns);
        var selectColumns = string.Join(", ", insertColumns.Select(column => QuoteName(column.Name)));
        var orderBy = string.Join(", ", orderColumns.Select(column => QuoteName(column.Name)));
        var sql = $"SELECT {selectColumns} FROM {fullName} ORDER BY {orderBy};";

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        if (!reader.HasRows)
        {
            return;
        }

        builder.AppendLine($"-- {table.Schema}.{table.Name}");
        var hasIdentity = insertColumns.Any(column => column.Identity);
        if (hasIdentity)
        {
            builder.AppendLine($"SET IDENTITY_INSERT {fullName} ON;");
        }

        const int rowsPerInsert = 200;
        var rowInBatch = 0;
        while (await reader.ReadAsync())
        {
            if (rowInBatch == 0)
            {
                builder.Append("INSERT INTO ").Append(fullName).Append(" (")
                    .Append(string.Join(", ", insertColumns.Select(column => QuoteName(column.Name))))
                    .AppendLine(") VALUES");
            }

            builder.Append("    (");
            for (var ordinal = 0; ordinal < insertColumns.Length; ordinal++)
            {
                if (ordinal > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(ToSqlLiteral(reader, ordinal, insertColumns[ordinal]));
            }

            rowInBatch++;
            var isLastInBatch = rowInBatch == rowsPerInsert;
            builder.AppendLine(isLastInBatch ? ");" : "),");
            if (isLastInBatch)
            {
                rowInBatch = 0;
            }
        }

        if (rowInBatch > 0)
        {
            builder.Length -= Environment.NewLine.Length + 1;
            builder.AppendLine(";");
        }

        if (hasIdentity)
        {
            builder.AppendLine($"SET IDENTITY_INSERT {fullName} OFF;");
        }

        builder.AppendLine("GO");
    }

    private static Column[] GetStableOrderColumns(Table table, Column[] insertColumns)
    {
        var primaryKey = table.Indexes.Cast<Microsoft.SqlServer.Management.Smo.Index>().FirstOrDefault(index => index.IndexKeyType == IndexKeyType.DriPrimaryKey);
        if (primaryKey is not null)
        {
            return primaryKey.IndexedColumns.Cast<IndexedColumn>()
                .Select(indexed => insertColumns.Single(column => column.Name == indexed.Name))
                .ToArray();
        }

        var uniqueIndex = table.Indexes.Cast<Microsoft.SqlServer.Management.Smo.Index>()
            .Where(index => index.IsUnique && index.IndexedColumns.Count > 0)
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (uniqueIndex is not null)
        {
            return uniqueIndex.IndexedColumns.Cast<IndexedColumn>()
                .Where(indexed => !indexed.IsIncluded)
                .Select(indexed => insertColumns.Single(column => column.Name == indexed.Name))
                .ToArray();
        }

        throw new InvalidOperationException(
            $"Table {table.Schema}.{table.Name} has data but no primary or unique key for deterministic ordering.");
    }

    private static string ToSqlLiteral(SqlDataReader reader, int ordinal, Column column)
    {
        if (reader.IsDBNull(ordinal))
        {
            return "NULL";
        }

        var value = reader.GetValue(ordinal);
        var type = column.DataType.SqlDataType;
        return type switch
        {
            SqlDataType.NVarChar or SqlDataType.NVarCharMax or SqlDataType.NChar or SqlDataType.NText or SqlDataType.Xml
                => SqlUnicodeLiteral(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            SqlDataType.VarChar or SqlDataType.VarCharMax or SqlDataType.Char or SqlDataType.Text
                => SqlLiteral(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            SqlDataType.Bit => (bool)value ? "1" : "0",
            SqlDataType.TinyInt or SqlDataType.SmallInt or SqlDataType.Int or SqlDataType.BigInt
                => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            SqlDataType.Decimal or SqlDataType.Numeric or SqlDataType.Money or SqlDataType.SmallMoney
                => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            SqlDataType.Float => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            SqlDataType.Real => Convert.ToSingle(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
            SqlDataType.UniqueIdentifier => $"'{((Guid)value):D}'",
            SqlDataType.Date => $"CONVERT(date, '{((DateTime)value):yyyy-MM-dd}', 23)",
            SqlDataType.DateTime => $"CONVERT(datetime, '{((DateTime)value):yyyy-MM-ddTHH:mm:ss.fff}', 126)",
            SqlDataType.SmallDateTime => $"CONVERT(smalldatetime, '{((DateTime)value):yyyy-MM-ddTHH:mm:ss}', 126)",
            SqlDataType.DateTime2 => $"CONVERT(datetime2({column.DataType.NumericScale}), '{((DateTime)value).ToString("O", CultureInfo.InvariantCulture)}', 126)",
            SqlDataType.DateTimeOffset => $"CONVERT(datetimeoffset({column.DataType.NumericScale}), '{((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture)}', 127)",
            SqlDataType.Time => $"CONVERT(time({column.DataType.NumericScale}), '{((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture)}')",
            SqlDataType.Binary or SqlDataType.VarBinary or SqlDataType.VarBinaryMax or SqlDataType.Image
                => "0x" + Convert.ToHexString((byte[])value),
            _ => throw new NotSupportedException(
                $"SQL serialization is not implemented for {((Table)column.Parent).Name}.{column.Name} ({type}).")
        };
    }

    private static void AppendProgrammableObjects(Database database, StringBuilder builder)
    {
        var options = CreateScriptingOptions();
        AppendSection(builder, "PROGRAMMABLE OBJECTS");

        foreach (UserDefinedFunction function in database.UserDefinedFunctions.Cast<UserDefinedFunction>()
                     .Where(item => !item.IsSystemObject && !IsDiagramObject(item.Schema, item.Name))
                     .OrderBy(item => item.Schema, StringComparer.Ordinal)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            AppendScript(builder, function.Script(options));
        }

        foreach (View view in database.Views.Cast<View>()
                     .Where(item => !item.IsSystemObject)
                     .OrderBy(item => item.Schema, StringComparer.Ordinal)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            AppendScript(builder, view.Script(options));
        }

        foreach (StoredProcedure procedure in database.StoredProcedures.Cast<StoredProcedure>()
                     .Where(item => !item.IsSystemObject && !IsDiagramObject(item.Schema, item.Name))
                     .OrderBy(item => item.Schema, StringComparer.Ordinal)
                     .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            AppendScript(builder, procedure.Script(options));
        }
    }

    private static async Task CompareAsync(string sourceConnection, string targetConnection, string? reportPath)
    {
        var source = await CaptureDatabaseAsync(sourceConnection);
        var target = await CaptureDatabaseAsync(targetConnection);
        var differences = new List<string>();

        CompareDictionary("schema", source.SchemaHashes, target.SchemaHashes, differences);
        CompareDictionary("data", source.DataHashes, target.DataHashes, differences);

        var report = new ParityReport(
            differences.Count == 0,
            source.SchemaHashes.Count,
            source.DataHashes.Count,
            source.RowCounts,
            differences);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, json + Environment.NewLine, Utf8WithoutBom);
        }

        if (differences.Count > 0)
        {
            throw new InvalidOperationException("Database parity validation failed.");
        }
    }

    private static async Task<DatabaseSnapshot> CaptureDatabaseAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var schemaHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in MetadataQueries)
        {
            schemaHashes[definition.Key] = await HashQueryAsync(connection, definition.Value);
        }

        var tables = await GetTableNamesAsync(connection);
        var dataHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var rowCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var data = await HashTableAsync(connection, table.Schema, table.Table);
            dataHashes[$"{table.Schema}.{table.Table}"] = data.Hash;
            rowCounts[$"{table.Schema}.{table.Table}"] = data.RowCount;
        }

        return new DatabaseSnapshot(schemaHashes, dataHashes, rowCounts);
    }

    private static async Task<(string Hash, long RowCount)> HashTableAsync(
        SqlConnection connection,
        string schema,
        string table)
    {
        var columns = new List<string>();
        var orderColumns = new List<string>();
        await using (var command = new SqlCommand(TableColumnQuery, connection))
        {
            command.Parameters.AddWithValue("@schema", schema);
            command.Parameters.AddWithValue("@table", table);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
                if (reader.GetBoolean(1))
                {
                    orderColumns.Add(reader.GetString(0));
                }
            }
        }

        if (orderColumns.Count == 0)
        {
            throw new InvalidOperationException($"Table {schema}.{table} has no stable primary-key order.");
        }

        var sql = $"SELECT {string.Join(",", columns.Select(QuoteName))} FROM {QuoteName(schema)}.{QuoteName(table)} ORDER BY {string.Join(",", orderColumns.Select(QuoteName))};";
        await using var dataCommand = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var dataReader = await dataCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long rowCount = 0;
        while (await dataReader.ReadAsync())
        {
            rowCount++;
            for (var ordinal = 0; ordinal < dataReader.FieldCount; ordinal++)
            {
                AppendHashValue(hash, dataReader.IsDBNull(ordinal) ? null : dataReader.GetValue(ordinal));
            }
        }

        return (Convert.ToHexString(hash.GetHashAndReset()), rowCount);
    }

    private static async Task<string> HashQueryAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (await reader.ReadAsync())
        {
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                AppendHashValue(hash, reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashValue(IncrementalHash hash, object? value)
    {
        byte[] payload;
        if (value is null)
        {
            payload = [0];
        }
        else
        {
            var text = value switch
            {
                byte[] bytes => Convert.ToHexString(bytes),
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
            payload = Utf8WithoutBom.GetBytes(value.GetType().FullName + ":" + text);
        }

        hash.AppendData(BitConverter.GetBytes(payload.Length));
        hash.AppendData(payload);
    }

    private static async Task ValidateEfAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<QmahDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var context = new QmahDbContext(options);
        if (!await context.Database.CanConnectAsync())
        {
            throw new InvalidOperationException("QmahDbContext could not connect to the rebuilt database.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var mappedTables = context.Model.GetEntityTypes()
            .Select(entity => new
            {
                Entity = entity,
                Schema = entity.GetSchema() ?? "dbo",
                Table = entity.GetTableName()
            })
            .Where(item => item.Table is not null)
            .GroupBy(item => (item.Schema, item.Table!))
            .OrderBy(group => group.Key.Schema, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Item2, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in mappedTables)
        {
            var store = StoreObjectIdentifier.Table(group.Key.Item2, group.Key.Schema);
            var columns = group.SelectMany(item => item.Entity.GetProperties())
                .Select(property => property.GetColumnName(store))
                .Where(name => name is not null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => QuoteName(name!));
            var sql = $"SELECT TOP (0) {string.Join(",", columns)} FROM {QuoteName(group.Key.Schema)}.{QuoteName(group.Key.Item2)};";
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        Console.WriteLine($"EF_TABLE_COUNT={mappedTables.Length}");
        Console.WriteLine("EF_MODEL_VALID=true");
    }

    private static async Task ScanDataAsync(string connectionString, string? reportPath)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        var findings = new List<object>();
        var candidates = await GetTextColumnsAsync(connection);
        foreach (var candidate in candidates)
        {
            var sql = $"SELECT COUNT_BIG(*) FROM {QuoteName(candidate.Schema)}.{QuoteName(candidate.Table)} WHERE LOWER(LTRIM(RTRIM({QuoteName(candidate.Column)}))) IN (N'aaa',N'test123',N'temp');";
            await using var command = new SqlCommand(sql, connection);
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (count > 0)
            {
                findings.Add(new { candidate.Schema, candidate.Table, candidate.Column, Count = count });
            }
        }

        var json = JsonSerializer.Serialize(new { SuspiciousExactValues = findings }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, json + Environment.NewLine, Utf8WithoutBom);
        }
    }

    private static async Task<List<(string Schema, string Table)>> GetTableNamesAsync(SqlConnection connection)
    {
        var result = new List<(string, string)>();
        await using var command = new SqlCommand(ProjectTableQuery, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<List<(string Schema, string Table, string Column)>> GetTextColumnsAsync(SqlConnection connection)
    {
        var result = new List<(string, string, string)>();
        await using var command = new SqlCommand(TextColumnQuery, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    private static void CompareDictionary(
        string category,
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> target,
        ICollection<string> differences)
    {
        foreach (var key in source.Keys.Union(target.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            source.TryGetValue(key, out var sourceHash);
            target.TryGetValue(key, out var targetHash);
            if (!string.Equals(sourceHash, targetHash, StringComparison.Ordinal))
            {
                differences.Add($"{category}:{key}");
            }
        }
    }

    private static ScriptingOptions CreateScriptingOptions() => new()
    {
        AnsiFile = false,
        AppendToFile = false,
        BatchSize = 1,
        Bindings = true,
        ClusteredIndexes = true,
        ContinueScriptingOnError = false,
        ConvertUserDefinedDataTypesToBaseType = false,
        DdlHeaderOnly = false,
        DdlBodyOnly = false,
        EnforceScriptingOptions = true,
        ExtendedProperties = false,
        FullTextIndexes = true,
        IncludeDatabaseContext = false,
        IncludeHeaders = false,
        IncludeIfNotExists = false,
        NoCommandTerminator = false,
        NoCollation = false,
        NoFileGroup = true,
        NonClusteredIndexes = true,
        SchemaQualify = true,
        ScriptBatchTerminator = true,
        ScriptData = false,
        ScriptDrops = false,
        ScriptOwner = false,
        ScriptSchema = true,
        Statistics = false,
        TargetDatabaseEngineType = DatabaseEngineType.Standalone,
        TargetServerVersion = SqlServerVersion.Version150,
        Triggers = true,
        WithDependencies = false
    };

    private static void AppendScript(StringBuilder builder, IEnumerable scripts)
    {
        foreach (var item in scripts)
        {
            var text = Convert.ToString(item, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.AppendLine(NormalizeNewLines(text).TrimEnd());
            if (!text.TrimEnd().EndsWith("GO", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("GO");
            }
        }
    }

    private static void AppendSection(StringBuilder builder, string name)
    {
        builder.AppendLine();
        builder.AppendLine($"-- {name}");
    }

    private static bool IsProjectTable(Table table) =>
        !table.IsSystemObject && !IsDiagramObject(table.Schema, table.Name);

    private static bool IsDiagramObject(string schema, string name) =>
        schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) &&
        (name.Equals("sysdiagrams", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("diagram", StringComparison.OrdinalIgnoreCase));

    private static bool IsRowVersion(Column column) =>
        column.DataType.SqlDataType is SqlDataType.Timestamp;

    private static string QuoteName(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string SqlUnicodeLiteral(string value) => SqlStringExpression(value, true);
    private static string SqlLiteral(string value) => SqlStringExpression(value, false);

    private static string SqlStringExpression(string value, bool unicode)
    {
        var parts = new List<string>();
        var segment = new StringBuilder();

        void FlushSegment()
        {
            if (segment.Length == 0)
            {
                return;
            }

            parts.Add((unicode ? "N'" : "'") + EscapeSqlText(segment.ToString()) + "'");
            segment.Clear();
        }

        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\t' || char.IsControl(character))
            {
                FlushSegment();
                parts.Add((unicode ? "NCHAR" : "CHAR") + $"({(int)character})");
            }
            else
            {
                segment.Append(character);
            }
        }

        FlushSegment();
        return parts.Count == 0 ? (unicode ? "N''" : "''") : string.Join(" + ", parts);
    }
    private static string EscapeSqlText(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string NormalizeNewLines(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n');
        return string.Join("\r\n", lines.Select(line => line.TrimEnd()));
    }

    private static void EnsureDatabaseName(SqlConnection connection, string expected)
    {
        if (!connection.Database.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Connection targets '{connection.Database}', expected '{expected}'.");
        }
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < values.Length; index += 2)
        {
            if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must use --name value pairs.");
            }

            result[values[index][2..]] = values[index + 1];
        }

        return result;
    }

    private static string Require(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{name}.");

    private static int ParseIntOption(
        IReadOnlyDictionary<string, string> options,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new ArgumentException($"--{name} 必須是 {minimum} 到 {maximum} 的整數。");
        }

        return parsed;
    }

    private static QmahDbContext CreateDbContext(string connection)
    {
        var options = new DbContextOptionsBuilder<QmahDbContext>()
            .UseSqlServer(connection)
            .Options;
        return new QmahDbContext(options);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("QmahDatabaseRelease commands:");
        Console.WriteLine("  export-sql --connection <connection> --database <name> --output <path>");
        Console.WriteLine("  compare --source <connection> --target <connection> [--report <path>]");
        Console.WriteLine("  validate-ef --connection <connection>");
        Console.WriteLine("  scan-data --connection <connection> [--report <path>]");
        Console.WriteLine("  restore-backup --connection <master connection> --backup <path> --database <name> --data-directory <path>");
        Console.WriteLine("  reset-password --connection <connection> --email <email> [--password <password>] [--credentials <path>] [--backup <path>]");
        Console.WriteLine("  seed-showcase-users --connection <connection> [--credentials <path>] [--backup <path>]");
        Console.WriteLine("  generate-showcase-data --connection <connection> [--post-count <1-512>] [--order-count <1-512>] [--activity-days <0-3650>] [--point-transaction-count <0-10000>] [--key-transaction-count <0-10000>] [--key-progress-transaction-count <0-10000>] [--seed <number>]");
        Console.WriteLine("  generate-showcase-ledger --connection <connection> [--activity-days <0-3650>] [--point-transaction-count <0-10000>] [--key-transaction-count <0-10000>] [--key-progress-transaction-count <0-10000>] [--seed <number>]");
    }

    private sealed record DatabaseSnapshot(
        SortedDictionary<string, string> SchemaHashes,
        SortedDictionary<string, string> DataHashes,
        SortedDictionary<string, long> RowCounts);

    private sealed record ParityReport(
        bool IsEquivalent,
        int SchemaSectionCount,
        int TableCount,
        SortedDictionary<string, long> RowCounts,
        List<string> Differences);

    private static readonly IReadOnlyDictionary<string, string> MetadataQueries =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["schemas"] = """
                SELECT s.name
                FROM sys.schemas s
                WHERE s.name IN (SELECT DISTINCT SCHEMA_NAME(t.schema_id) FROM sys.tables t WHERE t.is_ms_shipped = 0 AND NOT (SCHEMA_NAME(t.schema_id) = N'dbo' AND t.name = N'sysdiagrams'))
                ORDER BY s.name;
                """,
            ["tables"] = """
                SELECT s.name,t.name,t.temporal_type,t.is_memory_optimized
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                WHERE t.is_ms_shipped=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams')
                ORDER BY s.name,t.name;
                """,
            ["columns"] = """
                SELECT s.name,t.name,c.name,TYPE_NAME(c.user_type_id),c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,c.is_computed,c.collation_name,ic.seed_value,ic.increment_value,cc.definition
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id
                LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
                LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
                WHERE t.is_ms_shipped=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams')
                ORDER BY s.name,t.name,c.column_id;
                """,
            ["keys"] = """
                SELECT s.name,t.name,k.name,k.type,i.type_desc,ic.key_ordinal,c.name,ic.is_descending_key
                FROM sys.key_constraints k JOIN sys.tables t ON t.object_id=k.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
                JOIN sys.indexes i ON i.object_id=k.parent_object_id AND i.index_id=k.unique_index_id
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE t.is_ms_shipped=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams')
                ORDER BY s.name,t.name,k.name,ic.key_ordinal;
                """,
            ["foreignKeys"] = """
                SELECT ps.name,pt.name,fk.name,rs.name,rt.name,fkc.constraint_column_id,pc.name,rc.name,fk.delete_referential_action,fk.update_referential_action,fk.is_not_for_replication
                FROM sys.foreign_keys fk JOIN sys.tables pt ON pt.object_id=fk.parent_object_id JOIN sys.schemas ps ON ps.schema_id=pt.schema_id
                JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
                JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
                JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id
                JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id
                WHERE pt.is_ms_shipped=0
                ORDER BY ps.name,pt.name,fk.name,fkc.constraint_column_id;
                """,
            ["indexes"] = """
                SELECT s.name,t.name,i.name,i.type_desc,i.is_unique,i.has_filter,i.filter_definition,ic.key_ordinal,ic.is_included_column,ic.is_descending_key,c.name
                FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE t.is_ms_shipped=0 AND i.index_id>0 AND i.is_primary_key=0 AND i.is_unique_constraint=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams')
                ORDER BY s.name,t.name,i.name,ic.key_ordinal,ic.index_column_id;
                """,
            ["defaults"] = """
                SELECT s.name,t.name,c.name,d.name,d.definition
                FROM sys.default_constraints d JOIN sys.tables t ON t.object_id=d.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
                WHERE t.is_ms_shipped=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams')
                ORDER BY s.name,t.name,c.column_id;
                """,
            ["checks"] = """
                SELECT s.name,t.name,ch.name,ch.definition,ch.is_not_for_replication
                FROM sys.check_constraints ch JOIN sys.tables t ON t.object_id=ch.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
                WHERE t.is_ms_shipped=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams')
                ORDER BY s.name,t.name,ch.name;
                """,
            ["routines"] = """
                SELECT s.name,o.name,o.type,m.definition
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id JOIN sys.sql_modules m ON m.object_id=o.object_id
                WHERE o.is_ms_shipped=0 AND o.type IN ('V','P','FN','IF','TF','TR') AND NOT (s.name=N'dbo' AND o.name LIKE N'%diagram%')
                ORDER BY s.name,o.type,o.name;
                """
        };

    private const string ProjectTableQuery = """
        SELECT s.name,t.name
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
        WHERE t.is_ms_shipped=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams')
        ORDER BY s.name,t.name;
        """;

    private const string TableColumnQuery = """
        SELECT c.name,CONVERT(bit,CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END)
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
        LEFT JOIN (
            SELECT ic.object_id,ic.column_id
            FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            WHERE i.is_primary_key=1
        ) pk ON pk.object_id=c.object_id AND pk.column_id=c.column_id
        WHERE s.name=@schema AND t.name=@table AND ty.name NOT IN (N'timestamp',N'rowversion')
        ORDER BY c.column_id;
        """;

    private const string TextColumnQuery = """
        SELECT s.name,t.name,c.name
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
        WHERE t.is_ms_shipped=0 AND NOT (s.name=N'dbo' AND t.name=N'sysdiagrams') AND ty.name IN (N'nvarchar',N'varchar',N'nchar',N'char',N'ntext',N'text')
        ORDER BY s.name,t.name,c.column_id;
        """;
}
