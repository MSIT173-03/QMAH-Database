using System.Text.Json;

static class SourceCatalogLoader
{
    public static void MergeSelectedEntries(CollectorSettings settings, string catalogPath)
    {
        if (settings.AllowedSourceCategories.Count == 0 || !File.Exists(catalogPath))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (!document.RootElement.TryGetProperty("categories", out var categories)
                || categories.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in categories.EnumerateArray())
            {
                var code = GetString(item, "code");
                var name = GetString(item, "name");
                var url = GetString(item, "urlTemplate") ?? GetString(item, "url");
                if (string.IsNullOrWhiteSpace(code)
                    || string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(url)
                    || !settings.AllowedSourceCategories.Any(value =>
                        string.Equals(value, code, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(value, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (settings.SourceEntries.Any(entry =>
                        string.Equals(entry.Code, code, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var mapped = GetString(item, "mappedCategoryCode");
                mapped = ResolveMappedCategory(settings, mapped, name);
                settings.SourceEntries.Add(new SourceEntrySetting
                {
                    Code = code.Trim().ToUpperInvariant(),
                    Name = name.Trim(),
                    Url = url.Trim(),
                    CategoryCode = mapped,
                    Enabled = true
                });
            }
        }
        catch
        {
            // JSON 是可更新的觀察快照；失敗時保留正式 settings，不阻止既有分類收集。
        }
    }

    public static string ResolveMappedCategory(
        CollectorSettings settings,
        string? mappedCategoryCode,
        string name)
    {
        var direct = settings.Categories.FirstOrDefault(category =>
            category.Enabled
            && string.Equals(category.Code, mappedCategoryCode, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct.Code;

        var keywordMatch = settings.Categories.FirstOrDefault(category =>
            category.Enabled
            && category.Keywords.Any(keyword => !string.IsNullOrWhiteSpace(keyword)
                                                && name.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        if (keywordMatch is not null)
            return keywordMatch.Code;

        var normalizedName = name.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        if (normalizedName.Contains("書法", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("繪畫", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("清明", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("富春", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("快雪", StringComparison.OrdinalIgnoreCase))
            return FindCategory(settings, "PAINTING_REPRODUCTION");
        if (normalizedName.Contains("陶瓷", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("餐瓷", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("茶器", StringComparison.OrdinalIgnoreCase))
            return FindCategory(settings, "TABLEWARE");
        if (normalizedName.Contains("文具", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("文房", StringComparison.OrdinalIgnoreCase))
            return FindCategory(settings, "STATIONERY");
        if (normalizedName.Contains("多寶格", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("家飾", StringComparison.OrdinalIgnoreCase))
            return FindCategory(settings, "HOME_DECOR");
        if (normalizedName.Contains("玩具", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("拼圖", StringComparison.OrdinalIgnoreCase))
            return FindCategory(settings, "TOYS_PUZZLES");
        if (normalizedName.Contains("服飾", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("配件", StringComparison.OrdinalIgnoreCase))
            return FindCategory(settings, "APPAREL_ACCESSORIES");
        if (normalizedName.Contains("玉", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("肉形", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("毛公", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("珍玩", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("翠玉", StringComparison.OrdinalIgnoreCase))
            return FindCategory(settings, "OTHER_CULTURAL_DERIVATIVE");

        return FindCategory(settings, "COLLECTION_DERIVATIVE");
    }

    private static string FindCategory(CollectorSettings settings, string preferredCode) =>
        settings.Categories.FirstOrDefault(category =>
            category.Enabled
            && string.Equals(category.Code, preferredCode, StringComparison.OrdinalIgnoreCase))?.Code
        ?? settings.Categories.FirstOrDefault(category => category.Enabled)?.Code
        ?? preferredCode;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
