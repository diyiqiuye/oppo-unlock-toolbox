

using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OppoUnlockToolbox.Core;

namespace OppoUnlockToolbox.Ui;

public sealed record ToolInfo(string? Path, string Source);

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, string> RunLabels =
        new Dictionary<string, string>
        {
            ["write"] = "执行写入（改写分区）",
            ["fastboot"] = "执行解锁（清空数据）",
            ["restore"] = "执行还原（改写分区）",
        };

    private static readonly IReadOnlyDictionary<string, string> StageIcons =
        new Dictionary<string, string>
        {
            ["temproot"] = "key",
            ["push"] = "upload",
            ["backup"] = "save",
            ["write"] = "edit",
            ["fastboot"] = "unlock",
            ["verify"] = "shield",
            ["restore"] = "undo",
        };

    private readonly List<LogEntry> _logEntries = new();
    private readonly Dictionary<string, (Border Root, Border Chip, IconBox Icon, TextBlock Title, TextBlock Glyph)> _railParts = new();
    private readonly Dictionary<string, (TextBlock Value, TextBlock Note, IconBox Icon)> _tiles = new();

    private readonly Dictionary<string, Dictionary<string, object?>> _stageVars = new();
    private readonly ConcurrentQueue<Dictionary<string, object?>> _probeQueue = new();

    private TaskRunner? _runner;
    private string? _runningSid;
    private string _currentSid = Workflow.Stages[0].Sid;
    private bool _probing;
    private int _stepTotal = 1;
    private readonly DispatcherTimer _pollTimer;
    private readonly DeviceMonitor _deviceMonitor;
    private readonly DispatcherTimer _probeDebounce;
    private string _lastDeviceMode = "";
    private StreamWriter? _logWriter;

    public MainWindow()
    {
        InitializeComponent();
        BuildStageRail();
        BuildDeviceTiles();
        UpdateDeviceTiles(new Dictionary<string, object?>());
        AutoFillRestoreSources();
        SelectStage(Workflow.Stages[0].Sid);

        Loaded += OnWindowLoaded;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();

        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _runner?.Stop();
            _deviceMonitor.Dispose();
            _logWriter?.Dispose();
        };

        _probeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _probeDebounce.Tick += (_, _) =>
        {
            _probeDebounce.Stop();
            if (_runner is { IsAlive: true })
                return;
            RefreshDevice();
        };

        _deviceMonitor = new DeviceMonitor(() => Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || _probing || _runner is { IsAlive: true })
                return;
            _probeDebounce.Stop();
            _probeDebounce.Start();
        }));
        _deviceMonitor.Start();
    }

    private Dictionary<string, object?> StageVars(string sid)
    {
        if (!_stageVars.TryGetValue(sid, out var vars))
        {
            vars = new Dictionary<string, object?>();
            _stageVars[sid] = vars;
        }
        return vars;
    }

    private void BuildStageRail()
    {
        foreach (var stage in Workflow.Stages)
        {
            var chip = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(9),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var icon = new IconBox { Key = StageIcons.GetValueOrDefault(stage.Sid, "info"), Size = 16 };
            chip.Child = icon;

            var title = new TextBlock
            {
                Text = stage.Title,
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var glyph = new TextBlock
            {
                Text = "○",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            var content = new Grid { Margin = new Thickness(12, 0, 14, 0) };
            content.Children.Add(chip);
            Grid.SetColumn(title, 1);
            content.Children.Add(title);
            Grid.SetColumn(glyph, 2);
            content.Children.Add(glyph);
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var row = new Border
            {
                Height = 42,
                CornerRadius = new CornerRadius(12),
                Child = content,
                Cursor = Cursors.Hand,
                Focusable = true,
                Margin = new Thickness(0, 2, 0, 2),
            };
            row.MouseLeftButtonUp += (_, _) => SelectStage(stage.Sid);
            row.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                    SelectStage(stage.Sid);
            };
            row.MouseEnter += (_, _) => PaintRailRow(stage.Sid);
            row.MouseLeave += (_, _) => PaintRailRow(stage.Sid);

            StageRailHost.Children.Add(row);
            _railParts[stage.Sid] = (row, chip, icon, title, glyph);
            PaintRailRow(stage.Sid);
        }
    }

    private void PaintRailRow(string sid)
    {
        if (!_railParts.TryGetValue(sid, out var parts))
            return;
        var isCurrent = sid == _currentSid;
        var state = isCurrent ? "active" : StageState(sid);
        var (bg, fg, iconFg) = state switch
        {
            "active" => (TryBrush("AccentSoft"), TryBrush("AccentText"), TryBrush("AccentText")),
            "done" => (TryBrush("OkSoft"), TryBrush("Ok"), TryBrush("Ok")),
            "fail" => (TryBrush("DangerSoft"), TryBrush("Danger"), TryBrush("Danger")),
            _ => ((Brush?)null, TryBrush("Ink2"), TryBrush("Ink3")),
        };
        var hovered = parts.Root.IsMouseOver && !isCurrent && state == "todo";
        parts.Root.Background = hovered ? TryBrush("Hover") : bg ?? Brushes.Transparent;
        parts.Title.Foreground = state switch
        {
            "active" => TryBrush("Ink"),
            "done" => TryBrush("Ok"),
            "fail" => TryBrush("Danger"),
            _ => TryBrush("Ink2"),
        };
        parts.Title.FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal;
        parts.Chip.Background = state switch
        {
            "active" => TryBrush("Surface"),
            "done" => TryBrush("OkSoft"),
            "fail" => TryBrush("DangerSoft"),
            _ => TryBrush("Hover"),
        };
        parts.Icon.Foreground = iconFg;
        parts.Glyph.Text = state switch
        {
            "active" => "▸",
            "done" => "✓",
            "fail" => "✗",
            "skip" => "–",
            _ => "○",
        };
        parts.Glyph.Foreground = parts.Title.Foreground;
    }

    private readonly Dictionary<string, string> _sessionStates = new();
    private string StageState(string sid) => _sessionStates.GetValueOrDefault(sid, "todo");

    private void BuildDeviceTiles()
    {
        _tiles["tools"] = BuildTile(TileTools, "chip", "工具链");
        _tiles["device"] = BuildTile(TileDevice, "phone", "设备");
        _tiles["system"] = BuildTile(TileSystem, "shield", "系统");
        _tiles["lock"] = BuildTile(TileLock, "lock", "引导锁 / 槽位");
    }

    private (TextBlock Value, TextBlock Note, IconBox Icon) BuildTile(Border host, string iconKey, string title)
    {
        var icon = new IconBox { Key = iconKey, Size = 16, Foreground = TryBrush("Ink3") };
        var caption = new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = TryBrush("Ink3"),
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var captionRow = new StackPanel { Orientation = Orientation.Horizontal };
        captionRow.Children.Add(icon);
        captionRow.Children.Add(caption);

        var value = new TextBlock
        {
            Text = "未检测",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var note = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = TryBrush("Ink3"),
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var stack = new StackPanel();
        stack.Children.Add(captionRow);
        stack.Children.Add(value);
        stack.Children.Add(note);
        host.Child = stack;
        return (value, note, icon);
    }

    private void UpdateDeviceTiles(Dictionary<string, object?> info)
    {
        var tools = info.TryGetValue("tools", out var t) && t is Dictionary<string, ToolInfo> dict
            ? dict
            : new Dictionary<string, ToolInfo>();
        string adbSrc, fbSrc;
        bool ready;
        if (tools.Count > 0)
        {
            adbSrc = tools.TryGetValue("adb", out var a) ? a.Source : "未找到";
            fbSrc = tools.TryGetValue("fastboot", out var f) ? f.Source : "未找到";
            ready = tools.TryGetValue("adb", out var a2) && a2.Path is not null
                    && tools.TryGetValue("fastboot", out var f2) && f2.Path is not null;
        }
        else
        {
            ready = AppConfig.FindTool("adb") is not null && AppConfig.FindTool("fastboot") is not null;
            adbSrc = AppConfig.ToolSource("adb");
            fbSrc = AppConfig.ToolSource("fastboot");
        }

        var mode = info.TryGetValue("mode", out var m) ? m?.ToString() ?? "none" : "none";
        var modeText = mode switch
        {
            "device" => "已连接",
            "fastboot" => "fastboot 模式",
            "unauthorized" => "未授权",
            "offline" => "离线",
            "none" => "未连接",
            _ => mode,
        };
        var kernel = info.TryGetValue("kernel", out var k) ? k?.ToString() ?? "" : "";
        var lockValue = info.TryGetValue("locked", out var lk) && lk is int i ? i : (int?)null;
        var lockText = lockValue switch { 0 => "已解锁", 1 => "锁定", _ => "未检测" };
        var model = info.TryGetValue("model", out var mo) ? mo?.ToString() ?? "" : "";
        var release = info.TryGetValue("release", out var re) ? re?.ToString() ?? "" : "";
        var slot = info.TryGetValue("slot", out var sl) ? sl?.ToString() ?? "" : "";

        _tiles["tools"].Value.Text = ready ? "就绪" : "缺失";
        _tiles["tools"].Value.Foreground = ready ? TryBrush("Ok") : TryBrush("Danger");
        _tiles["tools"].Note.Text = $"adb {adbSrc} · fastboot {fbSrc}";

        _tiles["device"].Value.Text = modeText;
        _tiles["device"].Value.Foreground = TryBrush("Ink");
        _tiles["device"].Note.Text = info.TryGetValue("serial", out var se) && se?.ToString() is { Length: > 0 } serial
            ? serial
            : "未检测";

        _tiles["system"].Value.Text = model.Length > 0 ? $"Android {release} · {model}" : "未检测";
        _tiles["system"].Note.Text = kernel.Length > 0 ? kernel : "内核未检测";

        _tiles["lock"].Value.Text = lockText;
        _tiles["lock"].Value.Foreground = lockValue switch
        {
            0 => TryBrush("Ok"),
            1 => TryBrush("Ink"),
            _ => TryBrush("Ink3"),
        };
        _tiles["lock"].Note.Text = slot.Length > 0 ? $"活动槽 _{slot}" : "活动槽未识别";
    }

    private void SelectStage(string sid)
    {
        var stage = Workflow.StageById[sid];
        _currentSid = sid;
        foreach (var s in Workflow.Stages)
            PaintRailRow(s.Sid);

        StageChipText.Text = $"阶段 {stage.No} / {Workflow.Stages.Length}";
        RootFlagText.Text = stage.NeedsRoot ? "需要临时 root" : "";
        StageTitleText.Text = stage.Title;
        RunBtnText.Text = RunLabels.GetValueOrDefault(stage.Sid, "执行本阶段");
        RunBtn.Style = (Style)FindResource(RunLabels.ContainsKey(stage.Sid) ? "BtnDanger" : "BtnPrimary");

        if (!string.IsNullOrEmpty(stage.Danger))
        {
            DangerChip.Visibility = Visibility.Visible;
            DangerBanner.Visibility = Visibility.Visible;
            DangerText.Text = stage.Danger;
        }
        else
        {
            DangerChip.Visibility = Visibility.Collapsed;
            DangerBanner.Visibility = Visibility.Collapsed;
        }

        ResetProgress("就绪");
        RenderFields(stage);
        RightScroll.ScrollToTop();
    }

    private void RenderFields(Stage stage)
    {
        ParamsHost.Children.Clear();
        var vars = StageVars(stage.Sid);
        var visible = stage.Fields.Where(f => f.When is null || f.When(vars)).ToList();
        if (visible.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "这一阶段没有参数，直接执行即可。",
                FontSize = 12.5,
                Foreground = TryBrush("Ink3"),
            };
            ParamsHost.Children.Add(empty);
            return;
        }

        for (var index = 0; index < visible.Count; index++)
        {
            var field = visible[index];
            if (field.Kind == "note")
            {
                ParamsHost.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = TryBrush("LvlWarn"),
                    Margin = new Thickness(0, 2, 0, 8),
                });
                continue;
            }
            var label = new TextBlock
            {
                Text = field.Label,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TryBrush("Ink2"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var control = new StackPanel();

            switch (field.Kind)
            {
                case "bool":
                {
                    if (!vars.ContainsKey(field.Key))
                        vars[field.Key] = field.Default is bool b && b;
                    var toggle = new ToggleButton { Style = (Style)FindResource("AppSwitch"), HorizontalAlignment = HorizontalAlignment.Left };
                    toggle.IsChecked = vars[field.Key] is true;
                    toggle.Click += (_, _) => vars[field.Key] = toggle.IsChecked == true;
                    control.Children.Add(toggle);
                    break;
                }
                case "choice":
                {
                    if (!vars.ContainsKey(field.Key))
                        vars[field.Key] = field.Default?.ToString() ?? "";
                    var panel = new WrapPanel();
                    foreach (var (value, text) in field.Choices)
                    {
                        var radio = new RadioButton
                        {
                            Style = (Style)FindResource("AppRadio"),
                            Content = text,
                            GroupName = $"{stage.Sid}.{field.Key}",
                            Margin = new Thickness(0, 0, 14, 4),
                        };
                        radio.IsChecked = string.Equals(vars[field.Key]?.ToString(), value, StringComparison.Ordinal);
                        radio.Checked += (_, _) =>
                        {
                            vars[field.Key] = value;
                            if (stage.Fields.Any(f => f.When is not null))
                                RenderFields(stage);
                        };
                        panel.Children.Add(radio);
                    }
                    control.Children.Add(panel);
                    break;
                }
                case "multi":
                {
                    List<string> holder;
                    if (vars.TryGetValue(field.Key, out var existing) && existing is List<string> list)
                    {
                        holder = list;
                    }
                    else
                    {
                        holder = field.Default is IEnumerable<string> defaults
                            ? defaults.ToList()
                            : field.Choices.Select(c => c.Value).ToList();
                        vars[field.Key] = holder;
                    }
                    var panel = new WrapPanel();
                    foreach (var (value, text) in field.Choices)
                    {
                        var check = new CheckBox
                        {
                            Style = (Style)FindResource("AppCheck"),
                            Content = text,
                            Margin = new Thickness(0, 0, 14, 4),
                        };
                        check.IsChecked = holder.Contains(value);
                        check.Click += (_, _) =>
                        {
                            if (check.IsChecked == true && !holder.Contains(value))
                                holder.Add(value);
                            else if (check.IsChecked != true && holder.Contains(value))
                                holder.Remove(value);
                        };
                        panel.Children.Add(check);
                    }
                    control.Children.Add(panel);
                    break;
                }
                case "dir":
                case "file":
                {
                    if (!vars.ContainsKey(field.Key))
                        vars[field.Key] = field.Default?.ToString() ?? "";
                    var chosen = vars[field.Key]?.ToString() ?? "";
                    var labelText = new TextBlock
                    {
                        Text = chosen.Length > 0 ? chosen : $"选择{field.Label}",
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 430,
                    };
                    var buttonContent = new StackPanel { Orientation = Orientation.Horizontal };
                    buttonContent.Children.Add(new IconBox { Key = "folder", Size = 15, Margin = new Thickness(0, 0, 7, 0) });
                    buttonContent.Children.Add(labelText);
                    var button = new Button
                    {
                        Style = (Style)FindResource("BtnDefault"),
                        Content = buttonContent,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    };
                    button.Click += (_, _) =>
                    {
                        string? path = field.Kind == "dir" ? PickDirectory() : PickFile(field.Label);
                        if (!string.IsNullOrEmpty(path))
                        {
                            vars[field.Key] = path;
                            labelText.Text = path;
                        }
                    };
                    control.Children.Add(button);
                    break;
                }
                default:
                {
                    if (!vars.ContainsKey(field.Key))
                        vars[field.Key] = field.Default?.ToString() ?? "";
                    var width = field.Width is { } w ? Math.Max(70, w * 13 + 26) : 280;
                    var input = new TextBox
                    {
                        Style = (Style)FindResource("AppInput"),
                        Width = width,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Text = vars[field.Key]?.ToString() ?? "",
                    };
                    input.TextChanged += (_, _) => vars[field.Key] = input.Text;
                    control.Children.Add(input);
                    break;
                }
            }

            var grid = new Grid { Margin = new Thickness(0, index == 0 ? 0 : 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(label);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);

            var rowBorder = new Border
            {
                Child = grid,
                Padding = new Thickness(0, 8, 0, 10),
            };
            if (field.Hint.Length > 0)
                rowBorder.ToolTip = field.Hint;
            if (index < visible.Count - 1)
                rowBorder.BorderBrush = TryBrush("Line");
            rowBorder.BorderThickness = new Thickness(0, 0, 0, index < visible.Count - 1 ? 1 : 0);
            ParamsHost.Children.Add(rowBorder);
        }
    }

    private string? PickDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择目录",
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PickFile(string label)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"选择{label}",
            Filter = "镜像文件 (*.img)|*.img|所有文件 (*.*)|*.*",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private Dictionary<string, object?> CollectParams(Stage stage)
    {
        var vars = StageVars(stage.Sid);
        var parameters = new Dictionary<string, object?>();
        foreach (var field in stage.Fields)
        {
            if (vars.TryGetValue(field.Key, out var value))
                parameters[field.Key] = value;
        }
        return parameters;
    }

    private void OnRunClick(object sender, RoutedEventArgs e) => RunCurrent();

    private void RunCurrent() => RunStage(_currentSid);

    private void RunStage(string sid)
    {
        var stage = Workflow.StageById[sid];
        if (_runner is { IsAlive: true })
        {
            MessageBox.Show(this, "上一个阶段还在运行，先等它结束或点『停止』。", "正在执行",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var parameters = CollectParams(stage);
        if (RunLabels.ContainsKey(sid))
        {
            var ok = new ConfirmWindow(
                $"确认执行：{stage.Title}",
                $"{stage.Title}\n\n{stage.Danger}\n\n确定要现在执行吗？".Trim(),
                RunLabels[sid])
            { Owner = this };
            if (ok.ShowDialog() != true)
                return;
        }

        List<Step> steps;
        try
        {
            steps = stage.Build(parameters);
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "无法构建步骤", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _stepTotal = Math.Max(1, steps.Count);
        SetRunning(true, stage);
        AppendLog("info", $"──── 阶段 {stage.No}：{stage.Title} ────");
        _runner = new TaskRunner(steps, parameters);
        _runningSid = sid;
        _runner.Start();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopCurrent();

    private void StopCurrent()
    {
        if (_runner is { IsAlive: true })
        {
            _runner.Stop();
            ProgressText.Text = "正在中止…";
            AppendLog("warn", "已请求中止，等待当前命令结束…");
        }
    }

    private void SetRunning(bool running, Stage? stage = null)
    {
        RunBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
        if (running)
        {
            ProgressBar.IsIndeterminate = true;
            ProgressText.Text = $"正在执行 {stage?.Title ?? ""}…";
        }
        else
        {
            ProgressBar.IsIndeterminate = false;
        }
    }

    private void ResetProgress(string text)
    {
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        ProgressText.Text = text;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var stage = Workflow.StageById[_currentSid];
        var lines = stage.Preview?.Invoke(CollectParams(stage)) ?? Array.Empty<string>();
        var text = string.Join("\n", lines);
        if (text.Length == 0)
            return;
        Clipboard.SetText(text);
        ProgressText.Text = "命令已复制到剪贴板";
    }

    private void Poll()
    {
        while (_probeQueue.TryDequeue(out var info))
            ApplyProbe(info);
        if (_runner is null)
            return;
        foreach (var evt in _runner.Drain())
            HandleEvent(evt);
    }

    private void HandleEvent(TaskEvent evt)
    {
        switch (evt.Kind)
        {
            case TaskEventKind.Log:
                AppendLog(evt.Level, evt.Text);
                break;
            case TaskEventKind.Status:
                ProgressText.Text = evt.Text;
                break;
            case TaskEventKind.StepStart:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = evt.Index / (double)_stepTotal * 100;
                ProgressText.Text = $"步骤 {evt.Index + 1}/{_stepTotal} · {evt.Text}";
                break;
            case TaskEventKind.StepOk:
                ProgressText.Text = evt.Text;
                break;
            case TaskEventKind.StepFail:
                ProgressText.Text = evt.Text;
                break;
            case TaskEventKind.Progress:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = Math.Clamp(evt.Fraction, 0, 1) * 100;
                break;
            case TaskEventKind.Done:
                OnDone(evt.Ok, evt.Summary);
                break;
        }
    }

    private void OnDone(bool ok, string summary)
    {
        var sid = _runningSid ?? _currentSid;
        var stage = Workflow.StageById[sid];
        SetRunning(false, stage);
        _runner = null;
        _runningSid = null;
        if (ok)
        {
            _sessionStates[sid] = "done";
            PaintRailRow(sid);
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            ProgressText.Text = $"阶段 {stage.No} 完成";
            AppendLog("ok", $"✓ 阶段 {stage.No}『{stage.Title}』完成。");
            if (sid == "backup")
                AutoFillRestoreSources();
            if (sid == "temproot")
                AppendLog("note", "提示：可以在「资源下载」里一键安装 KernelSU 管理器。");
            var order = Workflow.Stages.Select(s => s.Sid).ToList();
            var index = order.IndexOf(sid);
            if (index + 1 < order.Count)
                PaintRailRow(order[index + 1]);
        }
        else
        {
            _sessionStates[sid] = "fail";
            PaintRailRow(sid);
            ProgressBar.IsIndeterminate = false;
            ProgressText.Text = "已停止";
            AppendLog("err", $"✗ 阶段 {stage.No}『{stage.Title}』中断：{summary}");
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshDevice();

    private void RefreshDevice()
    {
        if (_probing)
            return;
        if (_runner is { IsAlive: true })
        {
            ProgressText.Text = "任务执行中，稍后再检测设备";
            return;
        }
        _probing = true;
        RefreshBtn.IsEnabled = false;
        ProgressText.Text = "正在检测设备…";
        Task.Run(() =>
        {
            var info = ProbeDevice();
            _probeQueue.Enqueue(info);
        });
    }

    private static Dictionary<string, object?> ProbeDevice()
    {
        var info = new Dictionary<string, object?> { ["mode"] = "none", ["tools"] = new Dictionary<string, ToolInfo>() };
        try
        {
            var tools = new Dictionary<string, ToolInfo>();
            foreach (var name in new[] { "adb", "fastboot" })
            {
                var path = AppConfig.FindTool(name);
                tools[name] = new ToolInfo(path, AppConfig.ToolSource(name));
            }
            info["tools"] = tools;
            var adb = AdbDefault.Instance;
            var devices = adb.Devices();
            if (devices.Count > 0)
            {
                var entry = devices[0];
                foreach (var candidate in devices)
                {
                    if (candidate["state"] == "device")
                    {
                        entry = candidate;
                        break;
                    }
                }
                info["mode"] = entry["state"];
                info["serial"] = entry["serial"];
                if (entry["state"] == "device")
                {
                    adb.SetSerial(entry["serial"]);
                    var props = adb.Props(40000);
                    info["model"] = props.GetValueOrDefault("ro.product.model", "");
                    info["release"] = props.GetValueOrDefault("ro.build.version.release", "");
                    info["display"] = props.GetValueOrDefault("ro.build.display.id", "");
                    info["kernel"] = adb.KernelRaw();
                    info["slot"] = adb.ActiveSlot();
                    info["locked"] = adb.FlashLocked();
                }
            }
            else if (adb.FastbootDevices().Count > 0)
            {
                info["mode"] = "fastboot";
                info["serial"] = adb.FastbootDevices()[0];
            }
        }
        catch (Exception exc)
        {
            info["error"] = exc.Message;
        }
        return info;
    }

    private void ApplyProbe(Dictionary<string, object?> info)
    {
        _probing = false;
        RefreshBtn.IsEnabled = true;
        StateStore.SetDevice(new Dictionary<string, object?>
        {
            ["model"] = info.GetValueOrDefault("model"),
            ["kernel"] = info.GetValueOrDefault("kernel"),
            ["kver"] = (info.GetValueOrDefault("kernel") as string ?? "").Split('-')[0],
            ["slot"] = info.GetValueOrDefault("slot"),
            ["locked"] = info.GetValueOrDefault("locked"),
            ["release"] = info.GetValueOrDefault("release"),
            ["display"] = info.GetValueOrDefault("display"),
        });
        UpdateDeviceTiles(info);
        AutoFillRestoreSources();
        var mode = info.GetValueOrDefault("mode", "none")?.ToString() ?? "none";
        if (mode != _lastDeviceMode)
        {
            var transition = (_lastDeviceMode, mode) switch
            {
                ("none", "device") => "检测到设备已连接",
                ("device", "none") => "设备已断开",
                ("none", "fastboot") => "检测到设备进入 fastboot",
                ("fastboot", "device") => "设备已从 fastboot 重启回系统",
                ("device", "unauthorized") => "设备未授权：请在手机上允许 USB 调试",
                _ => "",
            };
            if (transition.Length > 0)
                AppendLog("note", transition);
            _lastDeviceMode = mode;
        }
        var modeText = mode switch
        {
            "device" => "设备已连接",
            "fastboot" => "设备在 fastboot",
            "unauthorized" => "设备未授权",
            "offline" => "设备离线",
            "none" => "未检测到设备",
            _ => mode,
        };
        ProgressText.Text = modeText;
    }

    public void AppendLog(string level, string message)
    {
        var entry = new LogEntry(DateTime.Now.ToString("HH:mm:ss"), message, level);
        _logEntries.Add(entry);
        LogList.Items.Add(entry);
        if (AutoScrollCheck.IsChecked == true)
            LogList.ScrollIntoView(entry);
        PersistLog(entry);
    }

    private void PersistLog(LogEntry entry)
    {
        try
        {
            if (_logWriter is null)
            {
                Directory.CreateDirectory(AppConfig.LogDir());
                _logWriter = new StreamWriter(
                    Path.Combine(AppConfig.LogDir(),
                        $"session_{DateTime.Now:yyyyMMdd_HHmmss}.log"),
                    append: false, encoding: System.Text.Encoding.UTF8);
            }
            _logWriter.WriteLine($"{entry.TimeText}  {entry.MessageText}");
            _logWriter.Flush();
        }
        catch
        {

        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
        LogList.Items.Clear();
    }

    private void OnSaveLogClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存日志",
            Filter = "日志文件 (*.log)|*.log|所有文件 (*.*)|*.*",
            FileName = $"oppo_toolbox_{DateTime.Now:MMdd_HHmmss}.log",
            InitialDirectory = AppConfig.LogDir(),
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllLines(dialog.FileName,
                _logEntries.Select(entry => $"{entry.TimeText}  {entry.MessageText}"));
            AppendLog("ok", $"日志已保存：{dialog.FileName}");
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WelcomeLog()
    {
    }

    private const string WelcomeFlag = "welcome.ok";

    private static string WelcomeFlagPath() => Path.Combine(AppConfig.AppDir(), WelcomeFlag);

    private static bool WelcomeShown()
    {
        try
        {
            return File.Exists(WelcomeFlagPath());
        }
        catch
        {
            return true;
        }
    }

    private static void MarkWelcomeShown()
    {
        try
        {
            File.WriteAllText(WelcomeFlagPath(), AppConfig.AppVersion);
        }
        catch
        {

        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;
        if (App.SelfTestMode || WelcomeShown())
            return;
        try
        {
            var dialog = new FirstRunWindow { Owner = this };
            if (dialog.ShowDialog() == true)
                MarkWelcomeShown();
        }
        catch (Exception exc)
        {
            AppendLog("warn", $"首次运行说明没有弹出来：{exc.Message}");
        }
    }

    private void AutoFillRestoreSources()
    {
        try
        {
            var vars = StageVars("restore");
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
            foreach (var (key, pick) in new[]
                     {
                         ("abl_file", abl),
                         ("ocdt_file", ocdt),
                     })
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
            if (changed && _currentSid == "restore")
                RenderFields(Workflow.StageById["restore"]);
        }
        catch
        {

        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F1:
                OnAboutClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F2:
                OnResourcesClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F5:
                RefreshDevice();
                e.Handled = true;
                break;
            case Key.R when Keyboard.Modifiers == ModifierKeys.Control:
                RunCurrent();
                e.Handled = true;
                break;
            case Key.Escape:
                StopCurrent();
                e.Handled = true;
                break;
            case Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7
                when Keyboard.Modifiers == ModifierKeys.Control:
            {
                var index = e.Key switch
                {
                    Key.D1 => 0, Key.D2 => 1, Key.D3 => 2, Key.D4 => 3,
                    Key.D5 => 4, Key.D6 => 5, Key.D7 => 6, _ => -1,
                };
                if (index >= 0 && index < Workflow.Stages.Length)
                    SelectStage(Workflow.Stages[index].Sid);
                e.Handled = true;
                break;
            }
        }
    }

    private void OnBackupDirClick(object sender, RoutedEventArgs e)
    {
        var path = AppConfig.BackupDir();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show(this, path, "备份目录", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void OnResourcesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new DownloadWindow(this) { Owner = this };
        dialog.ShowDialog();

        RenderFields(Workflow.StageById[_currentSid]);
    }

    public (string Summary, string Error) ApplyAutoFill(string resourceKey, string resultPath, bool unzip)
    {
        if (resourceKey.Contains("ksu"))
            return InstallApk(resultPath, "KernelSU 管理器");
        if (resourceKey.Contains("dfroot"))
            return InstallApk(resultPath, "DFRoot");

        var analysis = ResourceFill.Analyze(resourceKey, resultPath, unzip);
        var affectedStages = new List<string>();
        var summary = "";
        var fillError = "";

        if (analysis.BundleDir != null)
        {
            StageVars("temproot")["bundle"] = analysis.BundleDir;
            affectedStages.Add("temproot");
            summary = "已填入『临时 Root』的 bundle 目录";
        }
        else if (resourceKey.Contains("bundle"))
        {
            fillError = "没认出 bundle 目录（需要含 exploit_guard / ksud / su0），请手动选择";
        }

        if (analysis.Abl != null || analysis.Ocdt != null)
        {
            foreach (var sid in new[] { "push", "write" })
            {
                var vars = StageVars(sid);
                if (analysis.Abl != null) vars["abl"] = analysis.Abl;
                if (analysis.Ocdt != null) vars["ocdt"] = analysis.Ocdt;
                affectedStages.Add(sid);
            }
            var detail = (analysis.Abl != null && analysis.Ocdt != null)
                ? ""
                : analysis.Abl == null ? "（abl 未识别）" : "（ocdt 未识别）";
            summary = summary.Length > 0
                ? summary + "；已填入镜像" + detail
                : "已填入『推送解锁文件』和『写入解锁文件』的镜像路径" + detail;
        }
        else if (resourceKey.Contains("image") && fillError.Length == 0)
        {
            fillError = "没认出 abl*.img / ocdt*.img，请手动选择";
        }

        foreach (var sid in affectedStages.Distinct())
        {
            if (sid == _currentSid)
                RenderFields(Workflow.StageById[sid]);
        }
        return (summary, fillError);
    }

    public (string Summary, string Error) InstallApk(string apkPath, string what)
    {
        if (!File.Exists(apkPath))
            return ("", "apk 文件不存在：" + apkPath);
        var mode = AdbDefault.Instance.DeviceState();
        if (mode != "device")
        {
            var reason = mode switch
            {
                "none" => "没有检测到设备：插好数据线、打开 USB 调试后重试安装。",
                "unauthorized" => "设备未授权：请在手机上允许 USB 调试后重试。",
                "fastboot" => "设备在 fastboot 模式，先让手机正常开机再安装。",
                _ => $"设备状态是 {mode}，暂时装不了。",
            };
            return ("", reason);
        }
        var res = ProcessRunner.Run(AdbDefault.Instance.AdbArgv("install", "-r", apkPath),
            new RunOptions { TimeoutMs = 180000 });
        var tail = res.Tail(2);
        if (res.Ok || tail.Contains("Success"))
        {
            AppendLog("ok", $"{what} 已安装到手机。");
            return ($"已安装到手机", "");
        }
        AppendLog("err", $"安装失败：{tail}");
        return ("", $"安装失败：{tail}");
    }

    private static Brush TryBrush(string key) =>
        (Brush)(FindResourceGlobal(key) ?? Brushes.Transparent);

    private static object? FindResourceGlobal(string key) =>
        Application.Current?.TryFindResource(key);
}

public sealed class LogEntry
{
    public string TimeText { get; }
    public string MessageText { get; }
    public string Level { get; }
    public Brush Brush { get; }

    public LogEntry(string time, string message, string level)
    {
        TimeText = time;
        MessageText = message;
        Level = level;
        Brush = level switch
        {
            "cmd" => (Brush)Application.Current.TryFindResource("LvlCmd") ?? Brushes.DodgerBlue,
            "out" => (Brush)Application.Current.TryFindResource("LvlOut") ?? Brushes.Gray,
            "ok" => (Brush)Application.Current.TryFindResource("LvlOk") ?? Brushes.Green,
            "warn" => (Brush)Application.Current.TryFindResource("LvlWarn") ?? Brushes.DarkOrange,
            "err" => (Brush)Application.Current.TryFindResource("LvlErr") ?? Brushes.Red,
            "note" => (Brush)Application.Current.TryFindResource("LvlNote") ?? Brushes.Gray,
            _ => (Brush)Application.Current.TryFindResource("LvlInfo") ?? Brushes.Black,
        };
    }
}
