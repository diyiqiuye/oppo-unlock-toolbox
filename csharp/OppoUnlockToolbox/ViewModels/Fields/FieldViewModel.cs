namespace OppoUnlockToolbox.ViewModels.Fields;

/// <summary>参数表单里一个字段的基类。值的唯一落点是阶段共享的 vars 字典。</summary>
public abstract class FieldViewModel : ObservableObject
{
    protected FieldViewModel(Field field, Dictionary<string, object?> vars)
    {
        Field = field;
        Vars = vars;
    }

    public Field Field { get; }

    /// <summary>阶段级共享字典，Step 读参数时读的就是它。</summary>
    protected Dictionary<string, object?> Vars { get; }

    public string Label => Field.Label;
    public string Hint => Field.Hint;
    public bool HasHint => Field.Hint.Length > 0;

    /// <summary>选择类字段变了：有条件字段的阶段要重画整张表单。</summary>
    public event Action? SelectionChanged;

    protected void RaiseSelectionChanged() => SelectionChanged?.Invoke();

    /// <summary>按字段声明造出对应的 VM。</summary>
    public static FieldViewModel Create(Field field, Dictionary<string, object?> vars,
        string group, Func<Field, string?> pick)
    {
        if (field.Kind == "note")
            return new NoteFieldViewModel(field, vars);
        return field.Kind switch
        {
            "bool" => new BooleanFieldViewModel(field, vars),
            "choice" => new ChoiceFieldViewModel(field, vars, group),
            "multi" => new MultiFieldViewModel(field, vars),
            "dir" or "file" => new PathFieldViewModel(field, vars, pick),
            _ => new TextFieldViewModel(field, vars),
        };
    }
}
