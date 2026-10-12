// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using System.Text.RegularExpressions;

using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Shared;

internal static partial class BundleFiles
{
    internal static readonly byte[] ElfMagic = { 0x7f, (byte)'E', (byte)'L', (byte)'F' };

    public static string NormName(string name)
    {
        var ext = Path.GetExtension(name);
        string baseName;
        if (!new[] { ".exe", ".bin", ".elf", ".img", ".env", "" }.Contains(ext.ToLowerInvariant()))
            baseName = name;
        else
            baseName = ext.Length > 0 ? name[..^ext.Length] : name;
        return NonAlnumRegex().Replace(baseName.ToLowerInvariant().Replace("副本", ""), "");
    }

    [GeneratedRegex("[^a-z0-9]+")]
    internal static partial Regex NonAlnumRegex();

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    internal static partial Regex KverRegex();

    internal static bool IsElf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[4];
            return fs.Read(head, 0, 4) == 4 && head.AsSpan().SequenceEqual(ElfMagic);
        }
        catch
        {
            return false;
        }
    }

    internal static (Dictionary<string, string> Found, List<string> Missing, List<string> Listing)
        ResolveBins(Ctx ctx, string bundle)
    {
        var listing = new List<string>();
        if (Directory.Exists(bundle))
        {
            foreach (var full in Directory.GetFiles(bundle).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (File.Exists(full) && (FileTools.FileSize(full) ?? 0) > 0)
                    listing.Add(full);
            }
        }
        var elfs = listing.Where(IsElf).ToList();
        var found = new Dictionary<string, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in ExploitProfiles.BundleBins)
        {
            var aliases = (ExploitProfiles.BundleAliases.TryGetValue(key, out var list) ? list : new[] { key })
                .Select(NormName).ToArray();
            string? best = null;
            var bestScore = 0;
            foreach (var path in elfs)
            {
                if (used.Contains(path))
                    continue;
                var norm = NormName(Path.GetFileName(path));
                var score = 0;
                foreach (var alias in aliases)
                {
                    if (alias.Length == 0)
                        continue;
                    if (norm == alias)
                        score = Math.Max(score, 3);
                    else if (norm.StartsWith(alias, StringComparison.Ordinal))
                        score = Math.Max(score, 2);
                    else if (norm.Contains(alias))
                        score = Math.Max(score, 1);
                }
                if (score > bestScore)
                {
                    best = path;
                    bestScore = score;
                }
            }
            if (best != null)
            {
                found[key] = best;
                used.Add(best);
            }
        }

        var missing = ExploitProfiles.BundleBins.Where(k => !found.ContainsKey(k)).ToList();
        var freeElfs = elfs.Where(p => !used.Contains(p)).ToList();
        if (missing.Count > 0 && freeElfs.Count == missing.Count)
        {

            var rest = freeElfs.OrderByDescending(p => FileTools.FileSize(p) ?? 0).ToList();
            string[] order = { "ksud", "exploit_guard", "su0" };
            var picks = order.Where(missing.Contains).ToList();
            foreach (var (key, path) in picks.Zip(rest))
            {
                found[key] = path;
                used.Add(path);
                ctx.Log($"警告：按体积推断 {key} ← {Path.GetFileName(path)}，请确认没认错（认错只会让 exploit 跑不起来）。", "warn");
            }
            missing = ExploitProfiles.BundleBins.Where(k => !found.ContainsKey(k)).ToList();
        }
        return (found, missing, listing);
    }

    internal static (string Path, string Why) FindProfile(Ctx ctx, string bundle, string kver,
        string kernelBuild = "")
    {
        var folder = Path.Combine(bundle, "profiles");
        if (!Directory.Exists(folder))
        {
            folder = bundle;
            ctx.Log("bundle 里没有 profiles/ 目录，改在 bundle 根目录找 profile。", "note");
        }
        if (!Directory.Exists(folder))
            return ("", "目录不存在");
        var envs = Directory.GetFiles(folder)
            .Where(p => p.ToLowerInvariant().EndsWith(".env") && File.Exists(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (envs.Count == 0)
            return ("", "没有 .env 文件");

        if (kernelBuild.Length > 0)
        {
            var buildToken = NormName(kernelBuild);
            if (buildToken.Length > 0)
            {
                foreach (var path in envs)
                {
                    if (NormName(Path.GetFileName(path)) == buildToken)
                        return (path, $"按内核 build 精确匹配（{kernelBuild}）");
                }
            }
        }

        if (kver.Length > 0)
        {
            var token = NormName(kver);
            foreach (var path in envs)
            {
                if (token.Length > 0 && NormName(Path.GetFileName(path)).Contains(token))
                {
                    var warn = kernelBuild.Length > 0 && envs.Count > 1
                        ? "（注意：按版本号匹配；multiversion 包建议确认 build 是否一致）"
                        : "";
                    return (path, $"按内核版本 {kver} 匹配{warn}");
                }
            }
        }
        if (envs.Count == 1)
        {

            var found = KverRegex().Match(Path.GetFileName(envs[0]));
            if (found.Success && kver.Length > 0 && found.Value != kver)
                return ("", $"唯一的 profile 名字里写的是内核 {found.Value}，与设备内核 {kver} 不一致");
            return (envs[0], "只有一个 .env，直接采用");
        }
        return ("", $"有 {envs.Count} 个 .env，但没有一个能对上内核{(kver.Length > 0 ? " " + kver : " 未知")}");
    }
}
