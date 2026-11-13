using System;
using Core.Application.DTOs;

namespace Core.Application.Services;

/// <summary>Who is using this terminal right now (one session per app instance).</summary>
public interface IUserSession
{
    UserDto? CurrentUser { get; }
    bool IsLoggedIn { get; }
    event Action? Changed;
    void SignIn(UserDto user);
    void SignOut();
}
