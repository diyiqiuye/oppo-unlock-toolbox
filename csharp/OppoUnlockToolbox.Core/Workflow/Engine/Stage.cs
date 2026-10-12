// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
namespace OppoUnlockToolbox.Core.Workflow;

public sealed class Stage
{
    public string Sid { get; }
    public int No { get; }
    public string Title { get; }
    public Field[] Fields { get; }
    public string Danger { get; }
    public Func<Dictionary<string, object?>, string[]>? Preview { get; }
    public Func<Dictionary<string, object?>, List<Step>> Build { get; }
    public bool NeedsRoot { get; }

    public Stage(string sid, int no, string title, Field[]? fields = null, string danger = "",
        Func<Dictionary<string, object?>, string[]>? preview = null,
        Func<Dictionary<string, object?>, List<Step>>? build = null,
        bool needsRoot = false)
    {
        Sid = sid;
        No = no;
        Title = title;
        Fields = fields ?? Array.Empty<Field>();
        Danger = danger;
        Preview = preview;
        Build = build ?? (_ => new List<Step>());
        NeedsRoot = needsRoot;
    }
}
