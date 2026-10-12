namespace OppoUnlockToolbox.Services;

/// <summary>
/// 一次使用过程中的阶段状态：当前停在哪一步、每一步填过什么、哪些步已经跑完。
/// 参数按阶段分别保存（切来切去不丢），和拆分前 MainWindow 里的 _stageVars / _sessionStates 等价。
/// </summary>
public sealed class StageSession
{
    private readonly Dictionary<string, Dictionary<string, object?>> _vars = new();
    private readonly Dictionary<string, string> _states = new();

    public Stage Current { get; private set; } = StageRegistry.Stages[0];

    public int Count => StageRegistry.Stages.Length;

    /// <summary>当前阶段或阶段状态变了，界面整体重画。</summary>
    public event Action? Changed;

    public string StateOf(string sid) => _states.GetValueOrDefault(sid, "todo");

    public void Mark(string sid, string state)
    {
        _states[sid] = state;
        Changed?.Invoke();
    }

    public Dictionary<string, object?> VarsOf(string sid)
    {
        if (!_vars.TryGetValue(sid, out var vars))
        {
            vars = new Dictionary<string, object?>();
            _vars[sid] = vars;
        }
        return vars;
    }

    public void Select(string sid)
    {
        if (sid == Current.Sid || !StageRegistry.StageById.ContainsKey(sid))
            return;
        Current = StageRegistry.StageById[sid];
        Changed?.Invoke();
    }

    public void SelectIndex(int index)
    {
        if (index >= 0 && index < StageRegistry.Stages.Length)
            Select(StageRegistry.Stages[index].Sid);
    }

    /// <summary>下一个阶段（没有就返回 null）。</summary>
    public Stage? NextStage()
    {
        var index = StageRegistry.StageIndex(Current.Sid);
        return index + 1 < StageRegistry.Stages.Length ? StageRegistry.Stages[index + 1] : null;
    }

    /// <summary>把当前阶段的参数收成一个字典交给 Step 用。</summary>
    public Dictionary<string, object?> CollectParams(Stage stage)
    {
        var vars = VarsOf(stage.Sid);
        var parameters = new Dictionary<string, object?>();
        foreach (var field in stage.Fields)
        {
            if (vars.TryGetValue(field.Key, out var value))
                parameters[field.Key] = value;
        }
        return parameters;
    }

    /// <summary>外部（资源下载自动填充）改了参数，通知界面重画。</summary>
    public void NotifyVarsChanged(string sid)
    {
        if (sid == Current.Sid)
            Changed?.Invoke();
    }
}
