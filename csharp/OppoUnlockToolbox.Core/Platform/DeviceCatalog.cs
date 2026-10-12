namespace OppoUnlockToolbox.Core.Platform;

/// <summary>已适配的机型、内核与 SU 管理器识别规则。</summary>
public static class DeviceCatalog
{
    public static readonly string[] SupportedModels =
    {
        "PFEM00", "PFEM10", "CPH2305", "PGU110",
        "PGEM10",
    };

    public static readonly string[] KnownKVers = { "5.10.198", "5.10.168", "5.15.123" };

    public static readonly string[] GuardModules = { "oplus_security_guard" };

    public const string KsuPackage = "me.weishu.kernelsu";

    /// <summary>包名里带这些片段的当成 SU 管理器；DFRoot 需要它自带 libksud.so。</summary>
    public static readonly string[] SuManagerHints =
    {
        "kernelsu", "ksunext", "kowsu", "sukisu",
    };
}
