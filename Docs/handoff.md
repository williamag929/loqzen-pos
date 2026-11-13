# Handoff / Progress Report - Loqzen POS
**Fecha:** 2026-11-13  
**Última sesión:** Servidor LAN embebido (KDS web + comandera web en tiempo real), Outbox → webhook, bloqueo de PIN, overrides de un solo uso, visor para comandas


> 👋 **¿Nuevo en el proyecto?** Empieza por [`CONTRIBUTING.md`](../CONTRIBUTING.md) (setup, convenciones, trampas conocidas y checklist).

---

---

## 🆕 SESIÓN 2026-11-13 (tarde): Servidor embebido, pantallas en red, Outbox, endurecimiento

Modelo de `memory-bank/orchestration.md`: contrato primero, luego cuatro agentes en paralelo — red (`Presentation.Server`), backend (`Infrastructure.Data`), UI (`Presentation.Desktop`) y dos de QA (`tests/`). El backend y la UI recibieron tareas encadenadas a medida que entregaban.

**Hecho (340 tests, build 0 errores/0 warnings; verificado en pantalla: KDS en vivo, barra responsiva, chip 🌐; verificado por HTTP/WS desde Node: `/health`, `/api/kds/orders`, `/advance`, push WebSocket y limpieza de conexiones):**
- `Presentation.Server` pasó de proceso aparte a **biblioteca** (`LoqzenServer`) que el escritorio arranca en `Server:Port` (5055). Config aislada, log solo errores, una `IServiceScope` por petición, errores → `{error}` con 400/404/409/500. Rutas: health, `/kds`, `/comandera`, KDS (settings/orders/advance/printed/undo), catálogo, mesas, comandas (abrir/items/notas/anular/enviar/lista para pagar), `WS /ws/kds?station=`.
- **KDS web** (`wwwroot/kds.html`) y **comandera web** (`wwwroot/comandera.html`): archivo único, sin CDN, táctil, reconexión con backoff, reloj corregido contra `/health`. Mismas reglas que el escritorio porque usan los mismos servicios.
- `IKitchenEventBus` (Core.Application/Kitchen): `KitchenService` y `ComandaService` publican `Created/StatusChanged/Printed/ItemCancelled/Undone` tras guardar. El KDS y las comandas del escritorio se refrescan al instante (debounce 150 ms; la encuesta queda de respaldo). `CheckoutViewModel` crea el ticket de cocina vía `IKitchenService.CreateAsync` y ya no usa `PosDbContext`.
- **Outbox → webhook**: `OutboxDispatcher` + `HttpOutboxSink` (`Sync:*`, apagado por defecto), `OutboxSyncWorker` en el escritorio, indicador y "Reintentar fallidos" en Turno de Caja.
- Endurecimiento: `ManagerOverride.ConsumedAt/ConsumedFor` (un solo uso, migración `ManagerOverrideConsumed`); `PinAttemptLimiter` (5 fallos/min → 30 s) en login y PIN de gerente, con cuenta regresiva en pantalla.
- UI: barra superior responsiva (iconos y etiquetas cortas bajo 1200 px, `MinWidth` 900), chip 🌐 con la URL del KDS web (toca para copiar), visor del cliente también para comandas, encabezado del KDS en `WrapPanel`, selector de categoría e impuesto en el formulario de producto (antes no existía).

**⚠️ Para el usuario:** tras reiniciar, el chip 🌐 muestra la URL para tablets (`http://<ip>:5055/kds` y `/comandera`). Si el puerto está ocupado la app abre igual y el chip lo avisa. Para KDS de cocina sin login: `Authorization:RequireLogin=false` en un `appsettings.{env}.json`.

**Limitaciones conocidas:** KDS web y comandera web no se abrieron en un navegador real desde este repo (solo API/WS por tests y Node; la captura headless salió antes de cargar datos) → probar en una tablet. `HttpOutboxSink` usa `CancelAfter` real (no `TimeProvider`). Sin mDNS: hay que escribir la IP. `UserService` no valida `CanAssignRole`.

