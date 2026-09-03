using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

const string ApiRoot = "https://odapi.npm.gov.tw/data/open/api/v1/digitalCollection";

// CLI 工具不接受直接雙擊執行；沒有參數時立即結束，避免誤觸發線上抓取。
if (args.Length == 0)
    return 0;

try
{
    var options = PipelineOptions.Parse(args);
    if (options.ShowHelp)
    {
        Console.WriteLine(PipelineOptions.HelpText);
        return 0;
    }
    if (options.VerifyEraRules)
        return EraNormalizer.VerifyRegressionCases();

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
        Console.Error.WriteLine("Cancellation requested...");
    };

    return await ArtifactPipeline.RunAsync(ApiRoot, options, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Pipeline cancelled.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Pipeline failed: {ex.Message}");
    return 1;
}

static partial class ArtifactPipeline
{
    private static HttpClient CreateSourceHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var address = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken))
                    .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                    ?? throw new HttpRequestException($"來源主機 {context.DnsEndPoint.Host} 沒有可用 IPv4 位址。");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static async Task<int> RunAsync(string apiRoot, PipelineOptions options, CancellationToken cancellationToken)
    {
        SourceCatalog.Validate(apiRoot, options.Datasets);
        if (options.EstimateOnly)
            return await EstimateAsync(apiRoot, options, cancellationToken);

        Directory.CreateDirectory(options.OutputDirectory);
        var rawDirectory = Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "raw")).FullName;
        var processedDirectory = Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "processed")).FullName;
        var importDirectory = Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "import")).FullName;

        var generatedAtUtc = DateTime.UtcNow;
        var records = new List<ArtifactImportRow>();
        var qualityRows = new List<ArtifactQualityRow>();
        var datasetStats = new List<DatasetRunStats>();
        var failures = new List<PipelineFailure>();
        var totalRequested = options.OfflineInput is null
            ? options.Datasets.Sum(x => (long)x.RequestedCount)
            : 0L;
        var completed = 0;
        var fatalFailure = false;

        Console.WriteLine(options.OfflineInput is null
            ? $"MODE download; output={options.OutputDirectory}; images={(options.DownloadImages ? "on" : "off")}"
            : $"MODE offline-export; input={options.OfflineInput}; output={options.OutputDirectory}");

        if (options.OfflineInput is not null)
        {
            var offline = await OfflineInput.LoadAsync(options.OfflineInput, cancellationToken);
            // 離線模式是重整既有資料，不是重新套用線上抓取數量；進度總量要以實際輸入資料計算。
            totalRequested = offline.Records.LongCount(record =>
                options.Datasets.Any(dataset => dataset.Matches(record)));
            totalRequested = Math.Max(1L, totalRequested);
            foreach (var dataset in options.Datasets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = offline.Records
                    .Where(record => dataset.Matches(record))
                    .ToList();

                var rawRows = new List<NpmSourceRow>(candidates.Count);
                foreach (var record in candidates)
                {
                    if (TryDeserializeSource(record.SourcePayloadJson, out var source))
                        rawRows.Add(source);
                    else
                        failures.Add(new PipelineFailure(dataset.Code, record.ArtifactRef, "offline", "sourcePayloadJson", "無法解析既有匯入資料中的來源快照。"));
                }

                var rawPath = Path.Combine(rawDirectory, dataset.FileName + ".json");
                var rawJson = JsonSerializer.Serialize(rawRows, JsonDefaults.Pretty);
                await File.WriteAllTextAsync(rawPath, rawJson, JsonDefaults.Utf8NoBom, cancellationToken);

                var selected = new List<ArtifactImportRow>(candidates.Count);
                foreach (var original in candidates)
                {
                    var normalized = NormalizeImportedRecord(original, options.MediaRoot);
                    qualityRows.Add(normalized.Quality);
                    failures.AddRange(normalized.Failures);
                    if (normalized.Record.QuestionEnabled)
                    {
                        records.Add(normalized.Record);
                        selected.Add(normalized.Record);
                    }
                    else
                    {
                        failures.Add(new PipelineFailure(
                            dataset.Code,
                            normalized.Record.ArtifactRef,
                            "selection",
                            "gallery",
                            "資料不符合圖鑑與題庫共用條件，不匯出到正式資料包。"));
                    }
                }

                var rawHash = Sha256Text(rawJson);
                var stats = BuildDatasetStats(dataset, dataset.RequestedCount, candidates.Count, selected.Count,
                    selected, rawPath, rawHash, qualityRows, failures);
                datasetStats.Add(stats);
                completed += selected.Count;
                WriteProgress(dataset, completed, totalRequested, selected.Count);
            }

            // 離線資料可能帶有不屬於目前 API 標準資料集的資料；只留在品質報告，不進入正式匯入。
            var knownCodes = options.Datasets.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownRecords = offline.Records.Where(record =>
                !knownCodes.Contains(record.CategoryCode) &&
                !options.Datasets.Any(dataset => dataset.Matches(record))).ToList();
            foreach (var record in unknownRecords)
            {
                var normalized = NormalizeImportedRecord(record, options.MediaRoot);
                qualityRows.Add(normalized.Quality with { Dataset = record.CategoryCode });
                failures.AddRange(normalized.Failures);
                failures.Add(new PipelineFailure(record.CategoryCode, record.ArtifactRef, "offline", "CategoryCode", "既有資料不屬於目前 API 標準文物資料集，已排除，不會進入圖鑑或題庫。"));
            }
        }
        else
        {
            // 開放資料端點無回應時要盡快明確失敗，避免 GUI 看起來像卡住；
            // 大量資料不靠拉長單次連線，而是由使用者在確認來源可用後分批執行。
            using var http = CreateSourceHttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Qingming-NpmArtifactPipeline", "2.0"));

            foreach (var dataset in options.Datasets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawPath = Path.Combine(rawDirectory, dataset.FileName + ".json");
                try
                {
                    Console.WriteLine($"FETCH|{dataset.Code}|{dataset.ApiName}");
                    var rawJson = await http.GetStringAsync($"{apiRoot}/{dataset.ApiName}.json", cancellationToken);
                    await File.WriteAllTextAsync(rawPath, rawJson, JsonDefaults.Utf8NoBom, cancellationToken);

                    var sourceRows = JsonSerializer.Deserialize<List<NpmSourceRow>>(rawJson, JsonDefaults.Source)
                        ?? throw new InvalidDataException("來源 API 不是文物陣列格式。 ");
                    var eligibleRows = sourceRows
                        .Where(IsQuestionReadySource)
                        .ToList();
                    var candidateBuffer = Math.Max(20, dataset.RequestedCount / 4);
                    var candidateCount = dataset.RequestedCount == 0
                        ? 0
                        : (int)Math.Min(
                            (long)eligibleRows.Count,
                            (long)dataset.RequestedCount + candidateBuffer);
                    var orderedRows = OrderEligibleRows(eligibleRows, options.SelectionMode, options.Seed, dataset.Code);
                    var candidates = options.SelectionMode == "sequential"
                        ? orderedRows.Take(candidateCount).ToList()
                        : SelectEraDiverse(orderedRows, candidateCount);
                    Console.WriteLine($"SELECT|{dataset.Code}|mode={options.SelectionMode}|seed={options.Seed}|source={sourceRows.Count}|question-ready={eligibleRows.Count}|candidates={candidates.Count}|requested={dataset.RequestedCount}");

                    var processed = await ProcessOnlineRowsAsync(
                        dataset, candidates, dataset.RequestedCount, options, http, qualityRows, failures, cancellationToken);
                    records.AddRange(processed.Records);

                    var stats = BuildDatasetStats(dataset, dataset.RequestedCount, sourceRows.Count, processed.Records.Count,
                        processed.Records, rawPath, Sha256Text(rawJson), qualityRows, failures);
                    datasetStats.Add(stats);
                    completed += processed.Records.Count;
                    WriteProgress(dataset, completed, totalRequested, processed.Records.Count);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    fatalFailure = true;
                    var message = ex.Message;
                    failures.Add(new PipelineFailure(dataset.Code, "", "fetch", "dataset", message));
                    Console.Error.WriteLine($"Dataset {dataset.Code} failed: {message}");
                    var fallbackRaw = "[]";
                    await File.WriteAllTextAsync(rawPath, fallbackRaw, JsonDefaults.Utf8NoBom, cancellationToken);
                    datasetStats.Add(BuildDatasetStats(dataset, dataset.RequestedCount, 0, 0,
                        [], rawPath, Sha256Text(fallbackRaw), qualityRows, failures));
                    WriteProgress(dataset, completed, totalRequested, 0);
                }
            }
        }

        var standardCategoryCodes = options.Datasets
            .Select(dataset => dataset.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderedRecords = records
            .Where(record => record.QuestionEnabled && standardCategoryCodes.Contains(record.CategoryCode))
            .OrderBy(record => record.CategoryCode, StringComparer.Ordinal)
            .ThenBy(record => record.ArtifactRef, StringComparer.Ordinal)
            .ToList();
        var orderedQualityRows = qualityRows
            .OrderBy(row => row.Dataset, StringComparer.Ordinal)
            .ThenBy(row => row.ArtifactRef, StringComparer.Ordinal)
            .ToList();

        var processedJsonPath = Path.Combine(processedDirectory, "artifacts.json");
        var processedCsvPath = Path.Combine(processedDirectory, "artifacts.csv");
        var importJsonPath = Path.Combine(importDirectory, "artifacts.json");
        var importCsvPath = Path.Combine(importDirectory, "artifacts.csv");
        var importSqlPath = Path.Combine(importDirectory, "artifacts.upsert.sql");
        var legacyJsonPath = Path.Combine(options.OutputDirectory, "artifacts.import.json");

        var artifactJson = JsonSerializer.Serialize(orderedRecords, JsonDefaults.Pretty);
        await File.WriteAllTextAsync(processedJsonPath, artifactJson, JsonDefaults.Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(importJsonPath, artifactJson, JsonDefaults.Utf8NoBom, cancellationToken);
        await File.WriteAllTextAsync(legacyJsonPath, artifactJson, JsonDefaults.Utf8NoBom, cancellationToken);
        await WriteArtifactCsvAsync(processedCsvPath, orderedRecords, cancellationToken);
        await WriteArtifactCsvAsync(importCsvPath, orderedRecords, cancellationToken);
        // sqlcmd ODBC 會依 BOM 判斷 Unicode 輸入；SQL 另輸出 UTF-8 BOM，避免繁中註解或字串被系統 code page 誤讀。
        await File.WriteAllTextAsync(importSqlPath, BuildUpsertSql(orderedRecords), JsonDefaults.Utf8Bom, cancellationToken);

        var readableFiles = await WriteReadableExportsAsync(options, orderedRecords, cancellationToken);
        var exportPaths = Directory.EnumerateFiles(rawDirectory, "*.json", SearchOption.TopDirectoryOnly).ToList();
        exportPaths.AddRange([processedJsonPath, processedCsvPath, importJsonPath, importCsvPath, importSqlPath, legacyJsonPath]);
        exportPaths.AddRange(readableFiles);

        var exportFiles = await HashFilesAsync(exportPaths, options.OutputDirectory, cancellationToken);
        var mediaFiles = await HashMediaAsync(orderedRecords, options.MediaRoot, cancellationToken);
        var totalCounts = BuildTotals(options, orderedRecords, orderedQualityRows, datasetStats, failures);
        var parameterSnapshot = options.ToManifestParameters();

        var qualityReportPath = Path.Combine(options.OutputDirectory, "quality-report.json");
        var qualityReport = new
        {
            schemaVersion = 2,
            generatedAtUtc,
            mode = options.OfflineInput is null ? "download" : "offline-export",
            parameters = parameterSnapshot,
            totals = totalCounts,
            datasets = datasetStats,
            records = orderedQualityRows,
            failures,
            outputFileHashes = exportFiles,
            mediaFileHashes = mediaFiles
        };
        await File.WriteAllTextAsync(qualityReportPath,
            JsonSerializer.Serialize(qualityReport, JsonDefaults.Pretty), JsonDefaults.Utf8NoBom, cancellationToken);

        var manifestFiles = await HashFilesAsync(
            [.. exportFiles.Select(file => Path.Combine(options.OutputDirectory, file.Path)), qualityReportPath],
            options.OutputDirectory, cancellationToken);
        var manifestPath = Path.Combine(options.OutputDirectory, "manifest.json");
        var manifest = new
        {
            schemaVersion = 2,
            generatedAtUtc,
            mode = options.OfflineInput is null ? "download" : "offline-export",
            source = apiRoot,
            parameters = parameterSnapshot,
            totals = totalCounts,
            datasets = datasetStats,
            files = manifestFiles,
            mediaFiles = mediaFiles,
            license = "CC BY 4.0 for text and medium-resolution images; see https://digitalarchive.npm.gov.tw/opendata",
            nonAffiliation = "本資料包為學生專題產出，非國立故宮博物院官方製作。",
            note = "files 不包含 manifest.json 自身的 hash；quality-report.json 會列出輸出檔案 hash。"
        };
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest, JsonDefaults.Pretty), JsonDefaults.Utf8NoBom, cancellationToken);

        Console.WriteLine($"Completed: {orderedRecords.Count} records, {orderedRecords.Count(x => x.QuestionEnabled)} question-ready.");
        if (failures.Count > 0)
            Console.Error.WriteLine($"Completed with {failures.Count} recorded failure(s); see quality-report.json.");
        return fatalFailure ? 1 : 0;
    }

    private static async Task<DatasetProcessingResult> ProcessOnlineRowsAsync(
        Dataset dataset,
        IReadOnlyList<NpmSourceRow> candidates,
        int targetCount,
        PipelineOptions options,
        HttpClient http,
        List<ArtifactQualityRow> qualityRows,
        List<PipelineFailure> failures,
        CancellationToken cancellationToken)
    {
        var records = new List<ArtifactImportRow>(candidates.Count);
        foreach (var source in candidates)
        {
            if (records.Count >= targetCount) break;
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = EraNormalizer.Normalize(source.Era, source.Identifier);
            var sourcePayloadJson = SerializeSource(source);
            var media = options.DownloadImages
                ? await MediaDownloader.DownloadAsync(http, source, dataset.Code, options.MediaRoot, cancellationToken)
                : MediaPaths.Disabled;
            var missingFields = MissingFields(source, media);
            var record = BuildArtifactRecord(dataset, source, normalized, media, sourcePayloadJson);
            var rowFailures = media.Failures
                .Select(failure => new QualityFailure("media", failure.Field, failure.Message))
                .ToList();
            var quality = new ArtifactQualityRow(
                record.ArtifactRef,
                dataset.Code,
                normalized.RequiresReview,
                record.QuestionEnabled,
                missingFields,
                rowFailures,
                normalized.Confidence,
                normalized.RuleId,
                normalized.Evidence,
                normalized.ReviewReason,
                Sha256Text(sourcePayloadJson));

            qualityRows.Add(quality);
            failures.AddRange(rowFailures.Select(failure =>
                new PipelineFailure(dataset.Code, record.ArtifactRef, failure.Stage, failure.Field, failure.Message)));
            if (record.QuestionEnabled)
            {
                records.Add(record);
            }
            else
            {
                failures.Add(new PipelineFailure(
                    dataset.Code,
                    record.ArtifactRef,
                    "selection",
                    "gallery",
                    "圖片下載失敗或必要欄位缺漏，不匯出到圖鑑與題庫共用資料包。"));
            }
        }

        return new DatasetProcessingResult(records);
    }

    private static ArtifactImportRow BuildArtifactRecord(
        Dataset dataset,
        NpmSourceRow source,
        EraResult normalized,
        MediaPaths media,
        string sourcePayloadJson)
    {
        var name = source.Name.Trim();
        var description = source.Desc?.Trim() ?? "";
        var era = source.Era?.Trim() ?? "";
        var size = source.Size?.Trim() ?? "";
        var sourceUrl = source.Url?.Trim() ?? "";
        var questionEnabled = !normalized.RequiresReview
            && !string.IsNullOrWhiteSpace(description)
            && !string.IsNullOrWhiteSpace(era)
            && !string.IsNullOrWhiteSpace(media.DisplayPath);

        return new ArtifactImportRow(
            StableGuid(source.Identifier),
            source.Identifier.Trim(),
            name,
            dataset.Code,
            string.IsNullOrWhiteSpace(source.Category) ? dataset.DisplayName : source.Category.Trim(),
            era,
            normalized.Bucket,
            description,
            sourceUrl,
            media.DisplayPath ?? "",
            sourcePayloadJson,
            normalized.RequiresReview ? "REVIEW_REQUIRED" : "AUTO_VERIFIED",
            questionEnabled,
            $"{name} 國立故宮博物院，臺北，CC BY 4.0 @ www.npm.gov.tw",
            normalized.EndYear,
            normalized.StartYear,
            "CC-BY-4.0",
            size,
            dataset.ApiName,
            media.ThumbnailPath ?? "");
    }

    private static (ArtifactImportRow Record, ArtifactQualityRow Quality, IReadOnlyList<PipelineFailure> Failures)
        NormalizeImportedRecord(ArtifactImportRow original, string mediaRoot)
    {
        var sourceAvailable = TryDeserializeSource(original.SourcePayloadJson, out var source);
        var sourcePayloadJson = sourceAvailable ? SerializeSource(source) : original.SourcePayloadJson ?? "";
        var imagePath = NormalizeMediaPath(original.ImageUrl, mediaRoot);
        var thumbnailPath = NormalizeMediaPath(original.ThumbnailUrl, mediaRoot);
        var eraTextOriginal = sourceAvailable && !string.IsNullOrWhiteSpace(source.Era)
            ? source.Era.Trim()
            : original.EraTextOriginal;
        var normalized = EraNormalizer.Normalize(eraTextOriginal, original.ArtifactRef);
        var record = original with
        {
            Id = original.Id == Guid.Empty ? StableGuid(original.ArtifactRef) : original.Id,
            EraTextOriginal = eraTextOriginal,
            EraBucketCode = normalized.Bucket,
            EraStartYear = normalized.StartYear,
            EraEndYear = normalized.EndYear,
            ImageUrl = imagePath,
            ThumbnailUrl = thumbnailPath,
            SourcePayloadJson = sourcePayloadJson,
            NormalizationStatus = normalized.RequiresReview ? "REVIEW_REQUIRED" : "AUTO_VERIFIED",
            QuestionEnabled = original.QuestionEnabled && !string.IsNullOrWhiteSpace(imagePath)
        };
        var missingFields = MissingFields(record);
        var rowFailures = new List<QualityFailure>();
        if (!sourceAvailable)
            rowFailures.Add(new QualityFailure("offline", "SourcePayloadJson", "無法解析既有匯入資料中的來源快照。"));
        var quality = new ArtifactQualityRow(
            record.ArtifactRef,
            record.CategoryCode,
            normalized.RequiresReview,
            record.QuestionEnabled,
            missingFields,
            rowFailures,
            normalized.Confidence,
            normalized.RuleId,
            normalized.Evidence,
            normalized.ReviewReason,
            Sha256Text(sourcePayloadJson));
        var failures = rowFailures.Select(failure =>
            new PipelineFailure(record.CategoryCode, record.ArtifactRef, failure.Stage, failure.Field, failure.Message)).ToList();
        return (record, quality, failures);
    }

    private static DatasetRunStats BuildDatasetStats(
        Dataset dataset,
        int requestedCount,
        int sourceRowCount,
        int selectedCount,
        IReadOnlyList<ArtifactImportRow> records,
        string rawPath,
        string rawHash,
        IReadOnlyList<ArtifactQualityRow> qualityRows,
        IReadOnlyList<PipelineFailure> failures)
    {
        var rows = qualityRows.Where(row => row.Dataset.Equals(dataset.Code, StringComparison.OrdinalIgnoreCase)).ToList();
        var datasetFailures = failures.Count(failure => failure.Dataset.Equals(dataset.Code, StringComparison.OrdinalIgnoreCase));
        return new DatasetRunStats(
            dataset.Code,
            dataset.DisplayName,
            dataset.ApiName,
            requestedCount,
            sourceRowCount,
            selectedCount,
            records.Count,
            records.Count(record => record.QuestionEnabled),
            rows.Count(row => row.RequiresEraReview),
            rows.Count(row => row.MissingFields.Count > 0),
            rows.Count(row => row.Failures.Count > 0),
            datasetFailures,
            ToOutputRelativePath(rawPath, Path.GetDirectoryName(Path.GetDirectoryName(rawPath))!),
            rawHash);
    }

    private static object BuildTotals(
        PipelineOptions options,
        IReadOnlyList<ArtifactImportRow> records,
        IReadOnlyList<ArtifactQualityRow> qualityRows,
        IReadOnlyList<DatasetRunStats> datasetStats,
        IReadOnlyList<PipelineFailure> failures)
    {
        return new
        {
            requestedCount = options.Datasets.Sum(dataset => (long)dataset.RequestedCount),
            sourceRowCount = datasetStats.Sum(dataset => dataset.SourceRowCount),
            selectedCount = datasetStats.Sum(dataset => dataset.SelectedCount),
            outputRecordCount = records.Count,
            questionEnabledCount = records.Count(record => record.QuestionEnabled),
            reviewRequiredCount = qualityRows.Count(row => row.RequiresEraReview),
            recordsWithMissingFields = qualityRows.Count(row => row.MissingFields.Count > 0),
            recordsWithFailures = qualityRows.Count(row => row.Failures.Count > 0),
            failureCount = failures.Count
        };
    }

    private static void WriteProgress(Dataset dataset, int completed, long total, int selected)
    {
        var percent = total <= 0 ? 100 : Math.Clamp((int)Math.Round(completed * 100d / total), 0, 100);
        Console.WriteLine($"PROGRESS|{dataset.Code}|{percent}|selected={selected}|completed={completed}|total={total}");
    }

    private static int CompletenessScore(NpmSourceRow row) =>
        (!string.IsNullOrWhiteSpace(row.ImageUrlM) ? 4 : 0) +
        (!string.IsNullOrWhiteSpace(row.Desc) ? 3 : 0) +
        (!string.IsNullOrWhiteSpace(row.Era) ? 2 : 0) +
        (!string.IsNullOrWhiteSpace(row.Size) ? 1 : 0);

    private static bool IsQuestionReadySource(NpmSourceRow row) =>
        IsGalleryReadySource(row)
        && !string.IsNullOrWhiteSpace(row.Desc)
        && !string.IsNullOrWhiteSpace(row.Era)
        && !EraNormalizer.Normalize(row.Era, row.Identifier).RequiresReview;

    private static bool IsGalleryReadySource(NpmSourceRow row) =>
        !string.IsNullOrWhiteSpace(row.Identifier)
        && !string.IsNullOrWhiteSpace(row.Name)
        && !string.IsNullOrWhiteSpace(row.Url)
        && !string.IsNullOrWhiteSpace(row.ImageUrlM);

    private static List<NpmSourceRow> SelectEraDiverse(IReadOnlyList<NpmSourceRow> ranked, int count)
    {
        var selected = new List<NpmSourceRow>(count);
        var selectedRefs = new HashSet<string>(StringComparer.Ordinal);
        var queues = ranked
            .Select(row => new { Row = row, Era = EraNormalizer.Normalize(row.Era, row.Identifier) })
            .Where(x => !x.Era.RequiresReview)
            .GroupBy(x => x.Era.Bucket)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Queue<NpmSourceRow>(group.Select(x => x.Row)))
            .ToList();

        var diverseTarget = Math.Min(count, (int)Math.Ceiling(count * 0.8));
        while (selected.Count < diverseTarget && queues.Any(queue => queue.Count > 0))
        {
            foreach (var queue in queues)
            {
                if (queue.Count == 0) continue;
                var row = queue.Dequeue();
                if (selectedRefs.Add(row.Identifier)) selected.Add(row);
                if (selected.Count >= diverseTarget) break;
            }
        }

        foreach (var row in ranked)
        {
            if (selected.Count >= count) break;
            if (selectedRefs.Add(row.Identifier)) selected.Add(row);
        }
        return selected;
    }

    private static IReadOnlyList<NpmSourceRow> OrderEligibleRows(
        IReadOnlyList<NpmSourceRow> rows,
        string selectionMode,
        int seed,
        string datasetCode) => selectionMode switch
        {
            "sequential" => rows
                .OrderBy(row => SequentialIdentifierKey(row.Identifier).Prefix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => SequentialIdentifierKey(row.Identifier).Number)
                .ThenBy(row => SequentialIdentifierKey(row.Identifier).Original, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            "random" => rows
                .OrderBy(row => StableNumber($"selection:{seed}:{datasetCode}:{row.Identifier}"))
                .ThenBy(row => row.Identifier, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => rows
                .OrderByDescending(CompletenessScore)
                .ThenBy(row => row.Identifier, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

    private static (string Prefix, long Number, string Original) SequentialIdentifierKey(string identifier)
    {
        var match = Regex.Match(identifier.Trim(), @"^(.*?)(\d+)$");
        if (!match.Success)
            return (identifier.Trim(), long.MaxValue, identifier.Trim());

        var number = long.TryParse(match.Groups[2].Value, out var parsed)
            ? parsed
            : long.MaxValue;
        return (match.Groups[1].Value, number, identifier.Trim());
    }

    private static IReadOnlyList<string> MissingFields(NpmSourceRow source, MediaPaths media)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(source.Identifier)) missing.Add("ArtifactRef");
        if (string.IsNullOrWhiteSpace(source.Name)) missing.Add("Name");
        if (string.IsNullOrWhiteSpace(source.Era)) missing.Add("EraTextOriginal");
        if (string.IsNullOrWhiteSpace(source.Desc)) missing.Add("DescriptionOriginal");
        if (string.IsNullOrWhiteSpace(source.Url)) missing.Add("SourceUrl");
        if (string.IsNullOrWhiteSpace(media.DisplayPath)) missing.Add("ImageUrl");
        if (string.IsNullOrWhiteSpace(media.ThumbnailPath)) missing.Add("ThumbnailUrl");
        return missing;
    }

    private static IReadOnlyList<string> MissingFields(ArtifactImportRow record)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(record.ArtifactRef)) missing.Add("ArtifactRef");
        if (string.IsNullOrWhiteSpace(record.Name)) missing.Add("Name");
        if (string.IsNullOrWhiteSpace(record.EraTextOriginal)) missing.Add("EraTextOriginal");
        if (string.IsNullOrWhiteSpace(record.DescriptionOriginal)) missing.Add("DescriptionOriginal");
        if (string.IsNullOrWhiteSpace(record.SourceUrl)) missing.Add("SourceUrl");
        if (string.IsNullOrWhiteSpace(record.ImageUrl)) missing.Add("ImageUrl");
        if (string.IsNullOrWhiteSpace(record.ThumbnailUrl)) missing.Add("ThumbnailUrl");
        return missing;
    }

    private static string SerializeSource(NpmSourceRow source) =>
        JsonSerializer.Serialize(source, JsonDefaults.PrettyCompact);

    private static bool TryDeserializeSource(string? value, out NpmSourceRow source)
    {
        source = new NpmSourceRow();
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            source = JsonSerializer.Deserialize<NpmSourceRow>(value, JsonDefaults.Source) ?? new NpmSourceRow();
            return !string.IsNullOrWhiteSpace(source.Identifier);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"npm-artifact:{value.Trim()}"));
        return new Guid(bytes[..16]);
    }

    private static ulong StableNumber(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        ulong result = 0;
        for (var index = 0; index < 8; index++)
            result = (result << 8) | bytes[index];
        return result;
    }

    private static string NormalizeMediaPath(string? value, string mediaRoot)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var original = value.Trim();
        if (original.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
            return original["/media/".Length..].Replace('\\', '/');
        if (original.StartsWith("media/", StringComparison.OrdinalIgnoreCase))
            return original["media/".Length..].Replace('\\', '/');

        var normalized = original.Replace('\\', '/');
        while (normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = normalized[1..];
        if (Path.IsPathRooted(original))
        {
            try
            {
                var fullMediaRoot = Path.GetFullPath(mediaRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullValue = Path.GetFullPath(value);
                var relative = Path.GetRelativePath(fullMediaRoot, fullValue).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal)) return "";
                normalized = relative;
            }
            catch (ArgumentException)
            {
                return "";
            }
        }
        if (normalized.Contains(":", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal))
            return "";
        return normalized;
    }

    private static string ToOutputRelativePath(string path, string outputDirectory)
    {
        return Path.GetRelativePath(outputDirectory, path).Replace('\\', '/');
    }

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<IReadOnlyList<FileHash>> HashFilesAsync(
        IEnumerable<string> paths,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var hashes = new List<FileHash>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            hashes.Add(new FileHash(
                ToOutputRelativePath(path, outputDirectory),
                new FileInfo(path).Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }
        return hashes.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
    }

    private static async Task<IReadOnlyList<MediaFileHash>> HashMediaAsync(
        IReadOnlyList<ArtifactImportRow> records,
        string mediaRoot,
        CancellationToken cancellationToken)
    {
        var paths = records
            .SelectMany(record => new[] { record.ImageUrl, record.ThumbnailUrl })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var hashes = new List<MediaFileHash>(paths.Count);
        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physicalPath = Path.Combine(mediaRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(physicalPath))
            {
                hashes.Add(new MediaFileHash(relativePath, null, null, true));
                continue;
            }
            await using var stream = File.OpenRead(physicalPath);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            hashes.Add(new MediaFileHash(
                relativePath,
                new FileInfo(physicalPath).Length,
                Convert.ToHexString(hash).ToLowerInvariant(),
                false));
        }
        return hashes;
    }

    private static async Task WriteArtifactCsvAsync(
        string path,
        IReadOnlyList<ArtifactImportRow> records,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', ArtifactSchema.Columns.Select(CsvValue)));
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new object?[]
            {
                record.Id,
                record.ArtifactRef,
                record.Name,
                record.CategoryCode,
                record.CategoryName,
                record.EraTextOriginal,
                record.EraBucketCode,
                record.DescriptionOriginal,
                record.SourceUrl,
                record.ImageUrl,
                record.SourcePayloadJson,
                record.NormalizationStatus,
                record.QuestionEnabled ? 1 : 0,
                record.AttributionText,
                record.EraEndYear,
                record.EraStartYear,
                record.LicenseCode,
                record.SizeOriginal,
                record.SourceDataset,
                record.ThumbnailUrl
            };
            builder.AppendLine(string.Join(',', values.Select(value => CsvValue(value?.ToString() ?? ""))));
        }
        await File.WriteAllTextAsync(path, builder.ToString(), JsonDefaults.Utf8Bom, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> WriteReadableExportsAsync(
        PipelineOptions options,
        IReadOnlyList<ArtifactImportRow> records,
        CancellationToken cancellationToken)
    {
        if (options.ReadableFormat == "none") return [];
        var previewDirectory = Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "preview")).FullName;
        var paths = new List<string>();
        if (options.ReadableFormat is "csv" or "both")
        {
            var path = Path.Combine(previewDirectory, "文物資料預覽.csv");
            var builder = new StringBuilder();
builder.AppendLine(string.Join(',', new[] { "故宮編號", "名稱", "類型", "原始年代", "分類年代", "說明", "展示圖路徑", "縮圖路徑", "來源網址", "圖鑑與題庫" }.Select(CsvValue)));
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new[]
                {
                    record.ArtifactRef, record.Name, record.CategoryName, record.EraTextOriginal,
                    EraDisplayName(record.EraBucketCode), record.DescriptionOriginal, record.ImageUrl,
                    record.ThumbnailUrl, record.SourceUrl, "是"
                };
                builder.AppendLine(string.Join(',', values.Select(CsvValue)));
            }
            await File.WriteAllTextAsync(path, builder.ToString(), JsonDefaults.Utf8Bom, cancellationToken);
            paths.Add(path);
        }
        if (options.ReadableFormat is "html" or "both")
        {
            var path = Path.Combine(previewDirectory, "文物資料預覽.html");
var html = new StringBuilder("<!doctype html><html lang=\"zh-Hant\"><meta charset=\"utf-8\"><title>文物資料預覽</title><style>body{font-family:system-ui,Microsoft JhengHei,sans-serif;background:#f6f3ee;color:#2c2926;margin:32px}h1{font-size:28px}table{border-collapse:collapse;background:white;width:100%}th,td{border:1px solid #d8d0c8;padding:9px;text-align:left;vertical-align:top}th{background:#eee6dc;white-space:nowrap}td:nth-child(6){min-width:280px;line-height:1.6}a{color:#7a3f32}</style><h1>文物資料預覽</h1><p>這是方便人工檢查的版本，不是網站正式資料庫的替代品</p><table><thead><tr><th>故宮編號</th><th>名稱</th><th>類型</th><th>原始年代</th><th>分類年代</th><th>說明</th><th>展示圖路徑</th><th>來源</th></tr></thead><tbody>");
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                html.Append("<tr>");
                foreach (var value in new[] { record.ArtifactRef, record.Name, record.CategoryName, record.EraTextOriginal, EraDisplayName(record.EraBucketCode), record.DescriptionOriginal, record.ImageUrl, record.SourceUrl })
                    html.Append("<td>").Append(WebUtility.HtmlEncode(value)).Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table></html>");
            await File.WriteAllTextAsync(path, html.ToString(), JsonDefaults.Utf8NoBom, cancellationToken);
            paths.Add(path);
        }
        return paths;
    }

    private static string BuildUpsertSql(IReadOnlyList<ArtifactImportRow> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- NpmArtifactPipeline｜可重複執行的 SQL Server 匯入檔");
        builder.AppendLine("-- 對應正式版 catalog Schema：ArtifactCategories、EraBuckets、Artifacts");
        builder.AppendLine("-- 品質報告與來源快照留在 output，不寫入網站正式資料表。");
        builder.AppendLine("SET ANSI_NULLS ON;");
        builder.AppendLine("SET QUOTED_IDENTIFIER ON;");
        builder.AppendLine("SET ANSI_PADDING ON;");
        builder.AppendLine("SET ANSI_WARNINGS ON;");
        builder.AppendLine("SET ARITHABORT ON;");
        builder.AppendLine("SET CONCAT_NULL_YIELDS_NULL ON;");
        builder.AppendLine("SET NUMERIC_ROUNDABORT OFF;");
        builder.AppendLine("SET NOCOUNT ON;");
        builder.AppendLine("SET XACT_ABORT ON;");
        builder.AppendLine("BEGIN TRANSACTION;");
        if (records.Count == 0)
        {
            builder.AppendLine("-- 沒有符合圖鑑與題庫共同條件的資料，未執行任何匯入。");
        }
        else
        {
            var categories = records
                .GroupBy(record => record.CategoryCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { Code = group.Key, Name = group.First().CategoryName })
                .OrderBy(category => category.Code, StringComparer.Ordinal)
                .ToList();
            foreach (var category in categories)
            {
                builder.AppendLine("MERGE [catalog].[ArtifactCategories] WITH (HOLDLOCK) AS target");
                builder.AppendLine($"USING (VALUES ({SqlGuid(StableGuid("artifact-category:" + category.Code))}, {SqlText(category.Code)}, {SqlText(category.Name)})) AS source (Id, Code, Name)");
                builder.AppendLine("ON target.Code = source.Code");
                builder.AppendLine("WHEN MATCHED THEN UPDATE SET Name = source.Name");
                builder.AppendLine("WHEN NOT MATCHED THEN INSERT (Id, Code, Name) VALUES (source.Id, source.Code, source.Name);");
            }
            builder.AppendLine();

            var eras = records
                .GroupBy(record => record.EraBucketCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Code = group.Key,
                    Name = EraDisplayName(group.Key),
                    Start = group.Where(record => record.EraStartYear.HasValue).Select(record => record.EraStartYear!.Value).Cast<int?>().Min(),
                    End = group.Where(record => record.EraEndYear.HasValue).Select(record => record.EraEndYear!.Value).Cast<int?>().Max()
                })
                .OrderBy(era => era.Start ?? int.MaxValue)
                .ThenBy(era => era.Code, StringComparer.Ordinal)
                .ToList();
            foreach (var era in eras)
            {
                builder.AppendLine("MERGE [catalog].[EraBuckets] WITH (HOLDLOCK) AS target");
                builder.AppendLine($"USING (VALUES ({SqlGuid(StableGuid("era-bucket:" + era.Code))}, {SqlText(era.Code)}, {SqlText(era.Name)}, {SqlNullableInt(era.Start)}, {SqlNullableInt(era.End)})) AS source (Id, Code, Name, StartYear, EndYear)");
                builder.AppendLine("ON target.Code = source.Code");
                builder.AppendLine("WHEN MATCHED THEN UPDATE SET Name = source.Name, StartYear = source.StartYear, EndYear = source.EndYear");
                builder.AppendLine("WHEN NOT MATCHED THEN INSERT (Id, Code, Name, StartYear, EndYear) VALUES (source.Id, source.Code, source.Name, source.StartYear, source.EndYear);");
            }
            builder.AppendLine();

            foreach (var record in records)
            {
                builder.AppendLine("MERGE [catalog].[Artifacts] WITH (HOLDLOCK) AS target");
                var categoryId = StableGuid("artifact-category:" + record.CategoryCode);
                var eraId = StableGuid("era-bucket:" + record.EraBucketCode);
                builder.AppendLine($"USING (VALUES ({SqlGuid(record.Id)}, {SqlText(record.ArtifactRef)}, {SqlText(record.Name)}, {SqlGuid(categoryId)}, {SqlGuid(eraId)}, {SqlText(record.EraTextOriginal)}, NULL, {SqlText(record.DescriptionOriginal)}, {SqlText(record.ImageUrl)}, {SqlText(record.ThumbnailUrl)}, {SqlText(record.SourceUrl)}, {SqlText(record.LicenseCode)}, {SqlText(record.AttributionText)})) AS source (Id, ArtifactRef, Name, CategoryId, EraBucketId, EraTextOriginal, CreatorDisplay, Description, PrimaryImagePath, ThumbnailPath, SourceUrl, LicenseCode, AttributionText)");
                builder.AppendLine("ON target.ArtifactRef = source.ArtifactRef");
                builder.AppendLine("WHEN MATCHED THEN UPDATE SET Name = source.Name, CategoryId = source.CategoryId, EraBucketId = source.EraBucketId, EraTextOriginal = source.EraTextOriginal, CreatorDisplay = source.CreatorDisplay, Description = source.Description, PrimaryImagePath = source.PrimaryImagePath, ThumbnailPath = source.ThumbnailPath, SourceUrl = source.SourceUrl, LicenseCode = source.LicenseCode, AttributionText = source.AttributionText");
                builder.AppendLine("WHEN NOT MATCHED THEN INSERT (Id, ArtifactRef, Name, CategoryId, EraBucketId, EraTextOriginal, CreatorDisplay, Description, PrimaryImagePath, ThumbnailPath, SourceUrl, LicenseCode, AttributionText) VALUES (source.Id, source.ArtifactRef, source.Name, source.CategoryId, source.EraBucketId, source.EraTextOriginal, source.CreatorDisplay, source.Description, source.PrimaryImagePath, source.ThumbnailPath, source.SourceUrl, source.LicenseCode, source.AttributionText);");
                builder.AppendLine();
            }
        }
        builder.AppendLine("COMMIT TRANSACTION;");
        return builder.ToString();
    }

    private static bool IsImportableArtifact(ArtifactImportRow record) =>
        !string.IsNullOrWhiteSpace(record.ArtifactRef)
        && !string.IsNullOrWhiteSpace(record.Name)
        && !string.IsNullOrWhiteSpace(record.CategoryCode)
        && !string.IsNullOrWhiteSpace(record.SourceUrl)
        && !string.IsNullOrWhiteSpace(record.LicenseCode)
        && !string.IsNullOrWhiteSpace(record.ImageUrl);

    private static string BuildQualityIssuesJson(ArtifactImportRow record, bool reviewRequired)
    {
        var issues = new List<object>();
        if (reviewRequired)
            issues.Add(new { code = "ERA_REVIEW_REQUIRED" });
        if (string.IsNullOrWhiteSpace(record.DescriptionOriginal))
            issues.Add(new { code = "QUESTION_DESCRIPTION_MISSING" });
        if (!IsImportableArtifact(record))
            issues.Add(new { code = "ARTIFACT_PUBLICATION_REQUIREMENTS_MISSING" });
        return JsonSerializer.Serialize(issues, JsonDefaults.PrettyCompact);
    }

    private static string EraDisplayName(string code) => code switch
    {
        "QING" => "清", "MING" => "明", "YUAN" => "元", "SONG" => "宋", "LIAO" => "遼", "JIN" => "金",
        "TANG" => "唐", "SUI" => "隋", "HAN" => "漢", "QIN" => "秦", "WARRING_STATES" => "戰國",
        "SPRING_AUTUMN" => "春秋", "ZHOU" => "周", "SHANG" => "商", "NEOLITHIC" => "新石器時代",
        "REPUBLIC" => "民國", "JAPAN" => "日本", "CROSS_DYNASTY" => "跨年代", "UNKNOWN" => "年代不明",
        _ => code
    };

    private static IEnumerable<string> SqlValues(ArtifactImportRow record)
    {
        yield return SqlGuid(record.Id);
        yield return SqlText(record.ArtifactRef);
        yield return SqlText(record.Name);
        yield return SqlText(record.CategoryCode);
        yield return SqlText(record.CategoryName);
        yield return SqlText(record.EraTextOriginal);
        yield return SqlText(record.EraBucketCode);
        yield return SqlText(record.DescriptionOriginal);
        yield return SqlText(record.SourceUrl);
        yield return SqlText(record.ImageUrl);
        yield return SqlText(record.SourcePayloadJson);
        yield return SqlText(record.NormalizationStatus);
        yield return record.QuestionEnabled ? "CAST(1 AS bit)" : "CAST(0 AS bit)";
        yield return SqlText(record.AttributionText);
        yield return SqlNullableInt(record.EraEndYear);
        yield return SqlNullableInt(record.EraStartYear);
        yield return SqlText(record.LicenseCode);
        yield return SqlText(record.SizeOriginal);
        yield return SqlText(record.SourceDataset);
        yield return SqlText(record.ThumbnailUrl);
    }

    private static string SqlGuid(Guid value) => $"'{value:D}'";

    private static string SqlNullableInt(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "NULL";

    private static string SqlText(string value) =>
        "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string CsvValue(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record DatasetProcessingResult(IReadOnlyList<ArtifactImportRow> Records);
}

