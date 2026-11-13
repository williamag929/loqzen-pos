"""Delivery / Outbox webhook mock.

- POST /webhook   Outbox sync sink (HttpOutboxSink). Body: { id, eventType, aggregateType, aggregateId,
                  occurredAt, payload }. Logs "[WEBHOOK] <eventType> <aggregateId>", keeps the last 100 in memory.
                  WEBHOOK_API_KEY: if set, requires "Authorization: Bearer <key>" (401 otherwise).
                  WEBHOOK_FAIL_RATE: 0..1 probability of answering 500 (exercises Outbox retries).
- GET /webhook, GET /events   Last 100 received events (newest last) as JSON.
- POST <any other path>       Legacy delivery mock: pretty-prints the body, answers 200.
- GET <any other path>        Health check.
"""
from collections import deque
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
import hmac
import json
import os
import random
import threading
from datetime import datetime, timezone

PORT = int(os.environ.get("PORT", 5000))
API_KEY = os.environ.get("WEBHOOK_API_KEY", "").strip()
MAX_EVENTS = 100


def _parse_fail_rate(raw):
    try:
        return min(1.0, max(0.0, float(raw)))
    except (TypeError, ValueError):
        return 0.0


FAIL_RATE = _parse_fail_rate(os.environ.get("WEBHOOK_FAIL_RATE", "0"))

_events = deque(maxlen=MAX_EVENTS)
_lock = threading.Lock()


class DeliveryWebhookHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        # Silence the default per-request access log; we print our own compact lines.
        pass

    def _path(self):
        return self.path.split("?", 1)[0].rstrip("/") or "/"

    def _send_json(self, status, obj):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _read_body(self):
        content_length = int(self.headers.get("Content-Length", 0) or 0)
        return self.rfile.read(content_length) if content_length > 0 else b""

    def do_POST(self):
        body = self._read_body()
        if self._path() == "/webhook":
            self._handle_outbox(body)
        else:
            self._handle_legacy(body)

    def _handle_outbox(self, body):
        if API_KEY:
            expected = f"Bearer {API_KEY}"
            provided = self.headers.get("Authorization", "")
            if not hmac.compare_digest(provided.encode("utf-8"), expected.encode("utf-8")):
                print("[WEBHOOK] 401 missing/invalid bearer token", flush=True)
                self._send_json(401, {"status": "error", "message": "Unauthorized"})
                return

        try:
            event = json.loads(body.decode("utf-8")) if body else None
        except (UnicodeDecodeError, json.JSONDecodeError):
            print("[WEBHOOK] 400 invalid JSON body", flush=True)
            self._send_json(400, {"status": "error", "message": "Invalid JSON"})
            return
        if not isinstance(event, dict):
            print("[WEBHOOK] 400 body is not a JSON object", flush=True)
            self._send_json(400, {"status": "error", "message": "Expected a JSON object"})
            return

        event_type = event.get("eventType", "?")
        aggregate_id = event.get("aggregateId", "?")

        if FAIL_RATE > 0 and random.random() < FAIL_RATE:
            print(f"[WEBHOOK] 500 (simulated) {event_type} {aggregate_id}", flush=True)
            self._send_json(500, {"status": "error", "message": "Simulated failure (WEBHOOK_FAIL_RATE)"})
            return

        event["receivedAt"] = datetime.now(timezone.utc).isoformat()
        with _lock:
            _events.append(event)
        print(f"[WEBHOOK] {event_type} {aggregate_id}", flush=True)
        self._send_json(200, {"status": "success", "id": event.get("id")})

    def _handle_legacy(self, body):
        print("\n==============================================", flush=True)
        print(f">> [DELIVERY WEBHOOK RECEIVED on {self.path}]:", flush=True)
        try:
            payload = json.loads(body.decode("utf-8"))
            print(json.dumps(payload, indent=2), flush=True)
        except Exception:
            print(body.decode("utf-8", errors="ignore"), flush=True)
        print("==============================================\n", flush=True)
        self._send_json(200, {"status": "success", "message": "Order forwarded to Loqzen POS"})

    def do_GET(self):
        if self._path() in ("/webhook", "/events"):
            with _lock:
                events = list(_events)
            self._send_json(200, {"count": len(events), "events": events})
            return
        self._send_json(200, {"status": "healthy", "service": "DeliveryWebhookMock"})


if __name__ == "__main__":
    server = ThreadingHTTPServer(("0.0.0.0", PORT), DeliveryWebhookHandler)
    print(
        f"[*] Delivery/Outbox Webhook Mock listening on port {PORT} "
        f"(auth={'on' if API_KEY else 'off'}, fail_rate={FAIL_RATE})...",
        flush=True,
    )
    server.serve_forever()
