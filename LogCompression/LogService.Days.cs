using System.Globalization;
using System.IO.Compression;

namespace LogCompression;

public sealed partial class LogService
{
    private static LogDay[] ListDays(string directory, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"日志目录不存在：{root}");
        var days = new List<LogDay>();
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in Directory.EnumerateDirectories(root, "*", options))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(folder);
            if (!DateOnly.TryParseExact(name, "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var files = ListDirectory(folder, token);
            if (files.Length > 0) days.Add(new LogDay(name, date, folder, files.Length, files.Sum(f => f.Length)));
        }
        return days.OrderBy(d => d.Date).ThenBy(d => d.Name, StringComparer.Ordinal).ToArray();
    }

    private static CompressionResult CompressDays(string directory, IReadOnlyCollection<string> dayNames,
        string archivePath, LogCompressionLevel level, IProgress<CompressionProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(dayNames);
        if (dayNames.Count == 0 || dayNames.Distinct(StringComparer.Ordinal).Count() != dayNames.Count)
            throw new ArgumentException("请选择至少一个日期，且日期不能重复。", nameof(dayNames));
        var target = PrepareArchiveTarget(archivePath);
        progress?.Report(new CompressionProgress(0, 0, 0, 0, "正在扫描所选日期…", IsScanning: true));
        token.ThrowIfCancellationRequested();
        var available = ListDays(directory, token);
        var selected = available.Where(d => dayNames.Contains(d.Name, StringComparer.Ordinal)).ToArray();
        // 只接受刚从根目录发现的日期名称，不能通过相对路径越界压缩其他文件。
        if (selected.Length != dayNames.Count) throw new ArgumentException("所选日期不存在、不是有效日期文件夹或已没有日志，请重新打开目录。", nameof(dayNames));
        var groups = selected.Select(d => (Day: d, Files: ListDirectory(d.DirectoryPath, token))).ToArray();
        if (groups.Any(g => g.Files.Length == 0)) throw new IOException("日期目录中的日志已被删除，请重新扫描后压缩。");
        var totalFiles = groups.Sum(g => g.Files.Length);
        var totalBytes = groups.Sum(g => g.Files.Sum(f => f.Length));
        var temporary = TemporaryPath(target.Path);
        var compression = ToCompressionLevel(level);
        var completed = 0;
        long processed = 0;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var bundle = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var group in groups)
                    {
                        token.ThrowIfCancellationRequested();
                        // 每日 ZIP 直接写进总包，无须保存中间文件；外层不重复压缩已压缩的数据。
                        var entry = bundle.CreateEntry(group.Day.Name + ".zip", CompressionLevel.NoCompression);
                        using var data = entry.Open();
                        using var daily = new ZipArchive(data, ZipArchiveMode.Create);
                        (completed, processed) = WriteLogEntries(daily, group.Files, compression, totalFiles, totalBytes,
                            completed, processed, progress, token, group.Day.Name + "/");
                    }
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            CommitArchive(temporary, target);
            return new CompressionResult(target.Path, totalFiles, processed, new FileInfo(target.Path).Length);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static (int Completed, long Processed) WriteLogEntries(ZipArchive archive, LogFile[] files,
        CompressionLevel compression, int totalFiles, long totalBytes, int completed, long processed,
        IProgress<CompressionProgress>? progress, CancellationToken token, string prefix = "")
    {
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            using var input = OpenSource(file.SourcePath);
            if (input.Length != file.Length || File.GetLastWriteTime(file.SourcePath) != file.LastWriteTime)
                throw new IOException($"日志在扫描后已发生变化，请重试：{prefix}{file.RelativePath}");
            var entry = archive.CreateEntry(file.RelativePath.Replace('\\', '/'), compression);
            // ZIP 的有效日期范围是 1980—2107，越界文件时间必须夹取后再写入。
            entry.LastWriteTime = new DateTimeOffset(ClampZipDate(file.LastWriteTime));
            using var destination = entry.Open();
            Copy(input, destination, token, bytes =>
            {
                processed += bytes;
                progress?.Report(new CompressionProgress(completed, totalFiles, processed, totalBytes, prefix + file.RelativePath));
            });
            completed++;
            progress?.Report(new CompressionProgress(completed, totalFiles, processed, totalBytes, prefix + file.RelativePath));
        }
        return (completed, processed);
    }

    private static FileStream CacheNestedArchive(ZipArchiveEntry entry, CancellationToken token)
    {
        // ZIP 读取需要可定位的流；只暂存每日压缩包本身，避免将大包整个装入内存，不展开日志。
        var path = Path.Combine(Path.GetTempPath(), "LogCompression-nested-" + Guid.NewGuid().ToString("N") + ".tmp");
        var cache = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
        try
        {
            using var source = entry.Open();
            Copy(source, cache, token);
            cache.Position = 0;
            return cache;
        }
        catch { cache.Dispose(); throw; }
    }

    private static void RewriteArchive(ZipArchive input, Stream output, string targetName, Action<Stream> replace,
        CancellationToken token)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
        foreach (var entry in input.Entries)
        {
            token.ThrowIfCancellationRequested();
            var replacement = archive.CreateEntry(entry.FullName, IsZip(entry.FullName) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            replacement.LastWriteTime = entry.FullName == targetName ? DateTimeOffset.Now : entry.LastWriteTime;
            replacement.ExternalAttributes = entry.ExternalAttributes;
            using var destination = replacement.Open();
            if (entry.FullName == targetName) replace(destination);
            else { using var source = entry.Open(); Copy(source, destination, token); }
        }
    }

    private sealed record ArchiveTarget(string Path, bool Existed, long Length, long WriteTicks);

    private static ArchiveTarget PrepareArchiveTarget(string path)
    {
        var target = Path.GetFullPath(path);
        if (!IsZip(target)) throw new ArgumentException("压缩输出必须使用 .zip 扩展名。", nameof(path));
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("压缩包保存目录不存在。");
        var info = new FileInfo(target);
        return new ArchiveTarget(target, info.Exists, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
    }

    private static void CommitArchive(string temporary, ArchiveTarget target)
    {
        var current = new FileInfo(target.Path);
        if (current.Exists != target.Existed || (target.Existed &&
            (current.Length != target.Length || current.LastWriteTimeUtc.Ticks != target.WriteTicks)))
            throw new IOException("压缩期间目标 ZIP 已被其他程序创建、修改或删除。目标文件已保留，请重新选择保存位置。");
        // 新目标始终使用不覆盖的移动，检查后出现同名文件也不能未经确认替换。
        if (target.Existed) File.Replace(temporary, target.Path, null);
        else File.Move(temporary, target.Path);
    }

    private static CompressionLevel ToCompressionLevel(LogCompressionLevel level) => level switch
    {
        LogCompressionLevel.Fast => CompressionLevel.Fastest,
        LogCompressionLevel.Balanced => CompressionLevel.Optimal,
        LogCompressionLevel.Smallest => CompressionLevel.SmallestSize,
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}
