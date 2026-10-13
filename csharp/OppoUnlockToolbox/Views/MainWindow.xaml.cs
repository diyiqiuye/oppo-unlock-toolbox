using System.Windows;
using OppoUnlockToolbox.ViewModels;

namespace OppoUnlockToolbox.Views;

/// <summary>
/// 主窗口只管窗口本身的事：自绘标题栏的窗控、最大化时的边距，以及把滚动/自动滚到底接到视口上。
/// 其余状态与动作都在 MainViewModel 里。
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Closed += OnClosed;
        StateChanged += (_, _) => UpdateMaximizeState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (DataContext is not MainViewModel viewModel)
            return;

        _viewModel = viewModel;
        viewModel.ScrollTopRequested += () => RightScroll.ScrollToTop();
        viewModel.LogAppended += OnLogAppended;
        viewModel.Start();
        UpdateMaximizeState();
    }

    /// <summary>
    /// 首次运行的「使用须知」必须等到这里才弹。
    /// 之前是在 Loaded 里同步 ShowDialog()，那时首帧还没画出来，模态循环又抢在渲染前面
    /// 跑起来 —— 用户看到的就是主页面一片空白，非得把弹窗关掉才显出来。
    /// ContentRendered 保证内容已经画完，这时再开模态循环就没这个问题。
    /// </summary>
    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        _viewModel?.ShowFirstRunNoticeIfNeeded();
    }

    private void OnLogAppended(LogRecord record)
    {
        if (_viewModel?.Logs.AutoScroll == true)
            LogList.ScrollIntoView(record);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 无边框窗口最大化时内容会溢出到屏幕外，按调整边框的厚度补回来；
    /// 顺便把最大化按钮的图标换成「还原」。
    /// </summary>
    private void UpdateMaximizeState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Key = maximized ? "window_restore" : "window_max";
        var border = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Margin = maximized
            ? new Thickness(border.Left, border.Top, border.Right, border.Bottom)
            : new Thickness(0);
    }

    private void OnClosed(object? sender, EventArgs e) => _viewModel?.Dispose();
}
