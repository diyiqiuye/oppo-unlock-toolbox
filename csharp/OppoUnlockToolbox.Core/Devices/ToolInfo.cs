namespace OppoUnlockToolbox.Core.Devices;

/// <summary>一个外部工具（adb / fastboot）的落点：路径为空表示没找到。</summary>
public sealed record ToolInfo(string? Path, string Source)
{
    public bool Ready => Path is not null;
}
