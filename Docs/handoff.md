# Handoff / Progress Report - Loqzen POS
**Fecha:** 2026-11-12  
**Última sesión:** Implementación Sistema de Devoluciones en Carrito (Fase 1)

---

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
