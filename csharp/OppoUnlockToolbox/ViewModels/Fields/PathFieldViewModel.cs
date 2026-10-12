using System.Windows.Input;

namespace OppoUnlockToolbox.ViewModels.Fields;

/// <summary>选目录 / 选文件：点按钮弹系统对话框，选完把路径写进 vars。</summary>
public sealed class PathFieldViewModel : FieldViewModel
{
    private readonly Func<Field, string?> _pick;

    public PathFieldViewModel(Field field, Dictionary<string, object?> vars, Func<Field, string?> pick)
        : base(field, vars)
    {
        _pick = pick;
        if (!vars.ContainsKey(field.Key))
            vars[field.Key] = field.Default?.ToString() ?? "";
        PickCommand = new RelayCommand(Pick);
    }

    public ICommand PickCommand { get; }

    public string Path => Vars[Field.Key]?.ToString() ?? "";

    /// <summary>没选之前显示「选择 xxx」。</summary>
    public string Display => Path.Length > 0 ? Path : $"选择{Field.Label}";

    private void Pick()
    {
        var chosen = _pick(Field);
        if (string.IsNullOrEmpty(chosen))
            return;
        Vars[Field.Key] = chosen;
        Raise(nameof(Path));
        Raise(nameof(Display));
    }
}
