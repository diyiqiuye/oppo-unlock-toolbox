

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OppoUnlockToolbox.Core;

public sealed class ToolMissingException : Exception
{
    public ToolMissingException(string message) : base(message) { }
}

public sealed partial class Adb
{
    [GeneratedRegex(@"^\[([^\]]+)\]:\s*\[(.*)\]\s*$")]
    private static partial Regex PropRegex();

    public string Serial { get; set; } = "";
    public string FastbootSerial { get; set; } = "";

    private static readonly object SerialLock = new();

    private string Require(string name)
    {
        var path = AppConfig.FindTool(name);
        if (string.IsNullOrEmpty(path))
        {
            throw new ToolMissingException(
                $"未找到 {name}。请把 platform-tools 放到工具箱的 tools/platform-tools 目录，" +
                "或安装 Android SDK 后重试。");
        }
        return path;
    }

    public List<object> AdbArgv(params object[] args)
    {
        var argv = new List<object> { Require("adb") };
        if (Serial.Length > 0)
            argv.AddRange(new object[] { "-s", Serial });
        argv.AddRange(args);
        return argv;
    }

    public List<object> FbArgv(params object[] args)
    {
        var argv = new List<object> { Require("fastboot") };
        if (FastbootSerial.Length > 0)
            argv.AddRange(new object[] { "-s", FastbootSerial });
        argv.AddRange(args);
        return argv;
    }

    public RunResult RunAdb(IEnumerable<object> args, RunOptions? options = null) =>
        ProcessRunner.Run(AdbArgv(args.ToArray()), options);

    public RunResult RunFb(IEnumerable<object> args, RunOptions? options = null) =>
        ProcessRunner.Run(FbArgv(args.ToArray()), options);

