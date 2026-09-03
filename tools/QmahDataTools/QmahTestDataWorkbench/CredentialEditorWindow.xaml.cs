using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace QmahTestDataWorkbench;

public partial class CredentialEditorWindow : Window
{
    private readonly string _templatePath;
    private readonly string _backupPath;
    private readonly ObservableCollection<CredentialRow> _rows = [];
    private bool _synchronizingPassword;

    public CredentialEditorWindow(string repositoryRoot, string targetPath, string backupPath)
    {
        InitializeComponent();
        _templatePath = Path.Combine(repositoryRoot, "QMAH.DemoCredentials.csv");
        _backupPath = Path.GetFullPath(backupPath);
        TargetPathBox.Text = Path.GetFullPath(targetPath);
        CredentialGrid.ItemsSource = _rows;
        LoadInitialRows();
    }

    public string SavedPath => Path.GetFullPath(TargetPathBox.Text.Trim());

    private void LoadInitialRows()
    {
        var targetPath = SavedPath;
        if (File.Exists(targetPath))
        {
            try
            {
                LoadRowsFrom(targetPath);
                return;
            }
            catch (Exception exception)
            {
                ShowWarning($"本機帳密檔無法讀取：{exception.Message}。目前改用版本庫範本。 ");
            }
        }

        LoadTemplateRows();
    }

    private void LoadTemplateButton_Click(object sender, RoutedEventArgs e) => LoadTemplateRows();

    private void LoadFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SavedPath;
            if (!File.Exists(path))
            {
                ShowWarning($"找不到本機帳密檔：{path}");
                return;
            }

            LoadRowsFrom(path);
        }
        catch (Exception exception)
        {
            ShowWarning($"讀取本機帳密檔失敗：{exception.Message}");
        }
    }

    private void LoadTemplateRows()
    {
        try
        {
            LoadRowsFrom(_templatePath);
            StatusText.Text = $"已載入範本，共 {_rows.Count} 個帳號；目前密碼欄位保持空白。";
        }
        catch (Exception exception)
        {
            ShowWarning($"讀取帳密範本失敗：{exception.Message}");
        }
    }

    private void LoadRowsFrom(string path)
    {
        var rows = ReadCredentialFile(path);
        if (rows.Count == 0)
            throw new InvalidDataException("檔案沒有可用的帳密資料列。 ");

        _rows.Clear();
        foreach (var row in rows)
            _rows.Add(row);
        UpdateStatus($"已讀取 {Path.GetFileName(path)}");
    }

    private void ChoosePathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "選擇本機帳密檔位置",
            Filter = "CSV 檔案|*.csv|所有檔案|*.*",
            FileName = Path.GetFileName(SavedPath),
            InitialDirectory = ExistingDirectory(SavedPath),
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) == true)
            TargetPathBox.Text = dialog.FileName;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SavedPath;
            if (string.Equals(path, _templatePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能將密碼寫回版本庫範本，請選擇 .local.csv 檔案。 ");
            if (_rows.Count == 0)
                throw new InvalidDataException("沒有可儲存的帳密資料列。 ");

            var content = BuildCsv(_rows);
            WriteTextAtomically(path, content);
            if (!string.Equals(path, _backupPath, StringComparison.OrdinalIgnoreCase))
                WriteTextAtomically(_backupPath, content);

            UpdateStatus($"已儲存本機帳密檔；密碼已填 {_rows.Count(row => !string.IsNullOrWhiteSpace(row.Password))}/{_rows.Count} 筆。 ");
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            ShowWarning($"儲存帳密檔失敗：{exception.Message}");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CredentialPasswordBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            SynchronizePasswordBox(passwordBox);
    }

    private void CredentialPasswordBox_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            SynchronizePasswordBox(passwordBox);
    }

    private void CredentialPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_synchronizingPassword || sender is not PasswordBox passwordBox || passwordBox.DataContext is not CredentialRow row)
            return;

        row.Password = passwordBox.Password;
        UpdateStatus();
    }

    private void SynchronizePasswordBox(PasswordBox passwordBox)
    {
        if (passwordBox.DataContext is not CredentialRow row)
            return;

        _synchronizingPassword = true;
        passwordBox.Password = row.Password;
        _synchronizingPassword = false;
    }

    private void UpdateStatus(string? prefix = null)
    {
        if (_rows.Count == 0)
        {
            StatusText.Text = prefix ?? "目前沒有帳密資料。";
            return;
        }

        var filled = _rows.Count(row => !string.IsNullOrWhiteSpace(row.Password));
        StatusText.Text = $"{prefix ?? "尚未儲存"}；密碼已填 {filled}/{_rows.Count} 筆。未填密碼的帳號不能建立展示會員。";
    }

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "展示帳密設定", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static List<CredentialRow> ReadCredentialFile(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (lines.Count == 0)
            return [];

        var start = lines[0].StartsWith("DisplayName,Email,Password,Role", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        var result = new List<CredentialRow>();
        foreach (var line in lines.Skip(start))
        {
            var values = ParseCsv(line);
            if (values.Count != 4)
                throw new InvalidDataException($"帳密 CSV 欄位數不正確：{line}");
            result.Add(new CredentialRow(values[0], values[1], values[2], values[3]));
        }

        return result;
    }

    private static string BuildCsv(IEnumerable<CredentialRow> rows)
    {
        var builder = new StringBuilder()
            .AppendLine("DisplayName,Email,Password,Role");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',',
                Csv(row.DisplayName),
                Csv(row.Email),
                Csv(row.Password),
                Csv(row.Role)));
        }

        return builder.ToString();
    }

    private static List<string> ParseCsv(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        if (quoted)
            throw new InvalidDataException("帳密 CSV 包含未關閉的引號。 ");
        values.Add(value.ToString());
        return values;
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private static void WriteTextAtomically(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, fullPath, overwrite: true);
    }

    private static string ExistingDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : Environment.CurrentDirectory;
    }

    public sealed class CredentialRow(string displayName, string email, string password, string role)
    {
        public string DisplayName { get; } = displayName;
        public string Email { get; } = email;
        public string Password { get; set; } = password;
        public string Role { get; } = role;
    }
}
