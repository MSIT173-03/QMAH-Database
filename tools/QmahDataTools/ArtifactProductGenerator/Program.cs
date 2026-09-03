using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

const string Notice = "本頁商品為 MSIT173 課程專題的虛擬展示資料，使用國立故宮博物院開放資料圖像建立對應的縮小複製品，僅供系統功能測試與課堂發表，不提供訂購、付款或實際販售。商品尺寸依公開資料換算為原作的一半；來源標示待測量或未提供時不自行推測。";

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
    var replicaSize = CreateReplicaSize(originalSize);
    var eraText = string.IsNullOrWhiteSpace(artifact.EraTextOriginal)
        ? artifact.EraBucket.Name.Trim()
        : artifact.EraTextOriginal.Trim();

    var productName = $"{artifact.Name}－縮小複製品";
    if (includeArtifactReference)
        productName += $"（故宮編號：{artifact.ArtifactRef}）";

    return new ProductOutput(
        StableGuid(externalRef),
        artifact.Id,
        externalRef,
        Trim(productName, 200),
        artifact.Category.Code,
        $"{marketingCopy.Text}\n\n商品資訊：\n分類：{artifact.Category.Name}\n年代：{eraText}\n商品尺寸：{replicaSize}\n原作尺寸：{originalSize}\n\n{Notice}\n\n圖像姓名標示：\n{attribution}\n\n原文物說明：\n{originalDescription}",
        replicaSize,
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
            $"玉石經過琢磨才成器，也在流傳中留下不同時代的眼光。{name}化為縮小複製品，讓這份溫潤含蓄的玉器之美走進日常。",
            $"欣賞玉器，不只看材質，也看工匠如何順著天然質地雕琢成形。{name}縮小複製品，適合放在近處慢慢看、慢慢品味。",
            $"一件玉器，可以寄託品味，也能收藏一段時代記憶。以{name}為靈感製作的縮小複製品，為展示空間添上一份沉靜氣質。",
            $"從光澤、質地到琢磨留下的細節，玉器總有值得反覆欣賞之處。{name}縮小複製品，邀你用更親近的距離重新認識它。"
        },
        "bronze" => new[]
        {
            $"金屬與火塑成器物，歲月再替表面留下時間的痕跡。這件「{name}」縮小複製品，把銅器沉穩厚實的存在感帶進收藏空間。",
            $"青銅器迷人的地方，在於器物本身與漫長年代共同形成的質感。以{name}為靈感製作的縮小複製品，值得從不同角度細看。",
            $"先看整體輪廓，再找製作與歲月留下的細節，銅器總能讓人多停留一會兒。這件「{name}」縮小複製品，讓這份歷史感更貼近日常。",
            $"有些文物不必鋪陳太多，安靜擺著就很有分量。這件「{name}」縮小複製品延續銅器特有的沉著氣質，適合成為展示中的視覺焦點。"
        },
        "ceramic" => new[]
        {
            $"泥土經過塑形與窯火，才成為能被長久欣賞的器物。這件「{name}」縮小複製品，把陶瓷溫雅耐看的氣質帶到眼前。",
            $"陶瓷的樂趣，在於輪廓、表面與燒製效果彼此呼應。以{name}為靈感製作的縮小複製品，適合留在身邊慢慢發現細節。",
            $"從日常器用到典藏珍品，陶瓷記錄了不同時代對生活之美的想像。這件「{name}」縮小複製品，讓這段美感自然融入展示空間。",
            $"一件陶瓷，可以從遠處看整體，也值得靠近欣賞質感。這件「{name}」縮小複製品，為收藏角落留下一份安定而耐看的風景。"
        },
        "enamel" => new[]
        {
            $"琺瑯以釉料與燒製換來鮮明而細緻的層次。這件「{name}」縮小複製品，把這份講究工序的華美濃縮成展示亮點。",
            $"色彩是琺瑯最直接的吸引力，工序與細節則值得再三欣賞。以{name}為靈感製作的縮小複製品，讓空間多一抹典藏氣息。",
            $"琺瑯工藝把色彩、材質與火候交織在同一件作品裡。這件「{name}」縮小複製品，適合近看其中豐富而有秩序的視覺層次。",
            $"想讓展示空間更有亮點，又保留古典工藝的細膩感，這件「{name}」縮小複製品會是一件很有存在感的收藏。"
        },
        "lacquer" => new[]
        {
            $"一道漆、一段等待，漆器的深度來自層層累積的工序。這件「{name}」縮小複製品，把這份沉靜而講究的工藝氣質帶進日常。",
            $"漆器耐看的地方，在於表面質感與製作時間共同留下的韻味。以{name}為靈感製作的縮小複製品，適合在近處慢慢欣賞。",
            $"光影落在漆面上，每個角度都有不同感受。這件「{name}」縮小複製品延續漆器含蓄而精緻的魅力，為展示空間添上一份古雅。",
            $"漆藝講究耐心，也讓器物擁有難以取代的深沉質感。這件「{name}」縮小複製品，是一件越看越能發現味道的收藏。"
        },
        "carving" => new[]
        {
            $"雕刻是在材料上不斷取捨，最後留下最想表達的形貌。這件「{name}」縮小複製品，讓刀工與構思成為可以近距離欣賞的焦點。",
            $"不同材料有不同個性，好的雕刻懂得順勢而為。以{name}為靈感製作的縮小複製品，保留一份因材施藝的工藝趣味。",
            $"雕刻值得從多個角度觀看，輪廓、轉折與細部會逐一展開。這件「{name}」縮小複製品，適合擺在能讓人停下腳步的位置。",
            $"從一塊材料到一件作品，中間藏著工匠無數次判斷。這件「{name}」縮小複製品，把這份手藝凝聚成耐看的收藏。"
        },
        "coin" => new[]
        {
            $"方寸之間，裝得下年代、制度與人們往來交易的痕跡。這件「{name}」縮小複製品，從一枚錢幣打開認識歷史的新角度。",
            $"錢幣曾在人群之間流轉，如今也成為辨認時代的重要線索。以{name}為靈感製作的縮小複製品，小巧卻很有故事。",
            $"看錢幣，不只看名稱，也看文字、形制與時代背景。這件「{name}」縮小複製品，適合作為一段歷史收藏的起點。",
            $"一枚錢幣，連起的是制度與日常生活。這件「{name}」縮小複製品，把龐大的時代故事收進容易細看的尺寸。"
        },
        "painting" => new[]
        {
            $"一幅畫最迷人的地方，是每次觀看都可能發現不同線索。這件「{name}」縮小複製品，把畫面的節奏與意境帶進日常空間。",
            $"從構圖、線條到題材安排，書畫總有值得慢慢閱讀之處。以{name}為靈感製作的縮小複製品，讓欣賞不必受距離限制。",
            $"遠看整體氣勢，近看筆墨細節，書畫能陪人反覆觀看。這件「{name}」縮小複製品，為牆面或展示角落留下一段雅致風景。",
            $"畫面不只記錄所見，也保存創作者觀看世界的方式。這件「{name}」縮小複製品，邀你把這份想像帶進自己的空間。"
        },
        _ => new[]
        {
            $"{name}化為縮小複製品，讓原作文物的時代氣息走進日常，也成為一件值得細看的收藏。"
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

static string CreateReplicaSize(string originalSize)
{
    if (originalSize.Contains("待測量", StringComparison.Ordinal)
        || originalSize.Equals("官方資料未提供", StringComparison.Ordinal))
        return originalSize;

    var expanded = Regex.Replace(
        originalSize,
        @"(?<first>\d+(?:\.\d+)?) × (?<second>\d+(?:\.\d+)?) 公分",
        "${first} 公分 × ${second} 公分");
    var replacements = 0;
    var scaled = Regex.Replace(
        expanded,
        @"(?<value>\d+(?:\.\d+)?)(?=\s*公分)",
        match =>
        {
            replacements++;
            var value = decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) / 2m;
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        });

    scaled = Regex.Replace(
        scaled,
        @"(?<first>\d+(?:\.\d+)?) 公分 × (?<second>\d+(?:\.\d+)?) 公分",
        "${first} × ${second} 公分");

    return replacements == 0
        ? $"待測量（原始記錄：{originalSize}）"
        : scaled;
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
            .Select(product => $"{product.ExternalRef}|{product.SizeText}|{product.CopyTemplateId}"))
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
