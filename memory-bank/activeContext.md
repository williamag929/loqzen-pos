# Active Context & Progress (snapshot 2025-01-15)

## Git state
- Commits: `58df566` docs → `9a26337` scaffold (Clean Arch, domain, SQLite, touch UI, docker) → `9634800` dual display, KDS, scanner, Kestrel.
- **Uncommitted WIP**: CheckoutViewModel refactored to use ISalesService, MVP E2E tests added.

## Build status
- ✅ `dotnet build` passes (0 errors, 0 warnings).
- ✅ `dotnet test` passes (42 tests, including 4 new MVP E2E integration tests).
- Toolchain: dotnet SDK 8.0.428 at `~/.dotnet/dotnet`; docker available.

## Done ✅
- Solution skeleton, 6 projects, references respect Clean Architecture at csproj level.
- Entities with Guid PKs, PosDbContext with precisions/indexes, seed data.
- Real + Mock printer/scale/scanner drivers. **Docker emulators verified and enhanced** (printer, scale, webhook).
- **CheckoutView completed for full sales cycle** (see details below).
- Checkout, KDS, CustomerDisplay, CashShift, Masters views (last two WIP).

### CheckoutView Completion (2026-11-12)
- **Left Panel - Product Catalog**:
  - Search box with barcode/SKU search (50px height)
  - Category filter buttons (horizontal scroll, 48px height)
  - Product grid with 170x130px touch cards (>100x100 requirement met)
  - CAS Scale weight indicator badge
  - Status bar

- **Right Panel - Cart/Ticket**:
  - Cart items with product name, quantity, line total
  - +/- buttons (48x48px) for quantity adjustment
  - Delete button (48x48px)
  - Subtotal, Tax (19% IVA), Total display
  - "💳 COBRAR" button (60px height, prominent green)
  - "Cancelar" button to clear cart

- **Payment Modal**:
  - Payment method selector: Efectivo/Tarjeta/Transferencia (52px buttons)
  - Total, Tendered, Change display (cash only shows tendered/change)
  - Touch numpad (48px min-height buttons, only visible for cash)
  - Confirm and Cancel buttons (52px height)
  - `BoolToColorConverter` created for payment method button highlighting

- **ViewModel Commands** (all implemented):
  - `AddProductCommand` - adds product to cart
  - `RemoveItemCommand` - removes cart item
  - `IncrementItemCommand` - increases quantity/weight
  - `DecrementItemCommand` - decreases quantity/weight
  - `StartPaymentCommand` - opens payment modal
  - `SelectPaymentMethodCommand` - switches Cash/Card/Transfer
  - `CompleteSaleAsyncCommand` - processes payment
  - `ClosePaymentModalCommand` - cancels payment modal
  - `ClearCartCommand` - clears cart

- **Touch-first design verified**:
  - All interactive elements ≥48x48px
  - Dark theme: Background #0F172A, Accent #2563EB
  - Spanish UI text, English code

### Docker Emulators Status (verified 2026-11-12)
| Emulator | Port | Status | Notes |
|----------|------|--------|-------|
| thermal-printer-emulator | TCP 9100 | ✅ Working | ESC/POS parsing, cash drawer pulse (0x1B 0x70) detection with params |
| serial-scale-emulator | TCP 9101 | ✅ Working | Changed from PTY to TCP for host accessibility; CAS SW-20RS frames |
| delivery-webhook-mock | HTTP 5000 | ✅ Working | POST/GET webhook mock for delivery integrations |
- Kestrel server with KDS REST + WebSocket broadcast and Outbox write on comanda.
- **DI container** via `Microsoft.Extensions.DependencyInjection` wired in `App.axaml.cs`.
- **Hardware switching via config**: `appsettings.json` / `appsettings.Development.json` with `Hardware.UseMock*` flags.
- ViewModels receive dependencies via constructor injection — no more direct `new PosDbContext()`.
- `GetByIdAsync` added to `ICategoryService`, `ITaxService`, `ICustomerService` and implementations.

## Architecture debt / gaps ⚠️ (priority order)
1. ~~Build broken~~ ✅ Fixed.
2. ~~No DI container~~ ✅ Implemented.
3. ~~Sales use-case logic in CheckoutViewModel~~ ✅ Extracted to `ISalesService`.
4. ~~CheckoutViewModel uses PosDbContext directly~~ ✅ Refactored to use ISalesService for order/payment flow (only product queries via DbContext for performance).
5. `KdsViewModel` still uses `PosDbContext` directly for queries (should use Application-layer services).
6. No EF Core migrations (uses EnsureCreated).
7. Kestrel not embedded in desktop host; no mDNS; WebSocket bag never prunes closed sockets; no station filtering.
8. No KDS-offline > 30 s fallback to kitchen printer; no ZPL generation verified.
9. No `ICashDrawerService` implementation; no Outbox dispatcher/background worker.
10. ~~No test projects at all~~ ✅ Test project added with 42 passing tests (including MVP E2E).
11. Android target not set up; docs claim .NET 9 option.

