using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

// 商品目前是文物明信片展示資料，不再把原作描述成縮小複製品；這樣可讓資料庫、前台與部署素材保持同一個產品契約。
const string CardSize = "A6 明信片（148 × 105 mm）";
const string OrientationRule = "依主圖原始寬高自動判斷（橫式／直式）";
const string Notice = "本頁商品為 QMAH 文物明信片展示資料，正面使用國立故宮博物院開放資料圖像，背面整理名稱、類型與基本收藏資訊。固定 A6 尺寸適合放入收藏冊、展示架或書桌，也能在背面寫下短訊息寄給親朋好友；實際寄送前，請依當地郵務規定確認紙材、尺寸、郵資與郵務面配置。目前內容供系統功能測試與課堂展示，不代表已建立實體印刷、付款或出貨流程。";

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
    const int basePrice = 280;
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

    var productName = $"{artifact.Name}－文物明信片";
    if (includeArtifactReference)
        productName += $"（故宮編號：{artifact.ArtifactRef}）";

    return new ProductOutput(
        StableGuid(externalRef),
        artifact.Id,
        externalRef,
        Trim(productName, 200),
        artifact.Category.Code,
        $"{marketingCopy.Text}\n\n商品資訊：\n分類：{artifact.Category.Name}\n年代：{eraText}\n商品尺寸：{CardSize}\n明信片方向：{OrientationRule}\n原作尺寸：{originalSize}\n\n{Notice}\n\n圖像姓名標示：\n{attribution}\n\n原文物說明：\n{originalDescription}",
        CardSize,
        OrientationRule,
        price,
        20,
        artifact.PrimaryImagePath,
        artifact.SourceUrl,
        artifact.ArtifactRef,
        marketingCopy.TemplateId,
        new PriceBreakdown(basePrice, midpointYear, ageYears, eraWeight, categoryWeight, variation, calculatedPrice, price));
}

