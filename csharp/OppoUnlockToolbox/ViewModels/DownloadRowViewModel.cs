using System.IO;
using System.Windows.Input;

namespace OppoUnlockToolbox.ViewModels;

/// <summary>资源列表里的一行：名字、状态、进度条、下载/取消按钮。</summary>
public sealed class DownloadRowViewModel : ObservableObject
{
    private readonly ResourceItem _item;
    private readonly ResourceInstaller _installer;
    private readonly DialogService _dialogs;
    private readonly Func<string> _targetDir;

    private CancellationTokenSource? _cts;
    private string _status;
    private string _buttonText = "下载";
    private double _progressValue;
    private bool _progressVisible;
    private bool _progressIndeterminate;
    private string _statusTone = "muted";

    public DownloadRowViewModel(ResourceItem item, string group, ResourceInstaller installer,
        DialogService dialogs, Func<string> targetDir)
    {
        _item = item;
        _installer = installer;
        _dialogs = dialogs;
        _targetDir = targetDir;
        Group = group;
        Name = string.IsNullOrEmpty(item.Name) ? item.Key : item.Name;
        _status = StatusIdleText();
        ButtonCommand = new RelayCommand(OnButton);
    }

    public string Name { get; }
    public string Group { get; }
    public ICommand ButtonCommand { get; }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string ButtonText
    {
        get => _buttonText;
        private set => Set(ref _buttonText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public bool ProgressVisible
    {
        get => _progressVisible;
        private set => Set(ref _progressVisible, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => Set(ref _progressIndeterminate, value);
    }

    /// <summary>状态文案配色：muted / ok / warn / danger。</summary>
    public string StatusTone
    {
        get => _statusTone;
        private set => Set(ref _statusTone, value);
    }

    private bool Running => _cts is not null;

    /// <summary>别人（关窗口）要求停下载。</summary>
    public void Cancel() => _cts?.Cancel();

    private string StatusIdleText() =>
        !UrlConfigured
            ? "未配置直链（点右上角「编辑直链」）"
            : (Downloader.HasPayload(_item)
                ? "随包自带（点下载直接用本地文件，不走网络）"
                : "未下载");

    private bool UrlConfigured => !string.IsNullOrWhiteSpace(_item.Url);

    private async void OnButton()
    {
        if (Running)
        {
            _cts!.Cancel();
            return;
        }

        if (!UrlConfigured)
        {
            _dialogs.OpenTextFile(ResourceCatalog.EnsureTemplate());
            return;
        }

        var fileName = Downloader.GetUrlFileName(_item.Url);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = _item.Key + ".bin";

        var destFile = Path.Combine(_targetDir(), fileName);
        if (File.Exists(destFile) &&
            !_dialogs.Ask($"{fileName} 已经存在，重新下载会覆盖它。继续吗？", "文件已存在"))
            return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        ButtonText = "取消";
        ProgressVisible = true;
        ProgressValue = 0;

        var sourcePrefix = "";
        var progress = new Progress<DownloadProgress>(report =>
        {
            if (report.TotalBytes > 0)
            {
                ProgressIndeterminate = false;
                ProgressValue = report.Percent;
                Status = $"{sourcePrefix}{FileTools.FmtSize(report.ReceivedBytes)} / " +
                         $"{FileTools.FmtSize(report.TotalBytes)} · " +
                         $"{FileTools.FmtSize(report.SpeedBytesPerSecond)}/s";
            }
            else
            {
                ProgressIndeterminate = true;
                Status = $"{sourcePrefix}已下载 {FileTools.FmtSize(report.ReceivedBytes)} · " +
                         $"{FileTools.FmtSize(report.SpeedBytesPerSecond)}/s";
            }
        });
        var sourceNote = new Progress<string>(note =>
        {
            sourcePrefix = note + " · ";
            Status = sourcePrefix + "连接中…";
        });

        try
        {
            var (result, error) = await Downloader.DownloadWithFallbackAsync(
                _item, _targetDir(), fileName, progress, sourceNote, cts.Token);
            if (result is null)
            {
                Status = error;
                StatusTone = "danger";
            }
            else
            {
                var (summary, fillError) = _installer.Apply(_item.Key, result, _item.Unzip);
                Status = summary + (fillError.Length > 0 ? "（" + fillError + "）" : "");
                StatusTone = fillError.Length > 0 ? "warn" : "ok";
            }
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            ButtonText = "重新下载";
            ProgressVisible = false;
            ProgressIndeterminate = false;
        }
    }
}
