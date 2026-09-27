namespace ForzaHaptics.Gui.Services;

public enum UnsavedChangesDecision
{
    Save,
    Discard,
    Cancel,
}

/// <summary>UI-neutral modal interactions requested by the main view model.</summary>
public interface IUserDialogService
{
    Task<string?> RequestTextAsync(
        string title,
        string prompt,
        string initialValue,
        CancellationToken cancellationToken = default);

    Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(
        string profileName,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmProfileDeletionAsync(
        string profileName,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmHidMaestroInstallationAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    Task<bool> ConfirmAdministratorRestartAsync(CancellationToken cancellationToken = default);

    Task ShowWarningAsync(string message, CancellationToken cancellationToken = default);
    Task ShowErrorAsync(string message, CancellationToken cancellationToken = default);
}
