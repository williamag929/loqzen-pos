using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.DTOs;
using Core.Application.Receipts;
using Core.Application.Security;
using Core.Application.Services;
using Core.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>
/// Manager PIN modal shared by every screen (singleton). <see cref="RequestAsync"/> resolves at once when
/// no approval is needed or the signed-in user is a manager; otherwise it opens the dialog and awaits it.
/// Services are resolved per call from a fresh scope (DbContext is not thread-safe).
/// </summary>
public partial class ManagerAuthPromptViewModel : ObservableObject, IManagerAuthPrompt
{
    private const string LockScope = "manager";

    private readonly IServiceScopeFactory _scopes;
    private readonly IUserSession _session;
    private readonly IPinAttemptLimiter _limiter;
    private readonly DispatcherTimer _lockTimer;
    private TaskCompletionSource<AuthorizationOutcome>? _pending;
    private AuthorizationRequest? _request;

    public ManagerAuthPromptViewModel(IServiceScopeFactory scopes, IUserSession session, IPinAttemptLimiter limiter)
    {
        _scopes = scopes;
        _session = session;
        _limiter = limiter;

        // 1 s countdown while approvals are locked (independent from the login lock: own scope).
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockTimer.Tick += (_, _) => UpdateLockout();
    }

    /// <summary>Too many wrong manager PINs: numpad and "Autorizar" are disabled until the countdown ends ("Cancelar" stays).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled))]
    private bool _isLocked;
    public bool IsInputEnabled => !IsLocked;

    private void UpdateLockout()
    {
        var left = _limiter.SecondsLocked(LockScope);
        if (left > 0)
        {
            IsLocked = true;
            Pin = string.Empty;
            ErrorMessage = $"🔒 Demasiados intentos. Espere {left} s";
            if (!_lockTimer.IsEnabled) _lockTimer.Start();
        }
        else if (IsLocked)
        {
            _lockTimer.Stop();
            IsLocked = false;
            if (IsOpen) ErrorMessage = string.Empty;
        }
    }

    public string Title => "🔐 Autorización de gerente";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _actionLabel = string.Empty;
    [ObservableProperty] private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAmount))]
    private string _amountText = string.Empty;
    public bool HasAmount => !string.IsNullOrEmpty(AmountText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinDots))]
    private string _pin = string.Empty;
    public string PinDots => new('●', Pin.Length);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty] private bool _isBusy;

    public async Task<AuthorizationOutcome> RequestAsync(AuthorizedAction action, string description, decimal? amount = null, string? reference = null)
    {
        var user = _session.CurrentUser;
        var request = new AuthorizationRequest(action, description, amount, reference, user?.Id, user?.Name);

        using (var scope = _scopes.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IManagerAuthorizationService>();
            if (!svc.RequiresAuthorization(action)) return AuthorizationOutcome.NotRequired;

            // A signed-in manager approves their own action: record it, no dialog.
            if (user is { CanAuthorize: true })
            {
                try
                {
                    return new AuthorizationOutcome(true, await svc.AuthorizeAsUserAsync(request, user.Id));
                }
                catch (UnauthorizedAccessException)
                {
                    // Role changed since sign-in: fall back to asking for a PIN.
                }
            }
        }

        if (IsOpen) return AuthorizationOutcome.Denied; // one approval at a time

        _request = request;
        ActionLabel = UserPermissions.Label(action);
        Description = description;
        AmountText = amount is { } a ? ReceiptFormatter.Cop(a) : string.Empty;
        Pin = string.Empty;
        ErrorMessage = string.Empty;
        IsBusy = false;
        _pending = new TaskCompletionSource<AuthorizationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsOpen = true;
        UpdateLockout(); // a lock from an earlier dialog is still running: show the countdown right away
        return await _pending.Task;
    }

    [RelayCommand]
    private void AppendDigit(string digit)
    {
        if (IsBusy || IsLocked || string.IsNullOrEmpty(digit)) return;
        ErrorMessage = string.Empty;
        foreach (var ch in digit)
        {
            if (Pin.Length >= PinHasher.MaxLength) break;
            if (char.IsAsciiDigit(ch)) Pin += ch;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Pin = string.Empty;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (IsBusy || IsLocked || _request == null || _pending == null) return;
        if (!PinHasher.IsValidPin(Pin))
        {
            ErrorMessage = $"El PIN debe tener entre {PinHasher.MinLength} y {PinHasher.MaxLength} dígitos.";
            return;
        }

        IsBusy = true;
        try
        {
            ManagerOverrideDto result;
            using (var scope = _scopes.CreateScope())
                result = await scope.ServiceProvider.GetRequiredService<IManagerAuthorizationService>()
                    .AuthorizeWithPinAsync(_request, Pin);

            Complete(new AuthorizationOutcome(true, result));
        }
        catch (PinLockedException)
        {
            UpdateLockout();
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(ex.Message) ? "PIN incorrecto o sin permiso para autorizar." : ex.Message;
            Pin = string.Empty;
            UpdateLockout(); // the failing attempt may have been the one that triggered the lock
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
            Pin = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Complete(AuthorizationOutcome.Denied);

    private void Complete(AuthorizationOutcome outcome)
    {
        var tcs = _pending;
        _pending = null;
        _request = null;
        IsOpen = false;
        Pin = string.Empty;
        ErrorMessage = string.Empty;
        _lockTimer.Stop(); // countdown resumes (from the limiter's clock) when the dialog opens again
        tcs?.TrySetResult(outcome);
    }
}
