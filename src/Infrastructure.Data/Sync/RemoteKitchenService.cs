using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Core.Application.DTOs;
using Core.Application.Kitchen;
using Core.Application.Services;
using Core.Domain.Entities;

namespace Infrastructure.Data.Sync;

/// <summary>
/// IKitchenService over the master's <c>/api/kds/*</c> (KitchenOrderWire ⇄ KitchenOrder) for a SATELLITE register's
/// KDS screen. Mutations go to the master, which publishes the kitchen event to every register through /ws/kds
/// (see <see cref="MasterKitchenSocketClient"/>). <see cref="CreateAsync"/> is not supported here: tickets are created by
/// the master when comanda items are confirmed. Error mapping and timeout: <see cref="MasterHttp"/>.
/// </summary>
public class RemoteKitchenService : IKitchenService
{
    private readonly MasterHttp _master;

    public RemoteKitchenService(HttpClient http, TerminalSettings settings)
    {
        _master = new MasterHttp(http, settings);
    }

    public async Task<IReadOnlyList<KitchenOrder>> GetActiveAsync(string? station = null, CancellationToken ct = default)
    {
        var path = string.IsNullOrWhiteSpace(station)
            ? "/api/kds/orders"
            : "/api/kds/orders?station=" + Uri.EscapeDataString(station.Trim());
        var wires = await _master.GetAsync<List<KitchenOrderWire>>(path, ct);
        return wires.Select(w => w.ToEntity()).ToList();
    }

    public async Task<KitchenOrder?> GetByIdAsync(Guid kitchenOrderId, CancellationToken ct = default)
    {
        try
        {
            var wire = await _master.GetAsync<KitchenOrderWire>($"/api/kds/orders/{kitchenOrderId}", ct);
            return wire.ToEntity();
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    public Task<KitchenOrder> CreateAsync(KitchenOrder order, CancellationToken ct = default) =>
        throw new InvalidOperationException("Los tickets de cocina los crea la caja principal; una caja satélite no puede crearlos.");

    public Task<KitchenOrder> AdvanceAsync(Guid kitchenOrderId, CancellationToken ct = default) => MutateAsync(kitchenOrderId, "advance", ct);

    public Task<KitchenOrder> MarkPrintedAsync(Guid kitchenOrderId, CancellationToken ct = default) => MutateAsync(kitchenOrderId, "printed", ct);

    public Task<KitchenOrder> UndoDeliverAsync(Guid kitchenOrderId, CancellationToken ct = default) => MutateAsync(kitchenOrderId, "undo", ct);

    private async Task<KitchenOrder> MutateAsync(Guid id, string action, CancellationToken ct)
    {
        var wire = await _master.PostAsync<KitchenOrderWire>($"/api/kds/orders/{id}/{action}", null, ct);
        return wire.ToEntity();
    }
}
