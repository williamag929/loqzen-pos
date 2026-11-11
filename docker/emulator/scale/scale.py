import time
import os
import pty
import random

def run_scale():
    master, slave = pty.openpty()
    s_name = os.ttyname(slave)
    print(f"[*] CAS SW-20RS Serial Scale Emulator running.", flush=True)
    print(f"[*] Virtual Serial PTY Slave: {s_name}", flush=True)

    weights = [0.250, 0.500, 1.250, 2.500, 0.750, 1.820]
    idx = 0

    while True:
        weight = weights[idx % len(weights)]
        # CAS SW-20RS format: ST,GS,+001.25kg\r\n
        packet = f"ST,GS,+{weight:06.2f}kg\r\n".encode('ascii')
        os.write(master, packet)
        print(f">> [SCALE EMULATOR EMITTED]: {packet.strip().decode()}", flush=True)
        time.sleep(3)
        idx += 1

if __name__ == '__main__':
    run_scale()
