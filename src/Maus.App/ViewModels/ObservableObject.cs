using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Maus.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Commande asynchrone qui se désactive pendant son exécution. Une erreur imprévue dans l'action d'un bouton est notée dans
/// le journal et expliquée, mais ne ferme plus tout MAUS : seule cette action a échoué (avant le 10/10/2026, elle remontait
/// jusqu'au filet de sécurité de l'application, qui arrête MAUS).
/// </summary>
public sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException))
        {
            Maus.Core.Diagnostics.Breadcrumbs.Add("action d'un bouton en erreur (" + ex.GetType().Name + ")");
            var log = Maus.Core.Diagnostics.CrashLog.Write(ex, "Erreur dans l'action d'un bouton : MAUS continue");
            System.Windows.MessageBox.Show(
                Maus.Core.Localization.Texts.T("Cette action n'a pas pu se terminer à cause d'une erreur inattendue. MAUS continue de fonctionner.") +
                "\n\n" + ex.Message +
                (log is null ? string.Empty : "\n\n" + Maus.Core.Localization.Texts.T("Détails enregistrés dans :") + "\n" + log),
                Maus.Core.Localization.Texts.T("MAUS — erreur"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            _running = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Commande immédiate qui reçoit le paramètre du bouton (navigation).</summary>
public sealed class ParameterCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
