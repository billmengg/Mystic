"""Print incoming tracker packets, to verify the stream without Unity."""
import json
import socket

s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(("127.0.0.1", 39570))
while True:
    p = json.loads(s.recv(65535))
    if p["valid"]:
        bs = p["bs"]
        print(f"seq={p['seq']} quat={p['head']['quat']} jawOpen={bs['jawOpen']:.2f} "
              f"blinkL={bs['eyeBlinkLeft']:.2f} blinkR={bs['eyeBlinkRight']:.2f}")
    else:
        print(f"seq={p['seq']} no face")