**Pedido del usuario (2026-11-13, caja):** (1) módulo de **pago de recibos / recaudos** en la caja (servicios, recargas, convenios) que entra al cuadre como movimiento propio, no como venta; (2) el **cuadre de caja** debe ser el mejor de la industria en facilidad y prevención de errores (referencia: Toast, Square). Diseño propuesto para (2): entidad `CashMovement` (In/Out, motivo, monto, usuario, override de gerente, referencia al recibo) que suma/resta del esperado; **conteo a ciegas** por defecto (`CashShift:BlindCount`), diferencia visible solo al terminar; umbral `CashShift:MaxDifferenceWithoutManager` → PIN + nota; esperado por medio de pago comparado con el cierre del datáfono; arqueo parcial y "dejar fondo"; Z impreso con `ReceiptFormatter`; historial por cajero. Lo que ya existe y se reutiliza: conteo por denominaciones, bloqueo si hay comandas abiertas, resumen por medio de pago.

**Siguiente fase recomendada:** **cuadre de caja de referencia + pago de recibos**, **segunda caja** (maestro/satélite) o **datáfono / pago en mesa** — ambas anotadas en el roadmap del README y en CONTRIBUTING §10.

---

## 🆕 SESIÓN 2026-11-13: Usuarios con PIN + autorización de gerente

Ejecutada con el modelo de `memory-bank/orchestration.md`: el arquitecto escribió el contrato (entidades, enums, DTOs, interfaces) y tres agentes trabajaron en paralelo por carpeta — backend (`Infrastructure.Data`), UI (`Presentation.Desktop`) y QA (`tests/`).

**Hecho (201 tests, build 0 errores, login y Maestros vistos en pantalla):**
- Entidades `User` (PIN con hash PBKDF2, rol, activo) y `ManagerOverride` (auditoría). `KitchenOrderItem.IsCancelled`. Migración `UsersAndManagerOverrides`.
- Roles `Kitchen < Waiter < Cashier < Manager < Admin` → `UserPermissions` (qué pestaña ve cada rol, quién autoriza, quién puede crear Admin).
- `IUserService` (crear/editar/cambiar PIN/login por PIN; PIN único entre activos; no deja desactivar al último que autoriza). `IManagerAuthorizationService` (PIN de gerente → `ManagerOverride`; `RequiresAuthorization` desde `Authorization:ManagerRequiredFor`).
- `IComandaService.CancelConfirmedItemAsync(comanda, item, overrideId)`: anula un producto ya en cocina, tacha la línea en el KDS (prefiere la línea con misma nota/cantidad), cancela la orden si ya no queda nada por cocinar (no toca órdenes Listas/Entregadas), Outbox `KitchenItemCancelled`. El override debe ser de acción `CancelKitchenItem`.
- Desktop: pantalla de PIN al inicio (`Authorization:RequireLogin`), chip 👤 + "Cambiar usuario", pestañas/botones por rol, modal `ManagerAuthDialog` compartido vía `IManagerAuthPrompt`, hooks en devolución, descuento 10 %, cajón sin venta, anular en cocina; cajero/mesero se toman del usuario conectado; pestaña **🔑 Usuarios** en Maestros; KDS muestra **ANULADO** tachado.
- Seed: Administrador 1234 · Cajero 1111 · Mesero 2222 (también en bases existentes).

**⚠️ Para el usuario:** al abrir la app pedirá PIN (1234). Cambia los PIN por defecto en Maestros → Usuarios. Para un KDS de cocina sin login usa `appsettings.{env}.json` con `"Authorization": { "RequireLogin": false }`.

**Limitaciones conocidas:** un `ManagerOverride` no se marca como consumido (se puede reutilizar); `UserService` no valida `CanAssignRole` (solo la UI); el modal de PIN no se probó con clic real (sin herramienta de input en Wayland), solo compilación + tests de bindings; no hay bloqueo por intentos fallidos.

**Siguiente fase recomendada:** Kestrel embebido + KDS/comanderas en red (ver CONTRIBUTING §10).

