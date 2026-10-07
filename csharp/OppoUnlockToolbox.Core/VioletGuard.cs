using System.Diagnostics;

namespace OppoUnlockToolbox.Core;

// 官方发布的 VX.Y_Release.exe 是自解压包：运行时把程序解到 %TEMP%\~<随机数>\，
// 再启动里面的 SmartTool.exe —— 真实进程名是 SmartTool，不是 VioletToolBox。
// 所以只看进程名里有没有 violet 永远找不到，必须认识 SmartTool 这个真实进程名。
//
// 只用进程名判断，不用窗口标题：浏览器开着「紫罗兰工具箱官网」时标题里也含"紫罗兰"，
// 用标题匹配会把浏览器误判成"工具箱已运行"，那是往危险方向放行。
public static class VioletGuard
{
    public const string ProcessName = "VioletToolBox";

    static readonly string[] KnownNames = { "VioletToolBox", "VioletBox", "SmartTool" };
    static readonly string[] Fragments = { "violet", "紫罗兰" };

    public static bool? OverrideForTests { get; set; }

    public static bool IsRunning() => FindRunning() is not null;

    public static bool IsViolet(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
            return false;

        foreach (var known in KnownNames)
            if (processName.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var fragment in Fragments)
            if (processName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    public static string? FindRunning()
    {
        if (OverrideForTests is { } forced)
            return forced ? ProcessName : null;

        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (IsViolet(proc.ProcessName))
                        return proc.ProcessName;
                }
                catch
                {

                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {

        }
        return null;
    }
}
