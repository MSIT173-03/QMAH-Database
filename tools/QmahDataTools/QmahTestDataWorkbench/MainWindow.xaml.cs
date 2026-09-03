using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using QMAH.Infrastructure.Data;
using QMAH.Infrastructure.Models.Entities;

namespace QmahTestDataWorkbench;

public partial class MainWindow : Window
{
    private readonly string _repositoryRoot;
    private readonly ObservableCollection<ArtifactRow> _artifactRows = [];
    private readonly ObservableCollection<ProductRow> _productRows = [];
    private readonly ObservableCollection<LookupOption> _categories = [];
    private readonly ObservableCollection<LookupOption> _eras = [];
    private readonly ObservableCollection<LookupOption> _productArtifacts = [];
    private string _connectionString = QmahDatabaseConnectionResolver.DefaultConnectionString;
    private Guid? _editingArtifactId;
    private Guid? _editingProductId;
    private Process? _runningProcess;
    private CancellationTokenSource? _runCancellation;
    private bool _ignoreSelection;

    public MainWindow()
    {
        InitializeComponent();
        _repositoryRoot = FindRepositoryRoot();
        ConnectionBox.Text = _connectionString;
        CredentialsBox.Text = Path.Combine(RepositoryParent(_repositoryRoot), "QMAH.DemoCredentials.local.csv");
        ArtifactGrid.ItemsSource = _artifactRows;
        ProductGrid.ItemsSource = _productRows;
        ArtifactCategoryBox.ItemsSource = _categories;
        ArtifactEraBox.ItemsSource = _eras;
        ProductArtifactBox.ItemsSource = _productArtifacts;
        ResetArtifactForm();
        ResetProductForm();
        AppendLog($"Repository 根目錄：{_repositoryRoot}");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await DetectDatabaseAsync();
    }