**Pedido del usuario (2026-11-13):** datáfono que reporte el pago automáticamente (tap, chip, Apple Pay / Google Pay). No estaba en el plan; solo el QR de pago en el visor. Propuesta: `IPaymentTerminalService` en `Core.Application/Hardware` (misma estrategia que la impresora: driver real + mock + emulador Docker), flujo `Tarjeta` → el POS envía monto → espera aprobación → `ProcessPaymentAsync(Card, reference=código de autorización)`; requiere elegir proveedor con API de semi-integración (Redeban/Credibanco, Bold, Mercado Pago Point, PayU).

**Pedido del usuario (2026-11-13, pago en mesa):** una caja + varios datáfonos inalámbricos asignados a meseras, para que el cliente pague en la mesa sin ir a la caja ni soltar la tarjeta. Diseño propuesto: `IPaymentTerminalService` multi-terminal (registro de datáfonos con nombre/IP, asignación a mesero en la comandera web), botón "💳 Cobrar en mesa" en `/comandera` → `POST /api/comandas/{id}/pay-on-terminal { terminalId }` → el servidor envía el monto, espera la aprobación y llama `CloseAndPayAsync(Card, reference = código de autorización)` → evento al KDS/caja y factura impresa en la caja (o en impresora del datáfono). Depende de escoger proveedor; los datáfonos inalámbricos de Bold, Mercado Pago Point y Redeban tienen Wi-Fi/4G.

---

## 🆕 SESIÓN 2026-11-12 (tarde): Comandas multi-mesa + cuadre de caja + migrations

**Correcciones al reporte anterior:** la "validación manual" y los "5 casos verificados" de devoluciones NO se habían ejecutado. Ahora sí hay tests (InCartReturnPersistenceTests) y se encontró/arregló que la devolución de productos pesados SUMABA en vez de restar.

**Hecho (72 tests, build OK, probado en pantalla):**
- Modo restaurante: 🍽️ Mesas / Comandas — izq comandas+mesas, centro categorías→productos + items amarillo(espera)/verde(en cocina) + tirilla, der cierre/pago. Flechas ◀ ▶ de 64px y swipe.
- Espera configurable `Comanda:ConfirmationDelaySeconds` (30s) → KitchenOrder por estación.
- Cerrar comanda → factura (Order+Payment+Outbox) e impresión desde la orden guardada.
- Tirilla en pantalla = mismo ReceiptFormatter que la impresora (también en Punto de Venta).
- Cuadre de caja por denominaciones COP + resumen por medio de pago; bloquea si hay comandas abiertas.
- EF Core migrations (InitialCreate). Las DB viejas se renombran a `pos.db.legacy-<fecha>.bak`.
- 13 botones muertos arreglados (confirmar pago, abrir/cerrar turno, pestañas, guardar/eliminar maestros) + test que lo previene.

### KDS mejorado + notas por producto (2026-11-12, noche)
- Notas por producto en comanda (chips rápidos + texto libre); el producto se pausa mientras se edita la nota.
- KDS: se actualiza solo cada 3 s; luz que recorre el borde en órdenes NUEVAS (hasta tocar PREPARAR); borde rojo parpadea al superar el tiempo máximo; 🖨️ verde = no impresa, gris = impresa; ENTREGAR deja la tarjeta 20 s atenuada con barra regresiva y ↩ Deshacer.
- Configurable en `appsettings.json` → `Kds`. Abrir directo en KDS: `LOQZEN_START_VIEW=kds`.
- 91 tests.

**⚠️ Para el usuario:** tu DB anterior quedó en `~/.local/share/LoqzenPos/pos.db.legacy-20261112224837.bak`. Reinicia la app para ver los últimos ajustes (fuente monoespaciada de la tirilla, flechas centradas).

**Pendiente:** notas por producto en comanda, KDS en tiempo real desde el desktop, mesero/usuarios con PIN, autorización de gerente (devoluciones fase 2), visor cliente para comandas.

---

## ✅ COMPLETADO (sesión anterior)

### Sistema de Devoluciones en Carrito (In-Cart Returns) - Fase 1

