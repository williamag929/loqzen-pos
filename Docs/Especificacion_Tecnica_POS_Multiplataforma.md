# Especificación Técnica: Módulo POS Multiplataforma (Offline-First)

## 1. Arquitectura y Stack Tecnológico

Cliente y Backoffice POS Standalone: Avalonia UI (.NET 8/9) para asegurar compatibilidad nativa en Windows, Linux y Android. Incluye un módulo local de administración de maestros (artículos, precios, impuestos, clientes y configuraciones básicas) para operación 100% autónoma.
Backend/Sincronización Opcional: API REST / SignalR para conexión desacoplada y totalmente opcional con un ERP central, permitiendo que el POS funcione de forma independiente sin depender del ERP.
Persistencia Local: SQLite con persistencia Offline-First mediante cola de sincronización (Outbox Pattern).
## 2. Requerimientos de Periféricos y Hardware

Lector de Código de Barras: Modo USB HID (emulación de teclado) con captura de eventos globales para evitar pérdida de foco.
Balanzas de Peso: Conexión vía Serie (RS232) / USB usando System.IO.Ports en C# para lectura continua de tramas de peso.
Impresión Térmica:
Tickets / Facturas: Protocolo ESC/POS vía red TCP (Puerto 9100) o USB.
Etiquetas / Comandas de Cocina: Generación de comandos ZPL / TSPL para impresión dual automática.
Doble Pantalla (Cajero / Cliente): Ventana secundaria dedicada en Avalonia UI para mostrar el detalle de venta en tiempo real, totales y códigos QR de pago dinámico.
Medios de Pago: Soporte mixto (Efectivo, Tarjeta, Transferencias, y pasarelas de terceros). Apertura automatizada de cajón monedero mediante pulso ESC/POS (0x1B 0x70).
## 3. Integración con Canales de Delivery

Recepción de órdenes de plataformas externas (Uber Eats, Rappi) mediante un servicio local (POS Delivery Bridge / Daemon) conectado directamente a sus APIs y Webhooks.
Ingreso directo de pedidos al POS local con alertas sonoras, impresión automática de comanda en cocina y descuento de stock directo en la base de datos SQLite local de forma autónoma.
