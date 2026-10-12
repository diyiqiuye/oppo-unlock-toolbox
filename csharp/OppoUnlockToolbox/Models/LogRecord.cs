using System.Windows.Media;

namespace OppoUnlockToolbox.Models;

/// <summary>日志面板里的一行。</summary>
public sealed class LogRecord : ObservableObject
{
    public LogRecord(string time, string message, string level)
    {
        TimeText = time;
        MessageText = message;
        Level = level;
    }

    public string TimeText { get; }
    public string MessageText { get; }

    /// <summary>cmd / out / info / ok / warn / err / note，界面按它取色。</summary>
    public string Level { get; }

    public Brush Brush => LevelBrush.Get(Level);

    /// <summary>换主题之后重新取一次颜色。</summary>
    public void RefreshBrush() => Raise(nameof(Brush));
}
