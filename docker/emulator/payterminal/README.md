# Payment terminal (datáfono) emulator

Emulates a semi-integrated card terminal for `JsonTcpPaymentTerminalDriver`
(`src/Infrastructure.Hardware/PaymentTerminals/JsonTcpPaymentTerminalDriver.cs`).
In the POS, create a terminal with provider **Emulador / JSON TCP** and address `localhost:9102`.

## Protocol

JSON lines over TCP: every message is one JSON object terminated by `\n`, one connection per operation.

| Direction | Message |
|-----------|---------|
| POS → terminal | `{"op":"charge","amount":12500,"currency":"COP","reference":"ORD-000123","description":"Mesa 4"}` |
| terminal → POS | `{"event":"progress","stage":"WaitingForCard","message":"Acerque la tarjeta"}` (0..n) |
| terminal → POS | `{"event":"progress","stage":"Processing","message":"Procesando..."}` |
| terminal → POS | `{"event":"result","status":"Approved","auth":"123456","brand":"VISA","last4":"4242","ref":"RRN...","message":"Aprobada","voucher":["...","..."]}` |
| POS → terminal | `{"op":"cancel"}` while waiting: the terminal aborts and answers `{"event":"result","status":"Cancelled"}` |
| POS → terminal | `{"op":"ping"}` → `{"event":"pong"}` |

`status` is one of `Approved | Declined | Error | Cancelled | Timeout`; `stage` is one of
`Connecting | WaitingForCard | Processing | Done`. `voucher` is optional (≤ 32-column lines the POS prints under the receipt).

## Amount rules (last two digits of the amount)

| Amount ends in | Result |
|----------------|--------|
| `99` | `Declined` — "Fondos insuficientes" |
| `98` | Never answers (hung terminal). Still reacts to `cancel`; the POS reports Timeout/Cancelled. |
| `97` | `Error` — "Error de comunicación con el emisor" |
| anything else | `Approved` (unless `TERMINAL_DECLINE_RATE` picks a random decline) |

## Environment

| Variable | Default | Meaning |
|----------|---------|---------|
| `TERMINAL_PORT` | `9102` | Listening port |
| `TERMINAL_DELAY_SECONDS` | `3` | Seconds between `WaitingForCard` and the result (the "card tap") |
| `TERMINAL_DECLINE_RATE` | `0` | 0..1 probability of a random decline for otherwise-approved amounts |

## Try it

```sh
# From docker/: docker compose up -d payment-terminal-emulator   (or locally: python3 server.py)

# Ping
printf '{"op":"ping"}\n' | nc -q 1 localhost 9102

# Approved charge (watch the progress events stream in)
printf '{"op":"charge","amount":12500,"currency":"COP","reference":"ORD-000123"}\n' | nc -q 5 localhost 9102

# Declined
printf '{"op":"charge","amount":10099,"currency":"COP","reference":"ORD-000124"}\n' | nc -q 5 localhost 9102
```

Python one-liner (no `nc`):

```sh
python3 -c 'import socket,json;s=socket.create_connection(("localhost",9102));s.sendall(b"{\"op\":\"charge\",\"amount\":12500,\"currency\":\"COP\",\"reference\":\"ORD-1\"}\n");f=s.makefile();[print(l,end="") for l in f]'
```
