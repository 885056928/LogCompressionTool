using System.Windows.Input;
using LogCompression;

namespace Main.ViewModels;

public sealed partial class MainViewModel
{
    #region 方法

    private async Task ExtractArchiveAsync()
    {
        var archive = _dialogs.ChooseExtractionArchive();
        if (archive is null) return;
        var destination = _dialogs.ChooseExtractionFolder();
        if (destination is null) return;

        await RunAsync("正在解压 ZIP…", async token =>
        {
            IsIndeterminate = false;
            var result = await _service.ExtractArchiveAsync(archive, destination, CreateExtractionProgress(), token);
            Progress = 100;
            Status = $"解压完成：{result.FileCount} 个文件，{FormatSize(result.ExtractedBytes)}，保存到 {result.DestinationDirectory}";
        });
    }

    private IProgress<ExtractionProgress> CreateExtractionProgress()
    {
        var lastPercent = -1;
        var lastCompleted = -1;
        var operation = _operation;
        var progress = new Progress<ExtractionProgress>(value =>
        {
            // 上一个任务排队中的进度回调不能覆盖当前解压状态。
            if (!IsBusy || _operation != operation) return;
            IsIndeterminate = value.IsScanning;
            Progress = Math.Clamp(value.Percentage, 0, 100);
            ProgressText = value.IsScanning ? value.CurrentFile :
                $"{value.CompletedFiles}/{value.TotalFiles} · {Progress:F0}% · {value.CurrentFile}";
        });

        return new InlineProgress<ExtractionProgress>(value =>
        {
            var percent = (int)value.Percentage;
            if (percent == lastPercent && value.CompletedFiles == lastCompleted && !value.IsScanning) return;
            lastPercent = percent;
            lastCompleted = value.CompletedFiles;
            ((IProgress<ExtractionProgress>)progress).Report(value);
        });
    }

    #endregion
}
