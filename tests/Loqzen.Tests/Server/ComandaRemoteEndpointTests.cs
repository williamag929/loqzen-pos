using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Core.Domain.Entities;
using Core.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.Server;

/// <summary>
/// The comanda routes added for shared comandas between registers, over real HTTP against the master:
/// reopen, hold on/off, cancel-confirmed (manager approved on another register), payment-lock / payment-unlock,
/// close-remote (invoiced on another register) and the <c>KitchenOrderWire</c> shape of /api/kds/orders.
/// </summary>
public class ComandaRemoteEndpointTests : IAsyncLifetime
{
    private readonly ServerFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private DateTime Now => _fx.Clock.GetUtcNow().UtcDateTime;

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> OpenAsync(string table, string waiter = "Ana")
    {
        var response = await _fx.PostJsonAsync("/api/comandas", new { tableName = table, waiterName = waiter });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await _fx.ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    private async Task<Guid> ProductIdAsync(string name)
    {
        var products = (await _fx.GetJsonAsync("/api/catalog/products")).AsArray();
        return products.Single(p => p!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<Guid>();
    }

    private async Task<JsonNode> AddItemAsync(Guid comandaId, string productName, decimal quantity = 1m, string? notes = null)
    {
        var response = await _fx.PostJsonAsync($"/api/comandas/{comandaId}/items", new { productId = await ProductIdAsync(productName), quantity, notes });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await _fx.ReadJsonAsync(response);
    }

    private async Task<JsonNode> PostOkAsync(string path, object? body = null)
    {
        var response = await _fx.PostJsonAsync(path, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await _fx.ReadJsonAsync(response);
    }

    private static Guid FirstItemId(JsonNode dto) => dto["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>();

    /// <summary>Open + one burger + sent to the kitchen → (comandaId, itemId).</summary>
    private async Task<(Guid ComandaId, Guid ItemId)> OpenWithConfirmedBurgerAsync(string table = "Mesa 1")
    {
        var id = await OpenAsync(table);
        var dto = await AddItemAsync(id, "Hamburguesa Especial");
        await PostOkAsync($"/api/comandas/{id}/send");
        return (id, FirstItemId(dto));
    }

    private object CloseBody(string terminalCode, Guid? orderId = null, string? orderNumber = "ORD-000007", decimal total = 0m) => new
    {
        comandaId = Guid.Empty, // the route id wins
        terminalCode,
        orderId = orderId ?? Guid.NewGuid(),
        orderNumber,
        method = "Cash",
        total,
        reference = (string?)null,
        closedAt = Now
    };

    // ── reopen ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reopen_ReadyToPay_GoesBackToOpen_AndItemsCanBeAddedAgain()
    {
        var (id, _) = await OpenWithConfirmedBurgerAsync();
        (await PostOkAsync($"/api/comandas/{id}/ready"))["status"]!.GetValue<string>().Should().Be("ReadyToPay");

        var reopened = await PostOkAsync($"/api/comandas/{id}/reopen");

        reopened["status"]!.GetValue<string>().Should().Be("Open");
        reopened["id"]!.GetValue<Guid>().Should().Be(id);
        (await AddItemAsync(id, "Gaseosa 400ml"))["items"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Reopen_OnAnOpenComanda_Returns409_AndUnknown_404()
    {
        var id = await OpenAsync("Mesa 1");

        var conflict = await _fx.PostJsonAsync($"/api/comandas/{id}/reopen", null);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(conflict))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

        (await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/reopen", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── hold ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hold_OnThenOff_TogglesIsOnHold_AndAHeldItemSurvivesAutoConfirm()
    {
        var id = await OpenAsync("Mesa 1");
        var itemId = FirstItemId(await AddItemAsync(id, "Hamburguesa Especial"));

        var held = await PostOkAsync($"/api/comandas/{id}/items/{itemId}/hold", new { onHold = true });
        held["items"]!.AsArray()[0]!["isOnHold"]!.GetValue<bool>().Should().BeTrue();
        held["items"]!.AsArray()[0]!["status"]!.GetValue<string>().Should().Be("Pending");

        // The master's timer skips held items.
        _fx.Clock.Advance(TimeSpan.FromSeconds(_fx.ComandaSettings.ConfirmationDelaySeconds + 5));
        (await _fx.WithServiceAsync<Core.Application.Services.IComandaService, int>(c => c.ConfirmDueItemsAsync())).Should().Be(0);

        var released = await PostOkAsync($"/api/comandas/{id}/items/{itemId}/hold", new { onHold = false });
        released["items"]!.AsArray()[0]!["isOnHold"]!.GetValue<bool>().Should().BeFalse();
        (await _fx.WithServiceAsync<Core.Application.Services.IComandaService, int>(c => c.ConfirmDueItemsAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Hold_WithoutBody_Returns400_AndUnknownComanda_404()
    {
        var id = await OpenAsync("Mesa 1");
        var itemId = FirstItemId(await AddItemAsync(id, "Hamburguesa Especial"));

        var noBody = await _fx.Client.PostAsync($"/api/comandas/{id}/items/{itemId}/hold", content: null);
        noBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/items/{itemId}/hold", new { onHold = true }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── cancel-confirmed ─────────────────────────────────────────────────────

    [Fact]
    public async Task CancelConfirmed_StrikesTheItem_WritesAConsumedManagerOverride_AndTheKdsLine()
    {
        var (id, itemId) = await OpenWithConfirmedBurgerAsync();
        var ticketId = (await _fx.GetJsonAsync("/api/kds/orders")).AsArray()
            .Single(o => o!["tableNumber"]!.GetValue<string>() == "Mesa 1")!["id"]!.GetValue<Guid>();

        var dto = await PostOkAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed",
            new { managerName = "Gerente Sofía", terminalCode = "CAJA-02", reason = "Cliente se fue" });

        dto["items"]!.AsArray().Should().ContainSingle().Which["status"]!.GetValue<string>().Should().Be("Cancelled");
        dto["status"]!.GetValue<string>().Should().Be("Open");

        // The master wrote its own single-use approval and consumed it in the same operation.
        var approval = await _fx.WithDbAsync(db => db.ManagerOverrides.AsNoTracking().SingleAsync());
        approval.Action.Should().Be(AuthorizedAction.CancelKitchenItem);
        approval.ManagerName.Should().Be("Gerente Sofía");
        approval.ManagerUserId.Should().Be(Guid.Empty, "authorized on another register, no local user row");
        approval.RequestedByName.Should().Be("CAJA-02");
        approval.Description.Should().Be("Anulación desde CAJA-02");
        approval.Reference.Should().Be("Cliente se fue");
        approval.AuthorizedAt.Should().Be(Now);
        approval.ConsumedAt.Should().Be(Now);
        approval.ConsumedFor.Should().Contain(itemId.ToString());

        // The kitchen line is struck; the whole ticket (its only line) is cancelled and leaves the active KDS list.
        var ticket = await _fx.GetJsonAsync($"/api/kds/orders/{ticketId}");
        ticket["items"]!.AsArray().Should().ContainSingle().Which["isCancelled"]!.GetValue<bool>().Should().BeTrue();
        ticket["status"]!.GetValue<string>().Should().Be("Cancelled");
        (await _fx.GetJsonAsync("/api/kds/orders")).AsArray().Select(o => o!["id"]!.GetValue<Guid>()).Should().NotContain(ticketId);

        // Striking it again: already cancelled → 409, the item stays as it was.
        var again = await _fx.PostJsonAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed",
            new { managerName = "Gerente Sofía", terminalCode = "CAJA-02" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.GetJsonAsync($"/api/comandas/{id}"))["items"]!.AsArray()[0]!["status"]!.GetValue<string>().Should().Be("Cancelled");
    }

    [Fact]
    public async Task CancelConfirmed_OnAPendingItem_Returns409_AndTheItemStaysPending()
    {
        var id = await OpenAsync("Mesa 1");
        var itemId = FirstItemId(await AddItemAsync(id, "Hamburguesa Especial"));

        var response = await _fx.PostJsonAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed",
            new { managerName = "Gerente", terminalCode = "CAJA-02" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("pendiente");
        (await _fx.GetJsonAsync($"/api/comandas/{id}"))["items"]!.AsArray()[0]!["status"]!.GetValue<string>().Should().Be("Pending");
    }

    /// <summary>
    /// BUG (ComandaEndpoints.cs, cancel-confirmed): the master inserts + SaveChanges the ManagerOverride BEFORE calling
    /// CancelConfirmedItemAsync. When the service refuses (409: item still Pending, already cancelled, comanda closed…)
    /// the approval row stays in the audit table, unconsumed (ConsumedAt null) and usable by id. A refused strike must
    /// leave no approval behind: either validate first or write the override in the same transaction as the strike.
    /// </summary>
    [Fact]
    public async Task CancelConfirmed_WhenRefused_LeavesNoOrphanUnconsumedApproval()
    {
        var id = await OpenAsync("Mesa 1");
        var itemId = FirstItemId(await AddItemAsync(id, "Hamburguesa Especial"));

        // Pending item → 409.
        (await _fx.PostJsonAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed", new { managerName = "Gerente", terminalCode = "CAJA-02" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var orphans = await _fx.WithDbAsync(db => db.ManagerOverrides.AsNoTracking().Where(o => o.ConsumedAt == null).ToListAsync());
        orphans.Should().BeEmpty("a refused cancel-confirmed must not leave an unconsumed CancelKitchenItem approval on the master");

        // Confirmed, struck once (consumed), struck again → 409: still exactly one approval, and it is consumed.
        await PostOkAsync($"/api/comandas/{id}/send");
        await PostOkAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed", new { managerName = "Gerente", terminalCode = "CAJA-02" });
        (await _fx.PostJsonAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed", new { managerName = "Gerente", terminalCode = "CAJA-02" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var all = await _fx.WithDbAsync(db => db.ManagerOverrides.AsNoTracking().ToListAsync());
        all.Should().ContainSingle().Which.ConsumedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData(null, "CAJA-02")]
    [InlineData("   ", "CAJA-02")]
    [InlineData("Gerente", null)]
    [InlineData("Gerente", "")]
    public async Task CancelConfirmed_MissingManagerOrTerminal_Returns400(string? managerName, string? terminalCode)
    {
        var (id, itemId) = await OpenWithConfirmedBurgerAsync();

        var response = await _fx.PostJsonAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed", new { managerName, terminalCode });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("managerName").And.Contain("terminalCode");
        (await _fx.WithDbAsync(db => db.ManagerOverrides.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task CancelConfirmed_NoBody_Returns400_AndUnknownComanda_404()
    {
        var (id, itemId) = await OpenWithConfirmedBurgerAsync();

        (await _fx.Client.PostAsync($"/api/comandas/{id}/items/{itemId}/cancel-confirmed", content: null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missing = await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/items/{itemId}/cancel-confirmed",
            new { managerName = "Gerente", terminalCode = "CAJA-02" });
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _fx.WithDbAsync(db => db.ManagerOverrides.CountAsync())).Should().Be(0, "404 is decided before the override row is written");
    }

    // ── payment-lock ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PaymentLock_ReservesTheTable_ConfirmsPendingItems_AndIsRefreshedByTheSameRegister()
    {
        var id = await OpenAsync("Mesa 1");
        await AddItemAsync(id, "Hamburguesa Especial");

        var locked = await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "caja-02 " });

        locked["status"]!.GetValue<string>().Should().Be("ReadyToPay", "Open → ReadyToPay exactly like /ready");
        locked["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-02", "codes are normalized");
        locked["paymentLockedAt"]!.GetValue<string>().Should().EndWith("Z");
        locked["paymentLockedAt"]!.GetValue<DateTime>().ToUniversalTime().Should().Be(Now);
        locked["items"]!.AsArray().Select(i => i!["status"]!.GetValue<string>()).Should().AllBe("Confirmed", "pending items go to the kitchen when the table is billed");
        locked["pendingCount"]!.GetValue<int>().Should().Be(0);
        (await _fx.GetJsonAsync("/api/kds/orders")).AsArray().Should().Contain(o => o!["tableNumber"]!.GetValue<string>() == "Mesa 1");

        // Same register again: just a refresh of the timestamp.
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-02" });
        refreshed["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-02");
        refreshed["paymentLockedAt"]!.GetValue<DateTime>().ToUniversalTime().Should().Be(Now);
        refreshed["status"]!.GetValue<string>().Should().Be("ReadyToPay");
        (await _fx.GetJsonAsync("/api/kds/orders")).AsArray().Count(o => o!["tableNumber"]!.GetValue<string>() == "Mesa 1")
            .Should().Be(1, "a refresh creates no new kitchen ticket");
    }

    [Fact]
    public async Task PaymentLock_HeldByAnotherRegister_Returns409_NamingTheHolder()
    {
        var (id, _) = await OpenWithConfirmedBurgerAsync();
        await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-02" });

        var conflict = await _fx.PostJsonAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-03" });

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(conflict))["error"]!.GetValue<string>().Should().Contain("CAJA-02");
        (await _fx.GetJsonAsync($"/api/comandas/{id}"))["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-02", "the holder keeps the lock");

        // A stale lock (older than PaymentLockMinutes) is ignored: the other register takes over.
        _fx.Clock.Advance(TimeSpan.FromMinutes(_fx.ComandaSettings.PaymentLockMinutes + 1));
        var takeover = await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-03" });
        takeover["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-03");
    }

    [Fact]
    public async Task PaymentLock_Validation_400WithoutTerminalCode_404Unknown_409WithoutItems()
    {
        var id = await OpenAsync("Mesa 1");

        (await _fx.PostJsonAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.Client.PostAsync($"/api/comandas/{id}/payment-lock", content: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/payment-lock", new { terminalCode = "CAJA-02" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var empty = await _fx.PostJsonAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-02" });
        empty.StatusCode.Should().Be(HttpStatusCode.Conflict, "nothing to bill");
        (await _fx.GetJsonAsync($"/api/comandas/{id}"))["paymentLockedByTerminalCode"].Should().BeNull();
        (await _fx.GetJsonAsync($"/api/comandas/{id}"))["status"]!.GetValue<string>().Should().Be("Open");
    }

    // ── payment-unlock ───────────────────────────────────────────────────────

    [Fact]
    public async Task PaymentUnlock_ByTheHolder_FreesTheTable_ByAnotherRegister_IsANoOp()
    {
        var (id, _) = await OpenWithConfirmedBurgerAsync();
        await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-02" });

        // Not the holder: 200, nothing changes.
        var noop = await PostOkAsync($"/api/comandas/{id}/payment-unlock", new { terminalCode = "CAJA-03" });
        noop["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-02");

        // The holder: lock cleared, status stays ReadyToPay (unlock does not reopen).
        var freed = await PostOkAsync($"/api/comandas/{id}/payment-unlock", new { terminalCode = "CAJA-02" });
        freed["paymentLockedByTerminalCode"].Should().BeNull();
        freed["paymentLockedAt"].Should().BeNull();
        freed["status"]!.GetValue<string>().Should().Be("ReadyToPay");

        // Now another register can take it.
        (await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-03" }))["paymentLockedByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-03");

        (await _fx.PostJsonAsync($"/api/comandas/{id}/payment-unlock", new { terminalCode = " " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/payment-unlock", new { terminalCode = "CAJA-02" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── close-remote ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CloseRemote_ClosesTheComanda_WithTheRemoteInvoice_AndNoLocalOrder()
    {
        var (id, _) = await OpenWithConfirmedBurgerAsync();
        var before = await _fx.GetJsonAsync($"/api/comandas/{id}");
        var total = before["total"]!.GetValue<decimal>();
        await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-02" });
        var remoteOrderId = Guid.NewGuid();
        _fx.Clock.Advance(TimeSpan.FromSeconds(30));

        var closed = await PostOkAsync($"/api/comandas/{id}/close-remote", CloseBody("CAJA-02", remoteOrderId, "ORD-000007", total));

        closed["id"]!.GetValue<Guid>().Should().Be(id, "the route id wins over the body's comandaId");
        closed["status"]!.GetValue<string>().Should().Be("Closed");
        closed["paidByTerminalCode"]!.GetValue<string>().Should().Be("CAJA-02");
        closed["orderNumber"]!.GetValue<string>().Should().Be("ORD-000007", "the remote invoice number travels in orderNumber");
        closed["orderId"].Should().BeNull("the Order lives on the satellite");
        closed["paymentLockedByTerminalCode"].Should().BeNull();
        closed["closedAt"]!.GetValue<DateTime>().Should().Be(Now);

        var row = await _fx.WithDbAsync(db => db.Comandas.AsNoTracking().SingleAsync(c => c.Id == id));
        row.RemoteOrderId.Should().Be(remoteOrderId);
        row.RemoteOrderNumber.Should().Be("ORD-000007");
        row.OrderId.Should().BeNull();
        (await _fx.WithDbAsync(db => db.Orders.AnyAsync(o => o.Id == remoteOrderId))).Should().BeFalse("no Order is written here; it arrives via the sync inbox");
        (await _fx.GetJsonAsync("/api/comandas")).AsArray().Should().NotContain(c => c!["id"]!.GetValue<Guid>() == id);
        (await _fx.GetJsonAsync("/api/tables")).AsArray().Should().OnlyContain(t => t!["openComandaId"] == null);

        // Idempotent: the satellite retries after a timeout.
        var again = await PostOkAsync($"/api/comandas/{id}/close-remote", CloseBody("CAJA-02", remoteOrderId, "ORD-000007", total));
        again["status"]!.GetValue<string>().Should().Be("Closed");
        again["orderNumber"]!.GetValue<string>().Should().Be("ORD-000007");

        // Another register claiming the same table: 409 naming the register that paid.
        var other = await _fx.PostJsonAsync($"/api/comandas/{id}/close-remote", CloseBody("CAJA-03"));
        other.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(other))["error"]!.GetValue<string>().Should().Contain("CAJA-02");
    }

    [Fact]
    public async Task CloseRemote_WithoutALock_ConfirmsPendingItemsForTheKitchen()
    {
        // "Mesa 8": DbInitializer seeds demo tickets for Mesa 2 / 4 / 7.
        var id = await OpenAsync("Mesa 8", "Luis");
        await AddItemAsync(id, "Hamburguesa Especial");
        await AddItemAsync(id, "Gaseosa 400ml", 2m);

        var closed = await PostOkAsync($"/api/comandas/{id}/close-remote", CloseBody("CAJA-02"));

        closed["status"]!.GetValue<string>().Should().Be("Closed");
        closed["items"]!.AsArray().Select(i => i!["status"]!.GetValue<string>()).Should().AllBe("Confirmed");
        var tickets = (await _fx.GetJsonAsync("/api/kds/orders")).AsArray().Where(o => o!["tableNumber"]!.GetValue<string>() == "Mesa 8").ToList();
        tickets.Select(t => t!["station"]!.GetValue<string>()).Should().BeEquivalentTo("Cocina Caliente", "Barra");
    }

    [Fact]
    public async Task CloseRemote_WhileAnotherRegisterHoldsTheLock_Returns409_AndLeavesItOpen()
    {
        var (id, _) = await OpenWithConfirmedBurgerAsync();
        await PostOkAsync($"/api/comandas/{id}/payment-lock", new { terminalCode = "CAJA-01" });

        var response = await _fx.PostJsonAsync($"/api/comandas/{id}/close-remote", CloseBody("CAJA-02"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().Contain("CAJA-01");
        var dto = await _fx.GetJsonAsync($"/api/comandas/{id}");
        dto["status"]!.GetValue<string>().Should().Be("ReadyToPay");
        dto["paidByTerminalCode"].Should().BeNull();
    }

    [Fact]
    public async Task CloseRemote_Validation_400OnMissingFields_404Unknown()
    {
        var (id, _) = await OpenWithConfirmedBurgerAsync();

        var noTerminal = await _fx.PostJsonAsync($"/api/comandas/{id}/close-remote", CloseBody(""));
        noTerminal.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(noTerminal))["error"]!.GetValue<string>().Should().Contain("terminalCode");

        var noOrderId = await _fx.PostJsonAsync($"/api/comandas/{id}/close-remote", CloseBody("CAJA-02", Guid.Empty));
        noOrderId.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(noOrderId))["error"]!.GetValue<string>().Should().Contain("orderId");

        var noOrderNumber = await _fx.PostJsonAsync($"/api/comandas/{id}/close-remote", CloseBody("CAJA-02", orderNumber: "  "));
        noOrderNumber.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _fx.ReadJsonAsync(noOrderNumber))["error"]!.GetValue<string>().Should().Contain("orderNumber");

        (await _fx.Client.PostAsync($"/api/comandas/{id}/close-remote", content: null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await _fx.PostJsonAsync($"/api/comandas/{Guid.NewGuid()}/close-remote", CloseBody("CAJA-02"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _fx.GetJsonAsync($"/api/comandas/{id}"))["status"]!.GetValue<string>().Should().Be("Open", "nothing was closed");
    }

    // ── /api/kds/orders as KitchenOrderWire ──────────────────────────────────

    [Fact]
    public async Task KdsOrders_AreKitchenOrderWire_WithProductIdsAndUtcTimestamps()
    {
        var id = await OpenAsync("Mesa 6", "Luis");
        var burgerId = await ProductIdAsync("Hamburguesa Especial");
        await AddItemAsync(id, "Hamburguesa Especial", 2m, "sin cebolla");
        await PostOkAsync($"/api/comandas/{id}/send");

        var ticket = (await _fx.GetJsonAsync("/api/kds/orders")).AsArray().Single(o => o!["tableNumber"]!.GetValue<string>() == "Mesa 6")!.AsObject();

        // Every KitchenOrder field travels (the satellite rebuilds the entity from this).
        ticket.Select(kv => kv.Key).Should().BeEquivalentTo(new[]
        {
            "id", "orderNumber", "orderId", "tableNumber", "waiterId", "waiterName", "status", "station", "generalNotes",
            "createdAt", "updatedAt", "printedAt", "deliveredAt", "items"
        });
        ticket["orderId"].Should().BeNull("a comanda ticket has no invoice yet");
        ticket["orderNumber"]!.GetValue<string>().Should().StartWith("CMD-");
        ticket["waiterName"]!.GetValue<string>().Should().Be("Luis");
        ticket["status"]!.GetValue<string>().Should().Be("Pending");
        ticket["createdAt"]!.GetValue<string>().Should().EndWith("Z");
        ticket["updatedAt"]!.GetValue<string>().Should().EndWith("Z");
        ticket["createdAt"]!.GetValue<DateTime>().ToUniversalTime().Should().Be(Now);

        var item = ticket["items"]!.AsArray().Should().ContainSingle().Which.AsObject();
        item.Select(kv => kv.Key).Should().BeEquivalentTo(new[] { "id", "productId", "productName", "quantity", "notes", "isPrepared", "isCancelled", "createdAt" });
        item["productId"]!.GetValue<Guid>().Should().Be(burgerId);
        item["productName"]!.GetValue<string>().Should().Be("Hamburguesa Especial");
        item["quantity"]!.GetValue<decimal>().Should().Be(2m);
        item["notes"]!.GetValue<string>().Should().Be("sin cebolla");
        item["isCancelled"]!.GetValue<bool>().Should().BeFalse();
        item["createdAt"]!.GetValue<string>().Should().EndWith("Z");

        // The mutation responses use the same wire shape.
        var advanced = await PostOkAsync($"/api/kds/orders/{ticket["id"]!.GetValue<Guid>()}/advance");
        advanced["status"]!.GetValue<string>().Should().Be("InPreparation");
        advanced["items"]!.AsArray()[0]!["productId"]!.GetValue<Guid>().Should().Be(burgerId);
        advanced["updatedAt"]!.GetValue<string>().Should().EndWith("Z");
        (await _fx.GetJsonAsync($"/api/kds/orders/{ticket["id"]!.GetValue<Guid>()}"))["items"]!.AsArray()[0]!["createdAt"]!.GetValue<string>().Should().EndWith("Z");
    }
}

/// <summary>With TerminalSettings.ApiKey configured, the register-only comanda routes demand the Bearer; the comandera routes stay open.</summary>
public class ComandaRemoteEndpointApiKeyTests : IAsyncLifetime
{
    private const string ApiKey = "lan-key-comandas";
    private readonly ServerFixture _fx = new(ApiKey);

    public Task InitializeAsync() => _fx.InitializeAsync();
    public Task DisposeAsync() => _fx.DisposeAsync();

    private async Task<(Guid ComandaId, Guid ItemId)> OpenWithConfirmedBurgerAsync()
    {
        var open = await _fx.PostJsonAsync("/api/comandas", new { tableName = "Mesa 1", waiterName = "Ana" });
        var id = (await _fx.ReadJsonAsync(open))["id"]!.GetValue<Guid>();
        var products = (await _fx.GetJsonAsync("/api/catalog/products")).AsArray();
        var burger = products.Single(p => p!["name"]!.GetValue<string>() == "Hamburguesa Especial")!["id"]!.GetValue<Guid>();
        var added = await _fx.ReadJsonAsync(await _fx.PostJsonAsync($"/api/comandas/{id}/items", new { productId = burger, quantity = 1 }));
        await _fx.PostJsonAsync($"/api/comandas/{id}/send", null);
        return (id, added["items"]!.AsArray()[0]!["id"]!.GetValue<Guid>());
    }

    public static IEnumerable<object[]> RegisterOnlyRoutes() => new[]
    {
        new object[] { "cancel-confirmed", new { managerName = "Gerente", terminalCode = "CAJA-02" } },
        new object[] { "payment-lock", new { terminalCode = "CAJA-02" } },
        new object[] { "payment-unlock", new { terminalCode = "CAJA-02" } },
        new object[] { "close-remote", new { terminalCode = "CAJA-02", orderId = Guid.NewGuid(), orderNumber = "ORD-1", method = "Cash", total = 1m } },
    };

    private static string PathFor(Guid comandaId, Guid itemId, string route) =>
        route == "cancel-confirmed" ? $"/api/comandas/{comandaId}/items/{itemId}/{route}" : $"/api/comandas/{comandaId}/{route}";

    [Theory]
    [MemberData(nameof(RegisterOnlyRoutes))]
    public async Task RegisterOnlyRoute_WithoutBearer_Returns401_AndChangesNothing(string route, object body)
    {
        var (id, itemId) = await OpenWithConfirmedBurgerAsync();
        using var anonymous = _fx.AnonymousClient();

        var response = await anonymous.PostAsJsonAsync(PathFor(id, itemId, route), body, ServerFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, route);
        (await _fx.ReadJsonAsync(response))["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        var dto = await _fx.GetJsonAsync($"/api/comandas/{id}");
        dto["status"]!.GetValue<string>().Should().Be("Open");
        dto["paymentLockedByTerminalCode"].Should().BeNull();
        dto["items"]!.AsArray()[0]!["status"]!.GetValue<string>().Should().Be("Confirmed");
        (await _fx.WithDbAsync(db => db.ManagerOverrides.CountAsync())).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(RegisterOnlyRoutes))]
    public async Task RegisterOnlyRoute_WithWrongBearer_Returns401(string route, object body)
    {
        var (id, itemId) = await OpenWithConfirmedBurgerAsync();
        using var wrong = _fx.AnonymousClient("not-the-key");

        var response = await wrong.PostAsJsonAsync(PathFor(id, itemId, route), body, ServerFixture.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, route);
    }

    [Theory]
    [MemberData(nameof(RegisterOnlyRoutes))]
    public async Task RegisterOnlyRoute_WithTheBearer_Passes(string route, object body)
    {
        var (id, itemId) = await OpenWithConfirmedBurgerAsync();

        var response = await _fx.PostJsonAsync(PathFor(id, itemId, route), body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, route);
    }

    [Fact]
    public async Task ComanderaRoutes_StayOpen_WithoutBearer()
    {
        using var anonymous = _fx.AnonymousClient();

        var open = await anonymous.PostAsJsonAsync("/api/comandas", new { tableName = "Mesa 9", waiterName = "Ana" }, ServerFixture.Json);
        open.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await _fx.ReadJsonAsync(open))["id"]!.GetValue<Guid>();
        (await anonymous.PostAsync($"/api/comandas/{id}/reopen", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "open, so it cannot be reopened; but the route is reachable");
        (await anonymous.GetAsync("/api/kds/orders")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
