from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import os

PORT = int(os.environ.get("PORT", 5000))

class DeliveryWebhookHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        content_length = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(content_length)
        
        print("\n==============================================", flush=True)
        print(f">> [DELIVERY WEBHOOK RECEIVED on {self.path}]:", flush=True)
        try:
            payload = json.loads(body.decode('utf-8'))
            print(json.dumps(payload, indent=2), flush=True)
        except Exception:
            print(body.decode('utf-8', errors='ignore'), flush=True)
        print("==============================================\n", flush=True)

        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        response = {"status": "success", "message": "Order forwarded to Loqzen POS"}
        self.wfile.write(json.dumps(response).encode('utf-8'))

    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(b'{"status":"healthy","service":"DeliveryWebhookMock"}')

if __name__ == '__main__':
    server = HTTPServer(('0.0.0.0', PORT), DeliveryWebhookHandler)
    print(f"[*] Delivery Webhook Mock API listening on port {PORT}...", flush=True)
    server.serve_forever()