static class SourceCatalog
{
    public static void Validate(string apiRoot, IReadOnlyList<Dataset> datasets)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "source-catalog.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 source-catalog.json，停止下載以避免使用未受控的來源設定。", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var configuredRoot = root.GetProperty("apiRoot").GetString();
        if (!string.Equals(configuredRoot?.TrimEnd('/'), apiRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source-catalog.json 的 apiRoot 與工具設定不一致，停止下載。");

        var configured = root.GetProperty("datasets")
            .EnumerateArray()
            .Select(item => $"{item.GetProperty("code").GetString()}|{item.GetProperty("apiName").GetString()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = datasets.Select(dataset => $"{dataset.Code}|{dataset.ApiName}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.IsSubsetOf(configured))
            throw new InvalidDataException("source-catalog.json 缺少程式要求的文物類別，請先完成來源審核再下載。");
    }
}

sealed record Dataset(string Code, string DisplayName, string ApiName, string FileName, int RequestedCount)
{
    public bool Matches(ArtifactImportRow record) =>
        Code.Equals(record.CategoryCode, StringComparison.OrdinalIgnoreCase) ||
        ApiName.Equals(record.SourceDataset, StringComparison.OrdinalIgnoreCase) ||
        FileName.Equals(record.SourceDataset, StringComparison.OrdinalIgnoreCase);
}

sealed record PipelineOptions(
    string OutputDirectory,
    string MediaRoot,
    int PerDataset,
    IReadOnlyDictionary<string, int> Counts,
    int Seed,
    string SelectionMode,
    bool DownloadImages,
    string? OfflineInput,
    bool ShowHelp,
    string ReadableFormat,
    bool EstimateOnly,
    bool VerifyEraRules,
    bool IncludeArchiveCategories)
{
    private static readonly DatasetDefinition[] Definitions =
    [
        // 端點依故宮數位典藏 OpenAPI 與政府資料開放平臺的正式類別建立。
        // 不從展示頁 HTML 推測類別；端點失效會由 --estimate-only 如實回報。
        new("BRONZE", "銅器", "bronzes", "bronze", true),
        new("CERAMIC", "陶瓷", "ceramics", "ceramic", true),
        new("JADE", "玉器", "jades", "jade", true),
        new("ENAMEL", "琺瑯器", "enamelWares", "enamel", true),
        new("LACQUER", "漆器", "lacquerWares", "lacquer", true),
        new("COIN", "錢幣", "coins", "coins", true),
        new("CARVING", "雕刻", "carvings", "carvings", true),
        new("PAINTING", "繪畫", "paintings", "painting", true),
        new("STUDIO_IMPLEMENT", "文具", "studioImplements", "studio-implements", false),
        new("MISCELLANEOUS", "雜項", "miscellaneousObjects", "miscellaneous", false),
        new("TEXTILE", "織品", "textiles", "textiles", false),
        new("EMBROIDERY", "絲繡", "tapestriesAndEmbroideries", "embroideries", false),
        new("CALLIGRAPHY", "法書", "calligraphicWorks", "calligraphy", false),
        new("CALLIGRAPHIC_MODEL", "法帖", "calligraphicModelBooks", "calligraphic-models", false),
        new("RUBBING", "拓片", "rubbings", "rubbings", false),
        new("FAN", "成扇", "fans", "fans", false)
    ];

    public IReadOnlyList<Dataset> Datasets => Definitions
        .Where(definition => IncludeArchiveCategories || definition.IncludedInMidterm)
        .Select(definition => new Dataset(
            definition.Code,
            definition.DisplayName,
            definition.ApiName,
            definition.FileName,
            Counts[definition.FileName]))
        .ToList();

    public static string HelpText => """
        NpmArtifactPipeline (.NET 10)

        線上下載：
          NpmArtifactPipeline.exe --per-dataset 10
          NpmArtifactPipeline.exe --bronze 10 --ceramic 20 --jade 15 --enamel 6 --lacquer 6 --studio-implements 6 --miscellaneous 6 --embroideries 6 --painting 5 --calligraphy 8 --calligraphic-models 6 --coins 6 --textiles 6 --rubbings 6 --fans 6 --carvings 6
          NpmArtifactPipeline.exe --estimate-only
          NpmArtifactPipeline.exe --no-images --output <output> --media-root <media-root>

        離線匯出既有資料：
          NpmArtifactPipeline.exe --offline --offline-input <既有 output 資料夾或 artifacts.import.json> --output <output>

        參數：
          --per-dataset <非負整數>      8 個期中正式類別的共同數量（預設 10；來源資料本身會形成實際上限）
          --bronze/--ceramic/--jade/--enamel/--lacquer/--studio-implements/--miscellaneous/--embroideries/--painting/--calligraphy/--calligraphic-models/--coins/--textiles/--rubbings/--fans/--carvings <非負整數>
          --output <path>              輸出根目錄
          --media-root <path>          圖片實體根目錄
          --no-images                  不下載圖片，圖片欄位輸出空字串並列入 quality report
          --offline                    不連線，從既有匯入 JSON 重新輸出
          --offline-input <path>       離線輸入檔案或 output 資料夾
          --selection-mode <diverse|random|sequential>  選取順序：欄位與年代多樣性、固定 seed 隨機、來源編號順序（預設 diverse）
          --seed <非負整數>             random 模式使用的固定 seed（預設 173）
          --estimate-only              只讀取 16 個 API 陣列筆數，不寫入 output、不下載圖片
          --all-categories             另外包含 8 個保留來源類別；僅供人工資料審核，不改變正式分類
          --verify-era-rules            執行內建年代規則回歸案例，不連線、不寫入 output
          --readable <none|csv|html|both>  額外產生人類閱讀版（預設 none）

        期中正式類別（8）：bronzes、ceramics、jades、enamelWares、lacquerWares、coins、carvings、paintings。
        保留來源（8）：studioImplements、miscellaneousObjects、textiles、tapestriesAndEmbroideries、calligraphicWorks、calligraphicModelBooks、rubbings、fans；只有 --all-categories 才會輸出。
        """;

    public static PipelineOptions Parse(string[] args)
    {
        var showHelp = args.Any(arg => arg is "--help" or "-h");
        var perDataset = ReadInt(args, "--per-dataset", 10);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in Definitions)
        {
            counts[definition.FileName] = ReadOptionalInt(args, $"--{definition.FileName}", $"--{definition.FileName}-count")
                ?? perDataset;
        }

        var defaultOutputRoot = ResolveDefaultOutputRoot();
        var output = Path.GetFullPath(ReadString(args, "--output",
            Path.Combine(defaultOutputRoot, "current")));
        var mediaRoot = Path.GetFullPath(ReadString(args, "--media-root",
            Path.Combine(defaultOutputRoot, "media")));
        var seed = ReadInt(args, "--seed", 173);
        var selectionMode = NormalizeSelectionMode(ReadString(args, "--selection-mode", "diverse"));
        var readableFormat = NormalizeReadableFormat(ReadString(args, "--readable", "none"));
        var offlineValue = ReadOptionalString(args, "--offline-input");
        var offlineInput = offlineValue is null
            ? (HasFlag(args, "--offline") ? ResolveOfflineInput(output) : null)
            : ResolveOfflineInput(Path.GetFullPath(offlineValue));
        var downloadImages = !HasFlag(args, "--no-images") && !HasFlag(args, "--no-download-images");
        if (HasFlag(args, "--download-images"))
        {
            var value = ReadOptionalString(args, "--download-images");
            if (value is not null && bool.TryParse(value, out var parsed))
                downloadImages = parsed;
        }

        var estimateOnly = HasFlag(args, "--estimate-only");
        return new PipelineOptions(output, mediaRoot, perDataset, counts, seed, selectionMode, downloadImages, offlineInput, showHelp, readableFormat, estimateOnly, HasFlag(args, "--verify-era-rules"), HasFlag(args, "--all-categories"));
    }

