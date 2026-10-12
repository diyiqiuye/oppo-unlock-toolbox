// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using System.IO.Compression;
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Stages;

internal static class TemprootStage
{
    internal static Step S3Kernel() => new("匹配内核版本与偏移 profile", ctx =>
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
        if (model.Length > 0 && !DeviceCatalog.SupportedModels.Contains(model))
        {
            ctx.Log($"提示：{model} 不在已验证机型列表（{string.Join("/", DeviceCatalog.SupportedModels)}）里，" +
                    "分区名与偏移可能不同，请自行确认。", "warn");
        }
        var bundle = ctx.Param("bundle", "").Trim();
        var (profile, why) = bundle.Length > 0
            ? FindProfile(ctx, bundle, kver, kernelRaw)
            : ("", "还没选 bundle 目录");
        ctx.Extras["profile_path"] = profile;
        if (DeviceCatalog.KnownKVers.Contains(kver))
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
            $"内核 {(kver.Length > 0 ? kver : "未知")} 未适配（内置只支持 {string.Join(" / ", DeviceCatalog.KnownKVers)}），" +
            $"也没找到可用的偏移 profile（{why}）。请用 bundle 里的 tools/offset_tool 算出该固件的 offsets，" +
            $"存成 profiles/{(kver.Length > 0 ? kver : "<内核版本>")}.env（名字里带内核版本号即可），" +
            "或确认风险后勾选『内核未适配也强制继续』。");
    });

    internal static Step S3Bundle() => new("校验 Release bundle 文件", ctx =>
    {
        var bundle = ctx.Param("bundle", "").Trim();
        if (bundle.Length == 0)
            throw new StepFailException(
                "还没有选择 bundle 目录。请从项目的 Releases 页面下载 bundle 并解压，" +
                "然后选中含 exploit_guard / ksud / su0 / profiles 的那个目录。");
        if (!Directory.Exists(bundle))
            throw new StepFailException($"bundle 目录不存在：{bundle}");
        var (found, missing, listing) = ResolveBins(ctx, bundle);
        foreach (var key in ExploitProfiles.BundleBins)
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

    internal static Step S3Push() => new("推送二进制并赋予权限", ctx =>
    {
        PushBins(ctx);
        return "已推送 exploit_guard / ksud / su0";
    });

    internal static Step S3Profile() => new("加载版本 profile 参数", ctx =>
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
            var offsets = string.Join(" ", pairs);
            ctx.Log($"已加载 {Path.GetFileName(profile)}：{pairs.Count} 项偏移参数。" +
                    offsets.Substring(0, Math.Min(160, offsets.Length)), "ok");
        }
        else
        {
            ctx.Log("没有可用 profile，使用内置默认偏移。", "note");
        }
        ctx.Extras["profile_args"] = string.Join(" ", pairs);
        return $"{pairs.Count} 项偏移参数";
    });

    internal static bool KsuActive(CancellationToken cancel, string what, Action<string, string> log)
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

    internal static string GuardEnvArgs(Ctx ctx)
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
        var hit = DeviceCatalog.GuardModules.FirstOrDefault(m => lower.Contains(m));
        if (hit is null)
        {
            ctx.Log("没有发现已知的 OPPO 安全守卫模块，跳过 guard 补丁 —— 强行传 " +
                    "CHEESE_GUARD_PATCH 会让 exploit 直接退出（找不到签名就 return 1）。", "note");
            return "";
        }
        ctx.Log($"发现安全守卫模块 {hit}，启用 guard 补丁。", "note");
        return "CHEESE_GUARD_PATCH=1 CHEESE_GUARD_COMBINED=1";
    }

    internal static Step S3Exploit() => new("运行 exploit（含重试与异步 insmod 等待）", ctx =>
    {
        var attemptsRaw = ctx.Params.TryGetValue("attempts", out var a) && a is not null ? a.ToString() : "";
        var attempts = int.TryParse(attemptsRaw, out var n) ? n : ExploitProfiles.MaxAttempts;
        attempts = Math.Min(8, Math.Max(1, attempts));
        var profile = ctx.Extras.TryGetValue("profile_args", out var pa) ? pa?.ToString() ?? "" : "";
        var kver = ctx.Extras.TryGetValue("kver", out var kv) ? kv?.ToString() ?? "" : "";
        var kmi = ExploitProfiles.ExploitKmiFor(kver);
        if (!ExploitProfiles.ExploitKmiMap.TryGetValue(kver ?? "", out _))
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
        var logs = AppPaths.LogDir();
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
                var cmd = $"cd {DevicePaths.DevTmp} && {envPrefix} ./exploit_guard {DevicePaths.DevTmp}/ksud " +
                          $"late-load --kmi {kmi} --allow-shell --package-name {DeviceCatalog.KsuPackage}";
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
            var hit = ExploitProfiles.ExploitFailMarkers.FirstOrDefault(marker => res.Out.Contains(marker));
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

            ctx.Log($"第 {attempt} 次：等待异步 insmod（最多 {ExploitProfiles.LoadWaitS} 秒，不抢跑）…", "info");
            var waited = 0;
            while (waited < ExploitProfiles.LoadWaitS)
            {
                if (KsuActive(ctx.Cancel, $"第 {attempt} 次（t+{waited}s）", ctx.Log))
                {
                    ok = true;
                    break;
                }
                Sleep(ctx, 10);
                waited += 10;
                ctx.Status($"等待 insmod：{waited}s / {ExploitProfiles.LoadWaitS}s");
                if (adb.Devices().Count == 0)
                {
                    ctx.Log($"第 {attempt} 次：等待期间设备掉线，等开机后重试。", "warn");
                    break;
                }
            }
            if (ok)
                break;
            ctx.Log($"第 {attempt} 次：{ExploitProfiles.LoadWaitS} 秒内模块没出现，重试。", "warn");
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

    internal static Step S3Verify() => new("验证 root 通道（主判据 uid=0）", ctx =>
    {
        ctx.Status("验证 uid=0");
        var res = ctx.Shell(adb, "id", root: true, timeoutMs: 90000, allowFail: true);
        var first = res.Out.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        ctx.Log($"root 通道 → {(first.Length > 0 ? first : "(无输出)")}", "info");
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

    internal static Step S3Record() => new("写入账本", ctx =>
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

    // ── DFRoot（CVE-2026-43284）临时 root 通道 ──
    //
    // DFRoot 只能以「装在手机上的 App」形式跑：它得用 Android 框架的 IpSecManager 建 IPsec ESP
    // transform，再把参数交给 libdfroot.so。所以工具箱只负责装它、打开它，然后等 uid=0 ——
    // 中间那一下「Launch Root」由用户在手机上点。

    internal static bool IsDfroot(IReadOnlyDictionary<string, object?>? vars) =>
        vars is not null && vars.TryGetValue("method", out var m) && m?.ToString() == "dfroot";

    internal static string TemprootMethod(Dictionary<string, object?>? parameters) =>
        IsDfroot(parameters) ? "dfroot" : "gpu";

    internal static Step D1Launch() => new("在手机上打开 DFRoot", ctx =>
    {
        DeviceOnline(ctx);
        if (!InstalledPackages().Contains(ExploitProfiles.DfrootPackage))
        {
            throw new StepFailException(
                "手机上还没装 DFRoot。先在『资源下载』里下载并安装 DFRoot，再重跑本阶段。");
        }
        var res = ctx.Run(adb.AdbArgv("shell", "am", "start", "-n", ExploitProfiles.DfrootComponent),
            label: "am start", timeoutMs: 60000, allowFail: true);
        if (!res.Ok || res.Out.Contains("Error", StringComparison.OrdinalIgnoreCase))
            throw new StepFailException($"打开 DFRoot 失败：{res.Out.Trim()}");
        return "已打开";
    });

    // DFRoot 这条路手机上没有 shell 够得着的 ksud（KernelSU 装的 /data/adb/ksud 和 DFRoot 自己
    // 用的 /data/user_de/0/df.root/ksud 都是 root-only），而设备自带的 su 又卡在管理器授权上。
    // GPU 链路之所以能要 root，就是因为它自己把 bundle 里的 ksud 推到了 /data/local/tmp 并 chmod 755。
    // 这里照做：从 SU 管理器 apk 里抠出 libksud.so（DFRoot 自己也是拿它当 ksud 用的）推上去。

    internal static Step D2EnsureKsud() => new("把 SU 管理器自带的 ksud 放上去", ctx =>
    {
        if (adb.ShellText($"[ -x {DevicePaths.Ksud} ] && echo yes", timeoutMs: 20000)
               .Contains("yes", StringComparison.Ordinal))
        {
            ctx.Log($"{DevicePaths.Ksud} 已经在手机上。", "ok");
            return "已存在";
        }

        var apk = FetchSuManagerApk(ctx);
        var local = ExtractKsud(ctx, apk);
        ctx.Run(adb.AdbArgv("push", local, DevicePaths.Ksud), label: "adb push ksud",
            timeoutMs: 300000, allowFail: false);
        ctx.Shell(adb, $"chmod 755 {DevicePaths.Ksud}", timeoutMs: 60000);
        ctx.Log($"{DevicePaths.Ksud} 已就位，root 通道有了。", "ok");
        return "已推送";
    });

    /// <summary>找一个自带 libksud.so 的 SU 管理器，把它的 apk 弄到本机：先看本机 payload 目录，再回手机拉。</summary>

    internal static string FetchSuManagerApk(Ctx ctx)
    {
        foreach (var dir in AppPaths.PayloadDirs())
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                var hit = Directory.EnumerateFiles(dir, "*.apk").FirstOrDefault(f =>
                    DeviceCatalog.SuManagerHints.Any(h =>
                        Path.GetFileName(f).Contains(h, StringComparison.OrdinalIgnoreCase)));
                if (hit != null)
                {
                    ctx.Log($"本机已有：{Path.GetFileName(hit)}", "note");
                    return hit;
                }
            }
            catch
            {

            }
        }

        var pkgs = InstalledPackages()
            .Where(p => DeviceCatalog.SuManagerHints.Any(h => p.Contains(h, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Equals(DeviceCatalog.KsuPackage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        if (pkgs.Count == 0)
        {
            throw new StepFailException(
                "手机上没找到自带 libksud.so 的 SU 管理器。先在『资源下载』里装一个 KernelSU 管理器，再重跑本阶段。");
        }

        var local = Path.Combine(AppPaths.AppDir(), "tmp", "su-manager.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        foreach (var pkg in pkgs)
        {
            var remote = adb.ShellText($"pm path {pkg}", timeoutMs: 30000)
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("package:", StringComparison.Ordinal) &&
                                     l.EndsWith("base.apk", StringComparison.OrdinalIgnoreCase));
            if (remote is null)
                continue;
            File.Delete(local);
            var res = ctx.Run(adb.AdbArgv("pull", remote.Substring("package:".Length), local),
                label: "adb pull", timeoutMs: 300000, allowFail: true, stream: false);
            if (res.Ok && File.Exists(local))
            {
                ctx.Log($"从手机上拉回 {pkg} 的 apk。", "note");
                return local;
            }
        }
        throw new StepFailException("拿不到 SU 管理器的 apk，没法从里面取 ksud。");
    }

    /// <summary>从 apk 里抠出 libksud.so，落到本机临时目录。</summary>

    internal static string ExtractKsud(Ctx ctx, string apk)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(apk);
        var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.Equals("lib/arm64-v8a/libksud.so", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("/libksud.so", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new StepFailException($"{Path.GetFileName(apk)} 里没有 libksud.so，换个 SU 管理器再试。");

        var outPath = Path.Combine(AppPaths.AppDir(), "tmp", "ksud");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        entry.ExtractToFile(outPath, true);
        ctx.Log($"取到 libksud.so（{entry.Length} 字节）", "note");
        return outPath;
    }

    internal static Step D3WaitRoot() => new("等待 root 生效（ksud 返回 uid=0）", ctx =>
    {
        ctx.Status("等待 uid=0");
        var deadline = DateTime.UtcNow.AddSeconds(300);
        var loaded = false;
        var hinted = false;
        string? lastEcho = null;
        while (DateTime.UtcNow < deadline)
        {
            ctx.CheckCancel();

            var res = adb.Shell("id", root: true, timeoutMs: 25000, cancel: ctx.Cancel, allowFail: true);
            var echo = (res.Out + " " + res.Error).Replace("\r", "").Trim();
            if (echo.StartsWith("uid=0", StringComparison.Ordinal))
            {
                ctx.Extras["root"] = true;
                ctx.Log($"ROOT OK（{echo.Split('\n')[0].Trim()}）—— 临时 root 已生效。重启手机后失效，需要重跑本阶段。", "ok");
                return "uid=0";
            }
            if (echo != lastEcho)
            {
                lastEcho = echo;
                ctx.Log($"root 探测回显：{(echo.Length > 0 ? echo : "(空)")}　退出码 {res.Code}", "warn");
            }
            if (!hinted && (echo.Contains("Operation not permitted", StringComparison.Ordinal) ||
                            echo.Contains("Bad file descriptor", StringComparison.Ordinal) ||
                            echo.Contains("Permission denied", StringComparison.Ordinal)))
            {
                hinted = true;
                ctx.Log("内核不给 adb shell 开门。去手机上打开 KernelSU 管理器 → 设置 → 打开『adb root』，" +
                        "再重跑本阶段。", "warn");
            }

            if (!loaded)
            {
                var log = adb.ShellText($"logcat -d -s {ExploitProfiles.DfrootLogTag}:I", timeoutMs: 30000);
                if (log.Contains(ExploitProfiles.DfrootOkMarker, StringComparison.Ordinal))
                {
                    loaded = true;
                    ctx.Log("DFRoot 已经把 KernelSU 加载起来了，继续确认 root 通道…", "ok");
                }
            }
            Thread.Sleep(2000);
        }

        DumpDfrootLog(ctx, adb.ShellText($"logcat -d -s {ExploitProfiles.DfrootLogTag}:I", timeoutMs: 30000));
        throw new StepFailException("等不到 uid=0（5 分钟超时）。上面那行『root 探测回显』就是原因；" +
                                    "如果是 Operation not permitted / Bad file descriptor，去手机上打开 " +
                                    "KernelSU 管理器 → 设置 → 『adb root』，再重跑本阶段。");
    });

    internal static Step D4Record() => new("写入账本", ctx =>
    {
        StateStore.SetDevice(new Dictionary<string, object?>
        {
            ["model"] = adb.Prop("ro.product.model"),
            ["kernel"] = adb.KernelRaw(),
            ["kver"] = adb.KernelVersion(),
            ["slot"] = adb.ActiveSlot(),
            ["serial"] = DeviceSerial(),
            ["root"] = true,
        });
        StateStore.MarkStage("temproot", true, "dfroot");
        return "已记录";
    });

    internal static void DumpDfrootLog(Ctx ctx, string log)
    {
        foreach (var line in log.Split('\n').TakeLast(12))
        {
            if (line.Trim().Length > 0)
                ctx.Log("dfroot: " + line.Trim(), "note");
        }
    }

    internal static List<string> InstalledPackages() =>
        adb.ShellText("pm list packages -3", timeoutMs: 60000)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => l.Substring("package:".Length).Trim())
            .Where(l => l.Length > 0)
            .ToList();

    internal static string[] PreviewTemproot2(Dictionary<string, object?>? parameters)
    {
        if (TemprootMethod(parameters) != "dfroot")
            return PreviewTemproot(parameters);
        return new[]
        {
            "# 1) 打开 DFRoot：adb shell am start -n df.root/.MainActivity",
            "# 2) 在手机上点 Launch Root；工具箱同时从 SU 管理器 apk 里取出 libksud.so 推上去：",
            $"      adb push <su-manager.apk 里的 libksud.so> {DevicePaths.Ksud} && adb shell chmod 755 {DevicePaths.Ksud}",
            $"# 3) 等 root：adb shell \"{Adb.RootRemote("id")}\" 返回 uid=0",
        };
    }

    internal static string[] PreviewTemproot(Dictionary<string, object?>? parameters)
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
            $"late-load --kmi <KMI> --allow-shell --package-name {DeviceCatalog.KsuPackage}\"",
            "",
            "# 3) 等待异步 insmod，然后验证主判据",
            $"{adbPath} shell \"grep -c '^kernelsu' /proc/modules\"",
            $"{adbPath} shell \"{Adb.RootRemote("id")}\"",
        };
    }
}
