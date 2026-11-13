"""
Scale Emulator for Loqzen POS
Emulates CAS SW-20RS scale via TCP socket (simulating RS-232 over network).

Protocol: CAS SW-20RS sends weight frames continuously or on request.
Frame format: "ST,GS,+001.25kg\r\n" or "ST,US,+001.25kg\r\n" (unstable)

Commands supported:
- Any data received triggers immediate weight response (polling mode)
- Scale also broadcasts weight every WEIGHT_STEP_SECONDS (continuous mode)
"""
import socket
import threading
import time
import os
import random

PORT = int(os.environ.get('SCALE_PORT', 9101))
WEIGHT_STEP_SECONDS = int(os.environ.get('WEIGHT_STEP_SECONDS', 3))
SCALE_MODEL = os.environ.get('SCALE_MODEL', 'CAS-SW20RS')

# Simulated weight values (kg)
WEIGHT_SEQUENCE = [0.000, 0.250, 0.500, 1.000, 1.250, 1.500, 2.000, 2.500, 0.750, 1.820]
current_weight_idx = 0
current_weight = 0.0
weight_lock = threading.Lock()
connected_clients = []
clients_lock = threading.Lock()


def get_weight_frame(stable: bool = True) -> bytes:
    """Generate a CAS SW-20RS weight frame."""
    global current_weight
    with weight_lock:
        status = "GS" if stable else "US"  # Gross Stable / Unstable
        sign = "+" if current_weight >= 0 else "-"
        # Format: ST,GS,+001.25kg\r\n (sign + 6 chars: XXX.XX)
        frame = f"ST,{status},{sign}{abs(current_weight):06.2f}kg\r\n"
        return frame.encode('ascii')


def weight_simulator():
    """Background thread that simulates weight changes."""
    global current_weight_idx, current_weight
    
    while True:
        time.sleep(WEIGHT_STEP_SECONDS)
        with weight_lock:
            current_weight_idx = (current_weight_idx + 1) % len(WEIGHT_SEQUENCE)
            current_weight = WEIGHT_SEQUENCE[current_weight_idx]
            # Add small random fluctuation for realism
            if current_weight > 0:
                current_weight += random.uniform(-0.005, 0.005)
                current_weight = max(0, current_weight)
        
        frame = get_weight_frame(stable=True)
        print(f">> [SCALE] Weight changed: {frame.strip().decode()}", flush=True)
        
        # Broadcast to all connected clients
        broadcast_weight(frame)


def broadcast_weight(frame: bytes):
    """Send weight frame to all connected clients."""
    with clients_lock:
        disconnected = []
        for client in connected_clients:
            try:
                client.send(frame)
            except:
                disconnected.append(client)
        for client in disconnected:
            connected_clients.remove(client)


def handle_client(client: socket.socket, addr: tuple):
    """Handle scale client connection."""
    print(f"[+] Scale client connected from {addr}", flush=True)
    
    with clients_lock:
        connected_clients.append(client)
    
    try:
        # Send initial stable weight
        client.send(get_weight_frame(stable=True))
        
        while True:
            # Wait for any request (scale query)
            data = client.recv(256)
            if not data:
                break
            
            # Any data received = weight request, respond immediately
            request = data.strip().decode('ascii', errors='ignore')
            if request:
                print(f">> [SCALE] Query received: '{request}'", flush=True)
            
            # Simulate brief unstable reading then stable
            client.send(get_weight_frame(stable=False))
            time.sleep(0.1)
            client.send(get_weight_frame(stable=True))
            
    except ConnectionResetError:
        pass
    except Exception as e:
        print(f"[-] Scale client error: {e}", flush=True)
    finally:
        with clients_lock:
            if client in connected_clients:
                connected_clients.remove(client)
        client.close()
        print(f"[-] Scale client disconnected from {addr}", flush=True)


def run_scale_server():
    """Main TCP server for scale emulator."""
    # Start weight simulator thread
    simulator_thread = threading.Thread(target=weight_simulator, daemon=True)
    simulator_thread.start()
    
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(('0.0.0.0', PORT))
    server.listen(5)
    
    print("=" * 50, flush=True)
    print("  LOQZEN POS - Serial Scale Emulator", flush=True)
    print(f"  Model: {SCALE_MODEL}", flush=True)
    print(f"  Listening on TCP port {PORT}", flush=True)
    print(f"  Weight broadcast interval: {WEIGHT_STEP_SECONDS}s", flush=True)
    print("  Frame format: ST,GS,+XXX.XXkg\\r\\n", flush=True)
    print("=" * 50, flush=True)
    
    while True:
        try:
            client, addr = server.accept()
            # Handle each client in a separate thread
            client_thread = threading.Thread(
                target=handle_client, 
                args=(client, addr),
                daemon=True
            )
            client_thread.start()
        except KeyboardInterrupt:
            print("\n[*] Shutting down scale emulator...", flush=True)
            break
        except Exception as e:
            print(f"[-] Server error: {e}", flush=True)


if __name__ == '__main__':
    run_scale_server()
