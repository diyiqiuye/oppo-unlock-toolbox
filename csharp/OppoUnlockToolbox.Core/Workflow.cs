

using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OppoUnlockToolbox.Core;

public sealed class Field
{
    public string Key { get; }
    public string Label { get; }
    public string Kind { get; }
    public string Hint { get; }
    public object? Default { get; }
    public (string Value, string Label)[] Choices { get; }
    public int? Width { get; }

    public Field(string key, string label, string kind, string hint = "",
        object? defaultValue = null, (string, string)[]? choices = null, int? width = null)
    {
        Key = key;
        Label = label;
        Kind = kind;
        Hint = hint;
        Default = defaultValue;
        Choices = choices ?? Array.Empty<(string, string)>();
        Width = width;
    }
}

public sealed class Stage
{
    public string Sid { get; }
    public int No { get; }
    public string Title { get; }
    public Field[] Fields { get; }
    public string Danger { get; }
    public Func<Dictionary<string, object?>, string[]>? Preview { get; }
    public Func<Dictionary<string, object?>, List<Step>> Build { get; }
    public bool NeedsRoot { get; }

    public Stage(string sid, int no, string title, Field[]? fields = null, string danger = "",
        Func<Dictionary<string, object?>, string[]>? preview = null,
        Func<Dictionary<string, object?>, List<Step>>? build = null,
        bool needsRoot = false)
    {
        Sid = sid;
        No = no;
        Title = title;
        Fields = fields ?? Array.Empty<Field>();
        Danger = danger;
        Preview = preview;
        Build = build ?? (_ => new List<Step>());
        NeedsRoot = needsRoot;
    }
}

public static partial class Workflow
{
    private static Adb adb => AdbDefault.Instance;

    private static string Now() => DateTime.Now.ToString("MMdd_HHmmss");

