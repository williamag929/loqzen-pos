# Loqzen POS

> **Punto de venta (POS) + modo restaurante (comandas multi-mesa) + KDS de cocina**, offline-first.
> .NET 8 · Avalonia UI 11 · SQLite (EF Core) · periféricos reales o emulados con Docker.

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](#-stack)
[![Avalonia UI](https://img.shields.io/badge/Avalonia%20UI-11.2-blueviolet)](#-stack)
[![SQLite](https://img.shields.io/badge/SQLite-Offline--First-003B57?logo=sqlite&logoColor=white)](#-stack)
[![Tests](https://img.shields.io/badge/tests-454%20passing-success)](#-tests)

👉 **¿Vas a colaborar? Lee primero [`CONTRIBUTING.md`](CONTRIBUTING.md).**

---

## 📖 Contenido

- [Estado actual](#-estado-actual)
- [Inicio rápido](#-inicio-rápido)
- [Funcionalidades](#-funcionalidades)
- [Stack](#-stack)
- [Estructura del proyecto](#-estructura-del-proyecto)
- [Configuración](#️-configuración)
- [Base de datos y migraciones](#️-base-de-datos-y-migraciones)
- [Hardware y emuladores Docker](#-hardware-y-emuladores-docker)
- [Tests](#-tests)
- [Reglas de arquitectura](#-reglas-de-arquitectura)
- [Roadmap](#️-roadmap)
- [Documentación](#-documentación)

---

## 📌 Estado actual

*Actualizado: 2026-11-13.* Lo que **funciona hoy** en el escritorio (Linux/Windows):

| Área | Estado |
|---|---|
| Venta rápida (catálogo, carrito, descuento 10%, devoluciones en carrito, pago efectivo/tarjeta/transferencia) | ✅ |
| **Tirilla en pantalla** idéntica a la impresa (mismo formateador) | ✅ |
| **Modo restaurante**: comandas por mesa, 3 paneles táctiles, productos amarillo→verde con espera configurable, notas por producto | ✅ |
| **KDS cocina**: actualización automática, luz en el borde de órdenes nuevas, alerta por tiempo, estado de impresión, archivo con deshacer | ✅ |
| **Turno de caja de referencia**: fondo sugerido, tablero en vivo, movimientos (entrada, salida, gasto, retiro a bóveda, propinas) con PIN de gerente y vale impreso, arqueo parcial, **cierre en 4 pasos con conteo a ciegas**, diferencia grande → gerente + nota, fondo para el siguiente turno, **reporte Z** impreso e historial | ✅ |
| **Recaudos** (pago de recibos: energía, agua, gas, recargas…): convenios con comisión, comprobante REC-000001, efectivo/tarjeta/transferencia, anulación con gerente; entra al cuadre aparte de las ventas | ✅ |
| Maestros (productos, categorías, impuestos, clientes) | ✅ |
| Impresión ESC/POS por TCP 9100 + respaldo TXT si falla + pulso de cajón | ✅ |
| Visor de cliente (segunda pantalla) | ✅ venta rápida · ⚠️ no muestra comandas |
| **Servidor LAN embebido** (Kestrel dentro del escritorio): REST + WebSocket, **KDS web** y **comandera web** para tablets/celulares en `http://<ip>:5055/kds` y `/comandera` | ✅ |
| KDS de escritorio **en tiempo real** (eventos, sin esperar la encuesta) | ✅ |
| **Sincronización Outbox → ERP/webhook** con reintentos y estado en Turno de Caja (`Sync:Enabled`) | ✅ |
| Bloqueo tras 5 PIN fallidos (30 s) · autorizaciones de gerente de un solo uso | ✅ |
| **Usuarios con PIN** (4–6 dígitos), roles Cocina/Mesero/Cajero/Gerente/Admin, pestañas según rol | ✅ |
| **Autorización de gerente** (PIN) para devoluciones, descuentos y anular productos ya en cocina, con auditoría (`ManagerOverrides`) | ✅ |
| Segunda caja (modo satélite), datáfono, ZPL, mDNS, app Android nativa | ❌ pendiente |

Detalle y próximos pasos: [`Docs/handoff.md`](Docs/handoff.md).

---

## 🚀 Inicio rápido

**Requisitos:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (probado con 8.0.428). Docker es opcional (solo para emular hardware).

```bash
git clone https://github.com/williamag929/loqzen-pos.git
cd loqzen-pos
dotnet tool restore                                   # instala dotnet-ef 8.0.11 (herramienta local)
dotnet build LoqzenPos.sln
dotnet test                                           # 91 tests, ~1 s
dotnet run --project src/Presentation.Desktop
```

Al primer arranque la app **crea la base SQLite, aplica migraciones y carga datos demo** (productos, 14 mesas, órdenes de KDS).
Por defecto usa impresora/balanza/escáner **simulados** (ver [Configuración](#️-configuración)).

Usuarios por defecto (cámbialos en **🗂️ Maestros → 🔑 Usuarios**): **Administrador 1234** · **Cajero 1111** · **Mesero 2222**.

Flujo para probar en 2 minutos:
0. Pantalla de PIN → **1234** (Administrador ve todas las pestañas; Cajero no ve Maestros; Mesero solo Comandas y KDS).
1. **💼 Turno de Caja** → Abrir turno (el cajero se toma del usuario conectado).
2. **🍽️ Mesas / Comandas** → tocar "Mesa 1" → elegir categoría → producto (queda amarillo 30 s) → 📝 Nota → guardar.
3. **👨‍🍳 KDS Cocina** → la orden aparece con luz en el borde → PREPARAR → LISTO → ENTREGAR (20 s con deshacer).
4. Volver a Comandas → **Cerrar ▶** → cobrar → se genera la factura.
   Para probar la autorización: entra como **Cajero (1111)**, en Punto de Venta toca 🔄 en un producto del carrito → pide **PIN de gerente** (1234).
5. Turno de Caja → **Cerrar turno / Arqueo** → contar billetes y monedas.

Abrir directamente en una pestaña (pantallas dedicadas, p. ej. cocina):
```bash
LOQZEN_START_VIEW=kds dotnet run --project src/Presentation.Desktop   # kds | comandas | caja | recaudos | maestros
```

---

## ✨ Funcionalidades

### Venta rápida (🛒 Punto de Venta)
Búsqueda por nombre/SKU/código de barras, filtros por categoría, productos por peso (balanza), +/−, descuento rápido, **devolución en el mismo ticket** (🔄 → sección *NOTA CREDITO* en el recibo), pago con numpad táctil y tirilla en vivo.

### Modo restaurante (🍽️ Mesas / Comandas)
Navegación por 3 paneles con flechas ◀ ▶ (64 px) o deslizando el dedo:
- **Izquierda:** comandas abiertas + mapa de mesas (libre/ocupada).
- **Centro:** categorías → productos; cada producto entra **amarillo** con cuenta regresiva (anulable, con 📝 nota) y pasa a **verde** = enviado a cocina. Una orden de cocina por estación (bebidas → Barra).
- **Derecha:** pre-cuenta, cobro y **factura** (Order + Payment + Outbox en una sola transacción).

### KDS (👨‍🍳 KDS Cocina)
Orden de llegada, filtro por estación, verde/amarillo/rojo por tiempo (rojo parpadea al superar el máximo), luz que recorre el borde de las órdenes nuevas, 🖨️ verde = no impresa / gris = impresa, entregar → 20 s atenuada con ↩ Deshacer.

### Turno y cuadre de caja (💼 Turno de Caja)
Apertura con fondo inicial; cierre con conteo por denominación (100.000 … 50), esperado vs. contado vs. diferencia, ventas por medio de pago. No permite cerrar con comandas abiertas.

---

### Usuarios y autorización de gerente (🔐)
PIN de 4–6 dígitos en pantalla táctil (sin usuario, el PIN identifica a la persona; se guarda con hash PBKDF2 y no puede repetirse entre usuarios activos). Roles ordenados por privilegio: **Cocina** (solo KDS) → **Mesero** (+Comandas) → **Cajero** (+Punto de Venta, Turno) → **Gerente** (+Maestros, autoriza) → **Admin** (+crea otros Admin). Las pestañas se muestran según el rol y el chip 👤 permite **Cambiar usuario**.
Devolver un producto, aplicar descuento o **anular un producto que ya está en cocina** abre el modal de PIN de gerente (si quien está conectado ya es gerente, se registra sin preguntar). Cada aprobación queda en `ManagerOverrides` (quién autorizó, a quién, monto, referencia, hora). En el KDS el producto anulado aparece tachado con etiqueta **ANULADO**; si no queda nada por cocinar la orden se cancela.

### Turno de caja y recaudos (💼 Caja · 🧾 Recaudos)
- **Esperado = fondo + ventas en efectivo + recaudos en efectivo + entradas − salidas − gastos − retiros − propinas.** Una sola regla en todo el sistema.
- **Cierre en 4 pasos:** revisión (bloquea si hay comandas abiertas, muestra lo esperado en tarjeta para comparar con el datáfono) → **conteo a ciegas** por denominación → resultado (diferencia en verde/ámbar/rojo; sobre el umbral pide PIN de gerente y nota) → fondo que queda para el siguiente turno. Imprime el **reporte Z**; el historial permite reimprimir cualquier cierre.
- **Recaudos:** elija convenio, escanee o digite la referencia, valor + comisión = total, cobre en efectivo/tarjeta/transferencia, comprobante impreso y pantalla de cambio. Anular pide gerente. Los convenios se administran en Maestros → 🏢 Convenios.

### Pantallas en red (🌐 KDS web y comandera web)
El escritorio arranca un servidor Kestrel en el puerto 5055 (chip 🌐 en la barra superior; tocarlo copia la URL). Cualquier tablet o celular en la misma red abre:
- `http://<ip>:5055/kds` — KDS de cocina en el navegador: filtro por estación, colores por tiempo, PREPARAR → LISTO → ENTREGAR con deshacer, impresión, productos anulados tachados. Se actualiza por WebSocket al instante y reconecta solo.
- `http://<ip>:5055/comandera` — comandera para meseros: mesas libres/ocupadas, categorías → productos, notas rápidas, anular pendientes, "Enviar ya" y "Lista para pagar". El cobro sigue en la caja.
Todo pasa por los mismos servicios que el escritorio (mismas reglas, misma base de datos). API REST documentada en `src/Presentation.Server/Endpoints/`.

## 🛠 Stack

| Capa | Tecnología |
|---|---|
| Runtime | .NET 8 (`net8.0` en todos los proyectos) |
| UI | Avalonia UI 11.2 + Fluent, MVVM con **CommunityToolkit.Mvvm 8.3** |
| Persistencia | SQLite + EF Core 8.0.11, **migraciones** (`dotnet-ef` herramienta local) |
| DI / config | Microsoft.Extensions.DependencyInjection + `appsettings.json` |
| Servidor LAN | ASP.NET Core minimal API + WebSockets (`Presentation.Server`, **biblioteca embebida** en el escritorio) |
| Hardware | TCP raw (ESC/POS), `System.IO.Ports` (balanza CAS), USB HID (escáner) |
| Tests | xUnit, FluentAssertions, Moq, EF InMemory + SQLite real |
| Emuladores | Docker Compose + Python |

---

## 📂 Estructura del proyecto

```text
loqzen-pos/
├── src/
│   ├── Core.Domain/              # Entidades (Guid PK), enums. Sin dependencias.
│   ├── Core.Application/         # DTOs, interfaces de servicios y hardware,
│   │   ├── Receipts/             #   ReceiptFormatter (único layout de tirilla), ReceiptDtoFactory
│   │   └── Kitchen/              #   KdsSettings, KdsTiming (reglas de tiempo del KDS)
│   ├── Infrastructure.Data/      # PosDbContext, Migrations/, Seed/DbInitializer, Services/
│   ├── Infrastructure.Hardware/  # Impresora ESC/POS (+EscPosRenderer), balanza, escáner (reales y mock)
│   ├── Presentation.Desktop/     # App Avalonia: Views/, ViewModels/, Controls/, Converters/
│   └── Presentation.Server/      # Biblioteca Kestrel embebida: REST, WebSocket /ws/kds, wwwroot/kds.html + comandera.html
├── tests/Loqzen.Tests/           # Domain/, Services/, Integration/ (SQLite real), Receipts/, Ui/
├── docker/                       # docker-compose.yml + emulator/{printer,scale,webhook}
├── Docs/                         # Especificaciones, handoff.md (estado y pendientes)
├── memory-bank/                  # Contexto para agentes IA (activeContext.md = estado técnico)
├── CONTRIBUTING.md               # Guía para colaboradores
└── LoqzenPos.sln
```

---

## ⚙️ Configuración

`src/Presentation.Desktop/appsettings.json` (se copia al directorio de salida; también se lee `appsettings.{DOTNET_ENVIRONMENT}.json`):

| Sección | Clave | Default | Qué hace |
|---|---|---|---|
| `Hardware` | `UseMockPrinter` / `UseMockScale` / `UseMockScanner` | `true` | `false` = usar dispositivo real |
| | `PrinterHost`, `PrinterPort` | `127.0.0.1`, `9100` | Impresora ESC/POS por red (o el emulador Docker) |
| | `ScalePortName`, `ScaleBaudRate` | `/dev/ttyUSB0`, `9600` | Balanza CAS SW-20RS por RS-232 |
| `Comanda` | `ConfirmationDelaySeconds` | `30` | Espera (amarillo) antes de enviar a cocina |
| | `QuickNotes` | 12 notas | Botones de notas rápidas ("Sin cebolla"…) |
| `Authorization` | `RequireLogin` | `true` | `false` = terminal abierto sin pantalla de PIN (p. ej. KDS de cocina) |
| | `ManagerRequiredFor` | `ReturnItem`, `ApplyDiscount`, `CancelKitchenItem` | Acciones que piden PIN de gerente (también `OpenCashDrawer`, `CloseShift`); `[]` = ninguna |
| `CashShift` | `BlindCount` / `MaxDifferenceWithoutManager` / `DefaultFloat` | `true` / `5000` / `200000` | Conteo a ciegas, umbral de diferencia que pide gerente + nota, fondo sugerido |
| | `Bills` / `Coins` / `ManagerRequiredFor` | COP / — | Denominaciones del conteo; movimientos que piden gerente (por defecto salidas, gastos, retiros y propinas) |
| `Server` | `Enabled` / `Port` / `BindAddress` | `true` / `5055` / `0.0.0.0` | Servidor LAN embebido (KDS web, comandera web, REST, WebSocket). `127.0.0.1` = solo este equipo |
| `Sync` | `Enabled` / `WebhookUrl` / `ApiKey` | `false` / `http://localhost:5000/webhook` / — | Envío del Outbox a un ERP o webhook (POST JSON, `Authorization: Bearer`) |
| | `IntervalSeconds` / `BatchSize` / `MaxRetries` / `TimeoutSeconds` | `15` / `50` / `10` / `10` | Reintentos con espera exponencial (1 s … 5 min); tras `MaxRetries` queda *fallido* y se reintenta desde Caja |
| `Kds` | `WarningMinutes` / `MaxPrepMinutes` | `5` / `15` | Umbrales amarillo / rojo (parpadea) |
| | `ArchiveDelaySeconds` | `20` | Tiempo con "Deshacer" tras ENTREGAR |
| | `PollSeconds` | `3` | Cada cuánto el KDS revisa órdenes nuevas |

Variables de entorno: `DOTNET_ENVIRONMENT` (default `Development`), `LOQZEN_START_VIEW`.

---

## 🗄️ Base de datos y migraciones

- Ubicación: `~/.local/share/LoqzenPos/pos.db` (Linux) · `%LOCALAPPDATA%\LoqzenPos\pos.db` (Windows).
  Para una base aislada en pruebas: `XDG_DATA_HOME=/tmp/loqzen dotnet run --project src/Presentation.Desktop` (Linux).
- Al arrancar, `DbInitializer` aplica las migraciones pendientes **conservando los datos**.
- Bases creadas por versiones antiguas (sin historial de migraciones) se **renombran** a `pos.db.legacy-<fecha>.bak` (nunca se borran) y se crea una nueva.
- Crear una migración tras cambiar entidades:
  ```bash
  dotnet tool run dotnet-ef migrations add NombreDescriptivo \
    --project src/Infrastructure.Data --startup-project src/Infrastructure.Data --output-dir Migrations
  ```

---

## 🔌 Hardware y emuladores Docker

| Dispositivo (referencia) | Conexión | Implementación |
|---|---|---|
| Impresora Epson TM-T20III / TM-T88VI | TCP 9100 | `EscPosNetworkPrinterService` (si falla → TXT en `LoqzenPos/FailedReceipts/`) |
| Cajón monedero | RJ11 a la impresora, pulso `ESC p` (`0x1B 0x70`) | `IPrinterService.OpenCashDrawerAsync` |
| Balanza CAS SW-20RS | RS-232, tramas `ST,GS,+001.25kg` | `CasSerialScaleService` |
| Escáner USB HID | teclado | `UsbHidBarcodeScannerService` |
| Visor de cliente | 2ª pantalla | `CustomerDisplayWindow` |

Emuladores (desarrollo sin hardware):
```bash
docker compose -f docker/docker-compose.yml up -d
```
| Servicio | Puerto | Notas |
|---|---|---|
| `thermal-printer-emulator` | TCP 9100 | Recibe ESC/POS y registra el pulso de cajón |
| `serial-scale-emulator` | TCP 9101 | Tramas CAS por TCP (no puerto serie) |
| `delivery-webhook-mock` | HTTP 5000 | Mock de Rappi/Uber Eats |

Para imprimir en el emulador: `"UseMockPrinter": false` en `appsettings.json`.

---

## 🧪 Tests

```bash
dotnet test                                         # todo
dotnet test --filter FullyQualifiedName~Comanda     # un área
```
Incluye tests con **SQLite real** (flujo completo y migraciones sobre archivos) y tests de **XAML** que fallan si un binding de comando no existe o si una vista combina `DataContext` + `IsVisible` en el mismo elemento (dos bugs reales que dejaron botones/pestañas muertos).

> Los tests de XAML leen el ensamblado compilado del escritorio: ejecuta `dotnet build LoqzenPos.sln` antes de `dotnet test --no-build`.

---

## 📐 Reglas de arquitectura

1. **Clean Architecture:** `Presentation.*` e `Infrastructure.*` dependen hacia adentro; la lógica de negocio vive en servicios de `Core.Application` / `Infrastructure.Data`, nunca en Views.
2. **Guid PK** en todas las entidades (seguro para nodos offline).
3. **Hardware solo por interfaces** (`IPrinterService`, `IScaleService`, `IBarcodeScannerService`); real vs. mock se elige por configuración.
4. **Offline-first + Outbox:** cada transacción se guarda localmente y escribe su `OutboxMessage` en el **mismo** `SaveChanges`.
5. **Táctil:** botones ≥ 48×48 px; textos de UI en español, código en inglés.

Detalle y convenciones de código: [`CONTRIBUTING.md`](CONTRIBUTING.md).

---

## 🗺️ Roadmap

- [x] Fase 1 – POS core, SQLite + migraciones, maestros, turno de caja
- [x] Fase 2 – ESC/POS, cajón, balanza, escáner, emuladores Docker
- [x] Fase 3 (parcial) – visor de cliente, comandas multi-mesa, KDS
- [x] Usuarios/PIN y autorización de gerente (devoluciones, descuentos, anular en cocina)
- [x] Kestrel embebido en el escritorio + KDS web y comandera web en tiempo real (WebSocket)
- [x] Visor de cliente para comandas
- [x] Despachador del Outbox hacia ERP/webhook
- [x] **Pago de recibos / recaudos en caja** (servicios públicos, recargas, convenios): módulo con comisión, su propio comprobante y **entrada al cuadre de caja** como movimiento aparte de las ventas
- [x] **Cuadre de caja de referencia** (meta: el más fácil y a prueba de errores de la industria, comparado con Toast y Square): movimientos de efectivo con motivo (entrada por recibo, gasto, retiro a bóveda, propinas) y PIN de gerente; **conteo a ciegas** (la cajera cuenta sin ver el esperado y solo después ve la diferencia); conteo por denominaciones con calculadora táctil (ya existe); diferencia grande → PIN de gerente + nota obligatoria; esperado por medio de pago vs lo reportado por el datáfono; arqueo parcial a mitad de turno y "dejar fondo" para el siguiente; reporte Z impreso y en pantalla; historial de cuadres por cajero
- [ ] **Segunda caja** (modo maestro/satélite: `TerminalId` en ventas y turnos, varios turnos abiertos, servicios del escritorio sobre HTTP)
- [ ] Datáfono semi-integrado (`IPaymentTerminalService`: el POS envía el monto, el datáfono cobra chip/NFC/Apple Pay/Google Pay y responde aprobado → pago con tarjeta se confirma solo) + QR dinámico (Nequi/Daviplata/Bre-B) en el visor del cliente
- [ ] **Pago en la mesa**: una sola caja y varios datáfonos inalámbricos (Wi-Fi/4G) asignados a meseros. Desde la comandera web el mesero toca "Cobrar en mesa" → el POS envía el monto al datáfono del mesero → el cliente paga en la mesa (tap, chip, Apple Pay / Google Pay) sin acercarse a la caja ni entregar la tarjeta → el datáfono responde aprobado → la comanda se cierra y factura sola en la caja. Requiere el `IPaymentTerminalService` anterior con soporte multi-terminal (un datáfono por mesero/mesa) y el endpoint de cierre en `Presentation.Server`.
- [ ] ZPL, mDNS, app Android nativa

---

## 📚 Documentación

| Documento | Para qué |
|---|---|
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | **Guía para colaboradores** (empezar aquí) |
| [`Docs/handoff.md`](Docs/handoff.md) | Estado de la última sesión y pendientes |
| [`memory-bank/activeContext.md`](memory-bank/activeContext.md) | Estado técnico detallado y deuda |
| [`Docs/Especificacion_Tecnica_POS_Multiplataforma.md`](Docs/Especificacion_Tecnica_POS_Multiplataforma.md) | Especificación POS (visión original) |
| [`Docs/Especificacion_Tecnica_KDS_Comandas.md`](Docs/Especificacion_Tecnica_KDS_Comandas.md) | Especificación KDS y comandas |
| [`Docs/Arquitectura_Docker_Emulacion_POS.md`](Docs/Arquitectura_Docker_Emulacion_POS.md) | Emuladores Docker |
| [`Docs/Hardware_POS_Seleccionado.md`](Docs/Hardware_POS_Seleccionado.md) | Hardware de referencia |
| [`Docs/in-cart-returns.md`](Docs/in-cart-returns.md) | Devoluciones en carrito |

> Las especificaciones en `Docs/` describen la **visión** (incluye .NET 9, Android, ZPL, mDNS). La tabla [Estado actual](#-estado-actual) indica lo que realmente está implementado.

---

## 📄 Licencia

Propietario — Todos los derechos reservados © Loqzen POS.
