using System.Windows.Input;

namespace Zara.Desktop.Commands;

/// <summary>
/// Runs one asynchronous UI action at a time and reports failures to the owning view model.
/// </summary>
internal sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception> _reportFailure;
    private bool _isExecuting;

    internal AsyncCommand(
        Func<Task> execute,
        Action<Exception> reportFailure,
        Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        NotifyCanExecuteChanged();

        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _reportFailure(exception);
        }
        finally
        {
            _isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    internal void NotifyCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
