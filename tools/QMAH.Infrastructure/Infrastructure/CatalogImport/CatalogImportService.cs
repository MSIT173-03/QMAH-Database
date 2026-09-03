using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QMAH.Infrastructure.CatalogImport;

public sealed class CatalogImportService(QmahDbContext db)
{
    public static readonly IReadOnlyList<string> SupportedCategoryCodes =
    ["BRONZE", "CERAMIC", "JADE", "ENAMEL", "LACQUER", "COIN", "CARVING", "PAINTING"];

    // 預檢與正式匯入共用同一份計畫，確認碼會綁定資料內容與同步選項
    public async Task<CatalogImportPreview> PreviewAsync(
        CatalogImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareAsync(request, cancellationToken);
        return plan.ToPreview(request.SyncShop, request.SyncQuestionBank);
    }

    // 正式匯入先驗證預檢確認碼，再一起處理檔案與資料庫
    public async Task<CatalogImportResult> ImportAsync(
        CatalogImportRequest request,
        string approvalToken,
        CancellationToken cancellationToken = default)
    {
        var plan = await PrepareAsync(request, cancellationToken);
        if (plan.MissingCategories.Count > 0 && request.RequireCompleteProfile)
        {
            throw new InvalidDataException(
                $"資料包未達固定 8 類匯入門檻：{string.Join(",", plan.MissingCategories)}");
        }

        if (request.SyncShop
            && request.MaxProducts > 0
            && plan.Products.Count < request.MaxProducts
            && request.RequireCompleteProfile)
        {
            throw new InvalidDataException(
                $"商品資料不足，預期 {request.MaxProducts} 筆，實際只有 {plan.Products.Count} 筆。");
        }

        var expectedToken = plan.ApprovalToken;
        var providedToken = approvalToken.Trim().ToUpperInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedToken),
                Encoding.ASCII.GetBytes(providedToken)))
        {
            throw new InvalidDataException("確認碼不符或資料包已變更；請重新執行預檢。");
        }

        var assets = CreateAssetPlans(request, plan);
        ValidateAssetTargets(request.WebRootPath, assets);
        var copiedAssets = new List<string>();
        var committed = false;

        // 檔案不屬於資料庫交易，失敗時要刪掉已複製檔案，避免留下孤兒檔
        try
        {
            CopyAssets(request.MediaRootPath, assets, copiedAssets);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var categories = await EnsureCategoriesAsync(plan.Artifacts, cancellationToken);
            var eras = await EnsureErasAsync(plan.Artifacts, cancellationToken);
            foreach (var artifact in plan.NewArtifacts)
            {
                db.Artifacts.Add(new Artifact
                {
                    Id = plan.ArtifactIds[artifact.ArtifactRef],
                    ArtifactRef = artifact.ArtifactRef.Trim(),
                    Name = artifact.Name.Trim(),
                    CategoryId = categories[NormalizeCode(artifact.CategoryCode)],
                    EraBucketId = eras[NormalizeCode(artifact.EraBucketCode)],
                    EraTextOriginal = NullIfWhiteSpace(artifact.EraTextOriginal),
                    Description = NullIfWhiteSpace(artifact.DescriptionOriginal),
                    SizeText = string.IsNullOrWhiteSpace(artifact.SizeOriginal)
                        ? "官方資料未提供"
                        : artifact.SizeOriginal.Trim(),
                    PrimaryImagePath = PublicAssetPath("catalog", artifact.CategoryCode, artifact.ArtifactRef, "display.jpg"),
                    ThumbnailPath = PublicAssetPath("catalog", artifact.CategoryCode, artifact.ArtifactRef, "thumbnail.jpg"),
                    SourceUrl = artifact.SourceUrl.Trim(),
                    LicenseCode = NullIfWhiteSpace(artifact.LicenseCode),
                    AttributionText = NullIfWhiteSpace(artifact.AttributionText),
                    IsActive = true
                });
            }

            if (plan.UpdatedArtifacts.Count > 0)
            {
                var artifactRefsToUpdate = plan.UpdatedArtifacts
                    .Select(artifact => artifact.ArtifactRef)
                    .ToArray();
                var existingArtifacts = await db.Artifacts
                    .Where(artifact => artifactRefsToUpdate.Contains(artifact.ArtifactRef))
                    .ToDictionaryAsync(artifact => artifact.ArtifactRef, StringComparer.OrdinalIgnoreCase, cancellationToken);
                foreach (var artifact in plan.UpdatedArtifacts)
                {
                    if (!existingArtifacts.TryGetValue(artifact.ArtifactRef, out var entity))
                        throw new InvalidDataException($"找不到要更新的文物：{artifact.ArtifactRef}");

                    entity.Name = artifact.Name.Trim();
                    entity.CategoryId = categories[NormalizeCode(artifact.CategoryCode)];
                    entity.EraBucketId = eras[NormalizeCode(artifact.EraBucketCode)];
                    entity.EraTextOriginal = NullIfWhiteSpace(artifact.EraTextOriginal);
                    entity.Description = NullIfWhiteSpace(artifact.DescriptionOriginal);
                    entity.SizeText = string.IsNullOrWhiteSpace(artifact.SizeOriginal)
                        ? "官方資料未提供"
                        : artifact.SizeOriginal.Trim();
                    entity.SourceUrl = artifact.SourceUrl.Trim();
                    entity.LicenseCode = NullIfWhiteSpace(artifact.LicenseCode);
                    entity.AttributionText = NullIfWhiteSpace(artifact.AttributionText);
                }
            }

            var now = DateTime.UtcNow;
            foreach (var artifactId in plan.NewQuestionArtifactIds)
            {
                db.ArtifactQuestionEntries.Add(new ArtifactQuestionEntry
                {
                    Id = StableGuid($"question:{artifactId:D}"),
                    ArtifactId = artifactId,
                    IsEnabled = true,
                    Difficulty = 1,
                    QuestionTemplateCode = "GENERAL",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            foreach (var product in plan.NewProducts)
            {
                var artifactId = ResolveProductArtifactId(product, plan.ArtifactIds);
                db.Products.Add(new Product
                {
                    Id = product.Id == Guid.Empty ? StableGuid($"product:{product.ExternalRef}") : product.Id,
                    ArtifactId = artifactId,
                    ExternalRef = NullIfWhiteSpace(product.ExternalRef),
                    Name = product.Name.Trim(),
                    CategoryCode = NormalizeCode(product.CategoryCode),
                    Description = NullIfWhiteSpace(product.Description),
                    SizeText = NullIfWhiteSpace(product.SizeText),
                    Price = product.Price,
                    Stock = product.Stock,
                    PrimaryImagePath = PublicAssetPath("store", product.CategoryCode, product.ExternalRef, "image.jpg"),
                    SourceUrl = NullIfWhiteSpace(product.SourceUrl),
                    IsActive = product.IsActive,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            if (plan.UpdatedProducts.Count > 0)
            {
                var productRefsToUpdate = plan.UpdatedProducts
                    .Select(product => product.ExternalRef)
                    .ToArray();
                var existingProducts = await db.Products
                    .Where(product => product.ExternalRef != null
                        && productRefsToUpdate.Contains(product.ExternalRef))
                    .ToDictionaryAsync(product => product.ExternalRef!, StringComparer.OrdinalIgnoreCase, cancellationToken);
                foreach (var product in plan.UpdatedProducts)
                {
                    if (!existingProducts.TryGetValue(product.ExternalRef, out var entity))
                        throw new InvalidDataException($"找不到要更新的商品：{product.ExternalRef}");

                    entity.Name = product.Name.Trim();
                    entity.CategoryCode = NormalizeCode(product.CategoryCode);
                    entity.Description = NullIfWhiteSpace(product.Description);
                    entity.SizeText = NullIfWhiteSpace(product.SizeText);
                    entity.Price = product.Price;
                    entity.SourceUrl = NullIfWhiteSpace(product.SourceUrl);
                    // Stock、IsActive 與圖片屬於後台營運資料；匯入只更新來源欄位，不覆蓋人工調整。
                    entity.UpdatedAt = now;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            return new CatalogImportResult(
                plan.NewArtifacts.Count,
                plan.NewQuestionArtifactIds.Count,
                plan.NewProducts.Count,
                copiedAssets.Count,
                plan.UpdatedArtifacts.Count,
                plan.UpdatedProducts.Count,
                plan.UnchangedArtifactCount,
                plan.UnchangedProductCount);
        }
        finally
        {
            if (!committed)
                DeleteCopiedAssets(copiedAssets);
        }
    }

    private async Task<ImportPlan> PrepareAsync(
        CatalogImportRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var allArtifacts = request.Artifacts ?? [];
        var allProducts = request.SyncShop ? request.Products ?? [] : [];
        var eraDefinitions = LoadEraDefinitions();

        // 文物編號是匯入去重鍵，資料包內重複時直接停止
        var duplicateArtifact = allArtifacts
            .Where(row => !string.IsNullOrWhiteSpace(row.ArtifactRef))
            .GroupBy(row => row.ArtifactRef.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateArtifact is not null)
            throw new InvalidDataException($"ArtifactRef 重複：{duplicateArtifact.Key}");

        var invalidArtifactCount = 0;
        var unmappedArtifactCount = 0;
        var candidates = new List<CatalogArtifactImportRow>();
        foreach (var row in allArtifacts)
        {
            var category = NormalizeCode(row.CategoryCode);
            var era = NormalizeCode(row.EraBucketCode);
            if (!SupportedCategoryCodes.Contains(category, StringComparer.OrdinalIgnoreCase)
                || !eraDefinitions.ContainsKey(era))
            {
                unmappedArtifactCount++;
                continue;
            }

            if (!IsValidArtifact(row))
            {
                invalidArtifactCount++;
                continue;
            }

            candidates.Add(row with
            {
                ArtifactRef = row.ArtifactRef.Trim(),
                CategoryCode = category,
                EraBucketCode = era
            });
        }

        candidates = candidates
            .GroupBy(row => row.CategoryCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => request.MaxArtifactsPerCategory > 0
                ? group.OrderBy(row => row.ArtifactRef, StringComparer.OrdinalIgnoreCase)
                    .Take(request.MaxArtifactsPerCategory)
                : group.OrderBy(row => row.ArtifactRef, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var artifactRefs = candidates.Select(row => row.ArtifactRef).ToArray();
        var existingArtifacts = artifactRefs.Length == 0
            ? []
            : await db.Artifacts
                .AsNoTracking()
                .Where(row => artifactRefs.Contains(row.ArtifactRef))
                .Select(row => new ExistingArtifact(
                    row.ArtifactRef,
                    row.Id,
                    row.Name,
                    row.Category.Code,
                    row.EraBucket.Code,
                    row.EraTextOriginal,
                    row.Description,
                    row.SizeText,
                    row.SourceUrl,
                    row.LicenseCode,
                    row.AttributionText))
                .ToListAsync(cancellationToken);
        var artifactIds = existingArtifacts.ToDictionary(
            row => row.ArtifactRef,
            row => row.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in candidates.Where(row => !artifactIds.ContainsKey(row.ArtifactRef)))
            artifactIds[row.ArtifactRef] = row.Id == Guid.Empty
                ? StableGuid($"artifact:{row.ArtifactRef}")
                : row.Id;

        var newArtifacts = candidates
            .Where(row => !existingArtifacts.Any(old => string.Equals(old.ArtifactRef, row.ArtifactRef, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var updatedArtifacts = candidates
            .Where(row => existingArtifacts.Any(old =>
                string.Equals(old.ArtifactRef, row.ArtifactRef, StringComparison.OrdinalIgnoreCase)
                && HasArtifactChanges(old, row)))
            .ToList();
        var unchangedArtifactCount = existingArtifacts.Count - updatedArtifacts.Count;
        var newArtifactIds = newArtifacts.Select(row => artifactIds[row.ArtifactRef]).ToHashSet();

        // 文物是圖鑑、遊戲與題庫共用的主檔；題庫同步預設開啟，
        // 只有管理員明確取消時才不建立題庫入口。
        var questionArtifactIds = candidates
            .Where(row => request.SyncQuestionBank && row.QuestionEnabled)
            .Select(row => artifactIds[row.ArtifactRef])
            .ToArray();
        var existingQuestionIds = questionArtifactIds.Length == 0
            ? []
            : await db.ArtifactQuestionEntries
                .AsNoTracking()
                .Where(row => questionArtifactIds.Contains(row.ArtifactId))
                .Select(row => row.ArtifactId)
                .ToListAsync(cancellationToken);
        var newQuestionArtifactIds = questionArtifactIds
            .Where(id => !existingQuestionIds.Contains(id))
            .Distinct()
            .ToList();

        var generatedProducts = false;
        var productCandidates = new List<CatalogProductImportRow>();
        if (request.SyncShop)
        {
            if (allProducts.Count == 0 && request.GenerateProductsFromArtifacts)
            {
                generatedProducts = true;
                productCandidates = candidates
                    .Select(CreateGeneratedProduct)
                    .OrderBy(row => row.ExternalRef, StringComparer.OrdinalIgnoreCase)
                    .Take(request.MaxProducts > 0 ? request.MaxProducts : int.MaxValue)
                    .ToList();
            }
            else
            {
                var duplicateProduct = allProducts
                    .Where(row => !string.IsNullOrWhiteSpace(row.ExternalRef))
                    .GroupBy(row => row.ExternalRef.Trim(), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateProduct is not null)
                    throw new InvalidDataException($"ExternalRef 重複：{duplicateProduct.Key}");

                productCandidates = allProducts
                    .OrderBy(row => row.ExternalRef, StringComparer.OrdinalIgnoreCase)
                    .Take(request.MaxProducts > 0 ? request.MaxProducts : int.MaxValue)
                    .ToList();
            }
        }

        var invalidProductCount = 0;
        var unmappedProductCount = 0;
        var normalizedProducts = new List<CatalogProductImportRow>();
        foreach (var product in productCandidates)
        {
            var normalized = product with
            {
                ExternalRef = product.ExternalRef?.Trim() ?? "",
                Name = product.Name?.Trim() ?? "",
                CategoryCode = NormalizeCode(product.CategoryCode),
                ArtifactRef = NormalizeArtifactRef(product)
            };
            if (!SupportedCategoryCodes.Contains(normalized.CategoryCode, StringComparer.OrdinalIgnoreCase))
            {
                unmappedProductCount++;
                continue;
            }

            if (!IsValidProduct(normalized))
            {
                invalidProductCount++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(normalized.ArtifactRef)
                && !artifactIds.ContainsKey(normalized.ArtifactRef))
            {
                var existingArtifact = await db.Artifacts
                    .AsNoTracking()
                    .Where(row => row.ArtifactRef == normalized.ArtifactRef)
                    .Select(row => row.Id)
                    .SingleOrDefaultAsync(cancellationToken);
                if (existingArtifact == Guid.Empty)
                {
                    unmappedProductCount++;
                    continue;
                }
                else
                {
                    artifactIds[normalized.ArtifactRef] = existingArtifact;
                }
            }

            normalizedProducts.Add(normalized);
        }

        var productRefs = normalizedProducts.Select(row => row.ExternalRef).ToArray();
        var existingProducts = productRefs.Length == 0
            ? []
            : await db.Products
                .AsNoTracking()
                .Where(row => row.ExternalRef != null && productRefs.Contains(row.ExternalRef))
                .Select(row => new ExistingProduct(
                    row.ExternalRef!,
                    row.Id,
                    row.ArtifactId,
                    row.CategoryCode,
                    row.Name,
                    row.Description,
                    row.SizeText,
                    row.Price,
                    row.SourceUrl))
                .ToListAsync(cancellationToken);
        var newProducts = normalizedProducts
            .Where(row => !existingProducts.Any(old => string.Equals(old.ExternalRef, row.ExternalRef, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var updatedProducts = normalizedProducts
            .Where(row => existingProducts.Any(old =>
                string.Equals(old.ExternalRef, row.ExternalRef, StringComparison.OrdinalIgnoreCase)
                && HasProductChanges(old, row)))
            .ToList();
        var unchangedProductCount = existingProducts.Count - updatedProducts.Count;

        var linkedArtifactIds = newProducts
            .Select(row => ResolveProductArtifactId(row, artifactIds))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (linkedArtifactIds.Length > 0)
        {
            var occupiedArtifactIds = await db.Products
                .AsNoTracking()
                .Where(row => row.ArtifactId.HasValue && linkedArtifactIds.Contains(row.ArtifactId.Value))
                .Select(row => row.ArtifactId!.Value)
                .ToListAsync(cancellationToken);
            var conflict = newProducts.FirstOrDefault(row =>
                ResolveProductArtifactId(row, artifactIds) is { } id && occupiedArtifactIds.Contains(id));
            if (conflict is not null)
            {
                throw new InvalidDataException(
                    $"商品 {conflict.ExternalRef} 對應的文物已經有商城商品，為避免一對一關聯被破壞，停止匯入。");
            }
        }

        var missingCategories = SupportedCategoryCodes
            .Where(code => candidates.Count(row => string.Equals(row.CategoryCode, code, StringComparison.OrdinalIgnoreCase))
                < (request.MaxArtifactsPerCategory > 0 ? request.MaxArtifactsPerCategory : 1))
            .ToList();
        var warnings = new List<string>();
        if (invalidArtifactCount > 0)
            warnings.Add($"有 {invalidArtifactCount} 筆文物因欄位或品質條件不符而排除。");
        if (unmappedArtifactCount > 0)
            warnings.Add($"有 {unmappedArtifactCount} 筆文物因分類或年代桶未對應而排除。");
        if (unmappedProductCount > 0)
            warnings.Add($"有 {unmappedProductCount} 筆商品的分類或文物對應不完整，已排除於本次匯入。");
        if (invalidProductCount > 0)
            warnings.Add($"有 {invalidProductCount} 筆商品因欄位不完整或數值不合法而排除。");
        if (generatedProducts)
            warnings.Add("未提供商品資料，已依新增文物產生可停用的商城展示商品；不會覆蓋既有商品。");
        if (updatedArtifacts.Count > 0)
            warnings.Add($"已有 {updatedArtifacts.Count} 件文物的來源欄位變更，正式匯入時會更新來源資料，不覆蓋圖片與人工啟用狀態。");
        if (updatedProducts.Count > 0)
            warnings.Add($"已有 {updatedProducts.Count} 件商品的來源欄位變更，正式匯入時會更新文案、分類、價格與來源，不覆蓋庫存、圖片與人工上架狀態。");

        var approvalToken = BuildApprovalToken(
            candidates,
            normalizedProducts,
            request,
            generatedProducts);

        return new ImportPlan(
            candidates,
            newArtifacts,
            updatedArtifacts,
            artifactIds,
            newQuestionArtifactIds,
            normalizedProducts,
            newProducts,
            updatedProducts,
            missingCategories,
            warnings,
            approvalToken,
            invalidArtifactCount,
            unmappedArtifactCount,
            invalidProductCount,
            unmappedProductCount,
            existingArtifacts.Count,
            existingQuestionIds.Count,
            existingProducts.Count,
            unchangedArtifactCount,
            unchangedProductCount,
            generatedProducts);
    }

    private async Task<Dictionary<string, Guid>> EnsureCategoriesAsync(
        IReadOnlyList<CatalogArtifactImportRow> artifacts,
        CancellationToken cancellationToken)
    {
        var groups = artifacts
            .GroupBy(row => NormalizeCode(row.CategoryCode), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in groups)
        {
            if (!await db.ArtifactCategories.AnyAsync(
                    category => category.Code == group.Key,
                    cancellationToken))
            {
                db.ArtifactCategories.Add(new ArtifactCategory
                {
                    Id = StableGuid($"category:{group.Key}"),
                    Code = group.Key,
                    Name = string.IsNullOrWhiteSpace(group.First().CategoryName)
                        ? group.Key
                        : group.First().CategoryName!.Trim()
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await db.ArtifactCategories
            .AsNoTracking()
            .ToDictionaryAsync(row => row.Code, row => row.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
    }

    private async Task<Dictionary<string, Guid>> EnsureErasAsync(
        IReadOnlyList<CatalogArtifactImportRow> artifacts,
        CancellationToken cancellationToken)
    {
        var definitions = LoadEraDefinitions();
        foreach (var group in artifacts.GroupBy(row => NormalizeCode(row.EraBucketCode), StringComparer.OrdinalIgnoreCase))
        {
            if (!definitions.TryGetValue(group.Key, out var definition))
                throw new InvalidDataException($"ERA_BUCKET_UNKNOWN|{group.Key} 未列入 era-buckets.json。");

            if (!await db.EraBuckets.AnyAsync(row => row.Code == group.Key, cancellationToken))
            {
                var sample = group.First();
                db.EraBuckets.Add(new EraBucket
                {
                    Id = StableGuid($"era:{group.Key}"),
                    Code = group.Key,
                    Name = definition.Name,
                    StartYear = ToShort(sample.EraStartYear) ?? definition.StartYear,
                    EndYear = ToShort(sample.EraEndYear) ?? definition.EndYear
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await db.EraBuckets
            .AsNoTracking()
            .ToDictionaryAsync(row => row.Code, row => row.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
    }

    private static List<AssetPlan> CreateAssetPlans(CatalogImportRequest request, ImportPlan plan)
    {
        var assets = new List<AssetPlan>();
        foreach (var artifact in plan.NewArtifacts)
        {
            assets.Add(CreateAssetPlan(
                request,
                "catalog",
                artifact.CategoryCode,
                artifact.ArtifactRef,
                artifact.ImageUrl,
                "display.jpg"));
            assets.Add(CreateAssetPlan(
                request,
                "catalog",
                artifact.CategoryCode,
                artifact.ArtifactRef,
                artifact.ThumbnailUrl,
                "thumbnail.jpg"));
        }

        foreach (var product in plan.NewProducts)
        {
            assets.Add(CreateAssetPlan(
                request,
                "store",
                product.CategoryCode,
                product.ExternalRef,
                product.ImageUrl,
                "image.jpg"));
        }

        return assets;
    }

    private static AssetPlan CreateAssetPlan(
        CatalogImportRequest request,
        string domain,
        string category,
        string key,
        string sourcePath,
        string fileName)
    {
        ValidateAssetSegment(domain, "domain");
        ValidateAssetSegment(category, "category");
        ValidateAssetSegment(key, "reference");
        ValidateAssetSegment(fileName, "file");
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidDataException($"MEDIA_MISSING|{domain}/{category}/{key}/{fileName}");

        var lowerCategory = category.ToLowerInvariant();
        var targetPath = Path.Combine(
            Path.GetFullPath(request.WebRootPath),
            "media",
            domain,
            lowerCategory,
            key,
            fileName);
        return new AssetPlan(
            sourcePath,
            targetPath,
            PublicAssetPath(domain, category, key, fileName));
    }

    private static void ValidateAssetTargets(
        string webRootPath,
        IReadOnlyList<AssetPlan> assets)
    {
        var root = Path.GetFullPath(webRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var duplicate = assets
            .GroupBy(asset => asset.TargetPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"ASSET_TARGET_DUPLICATE|{duplicate.Key}");

        foreach (var asset in assets)
        {
            if (!asset.TargetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ASSET_TARGET_ESCAPE|{asset.TargetPath}");
            if (File.Exists(asset.TargetPath))
                throw new InvalidDataException(
                    $"ASSET_TARGET_EXISTS|{asset.PublicPath} 已存在，為避免覆寫既有資產，停止匯入。");
        }
    }

    private static void CopyAssets(
        string mediaRootPath,
        IReadOnlyList<AssetPlan> assets,
        ICollection<string> copiedAssets)
    {
        foreach (var asset in assets)
        {
            var source = MediaSourcePath(mediaRootPath, asset.SourcePath);
            if (!File.Exists(source))
                throw new InvalidDataException($"MEDIA_MISSING|找不到 {asset.SourcePath}");

            Directory.CreateDirectory(Path.GetDirectoryName(asset.TargetPath)!);
            File.Copy(source, asset.TargetPath, overwrite: false);
            copiedAssets.Add(asset.TargetPath);
        }
    }

    private static string MediaSourcePath(string mediaRootPath, string sourcePath)
    {
        var root = Path.GetFullPath(mediaRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = sourcePath.TrimStart('/', '\\')
            .Replace('/', Path.DirectorySeparatorChar);
        if (relative.StartsWith("media" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            relative = relative[("media".Length + 1)..];

        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"MEDIA_PATH_ESCAPE|{sourcePath}");
        return fullPath;
    }

    private static void DeleteCopiedAssets(IEnumerable<string> paths)
    {
        foreach (var path in paths.Reverse())
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 不以清理失敗覆蓋原始匯入例外；下次由資產檢查回報殘留檔案。
            }
        }
    }

    private static bool IsValidArtifact(CatalogArtifactImportRow row) =>
        !string.IsNullOrWhiteSpace(row.ArtifactRef)
        && !string.IsNullOrWhiteSpace(row.Name)
        && string.Equals(row.NormalizationStatus, "AUTO_VERIFIED", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(row.SourceUrl)
        && !string.IsNullOrWhiteSpace(row.LicenseCode)
        && !string.IsNullOrWhiteSpace(row.ImageUrl)
        && !string.IsNullOrWhiteSpace(row.ThumbnailUrl);

    private static bool IsValidProduct(CatalogProductImportRow row) =>
        !string.IsNullOrWhiteSpace(row.ExternalRef)
        && !string.IsNullOrWhiteSpace(row.Name)
        && SupportedCategoryCodes.Contains(row.CategoryCode, StringComparer.OrdinalIgnoreCase)
        && row.Price >= 0
        && row.Stock >= 0
        && !string.IsNullOrWhiteSpace(row.ImageUrl);

    private static CatalogProductImportRow CreateGeneratedProduct(CatalogArtifactImportRow artifact)
    {
        var externalRef = $"artifact-{artifact.ArtifactRef}";
        var variation = StableNumber($"price:{artifact.ArtifactRef}") % 7 * 50;
        var sourceDescription = string.IsNullOrWhiteSpace(artifact.DescriptionOriginal)
            ? "以故宮開放資料文物為主題的展示型縮小複製品。"
            : artifact.DescriptionOriginal.Trim();
        return new CatalogProductImportRow(
            StableGuid($"product:{externalRef}"),
            externalRef,
            $"{artifact.Name.Trim()}－縮小複製品",
            NormalizeCode(artifact.CategoryCode),
            $"{sourceDescription}\n\n本商品為 QMAH 虛擬展示資料，僅供系統功能測試與課堂展示，不提供實際販售。",
            artifact.SizeOriginal,
            680 + variation,
            20,
            artifact.ImageUrl,
            artifact.SourceUrl,
            true,
            artifact.ArtifactRef);
    }

    private static bool HasArtifactChanges(
        ExistingArtifact existing,
        CatalogArtifactImportRow incoming) =>
        !string.Equals(existing.Name, incoming.Name.Trim(), StringComparison.Ordinal)
        || !string.Equals(existing.CategoryCode, incoming.CategoryCode, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(existing.EraBucketCode, incoming.EraBucketCode, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(existing.EraTextOriginal, NullIfWhiteSpace(incoming.EraTextOriginal), StringComparison.Ordinal)
        || !string.Equals(existing.Description, NullIfWhiteSpace(incoming.DescriptionOriginal), StringComparison.Ordinal)
        || !string.Equals(
            existing.SizeText,
            string.IsNullOrWhiteSpace(incoming.SizeOriginal) ? "官方資料未提供" : incoming.SizeOriginal.Trim(),
            StringComparison.Ordinal)
        || !string.Equals(existing.SourceUrl, incoming.SourceUrl.Trim(), StringComparison.Ordinal)
        || !string.Equals(existing.LicenseCode, NullIfWhiteSpace(incoming.LicenseCode), StringComparison.Ordinal)
        || !string.Equals(existing.AttributionText, NullIfWhiteSpace(incoming.AttributionText), StringComparison.Ordinal);

    private static bool HasProductChanges(
        ExistingProduct existing,
        CatalogProductImportRow incoming) =>
        !string.Equals(existing.Name, incoming.Name.Trim(), StringComparison.Ordinal)
        || !string.Equals(existing.CategoryCode, incoming.CategoryCode, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(existing.Description, NullIfWhiteSpace(incoming.Description), StringComparison.Ordinal)
        || !string.Equals(existing.SizeText, NullIfWhiteSpace(incoming.SizeText), StringComparison.Ordinal)
        || existing.Price != incoming.Price
        || !string.Equals(existing.SourceUrl, NullIfWhiteSpace(incoming.SourceUrl), StringComparison.Ordinal);

    private static string? NormalizeArtifactRef(CatalogProductImportRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ArtifactRef))
            return row.ArtifactRef.Trim();
        const string prefix = "artifact-";
        return row.ExternalRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? row.ExternalRef[prefix.Length..].Trim()
            : null;
    }

    private static Guid? ResolveProductArtifactId(
        CatalogProductImportRow product,
        IReadOnlyDictionary<string, Guid> artifactIds)
    {
        var artifactRef = NormalizeArtifactRef(product);
        return !string.IsNullOrWhiteSpace(artifactRef)
            && artifactIds.TryGetValue(artifactRef, out var artifactId)
            ? artifactId
            : null;
    }

    private static string BuildApprovalToken(
        IReadOnlyList<CatalogArtifactImportRow> artifacts,
        IReadOnlyList<CatalogProductImportRow> products,
        CatalogImportRequest request,
        bool generatedProducts)
    {
        // 以完整且固定順序的輸入欄位建立確認碼；不可只放編號，否則來源內容變更
        // 可能沿用舊確認碼，讓管理員誤以為預檢結果仍然相同。
        var canonical = JsonSerializer.Serialize(new
        {
            Artifacts = artifacts
                .OrderBy(row => row.ArtifactRef, StringComparer.OrdinalIgnoreCase)
                .Select(row => new
                {
                    row.Id,
                    ArtifactRef = row.ArtifactRef.Trim(),
                    Name = row.Name.Trim(),
                    CategoryCode = NormalizeCode(row.CategoryCode),
                    CategoryName = row.CategoryName?.Trim(),
                    EraBucketCode = NormalizeCode(row.EraBucketCode),
                    EraTextOriginal = row.EraTextOriginal?.Trim(),
                    DescriptionOriginal = row.DescriptionOriginal?.Trim(),
                    SourceUrl = row.SourceUrl.Trim(),
                    ImageUrl = row.ImageUrl.Trim(),
                    row.SourcePayloadJson,
                    NormalizationStatus = row.NormalizationStatus.Trim(),
                    row.QuestionEnabled,
                    AttributionText = row.AttributionText?.Trim(),
                    row.EraEndYear,
                    row.EraStartYear,
                    LicenseCode = row.LicenseCode?.Trim(),
                    SizeOriginal = row.SizeOriginal?.Trim(),
                    SourceDataset = row.SourceDataset?.Trim(),
                    ThumbnailUrl = row.ThumbnailUrl.Trim()
                }),
            Products = products
                .OrderBy(row => row.ExternalRef, StringComparer.OrdinalIgnoreCase)
                .Select(row => new
                {
                    row.Id,
                    ExternalRef = row.ExternalRef.Trim(),
                    Name = row.Name.Trim(),
                    CategoryCode = NormalizeCode(row.CategoryCode),
                    Description = row.Description?.Trim(),
                    SizeText = row.SizeText?.Trim(),
                    row.Price,
                    row.Stock,
                    ImageUrl = row.ImageUrl.Trim(),
                    SourceUrl = row.SourceUrl?.Trim(),
                    row.IsActive,
                    ArtifactRef = row.ArtifactRef?.Trim()
                }),
            Settings = new
            {
                request.SyncShop,
                request.SyncQuestionBank,
                request.MaxArtifactsPerCategory,
                request.MaxProducts,
                generatedProducts
            }
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private static IReadOnlyDictionary<string, EraDefinition> LoadEraDefinitions()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "era-buckets.json"),
            Path.Combine(AppContext.BaseDirectory, "Infrastructure", "CatalogImport", "era-buckets.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "QMAH.Web", "Infrastructure", "CatalogImport", "era-buckets.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "QmahDataTools", "NpmDataImporter", "era-buckets.json")
        };
        // 依序支援工具輸出、Web 執行檔與專案根目錄，方便 Visual Studio 和命令列使用
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("找不到 era-buckets.json，停止匯入。", candidates[0]);
        var rows = JsonSerializer.Deserialize<List<EraDefinition>>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? [];
        return rows.ToDictionary(row => row.Code, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateRequest(CatalogImportRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.WebRootPath))
            throw new InvalidDataException("WebRootPath 不可為空白。");
        if (string.IsNullOrWhiteSpace(request.MediaRootPath))
            throw new InvalidDataException("MediaRootPath 不可為空白。");
        if (request.MaxArtifactsPerCategory < 0 || request.MaxProducts < 0)
            throw new InvalidDataException("匯入數量上限不可為負數。");
    }

    private static void ValidateAssetSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/')
            || value.Contains('\\'))
        {
            throw new InvalidDataException($"ASSET_SEGMENT_INVALID|{label}:{value}");
        }
    }

    private static string NormalizeCode(string? value) => value?.Trim().ToUpperInvariant() ?? "";

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static short? ToShort(int? value) =>
        value is >= short.MinValue and <= short.MaxValue ? (short)value : null;

    // 固定識別值讓相同 ArtifactRef 重跑匯入時仍對應同一筆資料
    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash[..16]);
    }

    private static int StableNumber(string value) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0) & int.MaxValue;

    // 公開路徑維持固定層級，實體路徑仍會先經過檔案根目錄檢查
    public static string PublicAssetPath(string domain, string category, string key, string fileName) =>
        $"/media/{domain}/{category.ToLowerInvariant()}/{key}/{fileName}";

    private sealed record AssetPlan(string SourcePath, string TargetPath, string PublicPath);

    private sealed record EraDefinition(string Code, string Name, short? StartYear, short? EndYear);

    private sealed record ExistingArtifact(
        string ArtifactRef,
        Guid Id,
        string Name,
        string CategoryCode,
        string EraBucketCode,
        string? EraTextOriginal,
        string? Description,
        string? SizeText,
        string SourceUrl,
        string? LicenseCode,
        string? AttributionText);

    private sealed record ExistingProduct(
        string ExternalRef,
        Guid Id,
        Guid? ArtifactId,
        string CategoryCode,
        string Name,
        string? Description,
        string? SizeText,
        decimal Price,
        string? SourceUrl);

    private sealed record ImportPlan(
        IReadOnlyList<CatalogArtifactImportRow> Artifacts,
        IReadOnlyList<CatalogArtifactImportRow> NewArtifacts,
        IReadOnlyList<CatalogArtifactImportRow> UpdatedArtifacts,
        IReadOnlyDictionary<string, Guid> ArtifactIds,
        IReadOnlyList<Guid> NewQuestionArtifactIds,
        IReadOnlyList<CatalogProductImportRow> Products,
        IReadOnlyList<CatalogProductImportRow> NewProducts,
        IReadOnlyList<CatalogProductImportRow> UpdatedProducts,
        IReadOnlyList<string> MissingCategories,
        IReadOnlyList<string> Warnings,
        string ApprovalToken,
        int InvalidArtifactCount,
        int UnmappedArtifactCount,
        int InvalidProductCount,
        int UnmappedProductCount,
        int DuplicateArtifactCount,
        int ExistingQuestionEntryCount,
        int DuplicateProductCount,
        int UnchangedArtifactCount,
        int UnchangedProductCount,
        bool GeneratedProducts)
    {
        public CatalogImportPreview ToPreview(bool syncShop, bool syncQuestionBank) => new(
            ApprovalToken,
            Artifacts.Count,
            NewArtifacts.Count,
            DuplicateArtifactCount,
            NewQuestionArtifactIds.Count,
            ExistingQuestionEntryCount,
            Products.Count,
            NewProducts.Count,
            DuplicateProductCount,
            InvalidArtifactCount,
            UnmappedArtifactCount,
            InvalidProductCount,
            UnmappedProductCount,
            MissingCategories,
            Warnings,
            syncShop,
            GeneratedProducts,
            syncQuestionBank,
            UpdatedArtifacts.Count,
            UnchangedArtifactCount,
            UpdatedProducts.Count,
            UnchangedProductCount);
    }
}
