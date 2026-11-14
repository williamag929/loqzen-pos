# Integración con ERP por hooks — diseño

**Fecha:** 2026-11-14 · **Estado:** diseño aprobado para implementar en fases (ver §8). Lo marcado ✅ ya existe en el código.

## 1. Objetivo y principios

Conectar Loqzen POS con cualquier ERP o contabilidad (SAP Business One, Siigo, Alegra, World Office, un sistema propio) **sin que la caja dependa del ERP**:

1. **La venta nunca espera al ERP.** Todo se guarda primero en SQLite y se envía después (patrón Outbox ✅).
2. **Ninguna venta se pierde ni se duplica.** Entrega *al menos una vez* con reintentos; el ERP deduplica por el id del mensaje (`Idempotency-Key`).
3. **Una sola fuente hacia afuera.** Solo la **caja principal** habla con el ERP; las satélites le envían todo a ella (✅ fase segunda caja). El ERP ve una tienda, y cada evento dice de qué caja salió (`terminalCode`).
4. **Auditable y reenviable.** Cada mensaje queda en `OutboxMessages` con estado, intentos y último error; se puede reintentar o reenviar un rango de fechas desde la pantalla de Caja.
5. **Contrato estable y versionado.** Los payloads solo crecen (campos nuevos, nunca renombrados); `schemaVersion` avisa cambios mayores.

## 2. Topología

```
 CAJA-02 (satélite) ──Outbox──▶ CAJA-01 (principal) ──Outbox──▶ ERP / webhook
 CAJA-03 (satélite) ──Outbox──▶        │                     ◀──── hooks de entrada
 tablets (comandera/KDS web) ───────────┘                          (push en LAN o polling)
```

- **Salida (POS → ERP):** webhooks HTTP POST firmados. ✅ `HttpOutboxSink` + `OutboxDispatcher` + `OutboxSyncWorker`.
- **Entrada (ERP → POS):** dos transportes, porque una tienda rara vez tiene IP pública:
  - **Push** a la API embebida de la principal (`http://<ip>:5055/api/hooks/...`) cuando el ERP está en la misma red o hay VPN.
  - **Polling**: la principal consulta cada N segundos `GET {Sync:InboundPollUrl}?since=<cursor>` y aplica lo que el ERP tenga pendiente para ella. Mismo formato de sobre en ambos.

## 3. Hooks de salida (POS → ERP)

### 3.1 Sobre (envelope) ✅ con extensiones

```json
POST {Sync:WebhookUrl}
Authorization: Bearer <Sync:ApiKey>
Content-Type: application/json
Idempotency-Key: 7d0a9f1e-...            ← = id del mensaje (nuevo)
X-Loqzen-Event: OrderCompleted           ← (nuevo)
X-Loqzen-Terminal: CAJA-02               ← caja de origen (nuevo)
X-Loqzen-Signature: sha256=<hmac>        ← HMAC-SHA256 del cuerpo con Sync:SigningSecret (nuevo)
X-Loqzen-Schema: 1                       ← (nuevo)

{
  "id": "7d0a9f1e-...",            // OutboxMessage.Id — clave de idempotencia
  "eventType": "OrderCompleted",
  "aggregateType": "Order",
  "aggregateId": "c3f1...",
  "occurredAt": "2026-11-14T13:05:22Z",
  "terminalCode": "CAJA-02",
  "schemaVersion": 1,              // nuevo
  "store": { "taxId": "900.000.000-1", "name": "LOQZEN POS" },   // nuevo (StoreConfiguration)
  "payload": { ... }               // contrato por evento, §3.2
}
```

### 3.2 Catálogo de eventos (payloads ya definidos en `SyncEventTypes`, `Core.Application/DTOs/TerminalDto.cs`)

| Evento | Cuándo | Qué hace el ERP con él |
|---|---|---|
| `OrderCompleted` | Venta cobrada (venta rápida o comanda), con `order.items[]`, `order.payments[]` (método, monto, `reference` = aprobación del datáfono, `terminalPaymentId`), impuestos por línea, devoluciones en carrito (cantidad negativa) | Factura de venta / documento POS; asiento de caja o bancos según el medio de pago |
| `CashShiftOpened` / `CashShiftClosed` | Apertura y cierre de turno (esperado, contado, diferencia, fondo que queda, `countLines[]`, quién cerró, aprobación de gerente) | Cuadre de caja; asiento de sobrante/faltante |
| `CashMovementRecorded` | Entrada, salida, gasto, retiro a bóveda, propinas (motivo, referencia, gerente) | Caja menor / gastos |
| `BillPaymentRecorded` / `BillPaymentVoided` | Recaudo de servicios (convenio, referencia del recibo, valor, comisión, medio, `paymentReference`) | Cuenta por pagar al convenio + ingreso por comisión |
| `KitchenOrderCreated` / `KitchenItemCancelled` | Operativos de cocina | Normalmente se **filtran** (`Sync:Events`) |
| `ComandaClosedRemotely` | Interno entre cajas | Siempre filtrado hacia el ERP |
| `ProductChanged` (nuevo, fase B) | Alta/edición de producto o precio en Maestros | Mantener el catálogo del ERP cuando el POS es la fuente |
| `CustomerChanged` (nuevo, fase B) | Alta/edición de cliente | Idem |

