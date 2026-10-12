namespace OppoUnlockToolbox.ViewModels;

/// <summary>左侧下面那四张卡：工具链 / 设备 / 系统 / 引导锁。</summary>
public sealed class DevicePanelViewModel : ObservableObject
{
    private string _modeText = "未检测到设备";
    private string _statusDetail = "按 F5 重新检测";

    public DevicePanelViewModel()
    {
        Tiles = new[] { Tools, Device, System, Lock };
    }

    public DeviceTileViewModel Tools { get; } = new("chip", "工具链");
    public DeviceTileViewModel Device { get; } = new("phone", "设备");
    public DeviceTileViewModel System { get; } = new("shield", "系统");
    public DeviceTileViewModel Lock { get; } = new("lock", "引导锁 / 槽位");

    public IReadOnlyList<DeviceTileViewModel> Tiles { get; }

    /// <summary>状态胶囊的主文案：「设备已连接 / 未检测到设备」。</summary>
    public string ModeText
    {
        get => _modeText;
        set => Set(ref _modeText, value);
    }

    /// <summary>状态胶囊的副文案：序列号，或一句提示。</summary>
    public string StatusDetail
    {
        get => _statusDetail;
        set => Set(ref _statusDetail, value);
    }

    public void Apply(DeviceSnapshot snapshot)
    {
        var adb = snapshot.Tools.GetValueOrDefault("adb");
        var fastboot = snapshot.Tools.GetValueOrDefault("fastboot");
        var ready = adb is { Ready: true } && fastboot is { Ready: true };

        Tools.Value = ready ? "就绪" : "缺失";
        Tools.Tone = ready ? "ok" : "danger";
        Tools.Note = $"adb {adb?.Source ?? "未找到"} · fastboot {fastboot?.Source ?? "未找到"}";

        Device.Value = ModeTextOf(snapshot.Mode);
        Device.Tone = "ink";
        Device.Note = snapshot.Serial.Length > 0 ? snapshot.Serial : "未检测";

        System.Value = snapshot.Model.Length > 0
            ? $"Android {snapshot.Release} · {snapshot.Model}"
            : "未检测";
        System.Note = snapshot.Kernel.Length > 0 ? snapshot.Kernel : "内核未检测";

        Lock.Value = snapshot.Locked switch { 0 => "已解锁", 1 => "锁定", _ => "未检测" };
        Lock.Tone = snapshot.Locked switch { 0 => "ok", 1 => "ink", _ => "ink3" };
        Lock.Note = snapshot.Slot.Length > 0 ? $"活动槽 _{snapshot.Slot}" : "活动槽未识别";

        ModeText = ModeTextOf(snapshot.Mode);
        StatusDetail = snapshot.Serial.Length > 0 ? snapshot.Serial : "按 F5 重新检测";
    }

    public static string ModeTextOf(string mode) => mode switch
    {
        "device" => "设备已连接",
        "fastboot" => "设备在 fastboot",
        "unauthorized" => "设备未授权",
        "offline" => "设备离线",
        "none" => "未检测到设备",
        _ => mode,
    };
}
