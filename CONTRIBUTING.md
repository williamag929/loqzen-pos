# Guía para colaboradores — Loqzen POS

Nota para quien se suma al proyecto. Léela completa una vez: las secciones **"Trampas conocidas"** y **"Checklist"** te van a ahorrar horas.

---

## 1. Primer día (15 minutos)

```bash
git clone https://github.com/williamag929/loqzen-pos.git && cd loqzen-pos
dotnet tool restore            # dotnet-ef 8.0.11 local (.config/dotnet-tools.json)
dotnet build LoqzenPos.sln     # debe dar 0 errores
dotnet test                    # debe pasar todo (~1 s)
XDG_DATA_HOME=/tmp/loqzen dotnet run --project src/Presentation.Desktop   # base de datos aislada (Linux)
```

Luego lee, en este orden:
1. [`README.md`](README.md) → sección *Estado actual* (qué funciona y qué no).
2. [`Docs/handoff.md`](Docs/handoff.md) → qué se hizo en la última sesión y qué sigue.
3. [`memory-bank/activeContext.md`](memory-bank/activeContext.md) → detalle técnico y deuda.

> `memory-bank/` y `Docs/handoff.md` también los usan agentes de IA como contexto. Manténlos al día (ver Checklist).

---

## 2. Mapa del código: dónde va cada cosa

| Quiero… | Lugar |
|---|---|
| Agregar/cambiar una entidad | `src/Core.Domain/Entities/` (+ configuración en `Infrastructure.Data/Context/PosDbContext.cs` + **migración**) |
| Definir un caso de uso | Interfaz en `Core.Application/Services/I*.cs`, implementación en `Infrastructure.Data/Services/` |
| DTOs | `Core.Application/DTOs/` |
| Formato de la tirilla/recibo | `Core.Application/Receipts/ReceiptFormatter*.cs` (**único** lugar) |
| Reporte Z, comprobante de recaudo, vale de movimiento | `Core.Application/Receipts/CashReports*.cs` → `IPrinterService.PrintLinesAsync` |
| Reglas de tiempo del KDS | `Core.Application/Kitchen/KdsTiming.cs` |
| Drivers de hardware | `Infrastructure.Hardware/` detrás de las interfaces de `Core.Application/Hardware/HardwareInterfaces.cs` |
| Pantallas | `Presentation.Desktop/Views/*.axaml` + `ViewModels/` (+ `Controls/`, `Converters/`) |
| Registrar servicios / config | `Presentation.Desktop/ServiceCollectionExtensions.cs` y `appsettings.json` |
| Pedir PIN de gerente desde una pantalla | Inyecta `IManagerAuthPrompt` (`Presentation.Desktop/Services`) y llama `RequestAsync(AuthorizedAction, descripción, monto, referencia)`; nunca abras el modal a mano |
| Saber quién está conectado / qué puede ver | `IUserSession` (`UserSession` singleton) + `Core.Application/Security/UserPermissions.cs` |
| Datos demo | `Infrastructure.Data/Seed/DbInitializer.cs` |
| Endpoint o página para tablets | `Presentation.Server/Endpoints/*.cs` (+ `wwwroot/*.html` embebido). Usa `WithScopedAsync<TService>` y `MapError`; nunca `PosDbContext` directo |
| Reaccionar a cambios de cocina en una pantalla | Suscríbete a `IKitchenEventBus` y vuelve al hilo UI con `Dispatcher.UIThread.Post` (con *debounce*, ver `KdsViewModel`) |

### Servicios principales

| Servicio | Responsable de |
|---|---|
| `ISalesService` | Venta rápida: orden, items, descuento, pago (+ Outbox) |
| `IComandaService` | Mesas, comandas, items pendiente→confirmado, notas/pausa, cierre = factura |
| `IKitchenService` | KDS: órdenes activas, avanzar estado, impresa, entregar/deshacer |
| `ICashShiftService` | Turno: fondo sugerido, movimientos (`RecordMovementAsync`), `GetReconciliationAsync` (única regla de esperado), arqueo parcial, `CloseShiftAsync(CloseShiftRequest)` con umbral de gerente, historial |
| `IBillPaymentService` | Recaudos: convenios, cobro con comisión (REC-xxxxxx), anulación con override; crea el movimiento de efectivo |
| `IReportService` | Reportes diarios / Z |
| `IKitchenEventBus` | Pub/sub en proceso: los servicios publican `KitchenEvent` tras guardar; el KDS de escritorio y el hub WebSocket se refrescan al instante |
| `IOutboxDispatcher` / `IOutboxSink` | Envía `OutboxMessages` pendientes al webhook (`Sync`), reintentos exponenciales, `RetryFailedAsync` |
| `LoqzenServer` (`Presentation.Server`) | Kestrel embebido: `/kds`, `/comandera`, `/api/*`, `/ws/kds`; una `IServiceScope` por petición |
| `IUserService` | Usuarios con PIN (hash, unicidad entre activos, login por PIN) |
| `IManagerAuthorizationService` | PIN de gerente → registra `ManagerOverride` (auditoría); `RequiresAuthorization` lee `Authorization:ManagerRequiredFor` |
| `IProductService`, `ICategoryService`, `ITaxService`, `ICustomerService` | Maestros |