Reglas de payload: camelCase, enums como texto (`"Cash"`, `"Card"`), montos como números (COP sin decimales en la práctica pero el tipo es decimal), fechas ISO-8601 UTC, ids Guid. **Nunca se renombra ni se quita un campo**; lo nuevo se agrega.

### 3.3 Semántica de entrega

| Respuesta del ERP | Qué hace el POS |
|---|---|
| `2xx` | Mensaje `Synced`. Si el ERP lo rechazó por negocio (cliente sin NIT, etc.) debe responder **200** con `{ "accepted": false, "reason": "..." }`: el POS lo marca `Synced` y guarda `reason` en `LastError` para que se vea en Caja. Así un error de datos no bloquea la cola. |
| `409` o `422` | Permanente: `Failed` de inmediato, sin reintentos (nuevo; hoy todo no-2xx reintenta). |
| `401`/`403` | Permanente: `Failed` y alerta roja en el chip ("ERP: clave rechazada"). |
| `408`, `429`, `5xx`, timeout, red | Transitorio: reintento con espera `min(2^n, 300)` s ✅; tras `MaxRetries` → `Failed`. |

- **Orden:** un solo despachador, del más antiguo al más nuevo ✅. Nuevo `Sync:StrictOrdering` (por defecto `true` para ERP): al primer fallo transitorio se detiene la pasada, para que el ERP nunca reciba el cierre de turno antes que sus ventas. Con `false` se salta al siguiente (comportamiento actual).
- **Reenvío:** "Reintentar fallidos" ✅ y nuevo **"Reenviar rango"** (fecha inicio/fin, tipo de evento) desde Caja → vuelve a `Pending` con `RetryCount = 0`; el ERP deduplica por `Idempotency-Key`.
- **Lote opcional:** `Sync:BatchMode = true` envía `POST {WebhookUrl}` con un arreglo de sobres (≤ `BatchSize`) y espera un arreglo de resultados `[{ "id", "accepted", "reason" }]`. Útil para ERPs con límite de peticiones.
- **Filtro:** `Sync:Events` = lista de eventos a enviar (`null` = todos menos los de cocina y los internos entre cajas).

### 3.4 Seguridad

- TLS cuando el ERP está fuera de la LAN (`https://`). `Sync:ApiKey` como Bearer ✅.
- **Firma HMAC-SHA256** del cuerpo con `Sync:SigningSecret` en `X-Loqzen-Signature`; el ERP la verifica antes de procesar. Rotación: el POS acepta dos secretos durante la transición (`SigningSecret`, `SigningSecretPrevious`) y firma con el primero.
- Solo datos necesarios: nombre y documento del cliente si existen; nunca PIN ni hashes.

## 4. Hooks de entrada (ERP → POS)

Mismo sobre, en sentido contrario, aplicado por la **principal** con idempotencia por `id` (misma tabla `SyncInboxReceipts` que ya usan las satélites, con `terminalCode = "ERP"`).

| Evento | Payload | Efecto en el POS |
|---|---|---|
| `CatalogUpsert` | `{ products[], categories[], taxes[] }` (mismo formato que `CatalogSnapshotDto`, parcial) | Upsert por id o por SKU; las satélites lo reciben en su siguiente snapshot ✅ |
| `PriceListUpdate` | `{ items: [{ sku, price, validFrom }] }` | Precio nuevo (programado si `validFrom` futuro) |
| `StockAdjust` | `{ items: [{ sku, quantity, reason }] }` | Ajuste de inventario |
| `CustomerUpsert` | `{ customers[] }` | Clientes |
| `InvoiceAccepted` | `{ orderId, fiscalNumber, cufe, qrUrl, acceptedAt }` | Guarda el número fiscal / CUFE DIAN en la orden y permite **reimprimir la factura con QR** (nuevo: `Order.FiscalNumber`, `Order.FiscalCufe`, `Order.FiscalQrUrl`) |
| `InvoiceRejected` | `{ orderId, reason }` | Marca la orden y avisa en Caja ("3 facturas rechazadas por el ERP") |
| `Command` | `{ name: "reprint" \| "pullCatalog" \| "closeDay", args }` | Acciones remotas acotadas |

