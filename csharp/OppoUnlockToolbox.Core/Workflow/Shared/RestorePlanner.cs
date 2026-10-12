// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Shared.WorkflowClock;
using static OppoUnlockToolbox.Core.Workflow.Shared.DeviceGuard;
using static OppoUnlockToolbox.Core.Workflow.Shared.RemoteLayout;
using static OppoUnlockToolbox.Core.Workflow.Shared.BundleFiles;
using static OppoUnlockToolbox.Core.Workflow.Shared.ImageLedger;
using static OppoUnlockToolbox.Core.Workflow.Shared.PartitionWriter;

namespace OppoUnlockToolbox.Core.Workflow.Shared;

internal static class RestorePlanner
{
    internal static Dictionary<string, string> ScanRestoreSources(Ctx ctx, string slot)
    {
        var sources = new Dictionary<string, string>();
        var overrideDir = ctx.Param("backup_dir", "").Trim();
        var parts = new[] { "ocdt", "abl_" + slot };
        var manual = new Dictionary<string, string>
        {
            ["ocdt"] = ctx.Param("ocdt_file", "").Trim(),
            ["abl_" + slot] = ctx.Param("abl_file", "").Trim(),
        };

        foreach (var part in parts)
        {
            if (manual[part].Length == 0)
                continue;
            if (!File.Exists(manual[part]))
                throw new StepFailException($"界面上指定的 {part} 备份文件不存在：{manual[part]}");
            sources[part] = manual[part];
            ctx.Log($"{part} → {Path.GetFileName(manual[part])}（界面指定，优先采用）", "ok");
        }

        var need = parts.Where(p => !sources.ContainsKey(p)).ToList();
        if (need.Count > 0 && overrideDir.Length > 0 && Directory.Exists(overrideDir))
        {
            var candidates = Directory.GetFiles(overrideDir)
                .Where(p => File.Exists(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            foreach (var part in need.ToList())
            {
                var token = NormName(part);
                foreach (var path in candidates)
                {
                    if (token.Length > 0 && NormName(Path.GetFileName(path)).Contains(token))
                    {
                        sources[part] = path;
                        need.Remove(part);
                        ctx.Log($"{part} → {Path.GetFileName(path)}（文件名匹配）", "note");
                        break;
                    }
                }
            }

            foreach (var part in need.ToList())
            {
                var size = adb.PartitionSize(part, cancel: ctx.Cancel);
                if (size is not { } partSize || partSize <= 0)
                    continue;
                var hits = candidates.Where(p => (FileTools.FileSize(p) ?? -1) == partSize).ToList();
                if (hits.Count == 1)
                {
                    sources[part] = hits[0];
                    need.Remove(part);
                    ctx.Log($"{part} → {Path.GetFileName(hits[0])}（体积与设备分区一致：{FileTools.FmtSize(partSize)}）", "ok");
                }
                else if (hits.Count > 1)
                {
                    ctx.Log($"{part} 有 {hits.Count} 个体积相同的候选文件，名字也认不出来，请在界面上直接点名。", "warn");
                }
            }
        }

        need = parts.Where(p => !sources.ContainsKey(p)).ToList();
        if (need.Count > 0)
        {
            var now = DeviceSerial();
            foreach (var part in need)
            {
                var record = StateStore.NewestStockBackup(part);
                if (record is null)
                    continue;
                var local = StateStore.RecordString(record, "local");
                var serial = StateStore.RecordString(record, "serial");
                if (serial.Length > 0 && now.Length > 0 && serial != now)
                    throw new StepFailException(
                        $"账本里的 {part} 备份来自另一台设备（序列号 {serial}），当前设备是 {now} —— 拒绝拿它还原。" +
                        "请在界面上点名本机自己的备份文件。");
                sources[part] = local;
                ctx.Log($"{part} → {Path.GetFileName(local)}（账本原厂备份）", "note");
            }
        }

        foreach (var part in parts)
        {
            if (!sources.TryGetValue(part, out var path))
                continue;
            var md5 = FileTools.Md5File(path, ctx.Cancel);
            if (LooksLikeWrittenImage(part, path))
                throw new StepFailException(
                    $"{part} 选中的文件就是解锁镜像（前 {FileTools.FmtSize(StateStore.WrittenSize(part))} 字节的 MD5 与账本里写入记录一致），" +
                    "不是原厂备份 —— 已中止还原。请改用原厂备份文件。");
            if (md5.Length > 0 && StateStore.StockMd5s(part).Contains(md5))
            {
                ctx.Log($"{part} 来源已核对：与账本里的原厂备份一致。", "ok");
                continue;
            }
            var kind = StateStore.BackupKind(part);
            ctx.Log($"{part} 来源未核对：{Path.GetFileName(path)} 不在账本的原厂备份里" +
                    (kind == StateStore.KindUnknown ? "（账本里那份备份的来源也未核对）" : "") +
                    "，请自行确认这是原厂镜像。", "warn");
        }
        return sources;
    }
}