    private static void Sleep(Ctx ctx, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            ctx.CheckCancel();
            var remain = (end - DateTime.UtcNow).TotalSeconds;
            Adb.CancellableSleep((int)(Math.Min(1.0, Math.Max(0.05, remain)) * 1000), ctx.Cancel);
        }
    }

    private static string DeviceOnline(Ctx ctx, bool needBoot = true)
    {
        List<Dictionary<string, string>> devices;
        try
        {
            devices = adb.Devices();
        }
        catch (Exception exc)
        {
            throw new StepFailException($"无法调用 adb：{exc.Message}");
        }
        var states = devices.Select(d => d["state"]).ToList();
        if (devices.Count == 0)
        {
            if (adb.FastbootDevices().Count > 0)
                throw new StepFailException("设备当前在 fastboot 模式。请先让手机重启回系统（fastboot reboot）再做这一步。");
            throw new StepFailException("没有检测到设备：请插好数据线、开启 USB 调试，并在手机上允许本机授权。");
        }
        if (states.Contains("unauthorized"))
            throw new StepFailException("设备未授权：请在手机屏幕上点『允许 USB 调试』，然后重试。");
        if (!states.Contains("device"))
            throw new StepFailException($"设备状态是 {string.Join("/", states)}，请等系统起来或重新插拔数据线后重试。");
        var serial = devices.First(d => d["state"] == "device")["serial"];
        if (adb.Serial.Length == 0)
            adb.SetSerial(serial);
        ctx.Extras["serial"] = serial;
        if (needBoot && adb.ShellText("getprop sys.boot_completed").Trim() != "1")
        {
            ctx.Log("系统尚未启动完成，等待开机…", "warn");
            if (!adb.WaitForBoot(240000, ctx.Cancel))
                throw new StepFailException("等不到系统启动完成（sys.boot_completed != 1）。");
        }
        return serial;
    }

    private static void RequireRoot(Ctx ctx, string hint = "")
    {
        ctx.Status("校验临时 root 通道");
        bool ok;
        try
        {
            ok = adb.RootOk(90000, ctx.Cancel);
        }
        catch (Exception exc)
        {
            throw new StepFailException($"root 校验失败：{exc.Message}");
        }
        if (!ok)
        {
            throw new StepFailException(
                "当前没有 uid=0 通道。" + hint +
                " 请先完成『临时 Root』（解锁清数据后临时 root 会失效，需要重跑一次）。");
        }
        ctx.Log("root 通道就绪：uid=0", "ok");
        ctx.Extras["root"] = true;
    }

    private static long RequireFile(string path, string label)
    {
        if (string.IsNullOrEmpty(path))
            throw new StepFailException($"还没有选择{label}。");
        if (!File.Exists(path))
            throw new StepFailException($"{label} 不存在：{path}");
        var size = FileTools.FileSize(path) ?? 0;
        if (size <= 0)
            throw new StepFailException($"{label} 是空文件：{path}");
        return size;
    }

    private static string Slot(Ctx ctx)
    {
        var want = ctx.Param("slot", "auto").Trim();
        if (want is "a" or "b")
        {
            ctx.Log($"槽位：_{want}（手动指定）", "note");
            ctx.Extras["slot"] = want;
            return want;
        }
        var detected = adb.ActiveSlot();
        if (detected.Length > 0)
        {
            ctx.Log($"活动槽：_{detected}（自动检测 ro.boot.slot_suffix）", "ok");
            ctx.Extras["slot"] = detected;
            return detected;
        }
        ctx.Log("读不到 ro.boot.slot_suffix，按教程默认值 _b 处理。" +
                "如果不确定，请在『写入解锁文件』里手动指定槽位。", "warn");
        ctx.Extras["slot"] = "b";
        return "b";
    }

    private static void EnsureDirs(Ctx ctx)
    {
        foreach (var remote in new[] { AppConfig.RemoteWork, AppConfig.RemoteImages, AppConfig.RemoteBackup })
        {
            var res = adb.Shell($"mkdir -p {remote}", timeoutMs: 30000, cancel: ctx.Cancel, allowFail: true);
            if (!res.Ok)
                throw new StepFailException($"无法在设备上创建目录 {remote}（{res.Error}）");
        }
        ctx.Log($"设备工作目录：{AppConfig.RemoteWork}", "note");
    }

    private static string DeviceSerial()
    {
        foreach (var key in new[] { "ro.serialno", "ro.boot.serialno" })
        {
            var value = adb.ShellText($"getprop {key}", timeoutMs: 20000).Trim();
            if (value.Length > 0)
                return value;
        }
        return "";
    }

    private static bool LooksLikeWrittenImage(string part, string path)
    {
        var writtenMd5 = StateStore.WrittenMd5(part);
        var writtenSize = StateStore.WrittenSize(part);
        if (writtenMd5.Length == 0 || writtenSize <= 0)
            return false;
        if (FileTools.Md5File(path) == writtenMd5)
            return true;
        var head = FileTools.Md5File(path, default, writtenSize);
        return head.Length > 0 && head == writtenMd5;
    }

    private static Dictionary<string, string> ScanRestoreSources(Ctx ctx, string slot)
    {
        var sources = new Dictionary<string, string>();
        var overrideDir = ctx.Param("backup_dir", "").Trim();
        var parts = new[] { "ocdt", "abl_" + slot };
        var manual = new Dictionary<string, string>
        {
            ["ocdt"] = ctx.Param("ocdt_file", "").Trim(),
            ["abl_" + slot] = ctx.Param("abl_file", "").Trim(),
        };

        foreach (var part in parts)
        {
            if (manual[part].Length == 0)
                continue;
            if (!File.Exists(manual[part]))
                throw new StepFailException($"界面上指定的 {part} 备份文件不存在：{manual[part]}");
            sources[part] = manual[part];
            ctx.Log($"{part} → {Path.GetFileName(manual[part])}（界面指定，优先采用）", "ok");
        }

        var need = parts.Where(p => !sources.ContainsKey(p)).ToList();
        if (need.Count > 0 && overrideDir.Length > 0 && Directory.Exists(overrideDir))
        {
            var candidates = Directory.GetFiles(overrideDir)
                .Where(p => File.Exists(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            foreach (var part in need.ToList())
            {
                var token = NormName(part);
                foreach (var path in candidates)
                {
                    if (token.Length > 0 && NormName(Path.GetFileName(path)).Contains(token))
                    {
                        sources[part] = path;
                        need.Remove(part);
                        ctx.Log($"{part} → {Path.GetFileName(path)}（文件名匹配）", "note");
                        break;
                    }
                }
            }

            foreach (var part in need.ToList())
            {
                var size = adb.PartitionSize(part, cancel: ctx.Cancel);
                if (size is not { } partSize || partSize <= 0)
                    continue;
                var hits = candidates.Where(p => (FileTools.FileSize(p) ?? -1) == partSize).ToList();
                if (hits.Count == 1)
                {
                    sources[part] = hits[0];
                    need.Remove(part);
                    ctx.Log($"{part} → {Path.GetFileName(hits[0])}（体积与设备分区一致：{FileTools.FmtSize(partSize)}）", "ok");
                }
                else if (hits.Count > 1)
                {
                    ctx.Log($"{part} 有 {hits.Count} 个体积相同的候选文件，名字也认不出来，请在界面上直接点名。", "warn");
                }
            }
        }

        need = parts.Where(p => !sources.ContainsKey(p)).ToList();
        if (need.Count > 0)
        {
            var now = DeviceSerial();
            foreach (var part in need)
            {
                var record = StateStore.NewestStockBackup(part);
                if (record is null)
                    continue;
                var local = StateStore.RecordString(record, "local");
                var serial = StateStore.RecordString(record, "serial");
                if (serial.Length > 0 && now.Length > 0 && serial != now)
                    throw new StepFailException(
                        $"账本里的 {part} 备份来自另一台设备（序列号 {serial}），当前设备是 {now} —— 拒绝拿它还原。" +
                        "请在界面上点名本机自己的备份文件。");
                sources[part] = local;
                ctx.Log($"{part} → {Path.GetFileName(local)}（账本原厂备份）", "note");
            }
        }

        foreach (var part in parts)
        {
            if (!sources.TryGetValue(part, out var path))
                continue;
            var md5 = FileTools.Md5File(path, ctx.Cancel);
            if (LooksLikeWrittenImage(part, path))
                throw new StepFailException(
                    $"{part} 选中的文件就是解锁镜像（前 {FileTools.FmtSize(StateStore.WrittenSize(part))} 字节的 MD5 与账本里写入记录一致），" +
                    "不是原厂备份 —— 已中止还原。请改用原厂备份文件。");
            if (md5.Length > 0 && StateStore.StockMd5s(part).Contains(md5))
            {
                ctx.Log($"{part} 来源已核对：与账本里的原厂备份一致。", "ok");
                continue;
            }
            var kind = StateStore.BackupKind(part);
            ctx.Log($"{part} 来源未核对：{Path.GetFileName(path)} 不在账本的原厂备份里" +
                    (kind == StateStore.KindUnknown ? "（账本里那份备份的来源也未核对）" : "") +
                    "，请自行确认这是原厂镜像。", "warn");
        }
        return sources;
    }

    private static readonly byte[] ElfMagic = { 0x7f, (byte)'E', (byte)'L', (byte)'F' };

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
    private static partial Regex NonAlnumRegex();

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex KverRegex();

    private static bool IsElf(string path)
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

    private static (Dictionary<string, string> Found, List<string> Missing, List<string> Listing)
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
        foreach (var key in AppConfig.BundleBins)
        {
            var aliases = (AppConfig.BundleAliases.TryGetValue(key, out var list) ? list : new[] { key })
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

        var missing = AppConfig.BundleBins.Where(k => !found.ContainsKey(k)).ToList();
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
            missing = AppConfig.BundleBins.Where(k => !found.ContainsKey(k)).ToList();
        }
        return (found, missing, listing);
    }

    private static (string Path, string Why) FindProfile(Ctx ctx, string bundle, string kver,
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

    private static string RemoteImage(string key) =>
        $"{AppConfig.RemoteImages}/{AppConfig.ImageRemoteName[key]}";

    private static void PushBins(Ctx ctx, bool quiet = false)
    {
        var bundle = (ctx.Extras.TryGetValue("bundle", out var b) ? b?.ToString() : null)
                     ?? ctx.Param("bundle", "").Trim();
        var found = ctx.Extras.TryGetValue("bins", out var binsObj) && binsObj is Dictionary<string, string> d
            ? d
            : ResolveBins(ctx, bundle).Found;
        foreach (var key in AppConfig.BundleBins)
        {
            if (!found.TryGetValue(key, out var local) || local.Length == 0)
                throw new StepFailException($"认不出 {key} 是哪个文件，请回到上一步看提示。");
            var remote = $"{AppConfig.DevTmp}/{key}";
            ctx.Run(adb.AdbArgv("push", local, remote),
                label: $"adb push {key} ← {Path.GetFileName(local)}",
                timeoutMs: 300000, stream: !quiet);
            var remoteSize = adb.RemoteSize(remote, cancel: ctx.Cancel);
            var localSize = FileTools.FileSize(local);
            if (remoteSize != localSize)
                throw new StepFailException(
                    $"推送 {key} 后大小不一致（本地 {localSize?.ToString() ?? "?"} / 设备 {remoteSize?.ToString() ?? "?"}），请重插数据线后重试。");
        }
        ctx.Shell(adb,
            $"chmod 755 {AppConfig.DevTmp}/exploit_guard {AppConfig.DevTmp}/ksud {AppConfig.DevTmp}/su0",
            timeoutMs: 60000, stream: false);
        ctx.Log("exploit_guard / ksud / su0 已就位并赋予执行权限。", "ok");
    }

    private static Step S3Kernel() => new("匹配内核版本与偏移 profile", ctx =>
    {
        DeviceOnline(ctx);
        ctx.Status("读取内核版本");
        var kernelRaw = adb.KernelRaw();
        var kver = kernelRaw.Length > 0 ? kernelRaw.Split('-')[0] : "";
        ctx.Extras["kernel"] = kernelRaw;
        ctx.Extras["kver"] = kver;
        ctx.Log($"设备内核：{(kernelRaw.Length > 0 ? kernelRaw : "未知")}", "ok");
        var model = adb.Prop("ro.product.model");
        ctx.Log($"设备型号：{(model.Length > 0 ? model : "未知")}", "ok");
        if (model.Length > 0 && !AppConfig.SupportedModels.Contains(model))
        {
            ctx.Log($"提示：{model} 不在已验证机型列表（{string.Join("/", AppConfig.SupportedModels)}）里，" +
                    "分区名与偏移可能不同，请自行确认。", "warn");
        }
        var bundle = ctx.Param("bundle", "").Trim();
        var (profile, why) = bundle.Length > 0
            ? FindProfile(ctx, bundle, kver, kernelRaw)
            : ("", "还没选 bundle 目录");
        ctx.Extras["profile_path"] = profile;
        if (AppConfig.KnownKVers.Contains(kver))
        {
            ctx.Log($"内核 {kver} 属于内置适配版本，使用编译内置偏移。", "ok");
            if (profile.Length > 0)
                ctx.Log($"同时发现 profile：{profile}（{why}），它的参数会覆盖内置默认值。", "note");
            return "内置偏移";
        }
        if (profile.Length > 0)
        {
            ctx.Log($"找到版本 profile：{profile}（{why}）", "ok");
            return "profile 已就绪";
        }
        if (ctx.Flag("allow_unsupported"))
        {
            ctx.Log($"内核 {(kver.Length > 0 ? kver : "未知")} 未适配，你选择了强制继续 —— " +
                    "偏移不对会写错内核地址并触发重启，后果自负。", "warn");
            return "强制继续";
        }
        throw new StepFailException(
            $"内核 {(kver.Length > 0 ? kver : "未知")} 未适配（内置只支持 {string.Join(" / ", AppConfig.KnownKVers)}），" +
            $"也没找到可用的偏移 profile（{why}）。请用 bundle 里的 tools/offset_tool 算出该固件的 offsets，" +
            $"存成 profiles/{(kver.Length > 0 ? kver : "<内核版本>")}.env（名字里带内核版本号即可），" +
            "或确认风险后勾选『内核未适配也强制继续』。");
    });

    private static Step S3Bundle() => new("校验 Release bundle 文件", ctx =>
    {
        var bundle = ctx.Param("bundle", "").Trim();
        if (bundle.Length == 0)
            throw new StepFailException(
                "还没有选择 bundle 目录。请从项目的 Releases 页面下载 bundle 并解压，" +
                "然后选中含 exploit_guard / ksud / su0 / profiles 的那个目录。");
        if (!Directory.Exists(bundle))
            throw new StepFailException($"bundle 目录不存在：{bundle}");
        var (found, missing, listing) = ResolveBins(ctx, bundle);
        foreach (var key in AppConfig.BundleBins)
        {
            if (found.TryGetValue(key, out var path))
                ctx.Log($"{key,-14} ← {Path.GetFileName(path)}（{FileTools.FmtSize(FileTools.FileSize(path))}）", "ok");
        }
        var profilesDir = Path.Combine(bundle, "profiles");
        if (Directory.Exists(profilesDir))
        {
            var names = Directory.GetFiles(profilesDir)
                .Where(p => p.ToLowerInvariant().EndsWith(".env"))
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            ctx.Log($"profiles/：{(names.Count > 0 ? string.Join(", ", names) : "（空）")}", "note");
        }
        if (missing.Count > 0)
        {
            var seen = string.Join("、", listing.Take(8).Select(Path.GetFileName));
            if (seen.Length == 0)
                seen = "（没有文件）";
            throw new StepFailException(
                $"认不出哪个文件是 {string.Join("、", missing)}。目录里看到：{seen}。" +
                "文件名可以任意（下载后缀、副本、版本号、空格都行），但需要能看出是哪一个是" +
                " exploit_guard / ksud / su0；实在认不出就把它们改回上游的名字。");
        }
        ctx.Extras["bundle"] = bundle;
        ctx.Extras["bins"] = found;
        ctx.Log("本机文件名随意，设备端固定用 exploit_guard / ksud / su0 这三个名字。", "note");
        return "bundle 完整";
    });

    private static Step S3Push() => new("推送二进制并赋予权限", ctx =>
    {
        PushBins(ctx);
        return "已推送 exploit_guard / ksud / su0";
    });

    private static Step S3Profile() => new("加载版本 profile 参数", ctx =>
    {
        var kver = ctx.Extras.TryGetValue("kver", out var k) ? k?.ToString() ?? "" : "";
        var kernelBuild = ctx.Extras.TryGetValue("kernel", out var kb) ? kb?.ToString() ?? "" : "";
        var bundle = (ctx.Extras.TryGetValue("bundle", out var b) ? b?.ToString() : null)
                     ?? ctx.Param("bundle", "").Trim();
        var profile = ctx.Extras.TryGetValue("profile_path", out var p) ? p?.ToString() ?? "" : "";
        if (profile.Length == 0 && bundle.Length > 0)
        {
            (profile, _) = FindProfile(ctx, bundle, kver, kernelBuild);
            ctx.Extras["profile_path"] = profile;
        }
        var pairs = new List<string>();
        if (profile.Length > 0 && File.Exists(profile))
        {
            foreach (var rawLine in File.ReadAllLines(profile))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || !line.Contains('='))
                    continue;
                pairs.Add(line);
            }
            ctx.Log($"已加载 {Path.GetFileName(profile)}：{pairs.Count} 项偏移参数。" +
                    string.Join(" ", pairs)[..Math.Min(160, string.Join(" ", pairs).Length)], "ok");
        }
        else
        {
            ctx.Log("没有可用 profile，使用内置默认偏移。", "note");
        }
        ctx.Extras["profile_args"] = string.Join(" ", pairs);
        return $"{pairs.Count} 项偏移参数";
    });

    private static bool KsuActive(CancellationToken cancel, string what, Action<string, string> log)
    {
        try
        {
            if (adb.ModuleLoaded(15000, cancel))
            {
                log($"{what}：kernelsu 模块已在内核。", "ok");
                return true;
            }
        }
        catch
        {

        }
        try
        {
            if (adb.RootOk(20000, cancel))
            {
                log($"{what}：root 通道 uid=0 可用（模块名检查不可靠，以 uid=0 为准）。", "ok");
                return true;
            }
        }
        catch
        {

        }
        return false;
    }

    private static string GuardEnvArgs(Ctx ctx)
    {
        string modules;
        try
        {
            modules = adb.ShellText("cat /proc/modules", timeoutMs: 30000);
        }
        catch (Exception exc)
        {
            ctx.Log($"读取 /proc/modules 失败（{exc.Message}），不启用 guard 补丁。", "warn");
            return "";
        }
        var lower = modules.ToLowerInvariant();
        var hit = AppConfig.GuardModules.FirstOrDefault(m => lower.Contains(m));
        if (hit is null)
        {
            ctx.Log("没有发现已知的 OPPO 安全守卫模块，跳过 guard 补丁 —— 强行传 " +
                    "CHEESE_GUARD_PATCH 会让 exploit 直接退出（找不到签名就 return 1）。", "note");
            return "";
        }
        ctx.Log($"发现安全守卫模块 {hit}，启用 guard 补丁。", "note");
        return "CHEESE_GUARD_PATCH=1 CHEESE_GUARD_COMBINED=1";
    }

    private static Step S3Exploit() => new("运行 exploit（含重试与异步 insmod 等待）", ctx =>
    {
        var attemptsRaw = ctx.Params.TryGetValue("attempts", out var a) && a is not null ? a.ToString() : "";
        var attempts = int.TryParse(attemptsRaw, out var n) ? n : AppConfig.MaxAttempts;
        attempts = Math.Clamp(attempts, 1, 8);
        var profile = ctx.Extras.TryGetValue("profile_args", out var pa) ? pa?.ToString() ?? "" : "";
        var kver = ctx.Extras.TryGetValue("kver", out var kv) ? kv?.ToString() ?? "" : "";
        var kmi = AppConfig.ExploitKmiFor(kver);
        if (!AppConfig.ExploitKmiMap.TryGetValue(kver ?? "", out _))
        {
            ctx.Log($"内核 {kver} 不在 KMI 对照表里，按主版本推用 {kmi}；若 late-load 报 " +
                    "no embedded KernelSU module for KMI，请在 bundle 的 profiles 里补一个对应内核版本的 .env。", "warn");
        }
        var variant = ctx.Param("variant", "auto").Trim().ToLowerInvariant();
        if (variant != "initcred" && variant != "capsonly")
            variant = (kver ?? "").StartsWith("5.15", StringComparison.Ordinal) ? "initcred" : "capsonly";
        var variantEnv = variant == "initcred" ? "CHEESE_SC_INITCRED=1" : "CHEESE_SC_CAPSONLY=1";
        ctx.Log($"提权变体：{variant}（{variantEnv}）", "note");
        var envPrefix = string.Join(" ", new[]
        {
            profile, GuardEnvArgs(ctx), variantEnv, "CHEESE_KPTR0=1", "CHEESE_BIND_KPTR=1",
        }.Where(part => part.Length > 0));
        var stamp = Now();
        var logs = AppConfig.LogDir();
        var ok = false;
        for (var attempt = 1; attempt <= attempts; attempt++)
            {
                ctx.CheckCancel();
                ctx.Status($"临时 root：第 {attempt}/{attempts} 次尝试");
                if (!adb.WaitForBoot(300000, ctx.Cancel))
                {
                    ctx.Log($"第 {attempt} 次：等系统启动超时，跳过这次。", "warn");
                    continue;
                }
                if (KsuActive(ctx.Cancel, $"第 {attempt} 次（尝试开始前）", ctx.Log))
                {
                    ok = true;
                    break;
                }
                PushBins(ctx, quiet: true);
                var cmd = $"cd {AppConfig.DevTmp} && {envPrefix} ./exploit_guard {AppConfig.DevTmp}/ksud " +
                          $"late-load --kmi {kmi} --allow-shell --package-name {AppConfig.KsuPackage}";
            var logpath = Path.Combine(logs, $"exploit_{stamp}_a{attempt}.log");
            ctx.Log($"第 {attempt}/{attempts} 次：组合运行（guard 补丁 + late-load，KMI {kmi}）…", "info");
            var res = ctx.Run(adb.AdbArgv("shell", cmd), label: "exploit_guard",
                timeoutMs: 420000, allowFail: true, stream: true, logfile: logpath, closeStdin: true);
            ctx.Log($"exploit 退出码 {res.Code}，原始日志：{logpath}", "note");

            if (adb.Devices().Count == 0)
            {
                ctx.Log($"第 {attempt} 次：设备掉线（写入抖动触发重启），等开机后重试。", "warn");
                continue;
            }
            var hit = AppConfig.ExploitFailMarkers.FirstOrDefault(marker => res.Out.Contains(marker));
            if (hit != null)
            {

                if (KsuActive(ctx.Cancel, $"第 {attempt} 次（命中失败特征后复核）", ctx.Log))
                {
                    ok = true;
                    break;
                }
                ctx.Log($"第 {attempt} 次：命中失败特征『{hit}』，重试。", "warn");
                continue;
            }

            ctx.Log($"第 {attempt} 次：等待异步 insmod（最多 {AppConfig.LoadWaitS} 秒，不抢跑）…", "info");
            var waited = 0;
            while (waited < AppConfig.LoadWaitS)
            {
                if (KsuActive(ctx.Cancel, $"第 {attempt} 次（t+{waited}s）", ctx.Log))
                {
                    ok = true;
                    break;
                }
                Sleep(ctx, 10);
                waited += 10;
                ctx.Status($"等待 insmod：{waited}s / {AppConfig.LoadWaitS}s");
                if (adb.Devices().Count == 0)
                {
                    ctx.Log($"第 {attempt} 次：等待期间设备掉线，等开机后重试。", "warn");
                    break;
                }
            }
            if (ok)
                break;
            ctx.Log($"第 {attempt} 次：{AppConfig.LoadWaitS} 秒内模块没出现，重试。", "warn");
        }
        if (!ok)
        {

            if (KsuActive(ctx.Cancel, "收尾复核", ctx.Log))
            {
                ok = true;
            }
        }
        if (!ok)
        {
            throw new StepFailException(
                $"{attempts} 次尝试都没能把 kernelsu 载入内核。请查看 {logs} 里的 exploit 原始日志；" +
                "常见原因：bundle 与固件版本不匹配、屏幕熄灭导致进程被杀、USB 线不稳。");
        }
        return "kernelsu 已加载";
    });

    private static Step S3Verify() => new("验证 root 通道（主判据 uid=0）", ctx =>
    {
        ctx.Status("验证 uid=0");
        var res = ctx.Shell(adb, "id", root: true, timeoutMs: 90000, allowFail: true);
        var first = res.Out.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        ctx.Log($"ksud debug su → {(first.Length > 0 ? first : "(无输出)")}", "info");
        if (!first.StartsWith("uid=0", StringComparison.Ordinal))
        {
            throw new StepFailException("root 通道没拿到 uid=0（主判据失败）。可以重跑本步骤；" +
                                        "若一直失败，通常是 bundle 与固件版本不匹配。");
        }
        ctx.Log("ROOT OK —— 临时 root 已生效。注意：重启手机后失效，需要重跑本步骤。", "ok");
        var text = ctx.Shell(adb, "dmesg | grep -a commoncap | tail -3", root: true,
            timeoutMs: 90000, allowFail: true).Out;
        foreach (var line in text.Split('\n').Take(4))
        {
            if (line.Trim().Length > 0)
                ctx.Log($"dmesg: {line.Trim()}", "note");
        }
        ctx.Extras["root"] = true;
        return "uid=0";
    });

    private static Step S3Record() => new("写入账本", ctx =>
    {
        StateStore.SetDevice(new Dictionary<string, object?>
        {
            ["model"] = adb.Prop("ro.product.model"),
            ["kernel"] = (ctx.Extras.TryGetValue("kernel", out var k) ? k?.ToString() : null) ?? adb.KernelRaw(),
            ["kver"] = ctx.Extras.TryGetValue("kver", out var kv) ? kv?.ToString() ?? "" : "",
            ["slot"] = adb.ActiveSlot(),
            ["serial"] = DeviceSerial(),
            ["root"] = true,
        });
        StateStore.MarkStage("temproot", true, ctx.Extras.TryGetValue("kver", out var kver) ? kver?.ToString() ?? "" : "");
        return "已记录";
    });

    private static string[] PreviewTemproot(Dictionary<string, object?>? parameters)
    {
        var bundle = parameters is not null && parameters.TryGetValue("bundle", out var b) && b is not null
            ? b.ToString()! : "<bundle 目录>";
        var variant = parameters is not null && parameters.TryGetValue("variant", out var v) && v is not null
            ? v.ToString()! : "auto";
        var variantEnv = variant switch
        {
            "initcred" => "CHEESE_SC_INITCRED=1",
            "capsonly" => "CHEESE_SC_CAPSONLY=1",
            _ => "<按内核自动：5.15 -> CHEESE_SC_INITCRED=1，5.10 -> CHEESE_SC_CAPSONLY=1>",
        };
        const string adbPath = "adb";
        return new[]
        {
            "# 1) 推送二进制（来自 bundle）",
            $"{adbPath} push \"{bundle}\\exploit_guard\" /data/local/tmp/",
            $"{adbPath} push \"{bundle}\\ksud\" /data/local/tmp/",
            $"{adbPath} push \"{bundle}\\su0\" /data/local/tmp/",
            $"{adbPath} shell chmod 755 /data/local/tmp/exploit_guard /data/local/tmp/ksud /data/local/tmp/su0",
            "",
            "# 2) 组合运行（profile 参数按 profiles/<内核 build>.env 自动注入；KMI 按内核版本自动选择）",
            $"{adbPath} shell \"cd /data/local/tmp && <PROFILE_ARGS> <GUARD_ARGS> {variantEnv} " +
            $"CHEESE_KPTR0=1 CHEESE_BIND_KPTR=1 ./exploit_guard /data/local/tmp/ksud " +
            $"late-load --kmi <KMI> --allow-shell --package-name {AppConfig.KsuPackage}\"",
            "",
            "# 3) 等待异步 insmod，然后验证主判据",
            $"{adbPath} shell \"grep -c '^kernelsu' /proc/modules\"",
            $"{adbPath} shell \"echo 'id' | /data/local/tmp/ksud debug su\"",
        };
    }

    private static Step S4Check() => new("校验本地解锁文件", ctx =>
    {
        var picked = new Dictionary<string, (string Path, long Size, string Md5)>();
        foreach (var (key, label) in new[] { ("abl", "abl 镜像"), ("ocdt", "ocdt 镜像") })
        {
            var path = ctx.Param(key, "").Trim();
            var size = RequireFile(path, label);
            var digest = FileTools.Md5File(path, ctx.Cancel);
            ctx.Log($"{label} ← {Path.GetFileName(path)}（{FileTools.FmtSize(size)}，MD5 {FileTools.FmtMd5(digest, 12)}）", "ok");
            picked[key] = (path, size, digest);
            ctx.Extras[key] = path;
        }
        if (picked["abl"].Path == picked["ocdt"].Path ||
            (picked["abl"].Md5.Length > 0 && picked["abl"].Md5 == picked["ocdt"].Md5))
        {
            throw new StepFailException("abl 与 ocdt 选到了同一个文件，这是两个不同的镜像，请分开选择。");
        }
        if (picked["abl"].Size < picked["ocdt"].Size)
        {
            ctx.Log($"提示：abl（{FileTools.FmtSize(picked["abl"].Size)}）比 ocdt（{FileTools.FmtSize(picked["ocdt"].Size)}）小，" +
                    "通常 abl 更大一些，确认没有选反。", "warn");
        }
        ctx.Log("本机文件名随意（123abl.img 也行）；设备端会固定用 abl.img / ocdt.img 落盘。", "note");
        return "两个镜像就绪";
    });

    private static Step S4Push() => new("推送到设备临时目录", ctx =>
    {
        DeviceOnline(ctx);
        EnsureDirs(ctx);
        foreach (var key in new[] { "abl", "ocdt" })
        {
            var local = (string)ctx.Extras[key]!;
            var remote = RemoteImage(key);
            ctx.Run(adb.AdbArgv("push", local, remote),
                label: $"adb push {Path.GetFileName(local)}", timeoutMs: 600000, stream: false);
            var remoteSize = adb.RemoteSize(remote, cancel: ctx.Cancel);
            var localSize = FileTools.FileSize(local);
            if (remoteSize != localSize)
                throw new StepFailException(
                    $"推送 {Path.GetFileName(local)} 后大小不一致（本地 {localSize?.ToString() ?? "?"} / 设备 {remoteSize?.ToString() ?? "?"}）。");
            ctx.Log($"{Path.GetFileName(local)} → {remote}（{FileTools.FmtSize(localSize)}）", "ok");
        }
        StateStore.MarkStage("push", true);
        return $"已推送到 {AppConfig.RemoteImages}";
    });

    private static string[] PreviewPush(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        var abl = parameters is not null && parameters.TryGetValue("abl", out var a) && a is not null
            ? a.ToString()! : "<abl 镜像>";
        var ocdt = parameters is not null && parameters.TryGetValue("ocdt", out var o) && o is not null
            ? o.ToString()! : "<ocdt 镜像>";
        return new[]
        {
            "# 本地文件名随意（看内容不看名字），设备端固定落成 abl.img / ocdt.img",
            $"{adbPath} push \"{abl}\" {AppConfig.RemoteImages}/abl.img",
            $"{adbPath} push \"{ocdt}\" {AppConfig.RemoteImages}/ocdt.img",
            $"{adbPath} shell \"wc -c < {AppConfig.RemoteImages}/abl.img\"",
        };
    }

    private static Step S5Root() => new("确认 root 通道", ctx =>
    {
        RequireRoot(ctx);
        EnsureDirs(ctx);
        return "uid=0";
    });

    private static List<string> BackupTargets(Ctx ctx)
    {
        var all = AppConfig.Partitions.ToList();
        if (!ctx.Params.TryGetValue("scope", out var chosen) || chosen is null)
            return all;
        if (chosen is string str)
        {
            if (str.Length == 0)
                return all;
            if (str == "active")
                return new List<string> { "ocdt", "abl_" + (ctx.Extras.TryGetValue("slot", out var s) ? s?.ToString() ?? "b" : "b") };
            return all;
        }
        if (chosen is IEnumerable<object?> values)
        {
            var set = values.Select(v => v?.ToString() ?? "").ToHashSet();
            return all.Where(part => set.Contains(part)).ToList();
        }
        return all;
    }

    private static Step S5Dump() => new("dd 备份分区到设备临时目录", ctx =>
    {
        Slot(ctx);
        var parts = BackupTargets(ctx);
        if (parts.Count == 0)
            throw new StepFailException("『备份分区』里至少要勾选一个分区。");
        var stamp = Now();
        ctx.Extras["stamp"] = stamp;
        var dumped = new Dictionary<string, DumpMeta>();
        foreach (var part in parts)
        {
            ctx.CheckCancel();
            ctx.Status($"备份 {part}");
            var dev = AppConfig.BlockByName + part;
            var psize = adb.PartitionSize(part, cancel: ctx.Cancel);
            var remote = $"{AppConfig.RemoteBackup}/{part}.img";
            ctx.Shell(adb, $"dd if={dev} of={remote} bs={AppConfig.DdBs} && sync",
                root: true, timeoutMs: 900000, stream: false);
            var dsize = adb.RemoteSize(remote, root: true, cancel: ctx.Cancel);
            if (dsize is not { } dumpSize || dumpSize <= 0)
                throw new StepFailException($"备份 {part} 后读不到设备端文件大小，请重试本步骤。");
            if (psize is { } partitionSize && partitionSize > 0 && dumpSize != partitionSize)
                throw new StepFailException(
                    $"备份 {part} 的大小（{FileTools.FmtSize(dumpSize)}）与分区（{FileTools.FmtSize(partitionSize)}）不一致，请重试本步骤。");
            ctx.Log($"{part} 已 dump：{FileTools.FmtSize(dumpSize)}" +
                    (psize is { } ps && ps > 0 ? $"（分区 {FileTools.FmtSize(ps)}）" : ""), "ok");
            dumped[part] = new DumpMeta(remote, dumpSize, psize);
        }
        ctx.Extras["dumped"] = dumped;
        return $"已备份 {dumped.Count} 个分区";
    });

    private static Step S5Pull() => new("拉取到本机并逐字节校验", ctx =>
    {
        var dumped = ctx.Extras.TryGetValue("dumped", out var d) && d is Dictionary<string, DumpMeta> dict
            ? dict
            : new Dictionary<string, DumpMeta>();
        var stamp = ctx.Extras.TryGetValue("stamp", out var s) ? s?.ToString() ?? "" : "";
        if (stamp.Length == 0)
            stamp = Now();
        var slot = ctx.Extras.TryGetValue("slot", out var sl) ? sl?.ToString() ?? "" : "";
        var serial = DeviceSerial();
        if (dumped.Count == 0)
            throw new StepFailException("没有可拉取的备份，请先执行上一步。");
        foreach (var (part, meta) in dumped)
        {
            ctx.CheckCancel();
            ctx.Status($"校验 {part}");
            var local = Path.Combine(AppConfig.BackupDir(), $"{part}_{stamp}.img");
            ctx.Run(adb.AdbArgv("pull", meta.Remote, local),
                label: $"adb pull {part}", timeoutMs: 900000, stream: false);
            var lsize = FileTools.FileSize(local);
            if (lsize is not { } localSize || localSize <= 0)
                throw new StepFailException($"拉取 {part} 失败：本机文件是空的（{local}）");
            var remoteMd5 = adb.RemoteMd5(meta.Remote, root: true, cancel: ctx.Cancel);
            var localMd5 = FileTools.Md5File(local, ctx.Cancel);
            if (remoteMd5 is { } rmd5 && localMd5.Length > 0 && rmd5 != localMd5)
                throw new StepFailException(
                    $"备份校验失败：{part} 的设备端 MD5（{FileTools.FmtMd5(rmd5, 12)}）与本机文件（{FileTools.FmtMd5(localMd5, 12)}）不一致，请重做本步骤。");
            if (meta.Psize is { } psize && localSize != psize)
                throw new StepFailException(
                    $"备份 {part} 的本机大小（{FileTools.FmtSize(localSize)}）与分区（{FileTools.FmtSize(psize)}）不一致，请重做本步骤。");

            if (LooksLikeWrittenImage(part, local))
            {
                try { File.Delete(local); } catch { }
                throw new StepFailException(
                    $"{part} 现在的内容就是你写入的解锁镜像（前 {FileTools.FmtSize(StateStore.WrittenSize(part))} 字节的 MD5 与账本写入记录一致），" +
                    "这不是原厂内容，不能当还原用的备份（已删除刚拉下来的这份）。" +
                    "请先把该分区还原成原厂镜像、或线刷原厂固件之后再来备份。");
            }
            var known = StateStore.StockMd5s(part);
            var kind = known.Contains(localMd5) || known.Count == 0
                ? StateStore.KindStock
                : StateStore.KindUnknown;
            if (kind == StateStore.KindUnknown)
                ctx.Log($"{part} 这份备份与账本里已有的原厂备份都不一致（MD5 {FileTools.FmtMd5(localMd5, 12)}）：" +
                        "如果你刚刷过官方固件属正常，否则请确认分区有没有被别的工具改过。" +
                        "之后还原时这份会被跳过（标记为『来源未核对』，不会被账本当成原厂备份）。", "warn");
            var lastSerial = StateStore.RecordString(StateStore.BackupRecord(part), "serial");
            if (lastSerial.Length > 0 && serial.Length > 0 && lastSerial != serial)
                ctx.Log($"注意：{part} 上一次的备份来自另一台设备（序列号 {lastSerial}），这次是 {serial} —— 两台机器的备份不要混用。", "warn");
            StateStore.RecordBackup(part, local, localSize, localMd5, slot, serial, kind);
            ctx.Log($"{part} 备份完成 → {local}（{FileTools.FmtSize(localSize)}，MD5 {FileTools.FmtMd5(localMd5, 12)}，" +
                    $"{(kind == StateStore.KindStock ? "原厂备份" : "来源未核对")}）", "ok");
        }
        StateStore.MarkStage("backup", true);
        return "备份已落到 backup 目录并校验通过";
    });

    private static Step S5Manifest() => new("写出备份清单", ctx =>
    {
        var data = StateStore.Load();
        var partitions = new System.Text.Json.Nodes.JsonObject();
        foreach (var part in AppConfig.Partitions)
        {
            var backup = data["partitions"]?[part]?["backup"];
            partitions[part] = backup is null ? null : backup.DeepClone();
        }
        var manifest = new System.Text.Json.Nodes.JsonObject
        {
            ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["device"] = data["device"]!.DeepClone(),
            ["slot"] = ctx.Extras.TryGetValue("slot", out var s) ? s?.ToString() ?? "" : "",
            ["partitions"] = partitions,
        };
        var path = Path.Combine(AppConfig.BackupDir(),
            $"manifest_{(ctx.Extras.TryGetValue("stamp", out var st) ? st?.ToString() ?? Now() : Now())}.json");
        try
        {
            File.WriteAllText(path, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), Encoding.UTF8);
            ctx.Log($"清单：{path}", "note");
        }
        catch (Exception exc)
        {
            ctx.Log($"清单写入失败（不影响备份）：{exc.Message}", "warn");
        }
        return "清单已写出";
    });

    private static string[] PreviewBackup(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        return new[]
        {
            "# 需要临时 root（临时 Root 阶段）；<SLOT> 为自动检测的活动槽",
            $"{adbPath} shell \"echo 'dd if={AppConfig.BlockByName}abl_a of={AppConfig.RemoteBackup}/abl_a.img bs={AppConfig.DdBs} && sync' | {AppConfig.Ksud} debug su\"",
            $"{adbPath} shell \"echo 'dd if={AppConfig.BlockByName}abl_b of={AppConfig.RemoteBackup}/abl_b.img bs={AppConfig.DdBs} && sync' | {AppConfig.Ksud} debug su\"",
            $"{adbPath} shell \"echo 'dd if={AppConfig.BlockByName}ocdt of={AppConfig.RemoteBackup}/ocdt.img bs={AppConfig.DdBs} && sync' | {AppConfig.Ksud} debug su\"",
            $"{adbPath} pull {AppConfig.RemoteBackup}/abl_<SLOT>.img ./backup/",
            $"{adbPath} shell \"echo 'md5sum {AppConfig.RemoteBackup}/ocdt.img' | {AppConfig.Ksud} debug su\"",
        };
    }

    private static Step S6Gate() => new("安全闸门：备份与授权确认", ctx =>
    {
        RequireRoot(ctx);
        EnsureDirs(ctx);
        var slot = Slot(ctx);
        if (ctx.Param("confirm", "").Trim() != "解锁")
            throw new StepFailException("请在确认框里完整输入『解锁』两个字（照抄，不要带空格），再执行本步。");
        var abl = ctx.Param("abl", "").Trim();
        var ocdt = ctx.Param("ocdt", "").Trim();
        RequireFile(abl, "abl 镜像");
        RequireFile(ocdt, "ocdt 镜像");
        ctx.Extras["abl"] = abl;
        ctx.Extras["ocdt"] = ocdt;
        var missing = new[] { "ocdt", "abl_" + slot }.Where(p => StateStore.NewestStockBackup(p) is null).ToList();
        if (missing.Count > 0 && !ctx.Flag("force"))
        {
            throw new StepFailException(
                $"账本显示这些分区还没有可用的『原厂备份』：{string.Join("、", missing)}。请先完成『备份原分区』。" +
                "（账本里只记着来源未核对的备份时也会被拦下）确实在别处备份过，可勾选『跳过备份检查』。");
        }
        ctx.Log($"目标分区：ocdt + abl_{slot}", "info");
        foreach (var part in new[] { "ocdt", "abl_" + slot })
        {
            var record = StateStore.NewestStockBackup(part);
            if (record is null)
                continue;
            ctx.Log($"{part} 的还原来源：{Path.GetFileName(StateStore.RecordString(record, "local"))}" +
                    $"（MD5 {FileTools.FmtMd5(StateStore.RecordString(record, "md5"), 12)}）", "note");
        }
        if (ctx.Flag("force") && missing.Count > 0)
            ctx.Log($"已按你的选择跳过备份检查（缺少备份：{string.Join("、", missing)}）。", "warn");
        return "闸门通过";
    });

    private static Step S6Push() => new("推送解锁文件", ctx =>
    {
        foreach (var key in new[] { "abl", "ocdt" })
        {
            var local = (string)ctx.Extras[key]!;
            var remote = RemoteImage(key);
            ctx.Run(adb.AdbArgv("push", local, remote),
                label: $"adb push {Path.GetFileName(local)}", timeoutMs: 600000, stream: false);
            var remoteSize = adb.RemoteSize(remote, cancel: ctx.Cancel);
            if (remoteSize != FileTools.FileSize(local))
                throw new StepFailException($"推送 {Path.GetFileName(local)} 后大小不一致，请重试。");
        }
        return "已推送";
    });

    private static string DdWrite(Ctx ctx, string part, string imageLocal, string key)
    {
        var remote = RemoteImage(key);
        var dev = AppConfig.BlockByName + part;
        ctx.Status($"写入 {part}");
        var size = FileTools.FileSize(imageLocal) ?? 0;
        var psize = adb.PartitionSize(part, cancel: ctx.Cancel);

        if (psize is { } partitionSize && size > partitionSize)
        {
            throw new StepFailException(
                $"镜像 {Path.GetFileName(imageLocal)} 有 {FileTools.FmtSize(size)}，" +
                $"比分区 {part}（{FileTools.FmtSize(partitionSize)}）还大 —— 这是选错文件了，已中止写入。");
        }
        if (psize is { } ps2 && size < ps2 * 0.25)
        {
            ctx.Log($"提示：镜像 {Path.GetFileName(imageLocal)} 只有分区 {part} 的 {100.0 * size / ps2:0.0}%，" +
                    "确认一下选的是对的镜像。", "warn");
        }
        ctx.Shell(adb, $"dd if={remote} of={dev} bs={AppConfig.DdBs} && sync",
            root: true, timeoutMs: 900000, stream: false);
        var srcMd5 = FileTools.Md5File(imageLocal, ctx.Cancel);
        var backMd5 = adb.PartitionHeadMd5(part, size, cancel: ctx.Cancel);
        if (srcMd5.Length > 0 && backMd5 is { } bmd5 && srcMd5 != bmd5)
        {
            throw new StepFailException(
                $"写入 {part} 后读回校验不一致（分区前 {FileTools.FmtSize(size)} 字节 MD5 {FileTools.FmtMd5(bmd5, 12)} / 镜像 {FileTools.FmtMd5(srcMd5, 12)}）。" +
                "先不要重启，直接执行『还原原分区』。");
        }
        StateStore.RecordWrite(part, imageLocal, size, srcMd5);
        ctx.Log($"{part} 写入完成并读回校验通过（前 {FileTools.FmtSize(size)}，MD5 {FileTools.FmtMd5(srcMd5, 12)}）", "ok");
        return part;
    }

    private static Step S6Ocdt() => new("写入 ocdt", ctx =>
    {
        DdWrite(ctx, "ocdt", (string)ctx.Extras["ocdt"]!, "ocdt");
        return "ocdt 已写入";
    });

    private static Step S6Abl() => new("写入活动槽 abl", ctx =>
    {
        var slot = ctx.Extras.TryGetValue("slot", out var s) ? s?.ToString() ?? "b" : "b";
        DdWrite(ctx, "abl_" + slot, (string)ctx.Extras["abl"]!, "abl");
        return $"abl_{slot} 已写入";
    });

    private static Step S6Record() => new("记录状态", ctx =>
    {
        StateStore.MarkStage("write", true, $"slot={(ctx.Extras.TryGetValue("slot", out var s) ? s?.ToString() ?? "" : "")}");
        ctx.Log("写入阶段完成。下一步重启进 fastboot 执行解锁 —— 注意锁一旦解开就会清空数据。", "warn");
        return "已记录";
    });

    private static string[] PreviewWrite(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        var abl = parameters is not null && parameters.TryGetValue("abl", out var a) && a is not null
            ? a.ToString()! : "<abl 镜像>";
        var ocdt = parameters is not null && parameters.TryGetValue("ocdt", out var o) && o is not null
            ? o.ToString()! : "<ocdt 镜像>";
        return new[]
        {
            $"{adbPath} push \"{abl}\" {AppConfig.RemoteImages}/",
            $"{adbPath} push \"{ocdt}\" {AppConfig.RemoteImages}/",
            "",
            "# ocdt 单分区",
            $"{adbPath} shell \"echo 'dd if={AppConfig.RemoteImages}/ocdt.img of={AppConfig.BlockByName}ocdt bs={AppConfig.DdBs} && sync' | {AppConfig.Ksud} debug su\"",
            "# 活动槽 abl（<SLOT> 自动检测，教程示例为 b）",
            $"{adbPath} shell \"echo 'dd if={AppConfig.RemoteImages}/abl.img of={AppConfig.BlockByName}abl_<SLOT> bs={AppConfig.DdBs} && sync' | {AppConfig.Ksud} debug su\"",
            "",
            "# 读回校验（前 <镜像大小> 字节）",
            $"{adbPath} shell \"echo 'head -c <SIZE> {AppConfig.BlockByName}ocdt | md5sum' | {AppConfig.Ksud} debug su\"",
        };
    }

    private static Step S7Reboot() => new("重启进工厂 fastboot", ctx =>
    {
        if (adb.DeviceState() == "fastboot")
        {

            var devices = adb.FastbootDevices();
            ctx.Log("设备已经在 fastboot 模式，跳过重启直接解锁。", "ok");
            ctx.Log($"fastboot 已就绪：{string.Join(", ", devices)}", "ok");
            return "已在 fastboot";
        }
        DeviceOnline(ctx, needBoot: false);
        ctx.Log("即将重启到工厂 fastboot。解锁会清除全部数据，请确认已经备份好需要的东西。", "warn");

        ctx.Run(adb.AdbArgv("reboot", "bootloader"), label: "adb reboot bootloader",
            timeoutMs: 90000, allowFail: true, stream: false);
        adb.SetSerial("");
        ctx.Status("等待设备进入 fastboot");
        if (!adb.WaitForFastboot(240000, ctx.Cancel))
        {
            throw new StepFailException("等不到 fastboot 设备。请确认手机屏幕已显示工厂 fastboot 界面" +
                                        "（屏幕乱码是正常的），必要时换 USB 口或数据线后重试。");
        }
        var serials = adb.FastbootDevices();
        ctx.Log($"fastboot 已就绪：{string.Join(", ", serials)}", "ok");
        return "已进入 fastboot";
    });

    private static Step S7Ack() => new("解锁操作提示", ctx =>
    {

        ctx.Log("手机已进入工厂 fastboot 菜单：在屏幕上选中 UNLOCK THE BOOTLOADER，再按确认键。" +
                "不同机型按键不一样（音量键选择 / 电源键确认是最常见的组合），以屏幕上的提示为准。" +
                "确认后手机会清数据并自动重启。", "warn");
        ctx.Log("提醒：写入解锁文件之后，手机的触控和 WiFi 会失灵，这是正常现象，做『还原原分区』后就恢复正常。" +
                "这段时间要操作手机，建议用紫罗兰工具箱（VioletBox）投屏操作。", "warn");
        return "提示已显示";
    });

    private static Step S7Unlock() => new("执行 fastboot flashing unlock", ctx =>
    {
        ctx.Status("等待手机确认解锁");
        var res = ctx.Run(adb.FbArgv("flashing", "unlock"), label: "fastboot flashing unlock",
            timeoutMs: 600000, allowFail: true);
        var text = res.Out.ToLowerInvariant();
        ctx.Extras["unlock_out"] = res.Out;
        if (text.Contains("not allowed") || text.Contains("failed") || text.Contains("not supported") ||
            text.Contains("unsupported") || text.Contains("unknown command") || text.Contains("is locked"))
        {
            throw new StepFailException(
                "解锁指令没有生效（fastboot 回显：" + res.Tail(2) + "）。\n" +
                "最常见的原因：开发者选项里的『OEM 解锁』没有打开 —— 请让手机正常开机，进入 设置 → 关于手机 → " +
                "连点『版本号』7 次打开开发者选项 → 打开『OEM 解锁』和『USB 调试』，然后重跑本阶段。\n" +
                "如果 OEM 解锁是灰的/打不开，通常是网络没连上（需要联网校验），或者该版本不允许解锁；" +
                "也可以先在 fastboot 界面里手动选择 unlock，或改用 fastboot oem unlock 再试一次。");
        }
        if (!res.Ok && !text.Contains("okay") && !text.Contains("finished"))
        {
            ctx.Log($"fastboot 返回不明确（退出码 {res.Code}），用设备状态来判断结果。", "warn");
        }
        return "解锁指令已发送";
    });

    private static Step S7Wait() => new("等待清数据并重启完成", ctx =>
    {
        AdbDefault.Instance.FastbootSerial = "";
        AdbDefault.Instance.SetSerial("");
        ctx.Status("等待手机重启");
        if (!adb.WaitForBoot(600000, ctx.Cancel))
        {
            ctx.Log("等不到系统启动完成。手机可能还在清数据/首次开机向导里，" +
                    "等它进入桌面后直接做『验证解锁状态』即可。", "warn");
            return "未确认开机（可不影响结果）";
        }
        ctx.Log("系统已启动（首次开机向导可以跳过）。", "ok");
        return "已重启";
    });

    private static string[] PreviewUnlock(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        const string fbPath = "fastboot";
        return new[]
        {
            $"{adbPath} reboot bootloader",
            "# 手机屏幕进入工厂 fastboot（乱码正常），然后：",
            $"{fbPath} devices",
            $"{fbPath} flashing unlock      # 手机上选中 UNLOCK THE BOOTLOADER，再按确认键（按键因机型而异）",
            "# 清数据重启后验证：",
            $"{adbPath} shell getprop ro.boot.flash.locked   # 0=已解锁 1=锁定",
        };
    }

    private static Step S8Check() => new("读取引导锁状态", ctx =>
    {
        ctx.Status("读取引导锁状态");
        var mode = adb.DeviceState();
        ctx.Extras["mode"] = mode;
        int? locked = null;
        if (mode == "fastboot")
        {
            var res = ctx.Run(adb.FbArgv("getvar", "unlocked"), label: "fastboot getvar unlocked",
                timeoutMs: 60000, allowFail: true, stream: false);
            var text = (res.Out ?? "").ToLowerInvariant();
            if (text.Contains("unlocked: yes") || text.Contains("unlocked:yes"))
                locked = 0;
            else if (text.Contains("unlocked: no") || text.Contains("unlocked:no"))
                locked = 1;
            if (locked is null)
            {
                throw new StepFailException("设备在 fastboot 模式，但读不到 unlocked 状态。" +
                                            "请让手机正常开机后再做本步验证。");
            }
        }
        else
        {
            DeviceOnline(ctx, needBoot: false);
            locked = adb.FlashLocked();
            var verified = adb.Prop("ro.boot.verifiedbootstate");
            ctx.Log($"ro.boot.verifiedbootstate = {(verified.Length > 0 ? verified : "?")}", "info");
        }
        if (locked is null)
            throw new StepFailException("读不到 ro.boot.flash.locked。请确认系统已经正常开机。");
        if (locked == 0)
        {
            ctx.Log("ro.boot.flash.locked = 0 → 引导锁已解锁。", "ok");
        }
        else
        {
            var slotRaw = adb.ActiveSlot();
            var slotLabel = slotRaw.Length > 0 ? slotRaw : "?";
            throw new StepFailException("ro.boot.flash.locked = 1 → 仍然是锁定状态。可重做第 6、7 步；" +
                                        $"确认写入的分区确实是当前活动槽（abl_{slotLabel}）。" +
                                        "如果第 7 步的 fastboot 指令没有生效，先去开发者选项里打开『OEM 解锁』再重跑。");
        }
        StateStore.SetDevice(new Dictionary<string, object?> { ["locked"] = locked });
        return "已解锁";
    });

    private static Step S8Record() => new("记录解锁结果", ctx =>
    {
        StateStore.MarkStage("unlock", true, "flash.locked=0");
        ctx.Log("解锁完成。以后每次开机都会出现解锁警告提示，属于正常现象。", "note");
        ctx.Log("建议现在做『还原原分区』：重新获取临时 root 并把 abl / ocdt 还原成原厂镜像。", "warn");
        return "已记录";
    });

    private static string[] PreviewVerify(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        const string fbPath = "fastboot";
        return new[]
        {
            $"{adbPath} shell getprop ro.boot.flash.locked",
            $"{fbPath} getvar unlocked",
            $"{adbPath} shell getprop ro.boot.verifiedbootstate",
        };
    }

    private static Step S9Gate() => new("确认 root 通道与备份来源", ctx =>
    {
        RequireRoot(ctx, "解锁清数据之后临时 root 会失效，请先重跑『临时 Root』。");
        EnsureDirs(ctx);
        var slot = Slot(ctx);
        var sources = ScanRestoreSources(ctx, slot);
        var need = new[] { "ocdt", "abl_" + slot };
        var missing = need.Where(p => !sources.ContainsKey(p) || !File.Exists(sources[p])).ToList();
        if (missing.Count > 0)
        {
            throw new StepFailException(
                $"找不到这些分区的备份文件：{string.Join("、", missing)}。可以指定『备份目录』让我按体积自动认" +
                "（备份体积与分区一致），或者用『abl / ocdt 备份文件』直接点名。");
        }
        if (ctx.Param("confirm", "").Trim() != "还原")
            throw new StepFailException("请在确认框里完整输入『还原』两个字，再执行本步。");
        ctx.Extras["sources"] = sources;
        return "备份来源已确认";
    });

    private static Step S9Push() => new("推送备份镜像到设备", ctx =>
    {
        var sources = ctx.Extras.TryGetValue("sources", out var s) && s is Dictionary<string, string> dict
            ? dict
            : new Dictionary<string, string>();
        foreach (var part in AppConfig.Partitions)
        {
            if (!sources.TryGetValue(part, out var local) || !File.Exists(local))
                continue;
            var remote = $"{AppConfig.RemoteBackup}/{part}.img";
            ctx.Run(adb.AdbArgv("push", local, remote), label: $"adb push {part}",
                timeoutMs: 900000, stream: false);
            if (adb.RemoteSize(remote, root: true, cancel: ctx.Cancel) != FileTools.FileSize(local))
                throw new StepFailException($"推送 {part} 备份后大小不一致，请重试。");
            ctx.Log($"{part} 备份已推送到设备。", "ok");
        }
        return "备份已就位";
    });

    private static Step S9Restore() => new("写回原厂分区并校验", ctx =>
    {
        var sources = ctx.Extras.TryGetValue("sources", out var s) && s is Dictionary<string, string> dict
            ? dict
            : new Dictionary<string, string>();
        var slot = ctx.Extras.TryGetValue("slot", out var sl) ? sl?.ToString() ?? "b" : "b";
        var targets = new[] { "ocdt", "abl_" + slot };
        foreach (var part in targets)
        {
            if (!sources.TryGetValue(part, out var local))
                continue;
            var remote = $"{AppConfig.RemoteBackup}/{part}.img";
            var dev = AppConfig.BlockByName + part;
            ctx.Status($"还原 {part}");
            ctx.Shell(adb, $"dd if={remote} of={dev} bs={AppConfig.DdBs} && sync",
                root: true, timeoutMs: 900000, stream: false);
            var size = FileTools.FileSize(local) ?? 0;
            var want = FileTools.Md5File(local, ctx.Cancel);
            var got = adb.PartitionHeadMd5(part, size, cancel: ctx.Cancel);
            if (want.Length > 0 && got is { } gmd5 && want != gmd5)
            {
                throw new StepFailException(
                    $"还原 {part} 后读回校验不一致（分区 {FileTools.FmtMd5(gmd5, 12)} / 备份 {FileTools.FmtMd5(want, 12)}）。" +
                    "可重跑本步骤；反复失败请用官方工具线刷。");
            }
            StateStore.RecordRestore(part, local);
            ctx.Log($"{part} 已还原为原厂镜像（前 {FileTools.FmtSize(size)} 校验通过）。", "ok");
        }
        StateStore.MarkStage("restore", true);
        return $"已还原 {string.Join("、", targets)}";
    });

    private static Step S9Reboot() => new("重启手机", ctx =>
    {
        if (!ctx.Flag("reboot", true))
        {
            ctx.Log("按你的选择不自动重启。", "note");
            return "跳过重启";
        }
        ctx.Run(adb.AdbArgv("reboot"), label: "adb reboot", timeoutMs: 90000, allowFail: true, stream: false);
        ctx.Log("已发送重启指令，手机将回到正常系统。", "ok");
        return "已重启";
    });

    private static string[] PreviewRestore(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        return new[]
        {
            "# 需要临时 root（临时 Root 阶段）；备份来自 backup 目录或你指定的文件夹",
            $"{adbPath} push ./backup/abl_<SLOT>*.img {AppConfig.RemoteBackup}/abl_<SLOT>.img",
            $"{adbPath} push ./backup/ocdt*.img {AppConfig.RemoteBackup}/ocdt.img",
            $"{adbPath} shell \"echo 'dd if={AppConfig.RemoteBackup}/abl_<SLOT>.img of={AppConfig.BlockByName}abl_<SLOT> bs={AppConfig.DdBs} && sync' | {AppConfig.Ksud} debug su\"",
            $"{adbPath} shell \"echo 'dd if={AppConfig.RemoteBackup}/ocdt.img of={AppConfig.BlockByName}ocdt bs={AppConfig.DdBs} && sync' | {AppConfig.Ksud} debug su\"",
        };
    }

    public static readonly Stage[] Stages =
    {
        new("temproot", 1, "临时 Root",
            new Field[]
            {
                new("bundle", "bundle 目录", "dir",
                    "解压后的 Release bundle；本机文件名可任意（后缀 / 副本 / 版本号 / 空格都行）。本机不需要 Git Bash —— 一键 sh 的逻辑已经内置。"),
                new("attempts", "最多尝试次数", "int", "默认 4 次；exploit 触发重启后会自动等开机重试。",
                    defaultValue: AppConfig.MaxAttempts.ToString(), width: 6),
                new("variant", "提权变体", "choice",
                    "caps-only：把权限写进当前 shell 用户共享的 cred（更直接）；init_cred：用 commit_creds 换一份私有 cred（对其它进程更干净）。默认 caps-only，跑不通再换 init_cred。",
                    defaultValue: "capsonly",
                    choices: new[]
                    {
                        ("capsonly", "caps-only（推荐）"),
                        ("initcred", "init_cred"),
                    }),
                new("allow_unsupported", "内核未适配也强制继续", "bool",
                    "仅在你自己算过 profiles/<内核版本>.env 时勾选，否则会写错内核地址。"),
            },
            danger: "exploit 直接写内核内存，失败会触发重启；请保持屏幕常亮、别拔线。",
            preview: PreviewTemproot,
            build: _ => new List<Step> { S3Kernel(), S3Bundle(), S3Push(), S3Profile(), S3Exploit(), S3Verify(), S3Record() },
            needsRoot: false),
        new("push", 2, "推送解锁文件",
            new Field[]
            {
                new("abl", "abl 镜像", "file", "文件名不限：123abl.img、abl (1).img 都能用。"),
                new("ocdt", "ocdt 镜像", "file", "同上，认内容不认名字。"),
            },
            preview: PreviewPush,
            build: _ => new List<Step> { S4Check(), S4Push() }),
        new("backup", 3, "备份原分区",
            new Field[]
            {
                new("scope", "备份分区", "multi", "",
                    defaultValue: new[] { "abl_a", "abl_b", "ocdt" },
                    choices: new[] { ("abl_a", "abl_a"), ("abl_b", "abl_b"), ("ocdt", "ocdt") }),
                new("slot", "槽位", "choice", "默认按 ro.boot.slot_suffix 自动判断。",
                    defaultValue: "auto", choices: new[] { ("auto", "自动检测"), ("a", "A 槽"), ("b", "B 槽") }),
            },
            danger: "没有完成这一步就不要执行『写入解锁文件』。" +
                    "注意：『写入解锁文件』之后再回来备份，备份到的是解锁镜像而不是原厂内容，会被直接拦下。",
            preview: PreviewBackup,
            build: _ => new List<Step> { S5Root(), S5Dump(), S5Pull(), S5Manifest() },
            needsRoot: true),
        new("write", 4, "写入解锁文件",
            new Field[]
            {
                new("abl", "abl 镜像", "file", "与『推送解锁文件』里选的同一个文件。"),
                new("ocdt", "ocdt 镜像", "file", "与『推送解锁文件』里选的同一个文件。"),
                new("slot", "写入槽位", "choice", "必须与当前活动槽一致，写错槽位等于没解锁。",
                    defaultValue: "auto", choices: new[] { ("auto", "自动检测"), ("a", "A 槽"), ("b", "B 槽") }),
                new("confirm", "输入『解锁』确认", "text", "这一步会改写引导分区，必须手动输入『解锁』两个字。"),
                new("force", "跳过备份检查", "bool", "只在你自己另外备份过 abl / ocdt 时勾选。"),
            },
            danger: "写错分区可能变砖。请先完成『备份原分区』。",
            preview: PreviewWrite,
            build: _ => new List<Step> { S6Gate(), S6Push(), S6Ocdt(), S6Abl(), S6Record() },
            needsRoot: true),
        new("fastboot", 5, "fastboot 解锁",
            danger: "锁一旦解开，手机数据会被全部清除，先确认要留的东西都备份好了。\n" +
                    "手机进入工厂 fastboot 菜单后：选中 UNLOCK THE BOOTLOADER → 再按确认键（各机型按键不同，以屏幕提示为准）。\n" +
                    "注意 1：写入解锁文件之后，手机的触控和 WiFi 会失灵，这是正常现象，做完『还原原分区』就恢复正常。\n" +
                    "注意 2：这段时间要操作手机，建议用紫罗兰工具箱（VioletBox）投屏操作。\n" +
                    "注意 3：如果解锁指令写不进去，先开机进开发者选项打开『OEM 解锁』再重跑本阶段。",
            preview: PreviewUnlock,
            build: _ => new List<Step> { S7Reboot(), S7Ack(), S7Unlock(), S7Wait() }),
        new("verify", 6, "验证解锁状态",
            preview: PreviewVerify,
            build: _ => new List<Step> { S8Check(), S8Record() }),
        new("restore", 7, "还原原分区",
            new Field[]
            {
                new("backup_dir", "备份目录（可选）", "dir",
                    "点名的文件优先，其次在这个文件夹里按名字或体积自动认，最后才用账本里的原厂备份。"),
                new("abl_file", "abl 备份文件（可选）", "file", "直接点名 abl 的备份，优先级最高；留空则自动找。"),
                new("ocdt_file", "ocdt 备份文件（可选）", "file", "直接点名 ocdt 的备份，优先级最高；留空则自动找。"),
                new("slot", "槽位", "choice", "要与解锁时写入的槽位一致。", defaultValue: "auto",
                    choices: new[] { ("auto", "自动检测"), ("a", "A 槽"), ("b", "B 槽") }),
                new("confirm", "输入『还原』确认", "text", "写回引导分区同样是危险操作。"),
                new("reboot", "完成后自动重启", "bool", "还原校验通过后重启手机。", defaultValue: true),
            },
            danger: "需要临时 root；没有 root 时会被拦下。" +
                    "还原来源按『界面点名 > 备份目录 > 账本原厂备份』顺序取；选到解锁镜像或别的设备的备份会被直接拦下。",
            preview: PreviewRestore,
            build: _ => new List<Step> { S9Gate(), S9Push(), S9Restore(), S9Reboot() },
            needsRoot: true),
    };

    public static readonly IReadOnlyDictionary<string, Stage> StageById =
        Stages.ToDictionary(stage => stage.Sid);

    public static int StageIndex(string sid)
    {
        for (var index = 0; index < Stages.Length; index++)
        {
            if (Stages[index].Sid == sid)
                return index;
        }
        return 0;
    }

    public static string CurrentSlotLabel()
    {
        var data = StateStore.Load();
        var slot = data["device"]?["slot"]?.GetValue<string>() ?? "";
        return slot.Length > 0 ? "_" + slot : "—";
    }
}

internal sealed record DumpMeta(string Remote, long Size, long? Psize);
