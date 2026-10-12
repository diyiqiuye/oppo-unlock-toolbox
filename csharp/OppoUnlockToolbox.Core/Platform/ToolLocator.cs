using System.Diagnostics;
using System.IO;

namespace OppoUnlockToolbox.Core.Platform;

/// <summary>
/// 找 adb / fastboot：先看随包目录，再看环境变量与常见安装位置，最后看 PATH。
/// 结果按名字缓存，避免每步都去戳磁盘。
/// </summary>
public static class ToolLocator
{
    private static readonly object ToolLock = new();
    private static readonly Dictionary<string, string?> ToolCache = new();

    private static IEnumerable<string> ToolCandidates(string name)
    {
        var exe = name + ".exe";
        var resource = AppPaths.ResourceDir();
        yield return Path.Combine(resource, "tools", "platform-tools", exe);

        var exeDir = resource;
        try
        {
            var main = Process.GetCurrentProcess().MainModule;
            if (main?.FileName is { Length: > 0 } exePath)
                exeDir = Path.GetDirectoryName(exePath) ?? resource;
        }
        catch
        {
        }
        yield return Path.Combine(exeDir, "tools", "platform-tools", exe);

        foreach (var envName in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT", "ANDROID_SDK" })
        {
            var root = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "platform-tools", exe);
        }

        foreach (var drive in new[] { "C:", "D:", "E:" })
        {
            yield return $@"{drive}\Android\platform-tools\{exe}";
            yield return $@"{drive}\Android\Sdk\platform-tools\{exe}";
            yield return $@"{drive}\platform-tools\{exe}";
        }

        foreach (var envName in new[] { "LOCALAPPDATA", "USERPROFILE", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var root = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "Android", "Sdk", "platform-tools", exe);
                yield return Path.Combine(root, "platform-tools", exe);
            }
        }
    }

    public static string? FindTool(string name)
    {
        lock (ToolLock)
        {
            if (ToolCache.TryGetValue(name, out var cached))
                return cached;
            string? found = null;
            foreach (var candidate in ToolCandidates(name))
            {
                if (File.Exists(candidate))
                {
                    found = candidate;
                    break;
                }
            }
            if (found is null)
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(dir.Trim(), name + ".exe");
                    if (File.Exists(candidate))
                    {
                        found = candidate;
                        break;
                    }
                }
            }
            ToolCache[name] = found;
            return found;
        }
    }

    public static void SetToolOverride(string name, string? path)
    {
        lock (ToolLock)
        {
            ToolCache[name] = path;
        }
    }

    public static string ToolSource(string name)
    {
        var path = FindTool(name);
        if (string.IsNullOrEmpty(path))
            return "未找到";
        var bundled = Path.Combine(AppPaths.ResourceDir(), "tools", "platform-tools");
        try
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            if (string.Equals(dir, bundled, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(bundled + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "内置";
        }
        catch
        {

        }
        if (string.Equals(path, Which(name), StringComparison.OrdinalIgnoreCase))
            return "系统 PATH";
        return "本地 SDK";
    }

    private static string? Which(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var suffix = ".exe";
        foreach (var dir in pathEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), name + suffix);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>跑 `&lt;tool&gt; version` 并取第一行有意义的输出；失败返回空串。</summary>
    public static string ToolVersion(string name)
    {
        var path = FindTool(name);
        if (string.IsNullOrEmpty(path))
            return "";
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (proc is null)
                return "";
            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("version", StringComparison.OrdinalIgnoreCase))
                    return line;
            }
            var first = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            return first?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
