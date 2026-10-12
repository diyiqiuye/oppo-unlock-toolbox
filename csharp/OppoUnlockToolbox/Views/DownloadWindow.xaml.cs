using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using OppoUnlockToolbox.ViewModels;

namespace OppoUnlockToolbox.Views;

public partial class DownloadWindow : Window
{
    private readonly DownloadViewModel _viewModel;

    public DownloadWindow(ResourceInstaller installer)
    {
        InitializeComponent();
        var dialogs = new DialogService { Owner = this };
        _viewModel = new DownloadViewModel(installer, dialogs);
        _viewModel.CloseRequested += Close;
        DataContext = _viewModel;
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.CancelAll();
        base.OnClosing(e);
    }
}