Transportes:
- **Push:** `POST /api/hooks/inbox` en la principal, `Authorization: Bearer <Sync:InboundApiKey>` + firma HMAC con `Sync:InboundSigningSecret`. Responde `{ outcome: Applied|Duplicate|Ignored|Rejected, error }` como el inbox de satélites ✅.
- **Polling:** `Sync:InboundPollUrl` + `Sync:InboundPollSeconds` (60). `GET ...?since=<cursor>` → `{ items: [sobres], cursor }`; el POS aplica en orden y guarda el cursor en `Terminals.LastSyncAt`/una fila de estado. Reintenta igual que la salida.

## 5. Integración por ERP y por plataforma de automatización

El POS emite **un solo formato** (§3). Lo que cambia por ERP es el **adaptador**: quién recibe el webhook, cómo se autentica contra el ERP y a qué documento traduce cada evento. El adaptador puede ser (a) una plataforma de automatización (§5.2), (b) un microservicio propio o (c) un `IOutboxSink` dentro del POS (fase C). Recomendación: empezar por (a) o (b); meter código de un ERP en el POS solo cuando el volumen o la latencia lo exijan.

### 5.1 ERP por ERP

Leyenda de columnas: **Transporte** = cómo se le habla al ERP · **FE DIAN** = quién emite la factura electrónica · **Catálogo** = quién es la fuente de productos/precios.

| ERP | Transporte y autenticación | `OrderCompleted` → | `CashShiftClosed` / movimientos → | Recaudos → | FE DIAN | Catálogo | Notas |
|---|---|---|---|---|---|---|---|
| **Siigo** (nube) | API REST pública (`api.siigo.com`), token por usuario + `Partner-Id` | Factura de venta (tipo documento POS/FV), cliente genérico "Consumidor final" si no hay NIT, ítems por código, impuestos por línea, forma de pago según `payments[].method` | Comprobante de ingreso/egreso por la diferencia; gastos y retiros como egresos de caja | Recibo de caja contra la cuenta del convenio + ingreso por comisión | **El ERP** (Siigo emite y devuelve CUFE) → hook de entrada `InvoiceAccepted` | ERP → POS (`CatalogUpsert`) o POS → ERP (`ProductChanged`); elegir uno | Límite de peticiones por minuto: activar `BatchMode` o cola en el adaptador |
| **Alegra** (nube) | API REST (`api.alegra.com`), usuario + token (Basic) | Factura de venta / factura POS; contactos por documento; ítems por referencia | Pagos y gastos (recibos de caja / egresos) | Recibo de caja + ítem de servicio "Comisión recaudo" | **El ERP** → `InvoiceAccepted` con CUFE y QR | Normalmente ERP → POS | API sencilla; buena para Zapier/n8n sin código |
| **SAP Business One** | Service Layer (OData/REST) con sesión por login; o DI API en servidor Windows | `Invoices` (A/R Invoice) con `DocumentLines`; cliente `BusinessPartners` genérico; `IncomingPayments` por medio de pago | `JournalEntries` para sobrante/faltante; `VendorPayments`/`JournalEntries` para gastos | `IncomingPayments` contra cuenta del convenio | Según add-on de FE del partner (el adaptador devuelve `InvoiceAccepted` cuando el add-on confirma) | ERP → POS (`Items`, `PriceLists` → `CatalogUpsert`) | Usar almacén y lista de precios por tienda; `terminalCode` como `U_` campo de usuario en la factura |
| **World Office** | API REST del proveedor (según versión/licencia) o importación de documentos por archivo; confirmar con el partner | Documento de venta POS/FV | Comprobante de ingreso/egreso | Recibo de caja | Según licencia de FE de World Office | Normalmente ERP → POS | Si no hay API en la licencia, el adaptador genera el archivo de importación por lote nocturno |
| **CG Uno** (Siesa, Contabilidad Gerencial) | Interfaz contable: importación de movimientos (archivo plano/Excel con estructura del proveedor) o conector del partner; no hay API REST pública estándar | Asiento contable de la venta (ingreso, IVA, caja/bancos) por lote diario | Asientos de cierre y gastos | Asientos contra cuentas por pagar del convenio | **No** (CG Uno es contable): la FE la hace un proveedor tecnológico aparte o el POS más adelante | Sin catálogo: el POS es la fuente | El adaptador acumula el día y genera **un archivo por turno** desde `CashShiftClosed` |
| **Siesa Enterprise / Siesa Business** | Web services / APIs de integración de Siesa (según módulo contratado) o interfaces por archivo | Pedido/factura de venta en el módulo comercial, o solo contable como CG Uno | Comprobantes de caja | Recibos de caja | Según módulo de FE contratado | ERP → POS cuando Siesa administra ítems | Confirmar con Siesa el módulo de integración disponible antes de diseñar el mapeo fino |
| **Serpi** | API o importación según versión; confirmar con el proveedor | Documento de venta | Comprobantes | Recibos | Según proveedor | A convenir | Mismo adaptador genérico; solo cambia el mapeo |

