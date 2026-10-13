using System.IO.Compression;
using System.Security.Cryptography;

namespace LogCompression;

/// <summary>独立于 WPF 的日志处理实现。</summary>
public sealed partial class LogService : ILogService
{
    public const int PreviewBytes = 128 * 1024;
    public const int MaximumEditBytes = 32 * 1024 * 1024;

    public Task<IReadOnlyList<LogDay>> ListDaysAsync(string directory, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<LogDay>>(() => ListDays(directory, cancellationToken), cancellationToken);

    public Task<CompressionResult> CompressDaysAsync(string directory, IReadOnlyCollection<string> dayNames,
        string archivePath, LogCompressionLevel level = LogCompressionLevel.Balanced,
        IProgress<CompressionProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => CompressDays(directory, dayNames, archivePath, level, progress, cancellationToken), cancellationToken);

    // ZIP 压缩和遍历包含同步磁盘/CPU 工作，调度到可取消的后台任务，避免阻塞界面。
    public Task<IReadOnlyList<LogFile>> ListDirectoryAsync(string directory, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<LogFile>>(() => ListDirectory(directory, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<LogFile>> ListArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<LogFile>>(() =>
        {
            using var stream = OpenSource(archivePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            ValidateArchive(archive);
            var files = new List<LogFile>();
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsLog(entry.FullName)) files.Add(new LogFile(Path.GetFullPath(archivePath), entry.FullName,
                    entry.FullName, entry.Length, entry.LastWriteTime.LocalDateTime));
                else if (IsZip(entry.FullName))
                {
                    using var cached = CacheNestedArchive(entry, cancellationToken);
                    using var nested = new ZipArchive(cached, ZipArchiveMode.Read);
                    ValidateArchive(nested);
                    foreach (var log in nested.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsLog(log.FullName)) files.Add(new LogFile(Path.GetFullPath(archivePath),
                            entry.FullName + "/" + log.FullName, log.FullName, log.Length, log.LastWriteTime.LocalDateTime, entry.FullName));
                    }
                }
            }
            return files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        }, cancellationToken);

    public Task<LogDocument> ReadAsync(LogFile file, bool loadFullContent = false, CancellationToken cancellationToken = default)
        => Task.Run(() => Read(file, loadFullContent, cancellationToken), cancellationToken);

    public Task<LogDocument> SaveAsync(LogDocument document, string content, CancellationToken cancellationToken = default)
        => Task.Run(() => Save(document, content, cancellationToken), cancellationToken);

    public Task<CompressionResult> CompressAsync(string directory, string archivePath,
        LogCompressionLevel level = LogCompressionLevel.Balanced, IProgress<CompressionProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Compress(directory, archivePath, level, progress, cancellationToken), cancellationToken);

    private static LogFile[] ListDirectory(string directory, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"日志目录不存在：{root}");
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint };
        var files = new List<LogFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            token.ThrowIfCancellationRequested();
            if (!IsLog(path)) continue;
            var info = new FileInfo(path);
            files.Add(new LogFile(info.FullName, Path.GetRelativePath(root, info.FullName), null, info.Length, info.LastWriteTime));
        }
        return files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static LogDocument Read(LogFile file, bool full, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var source = OpenSource(file.SourcePath);
        var stamp = new FileInfo(file.SourcePath);
        if (!file.IsArchived) return ReadDocument(file with { Length = source.Length, LastWriteTime = stamp.LastWriteTime },
            source, source.Length, full, stamp, token);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        ValidateArchive(archive);
        if (file.InnerArchiveName is not null)
        {
            var innerEntry = archive.GetEntry(file.InnerArchiveName) ?? throw new FileNotFoundException("总 ZIP 中的日期压缩包已不存在。", file.InnerArchiveName);
            using var cached = CacheNestedArchive(innerEntry, token);
            using var nested = new ZipArchive(cached, ZipArchiveMode.Read);
            ValidateArchive(nested);
            var log = nested.GetEntry(file.EntryName!) ?? throw new FileNotFoundException("日期压缩包中的日志已不存在。", file.EntryName);
            using var logData = log.Open();
            return ReadDocument(file with { Length = log.Length, LastWriteTime = log.LastWriteTime.LocalDateTime },
                logData, log.Length, full, stamp, token);
        }
        var entry = archive.GetEntry(file.EntryName!) ?? throw new FileNotFoundException("ZIP 中的日志条目已不存在。", file.EntryName);
        using var data = entry.Open();
        return ReadDocument(file with { Length = entry.Length, LastWriteTime = entry.LastWriteTime.LocalDateTime },
            data, entry.Length, full, stamp, token);
    }

