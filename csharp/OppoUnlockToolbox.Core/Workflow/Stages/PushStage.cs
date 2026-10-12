// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.RestorePlanner;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Stages;

internal static class PushStage
{
    internal static Step S4Check() => new("校验本地解锁文件", ctx =>
    {
        var picked = new Dictionary<string, (string Path, long Size, string Md5)>();
        foreach (var (key, label) in new[] { ("abl", "abl 镜像"), ("ocdt", "ocdt 镜像") })
        {
            var path = ctx.Param(key, "").Trim();
            var size = RequireFile(path, label);
            var digest = FileTools.Md5File(path, ctx.Cancel);
            ctx.Log($"{label} ← {Path.GetFileName(path)}（{FileTools.FmtSize(size)}，MD5 {FileTools.FmtMd5(digest, 12)}）", "ok");
            picked[key] = (path, size, digest);
            ctx.Extras[key] = path;
        }
        if (picked["abl"].Path == picked["ocdt"].Path ||
            (picked["abl"].Md5.Length > 0 && picked["abl"].Md5 == picked["ocdt"].Md5))
        {
            throw new StepFailException("abl 与 ocdt 选到了同一个文件，这是两个不同的镜像，请分开选择。");
        }
        if (picked["abl"].Size < picked["ocdt"].Size)
        {
            ctx.Log($"提示：abl（{FileTools.FmtSize(picked["abl"].Size)}）比 ocdt（{FileTools.FmtSize(picked["ocdt"].Size)}）小，" +
                    "通常 abl 更大一些，确认没有选反。", "warn");
        }
        ctx.Log("本机文件名随意（123abl.img 也行）；设备端会固定用 abl.img / ocdt.img 落盘。", "note");
        return "两个镜像就绪";
    });

    internal static Step S4Push() => new("推送到设备临时目录", ctx =>
    {
        DeviceOnline(ctx);
        EnsureDirs(ctx);
        foreach (var key in new[] { "abl", "ocdt" })
        {
            var local = (string)ctx.Extras[key]!;
            var remote = RemoteImage(key);
            ctx.Run(adb.AdbArgv("push", local, remote),
                label: $"adb push {Path.GetFileName(local)}", timeoutMs: 600000, stream: false);
            var remoteSize = adb.RemoteSize(remote, cancel: ctx.Cancel);
            var localSize = FileTools.FileSize(local);
            if (remoteSize != localSize)
                throw new StepFailException(
                    $"推送 {Path.GetFileName(local)} 后大小不一致（本地 {localSize?.ToString() ?? "?"} / 设备 {remoteSize?.ToString() ?? "?"}）。");
            ctx.Log($"{Path.GetFileName(local)} → {remote}（{FileTools.FmtSize(localSize)}）", "ok");
        }
        StateStore.MarkStage("push", true);
        return $"已推送到 {DevicePaths.RemoteImages}";
    });

    internal static string[] PreviewPush(Dictionary<string, object?>? parameters)
    {
        const string adbPath = "adb";
        var abl = parameters is not null && parameters.TryGetValue("abl", out var a) && a is not null
            ? a.ToString()! : "<abl 镜像>";
        var ocdt = parameters is not null && parameters.TryGetValue("ocdt", out var o) && o is not null
            ? o.ToString()! : "<ocdt 镜像>";
        return new[]
        {
            "# 本地文件名随意（看内容不看名字），设备端固定落成 abl.img / ocdt.img",
            $"{adbPath} push \"{abl}\" {DevicePaths.RemoteImages}/abl.img",
            $"{adbPath} push \"{ocdt}\" {DevicePaths.RemoteImages}/ocdt.img",
            $"{adbPath} shell \"wc -c < {DevicePaths.RemoteImages}/abl.img\"",
        };
    }
}
