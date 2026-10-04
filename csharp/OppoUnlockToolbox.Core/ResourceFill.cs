

using System.IO;

namespace OppoUnlockToolbox.Core;

public static class ResourceFill
{
    public sealed record Analysis(string? BundleDir, string? Abl, string? Ocdt);

    public static Analysis Analyze(string resourceKey, string resultPath, bool unzip)
    {
        var isFile = File.Exists(resultPath);
        var producedFiles = new List<string>();
        string? producedDir = null;

        if (isFile)
        {
            producedFiles.Add(resultPath);

            if (unzip && resultPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.ChangeExtension(resultPath, null)))
            {
                producedDir = Path.ChangeExtension(resultPath, null);
            }
        }
        else if (Directory.Exists(resultPath))
        {
            producedDir = resultPath;
        }

        if (producedDir != null)
        {
            try
            {
                producedFiles.AddRange(Directory.GetFiles(producedDir, "*", SearchOption.AllDirectories));
            }
            catch
            {

            }
        }

        string? bundleDir = null;
        if (producedDir != null)
        {
            bundleDir = FindBundleDir(producedDir);
        }

        string? abl = producedFiles.FirstOrDefault(f =>
        {
            var name = Path.GetFileName(f).ToLowerInvariant();
            return name.StartsWith("abl") && name.EndsWith(".img");
        });
        string? ocdt = producedFiles.FirstOrDefault(f =>
        {
            var name = Path.GetFileName(f).ToLowerInvariant();
            return name.Contains("ocdt") && name.EndsWith(".img");
        });

        return new Analysis(bundleDir, abl, ocdt);
    }

    private static string? FindBundleDir(string producedDir)
    {
        if (LooksLikeBundle(producedDir))
            return producedDir;
        try
        {
            foreach (var sub in Directory.GetDirectories(producedDir))
            {
                if (LooksLikeBundle(sub))
                    return sub;
            }
        }
        catch
        {

        }
        return null;
    }

    private static bool LooksLikeBundle(string dir)
    {
        try
        {
            return Directory.GetFiles(dir).Any(f =>
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                return name.StartsWith("exploit_guard") || name.StartsWith("ksud") || name.StartsWith("su0");
            });
        }
        catch
        {
            return false;
        }
    }
}
