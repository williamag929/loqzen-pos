# Especificación Técnica: Módulo Adicional KDS (Kitchen Display System) y Comanderas Móviles

## 1. Visión General y Arquitectura de Red Local

Este documento define la arquitectura técnica para la Fase Adicional de Restaurante, la cual integra comanderas móviles para meseros y pantallas de cocina (KDS) funcionando de manera 100% Offline / Local sobre la red LAN del establecimiento.
### Principio de Funcionamiento:

Nodo Master (POS Local / Kestrel Embebido): La aplicación POS principal (Avalonia UI en .NET 8/9) expone un servidor HTTP/WebSocket interno mediante Kestrel.
Clientes Ligeros (KDS / Comanderas): Las tablets de los meseros y las pantallas de cocina se descubren e interconectan mediante mDNS / Zeroconf o IP fija en la red local.

```
  [ Tablet Mesero (Comandera) ] --(HTTP REST / WebSocket)--> [ POS Master (Kestrel + SQLite) ]
                                                                       |
  [ Pantalla KDS (Cocina) ]    <------(WebSocket Push)-----------------+
```

## 2. Flujo de Datos y Protocolos de Comunicación

### A. Envío de Comandas (Mesero -> POS Master)

Petición HTTP POST: La tablet del mesero envía un payload JSON con los artículos solicitados, mesa, número de comensales y notas especiales de preparación (ej. "sin cebolla", "término medio").
Persistencia Local: El POS Master registra la comanda en SQLite (Orders, OrderItems) con estado PENDING_KITCHEN.
Respuesta Rápida: El servidor confirma la recepción a la comandería en < 50ms.
### B. Notificación en Tiempo Real (POS Master -> KDS Cocina)

Push por WebSocket / SignalR Local: El servidor Kestrel emite un evento KitchenOrderCreated hacia las pantallas KDS suscritas por estación (ej. Barra, Cocina Caliente, Fríos).
Alertas Sonoras y Visuales: La pantalla KDS recibe la comanda, emite un tono de notificación y dibuja la tarjeta de comanda con código de colores según el tiempo transcurrido (Verde: < 5 min, Amarillo: 5-15 min, Rojo: > 15 min).
### C. Cambio de Estado de Preparación (KDS -> POS Master -> Mesero)

Interacción Táctil en KDS: El cocinero toca la tarjeta para cambiar el estado a IN_PREPARATION o READY.
Sincronización Multicasteada: El POS Master distribuye el nuevo estado a todas las comanderas móviles para que el mesero sepa instantáneamente cuando la orden está lista para servir.

## 3. Modelo de Datos para Comandas y KDS (SQLite Local)

### Tabla: KitchenOrders

Id (GUID) - Identificador único de la comanda.
TableNumber (TEXT) - Identificador de la mesa o zona.
WaiterId (GUID) - Identificador del mesero.
Status (INTEGER) - [0: Pendiente, 1: En Preparación, 2: Listo, 3: Entregado, 4: Cancelado].
CreatedAt (DATETIME) - Marca de tiempo de la orden.
Station (TEXT) - Estación de cocina asignada (Ej. "Cocina", "Barra").
### Tabla: KitchenOrderItems

Id (GUID)
KitchenOrderId (GUID) - FK a KitchenOrders.
ProductId (GUID)
ProductName (TEXT)
Quantity (DECIMAL)
Notes (TEXT) - Modificadores o especificaciones del cliente.
IsPrepared (BOOLEAN) - Control individual por ítem.

## 4. Estrategia de Resiliencia y Desconexión (Fallback)

Reconexión Automática: Si la red Wi-Fi local experimenta micro-cortes, las comanderas móviles reintentan el envío utilizando exponencial backoff y guardan la comanda temporalmente en una cola local en la tablet.
Modo Híbrido con Impresión de Resguardo: Si la pantalla KDS pierde conexión por más de 30 segundos, el POS Master redirige automáticamente la impresión de la comanda hacia la impresora térmica de cocina (vía ZPL/ESC-POS) como mecanismo de contingencia.
