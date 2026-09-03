using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

static partial class ArtifactPipeline
{
    private static async Task<int> EstimateAsync(
        string apiRoot,
        PipelineOptions options,
        CancellationToken cancellationToken)
    {
        using var http = CreateSourceHttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Qingming-NpmArtifactPipeline", "2.0"));
        var estimates = new List<(string Code, int? Count)>();
        var failed = false;
        Console.WriteLine("MODE estimate; no output files or images will be written.");
        foreach (var dataset in options.Datasets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var rawJson = await http.GetStringAsync($"{apiRoot}/{dataset.ApiName}.json", cancellationToken);
                var rows = JsonSerializer.Deserialize<List<NpmSourceRow>>(rawJson, JsonDefaults.Source) ?? [];
                var questionReady = rows.Count(IsQuestionReadySource);
                Console.WriteLine($"ESTIMATE|{dataset.Code}|available={rows.Count}|question-ready={questionReady}|api={dataset.ApiName}");
                estimates.Add((dataset.Code, rows.Count));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = true;
                Console.Error.WriteLine($"ESTIMATE_FAILED|{dataset.Code}|{ex.Message}");
                estimates.Add((dataset.Code, null));
            }
        }

        var total = estimates.Where(x => x.Count.HasValue).Sum(x => x.Count!.Value);
        var summary = string.Join('|', estimates.Select(x => $"{x.Code}={(x.Count?.ToString(CultureInfo.InvariantCulture) ?? "error")}"));
        Console.WriteLine($"ESTIMATE_SUMMARY|ARTIFACT|total={total}|{summary}");
        return failed ? 1 : 0;
    }
}
