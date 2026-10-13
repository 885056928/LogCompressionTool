#:project ../LogCompression/LogCompression.csproj

using System.IO.Compression;
using System.Text;
using LogCompression;

var root = Path.Combine(Path.GetTempPath(), "LogCompression-days-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
var errors = new List<string>();
try
{
    var logs = Path.Combine(root, "Logs");
    foreach (var day in new[] { "2026-10-1", "2026-10-03", "2026-10-13", "not-a-date", "2026-02-30" })
    {
        var folder = Path.Combine(logs, day, "Device"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "中文.log"), "[" + day + "]测试:原始内容\r\n", new UTF8Encoding(true));
    }
    Directory.CreateDirectory(Path.Combine(logs, "2026-10-02"));
    File.WriteAllText(Path.Combine(logs, "root.log"), "根目录日志");
    var service = new LogService();
    await Check("识别有效非空日期目录并按日期排序", async () =>
    {
        var days = await service.ListDaysAsync(logs);
        Assert(days.Count == 3 && days[0].Name == "2026-10-1" && days[1].Name == "2026-10-03" && days[2].Name == "2026-10-13");
        Assert(days.All(d => d.FileCount == 1 && d.TotalBytes > 0));
    });
    await Check("按天 ZIP 仅包含当天日志并保留子目录", async () =>
    {
        var days = await service.ListDaysAsync(logs);
        foreach (var day in days.Take(2))
        {
            var path = Path.Combine(root, day.Name + ".zip");
            await service.CompressAsync(day.DirectoryPath, path);
            using var archive = ZipFile.OpenRead(path);
            Assert(archive.Entries.Count == 1 && archive.GetEntry("Device/中文.log") is not null);
            using var reader = new StreamReader(archive.Entries[0].Open());
            Assert(reader.ReadToEnd() == "[" + day.Name + "]测试:原始内容\r\n");
        }
    });
    var bundle = Path.Combine(root, "bundle.zip");
    await Check("多天总包只含选中日期 ZIP，每日日志独立且内容一致", async () =>
    {
        var result = await service.CompressDaysAsync(logs, new[] { "2026-10-1", "2026-10-13" }, bundle);
        Assert(result.FileCount == 2);
        using var archive = ZipFile.OpenRead(bundle);
        Assert(archive.Entries.Count == 2 && archive.GetEntry("2026-10-1.zip") is not null && archive.GetEntry("2026-10-13.zip") is not null);
        foreach (var entry in archive.Entries)
        {
            using var data = entry.Open(); using var dayZip = new ZipArchive(data, ZipArchiveMode.Read);
            Assert(dayZip.Entries.Count == 1 && dayZip.GetEntry("Device/中文.log") is not null);
            using var reader = new StreamReader(dayZip.Entries[0].Open());
            Assert(reader.ReadToEnd() == "[" + Path.GetFileNameWithoutExtension(entry.Name) + "]测试:原始内容\r\n");
        }
    });
    await Check("程序可直接列出并读取总包内每日日志", async () =>
    {
        var files = await service.ListArchiveAsync(bundle);
        Assert(files.Count == 2 && files.All(f => f.InnerArchiveName is not null));
        var first = files.Single(f => f.InnerArchiveName == "2026-10-1.zip");
        var doc = await service.ReadAsync(first, true);
        Assert(doc.Content == "[2026-10-1]测试:原始内容\r\n" && doc.HasBom);
    });
    await Check("保存嵌套日志保留其他日期和源日志", async () =>
    {
        var files = await service.ListArchiveAsync(bundle);
        var doc = await service.ReadAsync(files.Single(f => f.InnerArchiveName == "2026-10-1.zip"), true);
        var saved = await service.SaveAsync(doc, "[2026-10-1]修改:保存成功\r\n");
        Assert(saved.Content == "[2026-10-1]修改:保存成功\r\n" && saved.HasBom);
        var after = await service.ListArchiveAsync(bundle);
        Assert((await service.ReadAsync(after.Single(f => f.InnerArchiveName == "2026-10-13.zip"), true)).Content == "[2026-10-13]测试:原始内容\r\n");
        Assert(File.ReadAllText(Path.Combine(logs, "2026-10-1", "Device", "中文.log")) == "[2026-10-1]测试:原始内容\r\n");
    });
    await Check("无日期/无效日期/重复日期明确拒绝，不创建输出", async () =>
    {
        foreach (var selection in new[] { Array.Empty<string>(), new[] { "../outside" }, new[] { "2026-10-1", "2026-10-1" } })
        {
            var output = Path.Combine(root, "invalid.zip");
            await Throws<ArgumentException>(() => service.CompressDaysAsync(logs, selection, output));
            Assert(!File.Exists(output));
        }
    });
    await Check("多天总包进度覆盖全部日志，完成计数正确", async () =>
    {
        var progress = new ObserveProgress();
        var result = await service.CompressDaysAsync(logs, new[] { "2026-10-03", "2026-10-13" }, Path.Combine(root, "progress.zip"), progress: progress);
        Assert(progress.Last is { CompletedFiles: 2, TotalFiles: 2 } && progress.Last.ProcessedBytes == result.OriginalBytes && progress.Last.Percentage == 100);
    });
    await Check("取消多天打包保留目标，清理临时文件", async () =>
    {
        var target = Path.Combine(root, "cancel.zip"); File.WriteAllText(target, "保留既有目标");
        using var cts = new CancellationTokenSource();
        var progress = new ObserveProgress(_ => cts.Cancel());
        await Throws<OperationCanceledException>(() => service.CompressDaysAsync(logs, new[] { "2026-10-1", "2026-10-13" }, target, progress: progress, cancellationToken: cts.Token));
        Assert(File.ReadAllText(target) == "保留既有目标");
        Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
    });
    await Check("多天压缩期间外部创建目标不能被覆盖", async () =>
    {
        var target = Path.Combine(root, "external.zip");
        var progress = new ObserveProgress(_ => { if (!File.Exists(target)) File.WriteAllText(target, "外部新建"); });
        await Throws<IOException>(() => service.CompressDaysAsync(logs, new[] { "2026-10-1" }, target, progress: progress));
        Assert(File.ReadAllText(target) == "外部新建");
    });
    await Check("扫描阶段新建或修改目标，整个目录和多天打包均拒绝覆盖", async () =>
    {
        foreach (var nested in new[] { false, true })
        foreach (var existed in new[] { false, true })
        {
            var target = Path.Combine(root, $"scan-{nested}-{existed}.zip");
            if (existed) File.WriteAllText(target, "原目标");
            var scanned = false;
            var progress = new ObserveProgress(value =>
            {
                if (!value.IsScanning || scanned) return;
                scanned = true;
                File.WriteAllText(target, "扫描阶段外部文件");
            });
            await Throws<IOException>(() => nested
                ? service.CompressDaysAsync(logs, new[] { "2026-10-1" }, target, progress: progress)
                : service.CompressAsync(logs, target, progress: progress));
            Assert(scanned && File.ReadAllText(target) == "扫描阶段外部文件");
        }
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"日期压缩验证：{count - errors.Count}/{count} 通过");
if (errors.Count > 0) throw new Exception(string.Join(Environment.NewLine, errors));

async Task Check(string name, Func<Task> test)
{
    count++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { errors.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); }
}
static void Assert(bool value) { if (!value) throw new Exception("行为断言失败"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("预期异常：" + typeof(T).Name);
}
sealed class ObserveProgress(Action<CompressionProgress>? callback = null) : IProgress<CompressionProgress>
{
    public CompressionProgress? Last { get; private set; }
    public void Report(CompressionProgress value) { Last = value; callback?.Invoke(value); }
}