Patrón común para todos: el adaptador **(1)** verifica la firma, **(2)** responde `200` rápido y encola, **(3)** traduce y llama al ERP, **(4)** si el ERP devuelve número fiscal/CUFE manda `InvoiceAccepted` al POS (push o cola de polling), **(5)** si el ERP rechaza por datos manda `InvoiceRejected` con el motivo (se ve en Caja). Para ERPs **solo por archivo** (CG Uno, algunas licencias de World Office/Siesa) el adaptador acumula y genera el archivo al recibir `CashShiftClosed`; el POS no cambia.

### 5.2 Plataformas de automatización: Zapier, n8n, Make y webhook propio

Son la forma más rápida de tener el adaptador sin programar un servicio. Reciben el webhook genérico del POS y lo convierten en llamadas al ERP (o a Google Sheets, correo, WhatsApp, contabilidad en la nube).

| Plataforma | Cómo se conecta | Verificar la firma | Responder `accepted:false` | Cuándo usarla |
|---|---|---|---|---|
| **n8n** (propio o nube) | Nodo **Webhook** (POST) → URL en `Sync:WebhookUrl`; `Header Auth` con `Sync:ApiKey` | Nodo **Crypto** (HMAC-SHA256 del cuerpo crudo con `Sync:SigningSecret`) + nodo **IF** comparando con `X-Loqzen-Signature` | Sí: nodo **Respond to Webhook** con `{ "accepted": false, "reason": "…" }` cuando el ERP rechaza | **Recomendada**: se puede instalar en el mismo servidor de la tienda o en la nube, soporta colas, reintentos y transformaciones JSON complejas |
| **Zapier** | Trigger **Webhooks by Zapier → Catch Raw Hook** | Paso **Code by Zapier** (JS/Python) calculando el HMAC sobre el cuerpo crudo | No en tiempo real (Zapier siempre responde `200` antes de ejecutar): los rechazos del ERP deben volver por **polling** (`Sync:InboundPollUrl`) o por un segundo Zap que haga `POST /api/hooks/inbox` | Integraciones simples y pocos eventos por día (costo por tarea) |
| **Make** (antes Integromat) | Módulo **Webhooks → Custom webhook** | Módulo **Crypto** (HMAC) + filtro | Sí: módulo **Webhook response** | Alternativa a Zapier con mejor precio por volumen |
| **Webhook propio** (Node/.NET/Python) | Cualquier endpoint HTTPS | Librería HMAC estándar | Sí | Cuando hay reglas de negocio propias o un ERP solo por archivo |

Flujo tipo en n8n (sirve de plantilla para los demás):

```
Webhook (POST /loqzen) ─▶ Crypto (HMAC) ─▶ IF firma ok ─▶ Switch por eventType
   │                                                       ├─ OrderCompleted      ─▶ HTTP Request (ERP: crear factura) ─▶ HTTP Request (POS /api/hooks/inbox: InvoiceAccepted)
   │                                                       ├─ CashShiftClosed     ─▶ HTTP Request (ERP: comprobante) / Google Sheets (cuadre del día)
   │                                                       ├─ BillPaymentRecorded ─▶ HTTP Request (ERP: recibo de caja)
   │                                                       └─ otros               ─▶ (ignorar)
   └─ firma mala ─▶ Respond 401
Respond to Webhook: { "accepted": true } | { "accepted": false, "reason": "<error del ERP>" }
```

Consejos: usar el `id` del sobre como clave de idempotencia también en la plataforma (n8n: nodo *Remove Duplicates* por `id`; Zapier: *Storage*); loggear cada sobre en una hoja/tabla antes de llamar al ERP para poder reprocesar; nunca ejecutar lógica del ERP **antes** de responder `200` si la plataforma tiene timeout corto.

### 5.3 Checklist para conectar un ERP nuevo

