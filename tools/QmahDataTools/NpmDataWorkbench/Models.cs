using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NpmDataWorkbench;

public sealed class ArtifactWorkbenchPreset
{
    public string Name { get; init; } = "預設 1";
    public string Description { get; init; } = "八類各 32 件的 256 件參考資料包設定。";
    public Dictionary<string, int> ArtifactCounts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string SelectionMode { get; init; } = "diverse";
    public int Seed { get; init; } = 173;
    public string Readable { get; init; } = "none";
    public bool DownloadImages { get; init; } = true;
    public int ImportArtifactPerCategory { get; init; } = 32;
    public int ImportMaxProducts { get; init; } = 256;
}

public sealed class ShopCategoryOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public ShopCategoryOption(
        string code,
        string name,
        bool isSelected,
        int observedProductLinks = 0,
        string? mappedCategoryCode = null)
    {
        Code = code;
        Name = name;
        ObservedProductLinks = observedProductLinks;
        MappedCategoryCode = mappedCategoryCode ?? "";
        _isSelected = isSelected;
    }

    public string Code { get; }
    public string Name { get; }
    public int ObservedProductLinks { get; }
    public string MappedCategoryCode { get; }
    public string Display => ObservedProductLinks > 0
        ? $"{Name}（{Code}）｜約 {ObservedProductLinks:N0} 個商品連結"
        : $"{Name}（{Code}）";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
