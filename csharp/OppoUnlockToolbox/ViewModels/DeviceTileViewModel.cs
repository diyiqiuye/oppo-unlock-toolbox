namespace OppoUnlockToolbox.ViewModels;

/// <summary>设备环境里的一张小卡片。</summary>
public sealed class DeviceTileViewModel : ObservableObject
{
    private string _value = "未检测";
    private string _note = "";
    private string _tone = "ink3";

    public DeviceTileViewModel(string iconKey, string caption)
    {
        IconKey = iconKey;
        Caption = caption;
    }

    public string IconKey { get; }
    public string Caption { get; }

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>取值颜色：ok / danger / ink / ink3，界面按它触发画笔。</summary>
    public string Tone
    {
        get => _tone;
        set => Set(ref _tone, value);
    }
}
