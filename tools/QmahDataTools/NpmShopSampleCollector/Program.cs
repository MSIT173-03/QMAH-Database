using System.Collections.Concurrent;
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

// CLI 工具不接受直接雙擊執行；沒有參數時立即結束，避免誤觸發線上抓取。
if (args.Length == 0)
    return 0;

if (args.Any(x => string.Equals(x, "--help", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(x, "-h", StringComparison.OrdinalIgnoreCase)))
{
    RuntimeOptions.PrintHelp();
    return 0;
}

try
{
    var runtime = RuntimeOptions.Parse(args);
    return await new CollectorRunner(runtime).RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Collector failed: {ex.Message}");
    return 2;
}

static class ShopCollectorConstants
{
    public const string DefaultSourceRoot = "https://panel.npmshops.com/mainssl/modules/MySpace";
    public const string DefaultSourceProvider = "NPM_SHOP_PUBLIC_PAGE";
    public const string DefaultMediaUrlPrefix = "/media";
    public const string CollectorVersion = "4.0";
}

sealed partial class CollectorRunner(RuntimeOptions runtime)
{
    private readonly RuntimeOptions _runtime = runtime;
    private readonly List<QualityExclusion> _exclusions = [];
    private readonly List<string> _warnings = [];
    private readonly List<AutoDiscoveredSourceCategory> _autoDiscoveredSourceCategories = [];
    private readonly List<RawProductRecord> _rawRecords = [];
    private readonly List<ProcessedProductRecord> _processedRecords = [];
    private readonly HashSet<string> _seenExternalRefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _seenNames = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;

    public async Task<int> RunAsync()
    {
        var settings = _runtime.Settings;
        if (_runtime.DiscoverStructure)
            return await DiscoverWebsiteStructureAsync();

        var buckets = CategoryBucket.Create(settings, _warnings);
        if (_runtime.EstimateOnly)
            return await EstimateAsync(buckets);

        var outputDirectory = settings.OutputDirectory;

        Console.WriteLine(_runtime.DryRun ? "MODE|offline" : "MODE|online");
        WriteProgress(buckets, "start", null);

        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(Path.Combine(outputDirectory, "raw"));
        Directory.CreateDirectory(Path.Combine(outputDirectory, "processed"));
        if (!_runtime.DryRun)
            Directory.CreateDirectory(settings.MediaRoot);

        if (_runtime.DryRun)
        {
            await LoadOfflineCandidatesAsync(buckets);
            await ProcessQueuesAsync(buckets, null);
            if (string.IsNullOrWhiteSpace(_runtime.OfflineInputPath))
                _warnings.Add("DRY_RUN_WITHOUT_INPUT");
        }
        else
        {
            using var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = settings.MaxConcurrentRequests,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
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
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)
            };
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Qingming-NpmShopCollector", ShopCollectorConstants.CollectorVersion));
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-TW,zh;q=0.9,en;q=0.5");

            var throttle = new RequestThrottle(
            settings.ThrottleMilliseconds,
            settings.CooldownEveryRequests,
            settings.CooldownMilliseconds,
            settings.JitterMilliseconds);
            var robots = new RobotsChecker(http, throttle, settings, _warnings);
            var fetcher = new HttpFetcher(
                http,
                throttle,
                robots,
                settings.MaxRetries,
                settings.RetryBaseDelayMilliseconds);
            await AutoDiscoverSourceEntriesAsync(fetcher);
            await DiscoverCandidatesAsync(buckets, fetcher);
            await ProcessQueuesAsync(buckets, fetcher);
        }

        if (_autoDiscoveredSourceCategories.Count > 0)
        {
            var sourceCatalogDirectory = Path.GetDirectoryName(_runtime.SourceCatalogPath)
                ?? outputDirectory;
            Directory.CreateDirectory(sourceCatalogDirectory);
            var autoCatalogPath = Path.Combine(sourceCatalogDirectory, "source-categories.auto.json");
            await File.WriteAllTextAsync(
                autoCatalogPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    note = "由商城根頁低成本自動觀察；需由商城開發者確認後，才可寫回正式設定或資料庫分類。",
                    categories = _autoDiscoveredSourceCategories
                }, JsonDefaults.Pretty),
                new UTF8Encoding(false));
            _warnings.Add($"AUTO_DISCOVERY_REPORT:{autoCatalogPath}");
        }

        var result = new CollectorResult(
            _startedAtUtc,
            DateTimeOffset.UtcNow,
            settings,
            buckets,
            _rawRecords,
            _processedRecords,
            _exclusions,
            _warnings,
            _runtime.DryRun,
            _runtime.SettingsPath,
            _runtime.OfflineInputPath);

        await OutputWriter.WriteAsync(result);

        if (!string.IsNullOrWhiteSpace(_runtime.LegacyImportPath))
        {
            var generatedImportPath = Path.Combine(settings.OutputDirectory, "products.import.json");
            if (!string.Equals(
                    Path.GetFullPath(generatedImportPath),
                    Path.GetFullPath(_runtime.LegacyImportPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_runtime.LegacyImportPath)!);
                File.Copy(generatedImportPath, _runtime.LegacyImportPath, true);
            }
        }

        WriteProgress(buckets, "done", null);
        Console.WriteLine($"SUMMARY|accepted={_processedRecords.Count}|target={settings.TargetTotal}|attempted={buckets.Sum(x => x.AttemptedCount)}|discovered={buckets.Sum(x => x.DiscoveredCount)}|excluded={_exclusions.Count}");
        Console.WriteLine($"Collected {_processedRecords.Count}/{settings.TargetTotal} products.");
        Console.WriteLine($"Output: {Path.GetFullPath(settings.OutputDirectory)}");
        if (_processedRecords.Count < settings.TargetTotal)
        {
            Console.Error.WriteLine(
                $"Target {settings.TargetTotal} was not reached; see quality-report.json for category gaps and exclusions.");
        }

        return 0;
    }

    private async Task AutoDiscoverSourceEntriesAsync(HttpFetcher fetcher)
    {
        var settings = _runtime.Settings;
        if (!settings.AutoDiscoverCategories || string.IsNullOrWhiteSpace(settings.SourceRoot))
            return;

        try
        {
            Console.WriteLine($"AUTO_DISCOVER|source=ROOT|limit={settings.AutoDiscoverMaxEntries}");
            var rootUrl = settings.SourceRoot.EndsWith("/", StringComparison.Ordinal)
                ? settings.SourceRoot
                : settings.SourceRoot + "/";
            var html = await fetcher.GetStringAsync(rootUrl);
            var links = HtmlParser.ExtractCategoryLinks(html, rootUrl)
                .Take(settings.AutoDiscoverMaxEntries)
                .ToArray();
            var existing = settings.SourceEntries
                .Select(x => x.Code)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowed = settings.AllowedSourceCategories;

            foreach (var link in links)
            {
                if (existing.Contains(link.Code)
                    || (allowed.Count > 0 && !allowed.Any(value =>
                        string.Equals(value, link.Code, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, link.Name, StringComparison.OrdinalIgnoreCase))))
                    continue;

                var mapped = SourceCatalogLoader.ResolveMappedCategory(settings, null, link.Name);
                settings.SourceEntries.Add(new SourceEntrySetting
                {
                    Code = link.Code,
                    Name = link.Name,
                    Url = link.UrlTemplate,
                    CategoryCode = mapped,
                    Enabled = true
                });
                existing.Add(link.Code);
                var observed = new AutoDiscoveredSourceCategory(
                    link.Code,
                    link.Name,
                    link.UrlTemplate,
                    mapped,
                    "root-page",
                    DateTimeOffset.UtcNow);
                _autoDiscoveredSourceCategories.Add(observed);
                _warnings.Add($"AUTO_DISCOVERED_SOURCE_CATEGORY:{link.Code}:{link.Name}:mapped={mapped}");
                Console.WriteLine($"AUTO_DISCOVERED|code={link.Code}|name={link.Name}|mapped={mapped}");
            }

            if (links.Length == settings.AutoDiscoverMaxEntries)
                _warnings.Add($"AUTO_DISCOVERY_LIMIT_REACHED:{settings.AutoDiscoverMaxEntries}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _warnings.Add($"AUTO_DISCOVERY_FAILED:{ShortError(ex)}");
            Console.Error.WriteLine($"AUTO_DISCOVER_FAILED|error={ShortError(ex)}");
        }
    }

    private async Task DiscoverCandidatesAsync(
        IReadOnlyList<CategoryBucket> buckets,
        HttpFetcher fetcher)
    {
        var settings = _runtime.Settings;
        // 先收集受控數量的商品連結就進入商品頁驗證，不必把所有分類頁都翻完。
        // 這會提早發現價格、圖片或重複資料問題，並在達成目標時停止再請求新的分類頁。
        var processingThreshold = Math.Min(
            Math.Max(buckets.Count * 3, 24),
            Math.Max(24, settings.TargetTotal));
        foreach (var entry in settings.SourceEntries
                     .Where(x => x.Enabled)
                     .Where(x => SettingsRules.IsAllowedSourceEntry(settings, x)))
        {
            if (_processedRecords.Count >= settings.TargetTotal)
                return;
            var categoryCode = NormalizeCode(entry.CategoryCode);
            var bucket = buckets.FirstOrDefault(x => x.Code == categoryCode);
            if (bucket is null)
            {
                AddExclusion(null, entry.Url, categoryCode, entry.Name, "INVALID_SOURCE_CATEGORY",
                    $"sourceEntry={entry.Code}");
                continue;
            }

            if (!SettingsRules.IsAllowedCategory(settings, bucket))
            {
                AddExclusion(null, entry.Url, bucket.Code, bucket.Name, "CATEGORY_NOT_ALLOWED",
                    $"sourceEntry={entry.Code}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Url))
            {
                AddExclusion(null, null, bucket.Code, bucket.Name, "SOURCE_URL_MISSING",
                    $"sourceEntry={entry.Code}");
                continue;
            }

            for (var page = 1; settings.MaxPages == 0 || page <= settings.MaxPages; page++)
            {
                var pageUrl = UrlBuilder.BuildPageUrl(entry.Url, page);
                Console.WriteLine($"FETCH|{bucket.Code}|source={entry.Code}|page={page}");
                string html;
                try
                {
                    html = await fetcher.GetStringAsync(pageUrl);
                }
                catch (Exception ex)
                {
                    AddExclusion(null, pageUrl, bucket.Code, bucket.Name, "SOURCE_PAGE_FETCH_FAILED",
                        $"sourceEntry={entry.Code};page={page};error={ShortError(ex)}");
                    _warnings.Add($"Source page failed: {pageUrl} ({ShortError(ex)})");
                    break;
                }

                var links = HtmlParser.ExtractProductLinks(html, pageUrl);
                if (links.Count == 0)
                {
                    Console.WriteLine($"DISCOVER|{bucket.Code}|source={entry.Code}|page={page}|links=0|new=0|queue={bucket.Queue.Count}");
                    break;
                }

                var pageNewCount = 0;
                foreach (var link in links)
                {
                    var externalRef = link.ExternalRef;
                    var urlKey = UrlBuilder.Normalize(link.ProductUrl);
                    var duplicateReasons = new List<string>();
                    if (string.IsNullOrWhiteSpace(externalRef))
                        externalRef = "url:" + StableShortHash(urlKey);
                    else if (!_seenExternalRefs.Add(externalRef))
                        duplicateReasons.Add("DUPLICATE_EXTERNAL_REF");

                    if (!_seenUrls.Add(urlKey))
                        duplicateReasons.Add("DUPLICATE_URL");

                    var reference = new CandidateReference(
                        entry.Code,
                        entry.Name,
                        bucket.Code,
                        bucket.Name,
                        externalRef,
                        link.ProductUrl,
                        pageUrl,
                        page,
                        null);

                    if (duplicateReasons.Count > 0)
                    {
                        AddExclusion(reference.ExternalRef, reference.ProductUrl, bucket.Code, bucket.Name,
                            duplicateReasons, "source discovery duplicate");
                        _rawRecords.Add(RawProductRecord.ForExcludedReference(reference, duplicateReasons, ""));
                        continue;
                    }

                    bucket.Queue.Enqueue(reference);
                    bucket.DiscoveredCount++;
                    pageNewCount++;
                }

                Console.WriteLine($"DISCOVER|{bucket.Code}|source={entry.Code}|page={page}|links={links.Count}|new={pageNewCount}|queue={bucket.Queue.Count}");

                var queuedCount = buckets.Sum(item => item.Queue.Count);
                if (!_runtime.EstimateOnly && queuedCount >= processingThreshold)
                {
                    Console.WriteLine($"PROCESS_EARLY|queued={queuedCount}|threshold={processingThreshold}|accepted={_processedRecords.Count}");
                    await ProcessQueuesAsync(buckets, fetcher);
                    if (_processedRecords.Count >= settings.TargetTotal)
                        return;
                }

                // 同一頁內容重複時 pageNewCount 會是 0；不能以「連續兩頁新增數剛好相同」提前停止，
                // 否則真實商城每頁固定筆數時會在第 2 頁就少抓大量商品。
                if (pageNewCount == 0)
                    break;
            }
        }
    }

    private async Task ProcessQueuesAsync(
        IReadOnlyList<CategoryBucket> buckets,
        HttpFetcher? fetcher)
    {
        var settings = _runtime.Settings;
        var round = 0;
        using var concurrency = new SemaphoreSlim(settings.MaxConcurrentRequests, settings.MaxConcurrentRequests);
        while (_processedRecords.Count < settings.TargetTotal)
        {
            var hasCandidate = buckets.Any(x => x.Queue.Count > 0 && x.AcceptedCount < x.Maximum);
            if (!hasCandidate)
                break;

            round++;
            var fillingMinimums = buckets.Any(x => x.Queue.Count > 0 && x.AcceptedCount < x.Minimum);
            var remaining = settings.TargetTotal - _processedRecords.Count;
            var batch = new List<(CategoryBucket Bucket, CandidateReference Reference)>();
            foreach (var bucket in buckets)
            {
                if (batch.Count >= remaining)
                    break;
                if (bucket.Queue.Count == 0 || bucket.AcceptedCount >= bucket.Maximum)
                    continue;
                if (fillingMinimums && bucket.AcceptedCount >= bucket.Minimum)
                    continue;

                var candidate = bucket.Queue.Dequeue();
                bucket.AttemptedCount++;
                batch.Add((bucket, candidate));
            }

            if (batch.Count == 0)
                break;

            var results = await Task.WhenAll(batch.Select(async item =>
            {
                await concurrency.WaitAsync();
                try
                {
                    return (item.Bucket, Outcome: await ProcessCandidateAsync(item.Reference, fetcher, round));
                }
                finally
                {
                    concurrency.Release();
                }
            }));

            foreach (var result in results)
            {
                var outcome = result.Outcome;
                _rawRecords.Add(outcome.Raw);
                if (outcome.Product is null)
                {
                    if (outcome.Exclusion is not null)
                        _exclusions.Add(outcome.Exclusion);
                    WriteProgress(buckets, "process", result.Bucket);
                    continue;
                }

                result.Bucket.AcceptedCount++;
                _processedRecords.Add(outcome.Product);
                WriteProgress(buckets, "process", result.Bucket);
            }

            WriteProgress(buckets, "batch", null);
        }
    }

    private async Task<CandidateOutcome> ProcessCandidateAsync(
        CandidateReference reference,
        HttpFetcher? fetcher,
        int selectionRound)
    {
        ProductPageData pageData;
        try
        {
            pageData = reference.OfflineData
                       ?? await FetchProductPageAsync(reference,
                           fetcher ?? throw new InvalidOperationException("HTTP fetcher is required for live collection."));
        }
        catch (Exception ex)
        {
            var reasons = new[] { "PRODUCT_PAGE_FETCH_FAILED" };
            var error = ShortError(ex);
            var failedRaw = RawProductRecord.ForFetchFailure(reference, reasons, error, selectionRound);
            var exclusion = MakeExclusion(reference, reasons, error);
            return new CandidateOutcome(failedRaw, null, exclusion);
        }

        var reasonsList = new List<string>();
        var details = new List<string>();
        var name = Text.Clean(pageData.Name);
        var description = Text.Clean(pageData.Description);
        var priceText = Text.Clean(pageData.PriceText);
        var price = pageData.Price ?? MoneyParser.Parse(priceText);
        var remoteImageUrl = UrlBuilder.NormalizeRemoteImage(pageData.RemoteImageUrl);

        if (string.IsNullOrWhiteSpace(name))
            reasonsList.Add("NAME_MISSING");

        var matchedTerms = _runtime.Settings.ExcludeTerms
            .Where(x => !string.IsNullOrWhiteSpace(x)
                        && name.Contains(x, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matchedTerms.Length > 0)
        {
            reasonsList.Add("EXCLUDED_TERM");
            details.Add("terms=" + string.Join("|", matchedTerms));
        }

        var unavailableTerms = new[] { "已售完", "缺貨", "停售", "下架", "暫停販售", "sold out", "out of stock" }
            .Where(term => name.Contains(term, StringComparison.OrdinalIgnoreCase)
                           || description.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (unavailableTerms.Length > 0)
        {
            reasonsList.Add("PRODUCT_UNAVAILABLE");
            details.Add("unavailableTerms=" + string.Join("|", unavailableTerms));
        }

        if (!SettingsRules.MatchesAllowedKeywords(_runtime.Settings, name, description,
                reference.EntryName, reference.CategoryName))
        {
            reasonsList.Add("KEYWORD_NOT_ALLOWED");
        }

        if (price is null || price <= 0)
            reasonsList.Add("PRICE_MISSING_OR_INVALID");

        var normalizedName = Text.NormalizeName(name);
        var nameReserved = !string.IsNullOrWhiteSpace(normalizedName)
                           && _seenNames.TryAdd(normalizedName, 0);
        if (!string.IsNullOrWhiteSpace(normalizedName) && !nameReserved)
        {
            reasonsList.Add("DUPLICATE_NORMALIZED_NAME");
            details.Add("normalizedName=" + normalizedName);
        }

        var image = reasonsList.Count > 0
            ? new ImageResolution(null, "NOT_ATTEMPTED_AFTER_FILTER", false, null)
            : await ResolveImageAsync(reference, remoteImageUrl, fetcher);
        var finalImageUrl = image.ImageUrl;
        if (reasonsList.Count == 0 && !image.Success)
        {
            if (_runtime.Settings.ImageRequired ||
                string.Equals(_runtime.Settings.MissingImagePolicy, "exclude", StringComparison.OrdinalIgnoreCase))
            {
                reasonsList.Add(image.Status);
            }
            else if (string.Equals(_runtime.Settings.MissingImagePolicy, "keep-remote", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(remoteImageUrl))
            {
                finalImageUrl = remoteImageUrl;
            }
            else if (string.Equals(_runtime.Settings.MissingImagePolicy, "keep-empty", StringComparison.OrdinalIgnoreCase))
            {
                finalImageUrl = "";
            }
            else
            {
                reasonsList.Add("MISSING_IMAGE_POLICY_INVALID");
            }
        }

        var status = reasonsList.Count == 0 ? "ACCEPTED" : "EXCLUDED";
        var raw = new RawProductRecord(
            reference.EntryCode,
            reference.EntryName,
            reference.CategoryCode,
            reference.CategoryName,
            reference.ExternalRef,
            reference.ProductUrl,
            reference.SourceListUrl,
            reference.PageNumber,
            name,
            description,
            priceText,
            price,
            remoteImageUrl,
            image.ImageUrl ?? "",
            image.Status,
            status,
            reasonsList,
            string.Join(";", details.Concat(image.Detail is null ? [] : [image.Detail])),
            pageData.SourcePayloadJson,
            selectionRound);

        if (reasonsList.Count > 0)
        {
            if (nameReserved)
                _seenNames.TryRemove(normalizedName, out _);
            return new CandidateOutcome(raw, null, MakeExclusion(reference, reasonsList, string.Join(";", details)));
        }

        // 匯入檔遵守正式 store.Products 的欄位長度；raw JSON 仍保留清理前的完整文字，
        // 讓真實商城偶爾出現超長名稱或網址時不會讓整批 SQL 匯入失敗。
        var storeName = Trim(name, 200);
        var storeImageUrl = Trim(finalImageUrl ?? "", 500);
        var storeSourceUrl = Trim(reference.ProductUrl, 1000);
        var storeExternalRef = reference.ExternalRef.Length <= 100
            ? reference.ExternalRef
            : "url:" + StableShortHash(reference.ProductUrl);
        var product = new ProductImportRecord(
            StableGuid(storeExternalRef),
            storeName,
            Trim(description, 1200),
            price!.Value,
            Math.Max(0, _runtime.Settings.DemoStock),
            storeImageUrl,
            storeSourceUrl,
            _runtime.Settings.SourceProvider,
            storeExternalRef,
            true,
            reference.CategoryCode,
            reference.CategoryName);

        return new CandidateOutcome(
            raw,
            new ProcessedProductRecord(
                product.Id,
                product.Name,
                product.Description,
                product.Price,
                product.Stock,
                product.ImageUrl,
                product.SourceUrl,
                product.SourceProvider,
                product.ExternalRef,
                product.IsActive,
                product.CategoryCode,
                product.CategoryName,
                normalizedName,
                image.Status,
                reference.EntryCode,
                reference.EntryName,
                selectionRound),
            null);
    }

    private async Task<ProductPageData> FetchProductPageAsync(
        CandidateReference reference,
        HttpFetcher fetcher)
    {
        var html = await fetcher.GetStringAsync(reference.ProductUrl);
        var name = HtmlParser.Meta(html, "og:title");
        if (string.IsNullOrWhiteSpace(name))
            name = HtmlParser.Title(html);

        var description = HtmlParser.Meta(html, "og:description");
        if (string.IsNullOrWhiteSpace(description))
            description = HtmlParser.Meta(html, "description");
        var remoteImage = HtmlParser.Meta(html, "og:image");
        if (string.IsNullOrWhiteSpace(remoteImage))
            remoteImage = HtmlParser.Meta(html, "twitter:image");
        if (string.IsNullOrWhiteSpace(remoteImage))
            remoteImage = HtmlParser.FirstImage(html, reference.ProductUrl);
        var priceText = HtmlParser.FindPriceText(html);
        var price = MoneyParser.Parse(priceText);
        var sourcePayload = JsonSerializer.Serialize(new
        {
            title = name,
            description,
            priceText,
            price,
            imageUrl = remoteImage,
            sourceUrl = reference.ProductUrl
        }, JsonDefaults.Compact);

        return new ProductPageData(
            reference.ExternalRef,
            reference.ProductUrl,
            name,
            description,
            priceText,
            price,
            remoteImage,
            sourcePayload);
    }

    private async Task<ImageResolution> ResolveImageAsync(
        CandidateReference reference,
        string remoteImageUrl,
        HttpFetcher? fetcher)
    {
        if (reference.OfflineData is not null)
        {
            return string.IsNullOrWhiteSpace(remoteImageUrl)
                ? new ImageResolution(null, "MISSING_IMAGE_URL", false, null)
                : new ImageResolution(remoteImageUrl, "DRY_RUN_INPUT_IMAGE", true, null);
        }

        if (string.IsNullOrWhiteSpace(remoteImageUrl))
            return new ImageResolution(null, "MISSING_IMAGE_URL", false, null);

        var shard = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference.ExternalRef)))[..2]
            .ToLowerInvariant();
        var safeRef = Text.SafePathPart(reference.ExternalRef);
        var relativeDirectory = Path.Combine("products", shard, safeRef);
        var physicalDirectory = Path.Combine(_runtime.Settings.MediaRoot, relativeDirectory);
        Directory.CreateDirectory(physicalDirectory);

        var existing = Directory.EnumerateFiles(physicalDirectory, "image.*")
            .FirstOrDefault(path => new FileInfo(path).Length > 0);
        if (existing is not null)
        {
            return new ImageResolution(
                UrlBuilder.MediaPath(_runtime.Settings.MediaUrlPrefix, relativeDirectory, Path.GetFileName(existing)),
                "LOCAL_IMAGE_REUSED",
                true,
                null);
        }

        try
        {
            var downloaded = await (fetcher ?? throw new InvalidOperationException(
                "HTTP fetcher is required to download images.")).GetBytesAsync(remoteImageUrl);
            if (!ImageBytes.IsImage(downloaded.Bytes, downloaded.ContentType))
            {
                return new ImageResolution(null, "IMAGE_CONTENT_INVALID", false,
                    $"contentType={downloaded.ContentType ?? "unknown"}");
            }

            var extension = ImageBytes.Extension(downloaded.ContentType, downloaded.Bytes);
            var filename = "image" + extension;
            var destination = Path.Combine(physicalDirectory, filename);
            var temporary = destination + ".download";
            try
            {
                await File.WriteAllBytesAsync(temporary, downloaded.Bytes);
                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return new ImageResolution(
                UrlBuilder.MediaPath(_runtime.Settings.MediaUrlPrefix, relativeDirectory, filename),
                "IMAGE_DOWNLOADED",
                true,
                null);
        }
        catch (Exception ex)
        {
            return new ImageResolution(null, "IMAGE_DOWNLOAD_FAILED", false, ShortError(ex));
        }
    }

    private async Task LoadOfflineCandidatesAsync(IReadOnlyList<CategoryBucket> buckets)
    {
        var path = _runtime.OfflineInputPath;
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!File.Exists(path))
            throw new FileNotFoundException("Offline input not found", path);

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        var rows = JsonDocumentHelpers.GetArray(document.RootElement);
        var rowNumber = 0;
        foreach (var row in rows)
        {
            rowNumber++;
            var name = JsonDocumentHelpers.GetString(row, "name", "title");
            var description = JsonDocumentHelpers.GetString(row, "description", "descriptionOriginal", "desc");
            var externalRef = JsonDocumentHelpers.GetString(row, "externalRef", "identifier", "id");
            var sourceUrl = JsonDocumentHelpers.GetString(row, "sourceUrl", "url");
            var categoryCode = JsonDocumentHelpers.GetString(row, "categoryCode", "dataset");
            var categoryName = JsonDocumentHelpers.GetString(row, "categoryName", "category");
            var imageUrl = JsonDocumentHelpers.GetString(row, "imageUrl", "imageUrlM", "imageUrl_m");
            var priceText = JsonDocumentHelpers.GetString(row, "priceText", "price");
            var price = JsonDocumentHelpers.GetDecimal(row, "price") ?? MoneyParser.Parse(priceText);

            categoryCode = ResolveOfflineCategoryCode(categoryCode, categoryName, name, buckets);
            var bucket = buckets.FirstOrDefault(x => x.Code == NormalizeCode(categoryCode));
            if (bucket is null)
            {
                AddExclusion(externalRef, sourceUrl, categoryCode ?? "", categoryName ?? "",
                    "OFFLINE_CATEGORY_UNKNOWN", $"row={rowNumber}");
                continue;
            }
            if (!SettingsRules.IsAllowedCategory(_runtime.Settings, bucket))
            {
                AddExclusion(externalRef, sourceUrl, bucket.Code, bucket.Name,
                    "CATEGORY_NOT_ALLOWED", $"row={rowNumber}");
                continue;
            }

            sourceUrl = string.IsNullOrWhiteSpace(sourceUrl)
                ? $"offline://{Uri.EscapeDataString(externalRef ?? name ?? rowNumber.ToString(CultureInfo.InvariantCulture))}"
                : sourceUrl;
            externalRef = string.IsNullOrWhiteSpace(externalRef)
                ? "offline:" + StableShortHash(UrlBuilder.Normalize(sourceUrl))
                : externalRef.Trim();

            var urlKey = UrlBuilder.Normalize(sourceUrl);
            var duplicateReasons = new List<string>();
            if (!_seenExternalRefs.Add(externalRef)) duplicateReasons.Add("DUPLICATE_EXTERNAL_REF");
            if (!_seenUrls.Add(urlKey)) duplicateReasons.Add("DUPLICATE_URL");
            var reference = new CandidateReference(
                "OFFLINE_INPUT",
                Path.GetFileName(path),
                bucket.Code,
                bucket.Name,
                externalRef,
                sourceUrl,
                path,
                rowNumber,
                new ProductPageData(
                    externalRef,
                    sourceUrl,
                    name ?? "",
                    description ?? "",
                    priceText ?? "",
                    price,
                    imageUrl ?? "",
                    JsonSerializer.Serialize(row, JsonDefaults.Compact)));

            if (duplicateReasons.Count > 0)
            {
                AddExclusion(externalRef, sourceUrl, bucket.Code, bucket.Name, duplicateReasons,
                    $"row={rowNumber}");
                _rawRecords.Add(RawProductRecord.ForExcludedReference(reference, duplicateReasons,
                    $"row={rowNumber}"));
                continue;
            }

            bucket.Queue.Enqueue(reference);
            bucket.DiscoveredCount++;
        }

        Console.WriteLine($"DISCOVER|OFFLINE_INPUT|rows={rowNumber}|queued={buckets.Sum(x => x.Queue.Count)}|excluded={_exclusions.Count}");
        WriteProgress(buckets, "discovered", null);
    }

    private void WriteProgress(
        IReadOnlyList<CategoryBucket> buckets,
        string stage,
        CategoryBucket? currentBucket)
    {
        var target = Math.Max(1, _runtime.Settings.TargetTotal);
        var accepted = _processedRecords.Count;
        var percent = Math.Clamp((int)Math.Round(accepted * 100d / target), 0, 100);
        var category = currentBucket?.Code ?? "ALL";
        var attempted = buckets.Sum(x => x.AttemptedCount);
        var discovered = buckets.Sum(x => x.DiscoveredCount);
        Console.WriteLine(
            $"PROGRESS|{category}|{percent}|stage={stage}|accepted={accepted}|attempted={attempted}|discovered={discovered}|excluded={_exclusions.Count}|target={_runtime.Settings.TargetTotal}");
    }

    private string? ResolveOfflineCategoryCode(
        string? code,
        string? categoryName,
        string? name,
        IReadOnlyList<CategoryBucket> buckets)
    {
        var normalizedCode = NormalizeCode(code);
        var direct = buckets.FirstOrDefault(x => x.Code == normalizedCode);
        if (direct is not null)
            return direct.Code;

        var combined = string.Join(" ", code, categoryName, name);
        foreach (var bucket in buckets)
        {
            if (bucket.Setting.Keywords.Any(keyword => !string.IsNullOrWhiteSpace(keyword)
                                                       && combined.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                return bucket.Code;
        }

        var aliases = new (string Code, string[] Terms)[]
        {
            ("STATIONERY", ["文具", "文房", "筆", "書寫"]),
            ("TABLEWARE", ["陶瓷", "餐瓷", "茶器", "茶具", "杯", "碗"]),
            ("APPAREL_ACCESSORIES", ["服飾", "配件", "絲巾", "領帶", "包"]),
            ("TOYS_PUZZLES", ["玩具", "拼圖", "積木"]),
            ("PAINTING_REPRODUCTION", ["書法", "繪畫", "畫作", "畫心", "手卷", "圖像", "複製", "清明上河圖", "富春山居圖", "快雪時晴帖"]),
            ("HOME_DECOR", ["家飾", "擺設", "居家", "多寶格"]),
            ("COLLECTION_DERIVATIVE", ["典藏", "國寶", "選粹"]),
            ("OTHER_CULTURAL_DERIVATIVE", ["翠玉", "玉器", "肉形石", "毛公鼎", "珍玩", "文物"])
        };
        foreach (var alias in aliases)
        {
            if (alias.Terms.Any(term => combined.Contains(term, StringComparison.OrdinalIgnoreCase))
                && buckets.Any(x => x.Code == alias.Code))
                return alias.Code;
        }

        return buckets.FirstOrDefault(x => x.Code == "OTHER_CULTURAL_DERIVATIVE")?.Code;
    }

    private QualityExclusion MakeExclusion(
        CandidateReference reference,
        IEnumerable<string> reasons,
        string details) =>
        new(reference.ExternalRef, reference.ProductUrl, reference.CategoryCode, reference.CategoryName,
            "", reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), details);

    private void AddExclusion(
        string? externalRef,
        string? sourceUrl,
        string categoryCode,
        string categoryName,
        string reason,
        string details) =>
        AddExclusion(externalRef, sourceUrl, categoryCode, categoryName, [reason], details);

    private void AddExclusion(
        string? externalRef,
        string? sourceUrl,
        string categoryCode,
        string categoryName,
        IEnumerable<string> reasons,
        string details) =>
        _exclusions.Add(new QualityExclusion(
            externalRef ?? "",
            sourceUrl ?? "",
            categoryCode,
            categoryName,
            "",
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            details));

    private static string NormalizeCode(string? value) => (value ?? "").Trim().ToUpperInvariant();

    private static string ShortError(Exception ex)
    {
        var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 240 ? message : message[..240];
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd() + "…";

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"npm-shop-sample:{value.Trim()}"));
        return new Guid(bytes[..16]);
    }

    private static string StableShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}

sealed record RuntimeOptions(
    CollectorSettings Settings,
    bool DryRun,
    string? SettingsPath,
    string? OfflineInputPath,
    string? LegacyImportPath,
    bool EstimateOnly,
    bool DiscoverStructure,
    string SourceCatalogPath)
{
    public static RuntimeOptions Parse(string[] args)
    {
        var settingsPathValue = ReadOption(args, "--settings");
        var settingsPath = string.IsNullOrWhiteSpace(settingsPathValue)
            ? null
            : Path.GetFullPath(settingsPathValue);
        // 相對路徑固定以外部工具輸出根目錄為基準，避免設定檔位於子資料夾時產生多份輸出。
        var settingsBaseDirectory = ResolveDefaultOutputRoot();

        var settings = settingsPath is null
            ? SettingsFactory.CreateDefaults()
            : SettingsLoader.Load(settingsPath);
        var sourceCatalogValue = ReadOption(args, "--source-catalog");
        var sourceCatalogPath = string.IsNullOrWhiteSpace(sourceCatalogValue)
            ? Path.Combine(settingsBaseDirectory, "NpmShopSampleCollector", "shop-source-catalog.json")
            : ResolvePath(sourceCatalogValue, settingsBaseDirectory);

        if (int.TryParse(ReadOption(args, "--count"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var count))
            settings.TargetTotal = count;
        if (int.TryParse(ReadOption(args, "--delay-ms"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var delay))
            settings.ThrottleMilliseconds = delay;
        if (int.TryParse(ReadOption(args, "--jitter-ms"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var jitter))
            settings.JitterMilliseconds = jitter;
        if (int.TryParse(ReadOption(args, "--cooldown-every"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var cooldownEvery))
            settings.CooldownEveryRequests = cooldownEvery;
        if (int.TryParse(ReadOption(args, "--cooldown-ms"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var cooldownMilliseconds))
            settings.CooldownMilliseconds = cooldownMilliseconds;
        if (int.TryParse(ReadOption(args, "--max-concurrency"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var maxConcurrency))
            settings.MaxConcurrentRequests = maxConcurrency;
        if (int.TryParse(ReadOption(args, "--retries"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var retries))
            settings.MaxRetries = retries;
        if (int.TryParse(ReadOption(args, "--retry-delay-ms"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var retryDelay))
            settings.RetryBaseDelayMilliseconds = retryDelay;
        if (int.TryParse(ReadOption(args, "--timeout-seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var timeoutSeconds))
            settings.RequestTimeoutSeconds = timeoutSeconds;
        if (int.TryParse(ReadOption(args, "--max-pages"), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var maxPages))
            settings.MaxPages = maxPages;
        var categories = ReadOption(args, "--categories");
        if (!string.IsNullOrWhiteSpace(categories))
            settings.AllowedCategories = categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var sourceCategories = ReadOption(args, "--source-categories");
        if (!string.IsNullOrWhiteSpace(sourceCategories))
            settings.AllowedSourceCategories = sourceCategories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (HasFlag(args, "--auto-discover"))
            settings.AutoDiscoverCategories = true;
        if (HasFlag(args, "--no-auto-discover"))
            settings.AutoDiscoverCategories = false;
        SourceCatalogLoader.MergeSelectedEntries(settings, sourceCatalogPath);
        var readableFormat = ReadOption(args, "--readable");
        if (!string.IsNullOrWhiteSpace(readableFormat))
            settings.ReadableFormat = NormalizeReadableFormat(readableFormat);

        string? legacyImportPath = null;
        var outputOverride = ReadOption(args, "--output");
        if (!string.IsNullOrWhiteSpace(outputOverride))
        {
            var resolvedOutput = ResolvePath(outputOverride, settingsBaseDirectory);
            if (string.Equals(Path.GetExtension(resolvedOutput), ".json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(resolvedOutput), ".csv", StringComparison.OrdinalIgnoreCase))
            {
                legacyImportPath = resolvedOutput;
                settings.OutputDirectory = Path.GetDirectoryName(resolvedOutput)
                                           ?? throw new InvalidOperationException("Legacy output path has no directory.");
            }
            else
            {
                settings.OutputDirectory = outputOverride;
            }
        }
        else
            settings.OutputDirectory = settings.OutputDirectory;
        var mediaOverride = ReadOption(args, "--media-root");
        if (!string.IsNullOrWhiteSpace(mediaOverride))
            settings.MediaRoot = mediaOverride;
        else
            settings.MediaRoot = settings.MediaRoot;

        settings.OutputDirectory = ResolvePath(settings.OutputDirectory, settingsBaseDirectory);
        settings.MediaRoot = ResolvePath(settings.MediaRoot, settingsBaseDirectory);
        var offlineInput = ReadOption(args, "--offline-input") ?? settings.OfflineInput;
        offlineInput = string.IsNullOrWhiteSpace(offlineInput)
            ? null
            : ResolvePath(offlineInput, settingsBaseDirectory);

        var dryRun = HasFlag(args, "--dry-run") || offlineInput is not null;
        var estimateOnly = HasFlag(args, "--estimate-only");
        var discoverStructure = HasFlag(args, "--discover-structure") || HasFlag(args, "--refresh-catalog");
        SettingsLoader.Normalize(settings);
        return new RuntimeOptions(settings, dryRun, settingsPath, offlineInput, legacyImportPath, estimateOnly,
            discoverStructure, sourceCatalogPath);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("NpmShopSampleCollector");
        Console.WriteLine("  NpmShopSampleCollector.exe --settings .\\sample-settings.json");
        Console.WriteLine("  NpmShopSampleCollector.exe --settings .\\sample-settings.json --dry-run --offline-input <file>");
        Console.WriteLine();
        Console.WriteLine("Options: --settings, --dry-run, --estimate-only, --discover-structure, --auto-discover, --no-auto-discover, --source-catalog,");
        Console.WriteLine("         --offline-input, --count, --categories, --source-categories,");
        Console.WriteLine("         --delay-ms, --jitter-ms, --cooldown-every, --cooldown-ms, --max-concurrency,");
        Console.WriteLine("         --retries, --retry-delay-ms, --timeout-seconds, --max-pages,");
        Console.WriteLine("         --output, --media-root, --readable <none|csv|html|both>");
        Console.WriteLine("Readable preview is optional and defaults to none.");
        Console.WriteLine("If targetTotal exceeds category caps, autoExpandCategoryMaximum=true expands caps; see quality-report.json.");
        Console.WriteLine("Online requests use bounded concurrency, shared throttling, retry-backoff and robots.txt checks.");
        Console.WriteLine("--discover-structure updates a reviewable heuristic JSON catalog without writing output or downloading images.");
        Console.WriteLine("Online runs perform one bounded root-page category probe by default; new categories are added only to this run and source-categories.auto.json.");
        Console.WriteLine("Default output is outside the tool folder; set QMAH_TOOL_OUTPUT or use --output to choose another location.");
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
                return args[index + 1];
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(name.Length + 1)..];
        }
        return null;
    }

    private static string ResolvePath(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Output and media paths cannot be empty.");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
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

    private static string NormalizeReadableFormat(string value) => value.Trim().ToLowerInvariant() switch
    {
        "csv" => "csv",
        "html" => "html",
        "both" => "both",
        _ => "none"
    };
}

static class SettingsLoader
{
    public static CollectorSettings Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Settings file not found", path);
        var json = File.ReadAllText(path, Encoding.UTF8);
        var settings = JsonSerializer.Deserialize<CollectorSettings>(json, JsonDefaults.Settings)
                       ?? throw new InvalidOperationException("Settings JSON is empty.");
        var defaults = SettingsFactory.CreateDefaults();
        settings.Categories ??= defaults.Categories;
        settings.SourceEntries ??= defaults.SourceEntries;
        settings.ExcludeTerms ??= defaults.ExcludeTerms;
        if (string.IsNullOrWhiteSpace(settings.SourceRoot)) settings.SourceRoot = defaults.SourceRoot;
        if (string.IsNullOrWhiteSpace(settings.SourceProvider)) settings.SourceProvider = defaults.SourceProvider;
        if (string.IsNullOrWhiteSpace(settings.MediaUrlPrefix)) settings.MediaUrlPrefix = defaults.MediaUrlPrefix;
        if (string.IsNullOrWhiteSpace(settings.SourceNote)) settings.SourceNote = defaults.SourceNote;
        if (string.IsNullOrWhiteSpace(settings.RobotsNote)) settings.RobotsNote = defaults.RobotsNote;
        if (string.IsNullOrWhiteSpace(settings.InventoryNote)) settings.InventoryNote = defaults.InventoryNote;
        if (string.IsNullOrWhiteSpace(settings.NonAffiliation)) settings.NonAffiliation = defaults.NonAffiliation;
        if (string.IsNullOrWhiteSpace(settings.ReadableFormat)) settings.ReadableFormat = defaults.ReadableFormat;
        if (settings.Categories.Count == 0) settings.Categories = defaults.Categories;
        if (settings.SourceEntries.Count == 0) settings.SourceEntries = defaults.SourceEntries;
        return settings;
    }

    public static void Normalize(CollectorSettings settings)
    {
        settings.Categories ??= [];
        settings.SourceEntries ??= [];
        settings.AllowedCategories ??= [];
        settings.AllowedKeywords ??= [];
        settings.ExcludeTerms ??= [];
        settings.AllowedSourceCategories ??= [];
        settings.AdaptationNotes ??= [];
        settings.AdaptationNotes.Clear();
        settings.MissingImagePolicy ??= "exclude";
        settings.TargetTotal = Math.Clamp(settings.TargetTotal, 1, 1_000_000);
        settings.AutoDiscoverMaxEntries = Math.Clamp(settings.AutoDiscoverMaxEntries, 1, 100);
        settings.DefaultCategoryMinimum = Math.Clamp(settings.DefaultCategoryMinimum, 0, 1_000_000);
        settings.DefaultCategoryMaximum = Math.Clamp(settings.DefaultCategoryMaximum, 0, 1_000_000);
        settings.ThrottleMilliseconds = Math.Clamp(settings.ThrottleMilliseconds, 0, 600_000);
        settings.JitterMilliseconds = Math.Clamp(settings.JitterMilliseconds, 0, 10_000);
        settings.CooldownEveryRequests = Math.Clamp(settings.CooldownEveryRequests, 0, 1_000_000);
        settings.CooldownMilliseconds = Math.Clamp(settings.CooldownMilliseconds, 0, 3_600_000);
        settings.MaxConcurrentRequests = Math.Clamp(settings.MaxConcurrentRequests, 1, 12);
        settings.MaxRetries = Math.Clamp(settings.MaxRetries, 0, 6);
        settings.RetryBaseDelayMilliseconds = Math.Clamp(settings.RetryBaseDelayMilliseconds, 100, 10_000);
        settings.RequestTimeoutSeconds = Math.Clamp(settings.RequestTimeoutSeconds, 10, 300);
        // 0 代表不設頁數上限，直到來源耗盡或其他安全停止條件成立。
        settings.MaxPages = Math.Clamp(settings.MaxPages, 0, 1_000_000);
        settings.DemoStock = Math.Max(0, settings.DemoStock);
        settings.MissingImagePolicy = NormalizeImagePolicy(settings.MissingImagePolicy);
        settings.ReadableFormat = NormalizeReadableFormat(settings.ReadableFormat);
        settings.SourceProvider = string.IsNullOrWhiteSpace(settings.SourceProvider)
            ? ShopCollectorConstants.DefaultSourceProvider
            : settings.SourceProvider.Trim();
        settings.MediaUrlPrefix = UrlBuilder.NormalizePrefix(settings.MediaUrlPrefix);

        foreach (var category in settings.Categories)
        {
            category.Keywords ??= [];
            category.Code = category.Code.Trim().ToUpperInvariant();
            category.Name = category.Name.Trim();
            category.Minimum = category.Minimum is null
                ? settings.DefaultCategoryMinimum
                : Math.Clamp(category.Minimum.Value, 0, 1_000_000);
            category.Maximum = category.Maximum is null
                ? settings.DefaultCategoryMaximum
                : Math.Clamp(category.Maximum.Value, 0, 1_000_000);
            if (category.Maximum < category.Minimum)
                category.Maximum = category.Minimum;
        }

        var enabledCategories = settings.Categories
            .Where(x => x.Enabled)
            .Where(x => settings.AllowedCategories.Count == 0
                        || settings.AllowedCategories.Any(value =>
                            string.Equals(value, x.Code, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(value, x.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var configuredCapacity = enabledCategories.Sum(x => x.Maximum ?? 0);
        if (settings.AutoExpandCategoryMaximum
            && enabledCategories.Count > 0
            && settings.TargetTotal > configuredCapacity)
        {
            var expandedMaximum = (int)Math.Ceiling(
                settings.TargetTotal / (double)enabledCategories.Count);
            foreach (var category in enabledCategories)
            {
                if ((category.Maximum ?? 0) < expandedMaximum)
                    category.Maximum = expandedMaximum;
            }

            settings.AdaptationNotes.Add(
                $"AUTO_EXPAND_CATEGORY_MAXIMUM:{expandedMaximum} (target={settings.TargetTotal})");
        }

        settings.AllowedCategories = settings.AllowedCategories
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.AllowedKeywords = settings.AllowedKeywords
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.ExcludeTerms = settings.ExcludeTerms
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeImagePolicy(string value) => value.Trim().ToLowerInvariant() switch
    {
        "keepremote" => "keep-remote",
        "keepempty" => "keep-empty",
        "keep-remote" => "keep-remote",
        "keep-empty" => "keep-empty",
        _ => "exclude"
    };

    private static string NormalizeReadableFormat(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "csv" => "csv",
        "html" => "html",
        "both" => "both",
        _ => "none"
    };
}

static class SettingsFactory
{
    public static CollectorSettings CreateDefaults()
    {
        var settings = new CollectorSettings
        {
            SourceRoot = ShopCollectorConstants.DefaultSourceRoot,
            OutputDirectory = "products",
            MediaRoot = "media",
            MediaUrlPrefix = ShopCollectorConstants.DefaultMediaUrlPrefix,
            TargetTotal = 60,
            DefaultCategoryMinimum = 1,
            DefaultCategoryMaximum = 12,
            AutoExpandCategoryMaximum = true,
            ThrottleMilliseconds = 600,
            JitterMilliseconds = 200,
            CooldownEveryRequests = 30,
            CooldownMilliseconds = 10_000,
            MaxConcurrentRequests = 3,
            MaxRetries = 3,
            RetryBaseDelayMilliseconds = 500,
            RequestTimeoutSeconds = 45,
            ImageRequired = true,
            MissingImagePolicy = "exclude",
            MaxPages = 0,
            DemoStock = 10,
            SourceProvider = ShopCollectorConstants.DefaultSourceProvider,
            RespectRobotsTxt = true,
            SqlSchema = "store",
            ReadableFormat = "none",
            SourceNote = "僅讀取來源公開分類頁與商品頁的名稱、摘要、價格、圖片網址與商品連結；不宣稱為官方資料集。",
            RobotsNote = "執行前請確認來源 robots.txt 與網站條款；本工具不繞過 robots.txt，採有上限的並行請求、低頻率節流與退避重試。",
            InventoryNote = "stock 是清明鑑定屋 Demo 的可售庫存，不代表官方商城即時庫存。",
            NonAffiliation = "本資料包為學生專題內部展示資料，非國立故宮博物院或故宮精品網路商城官方製作。",
            ExcludeTerms = DefaultExcludeTerms()
        };

        settings.Categories =
        [
            new CategorySetting("COLLECTION_DERIVATIVE", "典藏衍生", 1, 6, ["典藏", "國寶", "選粹"]),
            new CategorySetting("STATIONERY", "文具", 1, 6, ["文具", "文房", "筆", "書寫"]),
            new CategorySetting("HOME_DECOR", "家飾", 1, 6, ["家飾", "擺設", "居家", "多寶格"]),
            new CategorySetting("TABLEWARE", "餐瓷／茶器", 1, 6, ["陶瓷", "餐瓷", "茶器", "茶具", "杯", "碗"]),
            new CategorySetting("APPAREL_ACCESSORIES", "服飾配件", 1, 6, ["服飾", "配件", "絲巾", "領帶", "包"]),
            new CategorySetting("TOYS_PUZZLES", "玩具／拼圖", 1, 6, ["玩具", "拼圖", "積木"]),
            new CategorySetting("PAINTING_REPRODUCTION", "書畫複製", 1, 6, ["書法", "繪畫", "畫作", "畫心", "手卷"]),
            new CategorySetting("OTHER_CULTURAL_DERIVATIVE", "其他文物衍生", 1, 6,
                ["翠玉", "玉器", "肉形石", "毛公鼎", "珍玩", "文物"])
        ];

        settings.SourceEntries = DefaultSourceEntries();
        return settings;
    }

    public static List<string> DefaultExcludeTerms() =>
    [
        "期刊", "月刊", "季刊", "年刊", "雜誌", "特刊", "訂閱", "電子報",
        "journal", "magazine", "monthly", "quarterly", "subscription"
    ];

    private static List<SourceEntrySetting> DefaultSourceEntries() =>
    [
        Source("COLLECTION_DERIVATIVE", "ZC523", "典藏精品", "COLLECTION_DERIVATIVE"),
        Source("COLLECTION_DERIVATIVE", "ZC7286630", "國寶特選", "COLLECTION_DERIVATIVE"),
        Source("COLLECTION_DERIVATIVE", "ZC7263806", "故宮選粹", "COLLECTION_DERIVATIVE"),
        Source("PAINTING_REPRODUCTION", "ZC1254365", "清明上河圖系列", "PAINTING_REPRODUCTION"),
        Source("PAINTING_REPRODUCTION", "ZC4246159", "翠玉白菜系列", "OTHER_CULTURAL_DERIVATIVE"),
        Source("PAINTING_REPRODUCTION", "ZC2154336", "肉形石系列", "OTHER_CULTURAL_DERIVATIVE"),
        Source("PAINTING_REPRODUCTION", "ZC2154387", "毛公鼎系列", "OTHER_CULTURAL_DERIVATIVE"),
        Source("PAINTING_REPRODUCTION", "ZC2154442", "富春山居圖系列", "PAINTING_REPRODUCTION"),
        Source("PAINTING_REPRODUCTION", "ZC7374126", "快雪時晴帖系列", "PAINTING_REPRODUCTION"),
        Source("HOME_DECOR", "ZC2154202", "多寶格系列", "HOME_DECOR"),
        Source("OTHER_CULTURAL_DERIVATIVE", "ZC2155875", "玉辟邪系列", "OTHER_CULTURAL_DERIVATIVE"),
        Source("TABLEWARE", "ZC524", "陶瓷", "TABLEWARE"),
        Source("OTHER_CULTURAL_DERIVATIVE", "ZC545", "珍玩", "OTHER_CULTURAL_DERIVATIVE"),
        Source("STATIONERY", "ZC535", "文房四寶與書法", "STATIONERY"),
        Source("PAINTING_REPRODUCTION", "ZC508", "書法繪畫", "PAINTING_REPRODUCTION"),
        Source("COLLECTION_DERIVATIVE", "ZC519", "展覽圖錄", "COLLECTION_DERIVATIVE")
    ];

    private static SourceEntrySetting Source(string categoryCode, string sourceCode, string name,
        string mappedCategoryCode) => new()
    {
        Code = sourceCode,
        Name = name,
        Url = $"{ShopCollectorConstants.DefaultSourceRoot}/PrdList.php?sn=shop&cn={sourceCode}&pn={{page}}",
        CategoryCode = mappedCategoryCode,
        Enabled = true
    };
}

sealed class CollectorSettings
{
    public string SourceRoot { get; set; } = ShopCollectorConstants.DefaultSourceRoot;
    public string OutputDirectory { get; set; } = "products";
    public string MediaRoot { get; set; } = "media";
    public string MediaUrlPrefix { get; set; } = ShopCollectorConstants.DefaultMediaUrlPrefix;
    public int TargetTotal { get; set; } = 60;
    public int DefaultCategoryMinimum { get; set; } = 1;
    public int DefaultCategoryMaximum { get; set; } = 12;
    public bool AutoExpandCategoryMaximum { get; set; } = true;
    public bool AutoDiscoverCategories { get; set; } = true;
    public int AutoDiscoverMaxEntries { get; set; } = 24;
    public List<CategorySetting> Categories { get; set; } = [];
    public List<SourceEntrySetting> SourceEntries { get; set; } = [];
    public List<string> AllowedCategories { get; set; } = [];
    public List<string> AllowedSourceCategories { get; set; } = [];
    public List<string> AllowedKeywords { get; set; } = [];
    public List<string> ExcludeTerms { get; set; } = SettingsFactory.DefaultExcludeTerms();
    public int ThrottleMilliseconds { get; set; } = 750;
    public int JitterMilliseconds { get; set; } = 200;
    public int CooldownEveryRequests { get; set; } = 50;
    public int CooldownMilliseconds { get; set; } = 5_000;
    public int MaxConcurrentRequests { get; set; } = 4;
    public int MaxRetries { get; set; } = 3;
    public int RetryBaseDelayMilliseconds { get; set; } = 500;
    public int RequestTimeoutSeconds { get; set; } = 45;
    public bool ImageRequired { get; set; } = true;
    public string MissingImagePolicy { get; set; } = "exclude";
    public int MaxPages { get; set; } = 0;
    public int DemoStock { get; set; } = 10;
    public string SourceProvider { get; set; } = ShopCollectorConstants.DefaultSourceProvider;
    public bool RespectRobotsTxt { get; set; } = true;
    public string? RobotsUrl { get; set; }
    public string SqlSchema { get; set; } = "store";
    public string ReadableFormat { get; set; } = "none";
    public List<string> AdaptationNotes { get; set; } = [];
    public string? OfflineInput { get; set; }
    public string SourceNote { get; set; } = "";
    public string RobotsNote { get; set; } = "";
    public string InventoryNote { get; set; } = "";
    public string NonAffiliation { get; set; } = "";
}

sealed class CategorySetting
{
    public CategorySetting() { }

    public CategorySetting(string code, string name, int minimum, int maximum, IEnumerable<string> keywords)
    {
        Code = code;
        Name = name;
        Minimum = minimum;
        Maximum = maximum;
        Keywords = keywords.ToList();
    }

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? Minimum { get; set; }
    public int? Maximum { get; set; }
    public bool Enabled { get; set; } = true;
    public List<string> Keywords { get; set; } = [];
}

sealed class SourceEntrySetting
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string CategoryCode { get; set; } = "";
    public List<string> Keywords { get; set; } = [];
    public bool Enabled { get; set; } = true;
}

sealed class CategoryBucket
{
    private CategoryBucket(CategorySetting setting)
    {
        Setting = setting;
        Code = setting.Code.Trim().ToUpperInvariant();
        Name = setting.Name.Trim();
        Minimum = setting.Minimum ?? 0;
        Maximum = setting.Maximum ?? 0;
    }

    public CategorySetting Setting { get; }
    public string Code { get; }
    public string Name { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public Queue<CandidateReference> Queue { get; } = [];
    public int DiscoveredCount { get; set; }
    public int AttemptedCount { get; set; }
    public int AcceptedCount { get; set; }

    public static List<CategoryBucket> Create(CollectorSettings settings, List<string> warnings)
    {
        var buckets = new List<CategoryBucket>();
        foreach (var setting in settings.Categories.Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(setting.Code) || string.IsNullOrWhiteSpace(setting.Name))
            {
                warnings.Add("CATEGORY_CODE_OR_NAME_MISSING");
                continue;
            }
            var bucket = new CategoryBucket(setting);
            if (buckets.Any(x => x.Code == bucket.Code))
            {
                warnings.Add("DUPLICATE_CATEGORY_CODE:" + bucket.Code);
                continue;
            }
            buckets.Add(bucket);
        }
        return buckets;
    }
}

sealed record AutoDiscoveredSourceCategory(
    string Code,
    string Name,
    string UrlTemplate,
    string MappedCategoryCode,
    string EvidenceSource,
    DateTimeOffset ObservedAtUtc);

sealed record CandidateReference(
    string EntryCode,
    string EntryName,
    string CategoryCode,
    string CategoryName,
    string ExternalRef,
    string ProductUrl,
    string SourceListUrl,
    int PageNumber,
    ProductPageData? OfflineData);

sealed record ProductPageData(
    string ExternalRef,
    string SourceUrl,
    string Name,
    string Description,
    string PriceText,
    decimal? Price,
    string RemoteImageUrl,
    string SourcePayloadJson);

sealed record ImageResolution(string? ImageUrl, string Status, bool Success, string? Detail);

sealed record CandidateOutcome(
    RawProductRecord Raw,
    ProcessedProductRecord? Product,
    QualityExclusion? Exclusion);

sealed record ProductImportRecord(
    Guid Id,
    string Name,
    string Description,
    decimal Price,
    int Stock,
    string ImageUrl,
    string SourceUrl,
    string SourceProvider,
    string ExternalRef,
    bool IsActive,
    string CategoryCode,
    string CategoryName);

sealed record ProcessedProductRecord(
    Guid Id,
    string Name,
    string Description,
    decimal Price,
    int Stock,
    string ImageUrl,
    string SourceUrl,
    string SourceProvider,
    string ExternalRef,
    bool IsActive,
    string CategoryCode,
    string CategoryName,
    string NormalizedName,
    string ImageStatus,
    string SourceEntryCode,
    string SourceEntryName,
    int SelectionRound);

sealed record RawProductRecord(
    string SourceEntryCode,
    string SourceEntryName,
    string CategoryCode,
    string CategoryName,
    string ExternalRef,
    string SourceUrl,
    string SourceListUrl,
    int PageNumber,
    string Name,
    string Description,
    string PriceText,
    decimal? Price,
    string RemoteImageUrl,
    string LocalImageUrl,
    string ImageStatus,
    string Status,
    IReadOnlyList<string> ExclusionReasons,
    string ExclusionDetails,
    string SourcePayloadJson,
    int SelectionRound)
{
    public static RawProductRecord ForExcludedReference(
        CandidateReference reference,
        IEnumerable<string> reasons,
        string details) => new(
            reference.EntryCode,
            reference.EntryName,
            reference.CategoryCode,
            reference.CategoryName,
            reference.ExternalRef,
            reference.ProductUrl,
            reference.SourceListUrl,
            reference.PageNumber,
            "",
            "",
            "",
            null,
            "",
            "",
            "",
            "EXCLUDED",
            reasons.ToArray(),
            details,
            "",
            0);

    public static RawProductRecord ForFetchFailure(
        CandidateReference reference,
        IEnumerable<string> reasons,
        string error,
        int selectionRound) => new(
            reference.EntryCode,
            reference.EntryName,
            reference.CategoryCode,
            reference.CategoryName,
            reference.ExternalRef,
            reference.ProductUrl,
            reference.SourceListUrl,
            reference.PageNumber,
            "",
            "",
            "",
            null,
            "",
            "",
            "",
            "EXCLUDED",
            reasons.ToArray(),
            error,
            "",
            selectionRound);
}

sealed record QualityExclusion(
    string ExternalRef,
    string SourceUrl,
    string CategoryCode,
    string CategoryName,
    string Name,
    IReadOnlyList<string> Reasons,
    string Details);

sealed record CollectorResult(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    CollectorSettings Settings,
    IReadOnlyList<CategoryBucket> Buckets,
    IReadOnlyList<RawProductRecord> RawRecords,
    IReadOnlyList<ProcessedProductRecord> ProcessedRecords,
    IReadOnlyList<QualityExclusion> Exclusions,
    IReadOnlyList<string> Warnings,
    bool DryRun,
    string? SettingsPath,
    string? OfflineInputPath);

static class SettingsRules
{
    public static bool IsAllowedCategory(CollectorSettings settings, CategoryBucket bucket)
    {
        if (settings.AllowedCategories.Count == 0)
            return true;
        return settings.AllowedCategories.Any(value =>
            string.Equals(value, bucket.Code, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, bucket.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAllowedSourceEntry(CollectorSettings settings, SourceEntrySetting entry)
    {
        if (settings.AllowedSourceCategories.Count == 0)
            return true;
        return settings.AllowedSourceCategories.Any(value =>
            string.Equals(value, entry.Code, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, entry.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesAllowedKeywords(
        CollectorSettings settings,
        params string[] values)
    {
        if (settings.AllowedKeywords.Count == 0)
            return true;
        return settings.AllowedKeywords.Any(keyword =>
            values.Any(value => !string.IsNullOrWhiteSpace(value)
                                && value.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }
}

static class UrlBuilder
{
    public static string BuildPageUrl(string template, int page)
    {
        if (template.Contains("{page}", StringComparison.OrdinalIgnoreCase))
            return template.Replace("{page}", page.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        if (template.Contains("{pageNumber}", StringComparison.OrdinalIgnoreCase))
            return template.Replace("{pageNumber}", page.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        if (Regex.IsMatch(template, @"([?&])pn=\d+", RegexOptions.IgnoreCase))
            return Regex.Replace(template, @"([?&])pn=\d+", $"$1pn={page}", RegexOptions.IgnoreCase);
        return template + (template.Contains('?') ? "&" : "?") + "pn=" + page;
    }

    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(WebUtility.HtmlDecode(value.Trim()), UriKind.Absolute, out var uri))
            return value.Trim().TrimEnd('/').ToLowerInvariant();
        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static string NormalizePrefix(string? value)
    {
        var prefix = string.IsNullOrWhiteSpace(value) ? ShopCollectorConstants.DefaultMediaUrlPrefix : value.Trim();
        return "/" + prefix.Trim('/');
    }

    public static string NormalizeRemoteImage(string? value)
    {
        var image = WebUtility.HtmlDecode(value ?? "").Trim();
        if (image.StartsWith("//", StringComparison.Ordinal))
            return "https:" + image;
        return image;
    }

    public static string MediaPath(string prefix, string relativeDirectory, string filename) =>
        (NormalizePrefix(prefix) + "/" + Path.Combine(relativeDirectory, filename).Replace('\\', '/')).Replace("//", "/");
}

static partial class HtmlParser
{
    private static readonly Regex MetaTagRegex = new("<meta\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProductLinkRegex = new(
        "<(?:a|span)\\b[^>]*?href\\s*=\\s*(?:\\\"(?<url>[^\\\"]+)\\\"|'(?<url>[^']+)'|(?<url>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImageTagRegex = new(
        "<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<PageLink> ExtractProductLinks(string html, string pageUrl)
    {
        var links = new List<PageLink>();
        foreach (Match match in ProductLinkRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            if (!href.Contains("PrdInfo.php", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(new Uri(pageUrl), href, out var absolute))
                continue;
            var url = absolute.ToString();
            var externalRef = ExtractQuery(url, "prd");
            if (string.IsNullOrWhiteSpace(externalRef))
                continue;
            links.Add(new PageLink(url, externalRef.Trim()));
        }
        return links;
    }

    public static string Meta(string html, string key)
    {
        foreach (Match tag in MetaTagRegex.Matches(html))
        {
            var name = Attribute(tag.Value, "name");
            var property = Attribute(tag.Value, "property");
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(property, key, StringComparison.OrdinalIgnoreCase))
                continue;
            return Text.Clean(WebUtility.HtmlDecode(Attribute(tag.Value, "content")));
        }
        return "";
    }

    public static string Title(string html)
    {
        var match = Regex.Match(html, "<title\\b[^>]*>(?<value>[\\s\\S]*?)</title>",
            RegexOptions.IgnoreCase);
        return match.Success ? Text.Clean(WebUtility.HtmlDecode(match.Groups["value"].Value)) : "";
    }

    public static string FindPriceText(string html)
    {
        var patterns = new[]
        {
            @"id=['""]meMsg_[^'""]*_MsgPrdPriceAmt['""][^>]*>\s*(?<price>(?:NT\$|NT＄|＄|\$)?\s*[\d,]+)",
            @"(?:售價|價格|定價)[^<]{0,80}(?<price>(?:NT\$|NT＄|＄|\$)?\s*[\d,]+)",
            @"(?:itemprop|data-price)\s*=\s*['""]price['""][^>]*?(?:content|data-value)\s*=\s*['""](?<price>[\d,]+)",
            @"(?:price|amount)\s*['""]?\s*[:=]\s*['""]?(?<price>[\d,]+)"
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return Text.Clean(WebUtility.HtmlDecode(match.Groups["price"].Value));
        }
        return "";
    }

    public static string FirstImage(string html, string pageUrl)
    {
        foreach (Match match in ImageTagRegex.Matches(html))
        {
            var tag = match.Value;
            var source = Attribute(tag, "data-src");
            if (string.IsNullOrWhiteSpace(source))
                source = Attribute(tag, "src");
            if (string.IsNullOrWhiteSpace(source)
                || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Uri.TryCreate(new Uri(pageUrl), WebUtility.HtmlDecode(source), out var absolute))
                return absolute.ToString();
        }
        return "";
    }

    private static string Attribute(string tag, string name)
    {
        var match = Regex.Match(tag,
            $@"\b{Regex.Escape(name)}\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))",
            RegexOptions.IgnoreCase);
        return match.Groups["double"].Success ? match.Groups["double"].Value
            : match.Groups["single"].Success ? match.Groups["single"].Value
            : match.Groups["bare"].Value;
    }

    private static string ExtractQuery(string url, string key)
    {
        var match = Regex.Match(url, $@"(?:[?&]){Regex.Escape(key)}=([^&#]+)", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.UrlDecode(match.Groups[1].Value) : "";
    }
}

sealed record PageLink(string ProductUrl, string ExternalRef);

static class MoneyParser
{
    public static decimal? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = WebUtility.HtmlDecode(value);
        if (Regex.IsMatch(text, @"[~～]|(?:至)|(?:起)|(?:約)", RegexOptions.IgnoreCase))
            return null;
        var amounts = Regex.Matches(text, @"(?<!\d)-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?(?!\d)")
            .Select(match => decimal.TryParse(match.Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : (decimal?)null)
            .Where(amount => amount.HasValue)
            .Select(amount => amount!.Value)
            .Distinct()
            .ToList();
        return amounts.Count == 1 ? amounts[0] : null;
    }
}

static class Text
{
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var withoutTags = Regex.Replace(value, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags), @"\s+", " ").Trim();
    }

    public static string NormalizeName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        return Regex.Replace(normalized, @"[^\p{L}\p{N}]", "");
    }

    public static string SafePathPart(string value)
    {
        var safe = Regex.Replace(value.Trim(), @"[^\p{L}\p{N}_-]", "_");
        if (safe.Length > 80) safe = safe[..80];
        return string.IsNullOrWhiteSpace(safe) ? "item" : safe;
    }
}

sealed class RequestThrottle(int delayMilliseconds, int cooldownEveryRequests, int cooldownMilliseconds, int jitterMilliseconds)
{
    private readonly int _delayMilliseconds = delayMilliseconds;
    private readonly int _cooldownEveryRequests = cooldownEveryRequests;
    private readonly int _cooldownMilliseconds = cooldownMilliseconds;
    private readonly int _jitterMilliseconds = jitterMilliseconds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private int _requestCount;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cooldownEveryRequests > 0
                && _cooldownMilliseconds > 0
                && _requestCount > 0
                && _requestCount % _cooldownEveryRequests == 0)
            {
                Console.WriteLine($"THROTTLE|cooldown|after={_requestCount}|milliseconds={_cooldownMilliseconds}");
                await Task.Delay(_cooldownMilliseconds, cancellationToken);
            }

            var effectiveDelay = _delayMilliseconds + (_jitterMilliseconds == 0
                ? 0
                : Random.Shared.Next(_jitterMilliseconds + 1));
            var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
            var remaining = TimeSpan.FromMilliseconds(effectiveDelay) - elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);
            _lastRequestAt = DateTimeOffset.UtcNow;
            _requestCount++;
        }
        finally
        {
            _gate.Release();
        }
    }
}

sealed class HttpFetcher(
    HttpClient http,
    RequestThrottle throttle,
    RobotsChecker robots,
    int maxRetries,
    int retryBaseDelayMilliseconds)
{
    private readonly HttpClient _http = http;
    private readonly RequestThrottle _throttle = throttle;
    private readonly RobotsChecker _robots = robots;
    private readonly int _maxRetries = maxRetries;
    private readonly int _retryBaseDelayMilliseconds = retryBaseDelayMilliseconds;

    public async Task<string> GetStringAsync(string url)
    {
        var uri = ToUri(url);
        await EnsureAllowedAsync(uri);
        using var response = await SendAsync(uri);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<DownloadedBytes> GetBytesAsync(string url)
    {
        var uri = ToUri(url);
        await EnsureAllowedAsync(uri);
        using var response = await SendAsync(uri);
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 25_000_000)
            throw new InvalidOperationException("image exceeds 25 MB limit");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new DownloadedBytes(bytes, response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                await _throttle.WaitAsync();
                var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                    return response;

                var retryable = IsRetryable(response.StatusCode);
                var retryAfter = ReadRetryAfter(response);
                var error = new HttpRequestException($"HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
                response.Dispose();
                if (!retryable)
                    throw new InvalidOperationException(error.Message);
                if (attempt >= _maxRetries)
                    throw error;

                lastError = error;
                await Task.Delay(retryAfter ?? Backoff(attempt), CancellationToken.None);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                lastError = ex;
                await Task.Delay(Backoff(attempt), CancellationToken.None);
            }
            catch (TaskCanceledException ex) when (attempt < _maxRetries)
            {
                lastError = ex;
                await Task.Delay(Backoff(attempt), CancellationToken.None);
            }
        }

        throw lastError ?? new HttpRequestException("HTTP request failed without a response.");
    }

    private TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(15_000, _retryBaseDelayMilliseconds * Math.Pow(2, attempt)));

    private static bool IsRetryable(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        (HttpStatusCode)425 or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private async Task EnsureAllowedAsync(Uri uri)
    {
        if (!await _robots.IsAllowedAsync(uri))
            throw new InvalidOperationException("robots.txt disallows this URL");
    }

    private static Uri ToUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException("source URL is not absolute: " + url);
}

sealed record DownloadedBytes(byte[] Bytes, string? ContentType);

sealed class RobotsChecker(
    HttpClient http,
    RequestThrottle throttle,
    CollectorSettings settings,
    List<string> warnings)
{
    private readonly ConcurrentDictionary<string, Task<RobotsRules>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warningHosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsAllowedAsync(Uri uri)
    {
        if (!settings.RespectRobotsTxt)
            return true;

        var hostKey = uri.GetLeftPart(UriPartial.Authority);
        var rules = await _cache.GetOrAdd(hostKey, _ => LoadAsync(uri, hostKey));
        return rules.IsAllowed(uri.AbsolutePath);
    }

    private async Task<RobotsRules> LoadAsync(Uri sourceUri, string hostKey)
    {
        try
        {
            var robotsUrl = string.IsNullOrWhiteSpace(settings.RobotsUrl)
                ? new Uri(sourceUri.GetLeftPart(UriPartial.Authority) + "/robots.txt")
                : new Uri(settings.RobotsUrl);
            await throttle.WaitAsync();
            using var response = await http.GetAsync(robotsUrl);
            if (!response.IsSuccessStatusCode)
            {
                WarnUnavailable(hostKey, $"status={(int)response.StatusCode}");
                return RobotsRules.AllowAll;
            }
            var text = await response.Content.ReadAsStringAsync();
            return RobotsRules.Parse(text);
        }
        catch (Exception ex)
        {
            WarnUnavailable(hostKey, ex.Message);
            return RobotsRules.AllowAll;
        }
    }

    private void WarnUnavailable(string host, string detail)
    {
        if (_warningHosts.Add(host))
            warnings.Add($"ROBOTS_UNAVAILABLE:{host}:{detail}");
    }
}

sealed class RobotsRules
{
    private readonly List<RobotsRule> _rules;

    private RobotsRules(List<RobotsRule> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);

    public bool IsAllowed(string path)
    {
        var match = _rules
            .Where(rule => path.StartsWith(rule.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(rule => rule.Path.Length)
            .FirstOrDefault();
        return match is null || match.Allow;
    }

    public static RobotsRules Parse(string text)
    {
        var rules = new List<RobotsRule>();
        var applies = false;
        var hasDirective = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0)
            {
                applies = false;
                hasDirective = false;
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (hasDirective) applies = false;
                applies = value == "*";
                continue;
            }
            if (!applies || (key is not "Disallow" and not "Allow") || string.IsNullOrWhiteSpace(value))
                continue;
            hasDirective = true;
            rules.Add(new RobotsRule(value, key.Equals("Allow", StringComparison.OrdinalIgnoreCase)));
        }
        return new RobotsRules(rules);
    }
}

sealed record RobotsRule(string Path, bool Allow);

static class ImageBytes
{
    public static bool IsImage(byte[] bytes, string? contentType)
    {
        if (bytes.Length == 0) return false;
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return true;
        return bytes.Length >= 4 && (
            (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            || (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            || (bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            || (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E'
                && bytes[10] == (byte)'B' && bytes[11] == (byte)'P'));
    }

    public static string Extension(string? contentType, byte[] bytes) =>
        contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/svg+xml" => ".svg",
            _ when bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF => ".jpg",
            _ => ".jpg"
        };
}

static class JsonDocumentHelpers
{
    public static IEnumerable<JsonElement> GetArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();
        foreach (var name in new[] { "records", "products", "items" })
        {
            if (TryGetProperty(root, name, out var property) && property.ValueKind == JsonValueKind.Array)
                return property.EnumerateArray().ToArray();
        }
        throw new InvalidOperationException("Offline input must be a JSON array or contain records/products/items array.");
    }

    public static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var property)) continue;
            if (property.ValueKind == JsonValueKind.String) return property.GetString();
            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.ToString();
        }
        return null;
    }

    public static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number)) return number;
        return property.ValueKind == JsonValueKind.String ? MoneyParser.Parse(property.GetString()) : null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

static class OutputWriter
{
    public static async Task WriteAsync(CollectorResult result)
    {
        var output = result.Settings.OutputDirectory;
        var rawJson = Path.Combine(output, "raw", "products.raw.json");
        var rawCsv = Path.Combine(output, "raw", "products.raw.csv");
        var processedJson = Path.Combine(output, "processed", "products.processed.json");
        var processedCsv = Path.Combine(output, "processed", "products.processed.csv");
        var importJson = Path.Combine(output, "products.import.json");
        var importCsv = Path.Combine(output, "products.import.csv");
        var upsertSql = Path.Combine(output, "products.upsert.sql");
        var manifestPath = Path.Combine(output, "manifest.json");
        var qualityPath = Path.Combine(output, "quality-report.json");

        await WriteJsonAsync(rawJson, result.RawRecords);
        await WriteCsvAsync(rawCsv,
            ["sourceEntryCode", "sourceEntryName", "categoryCode", "categoryName", "externalRef", "sourceUrl",
                "sourceListUrl", "pageNumber", "name", "description", "priceText", "price", "remoteImageUrl",
                "localImageUrl", "imageStatus", "status", "exclusionReasons", "exclusionDetails", "sourcePayloadJson",
                "selectionRound"],
            result.RawRecords.Select(row => new object?[]
            {
                row.SourceEntryCode, row.SourceEntryName, row.CategoryCode, row.CategoryName, row.ExternalRef,
                row.SourceUrl, row.SourceListUrl, row.PageNumber, row.Name, row.Description, row.PriceText,
                row.Price?.ToString("0.00", CultureInfo.InvariantCulture), row.RemoteImageUrl, row.LocalImageUrl,
                row.ImageStatus, row.Status, string.Join("|", row.ExclusionReasons), row.ExclusionDetails,
                row.SourcePayloadJson, row.SelectionRound
            }));

        await WriteJsonAsync(processedJson, result.ProcessedRecords);
        await WriteCsvAsync(processedCsv,
            ["id", "name", "description", "price", "stock", "imageUrl", "sourceUrl", "sourceProvider",
                "externalRef", "isActive", "categoryCode", "categoryName", "normalizedName", "imageStatus",
                "sourceEntryCode", "sourceEntryName", "selectionRound"],
            result.ProcessedRecords.Select(row => new object?[]
            {
                row.Id, row.Name, row.Description, row.Price.ToString("0.00", CultureInfo.InvariantCulture), row.Stock,
                row.ImageUrl, row.SourceUrl, row.SourceProvider, row.ExternalRef, row.IsActive, row.CategoryCode,
                row.CategoryName, row.NormalizedName, row.ImageStatus, row.SourceEntryCode, row.SourceEntryName,
                row.SelectionRound
            }));

        var imports = result.ProcessedRecords.Select(row => new ProductImportRecord(
            row.Id, row.Name, row.Description, row.Price, row.Stock, row.ImageUrl, row.SourceUrl,
            row.SourceProvider, row.ExternalRef, row.IsActive, row.CategoryCode, row.CategoryName)).ToArray();
        await WriteJsonAsync(importJson, imports);
        await WriteCsvAsync(importCsv,
            ["id", "name", "description", "price", "stock", "imageUrl", "sourceUrl", "sourceProvider",
                "externalRef", "isActive", "categoryCode", "categoryName"],
            imports.Select(row => new object?[]
            {
                row.Id, row.Name, row.Description, row.Price.ToString("0.00", CultureInfo.InvariantCulture), row.Stock,
                row.ImageUrl, row.SourceUrl, row.SourceProvider, row.ExternalRef, row.IsActive, row.CategoryCode,
                row.CategoryName
            }));

        // sqlcmd ODBC 會依 BOM 判斷 Unicode 輸入；SQL 另輸出 UTF-8 BOM，避免繁中註解或字串被系統 code page 誤讀。
        await File.WriteAllTextAsync(upsertSql, SqlServerUpsert.Build(imports, result.Settings.SqlSchema),
            new UTF8Encoding(true));
        var readableFiles = await WriteReadableExportsAsync(result, imports);
        var readableCsv = readableFiles.FirstOrDefault(path =>
            string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase));
        var readableHtml = readableFiles.FirstOrDefault(path =>
            string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase));

        var categoryQuality = result.Buckets.Select(bucket => BuildCategoryQuality(bucket, result)).ToArray();
        var reasonSummary = result.Exclusions
            .SelectMany(row => row.Reasons)
            .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { reason = group.Key, count = group.Count() })
            .ToArray();
        var quality = new
        {
            schemaVersion = 1,
            generatedAtUtc = result.FinishedAtUtc,
            targetTotal = result.Settings.TargetTotal,
            acceptedCount = result.ProcessedRecords.Count,
            targetGap = Math.Max(0, result.Settings.TargetTotal - result.ProcessedRecords.Count),
            categorySummary = categoryQuality,
            exclusionReasonSummary = reasonSummary,
            exclusions = result.Exclusions,
            warnings = result.Warnings,
            adaptations = result.Settings.AdaptationNotes,
            policy = new
            {
                imageRequired = result.Settings.ImageRequired,
                missingImagePolicy = result.Settings.MissingImagePolicy,
                throttleMilliseconds = result.Settings.ThrottleMilliseconds,
                jitterMilliseconds = result.Settings.JitterMilliseconds,
                cooldownEveryRequests = result.Settings.CooldownEveryRequests,
                cooldownMilliseconds = result.Settings.CooldownMilliseconds,
                maxConcurrentRequests = result.Settings.MaxConcurrentRequests,
                maxPages = result.Settings.MaxPages,
                respectRobotsTxt = result.Settings.RespectRobotsTxt,
                excludedTerms = result.Settings.ExcludeTerms,
                noCrossCategoryFill = true,
                autoExpandCategoryMaximum = result.Settings.AutoExpandCategoryMaximum,
                autoDiscoverCategories = result.Settings.AutoDiscoverCategories,
                autoDiscoverMaxEntries = result.Settings.AutoDiscoverMaxEntries,
                readableFormat = result.Settings.ReadableFormat
            }
        };
        await WriteJsonAsync(qualityPath, quality);

        var manifest = new
        {
            schemaVersion = 3,
            collectorVersion = ShopCollectorConstants.CollectorVersion,
            generatedAtUtc = result.FinishedAtUtc,
            startedAtUtc = result.StartedAtUtc,
            dryRun = result.DryRun,
            settingsFile = result.SettingsPath,
            offlineInput = result.OfflineInputPath,
            targetTotal = result.Settings.TargetTotal,
            recordCount = result.ProcessedRecords.Count,
            rawRecordCount = result.RawRecords.Count,
            source = new
            {
                root = result.Settings.SourceRoot,
                entries = result.Settings.SourceEntries.Where(x => x.Enabled).Select(x => new
                {
                    x.Code, x.Name, x.Url, x.CategoryCode
                }).ToArray(),
                note = result.Settings.SourceNote,
                robotsNote = result.Settings.RobotsNote
            },
            collectionPolicy = new
            {
                lowFrequency = true,
                throttleMilliseconds = result.Settings.ThrottleMilliseconds,
                jitterMilliseconds = result.Settings.JitterMilliseconds,
                cooldownEveryRequests = result.Settings.CooldownEveryRequests,
                cooldownMilliseconds = result.Settings.CooldownMilliseconds,
                maxConcurrentRequests = result.Settings.MaxConcurrentRequests,
                maxPages = result.Settings.MaxPages,
                respectRobotsTxt = result.Settings.RespectRobotsTxt,
                inventoryNote = result.Settings.InventoryNote,
                nonAffiliation = result.Settings.NonAffiliation
            },
            adaptations = result.Settings.AdaptationNotes,
            selection = new
            {
                allowedCategories = result.Settings.AllowedCategories,
                allowedSourceCategories = result.Settings.AllowedSourceCategories,
                allowedKeywords = result.Settings.AllowedKeywords,
                excludedTerms = result.Settings.ExcludeTerms,
                categorySummary = categoryQuality
            },
            files = new
            {
                rawJson = Relative(output, rawJson),
                rawCsv = Relative(output, rawCsv),
                processedJson = Relative(output, processedJson),
                processedCsv = Relative(output, processedCsv),
                importJson = Relative(output, importJson),
                importCsv = Relative(output, importCsv),
                upsertSql = Relative(output, upsertSql),
                qualityReport = Relative(output, qualityPath),
                readableCsv = readableCsv is null ? null : Relative(output, readableCsv),
                readableHtml = readableHtml is null ? null : Relative(output, readableHtml)
            }
        };
        await WriteJsonAsync(manifestPath, manifest);
        await WriteJsonAsync(Path.Combine(output, "products.manifest.json"), manifest);
    }

    private static object BuildCategoryQuality(CategoryBucket bucket, CollectorResult result)
    {
        var exclusionReasons = result.Exclusions
            .Where(row => string.Equals(row.CategoryCode, bucket.Code, StringComparison.OrdinalIgnoreCase))
            .SelectMany(row => row.Reasons)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var gap = Math.Max(0, bucket.Minimum - bucket.AcceptedCount);
        var gapReasons = new List<string>();
        if (gap > 0)
        {
            if (bucket.DiscoveredCount == 0) gapReasons.Add("NO_DISCOVERED_PRODUCTS");
            if (bucket.Queue.Count == 0) gapReasons.Add("SOURCE_EXHAUSTED_OR_MAX_PAGES_REACHED");
            gapReasons.AddRange(exclusionReasons);
        }
        return new
        {
            categoryCode = bucket.Code,
            categoryName = bucket.Name,
            minimum = bucket.Minimum,
            maximum = bucket.Maximum,
            discoveredCount = bucket.DiscoveredCount,
            attemptedCount = bucket.AttemptedCount,
            acceptedCount = bucket.AcceptedCount,
            remainingQueueCount = bucket.Queue.Count,
            minimumGap = gap,
            exclusionReasons,
            gapReasons = gapReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static async Task WriteJsonAsync(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, JsonDefaults.Pretty);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
    }

    private static async Task WriteCsvAsync(
        string path,
        IReadOnlyList<string> headers,
        IEnumerable<object?[]> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(Csv.Escape)));
        foreach (var row in rows)
            builder.AppendLine(string.Join(",", row.Select(value => Csv.Escape(Format(value)))));
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static async Task<IReadOnlyList<string>> WriteReadableExportsAsync(
        CollectorResult result,
        IReadOnlyList<ProductImportRecord> imports)
    {
        if (result.Settings.ReadableFormat == "none") return [];

        var previewDirectory = Directory.CreateDirectory(Path.Combine(result.Settings.OutputDirectory, "preview")).FullName;
        var paths = new List<string>();
        if (result.Settings.ReadableFormat is "csv" or "both")
        {
            var path = Path.Combine(previewDirectory, "商品資料預覽.csv");
            await WriteCsvAsync(path,
                ["商品編號", "商品名稱", "商品類別", "價格", "展示庫存", "圖片相對路徑", "商品頁", "外部編號", "是否上架"],
                imports.Select(product => new object?[]
                {
                    product.Id, product.Name, product.CategoryName,
                    product.Price.ToString("0.00", CultureInfo.InvariantCulture), product.Stock,
                    product.ImageUrl, product.SourceUrl, product.ExternalRef, product.IsActive
                }));
            paths.Add(path);
        }

        if (result.Settings.ReadableFormat is "html" or "both")
        {
            var path = Path.Combine(previewDirectory, "商品資料預覽.html");
            var html = new StringBuilder("<!doctype html><html lang=\"zh-Hant\"><meta charset=\"utf-8\"><title>商品資料預覽</title><style>body{font-family:system-ui,Microsoft JhengHei,sans-serif;background:#f6f3ee;color:#2c2926;margin:32px}h1{font-size:28px}table{border-collapse:collapse;background:white;width:100%}th,td{border:1px solid #d8d0c8;padding:9px;text-align:left;vertical-align:top}th{background:#eee6dc;white-space:nowrap}a{color:#7a3f32}</style><h1>商品資料預覽</h1><p>這是方便人工檢查的版本，不是商城正式資料庫的替代品</p><table><thead><tr><th>商品編號</th><th>商品名稱</th><th>商品類別</th><th>價格</th><th>展示庫存</th><th>圖片相對路徑</th><th>商品頁</th><th>外部編號</th><th>是否上架</th></tr></thead><tbody>");
            foreach (var product in imports)
            {
                html.Append("<tr>");
                foreach (var value in new[]
                {
                    product.Id.ToString(), product.Name, product.CategoryName,
                    product.Price.ToString("0.00", CultureInfo.InvariantCulture), product.Stock.ToString(CultureInfo.InvariantCulture),
                    product.ImageUrl, product.SourceUrl, product.ExternalRef, product.IsActive ? "是" : "否"
                })
                    html.Append("<td>").Append(WebUtility.HtmlEncode(value)).Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table></html>");
            await File.WriteAllTextAsync(path, html.ToString(), new UTF8Encoding(false));
            paths.Add(path);
        }

        return paths;
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        Guid guid => guid.ToString(),
        bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

static class Csv
{
    public static string Escape(string? value)
    {
        value ??= "";
        return value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}

static class SqlServerUpsert
{
    public static string Build(IEnumerable<ProductImportRecord> products, string schema)
    {
        var records = products.ToList();
        // 正式資料契約固定使用 store，避免舊設定檔把匯入檔誤寫到 dbo 或其他 Schema。
        const string safeSchema = "store";
        var table = $"[{safeSchema}].[Products]";
        var builder = new StringBuilder();
        builder.AppendLine("-- NpmShopSampleCollector｜可重複執行的 SQL Server 匯入檔");
        builder.AppendLine("-- 對應正式版 store.Products；圖片只保存相對路徑，不寫入資料庫二進位內容。");
        builder.AppendLine("SET ANSI_NULLS ON;");
        builder.AppendLine("SET QUOTED_IDENTIFIER ON;");
        builder.AppendLine("SET ANSI_PADDING ON;");
        builder.AppendLine("SET ANSI_WARNINGS ON;");
        builder.AppendLine("SET ARITHABORT ON;");
        builder.AppendLine("SET CONCAT_NULL_YIELDS_NULL ON;");
        builder.AppendLine("SET NUMERIC_ROUNDABORT OFF;");
        builder.AppendLine("SET XACT_ABORT ON;");
        builder.AppendLine("BEGIN TRANSACTION;");
        foreach (var product in records)
        {
            builder.AppendLine("MERGE " + table + " WITH (HOLDLOCK) AS target");
            builder.AppendLine("USING (VALUES (");
            builder.AppendLine("    CONVERT(uniqueidentifier, " + String(product.Id.ToString()) + "),");
            builder.AppendLine("    " + String(product.CategoryCode) + ",");
            builder.AppendLine("    " + String(product.ExternalRef) + ",");
            builder.AppendLine("    " + String(product.Name) + ",");
            builder.AppendLine("    " + String(product.Description) + ",");
            builder.AppendLine("    CONVERT(decimal(12,2), " + product.Price.ToString("0.00", CultureInfo.InvariantCulture) + "),");
            builder.AppendLine("    " + product.Stock.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("    " + String(product.ImageUrl) + ",");
            builder.AppendLine("    " + String(product.SourceUrl) + ",");
            builder.AppendLine("    " + (product.IsActive ? "1" : "0"));
            builder.AppendLine(")) AS source ([Id], [CategoryCode], [ExternalRef], [Name], [Description], [Price], [Stock], [PrimaryImagePath], [SourceUrl], [IsActive])");
            builder.AppendLine("ON target.[ExternalRef] = source.[ExternalRef] AND target.[ExternalRef] IS NOT NULL");
            builder.AppendLine("WHEN MATCHED THEN UPDATE SET");
            builder.AppendLine("    [CategoryCode] = source.[CategoryCode],");
            builder.AppendLine("    [Name] = source.[Name],");
            builder.AppendLine("    [Description] = source.[Description],");
            builder.AppendLine("    [Price] = source.[Price],");
            // Stock 是商城營運資料；重跑樣本匯入不得覆蓋銷售後庫存或撞 ReservedStock CHECK。
            builder.AppendLine("    [PrimaryImagePath] = source.[PrimaryImagePath],");
            builder.AppendLine("    [SourceUrl] = source.[SourceUrl],");
            builder.AppendLine("    [IsActive] = source.[IsActive],");
            builder.AppendLine("    [UpdatedAt] = SYSUTCDATETIME()");
            builder.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
            builder.AppendLine("    INSERT ([Id], [CategoryCode], [ExternalRef], [Name], [Description], [Price], [Stock], [PrimaryImagePath], [SourceUrl], [IsActive])");
            builder.AppendLine("    VALUES (source.[Id], source.[CategoryCode], source.[ExternalRef], source.[Name], source.[Description], source.[Price], source.[Stock], source.[PrimaryImagePath], source.[SourceUrl], source.[IsActive]);");
            builder.AppendLine();
        }
        builder.AppendLine("COMMIT TRANSACTION;");
        return builder.ToString();
    }

    private static string String(string value) => "N'" + (value ?? "").Replace("'", "''") + "'";

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("qingming-store-import:" + value.Trim()));
        var guidBytes = bytes[..16];
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}

static class JsonDefaults
{
    public static readonly JsonSerializerOptions Settings = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
