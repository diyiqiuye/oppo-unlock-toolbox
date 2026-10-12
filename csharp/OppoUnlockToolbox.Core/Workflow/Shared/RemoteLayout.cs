// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Shared;

internal static class RemoteLayout
{
    internal static void EnsureDirs(Ctx ctx)
    {
        foreach (var remote in new[] { DevicePaths.RemoteWork, DevicePaths.RemoteImages, DevicePaths.RemoteBackup })
        {
            var res = adb.Shell($"mkdir -p {remote}", timeoutMs: 30000, cancel: ctx.Cancel, allowFail: true);
            if (!res.Ok)
                throw new StepFailException($"无法在设备上创建目录 {remote}（{res.Error}）");
        }
        ctx.Log($"设备工作目录：{DevicePaths.RemoteWork}", "note");
    }

    internal static string RemoteImage(string key) =>
        $"{DevicePaths.RemoteImages}/{DevicePaths.ImageRemoteName[key]}";

    internal static void PushBins(Ctx ctx, bool quiet = false)
    {
        var bundle = (ctx.Extras.TryGetValue("bundle", out var b) ? b?.ToString() : null)
                     ?? ctx.Param("bundle", "").Trim();
        var found = ctx.Extras.TryGetValue("bins", out var binsObj) && binsObj is Dictionary<string, string> d
            ? d
            : ResolveBins(ctx, bundle).Found;
        foreach (var key in ExploitProfiles.BundleBins)
        {
            if (!found.TryGetValue(key, out var local) || local.Length == 0)
                throw new StepFailException($"认不出 {key} 是哪个文件，请回到上一步看提示。");
            var remote = $"{DevicePaths.DevTmp}/{key}";
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
            $"chmod 755 {DevicePaths.DevTmp}/exploit_guard {DevicePaths.DevTmp}/ksud {DevicePaths.DevTmp}/su0",
            timeoutMs: 60000, stream: false);
        ctx.Log("exploit_guard / ksud / su0 已就位并赋予执行权限。", "ok");
    }
}
