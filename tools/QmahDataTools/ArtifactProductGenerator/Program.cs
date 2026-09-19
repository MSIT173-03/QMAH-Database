using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

// 商城現在販售的是縮小複製品＋文物明信片套組；把規格集中在產生器，避免資料庫與前台各自拼出不同說法。
const string CardSize = "A6 文物明信片（148 × 105 mm）";
const string BundleSize = "A6 文物明信片＋縮小複製品展示組";
const string OrientationRule = "依主圖比例自動套用明信片版型";
const string Notice = "本資料集的商品皆以對應文物建立縮小複製品與文物明信片套組；明信片正面呈現名稱、類型與主圖，背面整理原文物尺寸與說明。";

try
{
    var options = Options.Parse(args);
    if (options.Help)
    {
        Console.WriteLine(Options.HelpText);
        return 0;
    }

    var dbOptions = new DbContextOptionsBuilder<QmahDbContext>()
        .UseSqlServer(options.ConnectionString)
        .Options;
    await using var db = new QmahDbContext(dbOptions);

    if (!await db.Database.CanConnectAsync())
        throw new InvalidOperationException("無法連線到 QMAH。請先還原參考資料庫。");

    var artifacts = await db.Artifacts
        .AsNoTracking()
        .Include(artifact => artifact.Category)
        .Include(artifact => artifact.EraBucket)
        .Where(artifact => artifact.PrimaryImagePath != ""
            && artifact.SourceUrl != ""
            && artifact.LicenseCode == "CC-BY-4.0")
        .ToListAsync();

    // ImageId=0 不是可用圖片；查詢完成後在記憶體過濾，避免把本機函式放進 EF expression tree。
    artifacts = artifacts.Where(artifact => !IsKnownUnavailableImage(artifact.PrimaryImagePath)).ToList();

    var artifactSizes = LoadArtifactSizes(options.ArtifactDataPath);
    if (artifactSizes.Count > 0)
    {
        var missingSizeRows = artifacts
            .Where(artifact => !artifactSizes.ContainsKey(artifact.ArtifactRef))
            .Select(artifact => artifact.ArtifactRef)
            .ToArray();
        if (missingSizeRows.Length > 0)
            throw new InvalidDataException($"尺寸資料與目前文物不一致，缺少 {missingSizeRows.Length} 筆：{string.Join(',', missingSizeRows.Take(5))}");

        foreach (var artifact in artifacts)
            artifact.SizeText = NormalizeOriginalSize(artifactSizes[artifact.ArtifactRef]);
    }
    else
    {
        foreach (var artifact in artifacts)
            artifact.SizeText = NormalizeOriginalSize(artifact.SizeText);
    }

    var selected = SelectBalanced(artifacts, options.Count, options.Seed);
    var repeatedArtifactNames = selected
        .GroupBy(artifact => artifact.Name.Trim(), StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var products = selected
        .Select(artifact => CreateProduct(
            artifact,
            options,
            repeatedArtifactNames.Contains(artifact.Name.Trim())))
        .OrderBy(product => product.CategoryCode, StringComparer.Ordinal)
        .ThenBy(product => product.Name, StringComparer.Ordinal)
        .ToList();

    var approvalToken = ApprovalToken(products, options);
    var payload = new OutputDocument(
        DateTime.UtcNow,
        products.Count,
        options.MinimumPrice,
        options.MaximumPrice,
        options.Seed,
        options.ReferenceYear,
        Notice,
        products);

    Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
    await File.WriteAllTextAsync(
        options.OutputPath,
        JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }),
        new UTF8Encoding(false));

    Console.WriteLine($"PREVIEW|products:{products.Count}|price:{options.MinimumPrice}-{options.MaximumPrice}|seed:{options.Seed}|reference-year:{options.ReferenceYear}");
    Console.WriteLine($"OUTPUT|{options.OutputPath}");
    Console.WriteLine($"APPROVAL_TOKEN|{approvalToken}");

    if (!options.Apply && !options.RefreshExisting)
        return 0;

    if (options.Apply && options.RefreshExisting)
        throw new ArgumentException("--apply 與 --refresh-existing 只能擇一使用。");

    if (!CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(options.ApprovalToken.ToUpperInvariant()),
        Encoding.UTF8.GetBytes(approvalToken)))
        throw new InvalidOperationException("確認碼不符。請先檢查輸出 JSON，再使用本次顯示的 APPROVAL_TOKEN。");

    if (options.RefreshExisting)
    {
        var existingProductList = await db.Products.ToListAsync();
        if (existingProductList.Any(product => string.IsNullOrWhiteSpace(product.ExternalRef)))
            throw new InvalidOperationException("目前商品含有空白 ExternalRef，不能使用 --refresh-existing。");

        var existingProducts = existingProductList
            .ToDictionary(product => product.ExternalRef!, StringComparer.Ordinal);
        var generatedRefs = products.Select(product => product.ExternalRef).ToHashSet(StringComparer.Ordinal);

        if (existingProducts.Count != products.Count
            || existingProducts.Keys.Any(reference => !generatedRefs.Contains(reference)))
            throw new InvalidOperationException("目前商品基準與預覽結果不一致，不能使用 --refresh-existing。請改用新的測試資料庫後執行 --apply。");

        await using var refreshTransaction = await db.Database.BeginTransactionAsync();
        if (artifactSizes.Count > 0)
        {
            var artifactRefs = artifacts.Select(artifact => artifact.ArtifactRef).ToArray();
            var trackedArtifacts = await db.Artifacts
                .Where(artifact => artifactRefs.Contains(artifact.ArtifactRef))
                .ToListAsync();
            foreach (var artifact in trackedArtifacts)
                artifact.SizeText = NormalizeOriginalSize(artifactSizes[artifact.ArtifactRef]);
        }

        foreach (var generated in products)
        {
            var current = existingProducts[generated.ExternalRef];
            if (current.ArtifactId != generated.ArtifactId)
                throw new InvalidOperationException($"商品 {generated.ExternalRef} 對應到不同文物，不能使用 --refresh-existing。");

            current.Name = generated.Name;
            current.CategoryCode = generated.CategoryCode;
            current.Description = generated.Description;
            current.SizeText = generated.SizeText;
            current.Price = generated.Price;
            current.Stock = generated.Stock;
            current.PrimaryImagePath = generated.PrimaryImagePath;
            current.SourceUrl = generated.SourceUrl;
            current.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        await refreshTransaction.CommitAsync();
        Console.WriteLine($"REFRESHED|products:{products.Count}");
        return 0;
    }

    if (await db.CartItems.AnyAsync() || await db.OrderDetails.AnyAsync())
        throw new InvalidOperationException("資料庫已有購物車或訂單明細，不可替換商品基準。請改用新的測試資料庫。");

    await using var transaction = await db.Database.BeginTransactionAsync();
    if (artifactSizes.Count > 0)
    {
        var artifactRefs = artifacts.Select(artifact => artifact.ArtifactRef).ToArray();
        var trackedArtifacts = await db.Artifacts
            .Where(artifact => artifactRefs.Contains(artifact.ArtifactRef))
            .ToListAsync();
        foreach (var artifact in trackedArtifacts)
            artifact.SizeText = NormalizeOriginalSize(artifactSizes[artifact.ArtifactRef]);
        await db.SaveChangesAsync();
    }

    db.Products.RemoveRange(await db.Products.ToListAsync());
    await db.SaveChangesAsync();

    db.Products.AddRange(products.Select(product => new Product
    {
        Id = product.Id,
        ArtifactId = product.ArtifactId,
        ExternalRef = product.ExternalRef,
        Name = product.Name,
        CategoryCode = product.CategoryCode,
        Description = product.Description,
        SizeText = product.SizeText,
        Price = product.Price,
        Stock = product.Stock,
        PrimaryImagePath = product.PrimaryImagePath,
        SourceUrl = product.SourceUrl,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    }));
    await db.SaveChangesAsync();
    await transaction.CommitAsync();

    Console.WriteLine($"APPLIED|products:{products.Count}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Artifact product generation failed: {ex.Message}");
    return 1;
}