    public object ToManifestParameters() => new
    {
        perDataset = PerDataset,
        counts = Counts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        seed = Seed,
        selectionMode = SelectionMode,
        outputDirectory = Path.GetFileName(Path.TrimEndingDirectorySeparator(OutputDirectory)),
        mediaRoot = Path.GetRelativePath(
            Path.GetDirectoryName(OutputDirectory) ?? Environment.CurrentDirectory,
            MediaRoot).Replace('\\', '/'),
        downloadImages = DownloadImages,
        offlineInput = OfflineInput is null ? null : Path.GetFileName(OfflineInput),
        readableFormat = ReadableFormat
    };

    private static string ResolveOfflineInput(string path)
    {
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path))
            throw new FileNotFoundException("找不到離線輸入檔案或資料夾。", path);
        string[] candidates =
        [
            Path.Combine(path, "import", "artifacts.json"),
            Path.Combine(path, "artifacts.import.json"),
            Path.Combine(path, "processed", "artifacts.json")
        ];
        var input = candidates.FirstOrDefault(File.Exists);
        return input ?? throw new FileNotFoundException("離線輸入資料夾內找不到 import/artifacts.json、artifacts.import.json 或 processed/artifacts.json。", path);
    }

    private static string ResolveDefaultOutputRoot()
    {
        var configured = Environment.GetEnvironmentVariable("QMAH_TOOL_OUTPUT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (current.Name.Equals("_工具輸出", StringComparison.OrdinalIgnoreCase))
                return current.FullName;

            if (current.Name.Equals("00_五系統整合基準", StringComparison.OrdinalIgnoreCase)
                || current.Name.Equals("QMAH", StringComparison.OrdinalIgnoreCase)
                || current.Name.Equals("共用資料工具", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(current.Parent?.FullName ?? current.FullName, "_工具輸出");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QMAH", "ToolOutput");
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        var value = ReadOptionalInt(args, name);
        return value ?? fallback;
    }

    private static int? ReadOptionalInt(string[] args, params string[] names)
    {
        foreach (var name in names)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) continue;
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value))
                throw new ArgumentException($"參數 {name} 需要整數。 ");
            if (value < 0)
                throw new ArgumentException($"參數 {names[0]} 不可為負數。");
            return value;
        }
        return null;
    }

    private static string ReadString(string[] args, string name, string fallback) =>
        ReadOptionalString(args, name) ?? fallback;

    private static string? ReadOptionalString(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeReadableFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "csv" => "csv",
        "html" => "html",
        "both" => "both",
        _ => "none"
    };

    private static string NormalizeSelectionMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "diverse" or "quality" => "diverse",
        "random" or "shuffle" => "random",
        "sequential" or "sequence" or "ordered" => "sequential",
        _ => throw new ArgumentException("--selection-mode 必須是 diverse、random 或 sequential。")
    };

    private sealed record DatasetDefinition(string Code, string DisplayName, string ApiName, string FileName, bool IncludedInMidterm);
}

