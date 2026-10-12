// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Shared;

internal static class ImageLedger
{
    internal static bool LooksLikeWrittenImage(string part, string path)
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
}
