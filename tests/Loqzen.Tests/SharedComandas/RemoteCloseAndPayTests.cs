using System.Net;
using System.Text.Json;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Infrastructure.Data.Sync;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// Satellite CAJA-02 invoicing a master comanda on ITS OWN SQLite database: payment-lock on the master → local
/// Order + Payment on the satellite shift → Outbox ComandaClosedRemotely (+ OrderCompleted) → best-effort close-remote.
/// </summary>
public class RemoteCloseAndPayTests : IDisposable
{
    private readonly SqliteDb _satDb = SqliteDb.Create();
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly FakeMasterHandler _master = new();
    private readonly TerminalContext _sat = Terminals.Satellite("CAJA-02");
    private readonly TerminalSettings _settings = new() { Code = "CAJA-02", Mode = TerminalKind.Satellite, MasterUrl = "http://master.local:5055", ApiKey = "k" };
    private readonly RemoteComandaService _svc;

    private readonly Product _burger;
    private readonly Product _soda;
    private readonly Guid _shiftId;
    private readonly Guid _comandaId = Guid.NewGuid();

    private const string Lock = "payment-lock";
    private const string Unlock = "payment-unlock";
    private const string CloseRemote = "close-remote";

    public RemoteCloseAndPayTests()
    {
        _db = _satDb.NewContext();
        _svc = new RemoteComandaService(new HttpClient(_master), _settings, _sat, _db, new SalesService(_db, _sat), _clock);

        // Catalog replicated from the master: same product ids as the comanda items.
        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        _burger = new Product { Sku = "B1", Name = "Hamburguesa", Price = 20000m, Tax = tax };
        _soda = new Product { Sku = "S1", Name = "Gaseosa", Price = 5000m, Tax = tax };
        var shift = new CashShift { ShiftNumber = 3, CashierName = "Caja 2", Status = CashShiftStatus.Open, TerminalId = _sat.Id, TerminalCode = _sat.Code };
        _db.AddRange(tax, _burger, _soda, shift);
        _db.SaveChanges();
        _shiftId = shift.Id;

        // Default master: lock → ReadyToPay locked by CAJA-02, unlock → same unlocked, close-remote → Closed.
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Lock}", _ => FakeMasterHandler.Json(MasterDto(locked: true)));
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Unlock}", _ => FakeMasterHandler.Json(MasterDto(locked: false)));
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{CloseRemote}", _ => FakeMasterHandler.Json(MasterDto(locked: false, status: ComandaStatus.Closed)));
    }

    public void Dispose()
    {
        _db.Dispose();
        _satDb.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Master's view of the comanda: 2 burgers + 1 soda (+ a cancelled soda that must not be billed).</summary>
    private ComandaDto MasterDto(bool locked, ComandaStatus status = ComandaStatus.ReadyToPay, Guid? missingProductId = null) => new()
    {
        Id = _comandaId, Number = 12, TableName = "Mesa 5", WaiterName = "Ana", Status = status, OpenedAt = Now.AddMinutes(-20),
        PaymentLockedByTerminalCode = locked ? "CAJA-02" : null, PaymentLockedAt = locked ? Now : null,
        Items =
        {
            new ComandaItemDto { Id = Guid.NewGuid(), ProductId = _burger.Id, ProductName = "Hamburguesa", UnitPrice = 20000m, TaxRate = 0.19m, Quantity = 2, Station = "Cocina Caliente", Status = ComandaItemStatus.Confirmed, AddedAt = Now.AddMinutes(-19) },
            new ComandaItemDto { Id = Guid.NewGuid(), ProductId = missingProductId ?? _soda.Id, ProductName = missingProductId == null ? "Gaseosa" : "Jugo nuevo", UnitPrice = 5000m, TaxRate = 0.19m, Quantity = 1, Station = "Barra", Status = ComandaItemStatus.Confirmed, AddedAt = Now.AddMinutes(-18) },
            new ComandaItemDto { Id = Guid.NewGuid(), ProductId = _soda.Id, ProductName = "Gaseosa", UnitPrice = 5000m, TaxRate = 0.19m, Quantity = 1, Station = "Barra", Status = ComandaItemStatus.Cancelled, AddedAt = Now.AddMinutes(-17) }
        }
    };

    // 2 × 20000 + 5000 = 45000 + 19% = 53550
    private const decimal Total = 53550m;

    private IEnumerable<string> Actions => _master.Requests.Select(r => r.Path.Split('/').Last());

    private Order? LocalOrder()
    {
        using var ctx = _satDb.NewContext();
        return ctx.Orders.Include(o => o.Items).Include(o => o.Payments).SingleOrDefault();
    }

    private List<OutboxMessage> Outbox(string eventType)
    {
        using var ctx = _satDb.NewContext();
        return ctx.OutboxMessages.Where(m => m.EventType == eventType).ToList();
    }

    // ── preconditions ────────────────────────────────────────────────────────

    [Fact]
    public async Task NoOpenShift_FailsWithoutTouchingTheMaster()
    {
        var result = await _svc.CloseAndPayAsync(_comandaId, Guid.NewGuid(), PaymentMethod.Cash, 100000m);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("No hay turno de caja abierto.");
        _master.Requests.Should().BeEmpty();
        LocalOrder().Should().BeNull();
    }

    [Fact]
    public async Task ClosedShift_FailsWithoutTouchingTheMaster()
    {
        var shift = await _db.CashShifts.SingleAsync();
        shift.Status = CashShiftStatus.Closed;
        await _db.SaveChangesAsync();

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeFalse();
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstRequestIsThePaymentLock_WithThisRegisterCode()
    {
        await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        var first = _master.Requests.First();
        first.Method.Should().Be(HttpMethod.Post);
        first.Path.Should().Be($"/api/comandas/{_comandaId}/{Lock}");
        JsonDocument.Parse(first.Body!).RootElement.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
    }

    [Fact]
    public async Task Lock409_PropagatesAndCreatesNothing()
    {
        _master.OnError(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Lock}", HttpStatusCode.Conflict, "La mesa la está cobrando CAJA-03.");

        await _svc.Invoking(s => s.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La mesa la está cobrando CAJA-03.");

        Actions.Should().Equal(Lock);
        LocalOrder().Should().BeNull();
        _db.OutboxMessages.Count().Should().Be(0);
    }

    [Fact]
    public async Task LockOffline_PropagatesHttpRequestExceptionAndCreatesNothing()
    {
        _master.OnThrow(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Lock}", new HttpRequestException("refused"));

        await _svc.Invoking(s => s.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m))
            .Should().ThrowAsync<HttpRequestException>().WithMessage("Sin conexión con la caja principal*");

        LocalOrder().Should().BeNull();
    }

    [Fact]
    public async Task CashTenderedBelowTotal_UnlocksAndFailsBeforeAnyOrder()
    {
        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, Total - 1m);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Monto recibido insuficiente");
        Actions.Should().Equal(Lock, Unlock);
        JsonDocument.Parse(_master.Requests[1].Body!).RootElement.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        LocalOrder().Should().BeNull();
        _db.OutboxMessages.Count().Should().Be(0);
    }

    [Fact]
    public async Task MissingProductLocally_UnlocksAndThrowsCatalogoDesactualizado()
    {
        var unknown = Guid.NewGuid();
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Lock}", _ => FakeMasterHandler.Json(MasterDto(locked: true, missingProductId: unknown)));

        await _svc.Invoking(s => s.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Catálogo desactualizado*Jugo nuevo*");

        Actions.Should().Equal(Lock, Unlock);
        LocalOrder().Should().BeNull();
        _db.OutboxMessages.Count().Should().Be(0);
    }

    [Fact]
    public async Task InactiveProductLocally_CountsAsMissing()
    {
        _soda.IsActive = false;
        await _db.SaveChangesAsync();

        await _svc.Invoking(s => s.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Catálogo desactualizado*Gaseosa*");
        Actions.Should().Equal(Lock, Unlock);
    }

    [Fact]
    public async Task MasterSaysClosed_UnlocksAndFails()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Lock}", _ => FakeMasterHandler.Json(MasterDto(locked: true, status: ComandaStatus.Closed)));

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("La comanda ya está cerrada.");
        Actions.Should().Equal(Lock, Unlock);
        LocalOrder().Should().BeNull();
    }

    [Fact]
    public async Task NoBillableItems_UnlocksAndFails()
    {
        var dto = MasterDto(locked: true);
        foreach (var i in dto.Items) i.Status = ComandaItemStatus.Cancelled;
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Lock}", _ => FakeMasterHandler.Json(dto));

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("La comanda no tiene productos.");
        Actions.Should().Equal(Lock, Unlock);
    }

    [Fact]
    public async Task UnlockFailure_IsSwallowed_OriginalFailureReturned()
    {
        _master.OnThrow(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Unlock}", new HttpRequestException("gone"));

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 1m);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().StartWith("Monto recibido insuficiente");
    }

    // ── happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Cash_HappyPath_LocalOrderOutboxAndCloseRemote()
    {
        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 60000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ChangeAmount.Should().Be(60000m - Total);
        result.OrderNumber.Should().StartWith("ORD-");

        // Local invoice on the satellite shift.
        var order = LocalOrder();
        order.Should().NotBeNull();
        order!.Id.Should().Be(result.OrderId);
        order.Status.Should().Be(OrderStatus.Completed);
        order.CashShiftId.Should().Be(_shiftId);
        order.TerminalCode.Should().Be("CAJA-02");
        order.TerminalId.Should().Be(_sat.Id);
        order.Total.Should().Be(Total);
        order.Items.Should().HaveCount(2, "the cancelled soda is not billed");
        order.Items.Single(i => i.ProductId == _burger.Id).Quantity.Should().Be(2);
        order.Items.Single(i => i.ProductId == _soda.Id).Quantity.Should().Be(1);
        var payment = order.Payments.Should().ContainSingle().Subject;
        payment.Method.Should().Be(PaymentMethod.Cash);
        payment.Amount.Should().Be(Total);
        payment.TenderedAmount.Should().Be(60000m);
        payment.ChangeAmount.Should().Be(60000m - Total);

        // Outbox: ComandaClosedRemotely { comanda: {...} } + OrderCompleted.
        var closed = Outbox(SyncEventTypes.ComandaClosedRemotely).Should().ContainSingle().Subject;
        closed.AggregateType.Should().Be("Comanda");
        closed.AggregateId.Should().Be(_comandaId);
        closed.Status.Should().Be(SyncStatus.Pending);
        using var doc = JsonDocument.Parse(closed.PayloadJson);
        var comanda = doc.RootElement.GetProperty("comanda");
        comanda.GetProperty("comandaId").GetGuid().Should().Be(_comandaId);
        comanda.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        comanda.GetProperty("orderId").GetGuid().Should().Be(result.OrderId);
        comanda.GetProperty("orderNumber").GetString().Should().Be(result.OrderNumber);
        comanda.GetProperty("method").GetString().Should().Be("Cash");
        comanda.GetProperty("total").GetDecimal().Should().Be(Total);
        comanda.GetProperty("reference").ValueKind.Should().Be(JsonValueKind.Null);
        comanda.GetProperty("closedAt").GetDateTime().ToUniversalTime().Should().Be(Now);
        doc.RootElement.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        WireJson.Deserialize<ComandaRemoteCloseRequest>(comanda.GetRawText()).Should().BeEquivalentTo(
            new ComandaRemoteCloseRequest(_comandaId, "CAJA-02", result.OrderId, result.OrderNumber, PaymentMethod.Cash, Total, null, Now));

        var completed = Outbox(SyncEventTypes.OrderCompleted).Should().ContainSingle().Subject;
        completed.AggregateId.Should().Be(result.OrderId);

        // Master calls in order; close-remote carries the local order id/number.
        Actions.Should().Equal(Lock, CloseRemote);
        var close = JsonDocument.Parse(_master.Requests[1].Body!).RootElement;
        close.GetProperty("comandaId").GetGuid().Should().Be(_comandaId);
        close.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        close.GetProperty("orderId").GetGuid().Should().Be(result.OrderId);
        close.GetProperty("orderNumber").GetString().Should().Be(result.OrderNumber);
        close.GetProperty("method").GetString().Should().Be("Cash");
        close.GetProperty("total").GetDecimal().Should().Be(Total);
    }

    [Fact]
    public async Task Card_HappyPath_StoresReferenceAndTerminalPaymentId()
    {
        var terminalPaymentId = Guid.NewGuid();

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Card, 0m, "  AUTH-77 ", terminalPaymentId);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.ChangeAmount.Should().Be(0m);
        var payment = LocalOrder()!.Payments.Single();
        payment.Method.Should().Be(PaymentMethod.Card);
        payment.Amount.Should().Be(Total);
        payment.TenderedAmount.Should().Be(Total, "card: tendered = total regardless of the argument");
        payment.Reference.Should().Be("AUTH-77");
        payment.TerminalPaymentId.Should().Be(terminalPaymentId);

        using var doc = JsonDocument.Parse(Outbox(SyncEventTypes.ComandaClosedRemotely).Single().PayloadJson);
        doc.RootElement.GetProperty("comanda").GetProperty("reference").GetString().Should().Be("AUTH-77");
        doc.RootElement.GetProperty("comanda").GetProperty("method").GetString().Should().Be("Card");
        JsonDocument.Parse(_master.Requests[1].Body!).RootElement.GetProperty("reference").GetString().Should().Be("AUTH-77");
    }

    [Fact]
    public async Task CloseRemoteOffline_IsSwallowed_ResultStillSuccess()
    {
        _master.OnThrow(HttpMethod.Post, $"/api/comandas/{_comandaId}/{CloseRemote}", new HttpRequestException("refused"));

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        LocalOrder()!.Status.Should().Be(OrderStatus.Completed);
        Outbox(SyncEventTypes.ComandaClosedRemotely).Should().ContainSingle("the Outbox dispatcher delivers it later");
        Actions.Should().Equal(Lock, CloseRemote);
        Actions.Should().NotContain(Unlock, "the invoice exists: the table must not be given back");
    }

    [Fact]
    public async Task CloseRemote409_IsSwallowed_ResultStillSuccess()
    {
        _master.OnError(HttpMethod.Post, $"/api/comandas/{_comandaId}/{CloseRemote}", HttpStatusCode.Conflict, "La comanda ya fue cerrada por CAJA-02.");

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        Outbox(SyncEventTypes.ComandaClosedRemotely).Should().ContainSingle();
    }

    [Fact]
    public async Task CloseRemote500_IsSwallowed_ResultStillSuccess()
    {
        _master.OnError(HttpMethod.Post, $"/api/comandas/{_comandaId}/{CloseRemote}", HttpStatusCode.InternalServerError, "boom");

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    [Fact]
    public async Task HappyPath_OutboxRowsAreWrittenBeforeCloseRemoteIsCalled()
    {
        var outboxAtCloseRemote = -1;
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{CloseRemote}", _ =>
        {
            using var ctx = _satDb.NewContext();
            outboxAtCloseRemote = ctx.OutboxMessages.Count(m => m.EventType == SyncEventTypes.ComandaClosedRemotely);
            return FakeMasterHandler.Json(MasterDto(locked: false, status: ComandaStatus.Closed));
        });

        await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 100000m);

        outboxAtCloseRemote.Should().Be(1, "offline-first: the Outbox row is committed before the best-effort call");
    }

    [Fact]
    public async Task WeighedProduct_UsesWeightKg()
    {
        var cheese = new Product { Sku = "Q1", Name = "Queso", Price = 30000m, IsWeighed = true };
        _db.Products.Add(cheese);
        await _db.SaveChangesAsync();
        var dto = MasterDto(locked: true);
        dto.Items.Clear();
        dto.Items.Add(new ComandaItemDto { Id = Guid.NewGuid(), ProductId = cheese.Id, ProductName = "Queso", UnitPrice = 30000m, TaxRate = 0m, Quantity = 1, WeightKg = 0.5m, Station = "Cocina Caliente", Status = ComandaItemStatus.Confirmed, AddedAt = Now });
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/{Lock}", _ => FakeMasterHandler.Json(dto));

        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Cash, 15000m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var order = LocalOrder()!;
        order.Total.Should().Be(15000m);
        order.Items.Single().WeightKg.Should().Be(0.5m);
    }

    [Fact]
    public async Task ShortOverload_DelegatesWithoutReference()
    {
        var result = await _svc.CloseAndPayAsync(_comandaId, _shiftId, PaymentMethod.Transfer, 0m);

        result.Success.Should().BeTrue(result.ErrorMessage);
        var payment = LocalOrder()!.Payments.Single();
        payment.Method.Should().Be(PaymentMethod.Transfer);
        payment.Reference.Should().BeNull();
        payment.TerminalPaymentId.Should().BeNull();
    }

    // ── end to end: the satellite's Outbox row closes the comanda on a real master inbox ──

    [Fact]
    public async Task OutboxRow_AppliedByMasterInbox_ClosesTheMasterComanda()
    {
        // A real master database with the comanda the fake HTTP master "represented".
        using var masterDb = SqliteDb.Create();
        using var m = masterDb.NewContext();
        var masterCtx = Terminals.Master("CAJA-01");
        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        var burger = new Product { Id = _burger.Id, Sku = "B1", Name = "Hamburguesa", Price = 20000m, Tax = tax };
        var soda = new Product { Id = _soda.Id, Sku = "S1", Name = "Gaseosa", Price = 5000m, Tax = tax };
        m.AddRange(tax, burger, soda, new Terminal { Id = masterCtx.Id, Code = "CAJA-01", Name = "Principal", Kind = TerminalKind.Master });
        await m.SaveChangesAsync();
        var masterSvc = new ComandaService(m, new ComandaSettings(), _clock, null, masterCtx);
        var c = await masterSvc.OpenComandaAsync("Mesa 5", "Ana");
        await masterSvc.AddItemAsync(c.Id, burger.Id, 2);
        await masterSvc.AddItemAsync(c.Id, soda.Id);
        c = await masterSvc.LockForPaymentAsync(c.Id, "CAJA-02");

        // The fake HTTP master answers with the real master's DTO; close-remote is "offline" so only the Outbox carries the close.
        _master.On(HttpMethod.Post, $"/api/comandas/{c.Id}/payment-lock", _ => FakeMasterHandler.Json(c));
        _master.OnThrow(HttpMethod.Post, $"/api/comandas/{c.Id}/close-remote", new HttpRequestException("offline"));
        var result = await _svc.CloseAndPayAsync(c.Id, _shiftId, PaymentMethod.Cash, 100000m);
        result.Success.Should().BeTrue(result.ErrorMessage);

        var row = Outbox(SyncEventTypes.ComandaClosedRemotely).Single();
        var envelope = WireJson.Envelope(row.EventType, row.AggregateId, "CAJA-02", row.PayloadJson, Now, row.Id);
        var inbox = new SyncInboxService(m, _clock, masterCtx);

        var applied = await inbox.ApplyAsync(envelope);

        applied.Outcome.Should().Be(SyncInboxOutcome.Applied, applied.Error);
        var closed = await masterSvc.GetByIdAsync(c.Id);
        closed!.Status.Should().Be(ComandaStatus.Closed);
        closed.PaidByTerminalCode.Should().Be("CAJA-02");
        closed.OrderNumber.Should().Be(result.OrderNumber);
        closed.OrderId.Should().BeNull();
        closed.PaymentLockedByTerminalCode.Should().BeNull();
        m.Orders.Count().Should().Be(0);
    }
}
