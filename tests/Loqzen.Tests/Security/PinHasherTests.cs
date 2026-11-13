using Core.Application.Security;
using FluentAssertions;

namespace Loqzen.Tests.Security;

/// <summary>PIN format rules and salted PBKDF2 hashing/verification.</summary>
public class PinHasherTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("123456", true)]
    [InlineData("0000", true)]
    [InlineData("123", false)]
    [InlineData("1234567", false)]
    [InlineData("12a4", false)]
    [InlineData("12 4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidPin_RequiresFourToSixAsciiDigits(string? pin, bool expected) =>
        PinHasher.IsValidPin(pin).Should().Be(expected);

    [Fact]
    public void Hash_HasExpectedFormat()
    {
        var stored = PinHasher.Hash("1234");
        var parts = stored.Split('$');
        parts.Should().HaveCount(4);
        parts[0].Should().Be("pbkdf2");
        int.Parse(parts[1]).Should().BePositive();
        var salt = Convert.FromBase64String(parts[2]);
        var hash = Convert.FromBase64String(parts[3]);
        salt.Should().NotBeEmpty();
        hash.Should().NotBeEmpty();
        stored.Should().NotContain("1234", "the PIN must never be stored in clear text");
    }

    [Fact]
    public void Hash_SamePin_ProducesDifferentStrings_AndBothVerify()
    {
        var a = PinHasher.Hash("1234");
        var b = PinHasher.Hash("1234");

        a.Should().NotBe(b, "salt is random per hash");
        PinHasher.Verify("1234", a).Should().BeTrue();
        PinHasher.Verify("1234", b).Should().BeTrue();
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("abcd")]
    [InlineData(null)]
    public void Hash_InvalidPin_Throws(string? pin)
    {
        var act = () => PinHasher.Hash(pin!);
        act.Should().Throw<ArgumentException>().WithMessage("*PIN*");
    }

    [Fact]
    public void Verify_WrongPin_IsFalse()
    {
        var stored = PinHasher.Hash("1234");
        PinHasher.Verify("1235", stored).Should().BeFalse();
        PinHasher.Verify("12345", stored).Should().BeFalse("longer PIN with same prefix is a different PIN");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2$50000$onlythree")]
    [InlineData("md5$50000$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2$50000$***notbase64***$aGFzaA==")]
    [InlineData("pbkdf2$50000$c2FsdA==$***notbase64***")]
    public void Verify_MalformedStoredValue_IsFalse_NoThrow(string stored)
    {
        var act = () => PinHasher.Verify("1234", stored);
        act.Should().NotThrow().Which.Should().BeFalse();
    }

    [Fact]
    public void Verify_NullInputs_AreFalse()
    {
        var stored = PinHasher.Hash("1234");
        PinHasher.Verify(null, stored).Should().BeFalse();
        PinHasher.Verify("1234", null).Should().BeFalse();
        PinHasher.Verify(null, null).Should().BeFalse();
    }
}
