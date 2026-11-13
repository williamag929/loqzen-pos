"""Payment terminal (datáfono) emulator for Loqzen POS — JSON lines over TCP, port 9102.

Speaks the protocol of JsonTcpPaymentTerminalDriver (src/Infrastructure.Hardware/PaymentTerminals).
Every message is one JSON object terminated by '\\n'. One connection per operation.

  POS  -> {"op":"charge","amount":12500,"currency":"COP","reference":"ORD-000123","description":"Mesa 4"}
  TERM -> {"event":"progress","stage":"WaitingForCard","message":"Acerque la tarjeta"}
  TERM -> {"event":"progress","stage":"Processing","message":"Procesando..."}
  TERM -> {"event":"result","status":"Approved","auth":"123456","brand":"VISA","last4":"4242",
           "ref":"RRN...","message":"Aprobada","voucher":["...", "..."]}
  POS  -> {"op":"cancel"}   (while waiting: terminal aborts and answers status "Cancelled")
  POS  -> {"op":"ping"}     TERM -> {"event":"pong"}

Amount rules (last two digits of the whole amount), same as the in-process Mock driver:
  ...99 -> Declined ("Fondos insuficientes")      ...98 -> never answers (hung terminal; still honours cancel)
  ...97 -> Error ("Error de comunicacion")        else  -> Approved (or a random decline, see TERMINAL_DECLINE_RATE)

Environment:
  TERMINAL_PORT=9102             listening port
  TERMINAL_DELAY_SECONDS=3       seconds between WaitingForCard and the result (the "card tap")
  TERMINAL_DECLINE_RATE=0        0..1 probability of a random decline for otherwise-approved amounts
"""
import json
import os
import random
import select
import socketserver
import threading
import time
from datetime import datetime

PORT = int(os.environ.get("TERMINAL_PORT", 9102))
DELAY_SECONDS = max(0.0, float(os.environ.get("TERMINAL_DELAY_SECONDS", "3")))
BRANDS = ["VISA", "MASTERCARD", "AMEX"]
VOUCHER_WIDTH = 32


def _parse_rate(raw):
    try:
        return min(1.0, max(0.0, float(raw)))
    except (TypeError, ValueError):
        return 0.0


DECLINE_RATE = _parse_rate(os.environ.get("TERMINAL_DECLINE_RATE", "0"))

_brand_lock = threading.Lock()
_brand_index = -1
_request_counter = 0


def _next_brand():
    global _brand_index
    with _brand_lock:
        _brand_index = (_brand_index + 1) % len(BRANDS)
        return BRANDS[_brand_index]


def _next_request_id():
    global _request_counter
    with _brand_lock:
        _request_counter += 1
        return _request_counter


