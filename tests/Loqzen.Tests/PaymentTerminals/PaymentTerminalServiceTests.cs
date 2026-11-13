using Core.Application.DTOs;
using Core.Application.Hardware;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.PaymentTerminals;

/// <summary>Registry rules, assignment, connection test, and the charge flow (Pending row → driver → final row).</summary>
public class PaymentTerminalServiceTests : PaymentTerminalServiceTestBase
{
    private sealed class ProgressSink : IProgress<TerminalChargeProgress>
    {
        public readonly List<TerminalChargeProgress> Items = new();
        public void Report(TerminalChargeProgress value) => Items.Add(value);
    }

    // ── registry ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_CreatesMockTerminal_WithoutAddress()
    {
        var dto = await AddTerminalAsync("  Datáfono Caja  ");

        dto.Id.Should().NotBeEmpty();
        dto.Name.Should().Be("Datáfono Caja", "trimmed");
        dto.Provider.Should().Be(PaymentTerminalProvider.Mock);
        dto.Address.Should().BeNull();
        dto.IsActive.Should().BeTrue();
        dto.AssignmentLabel.Should().Be("En caja");
        dto.ProviderLabel.Should().Be("Simulado");
        (await TerminalRowAsync(dto.Id)).CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Save_NonMockProvider_RequiresAddress()
    {
        await Service.Invoking(s => s.SaveAsync(new SavePaymentTerminalRequest(null, "Inalámbrico", PaymentTerminalProvider.JsonTcp, "  ", null, true, 0)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*dirección*");
        (await Db.PaymentTerminals.CountAsync()).Should().Be(0);

        var ok = await AddTerminalAsync("Inalámbrico", PaymentTerminalProvider.JsonTcp, " 192.168.1.50:9102 ");
        ok.Address.Should().Be("192.168.1.50:9102");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Save_NameRequired(string name)
    {
        await Service.Invoking(s => s.SaveAsync(new SavePaymentTerminalRequest(null, name, PaymentTerminalProvider.Mock, null, null, true, 0)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*nombre*");
    }

    [Fact]
    public async Task Save_NameLongerThan60_Rejected()
    {
        await Service.Invoking(s => s.SaveAsync(new SavePaymentTerminalRequest(null, new string('x', 61), PaymentTerminalProvider.Mock, null, null, true, 0)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("datafono 1")]
    [InlineData("DATÁFONO 1")]
    [InlineData("Datáfono 1")]
    [InlineData("  Datafono 1  ")]
    public async Task Save_NameUniqueAmongActive_CaseAndAccentInsensitive(string duplicate)
    {
        await AddTerminalAsync("Datafono 1");

        await Service.Invoking(s => s.SaveAsync(new SavePaymentTerminalRequest(null, duplicate, PaymentTerminalProvider.Mock, null, null, true, 0)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ya existe*");
        (await Db.PaymentTerminals.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Save_InactiveTerminal_DoesNotBlockTheName_AndUpdateKeepsOwnName()
    {
        var old = await AddTerminalAsync("Datafono 1", isActive: false);
        old.IsActive.Should().BeFalse();

        var fresh = await AddTerminalAsync("DATAFONO 1");
        fresh.Id.Should().NotBe(old.Id);

        // Updating the active one with its own name (different casing) is not a collision with itself.
        var updated = await Service.SaveAsync(new SavePaymentTerminalRequest(fresh.Id, "datáfono 1", PaymentTerminalProvider.Mock, null, "SN-9", true, 5));
        updated.Name.Should().Be("datáfono 1");
        updated.SerialNumber.Should().Be("SN-9");
        updated.SortOrder.Should().Be(5);
        (await TerminalRowAsync(fresh.Id)).UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Save_ProviderNotAvailable_RejectedWithSpanishLabel()
    {
        await Service.Invoking(s => s.SaveAsync(new SavePaymentTerminalRequest(null, "Redeban caja", PaymentTerminalProvider.Redeban, "10.0.0.1:9000", null, true, 0)))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Redeban*no está disponible*");
    }

    [Fact]
    public async Task Save_UnknownId_KeyNotFound()
    {
        await Service.Invoking(s => s.SaveAsync(new SavePaymentTerminalRequest(Guid.NewGuid(), "X", PaymentTerminalProvider.Mock, null, null, true, 0)))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Save_DeactivatingViaSave_ClearsAssignment()
    {
        var t = await AddTerminalAsync();
        await Service.AssignAsync(t.Id, Guid.NewGuid(), "Pedro");

        var off = await Service.SaveAsync(new SavePaymentTerminalRequest(t.Id, t.Name, t.Provider, null, null, false, 0));

        off.IsActive.Should().BeFalse();
        off.AssignedUserId.Should().BeNull();
        off.AssignedUserName.Should().BeNull();
    }

    [Fact]
    public async Task GetTerminals_ActiveOnlyByDefault_OrderedBySortOrderThenName()
    {
        await AddTerminalAsync("Zeta", sortOrder: 0);
        await AddTerminalAsync("Alfa", sortOrder: 1);
        await AddTerminalAsync("Beta", sortOrder: 0);
        await AddTerminalAsync("Viejo", isActive: false);

        (await Service.GetTerminalsAsync()).Select(t => t.Name).Should().Equal("Beta", "Zeta", "Alfa");
        (await Service.GetTerminalsAsync(includeInactive: true)).Select(t => t.Name).Should().Equal("Beta", "Viejo", "Zeta", "Alfa");
    }

    [Fact]
    public async Task GetById_ReturnsNullForUnknown()
    {
        (await Service.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
        var t = await AddTerminalAsync();
        (await Service.GetByIdAsync(t.Id))!.Name.Should().Be(t.Name);
    }

    // ── deactivate ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Deactivate_SoftDeletes_AndClearsAssignment()
    {
        var t = await AddTerminalAsync();
        await Service.AssignAsync(t.Id, Guid.NewGuid(), "Pedro");

        await Service.DeactivateAsync(t.Id);

        var row = await TerminalRowAsync(t.Id);
        row.IsActive.Should().BeFalse();
        row.AssignedUserId.Should().BeNull();
        row.AssignedUserName.Should().BeNull();
        row.UpdatedAt.Should().Be(Now);
        (await Service.GetTerminalsAsync()).Should().BeEmpty();

        await Service.Invoking(s => s.DeactivateAsync(t.Id)).Should().NotThrowAsync("idempotent");
    }

    [Fact]
    public async Task Deactivate_UnknownId_KeyNotFound()
    {
        await Service.Invoking(s => s.DeactivateAsync(Guid.NewGuid())).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Deactivate_BlockedWhileAChargeIsPending()
    {
        var t = await AddTerminalAsync();
        var release = new TaskCompletionSource<TerminalChargeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Driver.OnCharge = (_, _, _) => release.Task;
        var charge = Service.ChargeAsync(Charge(t.Id));
        await Driver.Entered;

        await Service.Invoking(s => s.DeactivateAsync(t.Id)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*ocupado*");
        await Service.Invoking(s => s.SaveAsync(new SavePaymentTerminalRequest(t.Id, t.Name, t.Provider, null, null, false, 0)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*ocupado*");
        (await TerminalRowAsync(t.Id)).IsActive.Should().BeTrue();

        release.SetResult(FakeTerminalDriver.ApprovedVisa);
        (await charge).Status.Should().Be(TerminalPaymentStatus.Approved);
        await Service.DeactivateAsync(t.Id);
        (await TerminalRowAsync(t.Id)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Deactivate_RecoversStalePendingRow_ThenDeactivates()
    {
        var t = await AddTerminalAsync();
        var stale = await InsertPendingAsync(t.Id, Now - TimeSpan.FromSeconds(60 + 30 + 1));

        await Service.DeactivateAsync(t.Id);

        var row = await RowAsync(stale.Id);
        row.Status.Should().Be(TerminalPaymentStatus.Error);
        row.Message.Should().Be("Sin respuesta (recuperado)");
        row.CompletedAt.Should().Be(Now);
        (await TerminalRowAsync(t.Id)).IsActive.Should().BeFalse();
    }

    // ── assign ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Assign_ToWaiter_AndBackToRegister()
    {
        var t = await AddTerminalAsync();
        var waiter = Guid.NewGuid();

        var with = await Service.AssignAsync(t.Id, waiter, "  Pedro  ");
        with.AssignedUserId.Should().Be(waiter);
        with.AssignedUserName.Should().Be("Pedro");
        with.AssignmentLabel.Should().Be("Con Pedro");

        var back = await Service.AssignAsync(t.Id, null, null);
        back.AssignedUserId.Should().BeNull();
        back.AssignedUserName.Should().BeNull();
        back.AssignmentLabel.Should().Be("En caja");
        (await TerminalRowAsync(t.Id)).AssignedUserId.Should().BeNull();
    }

    [Fact]
    public async Task Assign_UserIdWithoutName_Rejected()
    {
        var t = await AddTerminalAsync();
        await Service.Invoking(s => s.AssignAsync(t.Id, Guid.NewGuid(), " "))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*nombre*");
    }

    [Fact]
    public async Task Assign_NameOnly_IsAcceptedWithoutUserId()
    {
        var t = await AddTerminalAsync();
        var dto = await Service.AssignAsync(t.Id, null, "Mesero sin cuenta");
        dto.AssignedUserId.Should().BeNull();
        dto.AssignedUserName.Should().Be("Mesero sin cuenta");
    }

    [Fact]
    public async Task Assign_NameTooLong_Rejected()
    {
        var t = await AddTerminalAsync();
        await Service.Invoking(s => s.AssignAsync(t.Id, Guid.NewGuid(), new string('p', 101)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Assign_UnknownOrInactiveTerminal_Rejected()
    {
        await Service.Invoking(s => s.AssignAsync(Guid.NewGuid(), null, null)).Should().ThrowAsync<KeyNotFoundException>();

        var t = await AddTerminalAsync(isActive: false);
        await Service.Invoking(s => s.AssignAsync(t.Id, Guid.NewGuid(), "Pedro"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*inactivo*");
    }

    // ── test connection ──────────────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_Ok_StoresLastPingOkAndLastSeenAt()
    {
        var t = await AddTerminalAsync("TCP", PaymentTerminalProvider.JsonTcp, "10.0.0.5:9102");
        TcpDriver.PingResult = true;

        (await Service.TestConnectionAsync(t.Id)).Should().BeTrue();

        TcpDriver.Pings.Should().Equal("10.0.0.5:9102");
        var row = await TerminalRowAsync(t.Id);
        row.LastPingOk.Should().BeTrue();
        row.LastSeenAt.Should().Be(Now);
    }

    [Fact]
    public async Task TestConnection_Failed_StoresFalse_KeepsLastSeenAt()
    {
        var t = await AddTerminalAsync("TCP", PaymentTerminalProvider.JsonTcp, "10.0.0.5:9102");
        TcpDriver.PingResult = true;
        await Service.TestConnectionAsync(t.Id);
        var seen = (await TerminalRowAsync(t.Id)).LastSeenAt;

        Clock.Advance(TimeSpan.FromMinutes(5));
        TcpDriver.PingResult = false;
        (await Service.TestConnectionAsync(t.Id)).Should().BeFalse();

        var row = await TerminalRowAsync(t.Id);
        row.LastPingOk.Should().BeFalse();
        row.LastSeenAt.Should().Be(seen, "a failed ping does not count as seeing the device");
    }

    [Fact]
    public async Task TestConnection_DriverThrows_ReturnsFalse_NeverThrows()
    {
        var t = await AddTerminalAsync();
        Driver.PingThrows = new InvalidOperationException("boom");

        (await Service.TestConnectionAsync(t.Id)).Should().BeFalse();
        (await TerminalRowAsync(t.Id)).LastPingOk.Should().BeFalse();
    }

    [Fact]
    public async Task TestConnection_UnknownTerminal_ReturnsFalse()
    {
        (await Service.TestConnectionAsync(Guid.NewGuid())).Should().BeFalse();
    }

    // ── charge: validation ───────────────────────────────────────────────────

    [Fact]
    public async Task Charge_AmountMustBePositive()
    {
        var t = await AddTerminalAsync();
        await Service.Invoking(s => s.ChargeAsync(Charge(t.Id, 0m))).Should().ThrowAsync<ArgumentException>().WithMessage("*mayor que cero*");
        await Service.Invoking(s => s.ChargeAsync(Charge(t.Id, -5m))).Should().ThrowAsync<ArgumentException>();
        (await Db.TerminalPayments.CountAsync()).Should().Be(0);
        Driver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Charge_OnlyOneTarget_Allowed()
    {
        var t = await AddTerminalAsync();
        await Service.Invoking(s => s.ChargeAsync(Charge(t.Id, orderId: Guid.NewGuid(), comandaId: Guid.NewGuid())))
            .Should().ThrowAsync<ArgumentException>();
        (await Db.TerminalPayments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Charge_UnknownTerminal_KeyNotFound()
    {
        await Service.Invoking(s => s.ChargeAsync(Charge(Guid.NewGuid()))).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Charge_InactiveTerminal_Rejected()
    {
        var t = await AddTerminalAsync(isActive: false);
        await Service.Invoking(s => s.ChargeAsync(Charge(t.Id))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*inactivo*");
        (await Db.TerminalPayments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Charge_NullRequest_Throws()
    {
        await Service.Invoking(s => s.ChargeAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    // ── charge: flow ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Charge_WritesPendingRowBeforeCallingTheDriver()
    {
        var t = await AddTerminalAsync("Caja 1");
        TerminalPaymentStatus? seenStatus = null;
        decimal? seenAmount = null;
        Driver.OnCharge = async (req, _, _) =>
        {
            await using var ctx = Sqlite.NewContext();
            var pending = await ctx.TerminalPayments.AsNoTracking().SingleAsync();
            seenStatus = pending.Status;
            seenAmount = pending.Amount;
            pending.TerminalName.Should().Be("Caja 1");
            pending.InvoiceReference.Should().Be("ORD-000001");
            pending.CompletedAt.Should().BeNull();
            pending.RequestedAt.Should().Be(Now);
            req.Amount.Should().Be(pending.Amount);
            req.InvoiceReference.Should().Be("ORD-000001");
            req.Currency.Should().Be("COP");
            return FakeTerminalDriver.ApprovedVisa;
        };

        await Service.ChargeAsync(Charge(t.Id, 12500m));

        seenStatus.Should().Be(TerminalPaymentStatus.Pending, "the audit row must exist before the device is contacted");
        seenAmount.Should().Be(12500m);
    }

    [Fact]
    public async Task Charge_Approved_PersistsAuthBrandLast4VoucherAndCompletedAt()
    {
        var t = await AddTerminalAsync();
        var shiftId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var req = Charge(t.Id, 12500m, "ORD-7", cashShiftId: shiftId, orderId: orderId, requestedBy: "Ana", description: "Mesa 4");

        var dto = await Service.ChargeAsync(req);

        dto.Status.Should().Be(TerminalPaymentStatus.Approved);
        dto.IsApproved.Should().BeTrue();
        dto.AuthorizationCode.Should().Be("123456");
        dto.CardBrand.Should().Be("VISA");
        dto.CardLast4.Should().Be("4242");
        dto.ProviderReference.Should().Be("RRN-1");
        dto.Message.Should().Be("Aprobada");
        dto.VoucherText.Should().Be("LOQZEN POS\nVENTA\nAPROBADA");
        dto.ReferenceText.Should().Be("AUTH 123456 · VISA ****4242");
        dto.StatusLabel.Should().Be("Aprobada");
        dto.CompletedAt.Should().Be(Now);
        dto.RequestedAt.Should().Be(Now);
        dto.OrderId.Should().Be(orderId);
        dto.PaymentTerminalId.Should().Be(t.Id);
        dto.Currency.Should().Be("COP");

        var row = await RowAsync(dto.Id);
        row.Status.Should().Be(TerminalPaymentStatus.Approved);
        row.AuthorizationCode.Should().Be("123456");
        row.CashShiftId.Should().Be(shiftId);
        row.OrderId.Should().Be(orderId);
        row.RequestedByUserId.Should().Be(req.RequestedByUserId);
        row.RequestedByName.Should().Be("Ana");
        row.CompletedAt.Should().Be(Now);
        row.ReferenceText.Should().Be(dto.ReferenceText);

        Driver.Requests.Should().ContainSingle().Which.Description.Should().Be("Mesa 4");

        var terminal = await TerminalRowAsync(t.Id);
        terminal.LastSeenAt.Should().Be(Now, "an approved charge proves the device is reachable");
        terminal.LastPingOk.Should().BeTrue();
    }

    [Fact]
    public async Task Charge_Declined_PersistsDeclinedRow_WithoutAuth()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult(TerminalChargeResult.Declined("Fondos insuficientes"));

        var dto = await Service.ChargeAsync(Charge(t.Id, 10099m));

        dto.Status.Should().Be(TerminalPaymentStatus.Declined);
        dto.Message.Should().Be("Fondos insuficientes");
        dto.AuthorizationCode.Should().BeNull();
        dto.ReferenceText.Should().BeEmpty();
        dto.VoucherText.Should().BeNull();
        dto.CompletedAt.Should().Be(Now);
        (await RowAsync(dto.Id)).Status.Should().Be(TerminalPaymentStatus.Declined);
        (await TerminalRowAsync(t.Id)).LastSeenAt.Should().BeNull("only approvals stamp LastSeenAt");
    }

    [Fact]
    public async Task Charge_DriverError_PersistsErrorRow()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult(TerminalChargeResult.Error("Error de comunicación"));

        var dto = await Service.ChargeAsync(Charge(t.Id));

        dto.Status.Should().Be(TerminalPaymentStatus.Error);
        dto.Message.Should().Be("Error de comunicación");
        (await RowAsync(dto.Id)).Status.Should().Be(TerminalPaymentStatus.Error);
    }

    [Fact]
    public async Task Charge_DriverThrows_PersistsErrorRow_DoesNotPropagate()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => throw new IOException("socket reset");

        var dto = await Service.ChargeAsync(Charge(t.Id));

        dto.Status.Should().Be(TerminalPaymentStatus.Error);
        dto.Message.Should().Be("socket reset");
        var row = await RowAsync(dto.Id);
        row.Status.Should().Be(TerminalPaymentStatus.Error);
        row.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Charge_DriverReturnsNull_PersistsErrorRow()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult<TerminalChargeResult>(null!);

        var dto = await Service.ChargeAsync(Charge(t.Id));

        dto.Status.Should().Be(TerminalPaymentStatus.Error);
        dto.Message.Should().Contain("no devolvió respuesta");
    }

    [Fact]
    public async Task Charge_ReportsConnectingWithTerminalName_ThenDriverProgress()
    {
        var t = await AddTerminalAsync("Inalámbrico 2");
        Driver.OnCharge = (_, p, _) =>
        {
            p!.Report(new TerminalChargeProgress(TerminalChargeStage.WaitingForCard, "Acerque la tarjeta"));
            return Task.FromResult(FakeTerminalDriver.ApprovedVisa);
        };
        var sink = new ProgressSink();

        await Service.ChargeAsync(Charge(t.Id), sink);

        sink.Items.Select(p => p.Stage).Should().Equal(TerminalChargeStage.Connecting, TerminalChargeStage.WaitingForCard);
        sink.Items[0].Message.Should().Contain("Inalámbrico 2");
    }

    [Fact]
    public async Task Charge_TruncatesLongFields_AndDefaultsRequestedBy()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult(new TerminalChargeResult(
            TerminalPaymentStatus.Approved, new string('a', 50), new string('b', 40), "12345", new string('r', 100),
            new string('m', 400), Enumerable.Repeat(new string('v', 32), 100).ToList()));

        var dto = await Service.ChargeAsync(Charge(t.Id, reference: new string('i', 80), requestedBy: " "));

        dto.AuthorizationCode.Should().HaveLength(40);
        dto.CardBrand.Should().HaveLength(30);
        dto.CardLast4.Should().Be("1234");
        dto.ProviderReference.Should().HaveLength(80);
        dto.Message.Should().HaveLength(300);
        dto.VoucherText.Should().HaveLength(2000);
        dto.InvoiceReference.Should().HaveLength(50);
        dto.RequestedByName.Should().Be("Caja");
        Driver.Requests.Single().InvoiceReference.Should().HaveLength(50, "the device gets the same reference the audit row stores");
    }

    [Fact]
    public async Task Charge_CurrencyComesFromSettings_UpperCased()
    {
        Settings.Currency = " usd ";
        var t = await AddTerminalAsync();

        var dto = await Service.ChargeAsync(Charge(t.Id));

        dto.Currency.Should().Be("USD");
        Driver.Requests.Single().Currency.Should().Be("USD");
    }

    // ── charge: busy / stale ─────────────────────────────────────────────────

    [Fact]
    public async Task Charge_SecondChargeOnSameTerminalWhilePending_IsRejectedAsBusy()
    {
        var t = await AddTerminalAsync();
        var release = new TaskCompletionSource<TerminalChargeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Driver.OnCharge = (_, _, _) => release.Task;

        var first = Service.ChargeAsync(Charge(t.Id, 1000m, "A"));
        await Driver.Entered;

        await Service.Invoking(s => s.ChargeAsync(Charge(t.Id, 2000m, "B")))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*ocupado*");
        Driver.Requests.Should().ContainSingle("the second charge never reaches the device");
        (await Db.TerminalPayments.AsNoTracking().CountAsync()).Should().Be(1, "the rejected charge leaves no audit row");

        release.SetResult(FakeTerminalDriver.ApprovedVisa);
        (await first).Status.Should().Be(TerminalPaymentStatus.Approved);

        // Once the first finished the terminal is free again.
        Driver.OnCharge = (_, _, _) => Task.FromResult(FakeTerminalDriver.ApprovedVisa);
        (await Service.ChargeAsync(Charge(t.Id, 2000m, "B"))).Status.Should().Be(TerminalPaymentStatus.Approved);
    }

    [Fact]
    public async Task Charge_OtherTerminalIsNotBlocked_ByABusyOne()
    {
        var busy = await AddTerminalAsync("Busy");
        var free = await AddTerminalAsync("Free");
        var release = new TaskCompletionSource<TerminalChargeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Driver.OnCharge = (req, _, _) => req.InvoiceReference == "A" ? release.Task : Task.FromResult(FakeTerminalDriver.ApprovedVisa);

        var first = Service.ChargeAsync(Charge(busy.Id, 1000m, "A"));
        await Driver.Entered;

        (await Service.ChargeAsync(Charge(free.Id, 2000m, "B"))).Status.Should().Be(TerminalPaymentStatus.Approved);

        release.SetResult(TerminalChargeResult.Declined());
        (await first).Status.Should().Be(TerminalPaymentStatus.Declined);
    }

    [Fact]
    public async Task Charge_StalePendingOlderThanTimeoutPlusGrace_IsRecoveredToError_AndChargeProceeds()
    {
        var t = await AddTerminalAsync();
        var stale = await InsertPendingAsync(t.Id, Now - TimeSpan.FromSeconds(60 + 30 + 1), 999m);

        var dto = await Service.ChargeAsync(Charge(t.Id));

        dto.Status.Should().Be(TerminalPaymentStatus.Approved);
        var recovered = await RowAsync(stale.Id);
        recovered.Status.Should().Be(TerminalPaymentStatus.Error);
        recovered.Message.Should().Be("Sin respuesta (recuperado)");
        recovered.CompletedAt.Should().Be(Now);
        recovered.Amount.Should().Be(999m, "only the status is touched");
    }

    [Fact]
    public async Task Charge_PendingYoungerThanTimeoutPlusGrace_StillCountsAsBusy()
    {
        var t = await AddTerminalAsync();
        await InsertPendingAsync(t.Id, Now - TimeSpan.FromSeconds(60 + 30 - 1));

        await Service.Invoking(s => s.ChargeAsync(Charge(t.Id)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*ocupado*");
        Driver.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Charge_TimeoutIsAtLeastFiveSeconds_EvenIfConfigured0()
    {
        Settings.ChargeTimeoutSeconds = 0;
        var t = await AddTerminalAsync();
        // 5 s + 30 s grace: a Pending row 34 s old is still live, 36 s old is stale.
        await InsertPendingAsync(t.Id, Now - TimeSpan.FromSeconds(34));
        await Service.Invoking(s => s.ChargeAsync(Charge(t.Id))).Should().ThrowAsync<InvalidOperationException>().WithMessage("*ocupado*");
    }

    // ── charge: timeout / cancel ─────────────────────────────────────────────

    [Fact]
    public async Task Charge_ConfiguredTimeoutElapses_DriverHonoursToken_ResultIsTimeout()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = FakeTerminalDriver.WaitForToken(() => TerminalChargeResult.Cancelled()); // what JsonTcp does

        var task = Service.ChargeAsync(Charge(t.Id));
        await Driver.Entered;
        Clock.Advance(TimeSpan.FromSeconds(59));
        task.IsCompleted.Should().BeFalse();

        Clock.Advance(TimeSpan.FromSeconds(1));
        var dto = await task;

        dto.Status.Should().Be(TerminalPaymentStatus.Timeout, "the driver answered Cancelled, but it was the service's timeout that fired");
        dto.Message.Should().Be("El datáfono no respondió a tiempo.");
        dto.StatusLabel.Should().Be("Sin respuesta");
        var row = await RowAsync(dto.Id);
        row.Status.Should().Be(TerminalPaymentStatus.Timeout);
        row.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Charge_TimeoutElapses_DriverThrowsOperationCanceled_ResultIsTimeout()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = FakeTerminalDriver.WaitForToken(onCancel: null); // propagates OperationCanceledException

        var task = Service.ChargeAsync(Charge(t.Id));
        await Driver.Entered;
        Clock.Advance(TimeSpan.FromSeconds(60));

        (await task).Status.Should().Be(TerminalPaymentStatus.Timeout);
    }

    [Fact]
    public async Task Charge_CallerCancels_ResultIsCancelled_AndRowIsStillPersisted()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = FakeTerminalDriver.WaitForToken(() => TerminalChargeResult.Cancelled());
        using var cts = new CancellationTokenSource();

        var task = Service.ChargeAsync(Charge(t.Id), null, cts.Token);
        await Driver.Entered;
        Clock.Advance(TimeSpan.FromSeconds(10));
        cts.Cancel();

        var dto = await task;
        dto.Status.Should().Be(TerminalPaymentStatus.Cancelled);
        dto.Message.Should().Be("Cancelada desde el POS.");
        var row = await RowAsync(dto.Id);
        row.Status.Should().Be(TerminalPaymentStatus.Cancelled);
        row.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Charge_CallerCancels_DriverThrows_ResultIsCancelled()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = FakeTerminalDriver.WaitForToken(onCancel: null);
        using var cts = new CancellationTokenSource();

        var task = Service.ChargeAsync(Charge(t.Id), null, cts.Token);
        await Driver.Entered;
        cts.Cancel();

        (await task).Status.Should().Be(TerminalPaymentStatus.Cancelled);
    }

    [Fact]
    public async Task Charge_DriverSaysCancelledOnItsOwn_WithoutAnyTokenFiring_StaysCancelled()
    {
        var t = await AddTerminalAsync();
        Driver.OnCharge = (_, _, _) => Task.FromResult(TerminalChargeResult.Cancelled());

        (await Service.ChargeAsync(Charge(t.Id))).Status.Should().Be(TerminalPaymentStatus.Cancelled);
    }

    // ── queries ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRecentCharges_NewestFirst_FilteredByTerminal_Clamped()
    {
        var a = await AddTerminalAsync("A");
        var b = await AddTerminalAsync("B");
        var c1 = await Service.ChargeAsync(Charge(a.Id, 100m, "1"));
        Clock.Advance(TimeSpan.FromMinutes(1));
        var c2 = await Service.ChargeAsync(Charge(b.Id, 200m, "2"));
        Clock.Advance(TimeSpan.FromMinutes(1));
        var c3 = await Service.ChargeAsync(Charge(a.Id, 300m, "3"));

        (await Service.GetRecentChargesAsync()).Select(x => x.Id).Should().Equal(c3.Id, c2.Id, c1.Id);
        (await Service.GetRecentChargesAsync(terminalId: a.Id)).Select(x => x.Id).Should().Equal(c3.Id, c1.Id);
        (await Service.GetRecentChargesAsync(take: 2)).Select(x => x.Id).Should().Equal(c3.Id, c2.Id);
        (await Service.GetRecentChargesAsync(take: 0)).Should().HaveCount(1, "take is clamped to at least 1");
        (await Service.GetRecentChargesAsync(terminalId: Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task GetApprovedForShift_OnlyApprovedOfThatShift_OldestFirst()
    {
        var t = await AddTerminalAsync();
        var shiftA = Guid.NewGuid();
        var shiftB = Guid.NewGuid();

        var a1 = await Service.ChargeAsync(Charge(t.Id, 10000m, "A1", cashShiftId: shiftA));
        Clock.Advance(TimeSpan.FromMinutes(1));
        Driver.OnCharge = (_, _, _) => Task.FromResult(TerminalChargeResult.Declined());
        await Service.ChargeAsync(Charge(t.Id, 5000m, "A-declined", cashShiftId: shiftA));
        Clock.Advance(TimeSpan.FromMinutes(1));
        Driver.OnCharge = (_, _, _) => Task.FromResult(FakeTerminalDriver.ApprovedVisa);
        var a2 = await Service.ChargeAsync(Charge(t.Id, 20000m, "A2", cashShiftId: shiftA));
        await Service.ChargeAsync(Charge(t.Id, 999m, "B1", cashShiftId: shiftB));
        await Service.ChargeAsync(Charge(t.Id, 777m, "no-shift"));

        var list = await Service.GetApprovedForShiftAsync(shiftA);

        list.Select(x => x.Id).Should().Equal(a1.Id, a2.Id);
        list.Sum(x => x.Amount).Should().Be(30000m);
        (await Service.GetApprovedForShiftAsync(Guid.NewGuid())).Should().BeEmpty();
    }
}
