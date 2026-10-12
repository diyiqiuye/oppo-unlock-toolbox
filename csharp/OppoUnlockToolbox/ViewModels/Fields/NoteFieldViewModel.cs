namespace OppoUnlockToolbox.ViewModels.Fields;

/// <summary>纯说明文字（不占参数，也不进 vars）。</summary>
public sealed class NoteFieldViewModel : FieldViewModel
{
    public NoteFieldViewModel(Field field, Dictionary<string, object?> vars)
        : base(field, vars)
    {
    }
}
