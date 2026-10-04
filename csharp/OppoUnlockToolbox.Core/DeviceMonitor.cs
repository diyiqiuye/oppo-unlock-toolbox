using System.Diagnostics;

namespace OppoUnlockToolbox.Core;

public sealed class DeviceMonitor : IDisposable
{
    private readonly Action _onChange;
    private readonly CancellationTokenSource _cts = new();
    private Process? _proc;
    private Task? _loop;
    private DateTime _lastTrigger = DateTime.UtcNow - TimeSpan.FromSeconds(10);
    private readonly object _lock = new();

    public DeviceMonitor(Action onChange)
    {
        _onChange = onChange;
    }

    public void Start()
    {
        _loop = Task.Run(() => RunLoop(_cts.Token));
    }

    private async Task RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var adbPath = AppConfig.FindTool("adb");
            if (string.IsNullOrEmpty(adbPath))
            {
                try { await Task.Delay(5000, token); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = adbPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("track-devices");
                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("track-devices 启动失败");
                _proc = proc;
                proc.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;
                    Trigger();
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;
                    Trigger();
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {

            }
            finally
            {
                _proc = null;
            }
            try { await Task.Delay(5000, token); } catch (OperationCanceledException) { return; }
        }
    }

    private void Trigger()
    {
        var fire = false;
        lock (_lock)
        {
            if ((DateTime.UtcNow - _lastTrigger).TotalMilliseconds > 1200)
            {
                _lastTrigger = DateTime.UtcNow;
                fire = true;
            }
        }
        if (fire)
            _onChange?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        _cts.Dispose();
    }
}
