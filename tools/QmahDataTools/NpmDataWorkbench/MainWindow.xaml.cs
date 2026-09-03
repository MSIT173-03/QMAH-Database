using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace NpmDataWorkbench;

public partial class MainWindow : Window
{
    private readonly string _toolsRoot;
    private readonly string _sourceToolsRoot;
    private readonly ObservableCollection<ShopCategoryOption> _shopCategories = [];
    private readonly Dictionary<string, int> _artifactSourceCounts = new(StringComparer.OrdinalIgnoreCase);
    private Process? _runningProcess;
    private CancellationTokenSource? _processCancellation;

    public MainWindow()
    {
        InitializeComponent();
        _toolsRoot = FindToolsRoot();
        _sourceToolsRoot = FindSourceToolsRoot();
        var outputRoot = ResolveDefaultOutputRoot();
        RootPathText.Text = _toolsRoot;
        OutputPathBox.Text = outputRoot;
        MediaPathBox.Text = Path.Combine(outputRoot, "media");
        PipelinePathBox.Text = ResolveRunnerPath("NpmArtifactPipeline");
        ImporterPathBox.Text = ResolveRunnerPath("NpmDataImporter");
        ProjectRootBox.Text = FindProjectRoot();
        ArtifactImportPathBox.Text = Path.Combine(outputRoot, "current", "artifacts.import.json");
        ProductImportPathBox.Text = Path.Combine(outputRoot, "products", "products.import.json");
        ShopCollectorPathBox.Text = ResolveRunnerPath("NpmShopSampleCollector");
        ShopSettingsPathBox.Text = Path.Combine(_sourceToolsRoot, "NpmShopSampleCollector", "sample-settings.json");
        SourceCatalogPathBox.Text = Path.Combine(_sourceToolsRoot, "NpmShopSampleCollector", "shop-source-catalog.json");
        ShopDelayBox.Text = "600";
        ShopCooldownEveryBox.Text = "30";
        ShopCooldownMsBox.Text = "10000";
        ShopMaxPagesBox.Text = "3";
        ArtifactReadableBox.SelectedIndex = 0;
        ShopReadableBox.SelectedIndex = 0;
        LoadDefaultArtifactPreset();
        ShopCategoriesList.ItemsSource = _shopCategories;
        LoadShopCategories();
        LoadCatalogStatus();
        AppendLog($"工作根目錄：{_toolsRoot}");
        AppendLog($"工具設定來源：{_sourceToolsRoot}");
        AppendLog("GUI 已就緒。建議先按「偵測 API 筆數」或「偵測所選分類商品量」。");
        UpdateArtifactTargetTotal();
    }

    private void BrowseOutputButton_Click(object sender, RoutedEventArgs e) => BrowseFolder(OutputPathBox, "選擇文物輸出資料夾");
    private void BrowseMediaButton_Click(object sender, RoutedEventArgs e) => BrowseFolder(MediaPathBox, "選擇圖片資料夾");
    private void BrowsePipelineButton_Click(object sender, RoutedEventArgs e) => BrowseFile(PipelinePathBox, "執行檔或專案|*.exe;*.csproj;*.dll|所有檔案|*.*");

    private void BrowseArtifactOfflineInputButton_Click(object sender, RoutedEventArgs e) => BrowseFile(ArtifactOfflineInputBox, "文物匯入資料|*.json|所有檔案|*.*");
    private void BrowseImporterButton_Click(object sender, RoutedEventArgs e) => BrowseFile(ImporterPathBox, "執行檔或專案|*.exe;*.csproj;*.dll|所有檔案|*.*");
    private void BrowseProjectRootButton_Click(object sender, RoutedEventArgs e) => BrowseFolder(ProjectRootBox, "選擇含 QMAH.Web 的專案根目錄");
    private void BrowseArtifactImportButton_Click(object sender, RoutedEventArgs e) => BrowseFile(ArtifactImportPathBox, "文物匯入資料|*.json|所有檔案|*.*");
    private void BrowseProductImportButton_Click(object sender, RoutedEventArgs e) => BrowseFile(ProductImportPathBox, "商品匯入資料|*.json|所有檔案|*.*");
    private void BrowseShopCollectorButton_Click(object sender, RoutedEventArgs e) => BrowseFile(ShopCollectorPathBox, "執行檔或專案|*.exe;*.csproj;*.dll|所有檔案|*.*");

