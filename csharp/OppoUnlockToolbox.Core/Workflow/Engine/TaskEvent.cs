// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
namespace OppoUnlockToolbox.Core.Workflow;

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