    public List<Dictionary<string, string>> Devices()
    {
        var res = RunAdb(new object[] { "devices", "-l" }, new RunOptions { TimeoutMs = 30000 });
        var found = new List<Dictionary<string, string>>();
        var lines = res.Lines();
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("*") || line.Trim().Length == 0)
                continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            var entry = new Dictionary<string, string>
            {
                ["serial"] = parts[0],
                ["state"] = parts[1],
                ["model"] = "",
                ["product"] = "",
            };
            foreach (var token in parts.Skip(2))
            {
                if (token.StartsWith("model:", StringComparison.Ordinal))
                    entry["model"] = token.Split(':', 2)[1];
                else if (token.StartsWith("product:", StringComparison.Ordinal))
                    entry["product"] = token.Split(':', 2)[1];
            }
            found.Add(entry);
        }
        return found;
    }

    public string DeviceState()
    {
        List<Dictionary<string, string>> devs;
        try
        {
            devs = Devices();
        }
        catch (ToolMissingException)
        {
            return "none";
        }
        foreach (var entry in devs)
        {
            if (Serial.Length > 0 && entry["serial"] == Serial)
                return entry["state"];
            if (Serial.Length == 0 && entry["state"] == "device")
            {
                Serial = entry["serial"];
                return "device";
            }
        }
        if (devs.Count > 0)
            return devs[0]["state"];
        var fb = FastbootDevices();
        if (fb.Count > 0)
        {
            FastbootSerial = fb[0];
            return "fastboot";
        }
        return "none";
    }

    public List<string> FastbootDevices()
    {
        List<string> serials;
        try
        {
            var res = RunFb(new object[] { "devices" }, new RunOptions { TimeoutMs = 30000 });
            serials = new List<string>();
            foreach (var line in res.Lines())
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && parts[0] != "<")
                    serials.Add(parts[0]);
            }
        }
        catch (ToolMissingException)
        {
            return new List<string>();
        }
        return serials;
    }

    public bool WaitForDevice(int timeoutMs = 180000, CancellationToken cancel = default,
        Action<string>? onLine = null)
    {
        var res = RunAdb(new object[] { "wait-for-device" }, new RunOptions
        {
            TimeoutMs = timeoutMs,
            Cancel = cancel,
            OnLine = onLine,
        });
        return res.Ok;
    }

    public bool WaitForBoot(int timeoutMs = 300000, CancellationToken cancel = default,
        Action<string>? onLine = null, int pollMs = 6000)
    {
        if (!WaitForDevice(timeoutMs, cancel))
            return false;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (cancel.IsCancellationRequested)
                return false;
            var value = ShellText("getprop sys.boot_completed", root: false, timeoutMs: 20000).Trim();
            if (value == "1")
                return true;
            onLine?.Invoke("等待系统启动完成…");
            CancellableSleep(pollMs, cancel);
        }
        return false;
    }

    public bool WaitForFastboot(int timeoutMs = 240000, CancellationToken cancel = default,
        Action<string>? onLine = null, int pollMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (cancel.IsCancellationRequested)
                return false;
            var serials = FastbootDevices();
            if (serials.Count > 0)
            {
                if (FastbootSerial.Length == 0)
                    FastbootSerial = serials[0];
                return true;
            }
            onLine?.Invoke("等待设备进入 fastboot…");
            CancellableSleep(pollMs, cancel);
        }
        return false;
    }

    internal static void CancellableSleep(int ms, CancellationToken cancel)
    {
        try
        {
            Task.Delay(ms, cancel).Wait(cancel);
        }
        catch (OperationCanceledException)
        {

        }
    }

    public RunResult Shell(string cmd, bool root = false, int timeoutMs = 120000,
        Action<string>? onLine = null, CancellationToken cancel = default, bool allowFail = false)
    {
        string remote;
        if (root)
        {
            if (cmd.Contains('\''))
            {
                return new RunResult(new[] { "<root shell>" }, -1,
                    error: $"root 命令中不允许出现单引号：{cmd}");
            }
            remote = $"echo '{cmd}' | {AppConfig.Ksud} debug su";
        }
        else
        {
            remote = cmd;
        }
        var res = RunAdb(new object[] { "shell", remote }, new RunOptions
        {
            TimeoutMs = timeoutMs,
            OnLine = onLine,
            Cancel = cancel,
        });
        if (!allowFail && !res.Ok && res.Error.Length == 0)
            res.Error = $"远端命令退出码 {res.Code}";
        return res;
    }

    public string ShellText(string cmd, bool root = false, int timeoutMs = 60000,
        CancellationToken cancel = default)
    {
        var res = Shell(cmd, root: root, timeoutMs: timeoutMs, cancel: cancel, allowFail: true);
        return res.Out.Replace("\r", "").Trim();
    }

    public bool RootOk(int timeoutMs = 60000, CancellationToken cancel = default)
    {
        var text = ShellText("id", root: true, timeoutMs: timeoutMs, cancel: cancel);
        return text.StartsWith("uid=0", StringComparison.Ordinal);
    }

    public string RootUid(int timeoutMs = 60000, CancellationToken cancel = default)
    {
        var text = ShellText("id", root: true, timeoutMs: timeoutMs, cancel: cancel);
        foreach (var line in text.Split('\n'))
            if (line.Trim().Length > 0)
                return line.Trim();
        return "";
    }

    public Dictionary<string, string> Props(int timeoutMs = 60000)
    {
        var text = ShellText("getprop", timeoutMs: timeoutMs);
        var table = new Dictionary<string, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var match = PropRegex().Match(rawLine.Trim());
            if (match.Success)
                table[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return table;
    }

    public string Prop(string name, int timeoutMs = 30000) =>
        ShellText($"getprop {name}", timeoutMs: timeoutMs);

    public string KernelVersion(int timeoutMs = 30000)
    {
        var raw = ShellText("uname -r", timeoutMs: timeoutMs);
        var first = raw.Length > 0 ? raw.Split('\n')[0].Trim() : "";
        return first.Length > 0 ? first.Split('-')[0] : "";
    }

    public string KernelRaw(int timeoutMs = 30000)
    {
        var raw = ShellText("uname -r", timeoutMs: timeoutMs);
        return raw.Length > 0 ? raw.Split('\n')[0].Trim() : "";
    }

    public string ActiveSlot(int timeoutMs = 30000)
    {
        foreach (var name in new[] { "ro.boot.slot_suffix", "ro.boot.slot" })
        {
            var value = Prop(name, timeoutMs).Trim();
            if (value is "_a" or "a")
                return "a";
            if (value is "_b" or "b")
                return "b";
        }
        return "";
    }

    public int? FlashLocked(int timeoutMs = 30000)
    {
        var value = Prop("ro.boot.flash.locked", timeoutMs).Trim();
        return value switch
        {
            "0" => 0,
            "1" => 1,
            _ => null,
        };
    }

    public RunResult Push(string local, string remote, int timeoutMs = 600000,
        Action<string>? onLine = null, CancellationToken cancel = default) =>
        RunAdb(new object[] { "push", local, remote }, new RunOptions
        {
            TimeoutMs = timeoutMs,
            OnLine = onLine,
            Cancel = cancel,
        });

    public RunResult Pull(string remote, string local, int timeoutMs = 900000,
        Action<string>? onLine = null, CancellationToken cancel = default) =>
        RunAdb(new object[] { "pull", remote, local }, new RunOptions
        {
            TimeoutMs = timeoutMs,
            OnLine = onLine,
            Cancel = cancel,
        });

    public long? RemoteSize(string path, bool root = false, int timeoutMs = 60000,
        CancellationToken cancel = default)
    {
        var text = ShellText($"wc -c < {path}", root: root, timeoutMs: timeoutMs, cancel: cancel);
        var firstLine = text.Length > 0 ? text.Split('\n')[0] : "";
        var digits = DigitsRegex().Replace(firstLine, "");
        return digits.Length > 0 && long.TryParse(digits, out var value) ? value : null;
    }

    public string? RemoteMd5(string path, bool root = false, int timeoutMs = 300000,
        CancellationToken cancel = default)
    {
        var text = ShellText($"md5sum {path}", root: root, timeoutMs: timeoutMs, cancel: cancel);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Length == 32 && HexRegex().IsMatch(parts[0]))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public string LocalMd5(string path, CancellationToken cancel = default) =>
        FileTools.Md5File(path, cancel);

    public long? PartitionSize(string part, int timeoutMs = 60000, CancellationToken cancel = default)
    {
        var dev = AppConfig.BlockByName + part;
        var text = ShellText($"blockdev --getsize64 {dev}", root: true, timeoutMs: timeoutMs, cancel: cancel);
        var firstLine = text.Length > 0 ? text.Split('\n')[0] : "";
        var digits = DigitsRegex().Replace(firstLine, "");
        if (digits.Length > 0 && long.TryParse(digits, out var value))
            return value;
        return RemoteSize(dev, root: true, timeoutMs: timeoutMs, cancel: cancel);
    }

    public string? PartitionHeadMd5(string part, long size, int timeoutMs = 900000,
        CancellationToken cancel = default)
    {
        if (size <= 0)
            return null;
        var dev = AppConfig.BlockByName + part;
        var text = ShellText($"head -c {size} {dev} | md5sum", root: true,
            timeoutMs: timeoutMs, cancel: cancel);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Length == 32 && HexRegex().IsMatch(parts[0]))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    public bool ModuleLoaded(int timeoutMs = 30000, CancellationToken cancel = default)
    {
        var text = ShellText("grep -c '^kernelsu' /proc/modules", timeoutMs: timeoutMs, cancel: cancel);
        if (text.Trim().Length == 0)
            return false;
        var first = text.Trim().Split('\n').FirstOrDefault() ?? "";
        return first == "1";
    }

    public string KernelsuLine(int timeoutMs = 30000) =>
        ShellText("grep kernelsu /proc/modules", timeoutMs: timeoutMs).Trim();

    public RunResult Reboot(string target = "", int timeoutMs = 60000) =>
        target.Length > 0
            ? RunAdb(new object[] { "reboot", target }, new RunOptions { TimeoutMs = timeoutMs })
            : RunAdb(new object[] { "reboot" }, new RunOptions { TimeoutMs = timeoutMs });

    public RunResult FastbootReboot(int timeoutMs = 120000) =>
        RunFb(new object[] { "reboot" }, new RunOptions { TimeoutMs = timeoutMs });

    public bool EnsureRemoteDir(string remote, CancellationToken cancel = default)
    {
        var res = Shell($"mkdir -p {remote}", timeoutMs: 30000, cancel: cancel, allowFail: true);
        return res.Ok;
    }

    public void SetSerial(string serial)
    {
        lock (SerialLock)
        {
            Serial = serial;
        }
    }

    public string DescribeLocalSize(string path) =>
        File.Exists(path) ? FileTools.FmtSize(FileTools.FileSize(path)) : "—";

    [GeneratedRegex(@"[^0-9]")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HexRegex();
}

public static class AdbDefault
{
    public static readonly Adb Instance = new();
}
