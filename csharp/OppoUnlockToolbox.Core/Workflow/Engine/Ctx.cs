// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
using System.Text;

namespace OppoUnlockToolbox.Core.Workflow;

public sealed class Ctx
{
    private readonly Action<TaskEvent> _emit;

    public CancellationTokenSource CancelSource { get; }
    public CancellationToken Cancel => CancelSource.Token;
    public Dictionary<string, object?> Params { get; }
    public Dictionary<string, object?> Extras { get; } = new();

    public Ctx(Action<TaskEvent> emit, CancellationTokenSource cancelSource,
        Dictionary<string, object?>? parameters = null)
    {
        _emit = emit;
        CancelSource = cancelSource;
        Params = parameters ?? new Dictionary<string, object?>();
    }

    public void Log(string text, string level = "info") =>
        _emit(TaskEvent.LogE(level, text));

    public void Status(string text) => _emit(TaskEvent.StatusE(text));

    public string Param(string key, string defaultValue = "")
    {
        if (!Params.TryGetValue(key, out var value) || value is null)
            return defaultValue;
        var text = value.ToString() ?? "";
        return text.Length == 0 ? defaultValue : text;
    }

    public bool Flag(string key, bool defaultValue = false)
    {
        if (!Params.TryGetValue(key, out var value) || value is null)
            return defaultValue;
        if (value is bool b)
            return b;
        if (value is string s)
            return s.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
        return Convert.ToBoolean(value);
    }

    public void CheckCancel()
    {
        if (CancelSource.IsCancellationRequested)
            throw new CancelledException();
    }

    public RunResult Run(IEnumerable<object?> argv, string label = "", int timeoutMs = 180000,
        bool allowFail = false, bool stream = true, string? logfile = null, bool closeStdin = false)
    {
        CheckCancel();
        var argvList = argv.Select(a => a?.ToString() ?? "").ToList();
        if (label.Length == 0)
            label = argvList[0];
        Log("$ " + Cmd.QuoteArgv(argvList), "cmd");

        var collected = new List<string>();
        void OnLine(string line)
        {
            lock (collected) collected.Add(line);
            if (stream && line.Trim().Length > 0)
                Log(line, "out");
        }

        var started = DateTime.UtcNow;
        var res = ProcessRunner.Run(argvList, new RunOptions
        {
            TimeoutMs = timeoutMs,
            OnLine = OnLine,
            Cancel = CancelSource.Token,
            InputText = closeStdin ? string.Empty : null,
        });
        var cost = (DateTime.UtcNow - started).TotalSeconds;
        if (cost > 2)
            Log($"   ↑ 用时 {cost:0.0}s", "note");

        if (logfile != null)
            AppendLog(logfile, res.Argv, collected);

        if (res.TimedOut)
        {
            var message = $"{label} 超时（{timeoutMs / 1000.0:0.#}s）";
            Log(message, "warn");
            if (!allowFail)
                throw new StepFailException(message);
        }
        else if (!res.Ok)
        {
            var message = res.Error.Length > 0 ? res.Error : $"{label} 退出码 {res.Code}";
            Log($"{label}：{message}", "warn");
            if (!allowFail)
                throw new StepFailException($"{label} 失败（{message}）");
        }
        return res;
    }

    public RunResult Shell(Adb adb, string cmd, bool root = false, int timeoutMs = 180000,
        bool allowFail = false, bool stream = true, string? logfile = null)
    {
        if (!root)
        {
            return Run(adb.AdbArgv("shell", cmd), label: "adb shell", timeoutMs: timeoutMs,
                allowFail: allowFail, stream: stream, logfile: logfile);
        }
        CheckCancel();
        Log("$ [root] " + cmd, "cmd");
        var collected = new List<string>();
        void OnLine(string line)
        {
            lock (collected) collected.Add(line);
            if (stream && line.Trim().Length > 0)
                Log(line, "out");
        }
        var res = adb.Shell(cmd, root: true, timeoutMs: timeoutMs, onLine: OnLine,
            cancel: CancelSource.Token, allowFail: true);
        if (logfile != null)
            AppendLog(logfile, new[] { "adb shell (root)", cmd }, collected);
        if (res.TimedOut)
        {
            var message = $"root 命令超时（{timeoutMs / 1000.0:0.#}s）";
            Log(message, "warn");
            if (!allowFail)
                throw new StepFailException(message);
        }
        else if (!res.Ok)
        {
            var message = res.Error.Length > 0 ? res.Error : $"root 命令退出码 {res.Code}";
            Log(message, "warn");
            if (!stream && res.Error.Length == 0)
            {
                // stream: false 时回显被收走了却没打出来，失败只剩一个退出码 —— 补上尾巴
                lock (collected)
                {
                    var tail = collected.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(6).ToList();
                    if (tail.Count > 0)
                        Log("  命令回显：" + string.Join(" ｜ ", tail), "warn");
                }
            }
            if (!allowFail)
                throw new StepFailException(message);
        }
        return res;
    }

    private static void AppendLog(string path, IReadOnlyList<string> header, List<string> lines)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("\n===== ").Append(string.Join(" ", header)).Append(" =====\n");
            lock (lines)
                sb.Append(string.Join("\n", lines)).Append('\n');
            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {

        }
    }
}