static class OfflineInput
{
    public static async Task<OfflineInputData> LoadAsync(string inputPath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(inputPath, JsonDefaults.Utf8NoBom, cancellationToken);
        var records = JsonSerializer.Deserialize<List<ArtifactImportRow>>(json, JsonDefaults.Source)
            ?? throw new InvalidDataException("離線輸入 JSON 不是 Artifacts 陣列。 ");
        return new OfflineInputData(inputPath, records);
    }
}

sealed record OfflineInputData(string InputPath, IReadOnlyList<ArtifactImportRow> Records);

sealed class NpmSourceRow
{
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("size")] public string? Size { get; set; }
    [JsonPropertyName("era")] public string? Era { get; set; }
    [JsonPropertyName("desc")] public string? Desc { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("imageUrl_s")] public string? ImageUrlS { get; set; }
    [JsonPropertyName("imageUrl_m")] public string? ImageUrlM { get; set; }
}

sealed record ArtifactImportRow(
    Guid Id,
    string ArtifactRef,
    string Name,
    string CategoryCode,
    string CategoryName,
    string EraTextOriginal,
    string EraBucketCode,
    string DescriptionOriginal,
    string SourceUrl,
    string ImageUrl,
    string SourcePayloadJson,
    string NormalizationStatus,
    bool QuestionEnabled,
    string AttributionText,
    int? EraEndYear,
    int? EraStartYear,
    string LicenseCode,
    string SizeOriginal,
    string SourceDataset,
    string ThumbnailUrl);

