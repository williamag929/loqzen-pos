# System Patterns & Architecture

## Solution layout (`LoqzenPos.sln`, all `net8.0`, Nullable enabled)
```
src/Core.Domain            -> no deps. BaseEntity(Guid Id, CreatedAt, UpdatedAt), Entities, Enums
src/Core.Application       -> Domain. DTOs (ReceiptDto, CartItemDto), Hardware/HardwareInterfaces.cs, Services/I*Service
src/Infrastructure.Data    -> Domain, Application. EF Core Sqlite 8.0.11. PosDbContext, Seed/DbInitializer, Services/
src/Infrastructure.Hardware-> Domain, Application. System.IO.Ports 8.0.0. Printers/, Scales/, Scanners/
src/Presentation.Desktop   -> all of the above. Avalonia 11.2.2 + Fluent, CommunityToolkit.Mvvm 8.3.2
src/Presentation.Server    -> Domain, Application, Data. ASP.NET Core minimal API (Kestrel) + WebSockets
docker/                    -> docker-compose.yml + emulator/{printer,scale,webhook} (all Python)
```

## Domain model (Core.Domain/Entities)
- Catalog.cs: `Category`, `Product` (Sku unique, Barcode idx, Price, Cost, StockQuantity, IsWeighed, IsActive)
- CustomerAndTax.cs: `Customer` (DocumentId, FullName), `Tax` (Rate precision 6,4)
- Sales.cs: `Order` (OrderNumber unique, Status, Subtotal/TaxTotal/DiscountTotal/Total, CustomerId?, CashShiftId?), `OrderItem` (snapshot ProductName/UnitPrice/TaxRate, WeightKg?), `Payment` (Method, Amount, Tendered, Change, Reference)
- ShiftAndOutbox.cs: `CashShift` (Opening/Closing/Expected, Difference computed), `OutboxMessage` (EventType, AggregateType/Id, PayloadJson, SyncStatus, RetryCount, LastError)
- KitchenOrder.cs: `KitchenOrder` (OrderNumber, TableNumber, WaiterName, Station, GeneralNotes, Status), `KitchenOrderItem` (IsPrepared)
- Enums: OrderStatus, PaymentMethod, CashShiftStatus, SyncStatus, KitchenOrderStatus (Pending, InPreparation, Ready, Delivered, Cancelled)
- Money = `decimal` HasPrecision(18,2); quantities (18,3).

## Persistence
- `PosDbContext` default path: `$LocalApplicationData/LoqzenPos/pos.db` (Linux: `~/.local/share/LoqzenPos/pos.db`).
- Schema is created via `DbInitializer.InitializeAsync` → `EnsureCreatedAsync()` + seed. **No EF migrations yet** (spec asks for migrations).

## Hardware layer
| Interface | Real impl | Mock impl |
|---|---|---|
| IPrinterService | `EscPosNetworkPrinterService` (TcpClient → 9100) | `MockPrinterService` |
| IScaleService | `CasSerialScaleService` (SerialPort) | `MockScaleService` |
| IBarcodeScannerService | `UsbHidBarcodeScannerService` | `MockBarcodeScannerService` |
| ICashDrawerService | (none yet — drawer is via `IPrinterService.OpenCashDrawerAsync`) | — |

## Embedded server (Presentation.Server/Program.cs)
- `GET /health`, `GET /api/catalog/products`, `GET /api/kds/orders`,
  `POST /api/orders/comandera` (creates KitchenOrder + OutboxMessage, broadcasts `KitchenOrderCreated`),
  `PUT /api/kds/orders/{id}/status` (broadcasts `KitchenOrderStatusChanged`), `WS /ws/kds`.
- Sockets kept in a `ConcurrentBag<WebSocket>` (never removed → leak). CORS allow-any.
- Runs as a **separate process** today; spec says it should be embedded in the desktop host.

## Desktop UI (MVVM)
- `MainViewModel` hosts tabs by `ActiveViewIndex`: 0 Checkout, 1 KDS, 2 CashShift, 3 Masters; opens `CustomerDisplayWindow`.
- ViewModels: Checkout, Kds, CashShift, Masters, CustomerDisplay (all `ObservableObject` + `[ObservableProperty]`/`[RelayCommand]`).
- UI text is in **Spanish**; code identifiers in English. Dark slate palette (`#0F172A`, accent `#2563EB`).

## Docker emulators
- printer: TCP 9100, logs `[CASHOUT]` on `0x1B 0x70`, stores receipt traces.
- scale: socat virtual PTY streaming CAS frames every `WEIGHT_STEP_SECONDS`.
- webhook: delivery mock on :5000 (Python, despite docs saying Node).
- Run: `docker compose -f docker/docker-compose.yml up -d`.
