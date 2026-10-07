

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OppoUnlockToolbox.Core;

var failures = new List<string>();
var checkCount = 0;
var baseDir = AppContext.BaseDirectory;

var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", ".."));
var mockCandidates = new[]
{
    Path.Combine(projectRoot, "csharp", "tests", "mock_device.py"),
    Path.Combine(projectRoot, "tests", "mock_device.py"),
};
var mockScript = mockCandidates.FirstOrDefault(File.Exists) ?? mockCandidates[0];
var adbShim = Path.Combine(projectRoot, "csharp", "tests", "bin", "adb", "adb.exe");
var fbShim = Path.Combine(projectRoot, "csharp", "tests", "bin", "fastboot", "fastboot.exe");

if (!File.Exists(mockScript))
{
    Console.WriteLine($"找不到 mock 设备脚本（{string.Join(" 或 ", mockCandidates)}）");
    return 2;
}
if (!File.Exists(adbShim) || !File.Exists(fbShim))
{
    Console.WriteLine("找不到 mock adb/fastboot 壳。请先运行：powershell -File csharp\\build.ps1 -Tests");
    return 2;
}

void Check(bool condition, string label, string detail = "")
{
    checkCount++;
    if (condition)
    {
        Console.WriteLine($"  ok   {label}");
    }
    else
    {
        Console.WriteLine($"  FAIL {label} {detail}");
        failures.Add(label);
    }
}

string Md5(byte[] data)
{
    var hash = MD5.HashData(data);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

string Md5File(string path) => FileTools.Md5File(path);

string TmpRoot;
Dictionary<string, byte[]> SetupDevice(string root, string kernel = "5.10.198-android12-9-gabcdef")
{
    foreach (var path in new[] { "data/local/tmp", "proc", "dev/block/by-name" })
        Directory.CreateDirectory(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
    var partitions = new Dictionary<string, byte[]>
    {
        ["abl_a"] = Repeat(0xa1, 256 * 1024),
        ["abl_b"] = Repeat(0xb2, 256 * 1024),
        ["ocdt"] = Repeat(0xc3, 128 * 1024),
    };
    foreach (var (name, blob) in partitions)
        File.WriteAllBytes(Path.Combine(root, "dev", "block", "by-name", name), blob);

    var sizes = partitions.ToDictionary(p => $"/dev/block/by-name/{p.Key}", p => (long)p.Value.Length);
    File.WriteAllText(Path.Combine(root, "sizes.json"),
        JsonSerializer.Serialize(sizes, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(root, "proc", "modules"), "");
    File.WriteAllText(Path.Combine(root, "props.json"), JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["ro.product.model"] = "PFEM00",
        ["ro.product.brand"] = "OPPO",
        ["ro.build.version.release"] = "14",
        ["ro.build.display.id"] = "UKQ1.231003.002 release-keys",
        ["ro.boot.slot_suffix"] = "_b",
        ["ro.boot.serialno"] = "MOCKFX5P0001",
        ["sys.boot_completed"] = "1",
        ["ro.kernel.version"] = kernel,
        ["ro.boot.verifiedbootstate"] = "orange",
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    File.WriteAllText(Path.Combine(root, "state.json"), JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["rooted"] = false, ["kernelsu"] = false, ["fastboot"] = false, ["unlocked"] = false,
    }));
    return partitions;
}

static byte[] Repeat(byte value, int count)
{
    var data = new byte[count];
    Array.Fill(data, value);
    return data;
}

void SetKernel(string kver)
{
    var path = Path.Combine(TmpRoot, "props.json");
    var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
    data["ro.kernel.version"] = kver;
    File.WriteAllText(path, JsonSerializer.Serialize(data));
}

void SetDeviceState(bool? fastboot = null, bool? unlocked = null, bool? rooted = null, bool? kernelsu = null)
{
    var path = Path.Combine(TmpRoot, "state.json");
    var data = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path))!;
    if (fastboot is { } f) data["fastboot"] = f;
    if (unlocked is { } u) data["unlocked"] = u;
    if (rooted is { } r) data["rooted"] = r;
    if (kernelsu is { } k) data["kernelsu"] = k;
    File.WriteAllText(path, JsonSerializer.Serialize(data));
}

