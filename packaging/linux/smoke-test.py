"""Smoke test for a running EasyRadioLink server: SYNC handshake + UDP ping (used by CI on Linux)."""
import json, random, socket, string, sys, time

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 5010
guid = "".join(random.choice(string.ascii_letters + string.digits) for _ in range(22))
hello = {"Client": {"ClientGuid": guid, "Name": "CI", "AllowRecord": False,
                    "RadioInfo": {"radios": [{"freq": 1, "modulation": 3}] * 11}},
         "MsgType": 2, "Version": "1.0.0", "Product": "EasyRadioLink"}

tcp = socket.create_connection((host, port), timeout=10)
tcp.sendall((json.dumps(hello) + "\n").encode())
reply = b""
while b"\n" not in reply:
    chunk = tcp.recv(65536)
    if not chunk:
        sys.exit("server closed the connection")
    reply += chunk
answer = json.loads(reply.split(b"\n")[0])
if answer.get("MsgType") != 2 or answer.get("Product") != "EasyRadioLink":
    sys.exit(f"unexpected answer: {answer}")
print("TCP handshake ok, server protocol", answer.get("Version"))

udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
udp.settimeout(2)
for attempt in range(5):
    udp.sendto(guid.encode(), (host, port))
    try:
        data, _ = udp.recvfrom(64)
        if data == guid.encode():
            print("UDP ping ok")
            break
    except socket.timeout:
        time.sleep(0.5)
else:
    sys.exit("no UDP ping answer")
tcp.close()
