namespace LogCompression;

/// <summary>磁盘日志或 ZIP 中的日志条目；ZIP 内容直接读取，不解压到磁盘。</summary>
public sealed record LogFile(string SourcePath, string RelativePath, string? EntryName,
    long Length, DateTime LastWriteTime, string? InnerArchiveName = null)
{
    public bool IsArchived => EntryName is not null;
    public string Name => Path.GetFileName(RelativePath);
    public string SizeText => Length >= 1048576 ? $"{Length / 1048576d:F2} MB" : $"{Length / 1024d:F1} KB";
    public string Location => IsArchived ? $"{SourcePath} → {(InnerArchiveName is null ? "" : InnerArchiveName + " → ")}{EntryName}" : SourcePath;
}

/// <summary>日志根目录下的日期文件夹，名称保留原样，日期用于正确排序。</summary>
public sealed record LogDay(string Name, DateOnly Date, string DirectoryPath, int FileCount, long TotalBytes);

public sealed record LogDocument(LogFile File, string Content, string EncodingName, bool HasBom,
    bool IsPreview, long SourceLength, long SourceWriteTicks, string ContentHash);

public sealed record CompressionProgress(int CompletedFiles, int TotalFiles, long ProcessedBytes,
    long TotalBytes, string CurrentFile, bool IsScanning = false)
{
    public double Percentage => IsScanning ? 0 : TotalBytes == 0 ? 100 : ProcessedBytes * 100d / TotalBytes;
}

public enum LogCompressionLevel { Fast, Balanced, Smallest }

public sealed record CompressionResult(string ArchivePath, int FileCount, long OriginalBytes, long ArchiveBytes)
{
    public double SavedPercentage => OriginalBytes == 0 ? 0 : (1 - ArchiveBytes / (double)OriginalBytes) * 100;
}
