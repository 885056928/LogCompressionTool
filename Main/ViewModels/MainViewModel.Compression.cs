using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using LogCompression;
using Main.Infrastructure;

namespace Main.ViewModels;

public sealed partial class MainViewModel
{
    #region 属性

    public ObservableCollection<DaySelectionViewModel> Days { get; } = [];
    public IReadOnlyList<string> CompressionModes { get; } = ["整个目录", "按天分别压缩", "多天合成总 ZIP"];
    private int _compressionModeIndex;
    public int CompressionModeIndex
    {
        get => _compressionModeIndex;
        set
        {
            if (!Set(ref _compressionModeIndex, Math.Clamp(value, 0, 2))) return;
            OnPropertyChanged(nameof(ShowDaySelection));
            OnPropertyChanged(nameof(CompressionActionText));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool ShowDaySelection => CompressionModeIndex > 0 && !_sourceIsArchive && _sourcePath.Length > 0;
    public string CompressionActionText => CompressionModeIndex switch
    { 1 => "按天分别压缩", 2 => "打包选中日期", _ => "压缩整个目录" };
    public string SelectedDaysSummary => Days.Count == 0 ? "未找到有日志的日期文件夹；可使用“整个目录”压缩。" :
        $"已选 {Days.Count(d => d.IsSelected)} / {Days.Count} 天 · {FormatSize(Days.Where(d => d.IsSelected).Sum(d => d.Day.TotalBytes))}";
    public string DatePickerText => $"选择日期 ({Days.Count(d => d.IsSelected)})";

    #endregion

    #region 命令

    public RelayCommand SelectAllDaysCommand { get; }
    public RelayCommand ClearDaysCommand { get; }

    #endregion

    #region 方法

    private void ResetDays(IReadOnlyList<LogDay> days)
    {
        foreach (var item in Days) item.PropertyChanged -= OnDayChanged;
        Days.Clear();
        foreach (var day in days)
        {
            var item = new DaySelectionViewModel(day);
            item.PropertyChanged += OnDayChanged;
            Days.Add(item);
        }
        OnPropertyChanged(nameof(SelectedDaysSummary));
        OnPropertyChanged(nameof(DatePickerText));
    }

    private void OnDayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DaySelectionViewModel.IsSelected) or nameof(DaySelectionViewModel.Day))
        {
            OnPropertyChanged(nameof(SelectedDaysSummary));
            OnPropertyChanged(nameof(DatePickerText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task CompressAsync()
    {
        if (!await EnsureCanLeaveAsync()) return;
        var selected = Days.Where(d => d.IsSelected).Select(d => d.Day).ToArray();
        var mode = CompressionModeIndex;
        if (mode != 0 && selected.Length == 0) { Status = "请至少勾选一个日期。"; return; }
        var level = CompressionLevelIndex switch
        { 1 => LogCompressionLevel.Fast, 2 => LogCompressionLevel.Smallest, _ => LogCompressionLevel.Balanced };
        if (mode == 1)
        {
            await CompressDailyAsync(selected, level);
            return;
        }
        var output = _dialogs.ChooseOutputArchive(_sourcePath);
        if (output is null) return;
        await RunAsync("正在压缩日志…", async token =>
        {
            IsIndeterminate = false;
            var progress = CreateCompressionProgress();
            var result = mode == 2
                ? await _service.CompressDaysAsync(_sourcePath, selected.Select(d => d.Name).ToArray(), output, level, progress, token)
                : await _service.CompressAsync(_sourcePath, output, level, progress, token);
            LastArchive = result.ArchivePath;
            Progress = 100;
            Status = $"压缩完成：{(mode == 2 ? selected.Length + " 天，" : "")}{result.FileCount} 个日志，{FormatSize(result.OriginalBytes)} → {FormatSize(result.ArchiveBytes)}，节省 {result.SavedPercentage:F1}%。";
        });
    }

    private async Task CompressDailyAsync(LogDay[] selected, LogCompressionLevel level)
    {
        var folder = _dialogs.ChooseOutputFolder();
        if (folder is null) return;
        var targets = selected.Select(day => new FileInfo(Path.Combine(folder, day.Name + ".zip"))).Select(info =>
            (Path: info.FullName, Exists: info.Exists, Length: info.Exists ? info.Length : 0,
                Ticks: info.Exists ? info.LastWriteTimeUtc.Ticks : 0)).ToArray();
        var existing = targets.Where(t => t.Exists).Select(t => t.Path).ToArray();
        if (existing.Length > 0 && !_dialogs.ConfirmArchiveOverwrite(existing))
        {
            StatusIsError = false;
            Status = "已取消覆盖，每日 ZIP 未修改。";
            return;
        }
        var completed = new List<CompressionResult>();
        var success = await RunAsync("正在按天压缩日志…", async token =>
        {
            IsIndeterminate = false;
            var progress = CreateCompressionProgress();
            var currentDays = await _service.ListDaysAsync(_sourcePath, token);
            var totalFiles = currentDays.Where(d => selected.Any(s => s.Name == d.Name)).Sum(d => d.FileCount);
            var totalBytes = currentDays.Where(d => selected.Any(s => s.Name == d.Name)).Sum(d => d.TotalBytes);
            for (var i = 0; i < selected.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var day = currentDays.FirstOrDefault(d => d.Name == selected[i].Name)
                    ?? throw new IOException("所选日期已不存在或没有日志，请重新打开目录。");
                var target = targets[i];
                var current = new FileInfo(target.Path);
                // 覆盖确认针对整个批次；后续日期不能覆盖确认后才被外部新建或修改的文件。
                if (current.Exists != target.Exists || (current.Exists &&
                    (current.Length != target.Length || current.LastWriteTimeUtc.Ticks != target.Ticks)))
                    throw new IOException("每日 ZIP 的目标在确认后发生变化，已停止压缩：" + target.Path);
                var filesBefore = completed.Sum(r => r.FileCount);
                var bytesBefore = completed.Sum(r => r.OriginalBytes);
                var dailyProgress = new InlineProgress<CompressionProgress>(value => progress.Report(new CompressionProgress(
                    filesBefore + value.CompletedFiles, totalFiles, bytesBefore + value.ProcessedBytes, totalBytes,
                    day.Name + "/" + value.CurrentFile, value.IsScanning)));
                var result = await _service.CompressAsync(day.DirectoryPath, target.Path, level, dailyProgress, token);
                completed.Add(result);
                LastArchive = result.ArchivePath;
            }
            Progress = 100;
            Status = $"按天压缩完成：{completed.Count} 个 ZIP，{completed.Sum(r => r.FileCount)} 个日志，已保存到 {folder}";
        });
        // 每天独立提交，取消或后续错误不删除已经成功生成的 ZIP，并明确告知用户结果。
        if (!success && completed.Count > 0)
            Status += $" 已完成 {completed.Count}/{selected.Length} 天的 ZIP 保留在 {folder}，可点击查看最后一个。";
    }

    private IProgress<CompressionProgress> CreateCompressionProgress()
    {
        var lastPercent = -1;
        var lastCompleted = -1;
        var operation = _operation;
        var progress = new Progress<CompressionProgress>(value =>
        {
            // 旧任务排队的回调不能覆盖随后启动的新任务进度。
            if (!IsBusy || _operation != operation) return;
            IsIndeterminate = value.IsScanning;
            Progress = Math.Clamp(value.Percentage, 0, 100);
            ProgressText = value.IsScanning ? value.CurrentFile : $"{value.CompletedFiles}/{value.TotalFiles} · {Progress:F0}% · {value.CurrentFile}";
        });
        // 每个百分比或文件完成时更新，避免大批日志的回调淹没 UI 消息队列。
        return new InlineProgress<CompressionProgress>(value =>
        {
            var percent = (int)value.Percentage;
            if (percent == lastPercent && value.CompletedFiles == lastCompleted) return;
            lastPercent = percent;
            lastCompleted = value.CompletedFiles;
            ((IProgress<CompressionProgress>)progress).Report(value);
        });
    }

    #endregion
}
