using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Services;

/// <summary>
/// Integration tests for CashShiftService with in-memory database.
/// </summary>
public class CashShiftServiceTests : IDisposable
{
    private readonly PosDbContext _db;
    private readonly CashShiftService _service;

    public CashShiftServiceTests()
    {
        _db = TestDbContextFactory.Create();
        _service = new CashShiftService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task OpenShiftAsync_CreatesNewShift()
    {
        var shift = await _service.OpenShiftAsync("Juan Pérez", 100.00m);

        shift.Should().NotBeNull();
        shift.CashierName.Should().Be("Juan Pérez");
        shift.OpeningAmount.Should().Be(100.00m);
        shift.Status.Should().Be(CashShiftStatus.Open);
        shift.ShiftNumber.Should().Be(1);
    }

    [Fact]
    public async Task OpenShiftAsync_IncrementsShiftNumber()
    {
        await _service.OpenShiftAsync("Cajero 1", 50.00m);
        await _service.CloseShiftAsync(50.00m);

        var shift2 = await _service.OpenShiftAsync("Cajero 2", 100.00m);

        shift2.ShiftNumber.Should().Be(2);
    }

    [Fact]
    public async Task OpenShiftAsync_ThrowsWhenShiftAlreadyOpen()
    {
        await _service.OpenShiftAsync("Cajero", 100.00m);

        var act = () => _service.OpenShiftAsync("Otro Cajero", 50.00m);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*turno abierto*");
    }

    [Fact]
    public async Task GetCurrentShiftAsync_ReturnsOpenShift()
    {
        await _service.OpenShiftAsync("Cajero", 100.00m);

        var current = await _service.GetCurrentShiftAsync();

        current.Should().NotBeNull();
        current!.Status.Should().Be(CashShiftStatus.Open);
    }

    [Fact]
    public async Task GetCurrentShiftAsync_ReturnsNullWhenNoOpenShift()
    {
        var current = await _service.GetCurrentShiftAsync();
        current.Should().BeNull();
    }
}
