using System.IO;

namespace OppoUnlockToolbox.Core.Platform;

/// <summary>
/// 程序自身和运行期数据落在哪里：程序目录、可写工作目录、日志、备份、随包资源。
/// </summary>
public static class AppPaths
{
    /// <summary>随包资源可能出现的目录名（挨着 exe 放）。</summary>
    public static readonly string[] PayloadFolderNames = { "resources", "downloads" };

    private static string? _appDir;
    private static readonly object AppDirLock = new();

    /// <summary>自检/测试用：把工作目录钉在一个临时位置。</summary>
    public static string? TestAppDir { get; set; }

    public static string ResourceDir() =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

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

    /// <summary>
    /// 程序目录可写就直接用程序目录（绿色版），否则退回 %LOCALAPPDATA%\OPPOUnlockToolbox。
    /// </summary>
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
                    string.IsNullOrEmpty(localAppData)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                        : localAppData,
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
}
