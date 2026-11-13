# Active Context & Progress (snapshot 2026-11-12)

## Git state
- Commits: `58df566` docs → `9a26337` scaffold (Clean Arch, domain, SQLite, touch UI, docker) → `9634800` dual display, KDS, scanner, Kestrel.
- **Uncommitted WIP**: CashShift + Masters modules (`Core.Application/Services/`, `Infrastructure.Data/Services/`, `CashShiftViewModel`, `MastersViewModel`, their Views) and edits to `MainViewModel` / `MainWindow.axaml`.

## Build status
- ✅ `dotnet build` passes (0 errors, 0 warnings) after fixing `MastersView.axaml` line 234 (replaced invalid `BoolConverters.True` with bound `ProductFormTitle` property).
- Toolchain: dotnet SDK 8.0.428 at `~/.dotnet/dotnet`; docker available.

## Done ✅
- Solution skeleton, 6 projects, references respect Clean Architecture at csproj level.
- Entities with Guid PKs, PosDbContext with precisions/indexes, seed data.
- Real + Mock printer/scale/scanner drivers. Docker emulators (printer, scale, webhook).
- Checkout, KDS, CustomerDisplay, CashShift, Masters views (last two WIP).
- Kestrel server with KDS REST + WebSocket broadcast and Outbox write on comanda.
- **DI container** via `Microsoft.Extensions.DependencyInjection` wired in `App.axaml.cs`.
- **Hardware switching via config**: `appsettings.json` / `appsettings.Development.json` with `Hardware.UseMock*` flags.
- ViewModels receive dependencies via constructor injection — no more direct `new PosDbContext()`.
- `GetByIdAsync` added to `ICategoryService`, `ITaxService`, `ICustomerService` and implementations.

## Architecture debt / gaps ⚠️ (priority order)
1. ~~Build broken~~ ✅ Fixed.
2. ~~No DI container~~ ✅ Implemented.
3. Presentation.Desktop references Infrastructure for composition (OK), but `CheckoutViewModel` and `KdsViewModel` still use `PosDbContext` directly for queries (should use Application-layer services / repositories).
4. Sales use-case logic (totals, tax, payment, order persistence + Outbox) lives in `CheckoutViewModel` → move into a `Core.Application` service (e.g. `ISalesService`).
5. No EF Core migrations (uses EnsureCreated).
6. Kestrel not embedded in desktop host; no mDNS; WebSocket bag never prunes closed sockets; no station filtering.
7. No KDS-offline > 30 s fallback to kitchen printer; no ZPL generation verified.
8. No `ICashDrawerService` implementation; no Outbox dispatcher/background worker.
9. No test projects at all.
10. Android target not set up; docs claim .NET 9 option.

## Next recommended steps
1. Extract `ISalesService` / `IOrderRepository` for checkout logic; add EF Core migrations.
2. Add `tests/` (xUnit) for domain totals, CashShift service, printer byte output against emulator.
3. Embed Kestrel into desktop; WebSocket hardening; KDS fallback printing.
4. Commit changes.
