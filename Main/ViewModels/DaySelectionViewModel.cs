using LogCompression;
using Main.Infrastructure;

namespace Main.ViewModels;

public sealed class DaySelectionViewModel(LogDay day) : ObservableObject
{
    #region 属性

    public LogDay Day { get; private set; } = day;
    public string Label => $"{Day.Date:yyyy-MM-dd} · {Day.FileCount} 个";
    public string Details => $"{Day.DirectoryPath} · {Day.TotalBytes / 1048576d:F2} MB";
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    #endregion

    #region 命令
    #endregion

    #region 方法

    public void UpdateSize(long difference)
    {
        Day = Day with { TotalBytes = Day.TotalBytes + difference };
        OnPropertyChanged(nameof(Day));
        OnPropertyChanged(nameof(Details));
    }

    #endregion
}
