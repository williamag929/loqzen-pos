import socket
import sys

PORT = 9100

def run_printer_server():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(('0.0.0.0', PORT))
    server.listen(5)
    print(f"[*] Thermal Printer Emulator listening on TCP port {PORT}...", flush=True)

    while True:
        client, addr = server.accept()
        print(f"[+] Connection accepted from {addr}", flush=True)
        try:
            data = client.recv(4096)
            if not data:
                continue

            # Check for cash drawer kick pulse (ESC p = 0x1B 0x70)
            if b'\x1bp' in data:
                print(">> [CASHOUT] Cajón monedero abierto con éxito (Pulse 0x1B 0x70 detected)!", flush=True)

            # Printable receipt text
            printable_text = "".join(chr(b) if 32 <= b <= 126 or b in (10, 13) else "." for b in data)
            print(">> [RECEIPT PRINT TRACE]:\n" + printable_text, flush=True)

        except Exception as e:
            print(f"[-] Error: {e}", flush=True)
        finally:
            client.close()

if __name__ == '__main__':
    run_printer_server()
