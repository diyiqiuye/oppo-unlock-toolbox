

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using OppoUnlockToolbox.Core;

namespace OppoUnlockToolbox.Ui;

public partial class DownloadWindow : Window
{
    private readonly MainWindow _main;
    private readonly List<ResourceItem> _items;
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly List<(TextBlock Status, ProgressBar Bar, Button Button)> _rows = new();
    private string _targetDir;

    public DownloadWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _items = ResourceCatalog.Load();
        _targetDir = Path.Combine(AppConfig.AppDir(), "downloads");
        TargetDirText.Text = _targetDir;
        BuildRows();
    }

    private void BuildRows()
    {
        ItemsHost.Children.Clear();
        _rows.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];

            var group = item.Group ?? "";
            if (index == 0 || group != (_items[index - 1].Group ?? ""))
            {
                if (group.Length > 0 || _items.Any(i => (i.Group ?? "").Length > 0))
                {
                    var label = group.Length > 0 ? group : "其他资源";
                    if (index > 0)
                        ItemsHost.Children.Add(new Separator
                        {
                            Style = (Style)FindResource("AppSeparator"),
                            Margin = new Thickness(0, 4, 0, 0),
                        });
                    var chip = new Border
                    {
                        Style = (Style)FindResource("ChipAmber"),
                        Margin = new Thickness(0, index == 0 ? 0 : 10, 0, 2),
                    };
                    chip.Child = new TextBlock
                    {
                        Text = label,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("AccentText"),
                    };
                    ItemsHost.Children.Add(chip);
                }
            }

            var status = new TextBlock
            {
                FontSize = 12,
                Foreground = (Brush)FindResource("Ink3"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            status.Text = UrlConfigured(item)
                ? (Downloader.HasPayload(item) ? "随包自带（点下载直接用本地文件，不走网络）" : "未下载")
                : "未配置直链（点右上角「编辑直链」）";
            var bar = new ProgressBar
            {
                Style = (Style)FindResource("AppProgress"),
                Height = 8,
                Minimum = 0,
                Maximum = 100,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var button = new Button
            {
                Style = (Style)FindResource("BtnDefault"),
                Content = "下载",
                MinWidth = 72,
                Height = 30,
            };
            button.Click += (_, _) => OnItemButtonClick(item, status, bar, button);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var textStack = new StackPanel();
            textStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Name) ? item.Key : item.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            textStack.Children.Add(status);
            textStack.Children.Add(bar);
            grid.Children.Add(textStack);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
            button.VerticalAlignment = VerticalAlignment.Center;

            var rowBorder = new Border
            {
                Child = grid,
                Padding = new Thickness(0, 10, 0, 12),
                BorderBrush = (Brush)FindResource("Line"),

                BorderThickness = new Thickness(0, 0, 0,
                    index < _items.Count - 1 && (_items[index + 1].Group ?? "") == group ? 1 : 0),
            };
            ItemsHost.Children.Add(rowBorder);
            _rows.Add((status, bar, button));
        }
    }

    private static bool UrlConfigured(ResourceItem item) =>
        !string.IsNullOrWhiteSpace(item.Url);

    private async void OnItemButtonClick(ResourceItem item, TextBlock status, ProgressBar bar, Button button)
    {

        if (_running.TryGetValue(item.Key, out var existingCts))
        {
            existingCts.Cancel();
            return;
        }

        if (!UrlConfigured(item))
        {
            OnEditConfigClick(this, new RoutedEventArgs());
            return;
        }

        var fileName = Downloader.GetUrlFileName(item.Url);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = item.Key + ".bin";
        var destFile = Path.Combine(_targetDir, fileName);
        if (File.Exists(destFile))
        {
            var choice = MessageBox.Show(this,
                $"{fileName} 已经存在，重新下载会覆盖它。继续吗？",
                "文件已存在", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes)
                return;
        }

        var cts = new CancellationTokenSource();
        _running[item.Key] = cts;
        button.Content = "取消";
        bar.Visibility = Visibility.Visible;
        bar.Value = 0;
        status.Foreground = (Brush)FindResource("Ink2");

        var sourcePrefix = "";
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                bar.IsIndeterminate = false;
                bar.Value = p.Percent;
                status.Text = $"{sourcePrefix}{FileTools.FmtSize(p.ReceivedBytes)} / {FileTools.FmtSize(p.TotalBytes)} · {FileTools.FmtSize(p.SpeedBytesPerSecond)}/s";
            }
            else
            {
                bar.IsIndeterminate = true;
                status.Text = $"{sourcePrefix}已下载 {FileTools.FmtSize(p.ReceivedBytes)} · {FileTools.FmtSize(p.SpeedBytesPerSecond)}/s";
            }
        });
        var sourceNote = new Progress<string>(note =>
        {
            sourcePrefix = note + " · ";
            status.Text = sourcePrefix + "连接中…";
        });

        try
        {
            var (result, error) = await Downloader.DownloadWithFallbackAsync(
                item, _targetDir, fileName, progress, sourceNote, cts.Token);
            if (result is null)
            {
                status.Text = error;
                status.Foreground = (Brush)FindResource("Danger");
            }
            else
            {
                var (summary, fillError) = _main.ApplyAutoFill(item.Key, result, item.Unzip);
                status.Text = summary + (fillError.Length > 0 ? "（" + fillError + "）" : "");
                status.Foreground = (Brush)FindResource(fillError.Length > 0 ? "Warn" : "Ok");
            }
        }
        finally
        {
            _running.Remove(item.Key);
            button.Content = "重新下载";
            bar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择保存目录" };
        if (dialog.ShowDialog(this) == true)
        {
            _targetDir = dialog.FolderName;
            TargetDirText.Text = _targetDir;
        }
    }

    private void OnEditConfigClick(object sender, RoutedEventArgs e)
    {
        var path = ResourceCatalog.EnsureTemplate();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(this, path, "编辑直链", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        foreach (var cts in _running.Values)
            cts.Cancel();
        base.OnClosing(e);
    }
}
