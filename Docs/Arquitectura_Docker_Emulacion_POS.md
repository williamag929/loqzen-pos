# Arquitectura de Simulación de Hardware POS con Docker

Este documento define la estructura en Docker Compose para emular por completo el ecosistema de hardware del punto de venta (POS) y sus periféricos (impresora térmica, balanza, cajón monedero, doble pantalla y pasarelas de pago) durante la fase de desarrollo e integración.

## 1. Topología del Contenedor de Emulación

La arquitectura se divide en microservicios independientes que simulan la red local y los puertos físicos del terminal Volcora POS:

```yaml
version: '3.8'

services:
  # 1. Base de datos local SQLite compartida por la app de escritorio (Avalonia UI)
  pos-db-sync:
    image: alpine:latest
    container_name: pos-db-sync
    volumes:
      - ./data:/app/data

  # 2. Servidor TCP simulador de Impresora Térmica Epson / Cajón Monedero (Puerto 9100)
  thermal-printer-emulator:
    build:
      context: ./emulator/printer
    container_name: thermal-printer-emulator
    ports:
      - "9100:9100"
    environment:
      - LOG_LEVEL=debug

  # 3. Emulador de Puerto Serie / Balanza CAS SW-20RS (Vía socat + script Python)
  serial-scale-emulator:
    build:
      context: ./emulator/scale
    container_name: serial-scale-emulator
    devices:
      - "/dev/ptmx:/dev/ptmx"
    environment:
      - SCALE_MODEL=CAS-SW20RS
      - WEIGHT_STEP_SECONDS=3

  # 4. Mock API para Canales de Delivery (Rappi / Uber Eats) y Pasarelas de Pago
  delivery-webhook-mock:
    build:
      context: ./emulator/webhook
    container_name: delivery-webhook-mock
    ports:
      - "5000:5000"
    environment:
      - PORT=5000
      - WEBHOOK_FAIL_RATE=${WEBHOOK_FAIL_RATE:-0}
      - WEBHOOK_API_KEY=${WEBHOOK_API_KEY:-}
```

## 2. Descripción de Componentes Emulados

### A. Impresora Térmica y Cajón Monedero (Puerto 9100)

Tecnología: Un servicio ligero en Node.js o Python que escucha en el puerto TCP 9100.
Funcionamiento: Recibe los bytes en formato ESC/POS enviados por la aplicación Avalonia UI. Si detecta la secuencia de pulso de apertura (0x1B 0x70), registra en la consola del emulador: [CASHOUT] Cajón monedero abierto con éxito. Si recibe texto, simula el corte de papel y guarda una traza del recibo generado.
### B. Balanza de Peso Serie (CAS SW-20RS)

Tecnología: Contenedor Linux con socat y un script de Python enlazado.
Funcionamiento: Crea un puerto serial virtual que emite periódicamente tramas de peso formateadas (ej. ST,GS,+001.25kg\r\n). La aplicación .NET conectada mediante System.IO.Ports lee este flujo exactamente igual que si estuviera conectada al puerto RS232 físico de la terminal Volcora.
### C. Webhook Simulator para Delivery (Rappi / Uber Eats) y sink del Outbox

Tecnología: API ligera en Python (`http.server`, sin dependencias), servicio `delivery-webhook-mock` en el puerto 5000.
Funcionamiento:

- `POST /webhook`: destino de la sincronización Outbox (`HttpOutboxSink`, `Sync.WebhookUrl = http://localhost:5000/webhook`). Recibe `{ id, eventType, aggregateType, aggregateId, occurredAt, payload }`, registra una línea por evento (`[WEBHOOK] <eventType> <aggregateId>`) y guarda en memoria los últimos 100 eventos.
- `GET /webhook` o `GET /events`: devuelve `{ count, events }` con los últimos 100 eventos recibidos (se pierden al reiniciar el contenedor).
- `POST` a cualquier otra ruta: simula la llegada de una orden de delivery (imprime el JSON y responde 200), comportamiento original.
- `GET /`: health check.

Variables de entorno:

- `WEBHOOK_FAIL_RATE` (0..1, por defecto 0): probabilidad de responder 500 en `POST /webhook` para ejercitar los reintentos del Outbox.
- `WEBHOOK_API_KEY` (vacía por defecto): si se define, `POST /webhook` exige `Authorization: Bearer <clave>` (debe coincidir con `Sync.ApiKey`); sin ella responde 401.

Ejemplo: `WEBHOOK_FAIL_RATE=0.3 docker compose -f docker/docker-compose.yml up -d delivery-webhook-mock`, luego `curl http://localhost:5000/events`.
