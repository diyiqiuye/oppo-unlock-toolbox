// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
namespace OppoUnlockToolbox.Core.Workflow;

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
