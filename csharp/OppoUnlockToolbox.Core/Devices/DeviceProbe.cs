using OppoUnlockToolbox.Core.Platform;

namespace OppoUnlockToolbox.Core.Devices;

/// <summary>
/// 只读地问设备一句话：连上了吗、什么机型、什么内核、哪个槽、锁没锁。
/// 界面上的「重新检测」和设备热插拔触发的都是它；不做任何写操作。
/// </summary>
public static class DeviceProbe
{
    public static DeviceSnapshot Probe()
    {
        var snapshot = new DeviceSnapshot();
        try
        {
            var tools = new Dictionary<string, ToolInfo>();
            foreach (var name in new[] { "adb", "fastboot" })
                tools[name] = new ToolInfo(ToolLocator.FindTool(name), ToolLocator.ToolSource(name));
            snapshot.Tools = tools;

            var adb = AdbDefault.Instance;
            var devices = adb.Devices();
            if (devices.Count > 0)
            {
                var entry = devices[0];
                foreach (var candidate in devices)
                {
                    if (candidate["state"] == "device")
                    {
                        entry = candidate;
                        break;
                    }
                }
                snapshot.Mode = entry["state"];
                snapshot.Serial = entry["serial"];
                if (entry["state"] == "device")
                {
                    adb.SetSerial(entry["serial"]);
                    var props = adb.Props(40000);
                    snapshot.Model = props.GetValueOrDefault("ro.product.model", "");
                    snapshot.Release = props.GetValueOrDefault("ro.build.version.release", "");
                    snapshot.Display = props.GetValueOrDefault("ro.build.display.id", "");
                    snapshot.Kernel = adb.KernelRaw();
                    snapshot.Slot = adb.ActiveSlot();
                    snapshot.Locked = adb.FlashLocked();
                }
            }
            else if (adb.FastbootDevices().Count > 0)
            {
                snapshot.Mode = "fastboot";
                snapshot.Serial = adb.FastbootDevices()[0];
            }
        }
        catch (Exception exc)
        {
            snapshot.Error = exc.Message;
        }
        return snapshot;
    }
}