// 故宮 ImageId=0 代表來源沒有圖；產生器直接排除，避免把 no image available 寫進正式商品。
static bool IsKnownUnavailableImage(string? imagePath) =>
    !string.IsNullOrWhiteSpace(imagePath)
    && Regex.IsMatch(imagePath, @"(?:[?&])ImageId=0(?:&|$)", RegexOptions.IgnoreCase);

static List<Artifact> SelectBalanced(IReadOnlyCollection<Artifact> artifacts, int count, int seed)
{
    var groups = artifacts
        .GroupBy(artifact => artifact.Category.Code, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .ToList();

    if (groups.Count == 0)
        throw new InvalidDataException("找不到具有 CC BY 4.0 圖片的文物。");

    if (count == 0)
        return groups
            .SelectMany(group => group.OrderBy(artifact => artifact.ArtifactRef, StringComparer.Ordinal))
            .ToList();

    var baseCount = count / groups.Count;
    var remainder = count % groups.Count;
    var selected = new List<Artifact>(count);

    for (var index = 0; index < groups.Count; index++)
    {
        var take = baseCount + (index < remainder ? 1 : 0);
        var candidates = groups[index]
            .OrderBy(artifact => StableNumber($"select:{seed}:{artifact.ArtifactRef}"))
            .Take(take)
            .ToList();

        if (candidates.Count != take)
            throw new InvalidDataException($"分類 {groups[index].Key} 只有 {candidates.Count} 件可用文物，需要 {take} 件。");

        selected.AddRange(candidates);
    }

    return selected;
}

static ProductOutput CreateProduct(
    Artifact artifact,
    Options options,
    bool includeArtifactReference)
{
    var midpointYear = EraMidpoint(artifact.EraBucket, options.ReferenceYear);
    var ageYears = Math.Max(0, options.ReferenceYear - midpointYear);
    var eraWeight = RoundToTen(Math.Min(900, ageYears / 4));
    var categoryWeight = CategoryWeight(artifact.Category.Code);
    var variation = (int)(StableNumber($"price:{options.Seed}:{artifact.ArtifactRef}") % 25) * 10;
    // 套組價格以明信片與展示用複製品的合理區間估算，仍保留穩定種子讓測試資料可重現。
    const int basePrice = 480;
    var calculatedPrice = RoundToTen(basePrice + eraWeight + categoryWeight + variation);
    var price = Math.Clamp(calculatedPrice, options.MinimumPrice, options.MaximumPrice);
    var externalRef = "artifact-" + artifact.ArtifactRef;
    if (externalRef.Length > 100)
        externalRef = "artifact-" + StableHex(artifact.ArtifactRef, 32).ToLowerInvariant();
    var originalDescription = string.IsNullOrWhiteSpace(artifact.Description)
        ? "原文物未提供說明。"
        : artifact.Description.Trim();
    var attribution = string.IsNullOrWhiteSpace(artifact.AttributionText)
        ? $"{artifact.Name}，國立故宮博物院，臺北，CC BY 4.0 @ www.npm.gov.tw"
        : artifact.AttributionText.Trim();
    var marketingCopy = CreateMarketingCopy(artifact, options.Seed);
    var originalSize = NormalizeOriginalSize(artifact.SizeText);
    var eraText = string.IsNullOrWhiteSpace(artifact.EraTextOriginal)
        ? artifact.EraBucket.Name.Trim()
        : artifact.EraTextOriginal.Trim();

    var artifactName = artifact.Name.Trim();
    var productName = $"{artifactName}－複製品＆文物明信片套組";
    if (includeArtifactReference)
        productName += $"（故宮編號：{artifact.ArtifactRef}）";

    // 每筆商品都直接帶入文物名稱，避免只寫本套組而讓商城使用者看不出複製品與明信片對應哪件文物。
    var productNotice = CreateProductNotice(
        artifactName,
        artifact.Category.Name,
        eraText,
        originalSize);

    return new ProductOutput(
        StableGuid(externalRef),
        artifact.Id,
        externalRef,
        Trim(productName, 200),
        artifact.Category.Code,
        $"{marketingCopy.Text}\n\n套組內容：\n1. {artifactName}文物明信片，{CardSize}；正面使用文物主圖，版型依圖片比例自動配置。\n2. {artifactName}縮小複製品展示物；依本件文物影像製作，實際材質與尺寸以出貨標示為準。\n\n文物資料：\n名稱：{artifactName}\n分類：{artifact.Category.Name}\n年代：{eraText}\n原文物尺寸：{originalSize}\n\n商品用途：\n{productNotice}\n\n來源與姓名標示：\n{attribution}\n\n原文物說明：\n{originalDescription}",
        BundleSize,
        OrientationRule,
        price,
        20,
        artifact.PrimaryImagePath,
        artifact.SourceUrl,
        artifact.ArtifactRef,
        marketingCopy.TemplateId,
        new PriceBreakdown(basePrice, midpointYear, ageYears, eraWeight, categoryWeight, variation, calculatedPrice, price));
}

static string CreateProductNotice(
    string artifactName,
    string? categoryName,
    string? eraText,
    string? originalSize)
{
    // 基礎說明只負責交代套組與文物身份；可用欄位逐句加入，缺值就省略，不把資料庫備註露給顧客。
    var facts = new List<string>();
    if (!string.IsNullOrWhiteSpace(categoryName))
        facts.Add($"分類為{categoryName.Trim()}");
    if (!string.IsNullOrWhiteSpace(eraText))
        facts.Add($"年代記錄為{eraText.Trim()}");
    if (!string.IsNullOrWhiteSpace(originalSize))
        facts.Add($"原作尺寸為{originalSize.Trim()}");

    var factSentence = facts.Count == 0
        ? string.Empty
        : $"資料記錄顯示，{string.Join('，', facts)}。";

    return $"本套組以{artifactName}為對象，包含一張文物明信片與一件依原作影像製作的縮小複製品展示物。{factSentence}明信片正面呈現作品名稱、類型與主圖，背面整理原作尺寸與原文物說明；複製品的材質與製作尺寸以商品資訊為準。";
}

static MarketingCopy CreateMarketingCopy(Artifact artifact, int seed)
{
    var name = artifact.Name.Trim();
    var templates = artifact.Category.Code.ToLowerInvariant() switch
    {
        "jade" => new[]
        {
            $"觀看{name}時，可先看輪廓、表面光澤與可見琢痕，再對照原作尺寸，分清影像細節與實物大小。",
            $"孔洞、紋飾與邊緣的處理，是{name}很值得放大的地方；主圖用來認識外觀，原作資料則以圖鑑記錄為準。",
            $"先看{name}的整體比例，再看表面留下的加工痕跡；明信片保留方便閱讀的主圖，原作尺寸另列。"
        },
        "bronze" => new[]
        {
            $"看{name}時，可先從器形、口沿、足部與紋飾找線索，再回到原圖確認表面痕跡。",
            $"{name}的主圖適合先看整體輪廓，再放大比較紋飾與表面狀態；照片上的色澤不直接等於材質結論。",
            $"鑄造接縫、鏽蝕與紋飾位置都是{name}值得回查的細節；影像看不清楚的地方，就保留疑問。"
        },
        "ceramic" => new[]
        {
            $"觀看{name}時，可先確認器口、腹部、底足與釉面，再對照紋飾在器身上的位置。",
            $"釉色和輪廓是{name}主圖裡最容易辨認的兩項；窯口、年代與工藝仍應回到來源資料核對。",
            $"先看{name}的整體比例與器面裝飾，再放大局部釉色；明信片方便展示，不能取代原始影像。"
        },
        "enamel" => new[]
        {
            $"看{name}時，可先看色塊、邊線與裝飾區域如何分界，再回到原圖確認細節。",
            $"{name}的反光會隨光線與角度改變；主圖呈現的是固定視角，色彩判讀仍以來源影像為準。",
            $"先比較{name}的整體構圖，再看局部釉色與線條；少用漂亮形容，直接回到圖像細節。"
        },
        "lacquer" => new[]
        {
            $"觀看{name}時，可先看器形與表面光澤，再找紋樣、刻痕與磨耗留下的位置。",
            $"{name}的漆面反光會隨觀看角度改變；主圖適合辨認構圖與表面線索，不單獨用來判定材質狀態。",
            $"把{name}的整體輪廓和局部紋樣分開看，會比只看色澤更容易回到原始資料。"
        },
        "carving" => new[]
        {
            $"觀看{name}時，可先看輪廓、轉折、刀痕與側面厚度，再比較不同區域的光影。",
            $"看{name}時，若主圖呈現工具痕、磨耗或材料紋理，可記下位置回查來源；看不清楚的地方不補猜。",
            $"長形或立體的{name}不適合只看正面；明信片保留主圖，細節仍要回到原始影像確認。"
        },
        "coin" => new[]
        {
            $"觀看{name}時，可先看錢文、穿孔、輪廓與邊緣磨耗，再回到圖鑑核對年代和版別。",
            $"如果{name}是方孔錢，穿孔形狀與錢文位置值得放大比較；影像不清楚的部分就保留疑問。",
            $"錢幣原作與明信片尺寸差異很大，閱讀時應分開看實物尺寸與主圖比例；商品頁會另外列出原作資料。"
        },
        "painting" => new[]
        {
            $"觀看{name}時，可先看完整構圖，再看題跋、鈐印、筆墨與留白的關係。",
            $"長幅作品先確認{name}的閱讀方向與題跋位置，再放大看局部線條；原作尺寸以圖鑑資料為準。",
            $"明信片保留{name}的主要畫面，適合先認識構圖；需要細讀筆墨與鈐印，仍應回到原始大圖。"
        },
        _ => new[]
        {
            $"這張{name}文物明信片保留來源圖像，適合先看整體，再回到原始資料核對細節。",
            $"觀看{name}時，可先從主圖辨認輪廓與裝飾，再以商品頁的原作尺寸和說明補足背景。",
            $"{name}的明信片與縮小複製品放在同一套組內，方便收藏，也方便把看到的細節帶回圖鑑查找。"
        }
    };

    var index = (int)(StableNumber($"copy:{seed}:{artifact.ArtifactRef}") % templates.Length);
    return new MarketingCopy($"{artifact.Category.Code.ToLowerInvariant()}-v3-{index + 1}", templates[index]);
}

static Dictionary<string, string?> LoadArtifactSizes(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
        throw new FileNotFoundException("找不到 --artifact-data 指定的文物 JSON。", path);

    var rows = JsonSerializer.Deserialize<List<ArtifactSizeRow>>(
        File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    if (rows.Count == 0 || rows.Any(row => string.IsNullOrWhiteSpace(row.ArtifactRef)))
        throw new InvalidDataException("文物 JSON 沒有可用的 artifactRef。 ");
    if (rows.Select(row => row.ArtifactRef).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
        throw new InvalidDataException("文物 JSON 的 artifactRef 有重複值。 ");

    return rows.ToDictionary(row => row.ArtifactRef, row => row.SizeOriginal, StringComparer.OrdinalIgnoreCase);
}

static string NormalizeOriginalSize(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return "官方資料未提供";

    var normalized = Regex.Replace(value.Trim(), @"\s*[xX×]\s*", " × ");
    normalized = Regex.Replace(
        normalized,
        @"(?<label>通高|全高|高)\s*(?<value>\d+(?:\.\d+)?)\s*公克",
        "${label} ${value} 公分");
    normalized = Regex.Replace(
        normalized,
        @"(?<first>\d+(?:\.\d+)?)(?:\s*公分)? × (?<second>\d+(?:\.\d+)?)\s*公分",
        "${first} × ${second} 公分");
    var barePair = Regex.Match(normalized, @"^(?<first>\d+(?:\.\d+)?) × (?<second>\d+(?:\.\d+)?)$");
    if (barePair.Success)
        normalized = $"{barePair.Groups["first"].Value} × {barePair.Groups["second"].Value} 公分";

    normalized = Regex.Replace(normalized, @"(?<value>\d+(?:\.\d+)?)\s*公分", "${value} 公分");
    normalized = Regex.Replace(normalized, @"(?<=[\p{L}])(?=\d)", " ");
    normalized = Regex.Replace(normalized, @"公分\s+(?=[\p{L}]+\s*\d)", "公分、");
    return normalized;
}

static int EraMidpoint(EraBucket era, int referenceYear)
{
    var start = era.StartYear ?? referenceYear;
    var end = era.EndYear ?? referenceYear;
    return (int)Math.Round((start + end) / 2d, MidpointRounding.AwayFromZero);
}

static int CategoryWeight(string categoryCode) => categoryCode.ToLowerInvariant() switch
{
    "jade" => 500,
    "enamel" => 420,
    "bronze" => 380,
    "carving" => 340,
    "lacquer" => 300,
    "ceramic" => 260,
    "painting" => 220,
    "coin" => 100,
    _ => 200
};

static int RoundToTen(int value) => (int)Math.Round(value / 10d, MidpointRounding.AwayFromZero) * 10;

static uint StableNumber(string value) =>
    BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0);

static string StableHex(string value, int length) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length];

static Guid StableGuid(string value)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return new Guid(bytes[..16]);
}