---

## 3. Convenciones

- **Idioma:** textos de UI en **español**; identificadores, comentarios de código y commits en **inglés**.
- **Namespaces** file-scoped y acordes a la carpeta (`Infrastructure.Data.Services`, …).
- **Guid PK** en toda entidad (`BaseEntity`). Dinero `decimal` con `HasPrecision(18,2)`; cantidades `(18,3)`.
- **Tiempo:** nunca `DateTime.UtcNow` en lógica nueva; inyecta `TimeProvider` (en tests usa `Fixtures/TestClock`).
- **Transacciones:** el cambio de negocio y su `OutboxMessage` se guardan en el **mismo** `SaveChanges`. Valida **antes** de modificar entidades rastreadas (si no, un fallo deja cambios a medio guardar en el contexto).
- **Archivos grandes:** se dividen en clases `partial` por responsabilidad (`ComandaService.cs/.Items.cs/.Close.cs/.Helpers.cs`, `ComandaViewModel.*.cs`).
- **UI táctil:** botones ≥ 48×48 px (acciones principales 52–64 px). Evita `ToolTip` como única pista: en pantalla táctil no se ve.
- **Commits:** Conventional Commits (`feat(kds): …`, `fix(ui): …`, `docs: …`). Un commit = un cambio coherente que compila y pasa tests.

---

## 4. Patrones que debes seguir

### 4.1 ViewModels y base de datos
- Los ViewModels **no** usan `PosDbContext` directamente (hay deuda antigua en `CheckoutViewModel`; no la copies).
- ViewModels **singleton** con temporizador (`ComandaViewModel`, `KdsViewModel`) resuelven servicios con un scope por operación:
  ```csharp
  using var scope = _scopes.CreateScope();
  var svc = scope.ServiceProvider.GetRequiredService<IKitchenService>();
  ```
  Motivo: `DbContext` no es thread-safe y el timer corre en paralelo con las acciones del usuario.

### 4.2 Comandos (CommunityToolkit.Mvvm)
- `[RelayCommand] Task GuardarAsync()` genera **`GuardarCommand`** (sin "Async"). En XAML usa `{Binding GuardarCommand}`.
- En listas, cada item tiene su ViewModel con su propio comando (`CategoryTileViewModel.SelectCommand`, `ComandaItemRowViewModel.CancelCommand`). Así el template enlaza directo al item, sin `#Root.DataContext.X`.
- Vistas nuevas: `x:DataType` + `x:CompileBindings="True"` para que los errores de binding salgan al compilar.

### 4.3 Recibos
- Cualquier recibo (impreso, TXT de respaldo, vista previa) sale de `ReceiptFormatter.Format(dto)`. Construye el DTO con `ReceiptDtoFactory` (`FromCart`, `FromComanda`, `FromOrder`).
- La factura final se imprime desde la **orden guardada** (`FromOrder`), no desde el estado de la pantalla.

### 4.4 Listas que se refrescan
- Si una lista se actualiza periódicamente (KDS), **fusiona** cambios en las instancias existentes (`card.Apply(entity)`) en vez de recrear: recrear reinicia animaciones y cuentas regresivas.

---

## 5. Trampas conocidas (todas ocurrieron de verdad)

| Síntoma | Causa | Solución / protección |
|---|---|---|
| Botón no hace nada, sin error | XAML enlazado a `XxxAsyncCommand`; el toolkit genera `XxxCommand` | Test `XamlCommandBindingTests` falla si un comando no existe |
| Todas las pestañas visibles, la última tapa las demás | `IsVisible` en el **mismo** elemento que `DataContext="{Binding X}"` (se evalúa contra X) | Pon `IsVisible` en un `Panel` contenedor. Test `NoElement_CombinesDataContextBinding…` |
| Devolución de producto pesado **cobra** en vez de restar | Productos por peso se calculan con `WeightKg`; había que negar el peso, no solo la cantidad | Test `InCartReturnPersistenceTests` |
| `SUM` de decimales falla en SQLite | EF Core no traduce `Sum` de `decimal` en SQLite | Trae los valores (`Select(...).ToListAsync()`) y suma en memoria |
| Test pasa con InMemory pero falla en producción | El proveedor InMemory no valida traducción SQL | Para flujos nuevos agrega un test en `Integration/` con SQLite `:memory:` real |
| Cifra grande recortada en un `Grid` `*, Auto` | La columna `Auto` del monto no crecía al cambiar el texto | Etiqueta en `Auto`, monto en `*` con `TextAlignment="Right"` (modal de cobro) |
| Encabezado con 3 `StackPanel` en un `Grid` se encima en ventanas angostas | `StackPanel` no recorta ni envuelve | Usa `WrapPanel` para barras de herramientas (KDS, barra superior) |
| La app no se cerraba con `kill`/apagado del equipo | El `ConsoleLifetime` del host Kestrel capturaba SIGTERM y solo detenía el servidor | `EmbeddedHostLifetime` en `LoqzenServer`; el escritorio decide cuándo terminar |
| `DataTemplate` con 2 hijos no compila (AVLN3000) | Un template solo admite un hijo | Envuelve en `<Panel>` |
| Cocina recibe el plato sin la nota | El temporizador confirmó mientras se escribía | Por eso existe `IsOnHold`: pausa el item mientras el editor está abierto |

