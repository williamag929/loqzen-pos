using System;
using System.Security.Cryptography;
using System.Text;

namespace Core.Application.Security;

/// <summary>
/// Salted PBKDF2-SHA256 for numeric PINs. Format: <c>pbkdf2$iterations$saltBase64$hashBase64</c>.
/// PIN space is small (4-6 digits), so this only protects against casual reading of the DB file;
/// uniqueness among active users is enforced by the user service so PIN-only login is unambiguous.
/// </summary>
public static class PinHasher
{
    public const int MinLength = 4;
    public const int MaxLength = 6;
    private const int Iterations = 50_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static bool IsValidPin(string? pin) =>
        !string.IsNullOrEmpty(pin) && pin.Length >= MinLength && pin.Length <= MaxLength && pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        if (!IsValidPin(pin))
            throw new ArgumentException($"El PIN debe tener entre {MinLength} y {MaxLength} dígitos.", nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? pin, string? stored)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations)) return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