sealed record ArtifactQualityRow(
    string ArtifactRef,
    string Dataset,
    bool RequiresEraReview,
    bool QuestionEnabled,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<QualityFailure> Failures,
    string EraConfidence,
    string? EraMatchedRule,
    string EraEvidence,
    string? EraReviewReason,
    string SourcePayloadSha256);

sealed record QualityFailure(string Stage, string Field, string Message);

sealed record PipelineFailure(string Dataset, string ArtifactRef, string Stage, string Field, string Message);

sealed record DatasetRunStats(
    string Code,
    string DisplayName,
    string ApiName,
    int RequestedCount,
    int SourceRowCount,
    int SelectedCount,
    int OutputCount,
    int QuestionEnabledCount,
    int ReviewRequiredCount,
    int RecordsWithMissingFields,
    int RecordsWithFailures,
    int FailureCount,
    string RawPath,
    string RawSha256);

sealed record FileHash(string Path, long SizeBytes, string Sha256);

sealed record MediaFileHash(string Path, long? SizeBytes, string? Sha256, bool Missing);

sealed record MediaFailure(string Field, string Message);

sealed record MediaPaths(string? DisplayPath, string? ThumbnailPath, IReadOnlyList<MediaFailure> Failures)
{
    public static MediaPaths Disabled { get; } = new("", "", []);
}

