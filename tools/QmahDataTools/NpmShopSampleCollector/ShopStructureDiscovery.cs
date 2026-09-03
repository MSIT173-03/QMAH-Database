using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed partial class CollectorRunner
{
    private async Task<int> DiscoverWebsiteStructureAsync()
    {
        var settings = _runtime.Settings;
        var observedAtUtc = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var pages = new List<StructurePageObservation>();
        var categories = new Dictionary<string, StructureCategoryObservation>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("MODE|structure-discovery|只讀少量頁面，不下載商品頁、圖片或建立 output");
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = Math.Min(settings.MaxConcurrentRequests, 4),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)
            };
            http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("Qingming-NpmShopStructureProbe", ShopCollectorConstants.CollectorVersion));
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-TW,zh;q=0.9,en;q=0.5");
            var throttle = new RequestThrottle(
                settings.ThrottleMilliseconds,
                settings.CooldownEveryRequests,
                settings.CooldownMilliseconds,
                settings.JitterMilliseconds);
            var robots = new RobotsChecker(http, throttle, settings, warnings);
            var fetcher = new HttpFetcher(http, throttle, robots, settings.MaxRetries, settings.RetryBaseDelayMilliseconds);

            var targets = new List<(string Code, string Name, string Url)>();
            if (!string.IsNullOrWhiteSpace(settings.SourceRoot))
                targets.Add(("ROOT", "商城根頁", settings.SourceRoot.EndsWith("/", StringComparison.Ordinal) ? settings.SourceRoot : settings.SourceRoot + "/"));
            targets.AddRange(settings.SourceEntries
                .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Url))
                .Take(24)
                .Select(x => (x.Code, x.Name, UrlBuilder.BuildPageUrl(x.Url, 1))));

            foreach (var target in targets)
            {
                Console.WriteLine($"STRUCTURE_FETCH|source={target.Code}|url={target.Url}");
                try
                {
                    var html = await fetcher.GetStringAsync(target.Url);
                    var productCount = HtmlParser.ExtractProductLinks(html, target.Url).Count;
                    var categoryLinks = HtmlParser.ExtractCategoryLinks(html, target.Url);
                    var pagination = Regex.IsMatch(html, @"(?:[?&]|&)pn=2\b|(?:下一頁|next|page\s*2)", RegexOptions.IgnoreCase);
                    pages.Add(new StructurePageObservation(
                        target.Code,
                        target.Url,
                        html.Length,
                        categoryLinks.Count,
                        productCount,
                        pagination,
                        Regex.Matches(html, "<img\\b", RegexOptions.IgnoreCase).Count));

                    foreach (var link in categoryLinks)
                    {
                        var mapped = InferCategoryCode(link.Name, settings);
                        if (!categories.TryGetValue(link.Code, out var existing))
                        {
                            categories[link.Code] = new StructureCategoryObservation(
                                link.Code,
                                link.Name,
                                link.UrlTemplate,
                                mapped,
                                productCount,
                                target.Code,
                                "observed");
                        }
                        else
                        {
                            categories[link.Code] = existing with
                            {
                                Name = string.IsNullOrWhiteSpace(existing.Name) ? link.Name : existing.Name,
                                UrlTemplate = existing.UrlTemplate.Length == 0 ? link.UrlTemplate : existing.UrlTemplate,
                                ObservedProductLinks = Math.Max(existing.ObservedProductLinks, productCount),
                                EvidenceSource = existing.EvidenceSource + "," + target.Code
                            };
                        }
                    }

                    Console.WriteLine($"STRUCTURE_PAGE|source={target.Code}|categories={categoryLinks.Count}|products={productCount}|pagination={pagination}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = ShortError(ex);
                    warnings.Add($"FETCH_FAILED:{target.Code}:{message}");
                    Console.Error.WriteLine($"STRUCTURE_FAILED|source={target.Code}|error={message}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add("PROBE_SETUP_FAILED:" + ShortError(ex));
            Console.Error.WriteLine($"STRUCTURE_FAILED|setup|error={ShortError(ex)}");
        }

        // 保留目前設定檔中的入口，讓網站暫時拒絕偵測時 JSON 仍有可追溯的今日結構快照。
        var configuredEntries = settings.SourceEntries
            .Where(x => !string.IsNullOrWhiteSpace(x.Code) && !string.IsNullOrWhiteSpace(x.Url))
            .Select(x =>
            {
                var code = x.Code.Trim().ToUpperInvariant();
                if (!categories.ContainsKey(code))
                {
                    categories[code] = new StructureCategoryObservation(
                        code,
                        x.Name.Trim(),
                        UrlBuilder.BuildPageUrl(x.Url, 1).Replace("pn=1", "pn={page}", StringComparison.OrdinalIgnoreCase),
                        x.CategoryCode,
                        0,
                        "settings",
                        "heuristic");
                }
                return new
                {
                    code,
                    name = x.Name,
                    url = x.Url,
                    mappedCategoryCode = x.CategoryCode,
                    enabled = x.Enabled,
                    observed = categories[code].Confidence == "observed"
                };
            })
            .ToArray();

        var catalog = new
        {
            schemaVersion = 1,
            catalogType = "heuristic-observation",
            observedAtUtc,
            sourceRoot = settings.SourceRoot,
            confidence = "heuristic",
            warning = "這是少量頁面的觀察與推測，不代表官方分類 API；正式收集前請人工檢查，再決定是否加入 sample-settings.json。",
            discoveryPolicy = new
            {
                maxConfiguredEntries = 24,
                pagesPerEntry = 1,
                productDetailsFetched = false,
                imagesDownloaded = false,
                outputWritten = false,
                robotsTxtRespected = settings.RespectRobotsTxt
            },
            listingStructure = new
            {
                categoryPagePattern = "PrdList.php?sn=shop&cn={category}&pn={page}",
                categoryParameter = "cn",
                pageParameter = "pn",
                categoryLinkSelector = "a/span[href*='PrdList.php']",
                productLinkSelector = "a/span[href*='PrdInfo.php']",
                productReferenceParameter = "prd",
                paginationObserved = pages.Any(x => x.PaginationObserved),
                productLinksObserved = pages.Sum(x => x.ProductLinkCount) > 0,
                imageTagsObserved = pages.Sum(x => x.ImageTagCount) > 0,
                evidencePageCount = pages.Count
            },
            categories = categories.Values
                .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            configuredSourceEntries = configuredEntries,
            evidencePages = pages,
            warnings
        };

        var catalogPath = Path.GetFullPath(_runtime.SourceCatalogPath);
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        await File.WriteAllTextAsync(
            catalogPath,
            JsonSerializer.Serialize(catalog, JsonDefaults.Pretty),
            new UTF8Encoding(false));

        Console.WriteLine($"STRUCTURE_SUMMARY|categories={categories.Count}|pages={pages.Count}|catalog={catalogPath}");
        return 0;
    }

    private static string InferCategoryCode(string name, CollectorSettings settings)
        => SourceCatalogLoader.ResolveMappedCategory(settings, null, name);
}

static partial class HtmlParser
{
    private static readonly Regex CategoryLinkRegex = new(
        "<(?:a|span)\\b[^>]*?href\\s*=\\s*(?:\\\"(?<url>[^\\\"]+)\\\"|'(?<url>[^']+)'|(?<url>[^\\s>]+))[^>]*>(?<text>[\\s\\S]*?)</(?:a|span)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<CategoryPageLink> ExtractCategoryLinks(string html, string pageUrl)
    {
        var result = new Dictionary<string, CategoryPageLink>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in CategoryLinkRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            if (!href.Contains("PrdList.php", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(new Uri(pageUrl), href, out var absolute))
                continue;
            var code = ExtractQuery(absolute.ToString(), "cn").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code))
                continue;
            var template = Regex.Replace(absolute.ToString(), @"([?&])pn=\d+", "$1pn={page}", RegexOptions.IgnoreCase);
            if (!template.Contains("{page}", StringComparison.OrdinalIgnoreCase))
                template += (template.Contains('?') ? "&" : "?") + "pn={page}";
            var name = Text.Clean(WebUtility.HtmlDecode(match.Groups["text"].Value));
            if (string.IsNullOrWhiteSpace(name)) name = code;
            result[code] = new CategoryPageLink(code, name, template);
        }
        return result.Values.ToArray();
    }
}

sealed record CategoryPageLink(string Code, string Name, string UrlTemplate);

sealed record StructureCategoryObservation(
    string Code,
    string Name,
    string UrlTemplate,
    string MappedCategoryCode,
    int ObservedProductLinks,
    string EvidenceSource,
    string Confidence);

sealed record StructurePageObservation(
    string SourceCode,
    string Url,
    int HtmlLength,
    int CategoryLinkCount,
    int ProductLinkCount,
    bool PaginationObserved,
    int ImageTagCount);
