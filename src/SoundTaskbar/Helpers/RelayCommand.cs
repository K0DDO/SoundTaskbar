using System.Windows.Input;

namespace SoundTaskbar.Helpers;

public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task>? _executeAsync;
    private readonly Action<object?>? _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_execute is not null)
        {
            _execute(parameter);
            return;
        }

        if (_executeAsync is null)
        {
            return;
        }

        try
        {
            _isRunning = true;
            CommandManager.InvalidateRequerySuggested();
            await _executeAsync(parameter);
        }
        finally
        {
            _isRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
