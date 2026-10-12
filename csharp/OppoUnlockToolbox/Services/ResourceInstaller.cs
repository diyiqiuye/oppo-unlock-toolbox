using System.IO;

namespace OppoUnlockToolbox.Services;

/// <summary>
/// 「资源下载」下完东西之后，把结果对号入座地填进各阶段的参数里
/// （bundle 目录 → 临时 Root；abl/ocdt 镜像 → 推送 + 写入），或者直接把 apk 装到手机上。
/// </summary>
public sealed class ResourceInstaller
{
    private readonly StageSession _session;
    private readonly Action<string, string> _log;

    public ResourceInstaller(StageSession session, Action<string, string> log)
    {
        _session = session;
        _log = log;
    }

    public (string Summary, string Error) Apply(string resourceKey, string resultPath, bool unzip)
    {
        if (resourceKey.Contains("ksu"))
            return InstallApk(resultPath, "KernelSU 管理器");
        if (resourceKey.Contains("dfroot"))
            return InstallApk(resultPath, "DFRoot");

        var analysis = ResourceFill.Analyze(resourceKey, resultPath, unzip);
        var affectedStages = new List<string>();
        var summary = "";
        var fillError = "";

        if (analysis.BundleDir != null)
        {
            _session.VarsOf("temproot")["bundle"] = analysis.BundleDir;
            affectedStages.Add("temproot");
            summary = "已填入『临时 Root』的 bundle 目录";
        }
        else if (resourceKey.Contains("bundle"))
        {
            fillError = "没认出 bundle 目录（需要含 exploit_guard / ksud / su0），请手动选择";
        }

        if (analysis.Abl != null || analysis.Ocdt != null)
        {
            foreach (var sid in new[] { "push", "write" })
            {
                var vars = _session.VarsOf(sid);
                if (analysis.Abl != null) vars["abl"] = analysis.Abl;
                if (analysis.Ocdt != null) vars["ocdt"] = analysis.Ocdt;
                affectedStages.Add(sid);
            }
            var detail = (analysis.Abl != null && analysis.Ocdt != null)
                ? ""
                : analysis.Abl == null ? "（abl 未识别）" : "（ocdt 未识别）";
            summary = summary.Length > 0
                ? summary + "；已填入镜像" + detail
                : "已填入『推送解锁文件』和『写入解锁文件』的镜像路径" + detail;
        }
        else if (resourceKey.Contains("image") && fillError.Length == 0)
        {
            fillError = "没认出 abl*.img / ocdt*.img，请手动选择";
        }

        foreach (var sid in affectedStages.Distinct())
            _session.NotifyVarsChanged(sid);
        return (summary, fillError);
    }

    public (string Summary, string Error) InstallApk(string apkPath, string what)
    {
        if (!File.Exists(apkPath))
            return ("", "apk 文件不存在：" + apkPath);
        var mode = AdbDefault.Instance.DeviceState();
        if (mode != "device")
        {
            var reason = mode switch
            {
                "none" => "没有检测到设备：插好数据线、打开 USB 调试后重试安装。",
                "unauthorized" => "设备未授权：请在手机上允许 USB 调试后重试。",
                "fastboot" => "设备在 fastboot 模式，先让手机正常开机再安装。",
                _ => $"设备状态是 {mode}，暂时装不了。",
            };
            return ("", reason);
        }
        var res = ProcessRunner.Run(AdbDefault.Instance.AdbArgv("install", "-r", apkPath),
            new RunOptions { TimeoutMs = 180000 });
        var tail = res.Tail(2);
        if (res.Ok || tail.Contains("Success"))
        {
            _log("ok", $"{what} 已安装到手机。");
            return ("已安装到手机", "");
        }
        _log("err", $"安装失败：{tail}");
        return ("", $"安装失败：{tail}");
    }
}
