using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OppoUnlockToolbox.Views;

namespace OppoUnlockToolbox.Services;

/// <summary>
/// 所有弹窗 / 系统对话框 / 打开外部东西，都从这里走。
/// ViewModel 因此不直接 new 窗口，也不直接摸 MessageBox 和剪贴板。
/// </summary>
public sealed class DialogService
{
    public Window? Owner { get; set; }

    public bool Confirm(string title, string message, string confirmLabel)
    {
        var dialog = new ConfirmWindow(title, message, confirmLabel);
        if (Owner is not null)
            dialog.Owner = Owner;
        return dialog.ShowDialog() == true;
    }

    public void Info(string message, string title) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warn(string message, string title) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Ask(string message, string title) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

    public string? PickDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "选择目录" };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickImage(string label)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"选择{label}",
            Filter = "镜像文件 (*.img)|*.img|所有文件 (*.*)|*.*",
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? AskSaveLogPath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存日志",
            Filter = "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            FileName = suggestedName,
            InitialDirectory = AppPaths.LogDir(),
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public void SetClipboard(string text) => Clipboard.SetText(text);

    public void ShowAbout()
    {
        var window = new AboutWindow();
        if (Owner is not null)
            window.Owner = Owner;
        window.ShowDialog();
    }

    /// <summary>弹「使用须知」，用户点了确定返回 true。</summary>
    public bool ShowFirstRun()
    {
        var window = new FirstRunWindow();
        if (Owner is not null)
            window.Owner = Owner;
        return window.ShowDialog() == true;
    }

    /// <summary>打开「资源下载」窗口，关掉之后调用方负责重画参数表单。</summary>
    public void ShowResources(ResourceInstaller installer)
    {
        var window = new DownloadWindow(installer);
        if (Owner is not null)
            window.Owner = Owner;
        window.ShowDialog();
    }

    public void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(Owner!, path, "打开目录", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {

        }
    }

    public void OpenTextFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            MessageBox.Show(Owner!, path, Path.GetFileName(path), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
