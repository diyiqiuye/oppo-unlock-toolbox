using System.Diagnostics;
using System.Windows;
using OppoUnlockToolbox.Core;

namespace OppoUnlockToolbox.Ui;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AuthorNameText.Text = AppConfig.AuthorName;
        CoolapkChipText.Text = $"酷安 @{AppConfig.AuthorCoolapkId}";
        BlessingText.Text = "祝你玩机愉快：解锁一次成功，数据分毫不丢，重启永不翻车 🎉";
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

    private void OnGithubClick(object sender, RoutedEventArgs e) => OpenUrl(AppConfig.UrlAuthorGithub);

    private void OnCoolapkClick(object sender, RoutedEventArgs e) => OpenUrl(AppConfig.UrlCoolapk);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