    private void BrowseShopSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseFile(ShopSettingsPathBox, "JSON 設定檔|*.json|所有檔案|*.*");
        LoadShopCategories();
    }

    private void BrowseSourceCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseFile(SourceCatalogPathBox, "商城商品分類 JSON|*.json|所有檔案|*.*");
        LoadShopCategories();
        LoadCatalogStatus();
    }

    private async void EstimateArtifactButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _artifactSourceCounts.Clear();
            ArtifactSourceLimitText.Text = "正在取得八類來源筆數";
            var args = new List<string> { "--estimate-only" };
            await RunProcessAsync(CreateToolStartInfo(PipelinePathBox.Text, args), "文物 API 偵測");
        }
        catch (Exception ex)
        {
            ShowWarning($"無法啟動文物 API 偵測：{ex.Message}");
        }
    }

    private async void RunArtifactButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var args = new List<string>
            {
                "--bronze", ReadCount(BronzeCountBox, "銅器"),
                "--ceramic", ReadCount(CeramicCountBox, "陶瓷"),
                "--jade", ReadCount(JadeCountBox, "玉器"),
                "--enamel", ReadCount(EnamelCountBox, "琺瑯器"),
                "--lacquer", ReadCount(LacquerCountBox, "漆器"),
                "--carvings", ReadCount(CarvingCountBox, "雕刻"),
                "--painting", ReadCount(PaintingCountBox, "繪畫"),
                "--coins", ReadCount(CoinsCountBox, "錢幣"),
                "--output", Path.Combine(OutputPathBox.Text.Trim(), "current"),
                "--media-root", MediaPathBox.Text.Trim(),
                "--selection-mode", SelectedArtifactSelectionMode(),
                "--seed", ReadNonNegativeInt(ArtifactSeedBox, "文物固定 seed", int.MaxValue),
                "--readable", SelectedReadable(ArtifactReadableBox)
            };
            if (DownloadImagesCheckBox.IsChecked != true)
                args.Add("--no-images");
            AddOptional(args, "--offline-input", ArtifactOfflineInputBox.Text);
            await RunProcessAsync(CreateToolStartInfo(PipelinePathBox.Text, args), "文物資料收集");
        }
        catch (Exception ex)
        {
            ShowWarning($"文物設定或啟動失敗：{ex.Message}");
        }
    }

    private void ArtifactCountBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateArtifactTargetTotal();

    private void LoadArtifactPresetButton_Click(object sender, RoutedEventArgs e) => LoadDefaultArtifactPreset(true);

    private void LoadDefaultArtifactPreset(bool showWarningOnFailure = false)
    {
        try
        {
            var presetPath = Path.Combine(AppContext.BaseDirectory, "presets", "default-1-256.json");
            if (!File.Exists(presetPath))
                throw new FileNotFoundException("找不到預設檔", presetPath);

            var preset = JsonSerializer.Deserialize<ArtifactWorkbenchPreset>(
                File.ReadAllText(presetPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (preset is null)
                throw new InvalidOperationException("預設檔內容為空。");

            foreach (var input in ArtifactCountInputs())
            {
                if (!preset.ArtifactCounts.TryGetValue(input.Code, out var count) || count < 0)
                    throw new InvalidOperationException($"預設檔缺少 {input.Code} 的非負整數目標。");
                input.Box.Text = count.ToString();
            }

            SelectComboBoxTag(ArtifactSelectionModeBox, preset.SelectionMode, "選取模式");
            if (preset.Seed < 0)
                throw new InvalidOperationException("預設檔的 seed 不得為負數。");
            ArtifactSeedBox.Text = preset.Seed.ToString();
            SelectComboBoxTag(ArtifactReadableBox, preset.Readable, "人類可讀預覽格式");
            DownloadImagesCheckBox.IsChecked = preset.DownloadImages;
            if (preset.ImportArtifactPerCategory < 1 || preset.ImportMaxProducts < 1)
                throw new InvalidOperationException("預設檔的匯入上限必須是正整數。");
            ArtifactPerCategoryBox.Text = preset.ImportArtifactPerCategory.ToString();
            MaxProductsBox.Text = preset.ImportMaxProducts.ToString();
            ArtifactSourceLimitText.Text = $"已載入{preset.Name}：{ArtifactCountBoxes().Sum(box => int.Parse(box.Text)):N0} 件";
            UpdateArtifactTargetTotal();

            if (showWarningOnFailure)
                AppendLog($"已載入{preset.Name}：{preset.Description}");
        }
        catch (Exception ex)
        {
            SetArtifactCounts(32);
            ArtifactSelectionModeBox.SelectedIndex = 0;
            ArtifactSeedBox.Text = "173";
            ArtifactReadableBox.SelectedIndex = 0;
            DownloadImagesCheckBox.IsChecked = true;
            ArtifactPerCategoryBox.Text = "32";
            MaxProductsBox.Text = "256";
            ArtifactSourceLimitText.Text = "預設 1 無法載入，已使用內建 256 件基準";
            if (showWarningOnFailure)
                ShowWarning($"無法載入預設 1：{ex.Message}");
        }
    }

    private static void SelectComboBoxTag(ComboBox comboBox, string tag, string label)
    {
        var item = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            throw new InvalidOperationException($"{label}「{tag}」不存在。");
        comboBox.SelectedItem = item;
    }

    private void SetArtifactBaselineButton_Click(object sender, RoutedEventArgs e) => SetArtifactCounts(32);

    private void SetArtifactSourceLimitButton_Click(object sender, RoutedEventArgs e)
    {
        var inputs = ArtifactCountInputs().ToArray();
        if (inputs.Any(input => !_artifactSourceCounts.ContainsKey(input.Code)))
        {
            ShowWarning("請先按「偵測 API 筆數」，取得八個正式分類的來源筆數。來源筆數是原始資料上限，最後可匯入筆數仍會受欄位、年代與圖片品質規則影響。");
            return;
        }

        foreach (var input in inputs)
            input.Box.Text = _artifactSourceCounts[input.Code].ToString();

        ArtifactSourceLimitText.Text = "已套用最近一次偵測的八類原始筆數";
        UpdateArtifactTargetTotal();
    }

    private void SetArtifactCounts(int count)
    {
        foreach (var box in ArtifactCountBoxes())
            box.Text = count.ToString();
        UpdateArtifactTargetTotal();
    }

    private void UpdateArtifactTargetTotal()
    {
        if (ArtifactTargetTotalText is null)
            return;

        var total = ArtifactCountBoxes()
            .Select(box => int.TryParse(box.Text.Trim(), out var value) && value >= 0 ? (long)value : 0L)
            .Sum();
        ArtifactTargetTotalText.Text = $"{total:N0} 件";
    }

    private IEnumerable<(string Code, TextBox Box)> ArtifactCountInputs() =>
    [
        ("BRONZE", BronzeCountBox),
        ("CERAMIC", CeramicCountBox),
        ("JADE", JadeCountBox),
        ("ENAMEL", EnamelCountBox),
        ("LACQUER", LacquerCountBox),
        ("COIN", CoinsCountBox),
        ("CARVING", CarvingCountBox),
        ("PAINTING", PaintingCountBox)
    ];

    private IEnumerable<TextBox> ArtifactCountBoxes() =>
    [
        BronzeCountBox,
        CeramicCountBox,
        JadeCountBox,
        EnamelCountBox,
        LacquerCountBox,
        CoinsCountBox,
        CarvingCountBox,
        PaintingCountBox
    ];

    private async void VerifyEraRulesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunProcessAsync(CreateToolStartInfo(PipelinePathBox.Text, ["--verify-era-rules"]), "年代規則自測");
        }
        catch (Exception ex)
        {
            ShowWarning($"無法執行年代規則自測：{ex.Message}");
        }
    }

    private async void PrecheckImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunProcessAsync(CreateToolStartInfo(ImporterPathBox.Text, ImportArguments(apply: false)), "資料匯入預檢");
        }
        catch (Exception ex)
        {
            ShowWarning($"匯入預檢無法啟動：{ex.Message}");
        }
    }

    private async void ApplyImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ImportApprovalTokenBox.Text))
            {
                ShowWarning("請先執行預檢，確認資料筆數與重複項目後再寫入。");
                return;
            }

            var confirmation = MessageBox.Show(this,
                "即將只新增預檢通過的文物、商品與圖片；同一 ArtifactRef 或 ExternalRef 的既有資料會略過，絕不覆寫。要繼續嗎？",
                "確認匯入 QMAH", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
                return;

            await RunProcessAsync(CreateToolStartInfo(ImporterPathBox.Text, ImportArguments(apply: true)), "資料寫入 QMAH");
        }
        catch (Exception ex)
        {
            ShowWarning($"資料寫入無法啟動：{ex.Message}");
        }
    }

    private List<string> ImportArguments(bool apply)
    {
        var project = ProjectRootBox.Text.Trim();
        var artifacts = ArtifactImportPathBox.Text.Trim();
        var products = ProductImportPathBox.Text.Trim();
        var media = MediaPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(artifacts)
            || string.IsNullOrWhiteSpace(products) || string.IsNullOrWhiteSpace(media))
            throw new InvalidOperationException("請完整指定專案根目錄、文物 JSON、商品 JSON 與圖片資料夾。");

        var args = new List<string>
        {
            "--project", project,
            "--artifacts", artifacts,
            "--products", products,
            "--media-root", media,
            "--artifact-per-category", ReadBoundedInt(ArtifactPerCategoryBox, "每類文物匯入上限", 1, int.MaxValue),
            "--max-products", ReadBoundedInt(MaxProductsBox, "商品匯入上限", 1, int.MaxValue)
        };
        if (apply)
        {
            args.Add("--apply");
            args.Add("--approve");
            args.Add(ImportApprovalTokenBox.Text.Trim());
        }
        return args;
    }

    private async void EstimateShopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var categories = SelectedShopCategories();
            if (categories.Count == 0)
            {
                ShowWarning("請至少選擇一個商城分類。");
                return;
            }

            var args = new List<string>
            {
                "--estimate-only",
                "--source-categories", string.Join(',', categories.Select(x => x.Code))
            };
            AddShopPacingArguments(args);
            AddOptional(args, "--settings", ShopSettingsPathBox.Text);
            await RunProcessAsync(CreateToolStartInfo(ShopCollectorPathBox.Text, args), "商城分類偵測");
        }
        catch (Exception ex)
        {
            ShowWarning($"商城設定或啟動失敗：{ex.Message}");
        }
    }

    private async void RefreshCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var args = new List<string>
            {
                "--discover-structure",
                "--source-catalog", SourceCatalogPathBox.Text.Trim()
            };
            AddOptional(args, "--settings", ShopSettingsPathBox.Text);
            await RunProcessAsync(CreateToolStartInfo(ShopCollectorPathBox.Text, args), "商城網站結構偵測");
            LoadShopCategories();
            LoadCatalogStatus();
        }
        catch (Exception ex)
        {
            ShowWarning($"商城網站結構偵測失敗：{ex.Message}");
        }
    }

    private async void RunShopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var categories = SelectedShopCategories();
            if (categories.Count == 0)
            {
                ShowWarning("請至少選擇一個商城分類。");
                return;
            }

            var target = ReadShopCount();
            var args = new List<string>
            {
                "--count", target.ToString(),
                "--source-categories", string.Join(',', categories.Select(x => x.Code)),
                "--output", Path.Combine(OutputPathBox.Text.Trim(), "products"),
                "--media-root", MediaPathBox.Text.Trim(),
                "--readable", SelectedReadable(ShopReadableBox)
            };
            AddShopPacingArguments(args);
            AddOptional(args, "--settings", ShopSettingsPathBox.Text);
            await RunProcessAsync(CreateToolStartInfo(ShopCollectorPathBox.Text, args), "商城商品收集");
        }
        catch (Exception ex)
        {
            ShowWarning($"商城設定或啟動失敗：{ex.Message}");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _processCancellation?.Cancel();
        try
        {
            if (_runningProcess is { HasExited: false })
                _runningProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendLog($"取消程序時發生例外：{ex.Message}", true);
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogBox.Document.Blocks.Clear();

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd).Text;
            Clipboard.SetText(text);
            AppendLog("日誌已複製到剪貼簿。");
        }
        catch (Exception ex)
        {
            ShowWarning($"複製日誌失敗：{ex.Message}");
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e) => OpenPath(OutputPathBox.Text.Trim());

    private void OpenArtifactCsvButton_Click(object sender, RoutedEventArgs e) =>
        OpenPath(Path.Combine(OutputPathBox.Text.Trim(), "current", "import", "artifacts.csv"));

    private async Task RunProcessAsync(ProcessStartInfo startInfo, string jobName)
    {
        if (_runningProcess is not null)
        {
            ShowWarning("目前已有工作執行中，請先等待完成或按取消。");
            return;
        }

        SetBusy(true, jobName);
        MainTabs.SelectedIndex = 2;
        using var cancellation = new CancellationTokenSource();
        _processCancellation = cancellation;
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                Dispatcher.BeginInvoke(() => HandleProcessLine(eventArgs.Data, false));
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                Dispatcher.BeginInvoke(() => HandleProcessLine(eventArgs.Data, true));
        };
        process.Exited += (_, _) =>
        {
            try { completion.TrySetResult(process.ExitCode); }
            catch { completion.TrySetResult(-1); }
        };

        _runningProcess = process;
        AppendLog($"> {FormatCommand(startInfo)}");
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("程序未能啟動。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var exitCode = await completion.Task.WaitAsync(cancellation.Token);
            AppendLog($"{jobName}結束，ExitCode={exitCode}", exitCode == 0 ? false : true);
            StatusText.Text = exitCode == 0 ? "完成" : $"失敗（ExitCode={exitCode}）";
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{jobName}已取消。", true);
            StatusText.Text = "已取消";
        }
        catch (Exception ex)
        {
            AppendLog($"{jobName}啟動或執行失敗：{ex.Message}", true);
            StatusText.Text = "執行失敗";
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            if (ReferenceEquals(_runningProcess, process))
                _runningProcess = null;
            _processCancellation = null;
            SetBusy(false, "");
        }
    }

    private void HandleProcessLine(string line, bool isError)
    {
        var progress = Regex.Match(line, @"^PROGRESS\|[^|]+\|(\d+)\|");
        if (progress.Success && int.TryParse(progress.Groups[1].Value, out var percent))
            ProgressBar.Value = Math.Clamp(percent, 0, 100);

        var total = Regex.Match(line, @"ESTIMATE_SUMMARY\|(?:ARTIFACT|SHOP)\|total=(\d+)");
        if (total.Success && int.TryParse(total.Groups[1].Value, out var count))
        {
            if (line.Contains("|ARTIFACT|", StringComparison.Ordinal))
                ArtifactEstimateText.Text = $"API 原始資料合計 {count:N0} 筆";
            else
                ShopEstimateText.Text = $"所選分類約 {count:N0} 個商品連結";
        }

        var datasetEstimate = Regex.Match(line, @"^ESTIMATE\|(?<code>[^|]+)\|available=(?<count>\d+)\|");
        if (datasetEstimate.Success
            && int.TryParse(datasetEstimate.Groups["count"].Value, out var available))
        {
            _artifactSourceCounts[datasetEstimate.Groups["code"].Value] = available;
            ArtifactSourceLimitText.Text = $"已取得 {_artifactSourceCounts.Count}/8 類來源筆數";
        }

        var approval = Regex.Match(line, @"^APPROVAL_TOKEN\|([A-Fa-f0-9]+)$");
        if (approval.Success)
            ImportApprovalTokenBox.Text = approval.Groups[1].Value;

        AppendLog(line, isError);
    }

    private void SetBusy(bool busy, string jobName)
    {
        RunArtifactButton.IsEnabled = !busy;
        EstimateArtifactButton.IsEnabled = !busy;
        RefreshCatalogButton.IsEnabled = !busy;
        RunShopButton.IsEnabled = !busy;
        EstimateShopButton.IsEnabled = !busy;
        PrecheckImportButton.IsEnabled = !busy;
        ApplyImportButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        if (busy)
        {
            ProgressBar.Value = 0;
            StatusText.Text = jobName + "…";
        }
    }

    private void AppendLog(string message, bool isError = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message, isError));
            return;
        }

        var run = new Run($"[{DateTime.Now:HH:mm:ss}] {message}")
        {
            Foreground = LogBrush(message, isError),
            FontWeight = IsImportant(message, isError) ? FontWeights.SemiBold : FontWeights.Normal
        };
        var paragraph = new Paragraph(run) { Margin = new Thickness(0, 0, 0, 3) };
        LogBox.Document.Blocks.Add(paragraph);
        LogBox.ScrollToEnd();
    }

    private static Brush LogBrush(string message, bool isError)
    {
        if (isError || message.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || message.Contains("error", StringComparison.OrdinalIgnoreCase))
            return Brushes.Firebrick;
        if (message.Contains("SUMMARY", StringComparison.OrdinalIgnoreCase) || message.Contains("ExitCode=0", StringComparison.OrdinalIgnoreCase) || message.Contains("Completed", StringComparison.OrdinalIgnoreCase) || message.Contains("Collected", StringComparison.OrdinalIgnoreCase))
            return Brushes.SeaGreen;
        if (message.Contains("ESTIMATE", StringComparison.OrdinalIgnoreCase) || message.StartsWith("MODE", StringComparison.OrdinalIgnoreCase))
            return Brushes.DarkViolet;
        if (message.StartsWith("PROGRESS|", StringComparison.OrdinalIgnoreCase))
            return Brushes.DarkCyan;
        if (message.StartsWith("FETCH|", StringComparison.OrdinalIgnoreCase) || message.StartsWith("DISCOVER|", StringComparison.OrdinalIgnoreCase))
            return Brushes.DodgerBlue;
        return (Brush)Application.Current.FindResource("InkBrush");
    }

    private static bool IsImportant(string message, bool isError) =>
        isError || message.Contains("SUMMARY", StringComparison.OrdinalIgnoreCase) || message.Contains("ExitCode", StringComparison.OrdinalIgnoreCase);

    private void LoadShopCategories()
    {
        _shopCategories.Clear();
        if (TryLoadCatalogCategories() || TryLoadSettingsSourceCategories())
        {
            LoadShopPacingSettings();
            return;
        }

        AppendLog("找不到可用的商城商品分類，改用內建預設分類。", true);
        LoadDefaultShopCategories();
        LoadShopPacingSettings();
    }

    private bool TryLoadCatalogCategories()
    {
        var path = SourceCatalogPathBox.Text.Trim();
        if (!File.Exists(path))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("categories", out var categories)
                || categories.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in categories.EnumerateArray())
            {
                var code = item.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
                var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var mapped = item.TryGetProperty("mappedCategoryCode", out var mappedElement)
                    ? mappedElement.GetString()
                    : null;
                var observed = item.TryGetProperty("observedProductLinks", out var observedElement)
                                && observedElement.TryGetInt32(out var count)
                    ? count
                    : 0;
                if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(name))
                    _shopCategories.Add(new ShopCategoryOption(code.Trim(), name.Trim(), true, observed, mapped));
            }

            if (_shopCategories.Count == 0)
                return false;

            AppendLog($"已載入 {_shopCategories.Count} 個商城商品分類（來自商品分類 JSON）。");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"商品分類 JSON 無法讀取：{ex.Message}，改用設定檔分類。", true);
            _shopCategories.Clear();
            return false;
        }
    }

    private bool TryLoadSettingsSourceCategories()
    {
        var path = ShopSettingsPathBox.Text.Trim();
        if (!File.Exists(path))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("sourceEntries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in entries.EnumerateArray())
            {
                var code = item.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
                var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var mapped = item.TryGetProperty("categoryCode", out var mappedElement)
                    ? mappedElement.GetString()
                    : null;
                var enabled = !item.TryGetProperty("enabled", out var enabledElement)
                              || enabledElement.ValueKind != JsonValueKind.False;
                if (!enabled || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                    continue;
                _shopCategories.Add(new ShopCategoryOption(code.Trim(), name.Trim(), true, 0, mapped));
            }

            if (_shopCategories.Count == 0)
                return false;

            AppendLog($"已載入 {_shopCategories.Count} 個設定檔商城商品分類。");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"商城設定檔無法讀取：{ex.Message}。", true);
            _shopCategories.Clear();
            return false;
        }
    }

    private void LoadDefaultShopCategories()
    {
        foreach (var (code, name) in DefaultShopCategories())
            _shopCategories.Add(new ShopCategoryOption(code, name, true));
        AppendLog($"已載入 {_shopCategories.Count} 個內建預設商城分類。");
    }

    private static IReadOnlyList<(string Code, string Name)> DefaultShopCategories() =>
    [
        ("ZC523", "典藏精品"),
        ("ZC7286630", "國寶特選"),
        ("ZC7263806", "故宮選粹"),
        ("ZC508", "書法繪畫"),
        ("ZC2154202", "多寶格系列"),
        ("ZC524", "陶瓷"),
        ("ZC535", "文房四寶與書法"),
        ("ZC545", "珍玩")
    ];

    private void LoadShopPacingSettings()
    {
        var path = ShopSettingsPathBox.Text.Trim();
        if (!File.Exists(path))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            SetOptionalInt(root, "throttleMilliseconds", ShopDelayBox, 0, 600000);
            SetOptionalInt(root, "cooldownEveryRequests", ShopCooldownEveryBox, 0, 1000000);
            SetOptionalInt(root, "cooldownMilliseconds", ShopCooldownMsBox, 0, 3600000);
            SetOptionalInt(root, "maxPages", ShopMaxPagesBox, 0, 1000000);
        }
        catch
        {
            // 分類載入已經有回退路徑；節流欄位保留畫面預設值即可。
        }
    }

    private static void SetOptionalInt(JsonElement root, string propertyName, TextBox target, int min, int max)
    {
        if (root.TryGetProperty(propertyName, out var element)
            && element.TryGetInt32(out var value))
            target.Text = Math.Clamp(value, min, max).ToString();
    }

    private void LoadCatalogStatus()
    {
        var path = SourceCatalogPathBox.Text.Trim();
        if (!File.Exists(path))
        {
            CatalogStatusText.Text = "商城商品分類 JSON 尚未建立；目前使用設定檔分類，設定檔也不可用時回退內建預設分類。";
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            var count = root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                ? categories.GetArrayLength()
                : 0;
            var observed = root.TryGetProperty("observedAtUtc", out var observedElement)
                ? observedElement.GetString() ?? ""
                : "";
            CatalogStatusText.Text = string.IsNullOrWhiteSpace(observed)
                ? $"商城商品分類 JSON 已建立，共 {count} 個商品分類；勾選的分類才會估算或抓取。"
                : $"商城商品分類 JSON：{count} 個商品分類，最近更新 {observed}；勾選的分類才會估算或抓取。";
        }
        catch (Exception ex)
        {
            CatalogStatusText.Text = $"商城商品分類 JSON 無法讀取（{ex.Message}）；仍使用設定檔／內建預設分類。";
        }
    }

    private List<ShopCategoryOption> SelectedShopCategories() =>
        _shopCategories.Where(x => x.IsSelected).ToList();

    private static string ReadCount(TextBox box, string label)
    {
        if (!int.TryParse(box.Text.Trim(), out var value) || value < 0)
            throw new InvalidOperationException($"{label}數量必須是 0 到 {int.MaxValue:N0} 的整數；實際可處理數量由來源資料筆數決定。");
        return value.ToString();
    }

    private static string ReadBoundedInt(TextBox box, string label, int minimum, int maximum)
    {
        if (!int.TryParse(box.Text.Trim(), out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{label}必須是 {minimum:N0} 到 {maximum:N0} 的整數。");
        return value.ToString();
    }

    private int ReadShopCount()
    {
        if (!int.TryParse(ShopCountBox.Text.Trim(), out var value) || value is < 1 or > 5000)
            throw new InvalidOperationException("目標商品數必須是 1 到 5000 的整數。");
        return value;
    }

    private void AddShopPacingArguments(List<string> args)
    {
        args.Add("--delay-ms");
        args.Add(ReadNonNegativeInt(ShopDelayBox, "每次請求延遲", 600000));
        args.Add("--cooldown-every");
        args.Add(ReadNonNegativeInt(ShopCooldownEveryBox, "每幾次冷卻", 1000000));
        args.Add("--cooldown-ms");
        args.Add(ReadNonNegativeInt(ShopCooldownMsBox, "冷卻時間", 3600000));
        args.Add("--max-pages");
        args.Add(ReadNonNegativeInt(ShopMaxPagesBox, "最大頁數", 1000000));
    }

    private static string ReadNonNegativeInt(TextBox box, string label, int max)
    {
        if (!int.TryParse(box.Text.Trim(), out var value) || value < 0 || value > max)
            throw new InvalidOperationException($"{label}必須是 0 到 {max:N0} 的整數。");
        return value.ToString();
    }

    private static string SelectedReadable(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";

    private string SelectedArtifactSelectionMode() =>
        (ArtifactSelectionModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "diverse";

    private static void AddOptional(List<string> args, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            args.Add(name);
            args.Add(value.Trim());
        }
    }

    private ProcessStartInfo CreateToolStartInfo(string selectedPath, IEnumerable<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            throw new InvalidOperationException("請先指定執行檔或專案路徑。");
        selectedPath = Path.GetFullPath(selectedPath.Trim());
        if (!File.Exists(selectedPath) && !Directory.Exists(selectedPath))
            throw new FileNotFoundException("找不到指定的執行檔或專案", selectedPath);

        var extension = Path.GetExtension(selectedPath);
        var isProject = string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase);
        var isDll = string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo
        {
            FileName = isProject || isDll ? "dotnet" : selectedPath,
            WorkingDirectory = _toolsRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (isProject)
        {
            info.ArgumentList.Add("run");
            info.ArgumentList.Add("--project");
            info.ArgumentList.Add(selectedPath);
            info.ArgumentList.Add("--");
        }
        else if (isDll)
        {
            info.ArgumentList.Add(selectedPath);
        }
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static string FormatCommand(ProcessStartInfo info) =>
        info.FileName + (info.ArgumentList.Count == 0 ? "" : " " + string.Join(' ', info.ArgumentList.Select(QuoteArgument)));

    private static string QuoteArgument(string value) =>
        value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private void BrowseFolder(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = ExistingDirectory(target.Text) };
        if (dialog.ShowDialog() == true)
            target.Text = dialog.FolderName;
    }

    private void BrowseFile(TextBox target, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇檔案",
            Filter = filter,
            CheckFileExists = true,
            InitialDirectory = ExistingDirectory(target.Text)
        };
        if (dialog.ShowDialog() == true)
            target.Text = dialog.FileName;
    }

    private static string ExistingDirectory(string value)
    {
        var path = value?.Trim() ?? "";
        if (File.Exists(path)) return Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        if (Directory.Exists(path)) return path;
        return Environment.CurrentDirectory;
    }

    private void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ShowWarning($"找不到路徑：{path}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowWarning($"無法開啟路徑：{ex.Message}");
        }
    }

    private void ShowWarning(string message) => MessageBox.Show(this, message, "NPM Data Workbench", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string FindToolsRoot()
    {
        var seeds = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var seed in seeds)
        {
            var directory = new DirectoryInfo(seed);
            for (var current = directory; current is not null; current = current.Parent)
            {
                var hasExecutables = File.Exists(Path.Combine(current.FullName, "NpmArtifactPipeline.exe"))
                                     && File.Exists(Path.Combine(current.FullName, "NpmShopSampleCollector.exe"));
                var hasProjects = Directory.Exists(Path.Combine(current.FullName, "NpmArtifactPipeline"))
                                  && Directory.Exists(Path.Combine(current.FullName, "NpmShopSampleCollector"));
                if (hasExecutables || hasProjects)
                    return current.FullName;
            }
        }
        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static string ResolveDefaultOutputRoot()
    {
        var configured = Environment.GetEnvironmentVariable("QMAH_TOOL_OUTPUT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (current.Name.Equals("_工具輸出", StringComparison.OrdinalIgnoreCase))
                return current.FullName;

            if (current.Name.Equals("00_五系統整合基準", StringComparison.OrdinalIgnoreCase)
                || current.Name.Equals("QMAH", StringComparison.OrdinalIgnoreCase)
                || current.Name.Equals("共用資料工具", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(current.Parent?.FullName ?? current.FullName, "_工具輸出");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QMAH", "ToolOutput");
    }

    private string ResolveRunnerPath(string projectName)
    {
        var executable = Path.Combine(_toolsRoot, projectName + ".exe");
        if (File.Exists(executable)) return executable;
        var sourceExecutable = Path.Combine(_sourceToolsRoot, projectName + ".exe");
        if (File.Exists(sourceExecutable)) return sourceExecutable;
        var project = Path.Combine(_sourceToolsRoot, projectName, projectName + ".csproj");
        if (File.Exists(project)) return project;
        var dll = Path.Combine(_toolsRoot, projectName + ".dll");
        if (File.Exists(dll)) return dll;
        var sourceDll = Path.Combine(_sourceToolsRoot, projectName + ".dll");
        return File.Exists(sourceDll) ? sourceDll : executable;
    }

    private string FindProjectRoot()
    {
        for (var current = new DirectoryInfo(_toolsRoot); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "QMAH.Web", "QMAH.Web.csproj")))
                return current.FullName;

            var siblingProjectRoot = Path.Combine(current.FullName, "QMAH", "QMAH.Web", "QMAH.Web.csproj");
            if (File.Exists(siblingProjectRoot))
                return Path.Combine(current.FullName, "QMAH");
        }
        return _toolsRoot;
    }

    private string FindSourceToolsRoot()
    {
        if (Directory.Exists(Path.Combine(_toolsRoot, "NpmArtifactPipeline")))
            return _toolsRoot;

        for (var current = new DirectoryInfo(_toolsRoot); current is not null; current = current.Parent)
        {
            var shared = Path.Combine(current.FullName, "共用資料工具");
            if (Directory.Exists(Path.Combine(shared, "NpmArtifactPipeline")))
                return shared;

            var projectTools = Path.Combine(current.FullName, "QMAH", "tools", "QmahDataTools");
            if (Directory.Exists(Path.Combine(projectTools, "NpmArtifactPipeline")))
                return projectTools;
        }

        return _toolsRoot;
    }
}
