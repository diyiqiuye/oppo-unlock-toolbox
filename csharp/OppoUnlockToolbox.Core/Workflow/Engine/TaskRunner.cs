using System.Collections.Concurrent;

// 从 Workflow.cs / TaskEngine.cs 按成员边界拆出，逻辑与拆分前逐字一致。
namespace OppoUnlockToolbox.Core.Workflow;

public sealed class TaskRunner
{
    private readonly List<Step> _steps;
    private readonly ConcurrentQueue<TaskEvent> _queue = new();
    private readonly CancellationTokenSource _cancelSource = new();

    public bool? Ok { get; private set; }
    public string Summary { get; private set; } = "";
    public Task? Background { get; private set; }

    public TaskRunner(IEnumerable<Step> steps, Dictionary<string, object?>? parameters = null)
    {
        _steps = steps.ToList();
        Params = parameters ?? new Dictionary<string, object?>();
    }

    public Dictionary<string, object?> Params { get; }
    public bool IsAlive => Background is { IsCompleted: false };
    public CancellationToken Cancel => _cancelSource.Token;

    public Action<TaskEvent>? OnEvent { get; set; }

    public void Emit(TaskEvent evt)
    {
        _queue.Enqueue(evt);
        OnEvent?.Invoke(evt);
    }

    public List<TaskEvent> Drain()
    {
        var items = new List<TaskEvent>();
        while (_queue.TryDequeue(out var evt))
            items.Add(evt);
        return items;
    }

    public void Stop() => _cancelSource.Cancel();

    public void Start()
    {
        Background = Task.Run(() =>
        {
            var total = Math.Max(1, _steps.Count);
            var ctx = new Ctx(Emit, _cancelSource, Params);
            var failure = "";
            try
            {
                for (var index = 0; index < _steps.Count; index++)
                {
                    var item = _steps[index];
                    if (_cancelSource.IsCancellationRequested)
                    {
                        failure = "已中止";
                        break;
                    }
                    Emit(TaskEvent.StepStartE(index, item.Title));
                    try
                    {
                        var message = item.Fn(ctx) ?? "";
                        Emit(TaskEvent.StepOkE(index, message));
                        Emit(TaskEvent.ProgressE((index + 1) / (double)total));
                    }
                    catch (CancelledException)
                    {
                        Emit(TaskEvent.StepFailE(index, "已中止"));
                        failure = "已中止";
                        break;
                    }
                    catch (StepFailException exc)
                    {
                        Emit(TaskEvent.StepFailE(index, exc.Message));
                        failure = exc.Message;
                        break;
                    }
                    catch (Exception exc)
                    {

                        Emit(TaskEvent.StepFailE(index, $"内部错误：{exc.GetType().Name}: {exc.Message}"));
                        failure = $"内部错误：{exc.Message}";
                        break;
                    }
                }
            }
            finally
            {
                Ok = failure.Length == 0;
                Summary = failure.Length > 0 ? failure : "全部步骤完成";
                Emit(TaskEvent.DoneE(Ok.Value, Summary));
            }
        });
    }

    public void Join() => Background?.Wait();
}

public static class TaskSync
{
    public static TaskRunner RunSync(IEnumerable<Step> steps, Dictionary<string, object?>? parameters = null,
        Action<TaskEvent>? onEvent = null)
    {
        var runner = new TaskRunner(steps, parameters);
        if (onEvent != null)
            runner.OnEvent = onEvent;
        runner.Start();
        runner.Join();
        return runner;
    }
}
