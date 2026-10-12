// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Shared;

internal static class WorkflowClock
{
    internal static Adb adb => AdbDefault.Instance;

    internal static string Now() => DateTime.Now.ToString("MMdd_HHmmss");

    internal static void Sleep(Ctx ctx, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            ctx.CheckCancel();
            var remain = (end - DateTime.UtcNow).TotalSeconds;
            Adb.CancellableSleep((int)(Math.Min(1.0, Math.Max(0.05, remain)) * 1000), ctx.Cancel);
        }
    }
}