string BuildBundle(string parent)
{
    var bundle = Path.Combine(parent, "bundle");
    Directory.CreateDirectory(Path.Combine(bundle, "profiles"));
    (string key, string name)[] names =
    {
        ("exploit_guard", "exploit_guard (1)"),
        ("ksud", "ksud_v3.3.0-aarch64"),
        ("su0", "su0 - 副本"),
    };
    foreach (var (_, name) in names)
    {
        using var fh = File.OpenWrite(Path.Combine(bundle, name));
        fh.Write("\u007fELF"u8.ToArray());
        fh.Write(RandomNumberGenerator.GetBytes(64));
    }
    File.WriteAllText(Path.Combine(bundle, "profiles", "5.10.198-CN01.env"),
        "# 模拟偏移\nCHEESE_IDMAP_PG_DIR=0xffffff8008000000\nCHEESE_INIT_TASK=0xffffff8009000000\n");
    return bundle;
}

string BuildUnnamedBundle(string parent)
{
    var bundle = Path.Combine(parent, "bundle_unnamed");
    Directory.CreateDirectory(bundle);
    (string name, int size)[] files =
    {
        ("a.bin", 64 * 1024),
        ("b.bin", 256 * 1024),
        ("c.bin", 8 * 1024),
    };
    foreach (var (name, size) in files)
    {
        using var fh = File.OpenWrite(Path.Combine(bundle, name));
        fh.Write("\u007fELF"u8.ToArray());
        fh.Write(RandomNumberGenerator.GetBytes(size));
    }
    return bundle;
}

(string Abl, string Ocdt) BuildImages(string parent)
{
    var images = Path.Combine(parent, "images");
    Directory.CreateDirectory(images);
    var abl = Path.Combine(images, "123 abl.img");
    var ocdt = Path.Combine(images, "我的ocdt解锁.img");
    File.WriteAllBytes(abl, Repeat(0xd4, 200 * 1024));
    File.WriteAllBytes(ocdt, Repeat(0xe5, 96 * 1024));
    return (abl, ocdt);
}

(bool Ok, string Summary, List<TaskEvent> Events) RunStage(string sid, Dictionary<string, object?> parameters)
{
    var stage = Workflow.StageById[sid];
    var events = new List<TaskEvent>();
    var steps = stage.Build(parameters);
    var runner = TaskSync.RunSync(steps, parameters, events.Add);
    return (runner.Ok == true, runner.Summary, events);
}

string LogText(List<TaskEvent> events) =>
    string.Join(" ", events.Where(e => e.Kind == TaskEventKind.Log).Select(e => e.Text));

