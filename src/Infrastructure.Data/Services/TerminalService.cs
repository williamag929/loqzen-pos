using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Services;

/// <summary>
/// Registry of cash registers (terminals). On a satellite the table holds only itself; on the master it also
/// holds every satellite that ever sent a heartbeat or an Outbox envelope. Codes are stored normalized
/// (<see cref="TerminalSettings.NormalizeCode"/>) so lookups are plain equality.
/// </summary>
public class TerminalService : ITerminalService
{
    private const int MaxNameLength = 60;
    private const int MaxAppVersionLength = 40;
    private const int MaxAddressLength = 64;

    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ITerminalContext? _terminal;

    public TerminalService(PosDbContext db, TimeProvider? clock = null, ITerminalContext? terminal = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _terminal = terminal;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task<TerminalDto> EnsureRegisteredAsync(string code, string name, TerminalKind kind, string? appVersion = null, CancellationToken ct = default)
    {
        var normalized = TerminalSettings.NormalizeCode(code);
        var cleanName = Truncate(string.IsNullOrWhiteSpace(name) ? normalized : name, MaxNameLength);
        var now = UtcNow;

        var row = await _db.Terminals.FirstOrDefaultAsync(t => t.Code == normalized, ct);
        if (row == null)
        {
            row = new Terminal { Code = normalized, CreatedAt = now };
            _db.Terminals.Add(row);
        }
        else
        {
            row.UpdatedAt = now;
        }

        row.Name = cleanName;
        row.Kind = kind;
        row.IsActive = true;
        row.AppVersion = Truncate(appVersion, MaxAppVersionLength, allowNull: true);
        row.LastSeenAt = now;

        if (kind == TerminalKind.Master)
            await AdoptLegacyRowsAsync(row, ct);

        await _db.SaveChangesAsync(ct);
        return await ToDtoWithShiftAsync(row, ct);
    }

    /// <summary>
    /// Rows written before multi-register support have no terminal. They were created on this (the only) register,
    /// which is now the master: stamp them so scoped queries and the day consolidation keep seeing them.
    /// Tracked updates, committed by the caller's SaveChanges together with the terminal row. After the first
    /// start there is nothing left to adopt, so the three queries are cheap.
    /// </summary>
    private async Task AdoptLegacyRowsAsync(Terminal master, CancellationToken ct)
    {
        foreach (var s in await _db.CashShifts.Where(s => s.TerminalId == null).ToListAsync(ct)) Stamp(s, master);
        foreach (var o in await _db.Orders.Where(o => o.TerminalId == null).ToListAsync(ct)) Stamp(o, master);
        foreach (var b in await _db.BillPayments.Where(b => b.TerminalId == null).ToListAsync(ct)) Stamp(b, master);
    }

    private static void Stamp(CashShift s, Terminal t) { s.TerminalId = t.Id; s.TerminalCode = t.Code; }
    private static void Stamp(Order o, Terminal t) { o.TerminalId = t.Id; o.TerminalCode = t.Code; }
    private static void Stamp(BillPayment b, Terminal t) { b.TerminalId = t.Id; b.TerminalCode = t.Code; }

    public async Task<IReadOnlyList<TerminalDto>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _db.Terminals.AsNoTracking().ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var open = await _db.CashShifts.AsNoTracking()
            .Where(s => s.Status == CashShiftStatus.Open && s.TerminalId != null && ids.Contains(s.TerminalId.Value))
            .Select(s => new { s.TerminalId, s.ShiftNumber, s.CashierName })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.Kind == TerminalKind.Master ? 0 : 1)
            .ThenBy(r => r.Code, StringComparer.Ordinal)
            .Select(r =>
            {
                var dto = ToDto(r);
                var shift = open.Where(s => s.TerminalId == r.Id).OrderByDescending(s => s.ShiftNumber).FirstOrDefault();
                if (shift != null)
                {
                    dto.OpenShiftNumber = shift.ShiftNumber;
                    dto.OpenShiftCashier = shift.CashierName;
                }
                return dto;
            })
            .ToList();
    }

    public async Task<TerminalDto?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        string normalized;
        try { normalized = TerminalSettings.NormalizeCode(code); }
        catch (ArgumentException) { return null; }

        var row = await _db.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.Code == normalized, ct);
        return row == null ? null : await ToDtoWithShiftAsync(row, ct);
    }

    public async Task<TerminalDto> HeartbeatAsync(TerminalHeartbeatRequest request, string? remoteAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = TerminalSettings.NormalizeCode(request.Code);
        if (request.Kind == TerminalKind.Master)
            throw new InvalidOperationException("Una caja principal no puede reportarse como satélite.");
        if (_terminal is { IsSet: true } && string.Equals(_terminal.Code, normalized, StringComparison.Ordinal))
            throw new InvalidOperationException($"El código {normalized} es el de esta misma caja.");
        if (_terminal is { IsSet: true, Kind: TerminalKind.Satellite })
            throw new InvalidOperationException("Solo la caja principal recibe latidos de otras cajas.");

        var now = UtcNow;
        var row = await _db.Terminals.FirstOrDefaultAsync(t => t.Code == normalized, ct);
        if (row == null)
        {
            row = new Terminal { Code = normalized, CreatedAt = now };
            _db.Terminals.Add(row);
        }
        else
        {
            row.UpdatedAt = now;
        }

        row.Name = Truncate(string.IsNullOrWhiteSpace(request.Name) ? normalized : request.Name, MaxNameLength);
        row.Kind = request.Kind;
        row.IsActive = true;
        row.AppVersion = Truncate(request.AppVersion, MaxAppVersionLength, allowNull: true);
        row.LastAddress = Truncate(remoteAddress, MaxAddressLength, allowNull: true);
        row.LastSeenAt = now;

        await _db.SaveChangesAsync(ct);
        return await ToDtoWithShiftAsync(row, ct);
    }

    public async Task MarkCatalogPulledAsync(CancellationToken ct = default)
    {
        if (_terminal is not { IsSet: true })
            throw new InvalidOperationException("La caja no está registrada todavía.");

        var row = await _db.Terminals.FirstOrDefaultAsync(t => t.Code == _terminal.Code, ct)
            ?? throw new InvalidOperationException("La caja no está registrada todavía.");
        var now = UtcNow;
        row.LastCatalogPullAt = now;
        row.LastSeenAt = now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<TerminalDto> ToDtoWithShiftAsync(Terminal row, CancellationToken ct)
    {
        var dto = ToDto(row);
        var shift = await _db.CashShifts.AsNoTracking()
            .Where(s => s.Status == CashShiftStatus.Open && s.TerminalId == row.Id)
            .OrderByDescending(s => s.ShiftNumber)
            .Select(s => new { s.ShiftNumber, s.CashierName })
            .FirstOrDefaultAsync(ct);
        if (shift != null)
        {
            dto.OpenShiftNumber = shift.ShiftNumber;
            dto.OpenShiftCashier = shift.CashierName;
        }
        return dto;
    }

    internal static TerminalDto ToDto(Terminal t) => new()
    {
        Id = t.Id,
        Code = t.Code,
        Name = t.Name,
        Kind = t.Kind,
        IsActive = t.IsActive,
        LastSeenAt = t.LastSeenAt,
        LastSyncAt = t.LastSyncAt,
        LastCatalogPullAt = t.LastCatalogPullAt,
        AppVersion = t.AppVersion,
        LastAddress = t.LastAddress
    };

    private static string Truncate(string? s, int max) => Truncate(s, max, allowNull: false)!;

    private static string? Truncate(string? s, int max, bool allowNull)
    {
        var t = s?.Trim() ?? string.Empty;
        if (t.Length == 0) return allowNull ? null : string.Empty;
        return t.Length > max ? t[..max] : t;
    }
}
