// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using static OppoUnlockToolbox.Core.Workflow.Stages.TemprootStage;
using static OppoUnlockToolbox.Core.Workflow.Stages.PushStage;
using static OppoUnlockToolbox.Core.Workflow.Stages.BackupStage;
using static OppoUnlockToolbox.Core.Workflow.Stages.WriteStage;
using static OppoUnlockToolbox.Core.Workflow.Stages.FastbootStage;
using static OppoUnlockToolbox.Core.Workflow.Stages.VerifyStage;
using static OppoUnlockToolbox.Core.Workflow.Stages.RestoreStage;

namespace OppoUnlockToolbox.Core.Workflow;

public static class StageRegistry
{
    public static readonly Stage[] Stages =
    {
        new("temproot", 1, "临时 Root",
            new Field[]
            {
                new("method", "提权方式", "choice",
                    "GPU：现有链路（CVE-2025-21479），需要 bundle 目录，走内核偏移注入。" +
                    "DFRoot：CVE-2026-43284（DirtyFrag），支持最新版系统；工具箱只负责把 KernelSU " +
                    "管理器和 DFRoot 装到手机上，装完打开 DFRoot 手动点一下。",
                    defaultValue: "gpu",
                    choices: new[]
                    {
                        ("gpu", "GPU · CVE-2025-21479（c14系统）"),
                        ("dfroot", "DFRoot · CVE-2026-43284（c16系统）"),
                    }),
                new("dfroot_hint", "在手机上点 Launch Root", "note",
                    when: vars => IsDfroot(vars)),
                new("bundle", "bundle 目录", "dir",
                    "解压后的 Release bundle；本机文件名可任意（后缀 / 副本 / 版本号 / 空格都行）。本机不需要 Git Bash —— 一键 sh 的逻辑已经内置。",
                    when: vars => !IsDfroot(vars)),
                new("attempts", "最多尝试次数", "int", "默认 4 次；exploit 触发重启后会自动等开机重试。",
                    defaultValue: ExploitProfiles.MaxAttempts.ToString(), width: 6,
                    when: vars => !IsDfroot(vars)),
                new("variant", "提权变体", "choice",
                    "caps-only：把权限写进当前 shell 用户共享的 cred（更直接）；init_cred：用 commit_creds 换一份私有 cred（对其它进程更干净）。默认 caps-only，跑不通再换 init_cred。",
                    defaultValue: "capsonly",
                    choices: new[]
                    {
                        ("capsonly", "caps-only（推荐）"),
                        ("initcred", "init_cred"),
                    },
                    when: vars => !IsDfroot(vars)),
                new("allow_unsupported", "内核未适配也强制继续", "bool",
                    "仅在你自己算过 profiles/<内核版本>.env 时勾选，否则会写错内核地址。",
                    when: vars => !IsDfroot(vars)),
            },
            danger: "exploit 直接写内核内存，失败会触发重启；请保持屏幕常亮、别拔线。",
            preview: PreviewTemproot2,
            build: p => TemprootMethod(p) == "dfroot"
                ? new List<Step> { D1Launch(), D2EnsureKsud(), D3WaitRoot(), D4Record() }
                : new List<Step> { S3Kernel(), S3Bundle(), S3Push(), S3Profile(), S3Exploit(), S3Verify(), S3Record() },
            needsRoot: false),
        new("push", 2, "推送解锁文件",
            new Field[]
            {
                new("abl", "abl 镜像", "file", "文件名不限：123abl.img、abl (1).img 都能用。"),
                new("ocdt", "ocdt 镜像", "file", "同上，认内容不认名字。"),
            },
            preview: PreviewPush,
            build: _ => new List<Step> { S4Check(), S4Push() }),
        new("backup", 3, "备份原分区",
            new Field[]
            {
                new("scope", "备份分区", "multi", "",
                    defaultValue: new[] { "abl_a", "abl_b", "ocdt" },
                    choices: new[] { ("abl_a", "abl_a"), ("abl_b", "abl_b"), ("ocdt", "ocdt") }),
                new("slot", "槽位", "choice", "默认按 ro.boot.slot_suffix 自动判断。",
                    defaultValue: "auto", choices: new[] { ("auto", "自动检测"), ("a", "A 槽"), ("b", "B 槽") }),
            },
            danger: "没有完成这一步就不要执行『写入解锁文件』。" +
                    "注意：『写入解锁文件』之后再回来备份，备份到的是解锁镜像而不是原厂内容，会被直接拦下。",
            preview: PreviewBackup,
            build: _ => new List<Step> { S5Root(), S5Dump(), S5Pull(), S5Manifest() },
            needsRoot: true),
        new("write", 4, "写入解锁文件",
            new Field[]
            {
                new("abl", "abl 镜像", "file", "与『推送解锁文件』里选的同一个文件。"),
                new("ocdt", "ocdt 镜像", "file", "与『推送解锁文件』里选的同一个文件。"),
                new("slot", "写入槽位", "choice", "必须与当前活动槽一致，写错槽位等于没解锁。",
                    defaultValue: "auto", choices: new[] { ("auto", "自动检测"), ("a", "A 槽"), ("b", "B 槽") }),
                new("confirm", "输入『解锁』确认", "text", "这一步会改写引导分区，必须手动输入『解锁』两个字。"),
                new("force", "跳过备份检查", "bool", "只在你自己另外备份过 abl / ocdt 时勾选。"),
            },
            danger: "写错分区可能变砖。请先完成『备份原分区』。",
            preview: PreviewWrite,
            build: _ => new List<Step> { S6Gate(), S6Push(), S6Ocdt(), S6Abl(), S6Record() },
            needsRoot: true),
        new("fastboot", 5, "fastboot 解锁",
            danger: "锁一旦解开，手机数据会被全部清除，先确认要留的东西都备份好了。\n" +
                    "前置要求：必须先在电脑上打开紫罗兰工具箱（VioletToolBox）、连接手机并保持在后台运行，否则本阶段会直接停止。\n" +
                    "手机进入工厂 fastboot 菜单后：选中 UNLOCK THE BOOTLOADER → 再按确认键（各机型按键不同，以屏幕提示为准）。\n" +
                    "注意 1：写入解锁文件之后，手机的触控和 WiFi 会失灵，这是正常现象，做完『还原原分区』就恢复正常。\n" +
                    "注意 2：这段时间要操作手机，建议用紫罗兰工具箱（VioletToolBox）投屏操作。\n" +
                    "注意 3：如果解锁指令写不进去，先开机进开发者选项打开『OEM 解锁』再重跑本阶段。",
            preview: PreviewUnlock,
            build: _ => new List<Step> { S7VioletGate(), S7Reboot(), S7Ack(), S7Unlock(), S7Wait() }),
        new("verify", 6, "验证解锁状态",
            preview: PreviewVerify,
            build: _ => new List<Step> { S8Check(), S8Record() }),
        new("restore", 7, "还原原分区",
            new Field[]
            {
                new("backup_dir", "备份目录（可选）", "dir",
                    "点名的文件优先，其次在这个文件夹里按名字或体积自动认，最后才用账本里的原厂备份。"),
                new("abl_file", "abl 备份文件（可选）", "file", "直接点名 abl 的备份，优先级最高；留空则自动找。"),
                new("ocdt_file", "ocdt 备份文件（可选）", "file", "直接点名 ocdt 的备份，优先级最高；留空则自动找。"),
                new("slot", "槽位", "choice", "要与解锁时写入的槽位一致。", defaultValue: "auto",
                    choices: new[] { ("auto", "自动检测"), ("a", "A 槽"), ("b", "B 槽") }),
                new("confirm", "输入『还原』确认", "text", "写回引导分区同样是危险操作。"),
                new("reboot", "完成后自动重启", "bool", "还原校验通过后重启手机。", defaultValue: true),
            },
            danger: "需要临时 root；没有 root 时会被拦下。" +
                    "还原来源按『界面点名 > 备份目录 > 账本原厂备份』顺序取；选到解锁镜像或别的设备的备份会被直接拦下。",
            preview: PreviewRestore,
            build: _ => new List<Step> { S9Gate(), S9Push(), S9Restore(), S9Reboot() },
            needsRoot: true),
    };

    public static readonly IReadOnlyDictionary<string, Stage> StageById =
        Stages.ToDictionary(stage => stage.Sid);

    public static int StageIndex(string sid)
    {
        for (var index = 0; index < Stages.Length; index++)
        {
            if (Stages[index].Sid == sid)
                return index;
        }
        return 0;
    }

    public static string CurrentSlotLabel()
    {
        var data = StateStore.Load();
        var slot = data["device"]?["slot"]?.GetValue<string>() ?? "";
        return slot.Length > 0 ? "_" + slot : "—";
    }
}
