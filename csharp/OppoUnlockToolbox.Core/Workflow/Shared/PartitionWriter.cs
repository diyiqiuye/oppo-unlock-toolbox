// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;

namespace OppoUnlockToolbox.Core.Workflow.Shared;

internal static class PartitionWriter
{
    internal static void SetPartitionRo(Ctx ctx, string part, bool readOnly)
    {
        var flag = readOnly ? "--setro" : "--setrw";
        ctx.Shell(adb, $"blockdev {flag} {DevicePaths.BlockByName}{part}",
            root: true, timeoutMs: 60000, allowFail: true, stream: false);
    }

    /// <summary>包住一次分区写入：先解开只读，写完不管成败都恢复。</summary>

    internal static void WritePartitionBlock(Ctx ctx, string part, string ddCommand)
    {
        SetPartitionRo(ctx, part, readOnly: false);
        try
        {
            ctx.Shell(adb, ddCommand, root: true, timeoutMs: 900000);
        }
        finally
        {
            SetPartitionRo(ctx, part, readOnly: true);
        }
    }

    internal static string DdWrite(Ctx ctx, string part, string imageLocal, string key)
    {
        var remote = RemoteImage(key);
        var dev = DevicePaths.BlockByName + part;
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
        WritePartitionBlock(ctx, part, $"dd if={remote} of={dev} bs={DevicePaths.DdBs} && sync");
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
}
