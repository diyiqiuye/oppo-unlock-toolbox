using System.Windows;
using System.Windows.Threading;

namespace OppoUnlockToolbox.Services;

/// <summary>
/// 盯着设备的插拔（adb track-devices），抖一下之后自动重新探测。
/// 正式任务跑着的时候不打扰设备——那时候 adb 正被任务占着。
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly DeviceMonitor _monitor;
    private readonly DispatcherTimer _debounce;
    private bool _probing;
    private bool _armed;

    public DeviceWatcher()
    {
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };

        _monitor = new DeviceMonitor(() => Dispatch(() =>
        {
            if (!_armed || _probing || Blocked())
                return;
            _debounce.Stop();
            _debounce.Start();
        }));
    }

    /// <summary>返回 true 表示现在不该探测（例如有阶段正在跑）。</summary>
    public Func<bool>? IsBlocked { get; set; }

    public bool IsProbing => _probing;

    /// <summary>探测结果（已在 UI 线程）。</summary>
    public event Action<DeviceSnapshot>? Probed;

    /// <summary>状态栏文案（“正在检测设备…”之类）。</summary>
    public event Action<string>? Status;

    public void Start()
    {
        _armed = true;
        _monitor.Start();
    }

    public void Refresh()
    {
        if (_probing)
            return;
        if (Blocked())
        {
            Status?.Invoke("任务执行中，稍后再检测设备");
            return;
        }
        _probing = true;
        Status?.Invoke("正在检测设备…");
        Task.Run(() =>
        {
            var snapshot = DeviceProbe.Probe();
            Dispatch(() =>
            {
                _probing = false;
                Probed?.Invoke(snapshot);
            });
        });
    }

    private bool Blocked() => IsBlocked?.Invoke() ?? false;

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _armed = false;
        _monitor.Dispose();
        _debounce.Stop();
    }
}
