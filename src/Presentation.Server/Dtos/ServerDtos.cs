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

// /ws/kds frames are Core.Application.Kitchen.KitchenSocketEnvelope (Type "event"); KDS tickets travel as
// Core.Application.Kitchen.KitchenOrderWire — both defined in the contract (KitchenEvents.cs), mapped below.

// ─── Request bodies (comandera tablet) ───────────────────────────────────────

public sealed record OpenComandaRequest(string? TableName, string? WaiterName, Guid? DiningTableId);

public sealed record AddComandaItemRequest(Guid ProductId, decimal? Quantity, decimal? WeightKg, string? Notes);

public sealed record SetItemNotesRequest(string? Notes);

/// <summary>Body of POST /api/comandas/{id}/items/{itemId}/hold.</summary>
public sealed record SetItemHoldRequest(bool OnHold);

// ─── Request bodies (satellite register → master) ────────────────────────────

/// <summary>
/// Body of POST /api/comandas/{id}/items/{itemId}/cancel-confirmed. The manager already approved on the satellite
/// (PIN there); the master records its own single-use ManagerOverride (Action CancelKitchenItem, ManagerName,
/// Description "Anulación desde {terminalCode}", RequestedByName = terminalCode, Reference = reason) and consumes it.
/// </summary>
public sealed record CancelConfirmedItemRequest(string? ManagerName, string? TerminalCode, string? Reason);

/// <summary>Body of POST /api/comandas/{id}/payment-lock and /payment-unlock.</summary>
public sealed record PaymentLockRequest(string? TerminalCode);

// ─── Pay at table (datáfonos) ────────────────────────────────────────────────

/// <summary>What the comandera needs to pick a terminal (subset of <c>PaymentTerminalDto</c>).</summary>
public sealed record PaymentTerminalListDto(
    Guid Id,
    string Name,
    PaymentTerminalProvider Provider,
    string ProviderLabel,
    string? AssignedUserName,
    string AssignmentLabel,
    bool? LastPingOk,
    DateTime? LastSeenAt);

/// <summary>Body of POST /api/payment-terminals/{id}/assign. Null/empty = back to the register.</summary>
public sealed record AssignTerminalRequest(string? UserName);

/// <summary>Body of POST /api/comandas/{id}/pay-on-terminal.</summary>
public sealed record PayOnTerminalRequest(Guid TerminalId, string? WaiterName);

/// <summary>
/// Outcome of pay-on-terminal. <c>Success</c> = charge approved AND comanda closed. <c>Charge</c> is the persisted
/// audit row (status, authorizationCode, cardBrand, cardLast4, message, referenceText…); <c>Payment</c> is null unless
/// the charge was approved; <c>Comanda</c> is reloaded after the operation (Closed on success, else ReadyToPay).
/// </summary>
public sealed record PayOnTerminalResponseDto(bool Success, TerminalPaymentDto Charge, PaymentResultDto? Payment, ComandaDto Comanda);

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
    /// <summary>Full ticket for /api/kds/* (contract record), with every timestamp forced to UTC kind.</summary>
    public static KitchenOrderWire ToWire(this KitchenOrder ko)
    {
        var wire = KitchenOrderWire.From(ko);
        return wire with
        {
            CreatedAt = AsUtc(wire.CreatedAt),
            UpdatedAt = AsUtc(wire.UpdatedAt),
            PrintedAt = AsUtc(wire.PrintedAt),
            DeliveredAt = AsUtc(wire.DeliveredAt),
            Items = wire.Items.Select(i => i with { CreatedAt = AsUtc(i.CreatedAt) }).ToList()
        };
    }

    public static ProductDto ToDto(this Product p, string? categoryName) => new(
        p.Id, p.Sku, p.Barcode, p.Name, p.Price, p.IsWeighed, p.CategoryId, categoryName ?? p.Category?.Name);

    public static CategoryDto ToDto(this Category c) => new(c.Id, c.Name, c.ColorHex, c.Icon, c.SortOrder);

    public static TableDto ToDto(this DiningTableDto t) => new(t.Id, t.Name, t.OpenComandaId);

    public static PaymentTerminalListDto ToDto(this PaymentTerminalDto t) => new(
        t.Id, t.Name, t.Provider, t.ProviderLabel, t.AssignedUserName, t.AssignmentLabel, t.LastPingOk, AsUtc(t.LastSeenAt));

    /// <summary>One /ws/kds frame (contract record, Type "event").</summary>
    public static KitchenSocketEnvelope ToSocketEnvelope(this KitchenEvent e) => new(
        "event", e.Type, e.KitchenOrderId, e.Station ?? string.Empty, e.OrderNumber ?? string.Empty,
        e.TableNumber ?? string.Empty, e.Status, AsUtc(e.OccurredAt));

    /// <summary>
    /// Timestamps are stored as UTC but come back from SQLite with Kind=Unspecified, which
    /// System.Text.Json writes without the trailing "Z"; browsers would then read them as local time.
    /// </summary>
    public static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    public static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;
}
