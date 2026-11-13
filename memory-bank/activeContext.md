# Active Context & Progress (snapshot 2025-01-15)

## Git state
- Latest commit: `10ecb6f` feat: add in-sale return/credit note system (Fase 1)
- Previous commits: `730490e` Report Service → `3bfc464` Customer Display discounts → `545cfe7` 10% discount → `b25793f` ESC/POS receipt
- **Fully committed**: In-cart returns, receipt printing, discount system, report service, customer display.

## Build status
- ✅ `dotnet build` passes (0 errors, 0 warnings).
- ✅ `dotnet test` passes (42 tests, including 4 MVP E2E integration tests).
- ✅ 201 tests pass (`dotnet test`) — 2026-11-13.
- Toolchain: dotnet SDK 8.0.428 at `~/.dotnet/dotnet`; docker available.

## Done ✅
- Solution skeleton, 6 projects, references respect Clean Architecture at csproj level.
- Entities with Guid PKs, PosDbContext with precisions/indexes, seed data.
- Real + Mock printer/scale/scanner drivers. **Docker emulators verified and enhanced** (printer, scale, webhook).
- **CheckoutView completed for full sales cycle** (see details below).
- **In-cart return/credit note system (Fase 1)** with receipt printing and validation.
- **10% quick discount system** with UI, DB persistence, and customer display sync.
- **Report Service** (daily/Z-report/period analytics) with IReportService interface.
- **Enhanced receipt printing** with ESC/POS formatting, store config, fallback to TXT.
- **Customer Display dual-screen support** with discount tracking and return item warnings.
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
6. ~~No EF Core migrations~~ ✅ InitialCreate migration (2026-11-12).
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

### In-Cart Return System (Fase 1 completed 2026-11-12)
**Business Logic:**
- Clicking "🔄" button on cart item now ADDS a return item (negative qty) instead of deleting
- Validates: cannot return more than added in same sale
- Return items have `IsReturn=true` flag in DTOs
- Cart displays: normal items + return items side-by-side

**Receipt Printing:**
- Separate sections: normal items, then "*** NOTA CREDITO ***"
- Return items prefixed with "DEVOL:" and "(Ajuste en venta)"
- Clear for customer to see credit note on same receipt

