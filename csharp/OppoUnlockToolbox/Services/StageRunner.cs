using System.Windows;
using System.Windows.Threading;

namespace OppoUnlockToolbox.Services;

/// <summary>
/// 把正式任务包成后台 TaskRunner 跑，事件统一回到 UI 线程再抛出去。
/// 替代原来 MainWindow 里 90ms 轮询 _runner.Drain() 的做法。
/// </summary>
public sealed class StageRunner
{
    private TaskRunner? _runner;

    public bool IsRunning => _runner is { IsAlive: true };

    /// <summary>步骤事件（已在 UI 线程）。</summary>
    public event Action<TaskEvent>? Event;

    public void Start(IEnumerable<Step> steps, Dictionary<string, object?> parameters)
    {
        var runner = new TaskRunner(steps, parameters)
        {
            OnEvent = evt => Dispatch(() => Event?.Invoke(evt)),
        };
        _runner = runner;
        runner.Start();
    }

    public void Stop() => _runner?.Stop();

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
