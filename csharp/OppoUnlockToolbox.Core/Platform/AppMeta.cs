namespace OppoUnlockToolbox.Core.Platform;

/// <summary>应用元信息：名字、版本、作者、对外链接。改版本号时这里和 csproj / installer.iss / 界面要一起改。</summary>
public static class AppMeta
{
    public const string AppName = "OPPO 解锁工具箱";
    public const string AppShort = "OPPO 解锁工具箱";
    public const string AppTagline = "Find X5 Pro / Find X6 Pro · BL 解锁 + 临时 root · 七阶段工作流";
    public const string AppVersion = "0.3";
    public const string Credit = "临时 root 链路移植自 diyiqiuye/CVE-2025-21479-FX5P (GPL-3.0)；" +
                                 "DFRoot 通道基于 diabl0w/DFRoot；工作流整理自社区解锁教程";

    public const string AuthorName = "寄山海";
    public const string AuthorCoolapkId = "寄山海";

    public const string UrlAuthorGithub = "https://github.com/diyiqiuye";
    public const string UrlCoolapk = "https://www.coolapk.com/";

    public const string UrlCve = "https://github.com/diyiqiuye/CVE-2025-21479-FX5P";
    public const string UrlCveReleases = UrlCve + "/releases";
    public const string UrlKernelSU = "https://github.com/tiann/KernelSU";
    public const string UrlPlatformTools = "https://developer.android.com/tools/releases/platform-tools";
}
