using System.Text.Json;
using System.Text.Json.Serialization;

namespace QMAH.Infrastructure.CatalogImport;

/// <summary>
/// 只負責讀取國立故宮博物院 Open Data 的外部格式；正規化與匯入仍由工具輸出及 CatalogImportService 負責。
/// </summary>
public sealed class NpmOpenDataClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyDictionary<string, string> SupportedDatasets { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bronzes"] = "BRONZE",
            ["ceramics"] = "CERAMIC",
            ["jades"] = "JADE",
            ["enamelWares"] = "ENAMEL",
            ["lacquerWares"] = "LACQUER",
            ["coins"] = "COIN",
            ["carvings"] = "CARVING",
            ["paintings"] = "PAINTING"
        };

    public static IReadOnlyDictionary<string, string> DatasetDisplayNames { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bronzes"] = "銅器",
            ["ceramics"] = "陶瓷",
            ["jades"] = "玉器",
            ["enamelWares"] = "琺瑯器",
            ["lacquerWares"] = "漆器",
            ["coins"] = "錢幣",
            ["carvings"] = "雕刻",
            ["paintings"] = "繪畫"
        };

    public static string GetDatasetDisplayName(string dataset) =>
        DatasetDisplayNames.TryGetValue(dataset, out var name) ? name : "未分類";

    public async Task<IReadOnlyList<NpmExternalArtifactDto>> GetDatasetAsync(
        string dataset,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedDatasets.ContainsKey(dataset))
            throw new ArgumentException("不支援的故宮資料集。", nameof(dataset));

        using var response = await httpClient.GetAsync(
            dataset + ".json",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<NpmExternalArtifactDto>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }
}

/// <summary>
/// 故宮 API 的外部 DTO。不可直接拿來當 EF Entity 或 API 回應 Entity。
/// </summary>
public sealed record NpmExternalArtifactDto(
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("era")] string? Era,
    [property: JsonPropertyName("desc")] string? Description,
    [property: JsonPropertyName("url")] string? SourceUrl,
    [property: JsonPropertyName("imageUrl_s")] string? SmallImageUrl,
    [property: JsonPropertyName("imageUrl_m")] string? MediumImageUrl);
