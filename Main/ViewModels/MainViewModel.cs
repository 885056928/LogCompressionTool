using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using LogCompression;
using Main.Infrastructure;
using Main.Services;

namespace Main.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ILogService _service;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _operation;
    private LogDocument? _document;
    private string _sourcePath = "";
    private bool _sourceIsArchive;

    #region 属性

    public ObservableCollection<LogFile> Files { get; } = [];
    public ICollectionView FileView { get; }
    public IReadOnlyList<string> CompressionLevels { get; } = ["均衡压缩", "快速压缩", "最小体积"];
    public string? SampleDirectory { get; }
    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) { FileView.Refresh(); OnPropertyChanged(nameof(FileSummary)); } }
    }
    private LogFile? _selectedFile;
    public LogFile? SelectedFile
    {
        get => _selectedFile;
        set { if (Set(ref _selectedFile, value)) CommandManager.InvalidateRequerySuggested(); }
    }
    private string _editorText = "";
    public string EditorText
    {
        get => _editorText;
        set
        {
            if (!Set(ref _editorText, value)) return;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DocumentState));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsEditorReadOnly));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsIdle => !IsBusy;
    public bool IsDirty => _document is { IsPreview: false } && EditorText != _document.Content;
    public bool HasDocument => _document is not null;
    public bool IsEditorReadOnly => IsBusy || _document is null || _document.IsPreview;
    public string SourcePath => string.IsNullOrEmpty(_sourcePath) ? "尚未选择日志目录或 ZIP 压缩包" : _sourcePath;
    public string SourceKind => string.IsNullOrEmpty(_sourcePath) ? "工作区" : _sourceIsArchive ? "ZIP 归档" : "日志目录";
    public string FileSummary => $"{FileView.Cast<LogFile>().Count()} / {Files.Count} 个日志 · {FormatSize(Files.Sum(f => f.Length))}";
    public string EmptyMessage => Files.Count == 0 ? "打开日志目录或 ZIP，开始查看日志。" : "没有匹配项，请修改或清空文件名筛选。";
    public string DocumentTitle => _document?.File.RelativePath ?? "日志内容";
    public string DocumentLocation => _document?.File.Location ?? "在左侧选择日志，然后点击“查看所选”或双击文件。";
    public string DocumentDetails => _document is null ? "支持 UTF-8 / UTF-16 / UTF-32 / GB18030" :
        $"{_document.File.SizeText}  ·  {_document.EncodingName.ToUpperInvariant()}{(_document.HasBom ? " + BOM" : "")}  ·  {_document.File.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
    public string DocumentState => _document is null ? "未打开" : _document.IsPreview ? "快速预览" : IsDirty ? "未保存" : "可编辑";
    public string PreviewHint => _document is { IsPreview: true } ?
        "当前仅显示前 128 KB。点击“加载全文编辑”查看完整内容并修改；全文编辑上限为 32 MB。" :
        "直接修改日志文本，点击“保存修改”写回原文件或 ZIP。快捷键：Ctrl+S 保存，Ctrl+O 打开目录。";
    private string _status = "准备就绪";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }
    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    private int _compressionLevelIndex;
    public int CompressionLevelIndex { get => _compressionLevelIndex; set => Set(ref _compressionLevelIndex, value); }
    private string _lastArchive = "";
    public string LastArchive { get => _lastArchive; private set => Set(ref _lastArchive, value); }

    #endregion

    #region 命令

    public AsyncCommand OpenFolderCommand { get; }
    public AsyncCommand OpenArchiveCommand { get; }
    public AsyncCommand OpenSampleCommand { get; }
    public AsyncCommand OpenSelectedCommand { get; }
    public AsyncCommand LoadFullCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand CompressCommand { get; }
    public AsyncCommand ExtractArchiveCommand { get; }
    public AsyncCommand OpenLastArchiveCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public RelayCommand CancelCommand { get; }

    #endregion

    #region 方法

    public MainViewModel(ILogService service, IDialogService dialogs)
    {
        _service = service;
        _dialogs = dialogs;
        SampleDirectory = FindSampleDirectory();
        FileView = CollectionViewSource.GetDefaultView(Files);
        FileView.Filter = value => value is LogFile file && file.RelativePath.Contains(Filter, StringComparison.OrdinalIgnoreCase);
        OpenFolderCommand = Command(async () =>
        {
            var path = _dialogs.ChooseFolder();
            if (path is not null) await OpenSourceAsync(path, false);
        });
        OpenArchiveCommand = Command(async () =>
        {
            var path = _dialogs.ChooseArchive();
            if (path is not null) await OpenSourceAsync(path, true);
        });
        OpenSampleCommand = Command(() => OpenSourceAsync(SampleDirectory!, false), () => SampleDirectory is not null);
        OpenSelectedCommand = Command(OpenSelectedAsync, () => SelectedFile is not null);
        LoadFullCommand = Command(async () => await RunAsync("正在加载全文…", async token =>
        {
            ApplyDocument(await _service.ReadAsync(_document!.File, true, token));
            Status = "全文已加载，可以修改后保存。";
        }),
            () => _document is { IsPreview: true } && _document.File.Length <= LogService.MaximumEditBytes);
        SaveCommand = Command(async () => await SaveCurrentAsync(), () => IsDirty);
        ExtractArchiveCommand = Command(ExtractArchiveAsync);
        CompressCommand = Command(CompressAsync, () => !_sourceIsArchive && Directory.Exists(_sourcePath) && Files.Count > 0 &&
            (CompressionModeIndex == 0 || Days.Any(d => d.IsSelected)));
        SelectAllDaysCommand = new RelayCommand(() => { foreach (var day in Days) day.IsSelected = true; }, () => !IsBusy && Days.Any(d => !d.IsSelected));
        ClearDaysCommand = new RelayCommand(() => { foreach (var day in Days) day.IsSelected = false; }, () => !IsBusy && Days.Any(d => d.IsSelected));
        OpenLastArchiveCommand = Command(() => OpenSourceAsync(LastArchive, true), () => File.Exists(LastArchive));
        ClearFilterCommand = new RelayCommand(() => Filter = "", () => Filter.Length > 0);
        CancelCommand = new RelayCommand(() => { _operation?.Cancel(); Status = "正在取消，请等待当前操作结束…"; }, () => IsBusy && _operation is { IsCancellationRequested: false });
    }

    private AsyncCommand Command(Func<Task> action, Func<bool>? enabled = null)
        => new(action, ShowError, () => !IsBusy && (enabled?.Invoke() ?? true));

    public async Task OpenSourceAsync(string path, bool archive)
    {
        if (IsBusy || !await EnsureCanLeaveAsync()) return;
        await RunAsync("正在扫描日志…", async token =>
        {
            var files = archive ? await _service.ListArchiveAsync(path, token) : await _service.ListDirectoryAsync(path, token);
            var days = archive ? Array.Empty<LogDay>() : await _service.ListDaysAsync(path, token);
            // 先读取新来源，成功后再替换工作区，失败时保留原编辑内容和文件列表。
            var first = files.Count == 0 ? null : await _service.ReadAsync(files[0], cancellationToken: token);
            _sourcePath = Path.GetFullPath(path);
            _sourceIsArchive = archive;
            ResetDays(days);
            Filter = "";
            Files.Clear();
            foreach (var file in files) Files.Add(file);
            SelectedFile = Files.FirstOrDefault();
            ApplyDocument(first);
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(SourceKind));
            OnPropertyChanged(nameof(FileSummary));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(ShowDaySelection));
            Status = files.Count == 0 ? "未找到 .log 文件，请选择其他目录或归档。" : $"已打开 {files.Count} 个日志。双击文件查看内容。";
        });
    }

    private async Task OpenSelectedAsync()
    {
        var selected = SelectedFile;
        if (selected is null || !await EnsureCanLeaveAsync()) return;
        await RunAsync("正在读取日志…", async token =>
        {
            ApplyDocument(await _service.ReadAsync(selected, cancellationToken: token));
            Status = "已打开 " + selected.RelativePath;
        });
    }

    private async Task<bool> SaveCurrentAsync()
    {
        if (_document is null || !IsDirty) return true;
        return await RunAsync("正在保存修改…", async token =>
        {
            var oldFile = _document.File;
            var saved = await _service.SaveAsync(_document, EditorText, token);
            var index = Files.IndexOf(oldFile);
            if (index >= 0) Files[index] = saved.File;
            if (!saved.File.IsArchived)
            {
                var dayName = saved.File.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                Days.FirstOrDefault(d => d.Day.Name == dayName)?.UpdateSize(saved.File.Length - oldFile.Length);
            }
            SelectedFile = saved.File;
            ApplyDocument(saved);
            OnPropertyChanged(nameof(FileSummary));
            Status = saved.File.IsArchived ? "修改已保存到 ZIP，其他条目内容已保留。" : "修改已保存到原日志文件。";
        });
    }

    private void ApplyDocument(LogDocument? document)
    {
        _document = document;
        EditorText = document?.Content ?? "";
        foreach (var name in new[] { nameof(HasDocument), nameof(IsEditorReadOnly), nameof(DocumentTitle), nameof(DocumentLocation),
            nameof(DocumentDetails), nameof(DocumentState), nameof(PreviewHint), nameof(IsDirty) }) OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task<bool> RunAsync(string status, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return false;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        StatusIsError = false;
        Status = status;
        Progress = 0;
        ProgressText = "";
        IsIndeterminate = true;
        IsBusy = true;
        try { await action(cancellation.Token); return true; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Status = "操作已取消。"; return false; }
        catch (Exception ex) { ShowError(ex); return false; }
        finally { _operation = null; IsBusy = false; IsIndeterminate = false; }
    }

    public async Task<bool> CanCloseAsync()
    {
        if (IsBusy) { Status = "请等待操作完成，或点击“取消操作”后再关闭窗口。"; return false; }
        return await EnsureCanLeaveAsync();
    }

    private async Task<bool> EnsureCanLeaveAsync()
    {
        if (!IsDirty) return true;
        return _dialogs.ConfirmUnsaved(_document!.File.Name) switch
        {
            UnsavedChoice.Save => await SaveCurrentAsync(),
            UnsavedChoice.Discard => DiscardChanges(),
            _ => false
        };
    }

    private bool DiscardChanges()
    {
        EditorText = _document!.Content;
        return true;
    }

    private void ShowError(Exception exception)
    {
        StatusIsError = true;
        Status = "操作失败：" + exception.Message;
    }

    private static string FormatSize(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576d:F2} MB" : $"{bytes / 1024d:F1} KB";
    private static string? FindSampleDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "LogExample", "Logs");
                if (Directory.Exists(candidate)) return candidate;
            }
        return null;
    }

    #endregion
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
