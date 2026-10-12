namespace OppoUnlockToolbox.ViewModels.Fields;

/// <summary>多选：一组 CheckBox，勾中的值收在一个 List&lt;string&gt; 里（备份分区就用它）。</summary>
public sealed class MultiFieldViewModel : FieldViewModel
{
    private readonly List<string> _selected;

    public MultiFieldViewModel(Field field, Dictionary<string, object?> vars)
        : base(field, vars)
    {
        if (vars.TryGetValue(field.Key, out var existing) && existing is List<string> list)
        {
            _selected = list;
        }
        else
        {
            _selected = field.Default is IEnumerable<string> defaults
                ? defaults.ToList()
                : field.Choices.Select(c => c.Value).ToList();
            vars[field.Key] = _selected;
        }

        Options = field.Choices
            .Select(choice => new MultiOptionViewModel(this, choice.Value, choice.Label))
            .ToList();
    }

    public IReadOnlyList<MultiOptionViewModel> Options { get; }

    internal bool IsSelected(string value) => _selected.Contains(value);

    internal void SetSelected(string value, bool wanted)
    {
        if (wanted && !_selected.Contains(value))
            _selected.Add(value);
        else if (!wanted && _selected.Contains(value))
            _selected.Remove(value);
    }
}

/// <summary>多选里的一个选项。</summary>
public sealed class MultiOptionViewModel : ObservableObject
{
    private readonly MultiFieldViewModel _owner;

    public MultiOptionViewModel(MultiFieldViewModel owner, string value, string text)
    {
        _owner = owner;
        Value = value;
        Text = text;
    }

    public string Value { get; }
    public string Text { get; }

    public bool IsChecked
    {
        get => _owner.IsSelected(Value);
        set => _owner.SetSelected(Value, value);
    }
}
