using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

public partial class CashShiftService
{
    public async Task<CashShift> OpenShiftAsync(string cashierName, decimal openingAmount, Guid? openedByUserId, CancellationToken ct = default)
    {
        if (openingAmount < 0)
            throw new ArgumentException("El monto de apertura no puede ser negativo.", nameof(openingAmount));

        var existing = await GetCurrentShiftAsync(ct);
        if (existing != null)
            throw new InvalidOperationException($"Ya existe un turno abierto (Turno #{existing.ShiftNumber} – {existing.CashierName}).");

        var lastShift = await _db.CashShifts
            .OrderByDescending(s => s.ShiftNumber)
            .Select(s => s.ShiftNumber)
            .FirstOrDefaultAsync(ct);

        var shift = new CashShift
        {
            ShiftNumber = lastShift + 1,
            CashierName = cashierName,
            OpeningAmount = openingAmount,
            OpenedAt = UtcNow,
            OpenedByUserId = openedByUserId,
            Status = CashShiftStatus.Open
        };

        _db.CashShifts.Add(shift);
        await _db.SaveChangesAsync(ct);
        return shift;
    }

    public async Task<CashMovementDto> RecordMovementAsync(CashMovementRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // --- validate (nothing tracked is modified until every check passed) ---
        if (!CashMovementTypes.Manual.Contains(request.Type))
            throw new ArgumentException("Tipo de movimiento no permitido.", nameof(request));
        if (request.Amount <= 0)
            throw new ArgumentException("El monto debe ser mayor que cero.", nameof(request));
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0)
            throw new ArgumentException("El motivo es obligatorio.", nameof(request));
        if (reason.Length > 200)
            throw new ArgumentException("El motivo admite máximo 200 caracteres.", nameof(request));
        var reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        if (reference is { Length: > 100 })
            throw new ArgumentException("La referencia admite máximo 100 caracteres.", nameof(request));

        var shift = await RequireOpenShiftAsync(ct);

        ManagerOverride? approval = null;
        if (_settings.MovementRequiresManager(request.Type))
            approval = await RequireOverrideAsync(request.ManagerOverrideId, AuthorizedAction.CashMovement, ct);

        if (CashMovementTypes.Sign(request.Type) < 0)
        {
            var expected = (await ComputeAsync(shift, ct)).ExpectedCash;
            if (request.Amount > expected)
                throw new InvalidOperationException(
                    $"El monto ({request.Amount:N0}) supera el efectivo esperado en caja ({expected:N0}).");
        }

        // --- mutate ---
        var movement = new CashMovement
        {
            CashShiftId = shift.Id,
            Type = request.Type,
            Amount = request.Amount,
            Reason = reason,
            Reference = reference,
            UserId = request.UserId,
            UserName = Truncate(request.UserName, 100),
            ManagerOverrideId = approval?.Id,
            OccurredAt = UtcNow,
            CreatedAt = UtcNow
        };
        _db.CashMovements.Add(movement);
        if (approval != null) Consume(approval, $"CashMovement {movement.Id}");

        _db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "CashMovementRecorded",
            AggregateType = "CashMovement",
            AggregateId = movement.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                movementId = movement.Id, shiftId = shift.Id, shiftNumber = shift.ShiftNumber,
                type = movement.Type.ToString(), amount = movement.Amount, reason = movement.Reason,
                reference = movement.Reference, userName = movement.UserName,
                managerOverrideId = movement.ManagerOverrideId, occurredAt = movement.OccurredAt
            }),
            Status = SyncStatus.Pending,
            CreatedAt = UtcNow,
            UpdatedAt = UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return ToDto(movement);
    }

    public async Task<PartialCountDto> RecordPartialCountAsync(IEnumerable<CashCountLineDto> lines, string userName, CancellationToken ct = default)
    {
        var valid = ValidateCountLines(lines, nameof(lines));
        var shift = await RequireOpenShiftAsync(ct);
        var expected = (await ComputeAsync(shift, ct)).ExpectedCash;

        var count = new PartialCashCount
        {
            CashShiftId = shift.Id,
            ExpectedAmount = expected,
            CountedAmount = valid.Sum(l => l.Total),
            LinesJson = JsonSerializer.Serialize(valid.Select(l => new { denomination = l.Denomination, quantity = l.Quantity })),
            UserName = Truncate(userName, 100),
            CountedAt = UtcNow,
            CreatedAt = UtcNow
        };
        _db.PartialCashCounts.Add(count);
        await _db.SaveChangesAsync(ct);

        return new PartialCountDto
        {
            Id = count.Id,
            ExpectedAmount = count.ExpectedAmount,
            CountedAmount = count.CountedAmount,
            UserName = count.UserName,
            CountedAt = count.CountedAt
        };
    }

    public async Task<ShiftReconciliationDto> CloseShiftAsync(CloseShiftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // --- validate (nothing tracked is modified until every check passed) ---
        var lines = ValidateCountLines(request.CountLines, nameof(request));
        await EnsureNoActiveComandasAsync(ct);
        var shift = await RequireOpenShiftAsync(ct);

        var expected = (await ComputeAsync(shift, ct)).ExpectedCash;
        var counted = lines.Sum(l => l.Total);
        var difference = counted - expected;

        if (request.FloatLeftAmount < 0 || request.FloatLeftAmount > counted)
            throw new ArgumentException("El fondo dejado en caja debe estar entre $0 y el efectivo contado.", nameof(request));

        ManagerOverride? approval = null;
        var threshold = _settings.MaxDifferenceWithoutManager;
        if (Math.Abs(difference) > threshold)
        {
            if (request.DifferenceOverrideId == null || string.IsNullOrWhiteSpace(request.Notes))
                throw new CashDifferenceRequiresApprovalException(difference, threshold);
            approval = await RequireOverrideAsync(request.DifferenceOverrideId, AuthorizedAction.CashDifference, ct);
        }

        // --- mutate (one SaveChanges, Outbox included) ---
        foreach (var l in lines)
            _db.CashCountLines.Add(new CashCountLine { CashShiftId = shift.Id, Denomination = l.Denomination, Quantity = l.Quantity });

        var closedBy = string.IsNullOrWhiteSpace(request.ClosedByName) ? shift.CashierName : request.ClosedByName.Trim();
        shift.ExpectedAmount = expected;
        shift.ClosingAmount = counted;
        shift.ClosedAt = UtcNow;
        shift.ClosedByUserId = request.ClosedByUserId;
        shift.ClosedByName = Truncate(closedBy, 100);
        shift.FloatLeftAmount = request.FloatLeftAmount;
        shift.WasBlindCount = request.WasBlindCount;
        shift.DifferenceOverrideId = approval?.Id;
        shift.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        shift.Status = CashShiftStatus.Closed;
        shift.UpdatedAt = UtcNow;
        if (approval != null) Consume(approval, $"CashShift {shift.Id}");

        _db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "CashShiftClosed",
            AggregateType = "CashShift",
            AggregateId = shift.Id,
            PayloadJson = JsonSerializer.Serialize(new
            {
                shiftId = shift.Id, number = shift.ShiftNumber, expected, counted, difference
            }),
            Status = SyncStatus.Pending,
            CreatedAt = UtcNow,
            UpdatedAt = UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return await GetReconciliationAsync(shift.Id, ct);
    }

    private static string Truncate(string? s, int max)
    {
        var t = s?.Trim() ?? string.Empty;
        return t.Length > max ? t[..max] : t;
    }
}
