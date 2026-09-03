using System.Globalization;
using System.Text.Json;

namespace QMAH.Infrastructure.CatalogImport;

public sealed record CatalogImportRequest(
    IReadOnlyList<CatalogArtifactImportRow> Artifacts,
    IReadOnlyList<CatalogProductImportRow> Products,
    string WebRootPath,
    string MediaRootPath,
    bool SyncShop,
    int MaxArtifactsPerCategory = 0,
    int MaxProducts = 0,
    bool RequireCompleteProfile = false,
    bool GenerateProductsFromArtifacts = false,
    bool SyncQuestionBank = true);

public sealed record CatalogArtifactImportRow(
    Guid Id,
    string ArtifactRef,
    string Name,
    string CategoryCode,
    string? CategoryName,
    string EraBucketCode,
    string? EraTextOriginal,
    string? DescriptionOriginal,
    string SourceUrl,
    string ImageUrl,
    string? SourcePayloadJson,
    string NormalizationStatus,
    bool QuestionEnabled,
    string? AttributionText,
    int? EraEndYear,
    int? EraStartYear,
    string? LicenseCode,
    string? SizeOriginal,
    string? SourceDataset,
    string ThumbnailUrl);

public sealed record CatalogProductImportRow(
    Guid Id,
    string ExternalRef,
    string Name,
    string CategoryCode,
    string? Description,
    string? SizeText,
    decimal Price,
    int Stock,
    string ImageUrl,
    string? SourceUrl,
    bool IsActive,
    string? ArtifactRef = null);

public sealed record CatalogImportPreview(
    string ApprovalToken,
    int ArtifactCandidateCount,
    int NewArtifactCount,
    int DuplicateArtifactCount,
    int NewQuestionEntryCount,
    int ExistingQuestionEntryCount,
    int ProductCandidateCount,
    int NewProductCount,
    int DuplicateProductCount,
    int InvalidArtifactCount,
    int UnmappedArtifactCount,
    int InvalidProductCount,
    int UnmappedProductCount,
    IReadOnlyList<string> MissingCategories,
    IReadOnlyList<string> Warnings,
    bool SyncShop,
    bool GeneratedProducts,
    bool SyncQuestionBank,
    int UpdatedArtifactCount,
    int UnchangedArtifactCount,
    int UpdatedProductCount,
    int UnchangedProductCount);

public sealed record CatalogImportResult(
    int ArtifactCount,
    int QuestionEntryCount,
    int ProductCount,
    int AssetCount,
    int UpdatedArtifactCount,
    int UpdatedProductCount,
    int UnchangedArtifactCount,
    int UnchangedProductCount);

public static class CatalogImportPackage
{
    private static readonly string[] ArtifactArrayNames =
        ["artifacts", "items", "records", "data", "results"];

    private static readonly string[] ProductArrayNames =
        ["products", "items", "records", "data", "results"];

    public static async Task<(IReadOnlyList<CatalogArtifactImportRow> Artifacts, IReadOnlyList<CatalogProductImportRow> Products)> LoadAsync(
        Stream artifactsStream,
        Stream? productsStream,
        CancellationToken cancellationToken = default)
    {
        var artifacts = await DeserializeArtifactsAsync(artifactsStream, cancellationToken);
        var products = productsStream is null
            ? []
            : await DeserializeProductsAsync(productsStream, cancellationToken);

        return (artifacts, products);
    }

    public static async Task<(IReadOnlyList<CatalogArtifactImportRow> Artifacts, IReadOnlyList<CatalogProductImportRow> Products)> LoadFilesAsync(
        string artifactsPath,
        string? productsPath,
        CancellationToken cancellationToken = default)
    {
        await using var artifacts = File.OpenRead(artifactsPath);
        await using var products = string.IsNullOrWhiteSpace(productsPath)
            ? null
            : File.OpenRead(productsPath);
        return await LoadAsync(artifacts, products, cancellationToken);
    }

