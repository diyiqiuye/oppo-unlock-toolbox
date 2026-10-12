// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
namespace OppoUnlockToolbox.Core.Workflow;

public sealed class StepFailException : Exception
{
    public StepFailException(string message) : base(message) { }
}

public sealed class CancelledException : Exception
{
    public CancelledException() : base("已中止") { }
}
