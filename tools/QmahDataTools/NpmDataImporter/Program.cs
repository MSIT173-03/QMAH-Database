using Microsoft.EntityFrameworkCore;

using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.CatalogImport;

var options = ImportOptions.Parse(args);
if (options.Help)
{
    Console.WriteLine(ImportOptions.HelpText);
    return;
}

try
{
    var projectRoot = ImportOptions.ResolveProjectRoot(options.Project);
    var webProject = Path.Combine(projectRoot, "QMAH.Web");
    if (!File.Exists(Path.Combine(webProject, "QMAH.Web.csproj")))
        throw new InvalidOperationException("目標必須是含 QMAH.Web.csproj 的 QMAH 專案。");

    var package = await CatalogImportPackage.LoadFilesAsync(
        Path.GetFullPath(options.Artifacts),
        options.SyncShop ? Path.GetFullPath(options.Products) : null);
    var request = new CatalogImportRequest(
        package.Artifacts,
        package.Products,
        Path.Combine(webProject, "wwwroot"),
        ImportOptions.ResolveMediaRoot(options.MediaRoot),
        options.SyncShop,
        options.ArtifactPerCategory,
        options.ProductLimit,
        RequireCompleteProfile: true,
        GenerateProductsFromArtifacts: false,
        SyncQuestionBank: options.SyncQuestionBank);

    var dbOptions = new DbContextOptionsBuilder<QmahDbContext>()
        .UseSqlServer(options.Connection)
        .Options;
    await using var db = new QmahDbContext(dbOptions);
    if (!await db.Database.CanConnectAsync())
        throw new InvalidOperationException(
            "無法連線目標 SQL Server；請先依 database/README.md 建立並核對 QMAH Schema。匯入器不會自動建表。");

    var service = new CatalogImportService(db);
    var preview = await service.PreviewAsync(request);
    Console.WriteLine(
        $"PRECHECK|artifactCandidates:{preview.ArtifactCandidateCount}|artifacts=added:{preview.NewArtifactCount}|updated:{preview.UpdatedArtifactCount}|unchanged:{preview.UnchangedArtifactCount}|existing:{preview.DuplicateArtifactCount}|invalid:{preview.InvalidArtifactCount}|unmapped:{preview.UnmappedArtifactCount}|questionEntries=new:{preview.NewQuestionEntryCount}|existing:{preview.ExistingQuestionEntryCount}|productCandidates:{preview.ProductCandidateCount}|products=added:{preview.NewProductCount}|updated:{preview.UpdatedProductCount}|unchanged:{preview.UnchangedProductCount}|existing:{preview.DuplicateProductCount}|invalid:{preview.InvalidProductCount}|unmapped:{preview.UnmappedProductCount}");
    Console.WriteLine(
        $"PROFILE|categories:{CatalogImportService.SupportedCategoryCodes.Count}|perCategory:{options.ArtifactPerCategory}|missing:{(preview.MissingCategories.Count == 0 ? "none" : string.Join(',', preview.MissingCategories))}|productsTarget:{options.ProductLimit}|productGap:{Math.Max(0, options.ProductLimit - preview.ProductCandidateCount)}|questionBank:{(options.SyncQuestionBank ? "enabled" : "disabled")}");
    foreach (var warning in preview.Warnings)
        Console.WriteLine($"WARNING|{warning}");
    Console.WriteLine($"APPROVAL_TOKEN|{preview.ApprovalToken}");

    if (!options.Apply)
    {
        Console.WriteLine("DRY_RUN|未寫入；資料齊全後，以 --apply --approve <本次確認碼> 才會只新增資料與複製資產。");
        return;
    }

    if (!string.Equals(options.ApprovalToken, preview.ApprovalToken, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "確認碼不符或已過期；請重新執行預檢，複製本次顯示的 APPROVAL_TOKEN 後再加 --apply。");

    var result = await service.ImportAsync(request, options.ApprovalToken);
    Console.WriteLine(
        $"APPLIED|artifacts=added:{result.ArtifactCount}|updated:{result.UpdatedArtifactCount}|unchanged:{result.UnchangedArtifactCount}|questionEntries:{result.QuestionEntryCount}|products=added:{result.ProductCount}|updated:{result.UpdatedProductCount}|unchanged:{result.UnchangedProductCount}|assets:{result.AssetCount}");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"NpmDataImporter failed: {exception.Message}");
    Environment.ExitCode = 1;
}

