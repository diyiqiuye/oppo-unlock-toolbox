

using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace OppoUnlockToolbox.Core;

public static class AppConfig
{

    public const string AppName = "OPPO 解锁工具箱";
    public const string AppShort = "OPPO 解锁工具箱";
    public const string AppTagline = "Find X5 Pro / Find X6 Pro · BL 解锁 + 临时 root · 七阶段工作流";
    public const string AppVersion = "0.2";
    public const string Credit = "临时 root 链路移植自 diyiqiuye/CVE-2025-21479-FX5P (GPL-3.0)；" +
                                 "DFRoot 通道基于 diabl0w/DFRoot；工作流整理自社区解锁教程";

    public const string UrlCve = "https://github.com/diyiqiuye/CVE-2025-21479-FX5P";
    public const string UrlCveReleases = UrlCve + "/releases";
    public const string UrlKernelSU = "https://github.com/tiann/KernelSU";
    public const string UrlPlatformTools = "https://developer.android.com/tools/releases/platform-tools";

    public const string AuthorName = "寄山海";
    public const string AuthorCoolapkId = "寄山海";
    public const string UrlAuthorGithub = "https://github.com/diyiqiuye";
    public const string UrlCoolapk = "https://www.coolapk.com/";

    public const string DevTmp = "/data/local/tmp";
    public const string RemoteWork = DevTmp + "/oppotool";
    public const string RemoteBackup = RemoteWork + "/backup";
    public const string RemoteImages = RemoteWork + "/images";
    public const string Ksud = DevTmp + "/ksud";
    public const string BlockByName = "/dev/block/by-name/";

    public static readonly string[] Partitions = { "abl_a", "abl_b", "ocdt" };
    public const int DdBs = 4096;

    public const string ImageAbl = "abl.img";
    public const string ImageOcdt = "ocdt.img";

    public static readonly string[] SupportedModels =
    {
        "PFEM00", "PFEM10", "CPH2305", "PGU110",
        "PGEM10",
    };
    public static readonly string[] KnownKVers = { "5.10.198", "5.10.168", "5.15.123" };

    public static readonly string[] GuardModules = { "oplus_security_guard" };

    public static readonly string[] PayloadFolderNames = { "resources", "downloads" };

    public static readonly IReadOnlyDictionary<string, string> ExploitKmiMap =
        new Dictionary<string, string>
        {
            ["5.10.198"] = "android12-5.10",
            ["5.10.168"] = "android12-5.10",
            ["5.15.123"] = "android13-5.15",
        };

    public static string ExploitKmiFor(string kver)
    {
        if (kver is { Length: > 0 } && ExploitKmiMap.TryGetValue(kver, out var mapped))
            return mapped;
        if (kver != null && kver.StartsWith("5.10.", StringComparison.Ordinal))
            return "android12-5.10";
        if (kver != null && kver.StartsWith("5.15.", StringComparison.Ordinal))
            return "android13-5.15";
        return "android12-5.10";
    }

    public const string KsuPackage = "me.weishu.kernelsu";

    // ── DFRoot（CVE-2026-43284 / DirtyFrag）临时 root 通道 ──

    /// <summary>上游官方 apk 的包名，安装包由用户在「资源下载」里装。</summary>
    public const string DfrootPackage = "df.root";
    public const string DfrootComponent = "df.root/.MainActivity";
    public const string DfrootLogTag = "dfroot";

    /// <summary>logcat 里出现这行说明 SU 守护进程起来了，也就是拿到 root 了。</summary>
    public const string DfrootOkMarker = "ksud start: SUCCESS";

    /// <summary>包名里带这些片段的，当成可能的 SU 管理器；DFRoot 需要它自带 libksud.so。</summary>
    public static readonly string[] SuManagerHints =
    {
        "kernelsu", "ksunext", "kowsu", "sukisu",
    };

    /// <summary>DFRoot 日志里出现这些行就说明这条路走不通，不用再等。</summary>
    public static readonly string[] DfrootFailMarkers =
    {
        "no SU manager selected",
        "is no longer installed",
        "libksud.so not found",
        "patch: ERROR",
        "timeout: ERROR",
        "ksud start: ERROR",
    };

    public static readonly string[] BundleBins = { "exploit_guard", "ksud", "su0" };

    public static readonly IReadOnlyDictionary<string, string[]> BundleAliases =
        new Dictionary<string, string[]>
        {
            ["exploit_guard"] = new[] { "exploit_guard", "exploitguard", "exploit", "guard" },
            ["ksud"] = new[] { "ksud", "kernelsu" },
            ["su0"] = new[] { "su0" },
        };

    public static readonly IReadOnlyDictionary<string, string> ImageRemoteName =
        new Dictionary<string, string> { ["abl"] = "abl.img", ["ocdt"] = "ocdt.img" };

    public const int MaxAttempts = 4;
    public const int LoadWaitS = 240;

    public static readonly string[] ExploitFailMarkers =
    {
        "bind kptr_restrict rc=-1",
        "guard: physical write FAILED",
        "guard: .text signature not in core",
    };

    private static string? _appDir;
    private static readonly object AppDirLock = new();

    public static string ResourceDir()
    {

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static IEnumerable<string> PayloadDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { ResourceDir(), AppDir() })
        {
            foreach (var name in PayloadFolderNames)
            {
                var dir = Path.Combine(root, name);
                if (seen.Add(dir))
                    yield return dir;
            }
        }
    }

    public static string? FindPayload(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        foreach (var dir in PayloadDirs())
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {

            }
        }
        return null;
    }

    /// <summary>
    /// 在 payload 目录（resources/ downloads/）里找 DFRoot 的官方 apk；找不到返回 null，
    /// 这时让用户自己去「资源下载」装。
    /// </summary>
    public static string? FindDfrootApk()
    {
        foreach (var dir in PayloadDirs())
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                var hit = Directory.EnumerateFiles(dir, "*dfroot*.apk")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (hit != null)
                    return hit;
            }
            catch
            {

            }
        }
        return null;
    }

    private static bool Writable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".write_probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? TestAppDir { get; set; }

    public static string AppDir()
    {
        lock (AppDirLock)
        {
            if (TestAppDir is { } testDir)
                return testDir;
            if (_appDir != null)
                return _appDir;
            var basePath = ResourceDir();
            string chosen;
            if (Writable(basePath))
            {
                chosen = basePath;
            }
            else
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var fallback = Path.Combine(
                    string.IsNullOrEmpty(localAppData) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : localAppData,
                    "OPPOUnlockToolbox");
                Directory.CreateDirectory(fallback);
                chosen = fallback;
            }
            _appDir = chosen;
            return chosen;
        }
    }

    public static string BackupDir()
    {
        var path = Path.Combine(AppDir(), "backup");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string LogDir()
    {
        var path = Path.Combine(AppDir(), "logs");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string StateFile() => Path.Combine(AppDir(), "state.json");

    public static string AssetPath(params string[] parts)
    {
        var all = new[] { ResourceDir(), "Assets" }.Concat(parts).ToArray();
        return Path.Combine(all);
    }

    private static readonly object ToolLock = new();
    private static readonly Dictionary<string, string?> ToolCache = new();

    private static IEnumerable<string> ToolCandidates(string name)
    {
        var exe = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        var resource = ResourceDir();
        yield return Path.Combine(resource, "tools", "platform-tools", exe);

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? resource;
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
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(dir.Trim(), name + (OperatingSystem.IsWindows() ? ".exe" : ""));
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
        var bundled = Path.Combine(ResourceDir(), "tools", "platform-tools");
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
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), name + suffix);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

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
