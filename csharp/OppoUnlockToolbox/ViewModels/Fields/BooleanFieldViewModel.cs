namespace OppoUnlockToolbox.ViewModels.Fields;

/// <summary>开关。</summary>
public sealed class BooleanFieldViewModel : FieldViewModel
{
    public BooleanFieldViewModel(Field field, Dictionary<string, object?> vars)
        : base(field, vars)
    {
        if (!vars.ContainsKey(field.Key))
            vars[field.Key] = field.Default is bool b && b;
    }

    public bool Value
    {
        get => Vars[Field.Key] is true;
        set
        {
            Vars[Field.Key] = value;
            Raise();
        }
    }
}