    private static async Task<IReadOnlyList<CatalogArtifactImportRow>> DeserializeArtifactsAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryFindRows(document.RootElement, ArtifactArrayNames, IsArtifactRow, out var rows))
            throw new InvalidDataException(
                "文物資料包必須是 JSON 陣列，或包含 artifacts、items、records、data 或 results 陣列的 JSON 文件。");

        return rows.Select(ParseArtifact).ToList();
    }

    private static async Task<IReadOnlyList<CatalogProductImportRow>> DeserializeProductsAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!TryFindRows(document.RootElement, ProductArrayNames, IsProductRow, out var rows))
            throw new InvalidDataException(
                "商品資料包必須是 JSON 陣列，或包含 products、items、records、data 或 results 陣列的 JSON 文件。");

        return rows.Select(ParseProduct).ToList();
    }

    private static bool TryFindRows(
        JsonElement element,
        IReadOnlyList<string> arrayNames,
        Func<JsonElement, bool> isSingleRow,
        out List<JsonElement> rows,
        int depth = 0)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            rows = element.EnumerateArray().ToList();
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object && depth < 3)
        {
            foreach (var name in arrayNames)
            {
                if (TryGetPropertyIgnoreCase(element, name, out var child)
                    && TryFindRows(child, arrayNames, isSingleRow, out rows, depth + 1))
                {
                    return true;
                }
            }

            if (isSingleRow(element))
            {
                rows = [element];
                return true;
            }
        }

        rows = [];
        return false;
    }

    private static CatalogArtifactImportRow ParseArtifact(JsonElement row)
    {
        var normalizationStatus = ReadString(
            row,
            "NormalizationStatus",
            "Normalization.Status",
            "Quality.NormalizationStatus");
        normalizationStatus = normalizationStatus.Trim();
        if (string.IsNullOrWhiteSpace(normalizationStatus))
        {
            if (ReadBoolean(row, false, "RequiresReview", "Normalization.RequiresReview"))
                normalizationStatus = "REVIEW_REQUIRED";
            else if (string.Equals(
                         ReadString(row, "Confidence", "Normalization.Confidence", "Quality.Confidence"),
                         "HIGH",
                         StringComparison.OrdinalIgnoreCase))
                normalizationStatus = "AUTO_VERIFIED";
            else
                normalizationStatus = "REVIEW_REQUIRED";
        }

        var imageUrl = ReadString(
            row,
            "ImageUrl",
            "PrimaryImagePath",
            "PrimaryImageUrl",
            "ImagePath",
            "Media.DisplayPath");
        var thumbnailUrl = ReadString(
            row,
            "ThumbnailUrl",
            "ThumbnailPath",
            "ThumbnailImageUrl",
            "ImageUrlS",
            "Media.ThumbnailPath");

        return new CatalogArtifactImportRow(
            ReadGuid(row, "Id", "ArtifactId"),
            ReadString(row, "ArtifactRef", "Identifier", "SourceIdentifier", "ObjectIdentifier", "Code"),
            ReadString(row, "Name", "Title", "ObjectName"),
            ReadString(row, "CategoryCode", "DatasetCode", "ArtifactCategoryCode"),
            ReadNullableString(row, "CategoryName", "Category", "DatasetName"),
            ReadString(row, "EraBucketCode", "EraCode", "NormalizedEraBucketCode", "Normalization.Bucket"),
            ReadNullableString(row, "EraTextOriginal", "EraText", "Era", "DateText"),
            ReadNullableString(row, "DescriptionOriginal", "Description", "Desc", "Content"),
            ReadString(row, "SourceUrl", "SourceLink", "Url"),
            imageUrl,
            ReadNullableString(row, "SourcePayloadJson", "RawJson", "SourcePayload") ?? row.GetRawText(),
            normalizationStatus,
            ReadBoolean(row, true, "QuestionEnabled", "IsQuestionEnabled", "IncludeInQuestionBank"),
            ReadNullableString(row, "AttributionText", "Attribution", "Credit"),
            ReadNullableInt(row, "EraEndYear", "EndYear", "Normalization.EndYear"),
            ReadNullableInt(row, "EraStartYear", "StartYear", "Normalization.StartYear"),
            ReadNullableString(row, "LicenseCode", "License", "LicenseType"),
            ReadNullableString(row, "SizeOriginal", "SizeText", "Size", "Dimensions"),
            ReadNullableString(row, "SourceDataset", "Dataset", "DatasetCode"),
            thumbnailUrl);
    }

    private static CatalogProductImportRow ParseProduct(JsonElement row) =>
        new(
            ReadGuid(row, "Id", "ProductId"),
            ReadString(row, "ExternalRef", "ProductRef", "ProductCode", "Sku", "SkuCode", "Code"),
            ReadString(row, "Name", "ProductName", "Title"),
            ReadString(row, "CategoryCode", "ArtifactCategoryCode", "Category"),
            ReadNullableString(row, "Description", "DescriptionOriginal", "Desc", "Content"),
            ReadNullableString(row, "SizeText", "Size", "Dimensions"),
            ReadDecimal(row, -1m, "Price", "UnitPrice", "Amount"),
            ReadInt(row, -1, "Stock", "Inventory", "Quantity"),
            ReadString(row, "ImageUrl", "PrimaryImagePath", "PrimaryImageUrl", "ImagePath"),
            ReadNullableString(row, "SourceUrl", "SourceLink", "Url"),
            ReadBoolean(row, true, "IsActive", "Active", "Enabled"),
            ReadNullableString(row, "ArtifactRef", "ArtifactCode", "ArtifactIdentifier"));

    private static bool IsArtifactRow(JsonElement row) =>
        HasAnyProperty(row, "ArtifactRef", "Identifier", "SourceIdentifier", "ObjectIdentifier")
        && HasAnyProperty(row, "Name", "Title", "ObjectName");

    private static bool IsProductRow(JsonElement row) =>
        HasAnyProperty(row, "ExternalRef", "ProductRef", "ProductCode", "Sku", "SkuCode")
        && HasAnyProperty(row, "Name", "ProductName", "Title");

    private static bool HasAnyProperty(JsonElement element, params string[] propertyNames) =>
        propertyNames.Any(name => TryGetPropertyIgnoreCase(element, name, out _));

    private static string ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => ""
            };
        }

        return "";
    }

    private static string? ReadNullableString(JsonElement element, params string[] propertyNames)
    {
        var value = ReadString(element, propertyNames);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBoolean(JsonElement element, bool fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number != 0;
            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
            if (value.ValueKind == JsonValueKind.String
                && value.GetString() is "1" or "0")
                return value.GetString() == "1";
        }

        return fallback;
    }

    private static Guid ReadGuid(JsonElement element, params string[] propertyNames)
    {
        var value = ReadNullableString(element, propertyNames);
        if (string.IsNullOrWhiteSpace(value))
            return Guid.Empty;
        if (Guid.TryParse(value, out var id))
            return id;

        throw new InvalidDataException("資料包中的識別碼格式無效；請確認 Id 欄位是 GUID，或移除後由匯入器產生穩定識別碼。");
    }

    private static int ReadInt(JsonElement element, int fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return fallback;
    }

    private static int? ReadNullableInt(JsonElement element, params string[] propertyNames)
    {
        var value = ReadInt(element, int.MinValue, propertyNames);
        return value == int.MinValue ? null : value;
    }

    private static decimal ReadDecimal(JsonElement element, decimal fallback, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return fallback;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyPath,
        out JsonElement value)
    {
        var current = element;
        foreach (var segment in propertyPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            var found = false;
            foreach (var property in current.EnumerateObject())
            {
                if (!string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                    continue;

                current = property.Value;
                found = true;
                break;
            }

            if (!found)
            {
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }
}