**UI Changes:**
- CheckoutView: Return items with red background (#450A0A), border #991B1B
- Label: "🔄 DEVOLUCIÓN: [name]" + "(Nota crédito - ajuste en venta)"
- CustomerDisplay: Same styling, shows "⚠️ DEVOLUCIÓN" to customer
- Both normal and return items visible simultaneously in cart

**Implementation:**
- `CartItemDto.IsReturn` flag + `DisplayName` property
- `ReceiptItemDto.IsReturn` flag
- `CheckoutViewModel.RemoveItem` → ReturnItem logic with validation
- `PrinterService` separates `Items.Where(!IsReturn)` vs `Items.Where(IsReturn)`
- CheckoutView.axaml and CustomerDisplayWindow.axaml with Panel containers for dual templates

**TODO (Fase 2):**
- Manager authorization PIN modal
- ManagerOverride entity for audit trail
- Configurable actions requiring authorization (appsettings.json)


### Restaurant mode + cuadre de caja (2026-11-12, verified)
**Verified by:** 72 xUnit tests (incl. real-SQLite e2e + migrations on files) and a manual run on Linux/Wayland (screenshot: 3 panels, yellow countdown -> green, live receipt).
- Entities: DiningTable, Comanda, ComandaItem (Pending->Confirmed/Cancelled), CashCountLine. Guid PKs.
- IComandaService/ComandaService (partial: .cs/.Items/.Close/.Helpers). TimeProvider injected. Confirm creates ONE KitchenOrder PER STATION + Outbox. CloseAndPay = Order+Payment+Outbox in one SaveChanges; validates cash BEFORE mutating.
- Config `Comanda:ConfirmationDelaySeconds` (30). Timer is app-wide in ComandaViewModel (singleton), not per-screen.
- ICashShiftService: GetCloseSummaryAsync (by method), CloseShiftWithCountAsync (COP denominations; blocks if comandas open).
- ReceiptFormatter + ReceiptDtoFactory (Core.Application/Receipts) = single layout for printer, TXT fallback and on-screen preview. EscPosRenderer converts lines to bytes.
- EF Core migrations: InitialCreate; dotnet-ef 8.0.11 local tool. DbInitializer.MigrateAsync; legacy EnsureCreated DBs renamed to *.legacy-<ts>.bak (never deleted).
- UI: ComandaView (List/Order/Pay panels + ComandaCatalogPanel), ReceiptPreviewControl, Numpad, swipe in code-behind. Checkout has live tirilla column.

**Bugs fixed this session:** 13 dead buttons (XAML *AsyncCommand vs generated *Command) + XamlCommandBindingTests guard; weighed in-cart return was charged instead of credited; returns were sent to KDS; cash close pre-filled counted = expected.

**Known limitations / next:**
- KDS (2026-11-12): IKitchenService/KitchenService (TimeProvider), KdsTiming rules, `Kds` config (WarningMinutes 5, MaxPrepMinutes 15, ArchiveDelaySeconds 20, PollSeconds 3). KdsViewModel is a singleton that polls the DB and MERGES cards (animations survive). BorderRunnerLight control = light running around NEW cards; late cards blink red; print button green until PrintedAt; Entregar -> DeliveredAt + 20s dimmed countdown with Undo. Migration KitchenOrderPrintedDelivered. `LOQZEN_START_VIEW=kds` env var opens the KDS tab at startup (kiosk).
- Presentation.Server WebSocket is still a separate process; the desktop does not broadcast (KDS on the same machine uses DB polling).
- Comanda notes DONE (2026-11-12): SetItemNotesAsync/SetItemHoldAsync; IsOnHold skips auto-confirm while editing; quick-note chips from `Comanda:QuickNotes`; max 200 chars; migration ComandaItemNotesHold. A hold left by a crash shows "en pausa" and is released by opening Nota -> Guardar/Cancelar.
- Comanda: each tap adds a new line (no merge); weighed products default 1 kg (no scale in comanda).
- Waiter/cashier names come from the signed-in user (free text fallback when `RequireLogin=false`).
- CustomerDisplay is not wired to comandas.
- Quick-sale Checkout still uses PosDbContext directly (pre-existing debt).

### Users with PIN + manager authorization (2026-11-13, verified: 201 tests, login/Maestros screenshots)
- Domain: `User` (Name, PinHash, Role, IsActive, LastLoginAt), `ManagerOverride` (Action, Description, ManagerUserId/Name, RequestedBy*, Amount, Reference, AuthorizedAt), `KitchenOrderItem.IsCancelled`. Enums `UserRole` (Kitchen<Waiter<Cashier<Manager<Admin), `AuthorizedAction` (ReturnItem, ApplyDiscount, CancelKitchenItem, OpenCashDrawer, CloseShift). Migration `20261113051007_UsersAndManagerOverrides`.
- Application: `Security/PinHasher` (PBKDF2-SHA256, 4–6 digits), `Security/UserPermissions` (CanAccess(role, PosView), CanAuthorize, CanManageUsers, CanAssignRole, labels), `AuthorizationSettings` (RequireLogin, ManagerRequiredFor; null = defaults, [] = none), `IUserService`, `IManagerAuthorizationService`, `IUserSession`, `IComandaService.CancelConfirmedItemAsync`.
- Data: `UserService(db, clock)`, `ManagerAuthorizationService(db, settings, clock)` (same error message for wrong PIN / no permission), `ComandaService.CancelConfirmedItemAsync` (validates override Action==CancelKitchenItem; strikes the kitchen line matching product+notes+qty; cancels the KitchenOrder only if still Pending/InPreparation and all lines cancelled; Outbox `KitchenItemCancelled`). Seed users Administrador 1234 / Cajero 1111 / Mesero 2222 when `Users` is empty (also on existing DBs).
- Desktop: `Services/UserSession` (singleton, IUserSession, CanAccess* for tabs), `Services/IManagerAuthPrompt` + `ManagerAuthPromptViewModel` (singleton modal; resolves NotRequired / auto-approves a signed-in manager / opens PIN dialog with TCS), `LoginViewModel`+`LoginView`, `ManagerAuthDialog`, `Numpad.ExactLabel`, `MainViewModel` (IsLoginVisible, LogoutCommand, jumps to first allowed tab on sign-in), `MastersViewModel.Users.cs` (🔑 Usuarios tab, soft deactivate), `KitchenItemViewModel` (KDS lines merge in place; ANULADO strike-through). Hooks: Checkout RemoveItemAsync/OpenDiscountModalAsync/OpenCashDrawerAsync, Comanda CancelItem (confirmed → manager), CashShift pre-fills cashier.
- Checkout layout (2026-11-13): root grid is 3*/Auto/2* with MinWidths; the tirilla column auto-hides below 1150 px (`CheckoutView.SizeChanged` → `ApplyViewportWidth`), so the view never overflows a half-screen window. Payment modal: 540 px, amounts in an `Auto,*` grid (label Auto, figure * right-aligned — an `*,Auto` grid clipped the figure), shared 64 px `Numpad` with `ExactAmountCommand`.
- Change screen (2026-11-13): `SaleSummaryViewModel` + `Views/SaleCompletedOverlay` ("CAMBIO A ENTREGAR", 84 px figure, Nueva venta). Checkout sets `LastSale` after `CompleteSaleAsync` (cleared by Nueva venta or AddProduct); Comanda sets it after `CloseAndPayAsync`. Customer display already showed "Su Cambio" in its Completed state.
- Known gaps: overrides are not single-use; service does not enforce CanAssignRole; no lockout after failed PINs; PIN modal not exercised by click (no Wayland input tool), only compile + XAML binding tests.

## Next recommended steps
1. ~~Refactor CheckoutViewModel to use ISalesService~~ ✅ Done
2. ~~Implement in-cart return system (Fase 1)~~ ✅ Done
3. ~~Add EF Core migrations~~ ✅ Done
4. ~~Implement manager authorization system (Fase 2 of returns)~~ ✅ Done 2026-11-13 (users + PIN + roles + audit).
5. Embed Kestrel in the desktop + WebSocket KDS/comanderas on other devices.
6. Customer display for comandas.
5. Add integration tests for printer output against Docker emulator.
6. Embed Kestrel into desktop; WebSocket hardening; KDS fallback printing.
