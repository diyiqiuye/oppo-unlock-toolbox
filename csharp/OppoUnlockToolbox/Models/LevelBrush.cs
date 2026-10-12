using System.Windows;
using System.Windows.Media;

namespace OppoUnlockToolbox.Models;

/// <summary>日志级别 → 画笔。资源里没配就退回一个能看的默认色。</summary>
public static class LevelBrush
{
    public static Brush Get(string level)
    {
        var key = level switch
        {
            "cmd" => "LvlCmd",
            "out" => "LvlOut",
            "ok" => "LvlOk",
            "warn" => "LvlWarn",
            "err" => "LvlErr",
            "note" => "LvlNote",
            _ => "LvlInfo",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Black;
    }
}
