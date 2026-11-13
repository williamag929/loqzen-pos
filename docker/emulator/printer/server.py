"""
Thermal Printer Emulator for Loqzen POS
Emulates Epson TM-T20III on TCP port 9100 with ESC/POS command parsing.
"""
import socket
import sys
import os

PORT = int(os.environ.get('PRINTER_PORT', 9100))
LOG_LEVEL = os.environ.get('LOG_LEVEL', 'info').lower()

# ESC/POS Command Constants
ESC = 0x1B
GS = 0x1D
DLE = 0x10

# Known ESC/POS commands for logging
ESCPOS_COMMANDS = {
    (0x1B, 0x40): "ESC @ (Initialize printer)",
    (0x1B, 0x70): "ESC p (Cash drawer kick pulse)",
    (0x1B, 0x64): "ESC d (Print and feed n lines)",
    (0x1B, 0x4A): "ESC J (Print and feed n dots)",
    (0x1B, 0x61): "ESC a (Select justification)",
    (0x1B, 0x45): "ESC E (Emphasis on/off)",
    (0x1B, 0x21): "ESC ! (Select print mode)",
    (0x1B, 0x2D): "ESC - (Underline on/off)",
    (0x1D, 0x21): "GS ! (Select character size)",
    (0x1D, 0x42): "GS B (Reverse print on/off)",
    (0x1D, 0x56): "GS V (Cut paper)",
    (0x1D, 0x6B): "GS k (Print barcode)",
    (0x1D, 0x28, 0x6B): "GS ( k (Print 2D barcode/QR)",
    (0x10, 0x04): "DLE EOT (Real-time status transmission)",
}


def parse_escpos_commands(data: bytes) -> list:
    """Parse and identify ESC/POS commands in the data stream."""
    commands_found = []
    i = 0
    while i < len(data):
        # Check for 3-byte command prefix
        if i + 2 < len(data) and (data[i], data[i+1], data[i+2]) in ESCPOS_COMMANDS:
            commands_found.append(ESCPOS_COMMANDS[(data[i], data[i+1], data[i+2])])
            i += 3
            continue
        # Check for 2-byte command prefix
        if i + 1 < len(data) and (data[i], data[i+1]) in ESCPOS_COMMANDS:
            commands_found.append(ESCPOS_COMMANDS[(data[i], data[i+1])])
            i += 2
            continue
        i += 1
    return commands_found


def handle_client(client: socket.socket, addr: tuple):
    """Handle a single client connection, reading until disconnect."""
    print(f"[+] Connection accepted from {addr}", flush=True)
    buffer = b''
    
    try:
        while True:
            data = client.recv(4096)
            if not data:
                break
            buffer += data
            
            # Check for cash drawer kick pulse (ESC p = 0x1B 0x70 m t1 t2)
            # Full command: 0x1B 0x70 0x00 0x19 0xFA (pin 2, on=25ms, off=250ms)
            if b'\x1bp' in data:
                # Extract parameters if present
                idx = data.find(b'\x1bp')
                params = data[idx+2:idx+5] if idx + 5 <= len(data) else b''
                if len(params) >= 3:
                    pin = params[0]
                    t1 = params[1] * 2  # on-time in ms
                    t2 = params[2] * 2  # off-time in ms
                    print(f">> [CASH DRAWER] ✓ OPENED (pin={pin}, on={t1}ms, off={t2}ms)", flush=True)
                else:
                    print(">> [CASH DRAWER] ✓ OPENED (ESC p detected)", flush=True)
            
            # Check for paper cut command (GS V)
            if b'\x1dV' in data:
                print(">> [PAPER CUT] ✂ Cut command received", flush=True)
            
            # Parse and log ESC/POS commands if debug mode
            if LOG_LEVEL == 'debug':
                commands = parse_escpos_commands(data)
                if commands:
                    print(f">> [ESC/POS COMMANDS]: {', '.join(commands)}", flush=True)
            
            # Print human-readable receipt text
            printable_text = "".join(
                chr(b) if 32 <= b <= 126 or b in (10, 13, 9) else "·" 
                for b in data
            ).strip()
            
            if printable_text:
                print(">> [RECEIPT PRINT TRACE]:", flush=True)
                print("-" * 42, flush=True)
                for line in printable_text.split('\n'):
                    print(f"| {line}", flush=True)
                print("-" * 42, flush=True)
            
            # Log raw hex in debug mode
            if LOG_LEVEL == 'debug':
                hex_dump = ' '.join(f'{b:02X}' for b in data[:64])
                if len(data) > 64:
                    hex_dump += f' ... ({len(data)} bytes total)'
                print(f">> [RAW HEX]: {hex_dump}", flush=True)
                
    except ConnectionResetError:
        pass
    except Exception as e:
        print(f"[-] Error handling client: {e}", flush=True)
    finally:
        client.close()
        print(f"[-] Connection closed from {addr}", flush=True)


def run_printer_server():
    """Main server loop accepting printer connections."""
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(('0.0.0.0', PORT))
    server.listen(5)
    
    print("=" * 50, flush=True)
    print("  LOQZEN POS - Thermal Printer Emulator", flush=True)
    print("  Model: Epson TM-T20III (ESC/POS)", flush=True)
    print(f"  Listening on TCP port {PORT}", flush=True)
    print(f"  Log level: {LOG_LEVEL}", flush=True)
    print("=" * 50, flush=True)

    while True:
        try:
            client, addr = server.accept()
            handle_client(client, addr)
        except KeyboardInterrupt:
            print("\n[*] Shutting down printer emulator...", flush=True)
            break
        except Exception as e:
            print(f"[-] Server error: {e}", flush=True)


if __name__ == '__main__':
    run_printer_server()
