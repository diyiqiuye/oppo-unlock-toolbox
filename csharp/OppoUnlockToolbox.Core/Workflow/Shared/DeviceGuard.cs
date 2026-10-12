// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Shared;

internal static class DeviceGuard
{
    internal static string DeviceOnline(Ctx ctx, bool needBoot = true)
    {
        List<Dictionary<string, string>> devices;
        try
        {
            devices = adb.Devices();
        }
        catch (Exception exc)
        {
            throw new StepFailException($"无法调用 adb：{exc.Message}");
        }
        var states = devices.Select(d => d["state"]).ToList();
        if (devices.Count == 0)
        {
            if (adb.FastbootDevices().Count > 0)
                throw new StepFailException("设备当前在 fastboot 模式。请先让手机重启回系统（fastboot reboot）再做这一步。");
            throw new StepFailException("没有检测到设备：请插好数据线、开启 USB 调试，并在手机上允许本机授权。");
        }
        if (states.Contains("unauthorized"))
            throw new StepFailException("设备未授权：请在手机屏幕上点『允许 USB 调试』，然后重试。");
        if (!states.Contains("device"))
            throw new StepFailException($"设备状态是 {string.Join("/", states)}，请等系统起来或重新插拔数据线后重试。");
        var serial = devices.First(d => d["state"] == "device")["serial"];
        if (adb.Serial.Length == 0)
            adb.SetSerial(serial);
        ctx.Extras["serial"] = serial;
        if (needBoot && adb.ShellText("getprop sys.boot_completed").Trim() != "1")
        {
            ctx.Log("系统尚未启动完成，等待开机…", "warn");
            if (!adb.WaitForBoot(240000, ctx.Cancel))
                throw new StepFailException("等不到系统启动完成（sys.boot_completed != 1）。");
        }
        return serial;
    }

    internal static void RequireRoot(Ctx ctx, string hint = "")
    {
        ctx.Status("校验临时 root 通道");
        bool ok;
        try
        {
            ok = adb.RootOk(90000, ctx.Cancel);
        }
        catch (Exception exc)
        {
            throw new StepFailException($"root 校验失败：{exc.Message}");
        }
        if (!ok)
        {
            throw new StepFailException(
                "当前没有 uid=0 通道。" + hint +
                " 请先完成『临时 Root』（解锁清数据后临时 root 会失效，需要重跑一次）。");
        }
        ctx.Log("root 通道就绪：uid=0", "ok");
        ctx.Extras["root"] = true;
    }

    internal static long RequireFile(string path, string label)
    {
        if (string.IsNullOrEmpty(path))
            throw new StepFailException($"还没有选择{label}。");
        if (!File.Exists(path))
            throw new StepFailException($"{label} 不存在：{path}");
        var size = FileTools.FileSize(path) ?? 0;
        if (size <= 0)
            throw new StepFailException($"{label} 是空文件：{path}");
        return size;
    }

    internal static string Slot(Ctx ctx)
    {
        var want = ctx.Param("slot", "auto").Trim();
        if (want is "a" or "b")
        {
            ctx.Log($"槽位：_{want}（手动指定）", "note");
            ctx.Extras["slot"] = want;
            return want;
        }
        var detected = adb.ActiveSlot();
        if (detected.Length > 0)
        {
            ctx.Log($"活动槽：_{detected}（自动检测 ro.boot.slot_suffix）", "ok");
            ctx.Extras["slot"] = detected;
            return detected;
        }
        ctx.Log("读不到 ro.boot.slot_suffix，按教程默认值 _b 处理。" +
                "如果不确定，请在『写入解锁文件』里手动指定槽位。", "warn");
        ctx.Extras["slot"] = "b";
        return "b";
    }

    internal static string DeviceSerial()
    {
        foreach (var key in new[] { "ro.serialno", "ro.boot.serialno" })
        {
            var value = adb.ShellText($"getprop {key}", timeoutMs: 20000).Trim();
            if (value.Length > 0)
                return value;
        }
        return "";
    }
}
