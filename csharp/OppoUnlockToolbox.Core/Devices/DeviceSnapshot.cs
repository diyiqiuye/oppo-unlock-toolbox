namespace OppoUnlockToolbox.Core.Devices;

/// <summary>一次设备探测的结果快照（只读事实，不含界面文案）。</summary>
public sealed class DeviceSnapshot
{
    /// <summary>device / fastboot / unauthorized / offline / none。</summary>
    public string Mode { get; set; } = "none";

    public string Serial { get; set; } = "";
    public string Model { get; set; } = "";
    public string Release { get; set; } = "";
    public string Display { get; set; } = "";

    /// <summary>完整的内核串（uname -r），例如 5.10.198-android12-9-...。</summary>
    public string Kernel { get; set; } = "";

    public string Slot { get; set; } = "";

    /// <summary>ro.boot.flash.locked：0 已解锁、1 锁定、null 读不到。</summary>
    public int? Locked { get; set; }

    public string Error { get; set; } = "";

    public IReadOnlyDictionary<string, ToolInfo> Tools { get; set; } =
        new Dictionary<string, ToolInfo>();

    /// <summary>内核版本号部分（去掉 -android12-... 后缀）。</summary>
    public string Kver => Kernel.Split('-')[0];

    public bool IsFastboot => Mode == "fastboot";
}