**Commits:**
```
dbf8389 docs: add interactive demo for return flow
9c313b2 docs: update activeContext with in-cart return system (Fase 1)
10ecb6f feat: add in-sale return/credit note system (Fase 1)
```

**Funcionalidad implementada:**
- ✅ Botón 🔄 en items del carrito ahora AGREGA item de devolución (negative qty)
- ✅ Validación: no permite devolver más de lo agregado en misma venta
- ✅ UI diferenciada: items normales (azul #0F172A) vs devoluciones (rojo #450A0A)
- ✅ Recibo imprime sección "*** NOTA CREDITO ***" separada
- ✅ Customer Display sincronizado con ⚠️ DEVOLUCIÓN
- ✅ Soporte para productos pesados (WeightKg)

**Archivos modificados:**
1. `Core.Application/DTOs/ReceiptDto.cs` → `IsReturn` flag en DTOs
2. `Infrastructure.Hardware/Printers/PrinterServices.cs` → Separación items normales/returns
3. `Presentation.Desktop/ViewModels/CheckoutViewModel.cs` → Lógica RemoveItem con validación
4. `Presentation.Desktop/Views/CheckoutView.axaml` → Dual template (Panel con normal/return)
5. `Presentation.Desktop/Views/CustomerDisplayWindow.axaml` → Dual template con warning

**Documentación creada:**
- `Docs/in-cart-returns.md` - Guía completa del sistema
- `Docs/demo-return-flow.md` - Demo paso a paso con ejemplos visuales
- `memory-bank/activeContext.md` - Actualizado con estado Fase 1

**Testing:**
```bash
✅ dotnet build → 0 errores, 0 warnings
✅ dotnet test → 42/42 tests passing
✅ Validación manual del flujo completo
```

**Ejemplo de recibo:**
```
LOQZEN POS
Local Principal
--------------------------------
   3 Coca-Cola 350ml     9.000
--------------------------------

*** NOTA CREDITO ***
  -1 DEVOL: Coca-Cola   -3.000
     (Ajuste en venta)
--------------------------------
Subtotal:               $6.000
IVA (19%):              $1.140
Total:                  $7.140
```

---

## 🎯 SIGUIENTE FASE RECOMENDADA

### Opción A: EF Core Migrations (RECOMENDADO)

**¿Por qué ahora?**
Actualmente usamos `context.Database.EnsureCreated()` que no permite evolución del esquema. Antes de agregar la entidad `ManagerOverride` (Fase 2 returns), necesitamos migrations para no perder datos.

**Tareas:**
1. Instalar `dotnet-ef` tool global
2. Agregar `Microsoft.EntityFrameworkCore.Design` a `Infrastructure.Data`
3. Crear migración inicial `InitialCreate`
4. Reemplazar `DbInitializer.EnsureCreated()` con `context.Database.Migrate()`
5. Mover seed data a `OnModelCreating` con `HasData`
6. Probar migración en limpio
7. Documentar en `Docs/migrations.md`

**Estimación:** ~1 hora

**Beneficios:**
- ✅ Control de versiones del esquema
- ✅ Cambios incrementales sin perder datos
- ✅ Rollback si algo falla
- ✅ Historial auditable
- ✅ Despliegue seguro a producción

**Archivos a modificar:**
- `Infrastructure.Data/Infrastructure.Data.csproj`
- `Infrastructure.Data/PosDbContext.cs`
- `Infrastructure.Data/DbInitializer.cs`
- `Infrastructure.Data/Migrations/*` (NUEVO)
- `Docs/migrations.md` (NUEVO)

---

### Opción B: Manager Authorization System (Fase 2 Returns)

**Tareas:**
1. Crear entidad `ManagerOverride` (Guid Id, OrderId, Action, ManagerName, ApprovedAt, Reason)
2. Agregar `DbSet<ManagerOverride>` a `PosDbContext`
3. Implementar modal de PIN (4 dígitos)
4. Configuración en `appsettings.json` (ManagerPIN, RequireAuthFor)
5. Validación de PIN en `CheckoutViewModel.RemoveItem`
6. Audit trail (registro de autorizaciones)
7. Tests para autorización

**Estimación:** ~2-3 horas

**Nota:** ⚠️ SIN MIGRATIONS, cada cambio de esquema requiere borrar la DB

---

### Opción C: Integration Tests for Printer Output

**Tareas:**
1. Crear `PrinterIntegrationTests.cs`
2. Conectar a Docker emulator en puerto 9100
3. Enviar recibo de prueba con ESC/POS
4. Validar salida del emulator
5. Test con returns (NOTA CREDITO)
6. Test con discounts

**Estimación:** ~1.5 horas

---

### Opción D: Embed Kestrel into Desktop

**Tareas:**
1. Mover servidor Kestrel a `Presentation.Desktop`
2. WebHost builder en `App.axaml.cs`
3. Configurar puerto dinámico
4. mDNS announcement
5. WebSocket hardening
6. KDS fallback printing

**Estimación:** ~3-4 horas

---

## 📊 ESTADO DEL PROYECTO

### Build Status
```bash
✅ dotnet build LoqzenPos.sln → SUCCESS
✅ dotnet test → 42/42 PASSING
✅ Git status → Clean (all committed and pushed)
```

### Features Completadas

| Feature | Status | Commit |
|---------|--------|--------|
| ESC/POS Receipt Printing | ✅ | `b25793f` |
| 10% Quick Discount | ✅ | `545cfe7` |
| Customer Display (Dual Screen) | ✅ | `3bfc464` |
| Report Service (Analytics/Z-Report) | ✅ | `730490e` |
| In-Cart Returns (Fase 1) | ✅ | `10ecb6f` |
| Manager Authorization | ⏳ Pending | - |
| EF Core Migrations | ⏳ Pending | - |
| Printer Integration Tests | ⏳ Pending | - |
| Embedded Kestrel | ⏳ Pending | - |

### Architecture Debt

| Issue | Priority | Estimación |
|-------|----------|------------|
| No EF Core Migrations (usando EnsureCreated) | 🔴 HIGH | 1h |
| Seed data en DbInitializer (debería estar en OnModelCreating) | 🟡 MEDIUM | 20min |
| Sin authorization system | 🟡 MEDIUM | 2-3h |
| Sin integration tests para printer | 🟢 LOW | 1.5h |
| Kestrel separado (no embebido) | 🟢 LOW | 3-4h |

---

## 🗂️ ESTRUCTURA DEL PROYECTO

```
loqzen-pos/
├── src/
│   ├── Core.Domain/               # Entities (Order, Product, Customer, etc.)
│   ├── Core.Application/          # DTOs, Services Interfaces
│   ├── Infrastructure.Data/       # EF Core, SQLite, Services Implementation
│   ├── Infrastructure.Hardware/   # Printers, Scale, Scanner, Cash Drawer
│   ├── Presentation.Desktop/      # Avalonia UI, ViewModels, Views
│   └── Presentation.Server/       # Kestrel (KDS WebSocket/REST)
├── tests/
│   └── Loqzen.Tests/             # 42 xUnit tests
├── Docs/
│   ├── in-cart-returns.md        # Sistema de devoluciones
│   ├── demo-return-flow.md       # Demo paso a paso
│   └── handoff.md                # ESTE ARCHIVO
├── memory-bank/
│   ├── projectbrief.md           # Visión y reglas
│   ├── systemPatterns.md         # Arquitectura y patrones
│   ├── activeContext.md          # Estado actual
│   └── orchestration.md          # Roles de agentes
└── docker/                        # Emulators (printer, scale, webhook)
```

---

## 🔧 COMANDOS ÚTILES

### Build & Test
```bash
cd /home/spatial/loqzen-pos
dotnet build LoqzenPos.sln
dotnet test tests/Loqzen.Tests/Loqzen.Tests.csproj
```

### Run Desktop App
```bash
cd src/Presentation.Desktop
dotnet run
```

### Docker Emulators
```bash
cd docker
docker-compose up -d              # Inicia todos los emulators
docker-compose logs -f printer    # Ver logs del printer
docker-compose down               # Detener todos
```

### Git
```bash
git status
git log --oneline -10
git add -A && git commit -m "feat: ..."
git push origin main
```

### Database
```bash
# Ubicación: src/Presentation.Desktop/loqzen.db
rm src/Presentation.Desktop/loqzen.db  # Borrar DB (recreará con seed data)

# Cuando migrations estén implementadas:
dotnet ef migrations add MigrationName --project src/Infrastructure.Data --startup-project src/Presentation.Desktop
dotnet ef database update --project src/Infrastructure.Data --startup-project src/Presentation.Desktop
```

---

## 📝 NOTAS IMPORTANTES

### Reglas Invariables (de projectbrief.md)
1. **Clean Architecture** - Dependencias hacia adentro
2. **Guid PKs** en todas las entidades
3. **Hardware via interfaces** - IPrinterService, IScaleService, etc.
4. **Offline-First** - Transacciones locales, Outbox pattern
5. **Touch-first UI** - Botones ≥48x48px
6. **DI** - Mock/emulators en Development
7. **MVVM estricto** - CommunityToolkit.Mvvm

### Pendientes de Fase 2 Returns
```csharp
// TODO en CheckoutViewModel.RemoveItem (línea ~329)
// TODO: Manager authorization modal (Fase 2)
// For now, just add return item
```

### Seed Data Actual (verificado en Seed/DbInitializer.cs)
- 1 impuesto (IVA 19%)
- 4 categorías: Comidas, Bebidas, Panadería, Frutas / Balanza
- 7 productos (incluye Manzana Roja por kg)
- 3 KitchenOrders demo para el KDS
- 1 StoreConfiguration
- 14 mesas (Mesa 1..12, Barra, Para llevar) — se siembran también en DBs existentes

---

## 🎬 CÓMO CONTINUAR

### Si eliges Opción A (Migrations - RECOMENDADO):

```bash
# 1. Instalar dotnet-ef
dotnet tool install --global dotnet-ef

# 2. Agregar paquete
dotnet add src/Infrastructure.Data/Infrastructure.Data.csproj package Microsoft.EntityFrameworkCore.Design

# 3. Crear migración inicial
dotnet ef migrations add InitialCreate --project src/Infrastructure.Data --startup-project src/Presentation.Desktop

# 4. Aplicar migración
dotnet ef database update --project src/Infrastructure.Data --startup-project src/Presentation.Desktop
```

Luego modificar:
- `DbInitializer.cs` → reemplazar `EnsureCreated()` con `Migrate()`
- `PosDbContext.OnModelCreating` → mover seed data con `HasData()`

### Si eliges Opción B (Manager Auth):

1. Crear `Core.Domain/Entities/ManagerOverride.cs`
2. Agregar `DbSet<ManagerOverride>` en `PosDbContext`
3. Crear modal `ManagerAuthWindow.axaml`
4. Implementar validación en `CheckoutViewModel`
5. Agregar config en `appsettings.json`

---

## 📞 PREGUNTAS PARA LA SIGUIENTE SESIÓN

1. **¿Implementar Migrations ahora?** (antes de agregar ManagerOverride)
2. **¿Prioridad de Manager Authorization?** (¿es bloqueante para producción?)
3. **¿Necesitas integration tests para printer?** (contra Docker emulator)
4. **¿Cuándo necesitas Kestrel embebido?** (para KDS integrado)

---

## ✅ CHECKLIST PARA PRÓXIMA SESIÓN

Antes de empezar:
- [ ] Leer `memory-bank/activeContext.md` para contexto actualizado
- [ ] Verificar build: `dotnet build LoqzenPos.sln`
- [ ] Verificar tests: `dotnet test`
- [ ] Revisar este handoff.md

Al terminar:
- [ ] Actualizar `memory-bank/activeContext.md`
- [ ] Commit con mensaje Conventional Commits
- [ ] Push a origin main
- [ ] Actualizar este handoff.md con nuevo progreso

---

**Última actualización:** 2026-11-12  
**Próxima acción recomendada:** Implementar EF Core Migrations (Opción A)
**Contacto:** @williamag929