static string Trim(string value, int maximumLength) =>
    value.Length <= maximumLength ? value : value[..maximumLength];

static string ApprovalToken(IReadOnlyCollection<ProductOutput> products, Options options)
{
    var value = string.Join('\n', products
            .OrderBy(product => product.ExternalRef, StringComparer.Ordinal)
            // 確認碼必須涵蓋實際會寫入資料庫的名稱、說明、價格與圖片，避免只審到尺寸就套用另一份內容。
            .Select(product => $"{product.ExternalRef}|{product.Name}|{product.Description}|{product.SizeText}|{product.Price}|{product.PrimaryImagePath}|{product.PostcardOrientation}|{product.CopyTemplateId}"))
        + $"\n{options.Count}|{options.MinimumPrice}|{options.MaximumPrice}|{options.Seed}|{options.ReferenceYear}";
    return StableHex(value, 16);
}

sealed record ProductOutput(
    Guid Id,
    Guid ArtifactId,
    string ExternalRef,
    string Name,
    string CategoryCode,
    string Description,
    string SizeText,
    string PostcardOrientation,
    int Price,
    int Stock,
    string PrimaryImagePath,
    string SourceUrl,
    string ArtifactRef,
    string CopyTemplateId,
    PriceBreakdown PriceBreakdown);

