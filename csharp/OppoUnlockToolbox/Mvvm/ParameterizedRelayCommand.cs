using System.Windows.Input;

namespace OppoUnlockToolbox.Mvvm;

/// <summary>带参数的 ICommand（快捷键 Ctrl+1..7 用它传阶段 id）。</summary>
public sealed class ParameterizedRelayCommand : ICommand
{
    private readonly Action<object?> _run;

    public ParameterizedRelayCommand(Action<object?> run) => _run = run;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _run(parameter);
}
