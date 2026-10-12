namespace OppoUnlockToolbox.ViewModels.Fields;

/// <summary>单选：一组 RadioButton，选中就写回 vars。</summary>
public sealed class ChoiceFieldViewModel : FieldViewModel
{
    public ChoiceFieldViewModel(Field field, Dictionary<string, object?> vars, string group)
        : base(field, vars)
    {
        if (!vars.ContainsKey(field.Key))
            vars[field.Key] = field.Default?.ToString() ?? "";
        GroupName = $"{group}.{field.Key}";
        Options = field.Choices
            .Select(choice => new ChoiceOptionViewModel(this, choice.Value, choice.Label))
            .ToList();
    }

    /// <summary>RadioButton 的分组名，必须每个字段唯一。</summary>
    public string GroupName { get; }

    public IReadOnlyList<ChoiceOptionViewModel> Options { get; }

    internal string SelectedValue => Vars[Field.Key]?.ToString() ?? "";

    internal void Choose(string value)
    {
        Vars[Field.Key] = value;
        Raise();
        foreach (var option in Options)
            option.Refresh();
        RaiseSelectionChanged();
    }
}

/// <summary>单选里的一个选项。</summary>
public sealed class ChoiceOptionViewModel : ObservableObject
{
    private readonly ChoiceFieldViewModel _owner;

    public ChoiceOptionViewModel(ChoiceFieldViewModel owner, string value, string text)
    {
        _owner = owner;
        Value = value;
        Text = text;
    }

    public string Value { get; }
    public string Text { get; }

    public bool IsChecked
    {
        get => _owner.SelectedValue == Value;
        set
        {
            if (value)
                _owner.Choose(Value);
        }
    }

    internal void Refresh() => Raise(nameof(IsChecked));
}