var tmp = Path.Combine(Path.GetTempPath(), "oppo_e2e_cs_" + DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(tmp);
TmpRoot = Path.Combine(tmp, "device");
var partitions = SetupDevice(TmpRoot);
var originals = partitions.ToDictionary(p => p.Key, p => Md5(p.Value));
var bundle = BuildBundle(tmp);
var (ablImg, ocdtImg) = BuildImages(tmp);

Environment.SetEnvironmentVariable("MOCK_DEVICE_ROOT", TmpRoot);
Environment.SetEnvironmentVariable("MOCK_DEVICE_PY", mockScript);
AppConfig.TestAppDir = tmp;
AppConfig.SetToolOverride("adb", adbShim);
AppConfig.SetToolOverride("fastboot", fbShim);
VioletGuard.OverrideForTests = true;
Directory.CreateDirectory(AppConfig.BackupDir());
Directory.CreateDirectory(AppConfig.LogDir());

var adb = AdbDefault.Instance;

Console.WriteLine("\n== 1 临时 root ==");
var temprootParams = new Dictionary<string, object?> { ["bundle"] = bundle, ["attempts"] = "2", ["allow_unsupported"] = false };
var (ok1, summary1, logs1) = RunStage("temproot", temprootParams);
Check(ok1, "临时 root 成功", summary1);
Check(adb.RootOk(), "root 通道 uid=0");
Check(StateStore.Load()["device"]?["model"]?.GetValue<string>() == "PFEM00", "账本记下型号");
Check(StateStore.Load()["device"]?["slot"]?.GetValue<string>() == "b", "识别活动槽 _b");

Console.WriteLine("\n== 1b 未适配内核必须被拦下 ==");
SetKernel("5.10.999-android12-9-gzzz");
var (ok1b, summary1b, _) = RunStage("temproot", temprootParams);
Check(!ok1b && summary1b.Contains("未适配"), "未适配内核被拦下", summary1b);

Console.WriteLine("\n== 1c 未适配内核 + 自带 profile：应该放行 ==");
File.WriteAllText(Path.Combine(bundle, "profiles", "5.10.999-自定义.env"), "CHEESE_IDMAP_PG_DIR=0xffffff8008000000\n");
var (ok1c, summary1c, logs1c) = RunStage("temproot", temprootParams);
Check(ok1c, "profile 文件名带内核版本即可放行", summary1c);
Check(LogText(logs1c).Contains("5.10.999"), "日志里说明了用的哪个 profile");

Console.WriteLine("\n== 1d 三个 ELF 全是无语义名字：按体积兜底 ==");
var (ok1d, summary1d, logs1d) = RunStage("temproot", new Dictionary<string, object?>
{
    ["bundle"] = BuildUnnamedBundle(tmp), ["attempts"] = "2", ["allow_unsupported"] = true,
});
Check(ok1d, "按体积推断出三个二进制并跑通", summary1d);
Check(LogText(logs1d).Contains("按体积推断"), "日志明确告警是推断出来的");
SetKernel("5.10.198-android12-9-gabcdef");

Console.WriteLine("\n== 2 推送解锁文件 ==");
var (ok2, summary2, _) = RunStage("push", new Dictionary<string, object?> { ["abl"] = ablImg, ["ocdt"] = ocdtImg });
Check(ok2, "两个镜像推送并核对大小", summary2);

Console.WriteLine("\n== 3 备份原分区 ==");
var (ok3, summary3, _) = RunStage("backup", new Dictionary<string, object?> { ["scope"] = new List<string> { "abl_a", "abl_b", "ocdt" }, ["slot"] = "auto" });
Check(ok3, "三个分区备份完成", summary3);
var backupDir = AppConfig.BackupDir();
var backupFiles = Directory.GetFiles(backupDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
Check(backupFiles.Count(f => f!.EndsWith(".img")) == 3, "备份文件数量为 3", string.Join(",", backupFiles));
foreach (var (name, blob) in partitions)
{
    var hit = backupFiles.FirstOrDefault(f => f!.StartsWith(name) && f.EndsWith(".img"));
    if (hit is null)
    {
        Check(false, $"找到 {name} 的备份");
        continue;
    }
    Check(Md5File(Path.Combine(backupDir, hit)) == originals[name], $"{name} 备份内容与原分区一致");
}

Console.WriteLine("\n== 3b 只勾选 ocdt：多选生效，不碰其它分区 ==");
var beforeOcdt = Directory.GetFiles(backupDir).Count(f => Path.GetFileName(f).StartsWith("ocdt"));
var (ok3b, summary3b, _) = RunStage("backup", new Dictionary<string, object?> { ["scope"] = new List<string> { "ocdt" }, ["slot"] = "auto" });
Check(ok3b, "只勾选 ocdt 也能备份", summary3b);
var afterOcdt = Directory.GetFiles(backupDir).Count(f => Path.GetFileName(f).StartsWith("ocdt"));
Check(afterOcdt == beforeOcdt + 1, "只多出一个 ocdt 备份", $"{beforeOcdt} -> {afterOcdt}");

Console.WriteLine("\n== 3c 一个都不勾：必须被拦下 ==");
var (ok3c, summary3c, _) = RunStage("backup", new Dictionary<string, object?> { ["scope"] = new List<string>(), ["slot"] = "auto" });
Check(!ok3c && summary3c.Contains("至少"), "空选择被拦下", summary3c);

Console.WriteLine("\n== 3d 账本指向最新一份时，界面点名的原厂备份必须优先 ==");
var stockAblB = Directory.GetFiles(backupDir)
    .Where(f => Path.GetFileName(f).StartsWith("abl_b"))
    .OrderBy(f => f, StringComparer.Ordinal)
    .First(f => Md5File(f) == originals["abl_b"]);
File.WriteAllBytes(Path.Combine(TmpRoot, "dev", "block", "by-name", "abl_b"), Repeat(0xaa, 256 * 1024));
var (ok3d0, summary3d0, logs3d0) = RunStage("backup", new Dictionary<string, object?>
{
    ["scope"] = new List<string> { "abl_b" }, ["slot"] = "auto",
});
Check(ok3d0, "被改过的分区仍能备份（记为来源未核对）", summary3d0);
Check(LogText(logs3d0).Contains("与账本里已有的原厂备份都不一致"), "日志提示来源未核对");
var ledgerAblB = StateStore.BackupPath("abl_b");
Check(Path.GetFileName(ledgerAblB) != Path.GetFileName(stockAblB), "（构造前提）账本现在指向另一份备份");
var (ok3d, summary3d, _) = RunStage("restore", new Dictionary<string, object?>
{
    ["confirm"] = "还原", ["slot"] = "b", ["backup_dir"] = "", ["abl_file"] = stockAblB, ["reboot"] = false,
});
Check(ok3d, "界面点名的原厂备份被采用（不再被账本覆盖）", summary3d);
Check(Md5(File.ReadAllBytes(Path.Combine(TmpRoot, "dev", "block", "by-name", "abl_b"))) == originals["abl_b"],
    "abl_b 已回到原厂（界面点名优先于账本）");
Check(StateStore.NewestStockBackup("abl_b") is not null, "账本里仍保留着原厂那份（来源未核对的不会顶掉它）");

Console.WriteLine("\n== 4a 没输入确认词必须被拦下 ==");
var (ok4a, summary4a, _) = RunStage("write", new Dictionary<string, object?>
{
    ["abl"] = ablImg, ["ocdt"] = ocdtImg, ["slot"] = "auto", ["confirm"] = "解锁吧",
});
Check(!ok4a && summary4a.Contains("解锁"), "确认词不对时拦下", summary4a);

Console.WriteLine("\n== 4 写入解锁文件（活动槽 _b）==");
var (ok4, summary4, _) = RunStage("write", new Dictionary<string, object?>
{
    ["abl"] = ablImg, ["ocdt"] = ocdtImg, ["slot"] = "auto", ["confirm"] = "解锁",
});
Check(ok4, "写入并读回校验通过", summary4);
var writtenAblB = File.ReadAllBytes(Path.Combine(TmpRoot, "dev", "block", "by-name", "abl_b"));
var imageBytes = File.ReadAllBytes(ablImg);
Check(writtenAblB.AsSpan(0, imageBytes.Length).SequenceEqual(imageBytes), "abl_b 前段已变成解锁镜像");
Check(Md5(File.ReadAllBytes(Path.Combine(TmpRoot, "dev", "block", "by-name", "abl_a"))) == originals["abl_a"],
    "另一槽 abl_a 未被误写");
Check(StateStore.Load()["partitions"]!["ocdt"]!["written"] is not null, "账本记为已写入");

Console.WriteLine("\n== 4b 写入解锁镜像之后再备份：必须被拦下 ==");
var (ok4b, summary4b, _) = RunStage("backup", new Dictionary<string, object?>
{
    ["scope"] = new List<string> { "abl_b" }, ["slot"] = "auto",
});
Check(!ok4b && summary4b.Contains("解锁镜像"), "备份到解锁镜像被拦下", summary4b);
var stockAfterWrite = StateStore.NewestStockBackup("abl_b");
Check(stockAfterWrite is not null &&
      Md5File(StateStore.RecordString(stockAfterWrite, "local")) == originals["abl_b"],
    "账本里可用的仍是原厂那份（没被解锁镜像顶掉）");

Console.WriteLine("\n== 4c 把解锁镜像当还原来源：必须被拦下 ==");
var (ok4c, summary4c, _) = RunStage("restore", new Dictionary<string, object?>
{
    ["confirm"] = "还原", ["slot"] = "b", ["backup_dir"] = "", ["abl_file"] = ablImg, ["reboot"] = false,
});
Check(!ok4c && summary4c.Contains("解锁镜像"), "用解锁镜像还原被拦下", summary4c);
Check(Md5(File.ReadAllBytes(Path.Combine(TmpRoot, "dev", "block", "by-name", "abl_b"))) != originals["abl_b"],
    "被拦下后分区没有被改写");

Console.WriteLine("\n== 5 前置：紫罗兰工具箱进程识别 ==");
Check(VioletGuard.IsViolet("SmartTool"), "识别 Release 释放的真实进程名 SmartTool");
Check(VioletGuard.IsViolet("VioletToolBox"), "识别 VioletToolBox 进程名");
Check(VioletGuard.IsViolet("VioletBox"), "识别 VioletBox 进程名");
Check(VioletGuard.IsViolet("紫罗兰工具箱"), "识别中文进程名");
Check(VioletGuard.IsViolet("MyVioletTool"), "名字里带 violet 也能识别");
Check(!VioletGuard.IsViolet("msedge"), "浏览器进程不误报");
Check(!VioletGuard.IsViolet(""), "空信息不误报");

Console.WriteLine("\n== 5 前置：紫罗兰工具箱没开必须被拦下 ==");
VioletGuard.OverrideForTests = false;
var (ok5g, summary5g, _) = RunStage("fastboot", new Dictionary<string, object?>());
Check(!ok5g && summary5g.Contains("紫罗兰"), "缺少紫罗兰工具箱时拦下解锁", summary5g);
VioletGuard.OverrideForTests = true;

Console.WriteLine("\n== 5 fastboot 解锁 ==");
var (ok5, summary5, _) = RunStage("fastboot", new Dictionary<string, object?> { ["ack"] = true });
Check(ok5, "解锁指令完成并重启", summary5);
Check(!File.Exists(Path.Combine(TmpRoot, "data", "local", "tmp", "ksud")),
    "解锁后 /data/local/tmp 被清空（模拟清数据）");

Console.WriteLine("\n== 5b 手机已经停在 fastboot：跳过重启直接解锁 ==");
SetDeviceState(fastboot: true, unlocked: false);
var (ok5b, summary5b, logs5b) = RunStage("fastboot", new Dictionary<string, object?> { ["ack"] = true });
Check(ok5b, "已在 fastboot 时无需重启", summary5b);
Check(LogText(logs5b).Contains("跳过重启"), "日志说明跳过了重启");

Console.WriteLine("\n== 6 验证解锁状态 ==");
var (ok6, summary6, _) = RunStage("verify", new Dictionary<string, object?>());
Check(ok6, "ro.boot.flash.locked=0", summary6);

Console.WriteLine("\n== 7a 解锁后没有 root，还原必须被拦下 ==");
var (ok7a, summary7a, _) = RunStage("restore", new Dictionary<string, object?>
{
    ["confirm"] = "还原", ["slot"] = "auto", ["reboot"] = false,
});
Check(!ok7a && summary7a.Contains("uid=0"), "缺 root 时拦下还原", summary7a);

Console.WriteLine("\n== 7b 重做临时 root 后还原 ==");
var (ok7b, summary7b, _) = RunStage("temproot", temprootParams);
Check(ok7b, "重新拿到临时 root", summary7b);
var (ok7b2, summary7b2, _) = RunStage("restore", new Dictionary<string, object?>
{
    ["confirm"] = "还原", ["slot"] = "auto", ["reboot"] = false,
});
Check(ok7b2, "还原完成", summary7b2);
foreach (var name in new[] { "abl_b", "ocdt" })
{
    Check(Md5(File.ReadAllBytes(Path.Combine(TmpRoot, "dev", "block", "by-name", name))) == originals[name],
        $"{name} 已回到原厂内容");
}
Check(StateStore.Load()["partitions"]!["abl_b"]!["restored"] is not null, "账本记为已还原");

Console.WriteLine("\n== 7b2 账本备份来自别的设备：必须被拦下 ==");
var propsPath = Path.Combine(TmpRoot, "props.json");
var propsRaw = File.ReadAllText(propsPath);
var props = JsonSerializer.Deserialize<Dictionary<string, string>>(propsRaw)!;
props["ro.boot.serialno"] = "MOCKOTHER0002";
File.WriteAllText(propsPath, JsonSerializer.Serialize(props));
var (ok7b3, summary7b3, _) = RunStage("restore", new Dictionary<string, object?>
{
    ["confirm"] = "还原", ["slot"] = "auto", ["reboot"] = false,
});
Check(!ok7b3 && summary7b3.Contains("另一台设备"), "别的设备的备份被拦下", summary7b3);
File.WriteAllText(propsPath, propsRaw);

Console.WriteLine("\n== 7c 备份文件被改名 + 账本清空：仍要能还原 ==");
var (ok7c0, summary7c0, _) = RunStage("write", new Dictionary<string, object?>
{
    ["abl"] = ablImg, ["ocdt"] = ocdtImg, ["slot"] = "auto", ["confirm"] = "解锁",
});
Check(ok7c0, "先写回解锁镜像", summary7c0);
var messy = Path.Combine(tmp, "改过名的备份");
Directory.CreateDirectory(messy);
var srcAbl = Directory.GetFiles(backupDir)
    .First(f => Path.GetFileName(f).StartsWith("abl_b") && Md5File(f) == originals["abl_b"]);
var srcOcdt = Directory.GetFiles(backupDir)
    .First(f => Path.GetFileName(f).StartsWith("ocdt") && Md5File(f) == originals["ocdt"]);
var renamedAbl = Path.Combine(messy, "我的B槽备份.dat");
var renamedOcdt = Path.Combine(messy, "备份2.dat");
File.Copy(Path.Combine(backupDir, srcAbl!), renamedAbl, overwrite: true);
File.Copy(Path.Combine(backupDir, srcOcdt!), renamedOcdt, overwrite: true);
StateStore.Reset();
Check(!StateStore.HasBackup("abl_b"), "账本已清空");
var (ok7c, summary7c, logs7c) = RunStage("restore", new Dictionary<string, object?>
{
    ["confirm"] = "还原", ["slot"] = "b", ["backup_dir"] = messy,
    ["abl_file"] = renamedAbl,
    ["ocdt_file"] = "",
    ["reboot"] = false,
});
Check(ok7c, "改名 + 清账本后仍能还原", summary7c);
if (!ok7c)
{
    foreach (var evt in logs7c.Where(e => e.Kind == TaskEventKind.Log && e.Level is "note" or "warn" or "err"))
        Console.WriteLine($"      | {evt.Text}");
}
Check(LogText(logs7c).Contains("体积与设备分区一致"), "ocdt 是靠体积认出来的");
foreach (var name in new[] { "abl_b", "ocdt" })
{
    Check(Md5(File.ReadAllBytes(Path.Combine(TmpRoot, "dev", "block", "by-name", name))) == originals[name],
        $"{name} 回到原厂（改名备份）");
}

Console.WriteLine("\n== 结构检查 ==");
Check(Workflow.Stages.Length == 7, "七个阶段");
Check(Workflow.Stages.Select(s => s.No).SequenceEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }), "阶段编号连续");
Check(Workflow.Stages.Select(s => s.Sid).SequenceEqual(
        new[] { "temproot", "push", "backup", "write", "fastboot", "verify", "restore" }),
    "阶段顺序正确");
foreach (var stage in Workflow.Stages)
{
    try
    {
        var lines = stage.Preview?.Invoke(new Dictionary<string, object?>()) ?? Array.Empty<string>();
        var steps = stage.Build(new Dictionary<string, object?>());
        Check(lines.Length > 0, $"阶段 {stage.Sid} 有命令预览");
        Check(steps.Count >= 2, $"阶段 {stage.Sid} 步骤数合理");
    }
    catch (Exception exc)
    {
        Check(false, $"阶段 {stage.Sid} 能构建", exc.Message);
    }
}

Console.WriteLine("\n== 结果 ==");
Console.WriteLine($"检查项 {checkCount}，失败 {failures.Count}");
foreach (var name in failures)
    Console.WriteLine($"  失败：{name}");
try { Directory.Delete(tmp, recursive: true); } catch {   }
return failures.Count == 0 ? 0 : 1;
