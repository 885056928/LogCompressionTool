#:project ../LogCompression/LogCompression.csproj

using System.IO.Compression;
using System.Security.Cryptography;
using LogCompression;

var source = Path.GetFullPath("LogExample/Logs");
var output = Path.GetFullPath("artifacts/LogExample.zip");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var service = new LogService();
var files = await service.ListDirectoryAsync(source);
if (files.Count != 40 || files.Any(f => f.Length != 10485760)) throw new Exception("示例日志数量或大小不符合预期。");
var result = await service.CompressAsync(source, output);
var archived = await service.ListArchiveAsync(output);
if (archived.Count != 40) throw new Exception("ZIP 日志数量不正确。");
using (var archive = ZipFile.OpenRead(output))
{
    foreach (var file in files)
    {
        using var original = File.OpenRead(file.SourcePath);
        using var compressed = archive.GetEntry(file.RelativePath.Replace('\\', '/'))!.Open();
        var originalHash = await SHA256.HashDataAsync(original);
        var restoredHash = await SHA256.HashDataAsync(compressed);
        if (!originalHash.SequenceEqual(restoredHash)) throw new Exception("压缩往返内容不一致：" + file.RelativePath);
    }
}
var sample = await service.ReadAsync(archived[0]);
if (!sample.IsPreview || !sample.Content.StartsWith("[2026-10-")) throw new Exception("ZIP 预览格式错误。");
Console.WriteLine($"PASS 40 个文件压缩往返 SHA-256 全部一致，{result.OriginalBytes:N0} → {result.ArchiveBytes:N0} 字节，节省 {result.SavedPercentage:F1}%。");
Console.WriteLine("压缩包：" + output);
var days = await service.ListDaysAsync(source);
var bundlePath = Path.GetFullPath("artifacts/LogExample-Days.zip");
var bundleResult = await service.CompressDaysAsync(source, days.Select(d => d.Name).ToArray(), bundlePath);
using (var bundle = ZipFile.OpenRead(bundlePath))
{
    if (bundle.Entries.Count != 10) throw new Exception("每日内部 ZIP 数量不正确。");
    foreach (var day in days)
    {
        using var dayData = bundle.GetEntry(day.Name + ".zip")!.Open();
        using var daily = new ZipArchive(dayData, ZipArchiveMode.Read);
        var dayFiles = await service.ListDirectoryAsync(day.DirectoryPath);
        if (daily.Entries.Count != 4) throw new Exception("每日日志数量不正确。");
        foreach (var file in dayFiles)
        {
            using var original = File.OpenRead(file.SourcePath);
            using var restored = daily.GetEntry(file.RelativePath.Replace('\\', '/'))!.Open();
            var originalHash = await SHA256.HashDataAsync(original);
            var restoredHash = await SHA256.HashDataAsync(restored);
            if (!originalHash.SequenceEqual(restoredHash))
                throw new Exception("嵌套 ZIP 往返内容不一致：" + day.Name + "/" + file.RelativePath);
        }
    }
}
var bundleFiles = await service.ListArchiveAsync(bundlePath);
if (bundleFiles.Count != 40 || bundleFiles.Any(f => f.InnerArchiveName is null)) throw new Exception("总 ZIP 日志列表不正确。");
var bundlePreview = await service.ReadAsync(bundleFiles[0]);
if (!bundlePreview.IsPreview || !bundlePreview.Content.StartsWith("[2026-10-")) throw new Exception("总 ZIP 日志预览不正确。");
Console.WriteLine($"PASS 10 天 → 10 个每日 ZIP → 1 个总 ZIP，40 个日志 SHA-256 全部一致；总包 {bundleResult.ArchiveBytes:N0} 字节，可直接读取预览。");
Console.WriteLine("多天总包：" + bundlePath);