---

## 6. Base de datos

1. Cambia la entidad y su configuración en `PosDbContext`.
2. Crea la migración:
   ```bash
   dotnet tool run dotnet-ef migrations add NombreDescriptivo \
     --project src/Infrastructure.Data --startup-project src/Infrastructure.Data --output-dir Migrations
   ```
3. Revisa el archivo generado (que no haya `DropTable`/`DropColumn` inesperados).
4. Agrega o ajusta un test en `tests/Loqzen.Tests/Integration/MigrationTests.cs` si la migración mueve datos.
5. **Nunca** edites una migración ya publicada en `main`; crea otra.

Datos demo: `DbInitializer` solo siembra si la tabla está vacía, así que no duplica datos al reiniciar.

---

## 7. Tests

| Carpeta | Qué contiene |
|---|---|
| `Domain/` | Cálculos de entidades |
| `Services/` | Servicios con EF InMemory (`TestDbContextFactory`), reloj controlado (`TestClock`) |
| `Integration/` | SQLite real: flujo completo, migraciones sobre archivos |
| `Receipts/` | Formato de tirilla (ancho 32, ASCII, secciones) |
| `Ui/` | Validación estática de XAML (comandos y patrón `DataContext`/`IsVisible`) |

Reglas:
- Todo servicio nuevo trae tests. Todo bug corregido trae un test que fallaba antes del arreglo.
- Lógica dependiente del tiempo se prueba avanzando `TestClock`, nunca con `Task.Delay`.
- Los tests de `Ui/` leen `Presentation.Desktop.dll`: compila la solución completa antes.

---

## 8. Probar la UI

- Base aislada: `XDG_DATA_HOME=/tmp/loqzen` (Linux).
- Abrir en una pestaña: `LOQZEN_START_VIEW=kds|comandas|caja|maestros`.
- Hardware: por defecto todo es mock (salida en consola). Para la impresora emulada: `docker compose -f docker/docker-compose.yml up -d` y `"UseMockPrinter": false`.
- Si un recibo no imprime, revisa `~/.local/share/LoqzenPos/FailedReceipts/` (respaldo TXT).
- Los tests no cubren lo visual: si tocaste XAML, **abre la app y míralo** antes de dar el cambio por terminado.

---

## 9. Checklist antes de hacer push

- [ ] `dotnet build LoqzenPos.sln` → 0 errores.
- [ ] `dotnet test` → todo pasa.
- [ ] Si cambiaste entidades: migración creada y revisada.
- [ ] Si tocaste XAML: abriste la app y verificaste la pantalla.
- [ ] Sin `DbContext` en ViewModels nuevos; sin lógica de negocio en Views.
- [ ] Actualizado `memory-bank/activeContext.md` (estado/deuda) y, si cerraste una tarea grande, `Docs/handoff.md`.
- [ ] README → *Estado actual*, si cambió lo que funciona.
- [ ] Commit con Conventional Commits. **No** uses `git push --force` sobre `main`.

---

## 10. Pendientes priorizados (buenos primeros aportes)

1. **Kestrel embebido** en el escritorio + WebSocket hacia KDS/comanderas en otros equipos (hoy `Presentation.Server` es un proceso aparte con lista de sockets que nunca se limpia).
2. **Visor de cliente** para comandas.
3. Comandas: productos por peso leyendo la balanza (hoy 1 kg fijo); agrupar el mismo producto en una línea.
4. Sacar `PosDbContext` de `CheckoutViewModel` y moverlo a `ISalesService`.
5. Barra superior: en ventanas angostas los botones "Scan"/"Visor Cliente" se superponen a las pestañas.

¿Dudas de arquitectura? Revisa `memory-bank/systemPatterns.md` y `memory-bank/projectbrief.md` (reglas invariables).
6. Autorización: un `ManagerOverride` de `CancelKitchenItem` se puede reutilizar (no se marca como consumido); `UserService` no valida `CanAssignRole` (solo la UI); el requester queda vacío si el llamador no lo pasa en el flujo por PIN.
