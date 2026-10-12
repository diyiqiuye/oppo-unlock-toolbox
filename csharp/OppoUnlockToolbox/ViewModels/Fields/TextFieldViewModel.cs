namespace OppoUnlockToolbox.ViewModels.Fields;

/// <summary>普通文本输入（确认词、次数等）。</summary>
public sealed class TextFieldViewModel : FieldViewModel
{
    public TextFieldViewModel(Field field, Dictionary<string, object?> vars)
        : base(field, vars)
    {
        if (!vars.ContainsKey(field.Key))
            vars[field.Key] = field.Default?.ToString() ?? "";
    }

    public string Value
    {
        get => Vars[Field.Key]?.ToString() ?? "";
        set
        {
            Vars[Field.Key] = value;
            Raise();
        }
    }

    /// <summary>Field.Width 是「多少个字宽」，换算成像素；没写就用默认宽度。</summary>
    public double Width => Field.Width is { } width ? Math.Max(70, width * 13 + 26) : 280;
}
