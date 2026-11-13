using System;

namespace Presentation.Desktop.Services;

/// <summary>
/// Start-up outcome of the embedded LAN server, set by <see cref="App"/> and read by the nav-bar chip.
/// Kept apart from <c>LoqzenServer</c> so the UI can tell "still starting" from "failed (port in use)".
/// </summary>
public sealed class LanServerStatus
{
    public bool IsRunning { get; private set; }
    public bool HasFailed { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised on the UI thread (App awaits the start on it).</summary>
    public event Action? Changed;

    public void MarkRunning()
    {
        IsRunning = true;
        HasFailed = false;
        Error = null;
        Changed?.Invoke();
    }

    public void MarkFailed(string error)
    {
        IsRunning = false;
        HasFailed = true;
        Error = error;
        Changed?.Invoke();
    }
}
