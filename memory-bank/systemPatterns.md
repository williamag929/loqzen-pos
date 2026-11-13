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
- Schema via EF migrations (`DbInitializer.InitializeAsync` → `MigrateAsync` + seed; legacy EnsureCreated DBs are renamed `*.legacy-*.bak`).

## Hardware layer
| Interface | Real impl | Mock impl |
|---|---|---|
| IPrinterService | `EscPosNetworkPrinterService` (TcpClient → 9100) | `MockPrinterService` |
| IScaleService | `CasSerialScaleService` (SerialPort) | `MockScaleService` |
| IBarcodeScannerService | `UsbHidBarcodeScannerService` | `MockBarcodeScannerService` |
| ICashDrawerService | (none yet — drawer is via `IPrinterService.OpenCashDrawerAsync`) | — |

## Embedded server (Presentation.Server — class library, 2026-11-13)
- `LoqzenServer(IServiceProvider appServices, ServerSettings)` started by `App.axaml.cs` (`Server:Enabled`, port 5055). One `IServiceScope` of the desktop container per request; config sources cleared (does not read the desktop appsettings "Kestrel").
- Routes: `GET /health`, `/kds`, `/comandera` (embedded wwwroot HTML), `/api/kds/settings|orders[?station]|orders/{id}`, `POST /api/kds/orders/{id}/advance|printed|undo`, `/api/catalog/products|categories`, `/api/tables`, `/api/comandas` (+ `{id}`, `/items`, `/items/{itemId}/cancel|notes`, `/send`, `/ready`), `WS /ws/kds?station=`.
- Realtime: services publish `KitchenEvent` on `IKitchenEventBus` after SaveChanges → `KdsSocketHub` fans out `{type:"kitchen", event, kitchenOrderId, station, ...}` (per-socket semaphore, removal on close) and the desktop `KdsViewModel`/`ComandaViewModel` reload (debounced). Errors: ArgumentException→400, not found→404, InvalidOperationException→409, else 500 `{error}`.
- Outbox: `OutboxDispatcher` (Infrastructure.Data/Outbox) + `HttpOutboxSink`, run by `OutboxSyncWorker` (desktop timer) when `Sync:Enabled`.

## Desktop UI (MVVM)
- `MainViewModel` hosts tabs by `ActiveViewIndex`: 0 Checkout, 1 KDS, 2 CashShift, 3 Masters, 4 Comandas; tabs filtered by `UserSession` role; PIN login overlay + shared `ManagerAuthDialog`; opens `CustomerDisplayWindow`.
- ViewModels: Checkout, Kds, CashShift, Masters, CustomerDisplay (all `ObservableObject` + `[ObservableProperty]`/`[RelayCommand]`).
- UI text is in **Spanish**; code identifiers in English. Dark slate palette (`#0F172A`, accent `#2563EB`).

## Docker emulators
- printer: TCP 9100, logs `[CASHOUT]` on `0x1B 0x70`, stores receipt traces.
- scale: socat virtual PTY streaming CAS frames every `WEIGHT_STEP_SECONDS`.
- webhook: delivery mock on :5000 (Python, despite docs saying Node).
- Run: `docker compose -f docker/docker-compose.yml up -d`.