    private async void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        await DetectDatabaseAsync();
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDatabaseAsync(loadRows: false);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDatabaseAsync(loadRows: true);
    }

    private async Task DetectDatabaseAsync()
    {
        try
        {
            SetBusy(true);
            var resolution = await QmahDatabaseConnectionResolver.ResolveAsync(
                ConnectionBox.Text,
                enableAutomaticDiscovery: true);
            _connectionString = resolution.ConnectionString;
            ConnectionBox.Text = _connectionString;
            DatabaseTargetText.Text = resolution.Target;
            AppendLog($"自動尋找完成：{resolution.Target}");
            if (resolution.FoundTargets.Count > 0)
                AppendLog($"找到的 QMAH 目標：{string.Join(", ", resolution.FoundTargets)}");
            else
                AppendLog("未找到可連線的 QMAH；目前保留預設連線字串。");

            await RefreshDatabaseAsync(loadRows: true);
        }
        catch (Exception exception)
        {
            DatabaseTargetText.Text = "尚未連線";
            AppendLog($"自動尋找失敗：{exception.Message}");
            ShowWarning($"無法自動尋找 QMAH：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshDatabaseAsync(bool loadRows)
    {
        try
        {
            SetBusy(true);
            await using var db = CreateDbContext();
            if (!await db.Database.CanConnectAsync())
            {
                DatabaseTargetText.Text = "無法連線";
                AppendLog("資料庫連線失敗；請確認 SQL Server instance 與 QMAH 資料庫。");
                if (loadRows)
                    ShowWarning("目前連線無法開啟 QMAH，沒有讀取資料。");
                return;
            }

            DatabaseTargetText.Text = DescribeConnection(_connectionString);
            var artifactCount = await db.Artifacts.CountAsync();
            var productCount = await db.Products.CountAsync();
            var userCount = await db.Users.CountAsync();
            AppendLog($"資料庫可用：文物 {artifactCount} 筆、商品 {productCount} 筆、會員 {userCount} 筆。");

            if (!loadRows)
                return;

            var artifacts = await db.Artifacts
                .AsNoTracking()
                .Include(item => item.Category)
                .Include(item => item.EraBucket)
                .OrderBy(item => item.ArtifactRef)
                .Take(500)
                .ToListAsync();
            var products = await db.Products
                .AsNoTracking()
                .Include(item => item.Artifact)
                .OrderBy(item => item.ExternalRef)
                .ThenBy(item => item.Name)
                .Take(500)
                .ToListAsync();
            var categories = await db.ArtifactCategories
                .AsNoTracking()
                .OrderBy(item => item.Code)
                .ToListAsync();
            var eras = await db.EraBuckets
                .AsNoTracking()
                .OrderBy(item => item.Code)
                .ToListAsync();

            _ignoreSelection = true;
            _artifactRows.Clear();
            foreach (var artifact in artifacts)
            {
                _artifactRows.Add(new ArtifactRow(
                    artifact.Id,
                    artifact.ArtifactRef,
                    artifact.Name,
                    artifact.CategoryId,
                    $"{artifact.Category.Code}｜{artifact.Category.Name}",
                    artifact.EraBucketId,
                    $"{artifact.EraBucket.Code}｜{artifact.EraBucket.Name}",
                    artifact.IsActive));
            }

            _productRows.Clear();
            foreach (var product in products)
            {
                _productRows.Add(new ProductRow(
                    product.Id,
                    product.ExternalRef ?? string.Empty,
                    product.Name,
                    product.ArtifactId,
                    product.Artifact?.Name ?? "未關聯文物",
                    product.Price.ToString("0.00", CultureInfo.InvariantCulture),
                    product.Stock,
                    product.IsActive));
            }

            _categories.Clear();
            foreach (var category in categories)
                _categories.Add(new LookupOption(category.Id, $"{category.Code}｜{category.Name}"));

            _eras.Clear();
            foreach (var era in eras)
                _eras.Add(new LookupOption(era.Id, $"{era.Code}｜{era.Name}"));

            _productArtifacts.Clear();
            _productArtifacts.Add(new LookupOption(Guid.Empty, "（不指定文物）"));
            foreach (var artifact in artifacts)
                _productArtifacts.Add(new LookupOption(artifact.Id, $"{artifact.ArtifactRef}｜{artifact.Name}"));
            _ignoreSelection = false;
        }
        catch (Exception exception)
        {
            _ignoreSelection = false;
            AppendLog($"資料整理失敗：{exception.Message}");
            if (loadRows)
                ShowWarning($"讀取資料失敗：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SeedUsersButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCredentialsFile())
            return;

        await RunReleaseCommandAsync(
            "建立／更新展示會員",
            "seed-showcase-users",
            BuildCredentialArguments());
    }

    private async void GenerateDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadGenerationOptions(
                out var postCount,
                out var orderCount,
                out var activityDays,
                out var pointTransactionCount,
                out var keyTransactionCount,
                out var keyProgressTransactionCount,
                out var seed))
            return;

        await RunReleaseCommandAsync(
            "產生關聯展示資料",
            "generate-showcase-data",
            [
                "--post-count", postCount.ToString(CultureInfo.InvariantCulture),
                "--order-count", orderCount.ToString(CultureInfo.InvariantCulture),
                "--activity-days", activityDays.ToString(CultureInfo.InvariantCulture),
                "--point-transaction-count", pointTransactionCount.ToString(CultureInfo.InvariantCulture),
                "--key-transaction-count", keyTransactionCount.ToString(CultureInfo.InvariantCulture),
                "--key-progress-transaction-count", keyProgressTransactionCount.ToString(CultureInfo.InvariantCulture),
                "--seed", seed.ToString(CultureInfo.InvariantCulture)
            ]);
    }

    private async void GenerateAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCredentialsFile()
            || !TryReadGenerationOptions(
                out var postCount,
                out var orderCount,
                out var activityDays,
                out var pointTransactionCount,
                out var keyTransactionCount,
                out var keyProgressTransactionCount,
                out var seed))
            return;

        var seedExitCode = await RunReleaseCommandAsync(
            "建立／更新展示會員",
            "seed-showcase-users",
            BuildCredentialArguments());
        if (seedExitCode != 0)
            return;

        await RunReleaseCommandAsync(
            "產生關聯展示資料",
            "generate-showcase-data",
            [
                "--post-count", postCount.ToString(CultureInfo.InvariantCulture),
                "--order-count", orderCount.ToString(CultureInfo.InvariantCulture),
                "--activity-days", activityDays.ToString(CultureInfo.InvariantCulture),
                "--point-transaction-count", pointTransactionCount.ToString(CultureInfo.InvariantCulture),
                "--key-transaction-count", keyTransactionCount.ToString(CultureInfo.InvariantCulture),
                "--key-progress-transaction-count", keyProgressTransactionCount.ToString(CultureInfo.InvariantCulture),
                "--seed", seed.ToString(CultureInfo.InvariantCulture)
            ]);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runCancellation?.Cancel();
            if (_runningProcess is { HasExited: false })
                _runningProcess.Kill(entireProcessTree: true);
            AppendLog("已要求停止目前命令。");
        }
        catch (Exception exception)
        {
            AppendLog($"停止命令失敗：{exception.Message}");
        }
    }

    private async Task<int> RunReleaseCommandAsync(
        string label,
        string command,
        IReadOnlyList<string> commandArguments)
    {
        if (_runningProcess is not null)
        {
            ShowWarning("已有資料命令正在執行。");
            return 1;
        }

        var releaseProject = Path.Combine(
            _repositoryRoot,
            "tools",
            "QmahDataTools",
            "QmahDatabaseRelease",
            "QmahDatabaseRelease.csproj");
        if (!File.Exists(releaseProject))
        {
            ShowWarning($"找不到資料庫產生器：{releaseProject}");
            return 1;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = _repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(releaseProject);
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.ArgumentList.Add("--connection");
        process.StartInfo.ArgumentList.Add(_connectionString);
        foreach (var argument in commandArguments)
            process.StartInfo.ArgumentList.Add(argument);

        _runningProcess = process;
        _runCancellation = new CancellationTokenSource();
        SetBusy(true);
        AppendLog($"開始：{label}");

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("無法啟動 dotnet。");

            var stdoutTask = ReadProcessOutputAsync(process.StandardOutput, "OUT");
            var stderrTask = ReadProcessOutputAsync(process.StandardError, "ERR");
            await process.WaitForExitAsync(_runCancellation.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            AppendLog($"{label}結束，ExitCode={process.ExitCode}");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{label}已取消。");
            return 1;
        }
        catch (Exception exception)
        {
            AppendLog($"{label}失敗：{exception.Message}");
            ShowWarning($"{label}失敗：{exception.Message}");
            return 1;
        }
        finally
        {
            process.Dispose();
            _runningProcess = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
            SetBusy(false);
        }
    }

    private async Task ReadProcessOutputAsync(StreamReader reader, string prefix)
    {
        while (await reader.ReadLineAsync() is { } line)
            AppendLog($"{prefix} | {line}");
    }

    private async void SaveArtifactButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var artifactRef = Required(ArtifactRefBox.Text, "文物編號");
            var name = Required(ArtifactNameBox.Text, "文物名稱");
            var categoryId = RequiredSelection(ArtifactCategoryBox, "分類");
            var eraBucketId = RequiredSelection(ArtifactEraBox, "年代分類");
            var imagePath = Required(ArtifactImageBox.Text, "主圖片路徑");
            var sourceUrl = Required(ArtifactSourceBox.Text, "來源網址");

            await using var db = CreateDbContext();
            if (!await db.Database.CanConnectAsync())
                throw new InvalidOperationException("目前連線無法開啟 QMAH。");

            var duplicate = await db.Artifacts
                .AnyAsync(item => item.ArtifactRef == artifactRef
                    && (!_editingArtifactId.HasValue || item.Id != _editingArtifactId.Value));
            if (duplicate)
                throw new InvalidOperationException($"文物編號已存在：{artifactRef}");

            Artifact artifact;
            if (_editingArtifactId is Guid id)
            {
                artifact = await db.Artifacts.SingleOrDefaultAsync(item => item.Id == id)
                    ?? throw new InvalidOperationException("選取的文物已不存在，請重新整理資料。");
            }
            else
            {
                artifact = new Artifact { Id = Guid.NewGuid() };
                db.Artifacts.Add(artifact);
            }

            artifact.ArtifactRef = artifactRef;
            artifact.Name = name;
            artifact.CategoryId = categoryId;
            artifact.EraBucketId = eraBucketId;
            artifact.EraTextOriginal = Optional(ArtifactEraTextBox.Text);
            artifact.CreatorDisplay = Optional(ArtifactCreatorBox.Text);
            artifact.Description = Optional(ArtifactDescriptionBox.Text);
            artifact.SizeText = Optional(ArtifactSizeBox.Text);
            artifact.PrimaryImagePath = imagePath;
            artifact.ThumbnailPath = Optional(ArtifactThumbnailBox.Text);
            artifact.SourceUrl = sourceUrl;
            artifact.LicenseCode = Optional(ArtifactLicenseBox.Text);
            artifact.AttributionText = Optional(ArtifactAttributionBox.Text);
            artifact.IsActive = ArtifactActiveBox.IsChecked == true;

            await db.SaveChangesAsync();
            AppendLog($"文物已儲存：{artifactRef}");
            await RefreshDatabaseAsync(loadRows: true);
            SelectArtifact(artifact.Id);
        }
        catch (Exception exception)
        {
            AppendLog($"文物儲存失敗：{exception.Message}");
            ShowWarning($"文物儲存失敗：{exception.Message}");
        }
    }

    private async void SaveProductButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = Required(ProductNameBox.Text, "商品名稱");
            var categoryCode = Required(ProductCategoryCodeBox.Text, "商品分類代碼");
            if (!decimal.TryParse(ProductPriceBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
                || price < 0)
                throw new InvalidOperationException("價格必須是大於或等於 0 的數字。");
            if (!int.TryParse(ProductStockBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock)
                || stock < 0)
                throw new InvalidOperationException("庫存必須是大於或等於 0 的整數。");

            var externalRef = Optional(ProductExternalRefBox.Text);
            var artifactId = SelectedOptionalGuid(ProductArtifactBox);
            await using var db = CreateDbContext();
            if (!await db.Database.CanConnectAsync())
                throw new InvalidOperationException("目前連線無法開啟 QMAH。");

            if (externalRef is not null)
            {
                var duplicate = await db.Products
                    .AnyAsync(item => item.ExternalRef == externalRef
                        && (!_editingProductId.HasValue || item.Id != _editingProductId.Value));
                if (duplicate)
                    throw new InvalidOperationException($"商品編號已存在：{externalRef}");
            }

            Product product;
            if (_editingProductId is Guid id)
            {
                product = await db.Products.SingleOrDefaultAsync(item => item.Id == id)
                    ?? throw new InvalidOperationException("選取的商品已不存在，請重新整理資料。");
            }
            else
            {
                product = new Product
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow
                };
                db.Products.Add(product);
            }

            product.ArtifactId = artifactId;
            product.CategoryCode = categoryCode;
            product.ExternalRef = externalRef;
            product.Name = name;
            product.Description = Optional(ProductDescriptionBox.Text);
            product.SizeText = Optional(ProductSizeBox.Text);
            product.Price = price;
            product.Stock = stock;
            product.PrimaryImagePath = Optional(ProductImageBox.Text);
            product.SourceUrl = Optional(ProductSourceBox.Text);
            product.IsActive = ProductActiveBox.IsChecked == true;
            product.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            AppendLog($"商品已儲存：{name}");
            await RefreshDatabaseAsync(loadRows: true);
            SelectProduct(product.Id);
        }
        catch (Exception exception)
        {
            AppendLog($"商品儲存失敗：{exception.Message}");
            ShowWarning($"商品儲存失敗：{exception.Message}");
        }
    }

    private async void ArtifactGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_ignoreSelection || ArtifactGrid.SelectedItem is not ArtifactRow row)
            return;

        _editingArtifactId = row.Id;
        ArtifactRefBox.Text = row.ArtifactRef;
        ArtifactNameBox.Text = row.Name;
        ArtifactCategoryBox.SelectedValue = row.CategoryId;
        ArtifactEraBox.SelectedValue = row.EraBucketId;
        await LoadArtifactDetails(row.Id);
    }

    private async void ProductGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_ignoreSelection || ProductGrid.SelectedItem is not ProductRow row)
            return;

        _editingProductId = row.Id;
        ProductExternalRefBox.Text = row.ExternalRef;
        ProductNameBox.Text = row.Name;
        ProductArtifactBox.SelectedValue = row.ArtifactId ?? Guid.Empty;
        ProductPriceBox.Text = row.Price;
        ProductStockBox.Text = row.Stock.ToString(CultureInfo.InvariantCulture);
        ProductActiveBox.IsChecked = row.IsActive;
        await LoadProductDetailsAsync(row.Id);
    }

    private async void NewArtifactButton_Click(object sender, RoutedEventArgs e)
    {
        ResetArtifactForm();
        await Task.CompletedTask;
    }

    private async void NewProductButton_Click(object sender, RoutedEventArgs e)
    {
        ResetProductForm();
        await Task.CompletedTask;
    }

    private void BrowseCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV 檔案|*.csv|所有檔案|*.*",
            CheckFileExists = false,
            FileName = Path.GetFileName(CredentialsBox.Text)
        };
        if (dialog.ShowDialog(this) == true)
            CredentialsBox.Text = dialog.FileName;
    }

    private void CreateCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = Path.GetFullPath(CredentialsBox.Text.Trim());
            var template = Path.Combine(_repositoryRoot, "QMAH.DemoCredentials.csv");
            if (!File.Exists(template))
                throw new FileNotFoundException("找不到帳密範本。", template);
            if (File.Exists(target))
            {
                AppendLog($"已開啟既有本機帳密檔編輯器：{target}");
                OpenCredentialsEditor(target);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(template, target);
            AppendLog($"已由版本庫範本建立本機帳密檔：{target}");
            OpenCredentialsEditor(target);
        }
        catch (Exception exception)
        {
            ShowWarning($"建立或編輯本機帳密檔失敗：{exception.Message}");
        }
    }

    private void OpenCredentialsEditor(string target)
    {
        var editor = new CredentialEditorWindow(
            _repositoryRoot,
            target,
            Path.Combine(RepositoryParent(_repositoryRoot), "QMAH.DemoCredentials.local.backup.csv"))
        {
            Owner = this
        };
        if (editor.ShowDialog() == true)
        {
            CredentialsBox.Text = editor.SavedPath;
            AppendLog($"本機帳密檔已儲存：{editor.SavedPath}");
        }
    }

    private void OpenCredentialsButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.GetFullPath(CredentialsBox.Text.Trim());
        if (!File.Exists(path))
        {
            ShowWarning($"找不到檔案：{path}");
            return;
        }

        OpenPath(path);
    }

    private async Task LoadArtifactDetails(Guid id)
    {
        try
        {
            await using var db = CreateDbContext();
            var artifact = await db.Artifacts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);
            if (artifact is null)
                return;

            ArtifactEraTextBox.Text = artifact.EraTextOriginal ?? string.Empty;
            ArtifactCreatorBox.Text = artifact.CreatorDisplay ?? string.Empty;
            ArtifactDescriptionBox.Text = artifact.Description ?? string.Empty;
            ArtifactSizeBox.Text = artifact.SizeText ?? string.Empty;
            ArtifactImageBox.Text = artifact.PrimaryImagePath;
            ArtifactThumbnailBox.Text = artifact.ThumbnailPath ?? string.Empty;
            ArtifactSourceBox.Text = artifact.SourceUrl;
            ArtifactLicenseBox.Text = artifact.LicenseCode ?? string.Empty;
            ArtifactAttributionBox.Text = artifact.AttributionText ?? string.Empty;
            ArtifactActiveBox.IsChecked = artifact.IsActive;
        }
        catch (Exception exception)
        {
            AppendLog($"讀取文物詳細資料失敗：{exception.Message}");
        }
    }

    private async Task LoadProductDetailsAsync(Guid id)
    {
        try
        {
            await using var db = CreateDbContext();
            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);
            if (product is null)
                return;

            ProductCategoryCodeBox.Text = product.CategoryCode;
            ProductDescriptionBox.Text = product.Description ?? string.Empty;
            ProductSizeBox.Text = product.SizeText ?? string.Empty;
            ProductImageBox.Text = product.PrimaryImagePath ?? string.Empty;
            ProductSourceBox.Text = product.SourceUrl ?? string.Empty;
        }
        catch (Exception exception)
        {
            AppendLog($"讀取商品詳細資料失敗：{exception.Message}");
        }
    }

    private void ResetArtifactForm()
    {
        _editingArtifactId = null;
        ArtifactGrid.SelectedItem = null;
        ArtifactRefBox.Text = string.Empty;
        ArtifactNameBox.Text = string.Empty;
        ArtifactEraTextBox.Text = string.Empty;
        ArtifactCreatorBox.Text = string.Empty;
        ArtifactDescriptionBox.Text = string.Empty;
        ArtifactSizeBox.Text = string.Empty;
        ArtifactImageBox.Text = "/media/test-artifact.png";
        ArtifactThumbnailBox.Text = string.Empty;
        ArtifactSourceBox.Text = "https://example.invalid/qmah-test-artifact";
        ArtifactLicenseBox.Text = "TEST";
        ArtifactAttributionBox.Text = "QMAH 測試資料";
        ArtifactActiveBox.IsChecked = true;
        if (_categories.Count > 0)
            ArtifactCategoryBox.SelectedIndex = 0;
        if (_eras.Count > 0)
            ArtifactEraBox.SelectedIndex = 0;
    }

    private void ResetProductForm()
    {
        _editingProductId = null;
        ProductGrid.SelectedItem = null;
        ProductNameBox.Text = string.Empty;
        ProductExternalRefBox.Text = string.Empty;
        ProductCategoryCodeBox.Text = "TEST";
        ProductPriceBox.Text = "0";
        ProductStockBox.Text = "0";
        ProductSizeBox.Text = string.Empty;
        ProductImageBox.Text = string.Empty;
        ProductSourceBox.Text = string.Empty;
        ProductDescriptionBox.Text = string.Empty;
        ProductActiveBox.IsChecked = true;
        if (_productArtifacts.Count > 0)
            ProductArtifactBox.SelectedIndex = 0;
    }

    private void SelectArtifact(Guid id)
    {
        var row = _artifactRows.FirstOrDefault(item => item.Id == id);
        if (row is not null)
            ArtifactGrid.SelectedItem = row;
    }

    private void SelectProduct(Guid id)
    {
        var row = _productRows.FirstOrDefault(item => item.Id == id);
        if (row is not null)
            ProductGrid.SelectedItem = row;
    }

    private IReadOnlyList<string> BuildCredentialArguments() =>
    [
        "--credentials", Path.GetFullPath(CredentialsBox.Text.Trim()),
        "--backup", Path.Combine(RepositoryParent(_repositoryRoot), "QMAH.DemoCredentials.local.backup.csv")
    ];

    private bool ValidateCredentialsFile()
    {
        var path = Path.GetFullPath(CredentialsBox.Text.Trim());
        if (File.Exists(path))
            return true;

        ShowWarning($"找不到展示帳密檔：{path}。先建立本機檔並填妥 Password。");
        return false;
    }

    private bool TryReadGenerationOptions(
        out int postCount,
        out int orderCount,
        out int activityDays,
        out int pointTransactionCount,
        out int keyTransactionCount,
        out int keyProgressTransactionCount,
        out int seed)
    {
        postCount = 0;
        orderCount = 0;
        activityDays = 0;
        pointTransactionCount = 0;
        keyTransactionCount = 0;
        keyProgressTransactionCount = 0;
        seed = 0;
        if (!int.TryParse(PostCountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out postCount)
            || postCount is < 1 or > 512)
        {
            ShowWarning("社群貼文數必須是 1 到 512 的整數。");
            return false;
        }

        if (!int.TryParse(OrderCountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out orderCount)
            || orderCount is < 1 or > 512)
        {
            ShowWarning("商城訂單數必須是 1 到 512 的整數。");
            return false;
        }

        if (!int.TryParse(ActivityDaysBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out activityDays)
            || activityDays is < 0 or > 3650)
        {
            ShowWarning("每日活動天數必須是 0 到 3,650 的整數。0 表示不新增每日活動。");
            return false;
        }

        if (!int.TryParse(PointTransactionCountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pointTransactionCount)
            || pointTransactionCount is < 0 or > 10000)
        {
            ShowWarning("點數流水筆數必須是 0 到 10,000 的整數。0 表示不新增點數流水。");
            return false;
        }

        if (!int.TryParse(KeyTransactionCountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out keyTransactionCount)
            || keyTransactionCount is < 0 or > 10000)
        {
            ShowWarning("鑰匙流水筆數必須是 0 到 10,000 的整數。0 表示不新增鑰匙流水。");
            return false;
        }

        if (!int.TryParse(KeyProgressTransactionCountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out keyProgressTransactionCount)
            || keyProgressTransactionCount is < 0 or > 10000)
        {
            ShowWarning("鑰匙進度流水筆數必須是 0 到 10,000 的整數。0 表示不新增鑰匙進度流水。");
            return false;
        }

        if (!int.TryParse(SeedBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seed)
            || seed < 0)
        {
            ShowWarning("固定 seed 必須是大於或等於 0 的整數。");
            return false;
        }

        return true;
    }

    private QmahDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<QmahDbContext>()
            .UseSqlServer(_connectionString)
            .Options;
        return new QmahDbContext(options);
    }

    private static string DescribeConnection(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return $"{builder.DataSource};Database={builder.InitialCatalog}";
        }
        catch
        {
            return "連線設定無效";
        }
    }

    private static string RepositoryParent(string repositoryRoot) =>
        Directory.GetParent(repositoryRoot)?.FullName ?? repositoryRoot;

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "QMAH.DatabaseTools.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{label}不可空白。");
        return value.Trim();
    }

    private static string? Optional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid RequiredSelection(ComboBox comboBox, string label) =>
        comboBox.SelectedValue is Guid value && value != Guid.Empty
            ? value
            : throw new InvalidOperationException($"請選擇{label}。");

    private static Guid? SelectedOptionalGuid(ComboBox comboBox) =>
        comboBox.SelectedValue is Guid value && value != Guid.Empty ? value : null;

    private void SetBusy(bool busy)
    {
        AutoDetectButton.IsEnabled = !busy;
        TestConnectionButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        SeedUsersButton.IsEnabled = !busy;
        GenerateDataButton.IsEnabled = !busy;
        GenerateAllButton.IsEnabled = !busy;
        SaveArtifactButton.IsEnabled = !busy;
        SaveProductButton.IsEnabled = !busy;
        CancelButton.IsEnabled = _runningProcess is not null;
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "QMAH 測試資料工作台", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private sealed record LookupOption(Guid Value, string Label);

    private sealed record ArtifactRow(
        Guid Id,
        string ArtifactRef,
        string Name,
        Guid CategoryId,
        string CategoryLabel,
        Guid EraBucketId,
        string EraLabel,
        bool IsActive);

    private sealed record ProductRow(
        Guid Id,
        string ExternalRef,
        string Name,
        Guid? ArtifactId,
        string ArtifactLabel,
        string Price,
        int Stock,
        bool IsActive);
}
