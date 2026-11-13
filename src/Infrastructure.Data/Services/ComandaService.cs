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
/// Multi-table comanda workflow: Pending (yellow) items auto-confirm after a configurable delay,
/// at which point a KitchenOrder is created for the KDS. Closing generates the invoice (Order).
/// All state changes + Outbox messages are committed in a single SaveChanges (Offline-First).
/// Split into partial files: queries/open (this), Items, Close, Helpers.
/// </summary>
public partial class ComandaService : IComandaService
{
    private readonly PosDbContext _db;
    private readonly ComandaSettings _settings;
    private readonly TimeProvider _clock;

    public ComandaService(PosDbContext db, ComandaSettings settings, TimeProvider clock)
    {
        _db = db;
        _settings = settings;
        _clock = clock;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private static readonly ComandaStatus[] ActiveStatuses = { ComandaStatus.Open, ComandaStatus.ReadyToPay };

    public async Task<IReadOnlyList<DiningTableDto>> GetTablesAsync(CancellationToken ct = default)
    {
        var tables = await _db.DiningTables.Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(ct);
        var open = await _db.Comandas
            .Where(c => c.DiningTableId != null && ActiveStatuses.Contains(c.Status))
            .Select(c => new { c.Id, c.DiningTableId })
            .ToListAsync(ct);

        return tables.Select(t => new DiningTableDto
        {
            Id = t.Id,
            Name = t.Name,
            OpenComandaId = open.FirstOrDefault(o => o.DiningTableId == t.Id)?.Id
        }).ToList();
    }

    public async Task<IReadOnlyList<ComandaDto>> GetActiveComandasAsync(CancellationToken ct = default)
    {
        var list = await _db.Comandas.Include(c => c.Items)
            .Where(c => ActiveStatuses.Contains(c.Status))
            .OrderBy(c => c.OpenedAt)
            .ToListAsync(ct);
        return list.Select(MapToDto).ToList();
    }

    public async Task<ComandaDto?> GetByIdAsync(Guid comandaId, CancellationToken ct = default)
    {
        var c = await LoadAsync(comandaId, ct);
        return c == null ? null : MapToDto(c);
    }

    public async Task<ComandaDto> OpenComandaAsync(string tableName, string waiterName, Guid? diningTableId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("La mesa es obligatoria.", nameof(tableName));

        var name = tableName.Trim();
        var busy = await _db.Comandas.AnyAsync(c =>
            ActiveStatuses.Contains(c.Status) &&
            ((diningTableId != null && c.DiningTableId == diningTableId) || c.TableName == name), ct);
        if (busy)
            throw new InvalidOperationException($"{name} ya tiene una comanda abierta.");

        var last = await _db.Comandas.OrderByDescending(c => c.Number).Select(c => c.Number).FirstOrDefaultAsync(ct);
        var comanda = new Comanda
        {
            Number = last + 1,
            DiningTableId = diningTableId,
            TableName = name,
            WaiterName = string.IsNullOrWhiteSpace(waiterName) ? "Mesero" : waiterName.Trim(),
            Status = ComandaStatus.Open,
            OpenedAt = UtcNow,
            UpdatedAt = UtcNow
        };
        _db.Comandas.Add(comanda);
        await _db.SaveChangesAsync(ct);
        return MapToDto(comanda);
    }
}
