using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Core.Domain.Entities;
using Core.Domain.Enums;
using Infrastructure.Data.Context;
using Infrastructure.Data.Seed;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Add PosDbContext
builder.Services.AddDbContext<PosDbContext>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

// Auto initialize SQLite DB
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
    await DbInitializer.InitializeAsync(db);
}

// Active WebSocket connections for KDS / Comanderas
var activeSockets = new System.Collections.Concurrent.ConcurrentBag<WebSocket>();

async Task BroadcastAsync(object message)
{
    var json = JsonSerializer.Serialize(message);
    var bytes = Encoding.UTF8.GetBytes(json);
    var segment = new ArraySegment<byte>(bytes);

    foreach (var socket in activeSockets)
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // Disconnected or closed
            }
        }
    }
}

// 1. Health Endpoint
app.MapGet("/health", () => Results.Ok(new
{
    Status = "Healthy",
    Node = "Loqzen-POS-Master",
    Mode = "Standalone-Offline-First",
    Timestamp = DateTime.UtcNow
}));

// 2. Catalog Endpoint (for Mobile Waiter Tablets)
app.MapGet("/api/catalog/products", async (PosDbContext db) =>
{
    var products = await db.Products
        .Include(p => p.Category)
        .Where(p => p.IsActive)
        .Select(p => new
        {
            p.Id,
            p.Sku,
            p.Barcode,
            p.Name,
            p.Price,
            p.IsWeighed,
            CategoryName = p.Category != null ? p.Category.Name : "General"
        })
        .ToListAsync();

    return Results.Ok(products);
});

// 3. KDS Orders Endpoint (Fetch Active Kitchen Orders)
app.MapGet("/api/kds/orders", async (PosDbContext db) =>
{
    var orders = await db.KitchenOrders
        .Include(k => k.Items)
        .Where(k => k.Status != KitchenOrderStatus.Delivered && k.Status != KitchenOrderStatus.Cancelled)
        .OrderBy(k => k.CreatedAt)
        .Select(k => new
        {
            k.Id,
            k.OrderNumber,
            k.TableNumber,
            k.WaiterName,
            k.Station,
            k.GeneralNotes,
            Status = k.Status.ToString(),
            k.CreatedAt,
            Items = k.Items.Select(i => new
            {
                i.Id,
                i.ProductName,
                i.Quantity,
                i.Notes,
                i.IsPrepared
            })
        })
        .ToListAsync();

    return Results.Ok(orders);
});

// 4. Create Comandera Order (Mesero -> POS Master)
app.MapPost("/api/orders/comandera", async (CreateComanderaOrderDto dto, PosDbContext db) =>
{
    var kitchenOrder = new KitchenOrder
    {
        OrderNumber = $"COM-{DateTime.UtcNow:yyyyMMdd}-{DateTime.UtcNow.Ticks % 1000:000}",
        TableNumber = string.IsNullOrWhiteSpace(dto.TableNumber) ? "Mesa 1" : dto.TableNumber,
        WaiterName = string.IsNullOrWhiteSpace(dto.WaiterName) ? "Mesero Móvil" : dto.WaiterName,
        Station = string.IsNullOrWhiteSpace(dto.Station) ? "Cocina Caliente" : dto.Station,
        GeneralNotes = dto.GeneralNotes,
        Status = KitchenOrderStatus.Pending,
        CreatedAt = DateTime.UtcNow
    };

    foreach (var item in dto.Items)
    {
        kitchenOrder.Items.Add(new KitchenOrderItem
        {
            ProductId = item.ProductId ?? Guid.NewGuid(),
            ProductName = item.ProductName,
            Quantity = item.Quantity,
            Notes = item.Notes
        });
    }

    db.KitchenOrders.Add(kitchenOrder);

    // Register Outbox Message for offline resilient sync
    db.OutboxMessages.Add(new OutboxMessage
    {
        EventType = "ComanderaOrderCreated",
        AggregateType = "KitchenOrder",
        AggregateId = kitchenOrder.Id,
        PayloadJson = JsonSerializer.Serialize(dto),
        Status = SyncStatus.Pending
    });

    await db.SaveChangesAsync();

    // Broadcast to connected KDS screens
    await BroadcastAsync(new
    {
        Event = "KitchenOrderCreated",
        OrderId = kitchenOrder.Id,
        kitchenOrder.OrderNumber,
        kitchenOrder.TableNumber,
        kitchenOrder.Station
    });

    return Results.Created($"/api/kds/orders/{kitchenOrder.Id}", new
    {
        Success = true,
        OrderId = kitchenOrder.Id,
        kitchenOrder.OrderNumber,
        Message = "Comanda recibida y transmitida a cocina con éxito (< 50ms)"
    });
});

// 5. Update Kitchen Order Status (KDS -> POS Master -> Waiter)
app.MapPut("/api/kds/orders/{id:guid}/status", async (Guid id, UpdateStatusDto dto, PosDbContext db) =>
{
    var order = await db.KitchenOrders.FindAsync(id);
    if (order == null) return Results.NotFound(new { Error = "Comanda no encontrada" });

    if (Enum.TryParse<KitchenOrderStatus>(dto.Status, true, out var newStatus))
    {
        order.Status = newStatus;
        await db.SaveChangesAsync();

        await BroadcastAsync(new
        {
            Event = "KitchenOrderStatusChanged",
            OrderId = order.Id,
            order.OrderNumber,
            Status = newStatus.ToString()
        });

        return Results.Ok(new { Success = true, OrderId = order.Id, Status = newStatus.ToString() });
    }

    return Results.BadRequest(new { Error = "Estado de comanda inválido" });
});

// 6. WebSocket Hub for KDS & Mobile Comanderas Push Notifications
app.Map("/ws/kds", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        activeSockets.Add(webSocket);

        var buffer = new byte[1024 * 4];
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
            }
        }
    }
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

app.Run();

// DTO records
public record CreateComanderaOrderDto(
    string TableNumber, 
    string WaiterName, 
    string Station, 
    string? GeneralNotes, 
    List<ComanderaItemDto> Items);

public record ComanderaItemDto(Guid? ProductId, string ProductName, decimal Quantity, string? Notes);

public record UpdateStatusDto(string Status);
