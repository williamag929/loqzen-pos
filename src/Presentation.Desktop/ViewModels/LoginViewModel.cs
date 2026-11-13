using System;
using System.Linq;
using System.Threading.Tasks;
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

    private readonly IServiceScopeFactory _scopes;
    private readonly UserSession _session;

    public LoginViewModel(IServiceScopeFactory scopes, UserSession session)
    {
        _scopes = scopes;
        _session = session;
        _session.Changed += () =>
        {
            if (_session.IsLoggedIn) return;
            Pin = string.Empty;
            ErrorMessage = string.Empty;
            _ = RefreshHintAsync();
        };
        _ = RefreshHintAsync();
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
        if (IsBusy || string.IsNullOrEmpty(digit)) return;
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
        if (IsBusy) return;
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
                return;
            }

            Pin = string.Empty;
            ErrorMessage = string.Empty;
            _session.SignIn(user);
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
