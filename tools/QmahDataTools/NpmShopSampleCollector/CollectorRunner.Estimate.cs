using System.Net;
using System.Net.Http.Headers;

sealed partial class CollectorRunner
{
    private async Task<int> EstimateAsync(IReadOnlyList<CategoryBucket> buckets)
    {
        var settings = _runtime.Settings;
        using var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = settings.MaxConcurrentRequests,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
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

        Console.WriteLine("MODE|estimate|只讀取分類頁，不下載商品頁與圖片，不寫入 output");
        await DiscoverCandidatesAsync(buckets, fetcher);
        var selected = buckets.Where(bucket => SettingsRules.IsAllowedCategory(settings, bucket)).ToList();
        var summary = selected.Select(bucket => $"{bucket.Code}={bucket.DiscoveredCount}").ToArray();
        var total = selected.Sum(bucket => bucket.DiscoveredCount);
        Console.WriteLine($"ESTIMATE_SUMMARY|SHOP|total={total}|{string.Join('|', summary)}");
        return 0;
    }
}
