namespace OppoUnlockToolbox.ViewModels;

/// <summary>阶段 → 图标 / 按钮文案 的界面映射；Core 里不掺这些显示用的东西。</summary>
public static class StageVisuals
{
    private static readonly Dictionary<string, string> IconKeys = new()
    {
        ["temproot"] = "key",
        ["push"] = "upload",
        ["backup"] = "save",
        ["write"] = "edit",
        ["fastboot"] = "unlock",
        ["verify"] = "shield",
        ["restore"] = "undo",
    };

    /// <summary>有单独文案的阶段 = 真正会改写设备的阶段，按钮走危险样式并要二次确认。</summary>
    private static readonly Dictionary<string, string> RunLabels = new()
    {
        ["write"] = "执行写入（改写分区）",
        ["fastboot"] = "执行解锁（清空数据）",
        ["restore"] = "执行还原（改写分区）",
    };

    public static string IconKey(string sid) => IconKeys.GetValueOrDefault(sid, "info");

    public static string RunLabel(string sid) => RunLabels.GetValueOrDefault(sid, "执行本阶段");

    public static bool RunIsDanger(string sid) => RunLabels.ContainsKey(sid);

    public static string Glyph(string state) => state switch
    {
        "active" => "▸",
        "done" => "✓",
        "fail" => "✗",
        "skip" => "–",
        _ => "○",
    };
}
