# PROMPT MAESTRO DE INICIALIZACIÓN: PROYECTO POS STANDALONE & KDS

Usa la documentación de referencia guardada en el workspace (README.md, Especificación Técnica y Arquitectura Docker) como la única fuente de verdad.

## 1. Misión del Orquestador

Construir la Fase 1 del Punto de Venta (POS) Standalone Multiplataforma (.NET 8/9, Avalonia UI, SQLite Offline-First). La aplicación debe ser 100% autónoma, capaz de manejar su propio módulo de maestros locales y preparada para abstracción de hardware y futura red KDS.

## 2. Asignación de Roles y Subagentes

Asigna y coordina los siguientes subagentes para ejecutar tareas en paralelo:

@dotnet-backend-architect:

Crea la estructura de la solución .NET 8/9 siguiendo Clean Architecture:
src/Core.Domain
src/Core.Application
src/Infrastructure.Data
src/Infrastructure.Hardware
src/Presentation.Desktop
Implementa las entidades principales con Guid como Clave Primaria (Product, Category, Order, OrderItem, Customer, Tax).
Configura el contexto de Entity Framework Core para SQLite con migraciones iniciales y el patrón Outbox (OutboxMessage).

@ui-ux-frontend-architect:

Configura el proyecto Avalonia UI en src/Presentation.Desktop con el patrón MVVM (CommunityToolkit.Mvvm).
Crea el layout base de CheckoutView.axaml dividido para pantallas táctiles de 15.6" / 18.5" (targets táctiles >= 48x48px):
Panel izquierdo: Catálogo con filtro de búsqueda rápida y categorías.
Panel derecho: Ticket de venta interactivo con subtotales en tiempo real.
Modal de Cobro: Numpad numérico táctil para cálculo de cambio e ingreso de efectivo.
Diseña la ventana secundaria CustomerDisplayWindow.axaml para el visor del cliente (totales y código QR).

@hardware-infrastructure-agent:

Define las interfaces del dominio en Core.Application:
IPrinterService (comandos ESC/POS y ZPL con pulso de cajón monedero 0x1B 0x70).
IScaleService (lectura serial System.IO.Ports para balanza CAS SW-20RS).
IBarcodeScannerService (captura global USB HID).
Genera el archivo docker/docker-compose.yml para los emuladores locales de impresora (TCP puerto 9100) y balanza serial.

## 3. Reglas Invariables de Código

Standalone: No agregar dependencias obligatorias a servicios externos o ERPs.
Offline-First: Toda transacción se guarda inmediatamente en la base de datos local SQLite.
Inyección de Dependencias: Usar implementaciones Mock o emuladas cuando el entorno sea Development.

## 4. Entregable de la Primera Iteración

Al finalizar la ejecución, compila la solución (dotnet build), verifica que no existan errores de arquitectura y presenta un reporte de artefactos (Artifacts) con la estructura de la solución y los archivos clave generados.

¡Inicia la orquestación ahora!
