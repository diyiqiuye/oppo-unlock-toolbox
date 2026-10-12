// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
namespace OppoUnlockToolbox.Core.Workflow;

public sealed class Field
{
    public string Key { get; }
    public string Label { get; }
    public string Kind { get; }
    public string Hint { get; }
    public object? Default { get; }
    public (string Value, string Label)[] Choices { get; }
    public int? Width { get; }

    /// <summary>返回 false 时这个字段不显示（值仍然保留在 vars 里）。</summary>
    public Func<IReadOnlyDictionary<string, object?>, bool>? When { get; }

    public Field(string key, string label, string kind, string hint = "",
        object? defaultValue = null, (string, string)[]? choices = null, int? width = null,
        Func<IReadOnlyDictionary<string, object?>, bool>? when = null)
    {
        Key = key;
        Label = label;
        Kind = kind;
        Hint = hint;
        Default = defaultValue;
        Choices = choices ?? Array.Empty<(string, string)>();
        Width = width;
        When = when;
    }
}