sealed record MarketingCopy(string TemplateId, string Text);

sealed record ArtifactSizeRow(string ArtifactRef, string? SizeOriginal);

sealed record PriceBreakdown(
    int BasePrice,
    int EraMidpointYear,
    int AgeYears,
    int EraWeight,
    int CategoryWeight,
    int SeededVariation,
    int BeforeClamp,
    int FinalPrice);

sealed record OutputDocument(
    DateTime GeneratedAtUtc,
    int Count,
    int MinimumPrice,
    int MaximumPrice,
    int Seed,
    int ReferenceYear,
    string UsageNotice,
    IReadOnlyList<ProductOutput> Products);

sealed record Options(
    string ConnectionString,
    string? ArtifactDataPath,
    string OutputPath,
    int Count,
    int MinimumPrice,
    int MaximumPrice,
    int Seed,
    int ReferenceYear,
    bool Apply,
    bool RefreshExisting,
    string ApprovalToken,
    bool Help)
{
    public const string HelpText = """
        ArtifactProductGenerator

        從 QMAH 的 CC BY 4.0 文物建立課程示意商品。預設只輸出預覽 JSON，不修改資料庫。

          --count <數量|all>      商品數量，預設 all（每件合格文物各一件商品）
          --min-price <整數>      套組最低示意價格，預設 680
          --max-price <整數>      套組最高示意價格，預設 1680
          --seed <整數>           固定亂數種子，預設 173
          --reference-year <年>   年代加權參考年，預設 2026
          --artifact-data <json>  含 artifactRef 與 sizeOriginal 的文物匯入 JSON
          --output <json>         輸出路徑
          --connection <字串>     SQL Server 連線字串
          --apply                 建立或整批替換商品基準；已有購物車或訂單時會拒絕
          --refresh-existing      只更新既有商品內容；保留商品 Id、購物車與訂單快照
          --approve <確認碼>      預覽後顯示的確認碼

        範例：
          dotnet run --project .\ArtifactProductGenerator -- --count all --min-price 300 --max-price 2200 --seed 173 --output C:\output\artifact-products.json
        """;

    public static Options Parse(string[] args)
    {
        string Value(string key, string fallback)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        int Number(string key, int fallback, int minimum, int maximum)
        {
            var value = Value(key, fallback.ToString());
            if (!int.TryParse(value, out var number) || number < minimum || number > maximum)
                throw new ArgumentException($"{key} 必須是 {minimum} 到 {maximum} 的整數。");
            return number;
        }

        if (args.Length == 0 || args.Contains("--help"))
            return new("", null, "", 0, 680, 1680, 173, 2026, false, false, "", true);

        var minimumPrice = Number("--min-price", 680, 1, 1_000_000);
        var maximumPrice = Number("--max-price", 1680, 1, 1_000_000);
        if (minimumPrice > maximumPrice)
            throw new ArgumentException("--min-price 不可大於 --max-price。");

        var countText = Value("--count", "all");
        var count = countText.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? 0
            : int.TryParse(countText, out var parsedCount) && parsedCount >= 1
                ? parsedCount
                : throw new ArgumentException("--count 必須是 all 或正整數。");

        return new Options(
            Value("--connection", "Server=(localdb)\\MSSQLLocalDB;Database=QMAH;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False"),
            string.IsNullOrWhiteSpace(Value("--artifact-data", "")) ? null : Path.GetFullPath(Value("--artifact-data", "")),
            Path.GetFullPath(Value("--output", Path.Combine("_工具輸出", "artifact-products.json"))),
            count,
            minimumPrice,
            maximumPrice,
            Number("--seed", 173, 0, int.MaxValue),
            Number("--reference-year", 2026, 1900, 9999),
            args.Contains("--apply"),
            args.Contains("--refresh-existing"),
            Value("--approve", ""),
            false);
    }
}
