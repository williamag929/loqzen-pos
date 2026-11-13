using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Services;
using Core.Domain.Common;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data.Sync;

/// <summary>
/// Catalog replication master → satellite. The master serves every row (active and inactive) so a deactivation
/// propagates; the satellite upserts by id and deactivates whatever the snapshot no longer lists (never deletes
/// rows that sales reference).
/// <para>
/// A fresh satellite is seeded with the demo catalog under its OWN ids, so the first pull meets rows with a
/// different id but the same natural key (Product.Sku, Category/Tax/Biller/User.Name). Those local twins are
/// resolved before the master's row is inserted: an unreferenced twin is deleted; a referenced one (sold products,
/// categories/taxes still used by local products, billers with payments, users with shifts/overrides/movements)
/// is kept but deactivated and its key mangled ("SKU~1a2b3c4d", "Nombre (local)") so the unique index holds and
/// PIN login stays unambiguous. Names compare case- and accent-insensitively, like the biller duplicate check.
/// </para>
/// Deletes/mangles are saved before inserts inside one transaction, so a unique index never fires mid-way.
/// </summary>
public class CatalogSyncService : ICatalogSyncService
{
    private const string LocalSuffix = " (local)";

    private readonly PosDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ITerminalContext? _terminal;

    public CatalogSyncService(PosDbContext db, TimeProvider? clock = null, ITerminalContext? terminal = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _terminal = terminal;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public async Task<CatalogSnapshotDto> BuildSnapshotAsync(CancellationToken ct = default)
    {
        var categories = await _db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var taxes = await _db.Taxes.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var products = await _db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var billers = await _db.Billers.AsNoTracking().OrderBy(b => b.SortOrder).ThenBy(b => b.Name).ToListAsync(ct);
        var users = await _db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync(ct);

        return new CatalogSnapshotDto
        {
            GeneratedAt = UtcNow,
            MasterCode = _terminal is { IsSet: true } ? _terminal.Code : string.Empty,
            Categories = categories.Select(c => new SnapshotCategory(c.Id, c.Name, c.ColorHex, c.Icon, c.SortOrder, c.IsActive)).ToList(),
            Taxes = taxes.Select(t => new SnapshotTax(t.Id, t.Name, t.Rate, t.IsActive)).ToList(),
            Products = products.Select(p => new SnapshotProduct(
                p.Id, p.Sku, p.Barcode, p.Name, p.Description, p.Price, p.Cost, p.IsWeighed, p.IsActive, p.CategoryId, p.TaxId)).ToList(),
            Billers = billers.Select(b => new SnapshotBiller(
                b.Id, b.Name, b.Code, b.CommissionFixed, b.CommissionPercent, b.ColorHex, b.SortOrder, b.IsActive)).ToList(),
            Users = users.Select(u => new SnapshotUser(u.Id, u.Name, u.PinHash, u.Role, u.IsActive)).ToList()
        };
    }

    public async Task<CatalogApplyResult> ApplySnapshotAsync(CatalogSnapshotDto snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_terminal is { IsSet: true, Kind: TerminalKind.Master })
            throw new InvalidOperationException("La caja principal es dueña del catálogo: no puede aplicar un snapshot.");

        var now = UtcNow;
        var counts = new Counts();

        // ── load local state + what references it ────────────────────────────
        var categories = await _db.Categories.ToListAsync(ct);
        var taxes = await _db.Taxes.ToListAsync(ct);
        var products = await _db.Products.ToListAsync(ct);
        var billers = await _db.Billers.ToListAsync(ct);
        var users = await _db.Users.ToListAsync(ct);

        var productRefs = new HashSet<Guid>(await _db.OrderItems.Select(i => i.ProductId).Distinct().ToListAsync(ct));
        productRefs.UnionWith(await _db.ComandaItems.Select(i => i.ProductId).Distinct().ToListAsync(ct));
        productRefs.UnionWith(await _db.KitchenOrderItems.Select(i => i.ProductId).Distinct().ToListAsync(ct));
        var billerRefs = new HashSet<Guid>(await _db.BillPayments.Select(b => b.BillerId).Distinct().ToListAsync(ct));
        var userRefs = new HashSet<Guid>();
        userRefs.UnionWith(await _db.CashShifts.Select(s => s.CashierId).Distinct().ToListAsync(ct));
        userRefs.UnionWith((await _db.CashShifts.Select(s => s.OpenedByUserId).Distinct().ToListAsync(ct)).Where(x => x != null).Select(x => x!.Value));
        userRefs.UnionWith((await _db.CashShifts.Select(s => s.ClosedByUserId).Distinct().ToListAsync(ct)).Where(x => x != null).Select(x => x!.Value));
        userRefs.UnionWith(await _db.ManagerOverrides.Select(o => o.ManagerUserId).Distinct().ToListAsync(ct));
        userRefs.UnionWith((await _db.ManagerOverrides.Select(o => o.RequestedByUserId).Distinct().ToListAsync(ct)).Where(x => x != null).Select(x => x!.Value));
        userRefs.UnionWith((await _db.CashMovements.Select(m => m.UserId).Distinct().ToListAsync(ct)).Where(x => x != null).Select(x => x!.Value));
        userRefs.UnionWith((await _db.BillPayments.Select(b => b.UserId).Distinct().ToListAsync(ct)).Where(x => x != null).Select(x => x!.Value));
        userRefs.UnionWith((await _db.KitchenOrders.Select(k => k.WaiterId).Distinct().ToListAsync(ct)).Where(x => x != null).Select(x => x!.Value));

        // ── phase 1: resolve local twins (delete or mangle) ──────────────────
        // Products first: a deleted product stops referencing its category/tax, which may then be deletable too.
        var deleted = new HashSet<Guid>();
        ResolveTwins(products, snapshot.Products, p => p.Id, s => s.Id, p => p.Sku, s => s.Sku,
            p => productRefs.Contains(p.Id),
            (p, now) => { p.Sku = Mangle(p.Sku, p.Id, 50); p.IsActive = false; p.UpdatedAt = now; },
            p => _db.Products.Remove(p), deleted, now, counts);
        bool CategoryUsed(Category c) => products.Any(p => !deleted.Contains(p.Id) && p.CategoryId == c.Id);
        bool TaxUsed(Tax t) => products.Any(p => !deleted.Contains(p.Id) && p.TaxId == t.Id);
        ResolveTwins(categories, snapshot.Categories, c => c.Id, s => s.Id, c => c.Name, s => s.Name,
            CategoryUsed, (c, now) => { c.Name = MangleName(c.Name, 100); c.IsActive = false; c.UpdatedAt = now; },
            c => _db.Categories.Remove(c), deleted, now, counts);
        ResolveTwins(taxes, snapshot.Taxes, t => t.Id, s => s.Id, t => t.Name, s => s.Name,
            TaxUsed, (t, now) => { t.Name = MangleName(t.Name, 50); t.IsActive = false; t.UpdatedAt = now; },
            t => _db.Taxes.Remove(t), deleted, now, counts);
        ResolveTwins(billers, snapshot.Billers, b => b.Id, s => s.Id, b => b.Name, s => s.Name,
            b => billerRefs.Contains(b.Id), (b, now) => { b.Name = MangleName(b.Name, 100); b.IsActive = false; b.UpdatedAt = now; },
            b => _db.Billers.Remove(b), deleted, now, counts);
        ResolveTwins(users, snapshot.Users, u => u.Id, s => s.Id, u => u.Name, s => s.Name,
            u => userRefs.Contains(u.Id), (u, now) => { u.Name = MangleName(u.Name, 100); u.IsActive = false; u.UpdatedAt = now; },
            u => _db.Users.Remove(u), deleted, now, counts);

        categories.RemoveAll(c => deleted.Contains(c.Id));
        taxes.RemoveAll(t => deleted.Contains(t.Id));
        products.RemoveAll(p => deleted.Contains(p.Id));
        billers.RemoveAll(b => deleted.Contains(b.Id));
        users.RemoveAll(u => deleted.Contains(u.Id));

        // Relational providers: commit deletes/mangles first so inserts never hit the unique indexes.
        // (The InMemory provider enforces no indexes and rejects transactions: single SaveChanges there.)
        var relational = _db.Database.IsRelational();
        await using var tx = relational ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            if (relational && _db.ChangeTracker.HasChanges())
                await SaveAsync(ct);

            // ── phase 2: upsert by id, deactivate what the master no longer lists ──
            Upsert(categories, snapshot.Categories, s => s.Id, s => new Category { Id = s.Id, CreatedAt = now }, _db.Categories,
                (c, s) => { c.Name = s.Name; c.ColorHex = s.ColorHex; c.Icon = s.Icon; c.SortOrder = s.SortOrder; c.IsActive = s.IsActive; },
                c => c.IsActive, c => c.IsActive = false, now, counts);
            Upsert(taxes, snapshot.Taxes, s => s.Id, s => new Tax { Id = s.Id, CreatedAt = now }, _db.Taxes,
                (t, s) => { t.Name = s.Name; t.Rate = s.Rate; t.IsActive = s.IsActive; },
                t => t.IsActive, t => t.IsActive = false, now, counts);
            Upsert(products, snapshot.Products, s => s.Id, s => new Product { Id = s.Id, CreatedAt = now }, _db.Products,
                (p, s) =>
                {
                    p.Sku = s.Sku; p.Barcode = s.Barcode; p.Name = s.Name; p.Description = s.Description;
                    p.Price = s.Price; p.Cost = s.Cost; p.IsWeighed = s.IsWeighed; p.IsActive = s.IsActive;
                    p.CategoryId = s.CategoryId; p.TaxId = s.TaxId;
                    // StockQuantity is intentionally not replicated: stock lives on the master.
                },
                p => p.IsActive, p => p.IsActive = false, now, counts);
            Upsert(billers, snapshot.Billers, s => s.Id, s => new Biller { Id = s.Id, CreatedAt = now }, _db.Billers,
                (b, s) =>
                {
                    b.Name = s.Name; b.Code = s.Code; b.CommissionFixed = s.CommissionFixed; b.CommissionPercent = s.CommissionPercent;
                    b.ColorHex = s.ColorHex; b.SortOrder = s.SortOrder; b.IsActive = s.IsActive;
                },
                b => b.IsActive, b => b.IsActive = false, now, counts);
            Upsert(users, snapshot.Users, s => s.Id, s => new User { Id = s.Id, CreatedAt = now }, _db.Users,
                // LastLoginAt is local to this register and is left untouched.
                (u, s) => { u.Name = s.Name; u.PinHash = s.PinHash; u.Role = s.Role; u.IsActive = s.IsActive; },
                u => u.IsActive, u => u.IsActive = false, now, counts);

            await SaveAsync(ct);
            if (tx != null) await tx.CommitAsync(ct);
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync(ct);
            throw;
        }

