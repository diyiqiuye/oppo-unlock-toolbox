using System.Diagnostics;

namespace OppoUnlockToolbox.Core;

public static class VioletGuard
{
    public const string ProcessName = "VioletToolBox";

    public static bool? OverrideForTests { get; set; }

    public static bool IsRunning() => FindRunning() is not null;

    public static string? FindRunning()
    {
        if (OverrideForTests is { } forced)
            return forced ? ProcessName : null;

        try
        {
            foreach (var name in new[] { "VioletToolBox", "VioletBox" })
            {
                var direct = Process.GetProcessesByName(name);
                if (direct.Length > 0)
                {
                    foreach (var proc in direct)
                        proc.Dispose();
                    return name;
                }
            }

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var processName = proc.ProcessName ?? "";
                    if (processName.Contains("violettoolbox", StringComparison.OrdinalIgnoreCase) ||
                        processName.Contains("violetbox", StringComparison.OrdinalIgnoreCase))
                        return processName;
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
