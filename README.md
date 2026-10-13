# 日志压缩工具

基于 .NET 10 和 WPF 的 Windows 日志查看与 ZIP 归档工具。可以打开本地日志目录或 ZIP，查看和编辑日志，并按目录或日期创建 ZIP 压缩包。

## 功能

- **日志浏览**：递归列出目录中的 `.log` 文件；可按文件名或相对路径筛选，并查看普通 ZIP 中的日志。
- **预览与编辑**：默认预览文件开头 128 KiB；需要完整编辑时可加载全文，保存前检测外部修改。全文编辑上限为 32 MiB。
- **编码支持**：读取 UTF-8、UTF-16、UTF-32，以及兼容 GB18030/GBK 的日志；保存时尽量保留识别到的编码、BOM 与换行。
- **ZIP 压缩**：支持整个目录压缩、按天分别压缩，以及将多天分别压缩后收纳进一个总 ZIP。按天模式允许选择日期；压缩支持进度、取消和覆盖确认。
- **查看压缩结果**：可直接浏览 ZIP 中的日志，也支持查看一层“总 ZIP → 每日 ZIP”的结构；编辑后保存会更新相应归档内容。
- **操作保护**：保存采用临时文件后替换；检测到来源文件或目标归档在操作期间被外部修改时会报告冲突。
- **键盘操作**：`Ctrl+O` 打开目录，`Ctrl+S` 保存日志，`Enter` 查看所选文件。

## 项目结构

| 项目或目录 | 用途 |
| --- | --- |
| `Main` | WPF 用户界面、MVVM 交互、窗口图标与文件对话框 |
| `LogCompression` | `ILogService` 及文件读取、编辑保存、ZIP 浏览和压缩实现；不依赖 WPF |
| `tests` | 使用 .NET 10 文件式应用运行的日志服务、日期压缩和 WPF 验证 |
| `tools` | 图标生成脚本 |
| `LogCompressionTool.slnx` | 包含上述两个项目的解决方案 |

## 环境要求

- Windows
- .NET 10 SDK

项目不依赖第三方 NuGet 包。

## 运行与构建

在仓库根目录运行：

```powershell
dotnet run --project Main/Main.csproj
```

构建 Release 版本：

```powershell
dotnet build LogCompressionTool.slnx -c Release
```

生成的程序位于 `Main/bin/Release/net10.0-windows/Main.exe`。也可以用 Visual Studio 打开 `LogCompressionTool.slnx` 并将 `Main` 设为启动项目。

## 使用说明

1. 选择 **打开目录** 或 **打开 ZIP**，然后在文件列表中选择日志。也可以用筛选框按名称或相对路径查找。
2. 预览较大的日志时，可选择加载全文编辑；保存按钮或 `Ctrl+S` 会写回原日志或对应 ZIP 条目。
3. 打开日志目录后，在压缩方式中选择：
   - **整个目录**：将目录及子目录中的 `.log` 文件压缩到一个 ZIP，并保留相对路径。
   - **按天分别压缩**：选择一个或多个日期，为每个日期生成单独 ZIP。
   - **多天合成总 ZIP**：先按日期分别生成 ZIP，再把这些每日 ZIP 放入一个总 ZIP；不会在磁盘留下每日中间包。
4. 可选择压缩等级。日期目录需位于日志根目录的直接子目录中，文件夹名采用 `年-月-日`，例如 `2026-10-1` 或 `2026-10-01`；目录内至少包含一个 `.log` 文件。
5. 压缩完成后选择 **查看刚生成的 ZIP**，即可检查归档中的日志。

压缩不会删除源日志。按天批处理会按日期顺序处理；取消或遇到错误时，已经完成的 ZIP 会保留，并显示完成情况。覆盖已有 ZIP 前会请求确认。

## 示例日志

若仓库目录下存在 `LogExample/Logs`，程序会在启动时自动打开它。生成的示例日志目录不包含在 Git 仓库中（已由 `.gitignore` 排除），以免将约 400 MiB 的测试数据提交到版本库。没有示例目录时，可直接打开自己的日志目录。

## 验证

以下命令在 Windows 和 .NET 10 SDK 环境中运行：

```powershell
dotnet run --file tests/Verify.cs
dotnet run --file tests/VerifyDays.cs
dotnet run --file tests/VerifySample.cs
dotnet run --file tests/VerifyUi.cs
```

`Verify.cs` 检查日志读取、编辑保存、编码、ZIP 往返、取消与外部修改冲突等行为；`VerifyDays.cs` 检查日期识别、每日 ZIP 和多天总包；`VerifySample.cs` 使用本地示例日志验证压缩前后的 SHA-256；`VerifyUi.cs` 在 STA 线程运行真实 WPF 窗口，检查主要交互、图标资源与绑定错误。示例验证需要先准备 `LogExample/Logs`。

## 应用图标

WPF 窗口和生成的应用程序共用 `Main/Assets/LogCompression.ico`。需要更新图标时运行：

```powershell
powershell -ExecutionPolicy Bypass -File tools/GenerateIcon.ps1
```
