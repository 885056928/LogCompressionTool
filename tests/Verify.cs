#:project ../LogCompression/LogCompression.csproj

using System.IO.Compression;
using System.Text;
using LogCompression;

// 使用 .NET 10 文件式应用验证真实文件操作，不增加解决方案项目或测试包。
var root = Path.Combine(Path.GetTempPath(), "LogCompression-verify-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var failures = new List<string>();
var count = 0;
try
{
    var service = new LogService();
    var logs = Path.Combine(root, "Logs");
    Directory.CreateDirectory(Path.Combine(logs, "2026-10-1"));
    var firstPath = Path.Combine(logs, "2026-10-1", "中文.log");
    var original = "[2026-10-01]启动:初始化完成\r\n[2026-10-01]采集:温度正常\r\n";
    File.WriteAllText(firstPath, original, new UTF8Encoding(true));
    File.WriteAllText(Path.Combine(logs, "忽略.txt"), "not a log");
    var utf16Path = Path.Combine(logs, "宽字符.LOG");
    File.WriteAllText(utf16Path, "[2026-10-01]编码:中文", Encoding.Unicode);
    var zip = Path.Combine(root, "logs.zip");

    await Check("递归扫描只返回日志，保留相对路径", async () =>
    {
        var files = await service.ListDirectoryAsync(logs);
        Assert(files.Count == 2 && files.Any(f => f.RelativePath.Replace('\\', '/') == "2026-10-1/中文.log"));
    });
    await Check("ZIP 压缩与内容、目录往返一致", async () =>
    {
        var result = await service.CompressAsync(logs, zip);
        var files = await service.ListArchiveAsync(zip);
        Assert(result.FileCount == 2 && files.Count == 2);
        var doc = await service.ReadAsync(files.Single(f => f.Name == "中文.log"), true);
        Assert(doc.Content == original && doc.HasBom && !doc.IsPreview);
    });
    await Check("解压 ZIP 保留目录结构和文件内容", async () =>
    {
        var output = Path.Combine(root, "extracted"); Directory.CreateDirectory(output);
        var result = await service.ExtractArchiveAsync(zip, output);
        Assert(result.FileCount == 2 && File.ReadAllText(Path.Combine(output, "2026-10-1", "中文.log")) == original);
        Assert(File.ReadAllText(Path.Combine(output, "宽字符.LOG")) == "[2026-10-01]编码:中文");
    });
    await Check("解压遇到重名文件时在写入其他条目前停止", async () =>
    {
        var source = Path.Combine(root, "collision.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using (var first = new StreamWriter(archive.CreateEntry("exists.log").Open())) first.Write("归档内容");
            using (var second = new StreamWriter(archive.CreateEntry("new.log").Open())) second.Write("新增内容");
        }
        var output = Path.Combine(root, "collision-output"); Directory.CreateDirectory(output);
        var existing = Path.Combine(output, "exists.log"); File.WriteAllText(existing, "原有内容");
        await Throws<IOException>(() => service.ExtractArchiveAsync(source, output));
        Assert(File.ReadAllText(existing) == "原有内容" && !File.Exists(Path.Combine(output, "new.log")));
    });
    await Check("解压拒绝路径穿越且不写入目标目录之外", async () =>
    {
        var source = Path.Combine(root, "unsafe.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create)) archive.CreateEntry("../escaped.log");
        var output = Path.Combine(root, "unsafe-output"); Directory.CreateDirectory(output);
        await Throws<InvalidDataException>(() => service.ExtractArchiveAsync(source, output));
        Assert(!File.Exists(Path.Combine(root, "escaped.log")) && !Directory.EnumerateFileSystemEntries(output).Any());
    });
    await Check("解压拒绝重复 ZIP 路径", async () =>
    {
        var source = Path.Combine(root, "duplicate-extract.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            using (var first = new StreamWriter(archive.CreateEntry("same.txt").Open())) first.Write("第一份");
            using (var second = new StreamWriter(archive.CreateEntry("same.txt").Open())) second.Write("第二份");
        }
        var output = Path.Combine(root, "duplicate-output"); Directory.CreateDirectory(output);
        await Throws<InvalidDataException>(() => service.ExtractArchiveAsync(source, output));
        Assert(!Directory.EnumerateFileSystemEntries(output).Any());
    });
    await Check("取消解压清理暂存内容", async () =>
    {
        var source = Path.Combine(root, "cancel-extract.zip");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var data = archive.CreateEntry("large.bin").Open()) data.Write(new byte[1024 * 1024]);
        var output = Path.Combine(root, "cancel-output"); Directory.CreateDirectory(output);
        using var cts = new CancellationTokenSource();
        var progress = new CancelExtractionProgress(() => cts.Cancel());
        await Throws<OperationCanceledException>(() => service.ExtractArchiveAsync(source, output, progress, cts.Token));
        Assert(progress.Reported && !Directory.EnumerateFileSystemEntries(output).Any());
    });
    await Check("ZIP 编辑保存保留其他条目", async () =>
    {
        var files = await service.ListArchiveAsync(zip);
        var doc = await service.ReadAsync(files.Single(f => f.Name == "中文.log"), true);
        var saved = await service.SaveAsync(doc, original + "[2026-10-01]修改:已保存\r\n");
        Assert(saved.Content.Contains("修改:已保存"));
        var after = await service.ListArchiveAsync(zip);
        Assert(after.Count == 2);
        Assert((await service.ReadAsync(after.Single(f => f.Name == "宽字符.LOG"), true)).Content == "[2026-10-01]编码:中文");
    });
    await Check("磁盘保存保留 UTF-16 BOM 和换行", async () =>
    {
        var file = (await service.ListDirectoryAsync(logs)).Single(f => f.Name == "宽字符.LOG");
        var doc = await service.ReadAsync(file, true);
        await service.SaveAsync(doc, "[2026-10-01]保存:完成\r\n");
        var bytes = File.ReadAllBytes(utf16Path);
        Assert(bytes[0] == 0xff && bytes[1] == 0xfe);
        Assert(File.ReadAllText(utf16Path, Encoding.Unicode) == "[2026-10-01]保存:完成\r\n");
    });
    await Check("拒绝覆盖外部修改", async () =>
    {
        var file = (await service.ListDirectoryAsync(logs)).Single(f => f.Name == "中文.log");
        var doc = await service.ReadAsync(file, true);
        File.WriteAllText(firstPath, "外部修改", new UTF8Encoding(true));
        await Throws<IOException>(() => service.SaveAsync(doc, "覆盖内容"));
        Assert(File.ReadAllText(firstPath) == "外部修改");
    });
    await Check("取消压缩保留既有目标并清理临时文件", async () =>
    {
        var cancelTarget = Path.Combine(root, "cancel.zip");
        File.WriteAllBytes(cancelTarget, new byte[] { 1, 2, 3 });
        var before = File.ReadAllBytes(cancelTarget);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Throws<OperationCanceledException>(() => service.CompressAsync(logs, cancelTarget, cancellationToken: cts.Token));
        Assert(File.ReadAllBytes(cancelTarget).SequenceEqual(before));
        Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
    });
    await Check("大日志默认预览，禁止用预览覆盖完整文件", async () =>
    {
        var path = Path.Combine(logs, "large.log");
        File.WriteAllText(path, string.Concat(Enumerable.Repeat("[2026-10-01]采集:中文边界\r\n", 20000)), new UTF8Encoding(false));
        var file = (await service.ListDirectoryAsync(logs)).Single(f => f.Name == "large.log");
        var preview = await service.ReadAsync(file);
        Assert(preview.IsPreview && preview.Content.Length < File.ReadAllText(path).Length && !preview.Content.Contains('\ufffd'));
        await Throws<InvalidOperationException>(() => service.SaveAsync(preview, "不能覆盖"));
        var full = await service.ReadAsync(file, true);
        Assert(!full.IsPreview && full.Content == File.ReadAllText(path));
    });
    await Check("空目录压缩明确失败，不留下输出", async () =>
    {
        var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
        var output = Path.Combine(root, "empty.zip");
        await Throws<InvalidOperationException>(() => service.CompressAsync(empty, output));
        Assert(!File.Exists(output));
    });
    await Check("损坏 ZIP 返回错误", async () =>
    {
        var broken = Path.Combine(root, "broken.zip"); File.WriteAllText(broken, "invalid zip");
        await Throws<InvalidDataException>(() => service.ListArchiveAsync(broken));
    });
    await Check("ZIP 重复日志路径明确拒绝", async () =>
    {
        var duplicate = Path.Combine(root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            archive.CreateEntry("same.log"); archive.CreateEntry("same.log");
        }
        await Throws<InvalidDataException>(() => service.ListArchiveAsync(duplicate));
    });
    await Check("GB18030 中文读取与保存保留编码", async () =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding("GB18030");
        var path = Path.Combine(logs, "gb.log");
        var text = "[2026-10-13]设备:温度正常\n";
        File.WriteAllText(path, text, encoding);
        var file = (await service.ListDirectoryAsync(logs)).Single(f => f.Name == "gb.log");
        var doc = await service.ReadAsync(file, true);
        Assert(doc.Content == text && doc.EncodingName == "gb18030" && !doc.HasBom);
        await service.SaveAsync(doc, text.Replace("正常", "已更新"));
        Assert(File.ReadAllBytes(path).SequenceEqual(encoding.GetBytes(text.Replace("正常", "已更新"))));
    });
    await Check("运行中取消压缩不替换目标且无临时文件", async () =>
    {
        var target = Path.Combine(root, "mid-cancel.zip"); File.WriteAllBytes(target, new byte[] { 7, 8, 9 });
        using var cts = new CancellationTokenSource();
        var progress = new CancelProgress(() => cts.Cancel());
        await Throws<OperationCanceledException>(() => service.CompressAsync(logs, target, progress: progress, cancellationToken: cts.Token));
        Assert(progress.Reported && File.ReadAllBytes(target).SequenceEqual(new byte[] { 7, 8, 9 }));
        Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
    });
    await Check("超大日志允许预览，全文编辑明确拒绝", async () =>
    {
        var path = Path.Combine(logs, "over-limit.log");
        using (var file = File.Create(path)) { file.WriteByte((byte)'a'); file.SetLength(32L * 1024 * 1024 + 1); }
        var item = (await service.ListDirectoryAsync(logs)).Single(f => f.Name == "over-limit.log");
        Assert((await service.ReadAsync(item)).IsPreview);
        await Throws<InvalidOperationException>(() => service.ReadAsync(item, true));
    });
    await Check("压缩期间创建同名目标不能被覆盖", async () =>
    {
        var target = Path.Combine(root, "created-during-compression.zip");
        var progress = new OnceProgress(() => File.WriteAllText(target, "另一个程序新建的文件"));
        await Throws<IOException>(() => service.CompressAsync(logs, target, progress: progress));
        Assert(File.ReadAllText(target) == "另一个程序新建的文件");
    });
    await Check("压缩期间更新既有目标不能被覆盖", async () =>
    {
        var target = Path.Combine(root, "updated-during-compression.zip");
        File.WriteAllText(target, "原目标");
        var progress = new OnceProgress(() => File.WriteAllText(target, "外部修改后的目标"));
        await Throws<IOException>(() => service.CompressAsync(logs, target, progress: progress));
        Assert(File.ReadAllText(target) == "外部修改后的目标");
        Assert(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any());
    });
}
finally
{
    // 仅清理本次验证新建的随机临时目录，不触碰项目中的示例日志。
    Directory.Delete(root, true);
}
Console.WriteLine($"验证结果：{count - failures.Count}/{count} 通过");
if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));

async Task Check(string name, Func<Task> test)
{
    count++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); }
}
static void Assert(bool condition) { if (!condition) throw new Exception("行为断言失败"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("预期异常：" + typeof(T).Name);
}
sealed class CancelProgress(Action cancel) : IProgress<CompressionProgress>
{
    public bool Reported { get; private set; }
    public void Report(CompressionProgress value) { Reported = true; cancel(); }
}
sealed class OnceProgress(Action firstReport) : IProgress<CompressionProgress>
{
    private bool _reported;
    public void Report(CompressionProgress value)
    {
        if (_reported) return;
        _reported = true;
        firstReport();
    }
}
sealed class CancelExtractionProgress(Action cancel) : IProgress<ExtractionProgress>
{
    public bool Reported { get; private set; }
    public void Report(ExtractionProgress value)
    {
        if (value.ProcessedBytes == 0 || Reported) return;
        Reported = true;
        cancel();
    }
}
