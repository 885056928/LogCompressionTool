using System.IO.Compression;

namespace LogCompression;

public sealed partial class LogService
{
    private static ExtractionResult ExtractArchive(string archivePath, string destinationDirectory,
        IProgress<ExtractionProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sourcePath = Path.GetFullPath(archivePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("ZIP 压缩包不存在。", sourcePath);

        var destination = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("解压目标目录不存在。");

        using var source = OpenSource(sourcePath);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        var entries = PlanExtraction(archive, destination);
        var files = entries.Where(entry => !entry.IsDirectory).ToArray();
        var totalBytes = files.Aggregate(0L, (total, entry) => checked(total + entry.Entry.Length));
        EnsureExtractionTargetsAvailable(entries, destination);
        progress?.Report(new ExtractionProgress(0, files.Length, 0, totalBytes, "正在检查 ZIP 条目…", IsScanning: true));

        var stagingDirectory = CreateExtractionStagingDirectory(destination, entries);
        long processedBytes = 0;
        var completedFiles = 0;
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var stagedPath = Path.Combine(stagingDirectory, entry.RelativePath);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(stagedPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                using (var input = entry.Entry.Open())
                using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CopyExtractedEntry(input, output, token, read =>
                    {
                        processedBytes = checked(processedBytes + read);
                        progress?.Report(new ExtractionProgress(completedFiles, files.Length, processedBytes,
                            totalBytes, entry.RelativePath));
                    });
                }

                if (new FileInfo(stagedPath).Length != entry.Entry.Length)
                    throw new InvalidDataException($"ZIP 条目长度与目录记录不符：{entry.RelativePath}");
                completedFiles++;
                progress?.Report(new ExtractionProgress(completedFiles, files.Length, processedBytes,
                    totalBytes, entry.RelativePath));
            }

            // 暂存和校验完整后才写入目标；取消或损坏条目不会留下半解压文件。
            token.ThrowIfCancellationRequested();
            EnsureExtractionTargetsAvailable(entries, destination);
            foreach (var entry in entries.Where(entry => entry.IsDirectory))
                Directory.CreateDirectory(Path.Combine(destination, entry.RelativePath));
            foreach (var entry in files)
            {
                // 进入提交阶段后完成本次写入，避免取消时目标目录只出现部分文件。
                var targetPath = Path.Combine(destination, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Move(Path.Combine(stagingDirectory, entry.RelativePath), targetPath);
            }

            return new ExtractionResult(destination, files.Length, processedBytes);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        }
    }

    private static ExtractionEntry[] PlanExtraction(ZipArchive archive, string destination)
    {
        var entries = new List<ExtractionEntry>();
        var paths = new Dictionary<string, ExtractionEntry>(StringComparer.OrdinalIgnoreCase);
        var destinationPrefix = Path.EndsInDirectorySeparator(destination)
            ? destination : destination + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            var isDirectory = normalized.EndsWith('/');
            if (normalized.Length == 0 || normalized.StartsWith('/') ||
                Path.IsPathRooted(entry.FullName) || (normalized.Length > 1 && char.IsLetter(normalized[0]) && normalized[1] == ':'))
                throw new InvalidDataException($"ZIP 含有绝对路径条目：{entry.FullName}");

            var components = normalized.Split('/');
            if (isDirectory) components = components[..^1];
            if (components.Length == 0 || components.Any(component => component.Length == 0 || component is "." or ".." ||
                    component.EndsWith(' ') || component.EndsWith('.') || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    IsReservedWindowsName(component)))
                throw new InvalidDataException($"ZIP 含有不安全或无效的路径条目：{entry.FullName}");

            var relativePath = Path.Combine(components);
            var fullPath = Path.GetFullPath(Path.Combine(destination, relativePath));
            if (!fullPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ZIP 条目超出解压目标目录：{entry.FullName}");

            var planned = new ExtractionEntry(entry, relativePath, isDirectory);
            if (!paths.TryAdd(relativePath, planned))
                throw new InvalidDataException($"ZIP 含有重复路径条目：{relativePath}");
            entries.Add(planned);
        }

        foreach (var entry in entries.Where(item => !item.IsDirectory))
        {
            for (var parent = Path.GetDirectoryName(entry.RelativePath); parent is not null;
                 parent = Path.GetDirectoryName(parent))
                if (paths.TryGetValue(parent, out var ancestor) && !ancestor.IsDirectory)
                    throw new InvalidDataException($"ZIP 文件路径与子路径冲突：{entry.RelativePath}");
        }
        return entries.ToArray();
    }

    private static void EnsureExtractionTargetsAvailable(IEnumerable<ExtractionEntry> entries, string destination)
    {
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var target = Path.Combine(destination, entry.RelativePath);
            if (File.Exists(target) || Directory.Exists(target))
                conflicts.Add(entry.RelativePath);

            var parent = entry.IsDirectory ? target : Path.GetDirectoryName(target);
            while (parent is not null && !string.Equals(parent, destination, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(parent)) conflicts.Add(Path.GetRelativePath(destination, parent));
                if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"解压目标路径经过了目录链接，已停止以防写出目标目录：{Path.GetRelativePath(destination, parent)}");
                parent = Path.GetDirectoryName(parent);
            }

            if (entry.IsDirectory && Directory.Exists(target) &&
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"解压目标目录不能是目录链接：{entry.RelativePath}");
        }

        if (conflicts.Count > 0)
        {
            var summary = string.Join("、", conflicts.Take(5));
            if (conflicts.Count > 5) summary += $"等 {conflicts.Count} 项";
            throw new IOException($"目标目录已存在同名文件或目录，未覆盖任何内容：{summary}");
        }
    }

    private static string CreateExtractionStagingDirectory(string destination, IReadOnlyCollection<ExtractionEntry> entries)
    {
        while (true)
        {
            var name = ".LogCompressionExtract-" + Guid.NewGuid().ToString("N");
            if (entries.Any(entry => string.Equals(entry.RelativePath, name, StringComparison.OrdinalIgnoreCase) ||
                entry.RelativePath.StartsWith(name + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            var path = Path.Combine(destination, name);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static void CopyExtractedEntry(Stream input, Stream output, CancellationToken token, Action<int> onRead)
    {
        var buffer = new byte[65536];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) return;
            output.Write(buffer, 0, read);
            onRead(read);
        }
    }

    private static bool IsReservedWindowsName(string component)
    {
        var name = component.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9');
    }

    private sealed record ExtractionEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
}
