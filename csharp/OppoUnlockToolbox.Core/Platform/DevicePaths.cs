namespace OppoUnlockToolbox.Core.Platform;

/// <summary>设备上的目录、分区名与镜像文件名——跟机型/镜像相关的那一档常量。</summary>
public static class DevicePaths
{
    public const string DevTmp = "/data/local/tmp";
    public const string RemoteWork = DevTmp + "/oppotool";
    public const string RemoteBackup = RemoteWork + "/backup";
    public const string RemoteImages = RemoteWork + "/images";
    public const string Ksud = DevTmp + "/ksud";

    /// <summary>KernelSU 自己装的那份 ksud；DFRoot 走过之后手机上就有它。</summary>
    public const string KsudInstalled = "/data/adb/ksud";

    public const string BlockByName = "/dev/block/by-name/";
    public const int DdBs = 4096;

    public const string ImageAbl = "abl.img";
    public const string ImageOcdt = "ocdt.img";

    public static readonly string[] Partitions = { "abl_a", "abl_b", "ocdt" };

    public static readonly IReadOnlyDictionary<string, string> ImageRemoteName =
        new Dictionary<string, string> { ["abl"] = ImageAbl, ["ocdt"] = ImageOcdt };
}
