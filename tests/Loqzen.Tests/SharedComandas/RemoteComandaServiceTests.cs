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

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// Satellite RemoteComandaService (CAJA-02) against an in-test master (<see cref="FakeMasterHandler"/>): routes, bodies,
/// Bearer, DTO deserialization and the MasterHttp error mapping. CloseAndPayAsync is covered in RemoteCloseAndPayTests.
/// </summary>
public class RemoteComandaServiceTests : IDisposable
{
    private const string MasterUrl = "http://master.local:5055/";
    private readonly SqliteDb _satDb = SqliteDb.Create();
    private readonly PosDbContext _db;
    private readonly TestClock _clock = new();
    private readonly FakeMasterHandler _master = new();
    private readonly TerminalContext _sat = Terminals.Satellite("CAJA-02");
    private readonly TerminalSettings _settings = new() { Code = "CAJA-02", Mode = TerminalKind.Satellite, MasterUrl = MasterUrl, ApiKey = "secret-key" };
    private readonly RemoteComandaService _svc;

    private readonly Guid _comandaId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();

    public RemoteComandaServiceTests()
    {
        _db = _satDb.NewContext();
        _svc = new RemoteComandaService(new HttpClient(_master), _settings, _sat, _db, new SalesService(_db, _sat), _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _satDb.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private ComandaDto SampleDto(ComandaStatus status = ComandaStatus.ReadyToPay) => new()
    {
        Id = _comandaId, Number = 7, TableName = "Mesa 3", WaiterName = "Ana", Status = status,
        OpenedAt = new DateTime(2026, 11, 12, 11, 0, 0, DateTimeKind.Utc),
        PaymentLockedByTerminalCode = "CAJA-02", PaymentLockedAt = new DateTime(2026, 11, 12, 11, 5, 0, DateTimeKind.Utc),
        Items =
        {
            new ComandaItemDto { Id = _itemId, ProductId = Guid.NewGuid(), ProductName = "Hamburguesa", UnitPrice = 20000m, TaxRate = 0.19m, Quantity = 2, Station = "Cocina Caliente", Status = ComandaItemStatus.Confirmed, AddedAt = new DateTime(2026, 11, 12, 11, 1, 0, DateTimeKind.Utc), ConfirmedAt = new DateTime(2026, 11, 12, 11, 2, 0, DateTimeKind.Utc) },
            new ComandaItemDto { Id = Guid.NewGuid(), ProductId = Guid.NewGuid(), ProductName = "Gaseosa", UnitPrice = 5000m, TaxRate = 0.19m, Quantity = 1, Station = "Barra", Status = ComandaItemStatus.Pending, IsOnHold = true, Notes = "sin hielo", AddedAt = new DateTime(2026, 11, 12, 11, 3, 0, DateTimeKind.Utc) }
        }
    };

    private static JsonElement Body(FakeMasterHandler.Recorded r) => JsonDocument.Parse(r.Body!).RootElement;

    // ── reads ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTables_GetsApiTables()
    {
        var tableId = Guid.NewGuid();
        _master.On(HttpMethod.Get, "/api/tables", new[] { new DiningTableDto { Id = tableId, Name = "Mesa 1", OpenComandaId = _comandaId } });

        var tables = await _svc.GetTablesAsync();

        var req = _master.Single(HttpMethod.Get, "/api/tables");
        req.Body.Should().BeNull();
        req.Authorization!.Scheme.Should().Be("Bearer");
        req.Authorization.Parameter.Should().Be("secret-key");
        tables.Should().ContainSingle().Which.Should().BeEquivalentTo(new DiningTableDto { Id = tableId, Name = "Mesa 1", OpenComandaId = _comandaId });
    }

    [Fact]
    public async Task GetActiveComandas_GetsApiComandas_AndDeserializesDto()
    {
        var expected = SampleDto();
        _master.On(HttpMethod.Get, "/api/comandas", new[] { expected });

        var list = await _svc.GetActiveComandasAsync();

        _master.Single(HttpMethod.Get, "/api/comandas");
        var dto = list.Should().ContainSingle().Subject;
        dto.Should().BeEquivalentTo(expected, o => o.Excluding(x => x.BillableItems));
        dto.Status.Should().Be(ComandaStatus.ReadyToPay);
        dto.PaymentLockedByTerminalCode.Should().Be("CAJA-02");
        dto.Items.Should().HaveCount(2);
        dto.Items[1].IsOnHold.Should().BeTrue();
        dto.Total.Should().Be(expected.Total);
        dto.PendingCount.Should().Be(1);
    }

    [Fact]
    public async Task GetById_GetsApiComandasId()
    {
        _master.On(HttpMethod.Get, $"/api/comandas/{_comandaId}", SampleDto());

        var dto = await _svc.GetByIdAsync(_comandaId);

        dto!.Id.Should().Be(_comandaId);
        dto.Number.Should().Be(7);
        _master.Requests.Should().ContainSingle().Which.PathAndQuery.Should().Be($"/api/comandas/{_comandaId}");
    }

    [Fact]
    public async Task GetById_404_ReturnsNull()
    {
        _master.OnError(HttpMethod.Get, $"/api/comandas/{_comandaId}", HttpStatusCode.NotFound, "Comanda no encontrada.");

        (await _svc.GetByIdAsync(_comandaId)).Should().BeNull();
    }

    [Fact]
    public async Task GetById_Offline_ThrowsHttpRequestException()
    {
        _master.OnThrow(HttpMethod.Get, $"/api/comandas/{_comandaId}", new HttpRequestException("Connection refused"));

        await _svc.Invoking(s => s.GetByIdAsync(_comandaId))
            .Should().ThrowAsync<HttpRequestException>().WithMessage("Sin conexión con la caja principal*Connection refused*");
    }

    [Fact]
    public async Task GetById_DtoFromMaster_MapsRemotePaidFields()
    {
        var dto = SampleDto(ComandaStatus.Closed);
        dto.PaymentLockedByTerminalCode = null; dto.PaymentLockedAt = null;
        dto.PaidByTerminalCode = "CAJA-03"; dto.OrderNumber = "ORD-9"; dto.ClosedAt = Now;
        _master.On(HttpMethod.Get, $"/api/comandas/{_comandaId}", dto);

        var got = await _svc.GetByIdAsync(_comandaId);

        got!.Status.Should().Be(ComandaStatus.Closed);
        got.PaidByTerminalCode.Should().Be("CAJA-03");
        got.OrderNumber.Should().Be("ORD-9");
        got.OrderId.Should().BeNull();
        got.ClosedAt.Should().Be(Now);
    }

    // ── mutations: route + body ──────────────────────────────────────────────

    [Fact]
    public async Task OpenComanda_PostsApiComandasWithBody()
    {
        var tableId = Guid.NewGuid();
        _master.On(HttpMethod.Post, "/api/comandas", SampleDto(ComandaStatus.Open));

        var dto = await _svc.OpenComandaAsync("  Mesa 3 ", "Ana", tableId);

        dto.Status.Should().Be(ComandaStatus.Open);
        var body = Body(_master.Single(HttpMethod.Post, "/api/comandas"));
        body.GetProperty("tableName").GetString().Should().Be("Mesa 3");
        body.GetProperty("waiterName").GetString().Should().Be("Ana");
        body.GetProperty("diningTableId").GetGuid().Should().Be(tableId);
    }

    [Fact]
    public async Task OpenComanda_BlankTable_ThrowsLocallyWithoutRequest()
    {
        await _svc.Invoking(s => s.OpenComandaAsync(" ", "Ana")).Should().ThrowAsync<ArgumentException>();
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddItem_PostsItemsWithBody()
    {
        var productId = Guid.NewGuid();
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items", SampleDto(ComandaStatus.Open));

        await _svc.AddItemAsync(_comandaId, productId, 2m, 0.75m, "sin cebolla");

        var body = Body(_master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/items"));
        body.GetProperty("productId").GetGuid().Should().Be(productId);
        body.GetProperty("quantity").GetDecimal().Should().Be(2m);
        body.GetProperty("weightKg").GetDecimal().Should().Be(0.75m);
        body.GetProperty("notes").GetString().Should().Be("sin cebolla");
    }

    [Fact]
    public async Task AddItem_NullOptionals_AreSentAsNull()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items", SampleDto(ComandaStatus.Open));

        await _svc.AddItemAsync(_comandaId, Guid.NewGuid());

        var body = Body(_master.Requests.Single());
        body.GetProperty("quantity").GetDecimal().Should().Be(1m);
        body.GetProperty("weightKg").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, 0.0)]
    [InlineData(1, -2.0)]
    public async Task AddItem_InvalidQuantityOrWeight_ThrowsLocally(decimal qty, double? weight)
    {
        await _svc.Invoking(s => s.AddItemAsync(_comandaId, Guid.NewGuid(), qty, weight == null ? null : (decimal)weight.Value))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelPendingItem_PostsCancelWithoutBody()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/cancel", SampleDto());

        await _svc.CancelPendingItemAsync(_comandaId, _itemId);

        var req = _master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/cancel");
        req.Body.Should().BeNull();
    }

    [Fact]
    public async Task SetItemNotes_PostsNotesTrimmed()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/notes", SampleDto());

        await _svc.SetItemNotesAsync(_comandaId, _itemId, "  sin cebolla  ");

        Body(_master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/notes")).GetProperty("notes").GetString().Should().Be("sin cebolla");
    }

    [Fact]
    public async Task SetItemNotes_Blank_PostsNull()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/notes", SampleDto());

        await _svc.SetItemNotesAsync(_comandaId, _itemId, "   ");

        Body(_master.Requests.Single()).GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SetItemNotes_TooLong_ThrowsLocally()
    {
        await _svc.Invoking(s => s.SetItemNotesAsync(_comandaId, _itemId, new string('a', ComandaSettings.MaxNotesLength + 1)))
            .Should().ThrowAsync<ArgumentException>();
        _master.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetItemHold_PostsHold(bool onHold)
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/hold", SampleDto());

        await _svc.SetItemHoldAsync(_comandaId, _itemId, onHold);

        Body(_master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/hold")).GetProperty("onHold").GetBoolean().Should().Be(onHold);
    }

    [Fact]
    public async Task ConfirmPendingNow_PostsSend()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/send", SampleDto());

        await _svc.ConfirmPendingNowAsync(_comandaId);

        _master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/send").Body.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmDueItems_ReturnsZeroWithoutRequest()
    {
        (await _svc.ConfirmDueItemsAsync()).Should().Be(0);
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadyToPay_PostsReady()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/ready", SampleDto());

        (await _svc.MarkReadyToPayAsync(_comandaId)).Status.Should().Be(ComandaStatus.ReadyToPay);

        _master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/ready").Body.Should().BeNull();
    }

    [Fact]
    public async Task Reopen_PostsReopen()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/reopen", SampleDto(ComandaStatus.Open));

        (await _svc.ReopenAsync(_comandaId)).Status.Should().Be(ComandaStatus.Open);

        _master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/reopen").Body.Should().BeNull();
    }

    [Fact]
    public async Task LockForPayment_PostsPaymentLockWithNormalizedCode()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/payment-lock", SampleDto());

        var dto = await _svc.LockForPaymentAsync(_comandaId, " caja-02 ");

        dto.PaymentLockedByTerminalCode.Should().Be("CAJA-02");
        Body(_master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/payment-lock")).GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
    }

    [Fact]
    public async Task UnlockPayment_PostsPaymentUnlock()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/payment-unlock", SampleDto());

        await _svc.UnlockPaymentAsync(_comandaId, "caja-02");

        Body(_master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/payment-unlock")).GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
    }

    [Fact]
    public async Task CloseRemotely_PostsCloseRemoteWithCamelCaseBodyAndStringEnum()
    {
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/close-remote", SampleDto(ComandaStatus.Closed));
        var orderId = Guid.NewGuid();
        var closedAt = new DateTime(2026, 11, 12, 12, 30, 0, DateTimeKind.Utc);
        var req = new ComandaRemoteCloseRequest(_comandaId, "CAJA-02", orderId, "ORD-5", PaymentMethod.Transfer, 29750m, "TRX-1", closedAt);

        await _svc.CloseRemotelyAsync(req);

        var body = Body(_master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/close-remote"));
        body.GetProperty("comandaId").GetGuid().Should().Be(_comandaId);
        body.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        body.GetProperty("orderId").GetGuid().Should().Be(orderId);
        body.GetProperty("orderNumber").GetString().Should().Be("ORD-5");
        body.GetProperty("method").GetString().Should().Be("Transfer");
        body.GetProperty("total").GetDecimal().Should().Be(29750m);
        body.GetProperty("reference").GetString().Should().Be("TRX-1");
        body.GetProperty("closedAt").GetDateTime().ToUniversalTime().Should().Be(closedAt);
        body.TryGetProperty("ComandaId", out _).Should().BeFalse("camelCase only");
    }

    [Fact]
    public async Task CloseRemotely_NullRequest_Throws()
    {
        await _svc.Invoking(s => s.CloseRemotelyAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        _master.Requests.Should().BeEmpty();
    }

    // ── CancelConfirmedItemAsync: local approval + master call ───────────────

    private async Task<ManagerOverride> AddApprovalAsync(AuthorizedAction action = AuthorizedAction.CancelKitchenItem, bool consumed = false)
    {
        var o = new ManagerOverride
        {
            Action = action, Description = "Anular", ManagerUserId = Guid.NewGuid(), ManagerName = "Gerente", Reference = "se cayó el plato",
            AuthorizedAt = Now, ConsumedAt = consumed ? Now : null
        };
        _db.ManagerOverrides.Add(o);
        await _db.SaveChangesAsync();
        return o;
    }

    [Fact]
    public async Task CancelConfirmedItem_PostsManagerNameAndTerminalCode_AndConsumesLocalApproval()
    {
        var approval = await AddApprovalAsync();
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/cancel-confirmed", SampleDto());

        var dto = await _svc.CancelConfirmedItemAsync(_comandaId, _itemId, approval.Id);

        dto.Id.Should().Be(_comandaId);
        var body = Body(_master.Single(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/cancel-confirmed"));
        body.GetProperty("managerName").GetString().Should().Be("Gerente");
        body.GetProperty("terminalCode").GetString().Should().Be("CAJA-02");
        body.GetProperty("reason").GetString().Should().Be("se cayó el plato");

        using var ctx = _satDb.NewContext();
        var row = ctx.ManagerOverrides.Single(o => o.Id == approval.Id);
        row.ConsumedAt.Should().Be(Now);
        row.ConsumedFor.Should().Contain(_itemId.ToString());
    }

    [Fact]
    public async Task CancelConfirmedItem_UnknownApproval_ThrowsWithoutRequest()
    {
        await _svc.Invoking(s => s.CancelConfirmedItemAsync(_comandaId, _itemId, Guid.NewGuid()))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Se requiere autorización de gerente.");
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelConfirmedItem_WrongAction_ThrowsWithoutRequest()
    {
        var approval = await AddApprovalAsync(AuthorizedAction.ApplyDiscount);

        await _svc.Invoking(s => s.CancelConfirmedItemAsync(_comandaId, _itemId, approval.Id))
            .Should().ThrowAsync<InvalidOperationException>();
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelConfirmedItem_ConsumedApproval_ThrowsWithoutRequest()
    {
        var approval = await AddApprovalAsync(consumed: true);

        await _svc.Invoking(s => s.CancelConfirmedItemAsync(_comandaId, _itemId, approval.Id))
            .Should().ThrowAsync<InvalidOperationException>();
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelConfirmedItem_MasterRefuses_ApprovalStaysUnconsumed()
    {
        var approval = await AddApprovalAsync();
        _master.OnError(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/cancel-confirmed", HttpStatusCode.Conflict, "El item ya está anulado.");

        await _svc.Invoking(s => s.CancelConfirmedItemAsync(_comandaId, _itemId, approval.Id))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("El item ya está anulado.");

        using var ctx = _satDb.NewContext();
        ctx.ManagerOverrides.Single(o => o.Id == approval.Id).ConsumedAt.Should().BeNull("the PIN can be used for a retry");
    }

    // ── error mapping (MasterHttp) ───────────────────────────────────────────

    [Fact]
    public async Task Error400_MapsToArgumentExceptionWithServerMessage()
    {
        _master.OnError(HttpMethod.Post, $"/api/comandas/{_comandaId}/send", HttpStatusCode.BadRequest, "La mesa es obligatoria.");

        await _svc.Invoking(s => s.ConfirmPendingNowAsync(_comandaId))
            .Should().ThrowAsync<ArgumentException>().WithMessage("La mesa es obligatoria.");
    }

    [Fact]
    public async Task Error404_MapsToKeyNotFoundException()
    {
        _master.OnError(HttpMethod.Post, $"/api/comandas/{_comandaId}/ready", HttpStatusCode.NotFound, "Comanda no encontrada.");

        await _svc.Invoking(s => s.MarkReadyToPayAsync(_comandaId))
            .Should().ThrowAsync<KeyNotFoundException>().WithMessage("Comanda no encontrada.");
    }

    [Fact]
    public async Task Error409_MapsToInvalidOperationExceptionWithServerMessage()
    {
        _master.OnError(HttpMethod.Post, $"/api/comandas/{_comandaId}/payment-lock", HttpStatusCode.Conflict, "La mesa la está cobrando CAJA-03.");

        await _svc.Invoking(s => s.LockForPaymentAsync(_comandaId, "CAJA-02"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("La mesa la está cobrando CAJA-03.");
    }

    [Fact]
    public async Task Error401_MapsToInvalidOperationExceptionAboutApiKey()
    {
        _master.OnError(HttpMethod.Get, "/api/comandas", HttpStatusCode.Unauthorized, null);

        await _svc.Invoking(s => s.GetActiveComandasAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Terminal.ApiKey*");
    }

    [Fact]
    public async Task Error500_MapsToHttpRequestExceptionOffline()
    {
        _master.OnError(HttpMethod.Get, "/api/comandas", HttpStatusCode.InternalServerError, "boom");

        var ex = await _svc.Invoking(s => s.GetActiveComandasAsync()).Should().ThrowAsync<HttpRequestException>();
        ex.Which.Message.Should().StartWith("Sin conexión con la caja principal").And.Contain("500").And.Contain("boom");
        ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task ErrorWithoutJsonBody_FallsBackToReasonPhrase()
    {
        _master.On(HttpMethod.Get, "/api/comandas", _ => FakeMasterHandler.Raw("<html>nope</html>", HttpStatusCode.Conflict, "text/html"));

        await _svc.Invoking(s => s.GetActiveComandasAsync())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Conflict");
    }

    [Fact]
    public async Task TransportFailure_MapsToHttpRequestExceptionOffline()
    {
        _master.OnThrow(HttpMethod.Get, "/api/tables", new HttpRequestException("No route to host"));

        await _svc.Invoking(s => s.GetTablesAsync())
            .Should().ThrowAsync<HttpRequestException>().WithMessage("Sin conexión con la caja principal: No route to host");
    }

    [Fact]
    public async Task InvalidJsonBody_MapsToHttpRequestExceptionOffline()
    {
        _master.On(HttpMethod.Get, "/api/tables", _ => FakeMasterHandler.Raw("<html>login</html>"));

        await _svc.Invoking(s => s.GetTablesAsync())
            .Should().ThrowAsync<HttpRequestException>().WithMessage("Sin conexión con la caja principal*respuesta inválida*");
    }

    [Fact]
    public async Task NullJsonBody_MapsToHttpRequestExceptionOffline()
    {
        _master.On(HttpMethod.Get, "/api/tables", _ => FakeMasterHandler.Raw("null"));

        await _svc.Invoking(s => s.GetTablesAsync())
            .Should().ThrowAsync<HttpRequestException>().WithMessage("Sin conexión con la caja principal*respuesta vacía*");
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceled()
    {
        _master.On(HttpMethod.Get, "/api/tables", _ => throw new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _svc.Invoking(s => s.GetTablesAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MissingMasterUrl_ThrowsInvalidOperation()
    {
        var svc = new RemoteComandaService(new HttpClient(_master), new TerminalSettings { Code = "CAJA-02", Mode = TerminalKind.Satellite }, _sat, _db, new SalesService(_db, _sat), _clock);

        await svc.Invoking(s => s.GetTablesAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*MasterUrl*");
        _master.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NoApiKey_SendsNoAuthorizationHeader()
    {
        var svc = new RemoteComandaService(new HttpClient(_master), new TerminalSettings { Code = "CAJA-02", Mode = TerminalKind.Satellite, MasterUrl = "http://m:5055" }, _sat, _db, new SalesService(_db, _sat), _clock);
        _master.On(HttpMethod.Get, "/api/tables", Array.Empty<DiningTableDto>());

        await svc.GetTablesAsync();

        _master.Requests.Single().Authorization.Should().BeNull();
    }

    [Fact]
    public async Task MasterUrlTrailingSlash_DoesNotDoubleSlash()
    {
        _master.On(HttpMethod.Get, "/api/tables", Array.Empty<DiningTableDto>());

        await _svc.GetTablesAsync();

        _master.Requests.Single().PathAndQuery.Should().Be("/api/tables");
    }

    [Fact]
    public async Task TerminalCodeInBodies_ComesFromTerminalContextWhenSet()
    {
        // Settings say CAJA-02 but the register was registered as CAJA-05: the context wins.
        var ctx = Terminals.Satellite("CAJA-05");
        var svc = new RemoteComandaService(new HttpClient(_master), _settings, ctx, _db, new SalesService(_db, ctx), _clock);
        var approval = await AddApprovalAsync();
        _master.On(HttpMethod.Post, $"/api/comandas/{_comandaId}/items/{_itemId}/cancel-confirmed", SampleDto());

        await svc.CancelConfirmedItemAsync(_comandaId, _itemId, approval.Id);

        Body(_master.Requests.Single()).GetProperty("terminalCode").GetString().Should().Be("CAJA-05");
    }
}
