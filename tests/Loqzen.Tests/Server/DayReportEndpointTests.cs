using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Core.Application.Services;
using Core.Domain.Enums;
using FluentAssertions;
using Loqzen.Tests.Fixtures;

namespace Loqzen.Tests.Server;

/// <summary>
/// GET /api/reports/day: the consolidated "cierre del día" across registers. Default = today in the server's
/// local zone (from the injected clock), <c>?date=YYYY-MM-DD</c> picks a day, anything else is a 400.
/// </summary>
public class DayReportEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private DateOnly ClockLocalDay => DateOnly.FromDateTime(_fx.Clock.GetLocalNow().DateTime);

    private static readonly string[] TopLevelKeys =
    {
        "day", "fromUtc", "toUtc", "terminals", "orderCount", "cashSales", "cardSales", "transferSales", "otherSales",
        "totalSales", "billPaymentsTotal", "billPaymentCommission", "difference", "openShiftCount"
    };

    private static readonly string[] TerminalKeys =
    {
        "terminalId", "terminalCode", "terminalName", "shiftCount", "openShiftCount", "orderCount", "cashSales", "cardSales",
        "transferSales", "otherSales", "totalSales", "billPaymentCount", "billPaymentsTotal", "billPaymentCommission",
        "cashIn", "cashOut", "difference", "shifts"
    };

    [Fact]
    public async Task Default_IsTodayInTheServersLocalZone_AndEmptyWithoutShifts()
    {
        var response = await _fx.Client.GetAsync("/api/reports/day");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["day"]!.GetValue<string>().Should().Be(ClockLocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        body["terminals"]!.AsArray().Should().BeEmpty();
        body["totalSales"]!.GetValue<decimal>().Should().Be(0m);
        body["orderCount"]!.GetValue<int>().Should().Be(0);
        body["openShiftCount"]!.GetValue<int>().Should().Be(0);
        body.AsObject().Select(kv => kv.Key).Should().BeEquivalentTo(TopLevelKeys);

        // fromUtc/toUtc cover exactly 24 h (DST aside) and contain the clock instant.
        var from = DateTime.Parse(body["fromUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var to = DateTime.Parse(body["toUtc"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var now = _fx.Clock.GetUtcNow().UtcDateTime;
        from.Should().BeOnOrBefore(now);
        to.Should().BeAfter(now);
        (to - from).Should().BeGreaterThanOrEqualTo(TimeSpan.FromHours(23)).And.BeLessThanOrEqualTo(TimeSpan.FromHours(25));
    }

    [Fact]
    public async Task ExplicitDate_HasTheDocumentedShape()
    {
        var response = await _fx.Client.GetAsync("/api/reports/day?date=2026-11-13");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await _fx.ReadJsonAsync(response);
        body["day"]!.GetValue<string>().Should().Be("2026-11-13");
        body["terminals"]!.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.Array);
        body.AsObject().Select(kv => kv.Key).Should().BeEquivalentTo(TopLevelKeys);
        body["fromUtc"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        body["toUtc"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task WithActivityOnTheMaster_ListsCaja01_WithItsSalesAndShift()
    {
        // Open a shift on the master (through the service, so it is stamped CAJA-01) and register a cash + card sale.
        var shift = await _fx.WithServiceAsync<ICashShiftService, Core.Domain.Entities.CashShift>(s => s.OpenShiftAsync("Cajero Master", 100000m, null));
        await _fx.WithDbAsync(db =>
        {
            CashOpsData.AddSale(db, shift.Id, PaymentMethod.Cash, 25000m);
            CashOpsData.AddSale(db, shift.Id, PaymentMethod.Card, 40000m);
            return Task.CompletedTask;
        });

        var body = await _fx.GetJsonAsync($"/api/reports/day?date={ClockLocalDay:yyyy-MM-dd}");

        var terminals = body["terminals"]!.AsArray();
        terminals.Should().ContainSingle();
        var t = terminals[0]!;
        t.AsObject().Select(kv => kv.Key).Should().BeEquivalentTo(TerminalKeys);
        t["terminalCode"]!.GetValue<string>().Should().Be("CAJA-01");
        t["terminalName"]!.GetValue<string>().Should().Be(ServerFixture.MasterName);
        t["terminalId"]!.GetValue<Guid>().Should().Be(_fx.Terminal.Id);
        t["shiftCount"]!.GetValue<int>().Should().Be(1);
        t["openShiftCount"]!.GetValue<int>().Should().Be(1);
        t["orderCount"]!.GetValue<int>().Should().Be(2);
        t["cashSales"]!.GetValue<decimal>().Should().Be(25000m);
        t["cardSales"]!.GetValue<decimal>().Should().Be(40000m);
        t["totalSales"]!.GetValue<decimal>().Should().Be(65000m);
        var shifts = t["shifts"]!.AsArray();
        shifts.Should().ContainSingle();
        shifts[0]!["shiftId"]!.GetValue<Guid>().Should().Be(shift.Id);
        shifts[0]!["shiftNumber"]!.GetValue<int>().Should().Be(shift.ShiftNumber);
        shifts[0]!["terminalCode"]!.GetValue<string>().Should().Be("CAJA-01");

        body["totalSales"]!.GetValue<decimal>().Should().Be(65000m);
        body["cashSales"]!.GetValue<decimal>().Should().Be(25000m);
        body["orderCount"]!.GetValue<int>().Should().Be(2);
        body["openShiftCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ADayWithoutShifts_IsEmpty_EvenWhenOtherDaysHaveActivity()
    {
        var shift = await _fx.WithServiceAsync<ICashShiftService, Core.Domain.Entities.CashShift>(s => s.OpenShiftAsync("Cajero Master", 0m, null));
        await _fx.WithDbAsync(db => { CashOpsData.AddSale(db, shift.Id, PaymentMethod.Cash, 1000m); return Task.CompletedTask; });

        var body = await _fx.GetJsonAsync($"/api/reports/day?date={ClockLocalDay.AddDays(-30):yyyy-MM-dd}");

        body["terminals"]!.AsArray().Should().BeEmpty();
        body["totalSales"]!.GetValue<decimal>().Should().Be(0m);
    }

    [Theory]
    [InlineData("13-11-2026")]
    [InlineData("2026/11/13")]
    [InlineData("hoy")]
    [InlineData("2026-13-01")]
    [InlineData("20261113")]
    public async Task BadDate_Returns400_WithErrorDto(string date)
    {
        var response = await _fx.Client.GetAsync("/api/reports/day?date=" + Uri.EscapeDataString(date));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, date);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("AAAA-MM-DD");
    }

    [Fact]
    public async Task DateWithSurroundingSpaces_IsAccepted()
    {
        var response = await _fx.Client.GetAsync("/api/reports/day?date=" + Uri.EscapeDataString(" 2026-11-13 "));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["day"]!.GetValue<string>().Should().Be("2026-11-13");
    }
}
