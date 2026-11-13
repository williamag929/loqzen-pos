using System.Net;
using System.Text.Json.Nodes;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Enums;
using FluentAssertions;

namespace Loqzen.Tests.Server;

/// <summary>
/// Datáfono registry routes for the comandera tablet: list, hand to a waiter / back to the register, recent charges,
/// and the feature flag (PaymentTerminalSettings.Enabled = false → 404 "Datáfonos desactivados." everywhere).
/// </summary>
public class PaymentTerminalEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private async Task<JsonNode> SeededMockTerminalAsync()
    {
        var list = (await _fx.GetJsonAsync("/api/payment-terminals")).AsArray();
        return list.Single(t => t!["name"]!.GetValue<string>() == "Datáfono simulado")!;
    }

    [Fact]
    public async Task List_ReturnsSeededMockTerminal_WithLabels()
    {
        var response = await _fx.Client.GetAsync("/api/payment-terminals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = (await _fx.ReadJsonAsync(response)).AsArray();
        list.Should().ContainSingle("DbInitializer seeds exactly one simulated terminal");

        var t = list[0]!;
        t["id"]!.GetValue<Guid>().Should().NotBeEmpty();
        t["name"]!.GetValue<string>().Should().Be("Datáfono simulado");
        t["provider"]!.GetValue<string>().Should().Be("Mock");
        t["providerLabel"]!.GetValue<string>().Should().Be("Simulado");
        t["assignedUserName"].Should().BeNull();
        t["assignmentLabel"]!.GetValue<string>().Should().Be("En caja");
        t["lastPingOk"].Should().BeNull();
        t["lastSeenAt"].Should().BeNull();
    }

    [Fact]
    public async Task List_ExcludesInactiveTerminals()
    {
        var inactive = await _fx.WithServiceAsync<IPaymentTerminalService, PaymentTerminalDto>(s =>
            s.SaveAsync(new SavePaymentTerminalRequest(null, "Datáfono apagado", PaymentTerminalProvider.Mock, null, null, IsActive: false, 5)));

        var names = (await _fx.GetJsonAsync("/api/payment-terminals")).AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

        names.Should().Contain("Datáfono simulado").And.NotContain("Datáfono apagado");
        inactive.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Assign_ToWaiter_ThenEmptyBody_BackToRegister()
    {
        var id = (await SeededMockTerminalAsync())["id"]!.GetValue<Guid>();

        var assign = await _fx.PostJsonAsync($"/api/payment-terminals/{id}/assign", new { userName = "Ana" });
        assign.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await _fx.ReadJsonAsync(assign);
        dto["id"]!.GetValue<Guid>().Should().Be(id);
        dto["assignedUserName"]!.GetValue<string>().Should().Be("Ana");
        dto["assignmentLabel"]!.GetValue<string>().Should().Be("Con Ana");

        // Visible on the list for the other tablets.
        (await SeededMockTerminalAsync())["assignmentLabel"]!.GetValue<string>().Should().Be("Con Ana");

        var release = await _fx.PostJsonAsync($"/api/payment-terminals/{id}/assign", new { });
        release.StatusCode.Should().Be(HttpStatusCode.OK);
        var released = await _fx.ReadJsonAsync(release);
        released["assignedUserName"].Should().BeNull();
        released["assignmentLabel"]!.GetValue<string>().Should().Be("En caja");

        // Persisted.
        var row = await _fx.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.PaymentTerminals, t => t.Id == id));
        row.AssignedUserName.Should().BeNull();
        row.AssignedUserId.Should().BeNull();
    }

    [Fact]
    public async Task Assign_WhitespaceName_IsBackToRegister()
    {
        var id = (await SeededMockTerminalAsync())["id"]!.GetValue<Guid>();
        await _fx.PostJsonAsync($"/api/payment-terminals/{id}/assign", new { userName = "Luis" });

        var response = await _fx.PostJsonAsync($"/api/payment-terminals/{id}/assign", new { userName = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["assignmentLabel"]!.GetValue<string>().Should().Be("En caja");
    }

    [Fact]
    public async Task Assign_NoBodyAtAll_IsBackToRegister()
    {
        var id = (await SeededMockTerminalAsync())["id"]!.GetValue<Guid>();
        await _fx.PostJsonAsync($"/api/payment-terminals/{id}/assign", new { userName = "Luis" });

        var response = await _fx.PostJsonAsync($"/api/payment-terminals/{id}/assign", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(response))["assignedUserName"].Should().BeNull();
    }

    [Fact]
    public async Task Assign_UnknownTerminal_Returns404()
    {
        var response = await _fx.PostJsonAsync($"/api/payment-terminals/{Guid.NewGuid()}/assign", new { userName = "Ana" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Be("Datáfono no encontrado.");
    }

    [Fact]
    public async Task Assign_InactiveTerminal_Returns409()
    {
        var inactive = await _fx.WithServiceAsync<IPaymentTerminalService, PaymentTerminalDto>(s =>
            s.SaveAsync(new SavePaymentTerminalRequest(null, "Datáfono apagado", PaymentTerminalProvider.Mock, null, null, IsActive: false, 5)));

        var response = await _fx.PostJsonAsync($"/api/payment-terminals/{inactive.Id}/assign", new { userName = "Ana" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("inactivo");
    }

    [Fact]
    public async Task Charges_EmptyThenPopulated_NewestFirst()
    {
        var empty = await _fx.Client.GetAsync("/api/payment-terminals/charges");
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fx.ReadJsonAsync(empty)).AsArray().Should().BeEmpty();

        var terminalId = (await SeededMockTerminalAsync())["id"]!.GetValue<Guid>();
        var first = await _fx.WithServiceAsync<IPaymentTerminalService, TerminalPaymentDto>(s =>
            s.ChargeAsync(new ChargeOnTerminalRequest(terminalId, 12500m, TerminalPaymentPurpose.Sale, "ORD-1", null, "Caja")));
        _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        var second = await _fx.WithServiceAsync<IPaymentTerminalService, TerminalPaymentDto>(s =>
            s.ChargeAsync(new ChargeOnTerminalRequest(terminalId, 10099m, TerminalPaymentPurpose.Sale, "ORD-2", null, "Caja")));

        var list = (await _fx.GetJsonAsync("/api/payment-terminals/charges")).AsArray();
        list.Should().HaveCount(2);
        list[0]!["id"]!.GetValue<Guid>().Should().Be(second.Id, "newest first");
        list[0]!["status"]!.GetValue<string>().Should().Be("Declined");
        list[0]!["amount"]!.GetValue<decimal>().Should().Be(10099m);
        list[0]!["statusLabel"]!.GetValue<string>().Should().Be("Rechazada");
        list[0]!["referenceText"]!.GetValue<string>().Should().BeEmpty();
        list[1]!["id"]!.GetValue<Guid>().Should().Be(first.Id);
        list[1]!["status"]!.GetValue<string>().Should().Be("Approved");
        list[1]!["terminalName"]!.GetValue<string>().Should().Be("Datáfono simulado");
        list[1]!["purpose"]!.GetValue<string>().Should().Be("Sale");
        list[1]!["invoiceReference"]!.GetValue<string>().Should().Be("ORD-1");
        list[1]!["referenceText"]!.GetValue<string>().Should().StartWith("AUTH ");
        list[1]!["isApproved"]!.GetValue<bool>().Should().BeTrue();
        list[1]!["voucherText"]!.GetValue<string>().Should().Contain("LOQZEN POS");
    }

    [Fact]
    public async Task Charges_TakeIsHonoured_AndClamped()
    {
        var terminalId = (await SeededMockTerminalAsync())["id"]!.GetValue<Guid>();
        for (var i = 0; i < 3; i++)
        {
            await _fx.WithServiceAsync<IPaymentTerminalService, TerminalPaymentDto>(s =>
                s.ChargeAsync(new ChargeOnTerminalRequest(terminalId, 1000m, TerminalPaymentPurpose.Sale, $"ORD-{i}", null, "Caja")));
            _fx.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        (await _fx.GetJsonAsync("/api/payment-terminals/charges?take=2")).AsArray().Should().HaveCount(2);
        (await _fx.GetJsonAsync("/api/payment-terminals/charges?take=0")).AsArray().Should().HaveCount(1, "take is clamped to 1..200");
        (await _fx.GetJsonAsync("/api/payment-terminals/charges?take=500")).AsArray().Should().HaveCount(3);
    }
}

/// <summary>With the feature off every datáfono route (and pay-on-terminal) answers 404 so the tablet hides it.</summary>
public class PaymentTerminalDisabledEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new(paymentTerminals: new PaymentTerminalSettings { Enabled = false, ChargeTimeoutSeconds = 5, MockDelaySeconds = 0 });

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    [Theory]
    [InlineData("GET", "/api/payment-terminals")]
    [InlineData("GET", "/api/payment-terminals/charges")]
    [InlineData("POST", "/api/payment-terminals/{id}/assign")]
    [InlineData("POST", "/api/comandas/{id}/pay-on-terminal")]
    public async Task Disabled_AllRoutesReturn404_WithMessage(string method, string path)
    {
        var seededId = await _fx.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.PaymentTerminals.Select(t => t.Id)));
        path = path.Replace("{id}", seededId.ToString());

        var response = method == "GET"
            ? await _fx.Client.GetAsync(path)
            : await _fx.PostJsonAsync(path, new { userName = "Ana", terminalId = seededId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Be("Datáfonos desactivados.");
    }

    [Fact]
    public async Task Disabled_OtherRoutesStillWork()
    {
        var response = await _fx.Client.GetAsync("/api/comandas");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
