using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Domain.Entities;
using Core.Domain.Enums;

namespace Presentation.Server.Dtos;

// ─── Wire DTOs (camelCase, enums as strings — see ServerJson.Options) ─────────

/// <summary><c>Terminal</c> is null until the desktop sets <c>ITerminalContext</c> (or when it is not registered at all).</summary>
public sealed record HealthDto(string Status, string Node, string Version, DateTime UtcNow, int ConnectedClients, HealthTerminalDto? Terminal);

/// <summary>Identity of this register as reported by /health (satellites ping it to decide "online").</summary>
public sealed record HealthTerminalDto(string Code, string Name, TerminalKind Kind);

/// <summary>Body of POST /api/sync/inbox responses: 200 for Applied/Duplicate/Ignored, 422 for Rejected.</summary>
public sealed record SyncInboxResponseDto(SyncInboxOutcome Outcome, string? Error);

public sealed record ErrorDto(string Error);

public sealed record KdsSettingsDto(int WarningMinutes, int MaxPrepMinutes, int ArchiveDelaySeconds, IReadOnlyList<string> Stations);

public sealed record KitchenOrderItemDto(
    Guid Id,
    string ProductName,
    decimal Quantity,
    string? Notes,
    bool IsPrepared,
    bool IsCancelled);

public sealed record KitchenOrderDto(
    Guid Id,
    string OrderNumber,
    string TableNumber,
    string WaiterName,
    string Station,
    string? GeneralNotes,
    KitchenOrderStatus Status,
    DateTime CreatedAt,
    DateTime? PrintedAt,
    DateTime? DeliveredAt,
    IReadOnlyList<KitchenOrderItemDto> Items);

public sealed record ProductDto(
    Guid Id,
    string Sku,
    string? Barcode,
    string Name,
    decimal Price,
    bool IsWeighed,
    Guid? CategoryId,
    string? CategoryName);

public sealed record CategoryDto(Guid Id, string Name, string? ColorHex, string? Icon, int SortOrder);

public sealed record TableDto(Guid Id, string Name, Guid? OpenComandaId);

/// <summary>What the comandera tablet needs from <c>ComandaSettings</c> to mirror the desktop's timing and note chips.</summary>
public sealed record ComandaSettingsDto(int ConfirmationDelaySeconds, IReadOnlyList<string> QuickNotes, int MaxNotesLength);

/// <summary>Pushed over /ws/kds for every <see cref="KitchenEvent"/>.</summary>
public sealed record KitchenSocketMessage(
    string Type,
    KitchenEventType Event,
    Guid KitchenOrderId,
    string Station,
    string OrderNumber,
    string TableNumber,
    KitchenOrderStatus Status,
    DateTime OccurredAt);

// ─── Request bodies (comandera tablet) ───────────────────────────────────────

public sealed record OpenComandaRequest(string? TableName, string? WaiterName, Guid? DiningTableId);

public sealed record AddComandaItemRequest(Guid ProductId, decimal? Quantity, decimal? WeightKg, string? Notes);

public sealed record SetItemNotesRequest(string? Notes);

// ─── Serialization + mapping helpers ─────────────────────────────────────────

public static class ServerJson
{
    /// <summary>Same shape for HTTP responses and WebSocket frames: camelCase, enums as strings.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

public static class ServerMapping
{
    public static KitchenOrderDto ToDto(this KitchenOrder ko) => new(
        ko.Id,
        ko.OrderNumber,
        ko.TableNumber,
        ko.WaiterName,
        ko.Station,
        ko.GeneralNotes,
        ko.Status,
        AsUtc(ko.CreatedAt),
        AsUtc(ko.PrintedAt),
        AsUtc(ko.DeliveredAt),
        ko.Items.Select(ToDto).ToList());

    public static KitchenOrderItemDto ToDto(this KitchenOrderItem it) => new(
        it.Id, it.ProductName, it.Quantity, it.Notes, it.IsPrepared, it.IsCancelled);

    public static ProductDto ToDto(this Product p, string? categoryName) => new(
        p.Id, p.Sku, p.Barcode, p.Name, p.Price, p.IsWeighed, p.CategoryId, categoryName ?? p.Category?.Name);

    public static CategoryDto ToDto(this Category c) => new(c.Id, c.Name, c.ColorHex, c.Icon, c.SortOrder);

    public static TableDto ToDto(this DiningTableDto t) => new(t.Id, t.Name, t.OpenComandaId);

    public static KitchenSocketMessage ToSocketMessage(this KitchenEvent e) => new(
        "kitchen", e.Type, e.KitchenOrderId, e.Station, e.OrderNumber, e.TableNumber, e.Status, AsUtc(e.OccurredAt));

    /// <summary>
    /// Timestamps are stored as UTC but come back from SQLite with Kind=Unspecified, which
    /// System.Text.Json writes without the trailing "Z"; browsers would then read them as local time.
    /// </summary>
    public static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    public static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;
}
