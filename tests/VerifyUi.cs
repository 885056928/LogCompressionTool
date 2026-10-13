#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false
#:project ../Main/Main.csproj

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LogCompression;
using Main.Services;
using Main.ViewModels;

var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var thread = new Thread(() =>
{
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    var app = new Main.App(); app.InitializeComponent();
    app.DispatcherUnhandledException += (_, e) =>
    {
        e.Handled = true;
        finished.TrySetException(e.Exception);
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    };
    var messages = new StringWriter();
    PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
    PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(messages));
    Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "LogCompression-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var window = new Main.MainWindow();
            Assert(window.Icon is not null, "主窗口正确加载多尺寸 ICO 资源");
            var dialogs = new TestDialogs();
            var model = new MainViewModel(new LogService(), dialogs);
            window.DataContext = model;
            await model.OpenSampleCommand.ExecuteAsync();
            window.Show();
            await WaitReady(model);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await WaitReady(model);
            Assert(model.Files.Count == 40 && model.DocumentState == "快速预览", "示例自动加载/预览");
            Capture(window, "wpf-preview.png");
            window.Width = 1000; window.Height = 650;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, "wpf-narrow.png");
            Assert(model.Days.Count == 10 && model.Days[0].Day.Date == new DateOnly(2026, 10, 1)
                && model.Days[^1].Day.Date == new DateOnly(2026, 10, 13), "日期目录识别与排序");
            var modeBox = (ComboBox)window.FindName("CompressionModeBox");
            modeBox.SelectedIndex = 2;
            Assert(model.ShowDaySelection && !model.CompressCommand.CanExecute(null), "未选日期不能压缩");
            model.SelectAllDaysCommand.Execute(null);
            Assert(model.Days.All(d => d.IsSelected), "全选日期");
            model.ClearDaysCommand.Execute(null);
            Assert(model.Days.All(d => !d.IsSelected), "清空日期");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var dateToggle = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("DatePickerToggle");
            dateToggle.IsChecked = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var dateSurface = (FrameworkElement)window.FindName("DatePickerSurface");
            var firstCheck = Descendants(dateSurface).OfType<CheckBox>().First(c => c.DataContext == model.Days[0]);
            firstCheck.Focus();
            firstCheck.IsChecked = true;
            Assert(model.Days[0].IsSelected && model.CompressCommand.CanExecute(null), "日期勾选绑定和命令可用性");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(Descendants(window).OfType<Button>().Single(b => b.Command == model.CompressCommand).IsEnabled, "压缩按钮实际启用");
            Assert(((TextBox)window.FindName("Editor")).ActualHeight >= 60, "日期选择不能挤占窄窗口编辑区域");
            Capture(window, "wpf-days.png");
            CaptureSurface((FrameworkElement)window.FindName("CompressionToolbar"), "wpf-toolbar.png");
            CaptureSurface(dateSurface, "wpf-days-popup.png");
            firstCheck.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(firstCheck)!, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Assert(dateToggle.IsChecked == false && dateToggle.IsKeyboardFocused, "Escape 关闭日期面板并恢复焦点");
            modeBox.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(modeBox.IsDropDownOpen && modeBox.Items.Count == 3, "压缩方式下拉");
            var modeSurface = (FrameworkElement)modeBox.Template.FindName("DropDownSurface", modeBox);
            Assert(Math.Abs(modeSurface.ActualWidth - modeBox.ActualWidth) <= 1, "压缩方式弹出列表与字段等宽");
            CaptureSurface(modeSurface, "wpf-mode-dropdown.png");
            modeBox.Focus();
            modeBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(modeBox)!, Environment.TickCount, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Assert(!modeBox.IsDropDownOpen, "下拉列表 Escape 关闭");
            modeBox.SelectedIndex = 0;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            modeBox.Focus();
            Assert(modeBox.SelectedIndex == 0 && model.CompressionModeIndex == 0 && modeBox.IsKeyboardFocusWithin,
                "下拉关闭后焦点和选择已稳定");
            modeBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(modeBox)!, Environment.TickCount, System.Windows.Input.Key.Down)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            Assert(model.CompressionModeIndex == 1, "下拉框方向键选择同步到 ViewModel");
            modeBox.SelectedIndex = 0;
            var levelBox = (ComboBox)window.FindName("CompressionLevelBox");
            levelBox.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var levelSurface = (FrameworkElement)levelBox.Template.FindName("DropDownSurface", levelBox);
            Assert(Math.Abs(levelSurface.ActualWidth - levelBox.ActualWidth) <= 1, "压缩等级弹出列表与字段等宽");
            CaptureSurface(levelSurface, "wpf-level-dropdown.png");
            var levelPeer = new System.Windows.Automation.Peers.ComboBoxAutomationPeer(levelBox).GetChildren()
                .Single(peer => peer.GetName() == model.CompressionLevels[1]);
            ((System.Windows.Automation.Provider.ISelectionItemProvider)levelPeer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.SelectionItem)).Select();
            Assert(model.CompressionLevelIndex == 1, "弹出列表选项选择同步到 ViewModel");
            levelBox.IsDropDownOpen = false;
            levelBox.SelectedIndex = 0;
            modeBox.SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(Math.Abs(modeBox.ActualHeight - levelBox.ActualHeight) <= 1 &&
                Math.Abs(modeBox.ActualHeight - dateToggle.ActualHeight) <= 1, "压缩操作控件高度统一");
            modeBox.SetCurrentValue(UIElement.IsEnabledProperty, false);
            Assert(!modeBox.IsEnabled && !modeBox.IsDropDownOpen, "下拉框禁用状态");
            modeBox.SetCurrentValue(UIElement.IsEnabledProperty, true);
            Assert(modeBox.IsEnabled && System.Windows.Data.BindingOperations.GetBindingExpression(modeBox, UIElement.IsEnabledProperty) is not null,
                "禁用验证后保留原绑定");
            modeBox.SelectedIndex = 0;
            await model.LoadFullCommand.ExecuteAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(model.DocumentState == "可编辑" && model.EditorText.EndsWith("\r\n"), "10 MB 示例日志全文加载");
            model.Filter = "没有匹配的日志";
            Assert(model.FileView.IsEmpty, "文件筛选无结果");
            model.ClearFilterCommand.Execute(null);
            Assert(!model.FileView.IsEmpty, "清空筛选");
            var logs = Path.Combine(root, "Logs"); Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "edit.log"); File.WriteAllText(path, "[2026-10-13]启动:完成\r\n", new UTF8Encoding(true));
            await model.OpenSourceAsync(logs, false);
            Assert(model.DocumentState == "可编辑", "小日志全文读取");
            model.EditorText += "[2026-10-13]修改:保存\r\n";
            Assert(model.IsDirty && model.SaveCommand.CanExecute(null), "编辑状态与保存命令");
            dialogs.Choice = UnsavedChoice.Cancel;
            Assert(!await model.CanCloseAsync() && model.IsDirty, "取消关闭保留编辑");
            await model.SaveCommand.ExecuteAsync();
            Assert(!model.IsDirty && File.ReadAllText(path).Contains("修改:保存"), "界面保存命令");
            var lfLogs = Path.Combine(root, "LF"); Directory.CreateDirectory(lfLogs);
            var lfPath = Path.Combine(lfLogs, "lf.log");
            var lfText = "[2026-10-13]首行:中文\n[2026-10-13]尾行:完成\n";
            File.WriteAllText(lfPath, lfText, new UTF8Encoding(false));
            await model.OpenSourceAsync(lfLogs, false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(!model.IsDirty, "LF 原始日志加载不能误报已修改");
            var lfEditor = (TextBox)window.FindName("Editor");
            lfEditor.Select(lfEditor.Text.IndexOf("尾行:完成", StringComparison.Ordinal) + 3, 2);
            lfEditor.SelectedText = "修改";
            await model.SaveCommand.ExecuteAsync();
            Assert(File.ReadAllText(lfPath) == lfText.Replace("尾行:完成", "尾行:修改"), "LF 日志保存保持换行");
            await model.OpenSourceAsync(logs, false);
            var zip = Path.Combine(root, "test.zip");
            await new LogService().CompressAsync(logs, zip);
            await model.OpenSourceAsync(zip, true);
            model.EditorText += "[2026-10-13]归档:修改\r\n";
            await model.SaveCommand.ExecuteAsync();
            Assert(!model.IsDirty && model.EditorText.Contains("归档:修改"), "界面 ZIP 编辑保存");
            var archivedFiles = await new LogService().ListArchiveAsync(zip);
            Assert((await new LogService().ReadAsync(archivedFiles[0], true)).Content.Contains("归档:修改"), "归档已实际更新");
            var datedLogs = Path.Combine(root, "DaysLogs");
            foreach (var day in new[] { "2026-10-1", "2026-10-3", "2026-10-13" })
            {
                var directory = Path.Combine(datedLogs, day); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "day.log"), "[" + day + "]测试:原始内容\r\n", new UTF8Encoding(true));
            }
            await model.OpenSourceAsync(datedLogs, false);
            model.CompressionModeIndex = 1;
            model.Days[0].IsSelected = true;
            model.Days[2].IsSelected = true;
            dialogs.OutputFolder = Path.Combine(root, "Daily");
            Directory.CreateDirectory(dialogs.OutputFolder);
            await model.CompressCommand.ExecuteAsync();
            Assert(!model.StatusIsError && Directory.GetFiles(dialogs.OutputFolder, "*.zip").Length == 2
                && !File.Exists(Path.Combine(dialogs.OutputFolder, "2026-10-3.zip")), "按天界面命令只生成选中日期");
            var firstDaily = Path.Combine(dialogs.OutputFolder, "2026-10-1.zip");
            var beforeOverwrite = File.ReadAllBytes(firstDaily);
            await model.CompressCommand.ExecuteAsync();
            Assert(dialogs.OverwriteRequests == 1 && File.ReadAllBytes(firstDaily).SequenceEqual(beforeOverwrite), "拒绝覆盖已有每日 ZIP");
            dialogs.AllowOverwrite = true;
            var firstDayLog = Path.Combine(datedLogs, "2026-10-1", "day.log");
            File.WriteAllText(firstDayLog, "[2026-10-1]源文件:更新\r\n", new UTF8Encoding(true));
            await model.CompressCommand.ExecuteAsync();
            Assert(!File.ReadAllBytes(firstDaily).SequenceEqual(beforeOverwrite), "允许覆盖后每日 ZIP 实际更新");
            dialogs.OutputFolder = Path.Combine(root, "DailyCancel"); Directory.CreateDirectory(dialogs.OutputFolder);
            PropertyChangedEventHandler cancelAfterFirst = (_, e) =>
            {
                if (e.PropertyName == nameof(model.LastArchive) && model.IsBusy) model.CancelCommand.Execute(null);
            };
            model.PropertyChanged += cancelAfterFirst;
            try { await model.CompressCommand.ExecuteAsync(); }
            finally { model.PropertyChanged -= cancelAfterFirst; }
            Assert(Directory.GetFiles(dialogs.OutputFolder, "*.zip").Length == 1 && model.Status.Contains("1/2")
                && model.Status.Contains("保留"), "取消每日批次保留已完成 ZIP 并显示部分结果");
            dialogs.OutputFolder = Path.Combine(root, "DailyConflict"); Directory.CreateDirectory(dialogs.OutputFolder);
            var conflictTarget = Path.Combine(dialogs.OutputFolder, "2026-10-13.zip");
            PropertyChangedEventHandler createLaterTarget = (_, e) =>
            {
                if (e.PropertyName == nameof(model.LastArchive) && model.IsBusy) File.WriteAllText(conflictTarget, "外部新建");
            };
            model.PropertyChanged += createLaterTarget;
            try { await model.CompressCommand.ExecuteAsync(); }
            finally { model.PropertyChanged -= createLaterTarget; }
            Assert(model.StatusIsError && model.Status.Contains("1/2") && File.ReadAllText(conflictTarget) == "外部新建",
                "每日批次后续目标冲突保留外部文件和已完成 ZIP");
            model.CompressionModeIndex = 2;
            dialogs.OutputArchive = Path.Combine(root, "DaysBundle.zip");
            await model.CompressCommand.ExecuteAsync();
            Assert(!model.StatusIsError && File.Exists(dialogs.OutputArchive), "多天打包界面命令");
            await model.OpenLastArchiveCommand.ExecuteAsync();
            Assert(model.Files.Count == 2 && model.Files.All(f => f.InnerArchiveName is not null)
                && !model.ShowDaySelection, "总包查看与模式切换");
            model.EditorText += "[2026-10-13]界面嵌套:已保存\r\n";
            await model.SaveCommand.ExecuteAsync();
            Assert(!model.IsDirty && !model.StatusIsError, "总包内日志编辑保存");
            var nestedFiles = await new LogService().ListArchiveAsync(dialogs.OutputArchive);
            Assert((await new LogService().ReadAsync(nestedFiles[0], true)).Content.Contains("界面嵌套:已保存"), "嵌套修改已写回");
            Assert(!File.ReadAllText(firstDayLog).Contains("界面嵌套"), "源日志保留");
            model.CompressionModeIndex = 0;
            await model.OpenSourceAsync(Path.Combine(root, "missing.zip"), true);
            Assert(model.StatusIsError && model.HasDocument, "错误反馈保留原工作区");
            Capture(window, "wpf-error.png");
            var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
            await model.OpenSourceAsync(empty, false);
            Assert(!model.HasDocument && model.FileView.IsEmpty, "空目录状态");
            Capture(window, "wpf-empty.png");
            var bindingErrors = messages.ToString();
            if (!string.IsNullOrWhiteSpace(bindingErrors)) throw new Exception("WPF 绑定警告/错误：" + bindingErrors);
            var editor = (TextBox)window.FindName("Editor");
            var editorPoint = editor.TranslatePoint(new Point(), window);
            Assert(editorPoint.X >= 0 && editorPoint.Y >= 0 && editorPoint.X + editor.ActualWidth <= window.ActualWidth,
                "窄窗口编辑区范围");
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.SetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("PASS WPF 日期排序/勾选/全选/清空、每日 ZIP/覆盖确认、多天总包压缩/查看/保存、原有日志编辑与状态流程；绑定警告和错误为 0。");
            finished.SetResult();
        }
        catch (Exception ex) { finished.SetException(ex); }
        finally { Directory.Delete(root, true); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
    });
    Dispatcher.Run();
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
await finished.Task.WaitAsync(TimeSpan.FromSeconds(90));

