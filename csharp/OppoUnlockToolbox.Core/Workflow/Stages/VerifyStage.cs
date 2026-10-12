// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Stages;

internal static class VerifyStage
{
    internal static Step S8Check() => new("读取引导锁状态", ctx =>
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

    internal static Step S8Record() => new("记录解锁结果", ctx =>
    {
        StateStore.MarkStage("unlock", true, "flash.locked=0");
        ctx.Log("解锁完成。以后每次开机都会出现解锁警告提示，属于正常现象。", "note");
        ctx.Log("建议现在做『还原原分区』：重新获取临时 root 并把 abl / ocdt 还原成原厂镜像。", "warn");
        return "已记录";
    });

    internal static string[] PreviewVerify(Dictionary<string, object?>? parameters)
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
}