        return new CatalogApplyResult(counts.Inserted, counts.Updated, counts.Deactivated);
    }

    // ── generic steps ────────────────────────────────────────────────────────

    private sealed class Counts
    {
        public int Inserted, Updated, Deactivated;
    }

    /// <summary>
    /// For every snapshot row whose id is unknown locally, finds a local row with the same natural key and either
    /// deletes it (unreferenced) or mangles + deactivates it (referenced), so the master's row can be inserted.
    /// </summary>
    private static void ResolveTwins<TLocal, TSnap>(
        List<TLocal> locals, IEnumerable<TSnap> snapshot,
        Func<TLocal, Guid> localId, Func<TSnap, Guid> snapId,
        Func<TLocal, string> localKey, Func<TSnap, string> snapKey,
        Func<TLocal, bool> isReferenced, Action<TLocal, DateTime> mangle, Action<TLocal> remove,
        HashSet<Guid> deleted, DateTime now, Counts counts)
        where TLocal : BaseEntity
    {
        var ids = locals.Select(localId).ToHashSet();
        var byKey = new Dictionary<string, List<TLocal>>(StringComparer.Ordinal);
        foreach (var l in locals)
        {
            var k = NaturalKey(localKey(l));
            if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = new List<TLocal>();
            list.Add(l);
        }

        foreach (var s in snapshot)
        {
            if (ids.Contains(snapId(s))) continue;
            if (!byKey.TryGetValue(NaturalKey(snapKey(s)), out var twins)) continue;
            foreach (var twin in twins.Where(t => !deleted.Contains(localId(t))).ToList())
            {
                if (isReferenced(twin))
                {
                    mangle(twin, now);
                    counts.Deactivated++;
                }
                else
                {
                    remove(twin);
                    deleted.Add(localId(twin));
                }
            }
        }
    }

    private static void Upsert<TLocal, TSnap>(
        List<TLocal> locals, IEnumerable<TSnap> snapshot, Func<TSnap, Guid> snapId,
        Func<TSnap, TLocal> create, DbSet<TLocal> set, Action<TLocal, TSnap> copy,
        Func<TLocal, bool> isActive, Action<TLocal> deactivate, DateTime now, Counts counts)
        where TLocal : BaseEntity
    {
        var byId = locals.ToDictionary(l => l.Id);
        var seen = new HashSet<Guid>();
        foreach (var s in snapshot)
        {
            var id = snapId(s);
            seen.Add(id);
            if (!byId.TryGetValue(id, out var row))
            {
                row = create(s);
                set.Add(row);
                counts.Inserted++;
            }
            else
            {
                row.UpdatedAt = now;
                counts.Updated++;
            }
            copy(row, s);
        }
        foreach (var row in locals.Where(l => !seen.Contains(l.Id) && isActive(l)))
        {
            deactivate(row);
            row.UpdatedAt = now;
            counts.Deactivated++;
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The UI logs ex.Message: make the SQLite reason (unique index, FK) visible there.
            throw new InvalidOperationException(
                $"No se pudo guardar el catálogo: {ex.InnerException?.Message ?? ex.Message}", ex);
        }
    }

    // ── keys ─────────────────────────────────────────────────────────────────

    /// <summary>Case- and accent-insensitive key: "Café" == "CAFE".</summary>
    private static string NaturalKey(string? value)
    {
        var s = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    /// <summary>"{sku}~{first 8 chars of id}", trimmed to the column length.</summary>
    private static string Mangle(string sku, Guid id, int max)
    {
        var suffix = "~" + id.ToString("N")[..8];
        var head = sku.Length + suffix.Length > max ? sku[..Math.Max(0, max - suffix.Length)] : sku;
        return head + suffix;
    }

    /// <summary>"{name} (local)", trimmed to the column length.</summary>
    private static string MangleName(string name, int max)
    {
        var head = name.Length + LocalSuffix.Length > max ? name[..Math.Max(0, max - LocalSuffix.Length)] : name;
        return head + LocalSuffix;
    }
}
