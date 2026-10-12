using System.Collections.ObjectModel;

namespace OppoUnlockToolbox.ViewModels;

/// <summary>底部「实时输出」面板。</summary>
public sealed class LogPanelViewModel : ObservableObject
{
    private readonly LogBook _book;
    private bool _autoScroll = true;

    public LogPanelViewModel(LogBook book)
    {
        _book = book;
    }

    public ObservableCollection<LogRecord> Entries => _book.Entries;

    public bool AutoScroll
    {
        get => _autoScroll;
        set => Set(ref _autoScroll, value);
    }

    /// <summary>换主题后把所有已存在的日志行重新上色。</summary>
    public void RefreshBrushes()
    {
        foreach (var record in Entries)
            record.RefreshBrush();
    }
}