1. Elegir transporte (API, archivo, plataforma de automatización) y quién emite la FE DIAN.
2. Definir el mapeo de los 5 eventos de negocio (`OrderCompleted`, `CashShiftClosed`, `CashMovementRecorded`, `BillPaymentRecorded`, `BillPaymentVoided`) a documentos del ERP; decidir cliente genérico y cuentas contables.
3. Decidir la dirección del catálogo (`CatalogUpsert` hacia el POS o `ProductChanged` hacia el ERP).
4. Configurar `Sync` en la principal (§6) y probar contra el emulador `delivery-webhook-mock` con firma.
5. Probar los tres caminos: aceptado, `accepted:false` (datos), `5xx` (reintento) y verificar en Caja que se ven los rechazos.
6. Si hay FE: verificar que `InvoiceAccepted` llega y que la tirilla reimprime con CUFE/QR.

## 6. Configuración (sección `Sync`)

```json
"Sync": {
  "Enabled": true,
  "WebhookUrl": "https://erp.midominio.com/loqzen/webhook",
  "ApiKey": "…",
  "SigningSecret": "…",            // nuevo
  "SigningSecretPrevious": null,   // nuevo
  "Events": null,                  // nuevo: ["OrderCompleted","CashShiftClosed",...]
  "StrictOrdering": true,          // nuevo
  "BatchMode": false,              // nuevo
  "IntervalSeconds": 15, "BatchSize": 50, "MaxRetries": 10, "TimeoutSeconds": 10,
  "InboundApiKey": null,           // nuevo (push ERP → POS)
  "InboundSigningSecret": null,    // nuevo
  "InboundPollUrl": null,          // nuevo (polling ERP → POS)
  "InboundPollSeconds": 60         // nuevo
}
```

En una **satélite** la sección `Sync` se ignora (apunta a la principal) ✅; solo la principal lleva estos valores.

## 7. Operación y monitoreo

- Caja → tarjeta "Sincronización": pendientes / fallidos / último envío ✅, último error, **últimos 20 rechazos del ERP** con su `reason`, botones "Reintentar fallidos" ✅ y "Reenviar rango" (nuevo).
- Chip 🌐/🖥️: rojo si el ERP rechaza la clave, ámbar si hay > N pendientes por más de X minutos (`Sync:AlertAfterMinutes`, 30).
- `/health` expone `sync: { pending, failed, lastSentAt, lastError }` para que el ERP o un monitor lo consulten.
- Emulador Docker `delivery-webhook-mock` ✅ (`WEBHOOK_FAIL_RATE`, `WEBHOOK_API_KEY`) + nuevo: verificación de firma (`WEBHOOK_SIGNING_SECRET`), respuesta `accepted:false` para montos terminados en 99, endpoint de polling de entrada con eventos de ejemplo (`InvoiceAccepted` con CUFE falso, `PriceListUpdate`).
- Tests: firma HMAC, 4xx permanente vs 5xx transitorio, orden estricto, reenvío por rango, inbox de ERP idempotente, `InvoiceAccepted` guarda el CUFE y la tirilla imprime el QR.

## 8. Fases de implementación

| Fase | Alcance | Cambios |
|---|---|---|
| **A — salida robusta** (1 sesión) | Firma HMAC + headers, `schemaVersion` + `store`, filtro `Events`, 4xx permanente, `StrictOrdering`, "Reenviar rango", `/health.sync`, emulador con firma y `accepted:false` | `HttpOutboxSink`, `OutboxDispatcher`, `SyncSettings`, `OutboxSyncWorker`, Caja, `docker/emulator/webhook` |
| **B — entrada** (1 sesión) | `POST /api/hooks/inbox` + polling, `CatalogUpsert`, `PriceListUpdate`, `StockAdjust`, `CustomerUpsert`, `InvoiceAccepted/Rejected` (número fiscal + CUFE + QR en la tirilla), `ProductChanged`/`CustomerChanged` de salida | `IErpInboxService` (reusa `SyncInboxService`), `Order.Fiscal*` + migración, `ReceiptFormatter` QR, endpoints, worker de polling |
| **C — adaptadores** (opcional) | `IOutboxSink` por ERP (Siigo, Alegra, SAP B1) elegido por `Sync:Provider`, con mapeo y autenticación propios | `Infrastructure.Data/Outbox/Providers/*` |

**Decisiones abiertas para el usuario:** (1) ¿qué ERP se conecta primero? (define si la fase C vale la pena o basta un adaptador externo); (2) ¿la facturación electrónica DIAN la hace el ERP o un proveedor tecnológico aparte? (define quién devuelve el CUFE en `InvoiceAccepted`); (3) ¿el catálogo lo manda el ERP o lo administra el POS? (define la dirección de `CatalogUpsert`/`ProductChanged`).
