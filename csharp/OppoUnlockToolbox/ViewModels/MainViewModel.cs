using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace OppoUnlockToolbox.ViewModels;

/// <summary>
/// 主界面的总装：把阶段会话、日志、设备探测、任务执行、资源安装串起来，
/// 暴露给 XAML 的命令与属性。原先 MainWindow 那 1200 行 code-behind 的职责都在这里。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly StageSession _session = new();
    private readonly LogBook _log = new();
    private readonly StageRunner _runner = new();
    private readonly DeviceWatcher _watcher = new();
    private readonly DialogService _dialogs;
    private readonly ThemeService _theme;
    private readonly ResourceInstaller _installer;
    private readonly bool _selfTestMode;

    private StageRailItemViewModel? _selectedRail;
    private int _stepTotal = 1;
    private string? _runningSid;
    private string _lastDeviceMode = "";
    private bool _running;

    public MainViewModel(DialogService dialogs, ThemeService theme, bool selfTestMode)
    {
        _dialogs = dialogs;
        _theme = theme;
        _selfTestMode = selfTestMode;
        _installer = new ResourceInstaller(_session, AppendLog);

        Rail = new ObservableCollection<StageRailItemViewModel>(
            StageRegistry.Stages.Select(stage => new StageRailItemViewModel(stage)));
        Stage = new StagePanelViewModel(_session, PickPathForField);
        Device = new DevicePanelViewModel();
        Logs = new LogPanelViewModel(_log);

        RefreshCommand = new RelayCommand(() => _watcher.Refresh(), () => !_watcher.IsProbing);
        OpenBackupDirCommand = new RelayCommand(() => _dialogs.OpenPath(AppPaths.BackupDir()));
        ResourcesCommand = new RelayCommand(OpenResources);
        AboutCommand = new RelayCommand(() => _dialogs.ShowAbout());
        RunCommand = new RelayCommand(RunCurrent, () => !_running);
        StopCommand = new RelayCommand(StopCurrent, () => _running);
        CopyCommand = new RelayCommand(CopyCurrentCommands);
        ClearLogCommand = new RelayCommand(_log.Clear);
        ThemeCommand = new RelayCommand(_theme.Toggle);
        SaveLogCommand = new RelayCommand(SaveLog);
        GoToStageCommand = new ParameterizedRelayCommand(
            parameter => { if (parameter is string sid) SelectStage(sid); });

        _session.Changed += RefreshRail;
        _runner.Event += OnTaskEvent;
        _watcher.Probed += OnProbed;
        _watcher.Status += text => Stage.ProgressText = text;
        _watcher.IsBlocked = () => _running;
        _theme.Changed += OnThemeChanged;

        _selectedRail = Rail[0];
        RefreshRail();
        Stage.ResetProgress("就绪");
        AutoFillRestoreSources();
    }

    // ── 给界面绑的东西 ──

    public ObservableCollection<StageRailItemViewModel> Rail { get; }
    public StagePanelViewModel Stage { get; }
    public DevicePanelViewModel Device { get; }
    public LogPanelViewModel Logs { get; }

    public string WindowTitle => $"{AppMeta.AppName} —— Find X5 Pro / Find X6 Pro";
    public string Version => "v" + AppMeta.AppVersion;

    /// <summary>顶栏品牌区下面的小字。</summary>
    public string Tagline => "Find X5 Pro / Find X6 Pro · BL 解锁 + 临时 root";

    /// <summary>会话卡里显示的工作目录。</summary>
    public string WorkDir => AppPaths.AppDir();

    /// <summary>会话卡里显示的日志目录。</summary>
    public string LogDir => AppPaths.LogDir();

    /// <summary>主题按钮：显示的是「切过去那个模式」的图标。</summary>
    public string ThemeIconKey => _theme.IsDark ? "sun" : "moon";

    public string ThemeToggleTip => _theme.IsDark ? "切换日间模式" : "切换夜间模式";

    /// <summary>侧栏底部进度卡：已完成几步 / 共几步。</summary>
    public string ProgressSummary => $"已完成 {_session.DoneCount} / {_session.Count}";

    public double ProgressPercent =>
        _session.Count == 0 ? 0 : _session.DoneCount * 100.0 / _session.Count;

    public ICommand RefreshCommand { get; }
    public ICommand OpenBackupDirCommand { get; }
    public ICommand ResourcesCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SaveLogCommand { get; }
    public ICommand GoToStageCommand { get; }
    public ICommand ThemeCommand { get; }

    /// <summary>列表选中项：用户点左边就是切阶段。</summary>
    public StageRailItemViewModel? SelectedRail
    {
        get => _selectedRail;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedRail))
                return;
            _selectedRail = value;
            Raise();
            SelectStage(value.Sid);
        }
    }

    /// <summary>换了阶段要把右边滚回顶部（视口的事交给 View）。</summary>
    public event Action? ScrollTopRequested;

    /// <summary>新日志行落地（视口据此决定要不要自动滚）。</summary>
    public event Action<LogRecord>? LogAppended;

    // ── 生命周期 ──

    public void Start()
    {
        _watcher.Start();
        _log.Appended += record => LogAppended?.Invoke(record);
    }

    /// <summary>窗口 Loaded 之后：没看过使用须知就弹一次。</summary>
    public void ShowFirstRunNoticeIfNeeded()
    {
        if (_selfTestMode || FirstRunNotice.AlreadyShown())
            return;
        try
        {
            if (_dialogs.ShowFirstRun())
                FirstRunNotice.MarkShown();
        }
        catch (Exception exc)
        {
            AppendLog("warn", $"首次运行说明没有弹出来：{exc.Message}");
        }
    }

    private void OnThemeChanged()
    {
        Raise(nameof(ThemeIconKey));
        Raise(nameof(ThemeToggleTip));
        Logs.RefreshBrushes();
    }

    public void Dispose()
    {
        _runner.Stop();
        _watcher.Dispose();
        _log.Dispose();
    }

    // ── 阶段导航 ──

    /// <summary>切到某个阶段：左边选中、右边重画、滚回顶部。用户点击和 Ctrl+1..7 都走这里。</summary>
    public void SelectStage(string sid)
    {
        if (!StageRegistry.StageById.ContainsKey(sid) || sid == _session.Current.Sid)
            return;
        _session.Select(sid);
        Stage.ResetProgress("就绪");
        ScrollTopRequested?.Invoke();
        var match = Rail.FirstOrDefault(item => item.Sid == sid);
        if (match is not null && !ReferenceEquals(match, _selectedRail))
        {
            _selectedRail = match;
            Raise(nameof(SelectedRail));
        }
    }

    private void RefreshRail()
    {
        Raise(nameof(ProgressSummary));
        Raise(nameof(ProgressPercent));
        foreach (var item in Rail)
        {
            item.IsCurrent = item.Sid == _session.Current.Sid;
            item.State = _session.StateOf(item.Sid);
        }
    }

    private string? PickPathForField(Field field) =>
        field.Kind == "dir" ? _dialogs.PickDirectory() : _dialogs.PickImage(field.Label);

    // ── 执行 ──

    public void RunCurrent() => RunStage(_session.Current.Sid);

    private void RunStage(string sid)
    {
        if (!StageRegistry.StageById.TryGetValue(sid, out var stage))
            return;
        if (_running)
        {
            _dialogs.Info("上一个阶段还在运行，先等它结束或点『停止』。", "正在执行");
            return;
        }

        var parameters = _session.CollectParams(stage);
        if (StageVisuals.RunIsDanger(sid))
        {
            var ok = _dialogs.Confirm(
                $"确认执行：{stage.Title}",
                $"{stage.Title}\n\n{stage.Danger}\n\n确定要现在执行吗？".Trim(),
                StageVisuals.RunLabel(sid));
            if (!ok)
                return;
        }

        List<Step> steps;
        try
        {
            steps = stage.Build(parameters);
        }
        catch (Exception exc)
        {
            _dialogs.Warn(exc.Message, "无法构建步骤");
            return;
        }

        _stepTotal = Math.Max(1, steps.Count);
        _running = true;
        _runningSid = sid;
        Stage.ShowIndeterminate($"正在执行 {stage.Title}…");
        AppendLog("info", $"──── 阶段 {stage.No}：{stage.Title} ────");
        CommandManager.InvalidateRequerySuggested();
        _runner.Start(steps, parameters);
    }

    private void StopCurrent()
    {
        if (!_running)
            return;
        _runner.Stop();
        Stage.ProgressText = "正在中止…";
        AppendLog("warn", "已请求中止，等待当前命令结束…");
    }

    private void OnTaskEvent(TaskEvent evt)
    {
        switch (evt.Kind)
        {
            case TaskEventKind.Log:
                AppendLog(evt.Level, evt.Text);
                break;
            case TaskEventKind.Status:
                Stage.ProgressText = evt.Text;
                break;
            case TaskEventKind.StepStart:
                Stage.ShowStep(evt.Index, _stepTotal, evt.Text);
                break;
            case TaskEventKind.StepOk:
            case TaskEventKind.StepFail:
                Stage.ProgressText = evt.Text;
                break;
            case TaskEventKind.Progress:
                Stage.ShowFraction(evt.Fraction);
                break;
            case TaskEventKind.Done:
                OnDone(evt.Ok, evt.Summary);
                break;
        }
    }

    private void OnDone(bool ok, string summary)
    {
        var sid = _runningSid ?? _session.Current.Sid;
        var stage = StageRegistry.StageById[sid];
        _running = false;
        _runningSid = null;

        if (ok)
        {
            _session.Mark(sid, "done");
            Stage.ProgressIndeterminate = false;
            Stage.ProgressValue = 100;
            Stage.ProgressText = $"阶段 {stage.No} 完成";
            AppendLog("ok", $"✓ 阶段 {stage.No}『{stage.Title}』完成。");
            if (sid == "backup")
                AutoFillRestoreSources();
            if (sid == "temproot")
                AppendLog("note", "提示：可以在「资源下载」里一键安装 KernelSU 管理器。");
        }
        else
        {
            _session.Mark(sid, "fail");
            Stage.ProgressIndeterminate = false;
            Stage.ProgressText = "已停止";
            AppendLog("err", $"✗ 阶段 {stage.No}『{stage.Title}』中断：{summary}");
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void CopyCurrentCommands()
    {
        var stage = _session.Current;
        var lines = stage.Preview?.Invoke(_session.CollectParams(stage)) ?? Array.Empty<string>();
        var text = string.Join("\n", lines);
        if (text.Length == 0)
            return;
        _dialogs.SetClipboard(text);
        Stage.ProgressText = "命令已复制到剪贴板";
    }

    // ── 设备探测 ──

    private void OnProbed(DeviceSnapshot snapshot)
    {
        var device = new Dictionary<string, object?> { ["kver"] = snapshot.Kver };
        if (snapshot.Model.Length > 0) device["model"] = snapshot.Model;
        if (snapshot.Kernel.Length > 0) device["kernel"] = snapshot.Kernel;
        if (snapshot.Slot.Length > 0) device["slot"] = snapshot.Slot;
        if (snapshot.Locked is { } locked) device["locked"] = locked;
        if (snapshot.Release.Length > 0) device["release"] = snapshot.Release;
        if (snapshot.Display.Length > 0) device["display"] = snapshot.Display;
        StateStore.SetDevice(device);

        Device.Apply(snapshot);
        AutoFillRestoreSources();

        if (snapshot.Mode != _lastDeviceMode)
        {
            var message = TransitionMessage(_lastDeviceMode, snapshot.Mode);
            if (message.Length > 0)
                AppendLog("note", message);
            _lastDeviceMode = snapshot.Mode;
        }
    }

    private static string TransitionMessage(string from, string to) => (from, to) switch
    {
        ("none", "device") => "检测到设备已连接",
        ("device", "none") => "设备已断开",
        ("none", "fastboot") => "检测到设备进入 fastboot",
        ("fastboot", "device") => "设备已从 fastboot 重启回系统",
        ("device", "unauthorized") => "设备未授权：请在手机上允许 USB 调试",
        _ => "",
    };

    // ── 日志 ──

    public void AppendLog(string level, string message) => _log.Append(level, message);

    private void SaveLog()
    {
        var path = _dialogs.AskSaveLogPath($"oppo_toolbox_{DateTime.Now:MMdd_HHmmss}.log");
        if (path is null)
            return;
        try
        {
            _log.SaveTo(path);
            AppendLog("ok", $"日志已保存：{path}");
        }
        catch (Exception exc)
        {
            _dialogs.Warn(exc.Message, "保存失败");
        }
    }

    // ── 资源下载 ──

    private void OpenResources()
    {
        _dialogs.ShowResources(_installer);
        Stage.Reload();
    }

    /// <summary>
    /// 「还原原分区」的两个文件框：优先用账本里这台机器的原厂备份自动填上。
    /// </summary>
    private void AutoFillRestoreSources()
    {
        try
        {
            var vars = _session.VarsOf("restore");
            var data = StateStore.Load();

            var deviceSerial = "";
            try { deviceSerial = data["device"]?["serial"]?.GetValue<string>() ?? ""; }
            catch { deviceSerial = ""; }

            (string Local, long Ts)? StockOf(string part)
            {
                var record = StateStore.NewestStockBackup(part);
                if (record is null)
                    return null;
                var local = StateStore.RecordString(record, "local");
                if (local.Length == 0 || !File.Exists(local))
                    return null;
                long ts;
                try { ts = record["ts"]?.GetValue<long>() ?? 0; }
                catch { ts = 0; }
                var serial = StateStore.RecordString(record, "serial");
                if (serial.Length > 0 && deviceSerial.Length > 0 && serial != deviceSerial)
                    return null;
                return (local, ts);
            }

            var ocdt = StockOf("ocdt");

            var slot = data["device"]?["slot"]?.GetValue<string>() ?? "";
            (string Local, long Ts)? abl = slot is "a" or "b" ? StockOf("abl_" + slot) : null;
            if (abl is null)
            {
                var a = StockOf("abl_a");
                var b = StockOf("abl_b");
                abl = (a, b) switch
                {
                    (null, null) => null,
                    (not null, null) => a,
                    (null, not null) => b,
                    (not null, not null) => a.Value.Ts >= b.Value.Ts ? a : b,
                };
            }

            var changed = false;
            foreach (var (key, pick) in new[] { ("abl_file", abl), ("ocdt_file", ocdt) })
            {
                if (pick is not { } chosen)
                    continue;
                var current = vars.GetValueOrDefault(key)?.ToString() ?? "";
                if (current.Length > 0 && File.Exists(current))
                    continue;
                if (current == chosen.Local)
                    continue;
                vars[key] = chosen.Local;
                changed = true;
            }

            if (changed)
                _session.NotifyVarsChanged("restore");
        }
        catch
        {

        }
    }
}