    private static LogDocument ReadDocument(LogFile file, Stream data, long length, bool full, FileInfo stamp, CancellationToken token)
    {
        if (full && length > MaximumEditBytes) throw new InvalidOperationException("全文编辑支持最大 32 MB 的日志，此文件可继续预览或压缩。");
        var preview = !full && length > PreviewBytes;
        var bytes = ReadBytes(data, preview ? PreviewBytes : MaximumEditBytes, preview, token);
        var decoded = LogTextCodec.Decode(bytes, preview);
        return new LogDocument(file, decoded.Content, decoded.EncodingName, decoded.HasBom, preview,
            stamp.Length, stamp.LastWriteTimeUtc.Ticks, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static LogDocument Save(LogDocument document, string content, CancellationToken token)
    {
        if (document.IsPreview) throw new InvalidOperationException("请先加载全文，再修改和保存；预览不能覆盖完整日志。");
        token.ThrowIfCancellationRequested();
        var bytes = LogTextCodec.Encode(content, document.EncodingName, document.HasBom);
        if (bytes.Length > MaximumEditBytes) throw new InvalidOperationException("保存内容超过 32 MB 的全文编辑上限。");
        var target = document.File.SourcePath;
        var temporary = TemporaryPath(target);
        try
        {
            using (var source = OpenSource(target))
            {
                EnsureUnchanged(document);
                if (document.File.IsArchived)
                {
                    using var input = new ZipArchive(source, ZipArchiveMode.Read);
                    ValidateArchive(input);
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    if (document.File.InnerArchiveName is null)
                    {
                        var original = input.GetEntry(document.File.EntryName!) ?? throw new IOException("ZIP 中的日志已删除，请重新打开归档。");
                        using (var data = original.Open()) EnsureContentUnchanged(document, data, token);
                        RewriteArchive(input, output, original.FullName, destination => destination.Write(bytes), token);
                    }
                    else
                    {
                        var dayEntry = input.GetEntry(document.File.InnerArchiveName) ?? throw new IOException("总 ZIP 中的日期压缩包已删除，请重新打开归档。");
                        using var cached = CacheNestedArchive(dayEntry, token);
                        using var nested = new ZipArchive(cached, ZipArchiveMode.Read);
                        ValidateArchive(nested);
                        var original = nested.GetEntry(document.File.EntryName!) ?? throw new IOException("日期压缩包中的日志已删除，请重新打开归档。");
                        using (var data = original.Open()) EnsureContentUnchanged(document, data, token);
                        RewriteArchive(input, output, dayEntry.FullName,
                            destination => RewriteArchive(nested, destination, original.FullName, log => log.Write(bytes), token), token);
                    }
                    output.Flush(true);
                }
                else
                {
                    EnsureContentUnchanged(document, source, token);
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    output.Write(bytes);
                    output.Flush(true);
                }
            }
            token.ThrowIfCancellationRequested();
            EnsureUnchanged(document);
            // 提交前可取消；替换成功后不再响应取消，避免将已保存的结果报告为失败。
            File.Replace(temporary, target, null);
            return Read(document.File, true, CancellationToken.None);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static CompressionResult Compress(string directory, string archivePath, LogCompressionLevel level,
        IProgress<CompressionProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = PrepareArchiveTarget(archivePath);
        progress?.Report(new CompressionProgress(0, 0, 0, 0, "正在扫描日志…", IsScanning: true));
        token.ThrowIfCancellationRequested();
        var files = ListDirectory(directory, token);
        if (files.Length == 0) throw new InvalidOperationException("所选目录及子目录中没有 .log 文件。");
        var compression = ToCompressionLevel(level);
        var temporary = TemporaryPath(target.Path);
        long processed = 0;
        var total = files.Sum(f => f.Length);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    (_, processed) = WriteLogEntries(archive, files, compression, files.Length, total, 0, 0, progress, token);
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            CommitArchive(temporary, target);
            return new CompressionResult(target.Path, files.Length, processed, new FileInfo(target.Path).Length);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void EnsureUnchanged(LogDocument document)
    {
        var stamp = new FileInfo(document.File.SourcePath);
        if (!stamp.Exists || stamp.Length != document.SourceLength || stamp.LastWriteTimeUtc.Ticks != document.SourceWriteTicks)
            throw new IOException("源文件已被其他程序修改。请保留当前编辑内容，重新加载后再保存。");
    }

    private static void EnsureContentUnchanged(LogDocument document, Stream source, CancellationToken token)
    {
        var bytes = ReadBytes(source, MaximumEditBytes, false, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != document.ContentHash)
            throw new IOException("日志内容已发生变化，无法覆盖保存。请复制当前编辑内容后重新加载。");
    }

    private static byte[] ReadBytes(Stream source, int limit, bool prefixOnly, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (output.Length < limit)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - output.Length));
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        if (!prefixOnly && source.ReadByte() != -1) throw new InvalidOperationException("日志超过 32 MB 的全文编辑上限。");
        return output.ToArray();
    }

    private static void Copy(Stream source, Stream destination, CancellationToken token, Action<int>? report = null)
    {
        var buffer = new byte[65536];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            report?.Invoke(read);
        }
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
            if ((IsLog(entry.FullName) || IsZip(entry.FullName)) && !names.Add(entry.FullName))
                throw new InvalidDataException($"ZIP 含有重复日志或压缩包路径，无法确定编辑对象：{entry.FullName}");
    }

    private static bool IsLog(string path) => string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase);
    private static bool IsZip(string path) => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
    private static string TemporaryPath(string target) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!,
        "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    private static DateTime ClampZipDate(DateTime date) => date.Year < 1980 ? new DateTime(1980, 1, 1)
        : date.Year > 2107 ? new DateTime(2107, 12, 31, 23, 59, 58) : date;
}