sealed record ImportOptions(
    string Project,
    string Artifacts,
    string Products,
    string MediaRoot,
    string Connection,
    int ArtifactPerCategory,
    int ProductLimit,
    string ApprovalToken,
    bool Apply,
    bool SyncShop,
    bool SyncQuestionBank,
    bool Help)
{
    public static string HelpText =>
        "NpmDataImporter --project <QMAH root|QMAH.Web> --artifacts <json> --media-root <wwwroot\\media> [--products <json>] [--artifact-per-category <正整數>] [--max-products <正整數> | --skip-products] [--no-question-bank] [--apply --approve <預檢確認碼>]";

    public static ImportOptions Parse(string[] arguments)
    {
        if (arguments.Length == 0 || arguments.Contains("--help", StringComparer.OrdinalIgnoreCase))
            return new(".", ".", ".", ".", "", 32, 256, "", false, true, true, true);

        string Value(params string[] names)
        {
            foreach (var name in names)
            {
                var index = Array.FindIndex(arguments, value =>
                    string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && index + 1 < arguments.Length)
                    return arguments[index + 1];
            }

            return "";
        }

        static int Number(string value, int fallback) =>
            int.TryParse(value, out var parsed) && parsed >= 1
                ? parsed
                : fallback;

        var project = Value("--project", "--qmah-root");
        var artifacts = Value("--artifacts", "--artifact-file");
        var mediaRoot = Value("--media-root", "--media");
        if (string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(artifacts)
            || string.IsNullOrWhiteSpace(mediaRoot))
        {
            throw new ArgumentException("必須提供 --project、--artifacts 與 --media-root。");
        }

        var skipProducts = arguments.Contains("--skip-products", StringComparer.OrdinalIgnoreCase);
        var products = Value("--products", "--product-file");
        if (!skipProducts && string.IsNullOrWhiteSpace(products))
            throw new ArgumentException("未使用 --skip-products 時，必須提供 --products。");

        return new(
            Path.GetFullPath(project),
            Path.GetFullPath(artifacts),
            string.IsNullOrWhiteSpace(products) ? "" : Path.GetFullPath(products),
            Path.GetFullPath(mediaRoot),
            string.IsNullOrWhiteSpace(Value("--connection", "--connection-string"))
                ? "Server=(localdb)\\MSSQLLocalDB;Database=QMAH;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False"
                : Value("--connection", "--connection-string"),
            Number(Value("--artifact-per-category", "--artifacts-per-category"), 32),
            skipProducts ? 0 : Number(Value("--max-products"), 256),
            Value("--approve", "--approval-token"),
            arguments.Contains("--apply", StringComparer.OrdinalIgnoreCase),
            !skipProducts,
            !arguments.Contains("--no-question-bank", StringComparer.OrdinalIgnoreCase),
            false);
    }

    public static string ResolveProjectRoot(string value)
    {
        var path = Path.GetFullPath(value);
        if (File.Exists(path)
            && string.Equals(Path.GetFileName(path), "QMAH.Web.csproj", StringComparison.OrdinalIgnoreCase))
        {
            path = Directory.GetParent(path)?.Parent?.FullName ?? path;
        }
        else if (Directory.Exists(path)
            && File.Exists(Path.Combine(path, "QMAH.Web.csproj")))
        {
            path = Directory.GetParent(path)?.FullName ?? path;
        }

        return path;
    }

    public static string ResolveMediaRoot(string value)
    {
        var path = Path.GetFullPath(value);
        if (string.Equals(new DirectoryInfo(path).Name, "media", StringComparison.OrdinalIgnoreCase))
            return path;

        var nestedMedia = Path.Combine(path, "media");
        if (Directory.Exists(nestedMedia)
            || string.Equals(new DirectoryInfo(path).Name, "wwwroot", StringComparison.OrdinalIgnoreCase))
        {
            return nestedMedia;
        }

        return path;
    }
}