static class MediaDownloader
{
    public static async Task<MediaPaths> DownloadAsync(
        HttpClient http,
        NpmSourceRow source,
        string categoryCode,
        string mediaRoot,
        CancellationToken cancellationToken)
    {
        var safeRef = Regex.Replace(source.Identifier, @"[^\p{L}\p{N}_-]", "_");
        var shard = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Identifier)))[..2].ToLowerInvariant();
        var relativeDirectory = Path.Combine("artifacts", categoryCode.ToLowerInvariant(), shard, safeRef);
        var physicalDirectory = Path.Combine(mediaRoot, relativeDirectory);
        Directory.CreateDirectory(physicalDirectory);
        var failures = new List<MediaFailure>();
        var display = await DownloadOneAsync(http, source.ImageUrlM, physicalDirectory, "display", failures, cancellationToken);
        var thumbnail = await DownloadOneAsync(http, source.ImageUrlS, physicalDirectory, "thumbnail", failures, cancellationToken);
        return new MediaPaths(
            display is null ? "" : ToRelativeMediaPath(relativeDirectory, display),
            thumbnail is null ? "" : ToRelativeMediaPath(relativeDirectory, thumbnail),
            failures);
    }

    private static async Task<string?> DownloadOneAsync(
        HttpClient http,
        string? url,
        string directory,
        string stem,
        List<MediaFailure> failures,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var existing = Directory.EnumerateFiles(directory, stem + ".*").FirstOrDefault();
        if (existing is not null) return Path.GetFileName(existing);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var extension = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".jpg"
            };
            var filename = stem + extension;
            var destination = Path.Combine(directory, filename);
            var temporary = destination + ".download";
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                await using (var output = File.Create(temporary))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return filename;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(new MediaFailure(stem + "Url", ex.Message));
            Console.Error.WriteLine($"MEDIA_FAIL|{stem}|{Shorten(url)}|{ex.Message}");
            return null;
        }
    }

    private static string ToRelativeMediaPath(string directory, string filename) =>
        Path.Combine(directory, filename).Replace('\\', '/');

    private static string Shorten(string value) => value.Length <= 120 ? value : value[..120] + "...";
}

