using System.Windows.Input;

namespace OppoUnlockToolbox.Mvvm;

/// <summary>
/// 把一个方法包成 ICommand。启用状态交给 WPF 的 RequerySuggested 自动重算，
/// 省得每个属性变更都手动通知一遍。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _run;
    private readonly Func<bool>? _canRun;

    public RelayCommand(Action run, Func<bool>? canRun = null)
    {
        _run = run;
        _canRun = canRun;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canRun?.Invoke() ?? true;

    public void Execute(object? parameter) => _run();
}