static async Task WaitReady(MainViewModel model)
{
    if (!model.IsBusy) return;
    var complete = new TaskCompletionSource();
    PropertyChangedEventHandler? listener = null;
    listener = (_, e) => { if (e.PropertyName == nameof(model.IsBusy) && !model.IsBusy) complete.TrySetResult(); };
    model.PropertyChanged += listener;
    try { if (!model.IsBusy) complete.TrySetResult(); await complete.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
    finally { model.PropertyChanged -= listener; }
}
static void Assert(bool value, string scenario) { if (!value) throw new Exception("验证失败：" + scenario); }
static IEnumerable<DependencyObject> Descendants(DependencyObject root)
{
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var child = VisualTreeHelper.GetChild(root, i);
        yield return child;
        foreach (var nested in Descendants(child)) yield return nested;
    }
}
static void Capture(Window window, string name)
{
    window.UpdateLayout();
    var surface = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
    CaptureSurface(surface, name);
}
static void CaptureSurface(FrameworkElement surface, string name)
{
    surface.UpdateLayout();
    var image = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
    // VisualBrush 去掉控件在父布局中的偏移，使操作区等局部预览也能完整渲染。
    var drawing = new DrawingVisual();
    using (var context = drawing.RenderOpen())
        context.DrawRectangle(new VisualBrush(surface) { Stretch = Stretch.Fill }, null,
            new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
    image.Render(drawing);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
    Directory.CreateDirectory("artifacts");
    using var file = File.Create(Path.Combine("artifacts", name)); encoder.Save(file);
}
sealed class TestDialogs : IDialogService
{
    public UnsavedChoice Choice { get; set; } = UnsavedChoice.Cancel;
    public string? ChooseFolder() => null;
    public string? ChooseArchive() => null;
    public string? OutputArchive { get; set; }
    public string? OutputFolder { get; set; }
    public bool AllowOverwrite { get; set; }
    public int OverwriteRequests { get; private set; }
    public string? ChooseOutputArchive(string directory) => OutputArchive;
    public string? ChooseOutputFolder() => OutputFolder;
    public bool ConfirmArchiveOverwrite(IReadOnlyList<string> paths) { OverwriteRequests++; return AllowOverwrite; }
    public UnsavedChoice ConfirmUnsaved(string fileName) => Choice;
}