## ISalesService Implementation (completed 2026-11-12)
- **DTOs**: `OrderDto`, `OrderItemDto`, `PaymentResultDto` in `Core.Application/DTOs/SalesDto.cs`
- **Interface**: `ISalesService` in `Core.Application/Services/ISalesService.cs`
- **Implementation**: `SalesService` in `Infrastructure.Data/Services/SalesService.cs`
- **Registration**: Added to `ServiceCollectionExtensions.cs`
- **Features**:
  - `CreateOrderAsync` - creates draft order with generated order number
  - `AddItemAsync` - adds product with tax calculation from Product.Tax.Rate
  - `RemoveItemAsync` - removes item and recalculates totals
  - `UpdateItemQuantityAsync` - updates quantity with total recalculation
  - `ApplyDiscountAsync` - applies discount to order
  - `ProcessPaymentAsync` - processes payment, updates status, creates Payment record, writes OutboxMessage in same transaction
  - `GetCurrentOrderAsync` - gets current draft order for cash shift
  - `GetByIdAsync` - gets order by ID

## Test Project Implementation (completed 2026-11-12)
- **Project**: `tests/Loqzen.Tests/Loqzen.Tests.csproj` (xUnit, net8.0)
- **Packages**: xUnit, FluentAssertions, Moq, Microsoft.EntityFrameworkCore.InMemory 8.0.11
- **Test Files**:
  - `Fixtures/DatabaseFixture.cs` - In-memory EF Core test database factory
  - `Domain/OrderItemTests.cs` - OrderItem subtotal, tax, weighted product calculations (5 tests)
  - `Domain/OrderTests.cs` - Order total calculations with discounts and multiple tax rates (4 tests)
  - `Domain/PaymentTests.cs` - Payment change calculation (3 tests)
  - `Domain/CashShiftTests.cs` - CashShift difference calculation (5 tests)
  - `Services/CashShiftServiceTests.cs` - Open shift, increment number, duplicate prevention (5 tests)
  - `Services/CashShiftCloseTests.cs` - Close shift, expected amount calculation, notes (5 tests)
  - `Services/ProductServiceTests.cs` - Create, GetAll, GetByBarcode/Sku (5 tests)
  - `Services/ProductServiceCrudTests.cs` - Update, Delete, AdjustStock (6 tests)
- **Total**: 38 tests, all passing
- **Run**: `dotnet test tests/Loqzen.Tests/Loqzen.Tests.csproj`

## MVP Readiness Status ✅

| Feature | Status | Notes |
|---------|--------|-------|
| Cash shift open/close | ✅ Working | Via ICashShiftService |
| Product add to cart | ✅ Working | With actual tax rates from Product.Tax |
| Payment processing | ✅ Working | Via ISalesService.ProcessPaymentAsync |
| Receipt printing | ✅ Configured | IPrinterService.PrintReceiptAsync called after payment |
| Order persisted to SQLite | ✅ Working | Order, Payment, OutboxMessage in same transaction |
| Cash drawer pulse | ✅ Working | ESC/POS 0x1B 0x70 command on cash payments |
| Integration tests | ✅ Passing | 42 tests including 4 MVP E2E tests |

### CheckoutViewModel ISalesService Integration (completed 2025-01-15)
- ✅ Injects `ISalesService` and `ICashShiftService` via constructor
- ✅ Checks cash shift status before allowing payment
- ✅ Creates orders via `ISalesService.CreateOrderAsync`
- ✅ Adds items via `ISalesService.AddItemAsync`
- ✅ Processes payments via `ISalesService.ProcessPaymentAsync`
- ✅ OutboxMessage created in same transaction (Offline-First pattern)
- ✅ KitchenOrder created for KDS
- ✅ Receipt printed after successful payment
- ✅ Cash drawer opened for cash payments

## Next recommended steps
1. ~~Refactor CheckoutViewModel to use ISalesService~~ ✅ Done
2. Add EF Core migrations.
3. Add integration tests for printer output against Docker emulator.
4. Embed Kestrel into desktop; WebSocket hardening; KDS fallback printing.
5. Commit changes.
