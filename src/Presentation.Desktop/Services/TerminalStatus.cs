using System;
using Avalonia.Threading;

namespace Presentation.Desktop.Services;

/// <summary>
/// Start-up outcome of this register's registration (ITerminalService.EnsureRegisteredAsync), set by
/// <see cref="App"/> and read by the 🖥️ nav-bar chip. A failed registration never prevents the POS from
/// opening: the error is shown here instead. Also carries the "Satellite without MasterUrl" config warning.
/// </summary>
public sealed class TerminalStatus
{
    public bool IsRegistered { get; private set; }
    public string? RegistrationError { get; private set; }

    /// <summary>Persistent configuration problem ("Falta Terminal:MasterUrl"); null when the config is usable.</summary>
    public string? ConfigError { get; private set; }

    public bool HasError => RegistrationError != null || ConfigError != null;

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Changed;

    public void MarkRegistered()
    {
        IsRegistered = true;
        RegistrationError = null;
        RaiseChanged();
    }

    public void MarkRegistrationFailed(string error)
    {
        IsRegistered = false;
        RegistrationError = error;
        RaiseChanged();
    }

    public void SetConfigError(string? error)
    {
        ConfigError = error;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke();
        else Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }
}
