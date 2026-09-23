using System.Windows.Input;

namespace FotoArchiv.App.Common;

public sealed class AsyncRelayCommand(
    Func<CancellationToken, Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private bool _isRunning;
    private CancellationTokenSource? _cancellation;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        _cancellation = new CancellationTokenSource();
        NotifyCanExecuteChanged();

        try
        {
            await execute(_cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            onError?.Invoke(exception);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