static class ArtifactSchema
{
    public static readonly string[] Columns =
    [
        "Id", "ArtifactRef", "Name", "CategoryCode", "CategoryName", "EraTextOriginal", "EraBucketCode",
        "DescriptionOriginal", "SourceUrl", "ImageUrl", "SourcePayloadJson", "NormalizationStatus",
        "QuestionEnabled", "AttributionText", "EraEndYear", "EraStartYear", "LicenseCode", "SizeOriginal",
        "SourceDataset", "ThumbnailUrl"
    ];
}

sealed record EraResult(
    string Bucket,
    int? StartYear,
    int? EndYear,
    string Confidence,
    string? RuleId,
    string Evidence,
    string? ReviewReason)
{
    public bool RequiresReview => !string.Equals(Confidence, "HIGH", StringComparison.Ordinal);
}

sealed record EraRule(string Id, string[] Tokens, string Bucket, int? StartYear, int? EndYear, string Confidence, int? YearOne = null);

sealed record EraOverride(string Bucket, int? StartYear, int? EndYear, string? Reason);

static class EraNormalizer
{
    private static readonly Lazy<IReadOnlyList<EraRule>> Rules = new(LoadRules);
    private static readonly Lazy<IReadOnlyDictionary<string, EraOverride>> Overrides = new(LoadOverrides);

