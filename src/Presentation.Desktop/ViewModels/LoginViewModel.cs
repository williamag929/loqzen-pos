using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Application.Security;
using Core.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Desktop.Services;

namespace Presentation.Desktop.ViewModels;

/// <summary>Full-screen PIN login. Signs the user into the shared <see cref="UserSession"/>.</summary>
public partial class LoginViewModel : ObservableObject
{
    private static readonly string[] SeededNames = { "Administrador", "Cajero", "Mesero" }; // must match DbInitializer

    private const string LockScope = "login";

    private readonly IServiceScopeFactory _scopes;
    private readonly UserSession _session;
    private readonly IPinAttemptLimiter _limiter;
    private readonly DispatcherTimer _lockTimer;

    public LoginViewModel(IServiceScopeFactory scopes, UserSession session, IPinAttemptLimiter limiter)
    {
        _scopes = scopes;
        _session = session;
        _limiter = limiter;

        // 1 s countdown while the PIN entry is locked; re-enables input when the limiter says 0.
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockTimer.Tick += (_, _) => UpdateLockout();

        _session.Changed += () =>
        {
            if (_session.IsLoggedIn) return;
            Pin = string.Empty;
            ErrorMessage = string.Empty;
            UpdateLockout(); // "Cambiar usuario" while locked: keep showing the countdown
            _ = RefreshHintAsync();
        };
        UpdateLockout();
        _ = RefreshHintAsync();
    }

    /// <summary>Too many wrong PINs: numpad and "Entrar" are disabled until the countdown ends.</summary>
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
            ErrorMessage = string.Empty;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinDots))]
    private string _pin = string.Empty;
    public string PinDots => new('●', Pin.Length);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty] private bool _isBusy;

    /// <summary>True only while the user table still holds exactly the three seeded demo users.</summary>
    [ObservableProperty] private bool _showDefaultPinHint;
    public string DefaultPinHint => "PINs por defecto: Admin 1234 · Cajero 1111 · Mesero 2222";

    public async Task RefreshHintAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var users = await scope.ServiceProvider.GetRequiredService<IUserService>().GetAllAsync();
            var names = users.Select(u => u.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            ShowDefaultPinHint = names.Length == SeededNames.Length
                                 && names.SequenceEqual(SeededNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            ShowDefaultPinHint = false;
        }
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
    private async Task LoginAsync()
    {
        if (IsBusy || IsLocked) return;
        if (!PinHasher.IsValidPin(Pin))
        {
            ErrorMessage = $"Ingrese un PIN de {PinHasher.MinLength} a {PinHasher.MaxLength} dígitos.";
            return;
        }

        IsBusy = true;
        try
        {
            Core.Application.DTOs.UserDto? user;
            using (var scope = _scopes.CreateScope())
                user = await scope.ServiceProvider.GetRequiredService<IUserService>().AuthenticateByPinAsync(Pin);

            if (user == null)
            {
                ErrorMessage = "PIN incorrecto";
                Pin = string.Empty;
                UpdateLockout(); // the failing attempt may have been the one that triggered the lock
                return;
            }

            Pin = string.Empty;
            ErrorMessage = string.Empty;
            _session.SignIn(user);
        }
        catch (PinLockedException)
        {
            UpdateLockout();
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
}
