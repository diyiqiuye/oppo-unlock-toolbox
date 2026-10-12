using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using OppoUnlockToolbox.Core;

namespace OppoUnlockToolbox.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AuthorNameText.Text = AppMeta.AuthorName;
        CoolapkChipText.Text = $"酷安 @{AppMeta.AuthorCoolapkId}";
        VersionText.Text = "v" + AppMeta.AppVersion;
        BlessingText.Text = "祝你玩机愉快：解锁一次成功，数据分毫不丢，重启永不翻车 🎉";
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }

    private void OnGithubClick(object sender, RoutedEventArgs e) => OpenUrl(AppMeta.UrlAuthorGithub);

    private void OnCoolapkClick(object sender, RoutedEventArgs e) => OpenUrl(AppMeta.UrlCoolapk);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
