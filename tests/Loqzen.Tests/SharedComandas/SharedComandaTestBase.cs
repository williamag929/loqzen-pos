using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Services;
using Infrastructure.Data.Sync;
using Loqzen.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Loqzen.Tests.SharedComandas;

/// <summary>
/// Master register (CAJA-01) on a REAL SQLite database: <see cref="Service"/> is the ComandaService every satellite
/// talks to, with a recording kitchen event bus and a controllable clock. Two products (kitchen + bar station).
/// </summary>
public abstract class SharedComandaTestBase : IDisposable
{
    public const string MasterCode = "CAJA-01";
    public const string SatelliteCode = "CAJA-02";
    public const string OtherSatelliteCode = "CAJA-03";

    protected readonly SqliteDb MasterDb = SqliteDb.Create();
    protected readonly PosDbContext Db;
    protected readonly TestClock Clock = new();
    protected readonly KitchenEventBus Bus = new();
    protected readonly List<KitchenEvent> Events = new();
    protected readonly TerminalContext Master = Terminals.Master(MasterCode);
    protected readonly ComandaSettings Settings = new() { ConfirmationDelaySeconds = 30, PaymentLockMinutes = 10 };
    protected readonly ComandaService Service;
    protected readonly Product Burger;
    protected readonly Product Soda;

    protected SharedComandaTestBase()
    {
        Db = MasterDb.NewContext();
        Bus.Subscribe(Events.Add);
        Service = new ComandaService(Db, Settings, Clock, Bus, Master);

        var tax = new Tax { Name = "IVA", Rate = 0.19m };
        var food = new Category { Name = "Comidas" };
        var drinks = new Category { Name = "Bebidas" };
        Burger = new Product { Sku = "B1", Name = "Hamburguesa", Price = 20000m, Tax = tax, Category = food };
        Soda = new Product { Sku = "S1", Name = "Gaseosa", Price = 5000m, Tax = tax, Category = drinks };
        Db.AddRange(tax, food, drinks, Burger, Soda);
        Db.SaveChanges();
    }

    protected DateTime Now => Clock.GetUtcNow().UtcDateTime;

    /// <summary>Open comanda with one burger + one soda, both still Pending.</summary>
    protected async Task<ComandaDto> OpenWithItemsAsync(string table = "Mesa 1")
    {
        var c = await Service.OpenComandaAsync(table, "Ana");
        await Service.AddItemAsync(c.Id, Burger.Id);
        return await Service.AddItemAsync(c.Id, Soda.Id);
    }

    protected async Task<Guid> OpenShiftAsync()
    {
        var shift = new CashShift { ShiftNumber = 1, CashierName = "Caja", Status = CashShiftStatus.Open, TerminalId = Master.Id, TerminalCode = Master.Code };
        Db.CashShifts.Add(shift);
        await Db.SaveChangesAsync();
        return shift.Id;
    }

    protected async Task<Guid> CreateCancelOverrideAsync()
    {
        var o = new ManagerOverride
        {
            Action = AuthorizedAction.CancelKitchenItem,
            Description = "Anular producto en cocina",
            ManagerUserId = Guid.NewGuid(),
            ManagerName = "Gerente",
            AuthorizedAt = Now
        };
        Db.ManagerOverrides.Add(o);
        await Db.SaveChangesAsync();
        return o.Id;
    }

    /// <summary>Fresh read of the row through a new context (what another request / register would see).</summary>
    protected Comanda Reload(Guid comandaId)
    {
        using var ctx = MasterDb.NewContext();
        return ctx.Comandas.Include(c => c.Items).Single(c => c.Id == comandaId);
    }

    protected ComandaRemoteCloseRequest RemoteClose(Guid comandaId, string terminalCode = SatelliteCode, Guid? orderId = null,
        string orderNumber = "ORD-000123", PaymentMethod method = PaymentMethod.Cash, decimal total = 29750m, string? reference = null, DateTime? closedAt = null)
        => new(comandaId, terminalCode, orderId ?? Guid.NewGuid(), orderNumber, method, total, reference, closedAt ?? Now);

    protected IEnumerable<KitchenEvent> ComandaChangedEvents => Events.Where(e => e.Type == KitchenEventType.ComandaChanged);

    public void Dispose()
    {
        Db.Dispose();
        MasterDb.Dispose();
    }
}

/// <summary>Shared JSON helpers mirroring the master server / MasterHttp wire format (camelCase, enums as names).</summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static string Serialize(object o) => JsonSerializer.Serialize(o, Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>
    /// The REAL Outbox payload builder (Infrastructure.Data.Outbox.OutboxPayloads.ComandaClosedRemotely is internal):
    /// invoked by reflection so these tests feed the inbox exactly what the satellite writes.
    /// </summary>
    public static string ComandaClosedRemotelyPayload(ComandaRemoteCloseRequest r)
    {
        var type = typeof(SyncInboxService).Assembly.GetType("Infrastructure.Data.Outbox.OutboxPayloads")
                   ?? throw new InvalidOperationException("OutboxPayloads not found in Infrastructure.Data.");
        var method = type.GetMethod("ComandaClosedRemotely", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                     ?? throw new InvalidOperationException("OutboxPayloads.ComandaClosedRemotely not found.");
        return (string)method.Invoke(null, new object[] { r })!;
    }

    public static SyncEnvelopeDto Envelope(string eventType, Guid aggregateId, string senderCode, string payloadJson, DateTime occurredAt, Guid? id = null)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return new SyncEnvelopeDto
        {
            Id = id ?? Guid.NewGuid(),
            EventType = eventType,
            AggregateType = "Comanda",
            AggregateId = aggregateId,
            OccurredAt = occurredAt,
            TerminalCode = senderCode,
            Payload = doc.RootElement.Clone()
        };
    }
}
