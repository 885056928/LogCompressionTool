namespace LogCompression;

/// <summary>供界面调用的日志检索、显示内容读取、修改保存与压缩接口。</summary>
public interface ILogService
{
    Task<IReadOnlyList<LogFile>> ListDirectoryAsync(string directory, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LogFile>> ListArchiveAsync(string archivePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LogDay>> ListDaysAsync(string directory, CancellationToken cancellationToken = default);
    Task<LogDocument> ReadAsync(LogFile file, bool loadFullContent = false, CancellationToken cancellationToken = default);
    Task<LogDocument> SaveAsync(LogDocument document, string content, CancellationToken cancellationToken = default);
    Task<CompressionResult> CompressAsync(string directory, string archivePath,
        LogCompressionLevel level = LogCompressionLevel.Balanced,
        IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>将选定日期分别生成内部 ZIP，再合并为一个总 ZIP；仅包含选中日期的日志。</summary>
    Task<CompressionResult> CompressDaysAsync(string directory, IReadOnlyCollection<string> dayNames, string archivePath,
        LogCompressionLevel level = LogCompressionLevel.Balanced,
        IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>将 ZIP 内容按原有相对路径解压到已有目录；遇到冲突或不安全路径时失败，不覆盖现有文件。</summary>
    Task<ExtractionResult> ExtractArchiveAsync(string archivePath, string destinationDirectory,
        IProgress<ExtractionProgress>? progress = null, CancellationToken cancellationToken = default);
}