    public static EraResult Normalize(string? rawEra, string? artifactRef)
    {
        if (!string.IsNullOrWhiteSpace(artifactRef) && Overrides.Value.TryGetValue(artifactRef, out var manual))
            return new(manual.Bucket, manual.StartYear, manual.EndYear, "HIGH", "MANUAL_OVERRIDE", "ArtifactRef 人工覆寫", manual.Reason ?? "已由人工覆寫。 ");

        var era = NormalizeText(rawEra);
        if (era.Length == 0)
            return new("UNKNOWN", null, null, "LOW", null, "原始年代文字空白", "沒有可判讀的原始年代文字。 ");

        var matches = Rules.Value
            .SelectMany(rule => rule.Tokens.Select(token => (Rule: rule, Token: NormalizeText(token))))
            .Where(x => x.Token.Length > 1 && era.Contains(x.Token, StringComparison.Ordinal))
            .OrderByDescending(x => x.Token.Length)
            .ThenByDescending(x => ConfidenceRank(x.Rule.Confidence))
            .ToList();
        var distinctRules = matches
            .GroupBy(x => x.Rule.Bucket, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(x => x.Token.Length)
                .ThenByDescending(x => ConfidenceRank(x.Rule.Confidence))
                .ThenByDescending(x => x.Rule.YearOne.HasValue)
                .First().Rule)
            .ToList();
        var years = ParseExplicitYears(era);

        if (distinctRules.Count > 1)
        {
            var allStarts = distinctRules.Select(x => x.StartYear).Where(x => x.HasValue).Select(x => x!.Value).Concat(years).ToList();
            var allEnds = distinctRules.Select(x => x.EndYear).Where(x => x.HasValue).Select(x => x!.Value).Concat(years).ToList();
            return new("CROSS_ERA", allStarts.Count == 0 ? null : allStarts.Min(), allEnds.Count == 0 ? null : allEnds.Max(), "LOW", string.Join(",", distinctRules.Select(x => x.Id)), $"同時命中：{string.Join("、", distinctRules.Select(x => x.Id))}", "原始文字含多個年代規則，需人工確認實際年代。 ");
        }

        if (distinctRules.Count == 1)
        {
            var rule = distinctRules[0];
            var matchedToken = matches[0].Token;
            var eraYear = ParseEraYear(era, matchedToken, rule.YearOne);
            var start = eraYear ?? (years.Count == 0 ? rule.StartYear : years.Min());
            var end = eraYear ?? (years.Count == 0 ? rule.EndYear : years.Max());
            var evidence = eraYear.HasValue
                ? $"年號「{matchedToken}」換算為西元 {eraYear.Value} 年"
                : $"最長完整詞命中「{matchedToken}」";
            return new(rule.Bucket, start, end, rule.Confidence, rule.Id, evidence, rule.Confidence == "HIGH" ? null : "規則年代範圍較寬，需人工確認。 ");
        }

        var century = Regex.Match(era, @"(?<!\d)(\d{1,2})世紀(?!\d)");
        if (century.Success && int.TryParse(century.Groups[1].Value, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 30)
            return new("CENTURY", (number - 1) * 100 + 1, number * 100, "LOW", "CENTURY", $"解析「{century.Value}」", "世紀未標示前後紀元或地區，需人工確認。 ");

        if (years.Count > 0)
            return new(years.Count == 1 ? "EXPLICIT_YEAR" : "EXPLICIT_RANGE", years.Min(), years.Max(), "MEDIUM", "GREGORIAN_YEAR", "解析原文中的明確西元年份", "已取得年份，但沒有可靠的年代分類規則。 ");

        return new("UNKNOWN", null, null, "LOW", null, "未命中完整年代規則", "無法可靠判斷，不自動推定年份。 ");
    }

    public static int VerifyRegressionCases()
    {
        var cases = new[]
        {
            ("明治時代", "JAPAN_MEIJI", "HIGH"),
            ("大明嘉靖年間", "MING", "HIGH"),
            ("清康熙", "QING", "HIGH"),
            ("清康熙五十二年", "QING", "HIGH"),
            ("明嘉靖十六年", "MING", "HIGH"),
            ("民國110年", "REPUBLIC", "MEDIUM"),
            ("明治45年", "JAPAN_MEIJI", "HIGH"),
            ("昭和64年", "JAPAN_SHOWA", "HIGH"),
            ("遼代", "LIAO", "HIGH"),
            ("南北朝", "NORTH_SOUTH", "MEDIUM"),
            ("西元1644年至1912年", "EXPLICIT_RANGE", "MEDIUM"),
            ("民國", "REPUBLIC", "MEDIUM"),
            ("新石器時代良渚文化", "CROSS_ERA", "LOW"),
            ("18世紀", "CENTURY", "LOW"),
            ("年代不詳", "UNKNOWN", "LOW")
        };
        var failed = 0;
        foreach (var (raw, bucket, confidence) in cases)
        {
            var result = Normalize(raw, null);
            var passed = result.Bucket == bucket && result.Confidence == confidence;
            Console.WriteLine($"ERA_TEST|{(passed ? "PASS" : "FAIL")}|{raw}|{result.Bucket}|{result.Confidence}|{result.RuleId ?? "-"}");
            if (!passed) failed++;
        }
        return failed == 0 ? 0 : 1;
    }

    private static IReadOnlyList<EraRule> LoadRules() => LoadJson<List<EraRule>>("era-rules.json") ?? throw new InvalidDataException("找不到或無法讀取 era-rules.json。 ");

    private static IReadOnlyDictionary<string, EraOverride> LoadOverrides() =>
        LoadJson<Dictionary<string, EraOverride>>("era-overrides.json") ?? new Dictionary<string, EraOverride>(StringComparer.Ordinal);

    private static T? LoadJson<T>(string filename)
    {
        var path = Environment.GetEnvironmentVariable(filename == "era-rules.json" ? "QMAH_ERA_RULES" : "QMAH_ERA_OVERRIDES") ?? Path.Combine(AppContext.BaseDirectory, filename);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Source) : default;
    }

    private static string NormalizeText(string? value) => (value ?? "").Normalize(NormalizationForm.FormKC).Replace(" ", "", StringComparison.Ordinal).Replace("　", "", StringComparison.Ordinal).Trim();

    private static List<int> ParseExplicitYears(string era) => Regex.Matches(era, @"(?<!\d)(?:西元)?(1[0-9]{3}|20[0-9]{2})(?!\d)").Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

    private static int? ParseEraYear(string era, string token, int? yearOne)
    {
        if (!yearOne.HasValue) return null;
        var match = Regex.Match(era, Regex.Escape(token) + @"(?:元|([1-9]\d?))年");
        if (!match.Success) return null;
        var number = match.Value.Contains("元年", StringComparison.Ordinal) ? 1 : int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return yearOne.Value + number - 1;
    }

    private static int ConfidenceRank(string confidence) => confidence switch { "HIGH" => 3, "MEDIUM" => 2, _ => 1 };
}

static class JsonDefaults
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    public static readonly Encoding Utf8Bom = new UTF8Encoding(true);
    public static readonly JsonSerializerOptions Source = new() { PropertyNameCaseInsensitive = true };
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static readonly JsonSerializerOptions PrettyCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
