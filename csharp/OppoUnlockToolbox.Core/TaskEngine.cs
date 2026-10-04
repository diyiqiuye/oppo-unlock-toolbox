

using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace OppoUnlockToolbox.Core;

public enum TaskEventKind
{
    Log,
    Status,
    StepStart,
    StepOk,
    StepFail,
    Progress,
    Done,
}

public sealed class TaskEvent
{
    public TaskEventKind Kind { get; init; }
    public string Level { get; init; } = "info";
    public string Text { get; init; } = "";
    public int Index { get; init; }
    public double Fraction { get; init; }
    public bool Ok { get; init; }
    public string Summary { get; init; } = "";

    public static TaskEvent LogE(string level, string text) => new() { Kind = TaskEventKind.Log, Level = level, Text = text };
    public static TaskEvent StatusE(string text) => new() { Kind = TaskEventKind.Status, Text = text };
    public static TaskEvent StepStartE(int index, string title) => new() { Kind = TaskEventKind.StepStart, Index = index, Text = title };
    public static TaskEvent StepOkE(int index, string message) => new() { Kind = TaskEventKind.StepOk, Index = index, Text = message };
    public static TaskEvent StepFailE(int index, string message) => new() { Kind = TaskEventKind.StepFail, Index = index, Text = message };
    public static TaskEvent ProgressE(double fraction) => new() { Kind = TaskEventKind.Progress, Fraction = fraction };
    public static TaskEvent DoneE(bool ok, string summary) => new() { Kind = TaskEventKind.Done, Ok = ok, Summary = summary };
}

public sealed class StepFailException : Exception
{
    public StepFailException(string message) : base(message) { }
}

public sealed class CancelledException : Exception
{
    public CancelledException() : base("已中止") { }
}

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

public sealed class Step
{
    public string Title { get; }
    public Func<Ctx, string?> Fn { get; }

    public Step(string title, Func<Ctx, string?> fn)
    {
        Title = title;
        Fn = fn;
    }
}

public sealed class TaskRunner
{
    private readonly List<Step> _steps;
    private readonly ConcurrentQueue<TaskEvent> _queue = new();
    private readonly CancellationTokenSource _cancelSource = new();

    public bool? Ok { get; private set; }
    public string Summary { get; private set; } = "";
    public Task? Background { get; private set; }

    public TaskRunner(IEnumerable<Step> steps, Dictionary<string, object?>? parameters = null)
    {
        _steps = steps.ToList();
        Params = parameters ?? new Dictionary<string, object?>();
    }

    public Dictionary<string, object?> Params { get; }
    public bool IsAlive => Background is { IsCompleted: false };
    public CancellationToken Cancel => _cancelSource.Token;

    public Action<TaskEvent>? OnEvent { get; set; }

    public void Emit(TaskEvent evt)
    {
        _queue.Enqueue(evt);
        OnEvent?.Invoke(evt);
    }

    public List<TaskEvent> Drain()
    {
        var items = new List<TaskEvent>();
        while (_queue.TryDequeue(out var evt))
            items.Add(evt);
        return items;
    }

    public void Stop() => _cancelSource.Cancel();

    public void Start()
    {
        Background = Task.Run(() =>
        {
            var total = Math.Max(1, _steps.Count);
            var ctx = new Ctx(Emit, _cancelSource, Params);
            var failure = "";
            try
            {
                for (var index = 0; index < _steps.Count; index++)
                {
                    var item = _steps[index];
                    if (_cancelSource.IsCancellationRequested)
                    {
                        failure = "已中止";
                        break;
                    }
                    Emit(TaskEvent.StepStartE(index, item.Title));
                    try
                    {
                        var message = item.Fn(ctx) ?? "";
                        Emit(TaskEvent.StepOkE(index, message));
                        Emit(TaskEvent.ProgressE((index + 1) / (double)total));
                    }
                    catch (CancelledException)
                    {
                        Emit(TaskEvent.StepFailE(index, "已中止"));
                        failure = "已中止";
                        break;
                    }
                    catch (StepFailException exc)
                    {
                        Emit(TaskEvent.StepFailE(index, exc.Message));
                        failure = exc.Message;
                        break;
                    }
                    catch (Exception exc)
                    {

                        Emit(TaskEvent.StepFailE(index, $"内部错误：{exc.GetType().Name}: {exc.Message}"));
                        failure = $"内部错误：{exc.Message}";
                        break;
                    }
                }
            }
            finally
            {
                Ok = failure.Length == 0;
                Summary = failure.Length > 0 ? failure : "全部步骤完成";
                Emit(TaskEvent.DoneE(Ok.Value, Summary));
            }
        });
    }

    public void Join() => Background?.Wait();
}

public static class TaskSync
{
    public static TaskRunner RunSync(IEnumerable<Step> steps, Dictionary<string, object?>? parameters = null,
        Action<TaskEvent>? onEvent = null)
    {
        var runner = new TaskRunner(steps, parameters);
        if (onEvent != null)
            runner.OnEvent = onEvent;
        runner.Start();
        runner.Join();
        return runner;
    }
}
