using Microsoft.Win32;
using System.Windows;

namespace Main.Services;

public enum UnsavedChoice { Save, Discard, Cancel }

public interface IDialogService
{
    string? ChooseFolder();
    string? ChooseArchive();
    string? ChooseOutputArchive(string directory);
    string? ChooseOutputFolder();
    bool ConfirmArchiveOverwrite(IReadOnlyList<string> paths);
    UnsavedChoice ConfirmUnsaved(string fileName);
}

public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public string? ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择日志目录", Multiselect = false };
        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }
    public string? ChooseArchive()
    {
        var dialog = new OpenFileDialog { Title = "打开日志压缩包", Filter = "ZIP 压缩包 (*.zip)|*.zip", CheckFileExists = true };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }
    public string? ChooseOutputArchive(string directory)
    {
        var dialog = new SaveFileDialog { Title = "保存日志压缩包", Filter = "ZIP 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip", AddExtension = true, OverwritePrompt = true,
            InitialDirectory = System.IO.Path.GetDirectoryName(directory),
            FileName = new System.IO.DirectoryInfo(directory).Name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }
    public UnsavedChoice ConfirmUnsaved(string fileName)
    {
        var result = MessageBox.Show(owner()!, $"“{fileName}”有未保存的修改。\n\n是：保存后继续\n否：放弃修改后继续\n取消：保留当前编辑", "未保存的修改",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return result switch { MessageBoxResult.Yes => UnsavedChoice.Save, MessageBoxResult.No => UnsavedChoice.Discard, _ => UnsavedChoice.Cancel };
    }
    public string? ChooseOutputFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择每日 ZIP 的保存目录", Multiselect = false };
        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }
    public bool ConfirmArchiveOverwrite(IReadOnlyList<string> paths)
    {
        var summary = string.Join("\n", paths.Take(8));
        if (paths.Count > 8) summary += $"\n……共 {paths.Count} 个 ZIP";
        return MessageBox.Show(owner()!, "以下每日 ZIP 已存在，是否覆盖？\n\n" + summary,
            "覆盖每日 ZIP", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
