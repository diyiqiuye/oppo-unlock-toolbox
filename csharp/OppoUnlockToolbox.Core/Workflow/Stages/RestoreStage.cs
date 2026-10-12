// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Stages;

internal static class RestoreStage
{
    internal static Step S9Gate() => new("确认 root 通道与备份来源", ctx =>
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

    internal static Step S9Push() => new("推送备份镜像到设备", ctx =>
    {
        var sources = ctx.Extras.TryGetValue("sources", out var s) && s is Dictionary<string, string> dict
            ? dict
            : new Dictionary<string, string>();
        foreach (var part in DevicePaths.Partitions)
        {
            if (!sources.TryGetValue(part, out var local) || !File.Exists(local))
                continue;
            var remote = $"{DevicePaths.RemoteBackup}/{part}.img";
            ctx.Run(adb.AdbArgv("push", local, remote), label: $"adb push {part}",
                timeoutMs: 900000, stream: false);
            if (adb.RemoteSize(remote, root: true, cancel: ctx.Cancel) != FileTools.FileSize(local))
                throw new StepFailException($"推送 {part} 备份后大小不一致，请重试。");
            ctx.Log($"{part} 备份已推送到设备。", "ok");
        }
        return "备份已就位";
    });

    internal static Step S9Restore() => new("写回原厂分区并校验", ctx =>
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
            var remote = $"{DevicePaths.RemoteBackup}/{part}.img";
            var dev = DevicePaths.BlockByName + part;
            ctx.Status($"还原 {part}");
            WritePartitionBlock(ctx, part, $"dd if={remote} of={dev} bs={DevicePaths.DdBs} && sync");
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

    internal static Step S9Reboot() => new("重启手机", ctx =>
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

    internal static string[] PreviewRestore(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        return new[]
        {
            "# 需要临时 root（临时 Root 阶段）；备份来自 backup 目录或你指定的文件夹",
            $"{adbPath} push ./backup/abl_<SLOT>*.img {DevicePaths.RemoteBackup}/abl_<SLOT>.img",
            $"{adbPath} push ./backup/ocdt*.img {DevicePaths.RemoteBackup}/ocdt.img",
            $"{adbPath} shell \"{Adb.RootRemote("dd if={DevicePaths.RemoteBackup}/abl_<SLOT>.img of={DevicePaths.BlockByName}abl_<SLOT> bs={DevicePaths.DdBs} && sync")}\"",
            $"{adbPath} shell \"{Adb.RootRemote("dd if={DevicePaths.RemoteBackup}/ocdt.img of={DevicePaths.BlockByName}ocdt bs={DevicePaths.DdBs} && sync")}\"",
        };
    }
}
