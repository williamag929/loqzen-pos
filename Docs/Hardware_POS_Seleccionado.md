# Configuración de Hardware POS Seleccionado

## 1. Terminal POS Principal

Modelo: Volcora POS Terminal Standard Plus (18.5 pulgadas)
Especificaciones Clave: Procesador Intel N97, pantalla táctil de 18.5", puertos dedicados USB, COM (RS232), LAN y conectividad Wi-Fi/Bluetooth.
Propósito: Estación central táctil para el cajero y despliegue de la interfaz principal en Avalonia UI (.NET).
## 2. Cajón Monedero

Modelo: Volcora Cash Register Drawer (16 pulgadas)
Especificaciones Clave: Construcción metálica de alta resistencia, ranura para medios y cerradura de 3 posiciones.
Conectividad: Conexión mediante cable RJ11/RJ12 conectado directamente al puerto de la impresora térmica para la apertura automática por pulso solenoide (ESC/POS 0x1B 0x70).
## 3. Impresión Térmica

Modelo principal / Comprobantes: Epson TM-T20III (o Epson TM-T88VI para alto tráfico)
Especificaciones Clave: Impresión térmica de alta velocidad, interfaz USB / Ethernet / Serie, con puerto DKD integrado para el control físico del cajón monedero.
## 4. Balanza de Peso

Modelo: CAS SW-20RS POS Interface Scale
Especificaciones Clave: Capacidad comercial certificada (NTEP / Legal for Trade), interfaz de comunicación RS-232 (puerto serial).
Integración: Lectura de tramas de peso en tiempo real mediante la capa de comunicación serie en C# (System.IO.Ports).
## 5. Arquitectura de Conexión de Periféricos

```
  [ Impresora Epson Thermal ] (TCP/IP o USB) <--- App Avalonia UI (.NET)
             |
             +--( Cable RJ11 / RJ12 )---> [ Cajón Monedero Volcora ]
             
  [ Balanza CAS SW-20RS ]    (Cable RS232)  <--- App Avalonia UI (.NET)
  
  [ Volcora POS Terminal ]   (Pantalla 2da) <--- App Avalonia UI (Window Secundaria)
```
