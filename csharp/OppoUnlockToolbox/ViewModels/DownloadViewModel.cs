using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace OppoUnlockToolbox.ViewModels;

/// <summary>「资源下载」窗口：列出 resources.json 里的条目，点了就下载 + 自动填参数。</summary>
public sealed class DownloadViewModel : ObservableObject
{
    private readonly ResourceInstaller _installer;
    private readonly DialogService _dialogs;
    private string _targetDir;

    public DownloadViewModel(ResourceInstaller installer, DialogService dialogs)
    {
        _installer = installer;
        _dialogs = dialogs;
        _targetDir = Path.Combine(AppPaths.AppDir(), "downloads");

        BrowseCommand = new RelayCommand(Browse);
        EditConfigCommand = new RelayCommand(() => _dialogs.OpenTextFile(ResourceCatalog.EnsureTemplate()));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());

        BuildRows();
    }

    public ObservableCollection<DownloadRowViewModel> Rows { get; } = new();

    public string TargetDir
    {
        get => _targetDir;
        private set => Set(ref _targetDir, value);
    }

    public ICommand BrowseCommand { get; }
    public ICommand EditConfigCommand { get; }
    public ICommand CloseCommand { get; }

    public event Action? CloseRequested;

    private void BuildRows()
    {
        var items = ResourceCatalog.Load();
        var anyGroup = items.Any(item => !string.IsNullOrWhiteSpace(item.Group));
        foreach (var item in items)
        {
            var group = !anyGroup ? "" : (string.IsNullOrWhiteSpace(item.Group) ? "其他资源" : item.Group);
            Rows.Add(new DownloadRowViewModel(item, group, _installer, _dialogs, () => _targetDir));
        }
    }

    private void Browse()
    {
        var chosen = _dialogs.PickDirectory();
        if (chosen is not null)
            TargetDir = chosen;
    }

    /// <summary>窗口关闭时把还在跑的下载都取消掉。</summary>
    public void CancelAll()
    {
        foreach (var row in Rows)
            row.Cancel();
    }
}
