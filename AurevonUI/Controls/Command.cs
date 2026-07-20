namespace AurevonUI;

public interface ICommand
{
    bool CanExecute(object? parameter = null);
    void Execute(object? parameter = null);
    event EventHandler? CanExecuteChanged;
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _can_execute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _can_execute = canExecute;
    }

    public bool CanExecute(object? parameter = null) => _can_execute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter = null)
    {
        if (CanExecute(parameter))
            _execute(parameter);
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
