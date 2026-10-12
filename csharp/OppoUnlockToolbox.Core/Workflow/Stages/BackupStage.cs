// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;
using System.Text;

namespace OppoUnlockToolbox.Core.Workflow.Stages;

internal static class BackupStage
{
    internal static Step S5Root() => new("确认 root 通道", ctx =>
    {
        RequireRoot(ctx);
        EnsureDirs(ctx);
        return "uid=0";
    });

    internal static List<string> BackupTargets(Ctx ctx)
    {
        var all = DevicePaths.Partitions.ToList();
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

    internal static Step S5Dump() => new("dd 备份分区到设备临时目录", ctx =>
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
            var dev = DevicePaths.BlockByName + part;
            var psize = adb.PartitionSize(part, cancel: ctx.Cancel);
            var remote = $"{DevicePaths.RemoteBackup}/{part}.img";
            ctx.Shell(adb, $"dd if={dev} of={remote} bs={DevicePaths.DdBs} && sync",
                root: true, timeoutMs: 900000);
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

    internal static Step S5Pull() => new("拉取到本机并逐字节校验", ctx =>
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
            var local = Path.Combine(AppPaths.BackupDir(), $"{part}_{stamp}.img");
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

    internal static Step S5Manifest() => new("写出备份清单", ctx =>
    {
        var data = StateStore.Load();
        var partitions = new System.Text.Json.Nodes.JsonObject();
        foreach (var part in DevicePaths.Partitions)
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
        var path = Path.Combine(AppPaths.BackupDir(),
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

    internal static string[] PreviewBackup(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        return new[]
        {
            "# 需要临时 root（临时 Root 阶段）；<SLOT> 为自动检测的活动槽",
            $"{adbPath} shell \"{Adb.RootRemote("dd if={DevicePaths.BlockByName}abl_a of={DevicePaths.RemoteBackup}/abl_a.img bs={DevicePaths.DdBs} && sync")}\"",
            $"{adbPath} shell \"{Adb.RootRemote("dd if={DevicePaths.BlockByName}abl_b of={DevicePaths.RemoteBackup}/abl_b.img bs={DevicePaths.DdBs} && sync")}\"",
            $"{adbPath} shell \"{Adb.RootRemote("dd if={DevicePaths.BlockByName}ocdt of={DevicePaths.RemoteBackup}/ocdt.img bs={DevicePaths.DdBs} && sync")}\"",
            $"{adbPath} pull {DevicePaths.RemoteBackup}/abl_<SLOT>.img ./backup/",
            $"{adbPath} shell \"{Adb.RootRemote("md5sum {DevicePaths.RemoteBackup}/ocdt.img")}\"",
        };
    }
}