def _center(text):
    text = text[:VOUCHER_WIDTH]
    return " " * ((VOUCHER_WIDTH - len(text)) // 2) + text


def _pair(label, value):
    value = str(value)[:VOUCHER_WIDTH - len(label) - 1]
    return label + " " * (VOUCHER_WIDTH - len(label) - len(value)) + value


def build_voucher(amount, currency, brand, last4, auth, ref, invoice):
    return [
        _center("LOQZEN POS"),
        _center("DATAFONO EMULADO"),
        "-" * VOUCHER_WIDTH,
        _center("VENTA"),
        _pair("TOTAL", f"{amount:,.0f} {currency}"),
        _pair("TARJETA", f"{brand} ****{last4}"),
        "",
        _center("APROBADA"),
        _pair("AUTORIZACION", auth),
        _pair("REF", ref),
        _pair("FACTURA", invoice or ""),
        _pair("FECHA", datetime.now().strftime("%d/%m/%Y %H:%M")),
        "-" * VOUCHER_WIDTH,
    ]


class TerminalHandler(socketserver.BaseRequestHandler):
    """One POS connection: a single op (charge / ping), then the POS closes the socket."""

    def setup(self):
        self._buffer = b""

    def _send(self, obj):
        self.request.sendall((json.dumps(obj, ensure_ascii=False) + "\n").encode("utf-8"))

    def _readline(self, timeout=None):
        """Next '\n'-terminated line (bytes, without the newline). None on EOF, '' on timeout.
        Hand-rolled instead of makefile() because a buffered file object cannot be reused after a timeout."""
        while b"\n" not in self._buffer:
            ready, _, _ = select.select([self.request], [], [], timeout)
            if not ready:
                return b""
            chunk = self.request.recv(4096)
            if not chunk:
                return None
            self._buffer += chunk
        line, _, self._buffer = self._buffer.partition(b"\n")
        return line

    def _log(self, msg):
        print(f"[{self.client_address[0]}:{self.client_address[1]}] {msg}", flush=True)

    def handle(self):
        self._log("connected")
        try:
            raw = self._readline()
            if raw is None:
                self._log("closed before sending a request")
                return
            try:
                req = json.loads(raw.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                self._log(f"invalid JSON: {raw[:120]!r}")
                self._send({"event": "result", "status": "Error", "message": "Solicitud inválida (JSON)"})
                return

            op = req.get("op") if isinstance(req, dict) else None
            self._log(f"request: {json.dumps(req, ensure_ascii=False)}")
            if op == "ping":
                self._send({"event": "pong"})
            elif op == "charge":
                self._handle_charge(req)
            elif op == "cancel":
                self._log("cancel with no charge in flight (ignored)")
            else:
                self._send({"event": "result", "status": "Error", "message": f"Operación no soportada: {op}"})
        except (BrokenPipeError, ConnectionResetError):
            self._log("connection reset by POS")
        finally:
            self._log("disconnected")

    def _wait_or_cancel(self, seconds):
        """Sleep `seconds` (None = forever) while watching the socket for {"op":"cancel"} or EOF.
        Returns 'cancel', 'eof' or None (time elapsed)."""
        deadline = None if seconds is None else time.monotonic() + seconds
        while True:
            remaining = None if deadline is None else deadline - time.monotonic()
            if remaining is not None and remaining <= 0:
                return None
            raw = self._readline(min(0.25, remaining) if remaining is not None else 0.25)
            if raw is None:
                return "eof"
            if raw == b"":
                continue
            try:
                msg = json.loads(raw.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                continue
            if isinstance(msg, dict) and msg.get("op") == "cancel":
                return "cancel"

    def _handle_charge(self, req):
        try:
            amount = float(req.get("amount", 0))
        except (TypeError, ValueError):
            self._send({"event": "result", "status": "Error", "message": "Monto inválido"})
            return
        currency = str(req.get("currency") or "COP")
        invoice = str(req.get("reference") or "")
        last_two = int(abs(amount)) % 100
        seq = _next_request_id()

        self._send({"event": "progress", "stage": "WaitingForCard", "message": "Acerque o inserte la tarjeta"})

        if last_two == 98:
            self._log(f"#{seq} amount ends in 98: hung terminal, never answering (waiting for cancel/EOF)")
            outcome = self._wait_or_cancel(None)
        else:
            outcome = self._wait_or_cancel(DELAY_SECONDS)

        if outcome == "cancel":
            self._log(f"#{seq} cancelled by POS")
            self._send({"event": "result", "status": "Cancelled", "message": "Cancelada desde el POS"})
            return
        if outcome == "eof":
            self._log(f"#{seq} POS closed the socket while waiting for the card")
            return

        self._send({"event": "progress", "stage": "Processing", "message": "Procesando la transacción..."})
        time.sleep(min(0.5, DELAY_SECONDS))

        if last_two == 99:
            self._log(f"#{seq} DECLINED (amount ends in 99)")
            self._send({"event": "result", "status": "Declined", "message": "Fondos insuficientes"})
            return
        if last_two == 97:
            self._log(f"#{seq} ERROR (amount ends in 97)")
            self._send({"event": "result", "status": "Error", "message": "Error de comunicación con el emisor"})
            return
        if DECLINE_RATE > 0 and random.random() < DECLINE_RATE:
            self._log(f"#{seq} DECLINED (random, rate={DECLINE_RATE})")
            self._send({"event": "result", "status": "Declined", "message": "Rechazada por el emisor"})
            return

        auth = f"{random.randint(0, 999999):06d}"
        brand = _next_brand()
        last4 = f"{random.randint(0, 9999):04d}"
        ref = f"RRN{int(time.time()):010d}{seq:04d}"
        self._log(f"#{seq} APPROVED {amount:,.0f} {currency} auth={auth} {brand} ****{last4} ref={ref}")
        self._send({
            "event": "result",
            "status": "Approved",
            "auth": auth,
            "brand": brand,
            "last4": last4,
            "ref": ref,
            "message": "Aprobada",
            "voucher": build_voucher(amount, currency, brand, last4, auth, ref, invoice),
        })


class ThreadedServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def run():
    with ThreadedServer(("0.0.0.0", PORT), TerminalHandler) as server:
        print("=" * 50, flush=True)
        print("  LOQZEN POS - Payment Terminal Emulator", flush=True)
        print("  Protocol: JSON lines over TCP", flush=True)
        print(f"  Listening on TCP port {PORT}", flush=True)
        print(f"  Card delay: {DELAY_SECONDS}s | Decline rate: {DECLINE_RATE}", flush=True)
        print("=" * 50, flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            print("\n[*] Shutting down payment terminal emulator...", flush=True)


if __name__ == "__main__":
    run()
