namespace OppoUnlockToolbox.ViewModels;

/// <summary>左侧阶段导航里的一行。</summary>
public sealed class StageRailItemViewModel : ObservableObject
{
    private bool _current;
    private string _state = "todo";

    public StageRailItemViewModel(Stage stage)
    {
        Stage = stage;
        IconKey = StageVisuals.IconKey(stage.Sid);
    }

    public Stage Stage { get; }

    public string Sid => Stage.Sid;
    public string Title => Stage.Title;
    public string IconKey { get; }

    public bool IsCurrent
    {
        get => _current;
        set
        {
            if (Set(ref _current, value))
                RaiseVisual();
        }
    }

    /// <summary>todo / active / done / fail / skip。</summary>
    public string State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
                RaiseVisual();
        }
    }

    /// <summary>当前停在这一步时永远算 active，其余看跑没跑过。</summary>
    public string VisualState => IsCurrent ? "active" : State;

    public string Glyph => StageVisuals.Glyph(VisualState);

    private void RaiseVisual()
    {
        Raise(nameof(VisualState));
        Raise(nameof(Glyph));
    }
}
