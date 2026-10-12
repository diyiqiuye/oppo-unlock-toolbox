using System.Windows;
using System.Windows.Input;

namespace OppoUnlockToolbox.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmLabel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmText.Text = confirmLabel.Length > 0 ? confirmLabel : "执行";
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
