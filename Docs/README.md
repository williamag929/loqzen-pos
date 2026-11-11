# README.md: Proyecto POS Multiplataforma Standalone & KDS (Agente AI Team)

Instrucciones para el Equipo de Agentes IA (Orquestador Multiagente) Este documento define el contexto, arquitectura, estándares de desarrollo y fases del proyecto POS Standalone desarrollado en .NET 8/9 (Avalonia UI) con persistencia local en SQLite y soporte completo de periféricos (reales y emulados vía Docker).

## 1. Visión General del Proyecto

Desarrollar una aplicación de Punto de Venta (POS) y Backoffice local altamente robusta, multiplataforma (Windows, Linux y Android), operando bajo el paradigma Offline-First. El sistema funciona de manera 100% autónoma (Standalone) con su propio módulo de maestros locales, integrando periféricos avanzados y soportando una fase adicional para redes de restaurantes (KDS y comanderas móviles).

## 2. Stack Tecnológico Oficial

Frontend / Desktop Client: Avalonia UI (.NET 8 / C#) con arquitectura MVVM (ReactiveUI o CommunityToolkit.Mvvm).
Persistencia Local: SQLite con Entity Framework Core (EF Core) o Dapper.
Servidor Local (KDS / Comandas): Kestrel embebido en .NET (para la fase de restaurante).
Entorno de Simulación: Docker Compose para emular impresoras térmicas (puerto TCP 9100), balanzas seriales (socat) y webhooks de delivery.

## 3. Estructura de Directorios del Repositorio

```text
/src
  ├── Core.Domain            # Entidades de dominio, Value Objects, UUIDs (Guid)
  ├── Core.Application       # Casos de uso, DTOs, interfaces de servicios (IHardwareService)
  ├── Infrastructure.Data    # Contexto SQLite, repositorios, outbox pattern de sincronización
  ├── Infrastructure.Hardware# Drivers de periféricos (ESC/POS, ZPL, System.IO.Ports)
  ├── Presentation.Desktop   # App Avalonia UI (Vistas, ViewModels, Doble Pantalla)
  └── Presentation.Server    # Servidor Kestrel embebido para KDS y Comanderas Móviles (Fase 2)
/docker
  ├── docker-compose.yml     # Orquestador de emuladores (Impresora, Balanza, Webhook Mock)
  └── emulator/              # Scripts de simulación (Python / Node.js)
/docs                        # Documentación técnica generada en Google Drive
```

## 4. Estándares y Reglas para los Agentes IA

Arquitectura Limpia (Clean Architecture):
Ninguna capa de presentación o infraestructura debe violar las reglas de dependencia. La lógica de negocio vive en Core.Application y Core.Domain.
Identificadores Únicos (UUIDs):
Todas las entidades transaccionales y maestras deben usar Guid como Clave Primaria para evitar colisiones en entornos desconectados (Offline-First).
Manejo de Periféricos por Interfaces:
Nunca acoplar llamadas directas a hardware en las Vistas. Todo periférico (balanza, impresora, lector de códigos) debe implementarse a través de interfaces (IScaleService, IPrinterService, etc.) para permitir el uso tanto de hardware real como de los emuladores Docker.
Resiliencia y Modo Desconectado:
Ante la pérdida de red local o internet, las operaciones locales en SQLite no deben bloquearse jamás. Utilizar patrones de cola (Outbox Pattern) para cualquier sincronización futura opcional.

## 5. Hoja de Ruta de Desarrollo (Roadmap Agentico)

Fase 1: Core POS Standalone & Base de Datos Local

Estructura de solución Clean Architecture en .NET 8/9.
Configuración de SQLite y repositorios locales con soporte para gestión de artículos, precios, impuestos y clientes.
Interfaz base en Avalonia UI optimizada para pantallas táctiles (Flujo de venta, cobros mixtos, apertura y cierre de caja).

Fase 2: Drivers de Periféricos y Entorno Docker

Integración de lectura de códigos de barras (USB HID).
Conexión serial para la balanza (CAS SW-20RS) mediante System.IO.Ports.
Generación y envío de comandos ESC/POS (impresora Epson) y ZPL (etiquetas de cocina) con soporte para apertura de cajón monedero.
Configuración del entorno de emulación con Docker Compose.

Fase 3: Doble Pantalla y Módulo KDS / Comandas (Fase Adicional)

Implementación de la ventana secundaria para visualización del cliente y códigos QR de pago.

## 6. Estructura y Roles del Equipo de Agentes IA (Orquestación en Antigravity)

UI/UX & Frontend Architect Agent:
Responsabilidad: Diseño de componentes táctiles, diseño de layouts en Avalonia UI (XAML) adaptados a pantallas de 15.6" y 18.5" (targets táctiles >= 48x48px).
Patrón: Implementación estricta de MVVM (CommunityToolkit.Mvvm / ReactiveUI), separación de View/ViewModel y comandos asíncronos.
Entregables: Vistas principales (CheckoutView.axaml), modales de cobro numéricos y gestión de la ventana secundaria (CustomerDisplayWindow.axaml).
Hardware & Infrastructure Agent:
Responsabilidad: Capa de abstracción de periféricos (IHardwareService).
Entregables: Adaptador ESC/POS y ZPL (IPrinterService), integración serial System.IO.Ports para balanza CAS SW-20RS (IScaleService), escucha global USB HID para escáner y scripts/docker-compose del entorno de prueba virtualizado.
.NET & Backend Architect Agent:
Responsabilidad: Definición de la Clean Architecture en .NET 8/9, modelo de dominio y persistencia local Offline-First.
Entregables: Contexto de SQLite con EF Core/Dapper, uso estricto de GUIDs (UUIDs) para claves primarias y patrón Outbox para la cola de eventos local.
Networking & KDS Agent (Fase Adicional):
Responsabilidad: Infraestructura de servidor local embebido en .NET (Kestrel).
Entregables: Endpoints REST/WebSockets para comanderas móviles y pantallas de cocina KDS, servicio mDNS/Zeroconf para autodescubrimiento en la red LAN.
Levantamiento del servidor Kestrel embebido para conectar comanderas móviles (Tablets) y pantallas KDS de cocina mediante WebSockets y red local LAN.
