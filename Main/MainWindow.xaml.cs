using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using LogCompression;
using Main.Services;
using Main.ViewModels;

namespace Main;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _checkingClose;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new LogService(), new DialogService(() => this));
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // 只自动打开项目内的示例，不记忆或扫描用户其他目录。
        if (DataContext is MainViewModel viewModel) await viewModel.OpenSampleCommand.ExecuteAsync();
    }

    private void OnDatePickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DatePickerToggle.IsChecked = false;
        DatePickerToggle.Focus();
        e.Handled = true;
    }

    private void OnDatePickerDone(object sender, RoutedEventArgs e)
    {
        DatePickerToggle.IsChecked = false;
        DatePickerToggle.Focus();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_checkingClose) return;
        _checkingClose = true;
        try
        {
            if (DataContext is MainViewModel viewModel && await viewModel.CanCloseAsync())
            {
                _allowClose = true;
                // 无修改时检查会同步完成，必须让当前 Closing 事件退出后再触发关闭。
                await Dispatcher.InvokeAsync(Close);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法关闭窗口", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _checkingClose = false; }
    }
}