static MarketingCopy CreateMarketingCopy(Artifact artifact, int seed)
{
    var name = artifact.Name.Trim();
    var templates = artifact.Category.Code.ToLowerInvariant() switch
    {
        "jade" => new[]
        {
            $"先看{name}的輪廓、表面光澤與可見琢痕，再回到原作尺寸判斷實物大小；明信片只保留影像線索，不替資料補猜材質或用途。",
            $"玉器的孔洞、紋飾與邊緣最適合放大比對。這張{name}文物明信片把主圖放在正面，背面另列原作尺寸與來源，查找時不會混在一起。",
            $"觀看{name}時，可以把正面影像和圖鑑中的原作尺寸並排；明信片固定 A6，玉器本身的長寬高仍以文物資料為準。"
        },
        "bronze" => new[]
        {
            $"青銅器先看器形、口沿、足部與紋飾，再看表面顏色是否可能受光線或保存狀態影響。{name}明信片背面保留來源，方便回到原圖核對。",
            $"{name}的主圖適合先看整體輪廓，再放大局部紋飾與表面痕跡；明信片尺寸固定，不能拿來推算青銅器的實際大小。",
            $"如果影像中看得到鑄造接縫、鏽蝕或紋飾，這些才是值得回查的線索。這張{name}文物明信片只整理可見內容，不把觀察寫成鑑定結論。"
        },
        "ceramic" => new[]
        {
            $"陶瓷先看器口、腹部、底足與釉面，再對照紋飾在器身上的位置。{name}文物明信片正面保留整體主圖，背面列原作尺寸，方便分清卡片與實物。",
            $"{name}的釉色和輪廓是主圖裡最容易比較的兩項；若要判斷窯口、年代或工藝，仍應回到圖鑑來源，不以明信片代替研究資料。",
            $"固定 A6 尺寸讓{name}適合放在書桌或展示架，但不代表原作也是同樣大小。商品頁把兩個尺寸分開列出，展示前先量空間比較準。"
        },
        "enamel" => new[]
        {
            $"琺瑯器可以先看色塊、邊線與裝飾區域的分界；{name}文物明信片保留主圖和基本來源，方便把可見色彩與原作資料分開核對。",
            $"{name}的表面反光會受觀看角度和燈光影響，商品頁的主圖只是一個固定視角。背面列出來源與原作尺寸，避免把照片效果當成材質結論。",
            $"觀看{name}時，先比較整體構圖，再看局部釉色與線條，比只用鮮豔或漂亮形容更容易回到實際影像。明信片成品固定為 A6。"
        },
        "lacquer" => new[]
        {
            $"漆器主圖先看器形和表面光澤，再找可見的紋樣、刻痕或磨耗；{name}文物明信片把影像與原作尺寸分開呈現，適合拿來回查細節。",
            $"漆面反光會隨角度改變，觀看{name}時最好不要只用一張照片判斷表面狀態。商品背面列來源與基本資訊，研究仍回到圖鑑原圖。",
            $"{name}固定做成 A6 明信片，展示時可以靠近看圖，但不能由卡片比例推算漆器實物大小；原作尺寸會另外標示。"
        },
        "carving" => new[]
        {
            $"雕刻品要看輪廓、轉折、刀痕與側面厚度；{name}文物明信片正面保留主圖，背面列原作尺寸，方便知道哪些是影像線索、哪些是實物尺度。",
            $"若主圖能看到工具痕、磨耗或材料紋理，可以把位置記下來再回查資料；不要只用表面顏色推定材質或年代。這是{name}明信片的觀看重點。",
            $"{name}的展示方向依主圖自然寬高判斷，長形作品不硬裁成正方形；成品仍固定為 A6 明信片，原作大小另列。"
        },
        "coin" => new[]
        {
            $"錢幣先看正背面文字、穿孔、輪廓與邊緣磨耗，再回到圖鑑核對年代和版別；{name}文物明信片只呈現主圖，不把卡片尺寸當成錢幣實際大小。",
            $"若{name}是方孔錢，穿孔形狀與錢文位置都值得比對；若主圖看不清楚，就保留疑問，不用一句「看起來像」代替資料。",
            $"錢幣原作尺寸通常不大，但商品仍固定為 A6 明信片，方便閱讀正背面影像與來源；兩種尺寸在商品頁分開標示。"
        },
        "painting" => new[]
        {
            $"書畫先看完整構圖，再看題跋、鈐印、筆墨與留白；{name}是長幅作品時，明信片依主圖比例採橫式或直式，不把原圖硬裁成方形。",
            $"{name}的正面保留主要畫面，背面列原作尺寸與來源；A6 是商品尺寸，不是畫冊或畫卷的實際長寬。需要細讀時仍應回到大圖。",
            $"觀看書畫時，先確認畫面方向和題跋位置，再看局部線條。這張{name}文物明信片把名稱和類型放在正面，方便展示時辨認作品。"
        },
        _ => new[]
        {
            $"{name}文物明信片正面使用來源圖像，背面列名稱、類型、原作尺寸與來源；成品固定為 A6，適合展示與回查，不替原作補寫沒有來源的結論。"
        }
    };

    var index = (int)(StableNumber($"copy:{seed}:{artifact.ArtifactRef}") % templates.Length);
    return new MarketingCopy($"{artifact.Category.Code.ToLowerInvariant()}-v2-{index + 1}", templates[index]);
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
            .Select(product => $"{product.ExternalRef}|{product.SizeText}|{product.PostcardOrientation}|{product.CopyTemplateId}"))
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
          --min-price <整數>      最低示意價格，預設 300
          --max-price <整數>      最高示意價格，預設 2200
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
            return new("", null, "", 0, 300, 2200, 173, 2026, false, false, "", true);

        var minimumPrice = Number("--min-price", 300, 1, 1_000_000);
        var maximumPrice = Number("--max-price", 2200, 1, 1_000_000);
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
