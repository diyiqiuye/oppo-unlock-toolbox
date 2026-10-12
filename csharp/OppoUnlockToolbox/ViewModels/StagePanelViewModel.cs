using System.Collections.ObjectModel;
using OppoUnlockToolbox.ViewModels.Fields;

namespace OppoUnlockToolbox.ViewModels;

/// <summary>右侧那张卡片：当前阶段的标题、警告、参数表单、进度。</summary>
public sealed class StagePanelViewModel : ObservableObject
{
    private readonly StageSession _session;
    private readonly Func<Field, string?> _pick;
    private string _progressText = "就绪";
    private double _progressValue;
    private bool _progressIndeterminate;

    public StagePanelViewModel(StageSession session, Func<Field, string?> pick)
    {
        _session = session;
        _pick = pick;
        session.Changed += Reload;
        Reload();
    }

    /// <summary>当前阶段的可见参数（按 Field.When 过滤之后）。</summary>
    public ObservableCollection<FieldViewModel> Fields { get; } = new();

    public Stage Stage => _session.Current;

    public string StageChipText => $"阶段 {Stage.No} / {_session.Count}";
    public string Title => Stage.Title;
    public string NeedsRootText => Stage.NeedsRoot ? "需要临时 root" : "";
    public bool NeedsRootVisible => Stage.NeedsRoot;
    public string DangerText => Stage.Danger;
    public bool DangerVisible => Stage.Danger.Length > 0;
    public string RunLabel => StageVisuals.RunLabel(Stage.Sid);
    public bool RunIsDanger => StageVisuals.RunIsDanger(Stage.Sid);
    public bool HasNoFields => Fields.Count == 0;

    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => Set(ref _progressValue, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        set => Set(ref _progressIndeterminate, value);
    }

    /// <summary>切阶段 / 参数被外部改动之后重画整张表单。</summary>
    public void Reload()
    {
        BuildFields();
        Raise(nameof(Stage));
        Raise(nameof(StageChipText));
        Raise(nameof(Title));
        Raise(nameof(NeedsRootText));
        Raise(nameof(NeedsRootVisible));
        Raise(nameof(DangerText));
        Raise(nameof(DangerVisible));
        Raise(nameof(RunLabel));
        Raise(nameof(RunIsDanger));
        Raise(nameof(HasNoFields));
    }

    public void ResetProgress(string text)
    {
        ProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = text;
    }

    public void ShowIndeterminate(string text)
    {
        ProgressIndeterminate = true;
        ProgressText = text;
    }

    public void ShowStep(int index, int total, string title)
    {
        ProgressIndeterminate = false;
        ProgressValue = index / (double)Math.Max(1, total) * 100;
        ProgressText = $"步骤 {index + 1}/{total} · {title}";
    }

    public void ShowFraction(double fraction)
    {
        ProgressIndeterminate = false;
        ProgressValue = Math.Clamp(fraction, 0, 1) * 100;
    }

    private void BuildFields()
    {
        foreach (var field in Fields)
            field.SelectionChanged -= OnFieldSelectionChanged;
        Fields.Clear();

        var vars = _session.VarsOf(Stage.Sid);
        foreach (var field in Stage.Fields)
        {
            if (field.When is not null && !field.When(vars))
                continue;
            var vm = FieldViewModel.Create(field, vars, Stage.Sid, _pick);
            vm.SelectionChanged += OnFieldSelectionChanged;
            Fields.Add(vm);
        }
    }

    /// <summary>只有「有 When 条件的阶段」才需要因为一次选择重画整张表单。</summary>
    private void OnFieldSelectionChanged()
    {
        if (Stage.Fields.Any(f => f.When is not null))
            BuildFields();
    }
}
