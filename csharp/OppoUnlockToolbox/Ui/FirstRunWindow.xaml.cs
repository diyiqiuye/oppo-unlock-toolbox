using System.Windows;
using System.Windows.Threading;

namespace OppoUnlockToolbox.Ui;

public partial class FirstRunWindow : Window
{
    private const int WaitSeconds = 5;

    private readonly DispatcherTimer _timer;
    private int _remain = WaitSeconds;

    public FirstRunWindow()
    {
        InitializeComponent();
        ConfirmBtn.Content = $"确定（{_remain}s）";
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remain--;
            if (_remain > 0)
            {
                ConfirmBtn.Content = $"确定（{_remain}s）";
                return;
            }
            _timer.Stop();
            ConfirmBtn.Content = "确定";
            ConfirmBtn.IsEnabled = true;
            ConfirmBtn.Focus();
        };
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmBtn.IsEnabled)
            return;
        DialogResult = true;
    }
}
