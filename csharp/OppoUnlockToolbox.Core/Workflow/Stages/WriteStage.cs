// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Stages;

internal static class WriteStage
{
    internal static Step S6Gate() => new("安全闸门：备份与授权确认", ctx =>
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

    internal static Step S6Push() => new("推送解锁文件", ctx =>
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

    /// <summary>
    /// DFRoot 走完之后，它的 bootstrap 会把所有以 _a / _b 结尾的分区（abl 就在其中）用
    /// BLKROSET 设成只读。内核写只读块设备时直接返回 EPERM，dmesg 里一个字都不留 ——
    /// 所以写分区之前必须先解开只读，写完再恢复。
    /// </summary>

    internal static Step S6Ocdt() => new("写入 ocdt", ctx =>
    {
        DdWrite(ctx, "ocdt", (string)ctx.Extras["ocdt"]!, "ocdt");
        return "ocdt 已写入";
    });

    internal static Step S6Abl() => new("写入活动槽 abl", ctx =>
    {
        var slot = ctx.Extras.TryGetValue("slot", out var s) ? s?.ToString() ?? "b" : "b";
        DdWrite(ctx, "abl_" + slot, (string)ctx.Extras["abl"]!, "abl");
        return $"abl_{slot} 已写入";
    });

    internal static Step S6Record() => new("记录状态", ctx =>
    {
        StateStore.MarkStage("write", true, $"slot={(ctx.Extras.TryGetValue("slot", out var s) ? s?.ToString() ?? "" : "")}");
        ctx.Log("写入阶段完成。下一步重启进 fastboot 执行解锁 —— 注意锁一旦解开就会清空数据。", "warn");
        return "已记录";
    });

    internal static string[] PreviewWrite(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        var abl = parameters is not null && parameters.TryGetValue("abl", out var a) && a is not null
            ? a.ToString()! : "<abl 镜像>";
        var ocdt = parameters is not null && parameters.TryGetValue("ocdt", out var o) && o is not null
            ? o.ToString()! : "<ocdt 镜像>";
        return new[]
        {
            $"{adbPath} push \"{abl}\" {DevicePaths.RemoteImages}/",
            $"{adbPath} push \"{ocdt}\" {DevicePaths.RemoteImages}/",
            "",
            "# ocdt 单分区",
            $"{adbPath} shell \"{Adb.RootRemote("dd if={DevicePaths.RemoteImages}/ocdt.img of={DevicePaths.BlockByName}ocdt bs={DevicePaths.DdBs} && sync")}\"",
            "# DFRoot 走完之后 _a/_b 分区是只读的，工具箱会先 blockdev --setrw，写完再 --setro",
            "# 活动槽 abl（<SLOT> 自动检测，教程示例为 b）",
            $"{adbPath} shell \"{Adb.RootRemote("dd if={DevicePaths.RemoteImages}/abl.img of={DevicePaths.BlockByName}abl_<SLOT> bs={DevicePaths.DdBs} && sync")}\"",
            "",
            "# 读回校验（前 <镜像大小> 字节）",
            $"{adbPath} shell \"{Adb.RootRemote("head -c <SIZE> {DevicePaths.BlockByName}ocdt | md5sum")}\"",
        };
    }
}
