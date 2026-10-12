// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Stages;

internal static class FastbootStage
{
    internal static Step S7VioletGate() => new("检查紫罗兰工具箱是否在后台运行", ctx =>
    {
        ctx.Status("检查紫罗兰工具箱（VioletToolBox）");
        var running = VioletGuard.FindRunning();
        if (running is not null)
        {
            ctx.Log($"检测到紫罗兰工具箱（{running}）正在后台运行，继续解锁。", "ok");
            return "紫罗兰已就绪";
        }
        throw new StepFailException(
            "没有检测到紫罗兰工具箱（VioletToolBox）在后台运行，已停止解锁。\n" +
            "解锁后手机的 USB 调试会自动关闭、触控和 WiFi 也会失灵，必须靠紫罗兰工具箱的投屏（scrcpy）来操作手机；" +
            "没有它挂着连接，手机重启后将无法再连接操作。\n" +
            "请先打开紫罗兰工具箱、连接好手机并保持在后台运行，再重跑本阶段。\n" +
            "下载：https://github.com/Smart-Paocai/VioletToolBox/releases");
    });

    internal static Step S7Reboot() => new("重启进工厂 fastboot", ctx =>
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

    internal static Step S7Ack() => new("解锁操作提示", ctx =>
    {

        ctx.Log("手机已进入工厂 fastboot 菜单：在屏幕上选中 UNLOCK THE BOOTLOADER，再按确认键。" +
                "不同机型按键不一样（音量键选择 / 电源键确认是最常见的组合），以屏幕上的提示为准。" +
                "确认后手机会清数据并自动重启。", "warn");
        ctx.Log("提醒：写入解锁文件之后，手机的触控和 WiFi 会失灵，这是正常现象，做『还原原分区』后就恢复正常。" +
                "这段时间要操作手机，建议用紫罗兰工具箱（VioletBox）投屏操作。", "warn");
        return "提示已显示";
    });

    internal static Step S7Unlock() => new("执行 fastboot flashing unlock", ctx =>
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

    internal static Step S7Wait() => new("等待清数据并重启完成", ctx =>
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

    internal static string[] PreviewUnlock(Dictionary<string, object?>? parameters)
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
}
