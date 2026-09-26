"""Smoke test for a running EasyRadioLink 1.1 server (used by CI on Linux).

    python3 smoke-test.py [host] [port] [expected fingerprint]

Checks: TLS handshake (python ssl, no certificate verification - EasyRadioLink pins the server identity instead),
prints the identity fingerprint (SHA-256 of the certificate's SubjectPublicKeyInfo, like the server log and the client
show it) and compares it with the expected one if given, SYNC handshake with a 32 byte UDP key in the reply and the
client's end-to-end public key in the client list, VOICE_KEY forwarding between two clients (each recipient gets only
its own entry), a hello without an end-to-end public key is refused, and the plain text VERSION_MISMATCH answer for an
EasyRadioLink 1.0 client.

The UDP ping is not tested here: every datagram is AES-256-GCM encrypted since 1.1 and the python standard library has
no AES-GCM (nor ECDH). The encrypted UDP path and the end-to-end voice encryption are covered by the unit tests and the
in-process integration test (ServerIntegrationTests). The server only checks the format of public keys and forwards
wrapped voice keys as opaque blobs, so this script can use a fixed public key and random "wrapped" keys.
"""
import base64
import hashlib
import json
import os
import random
import socket
import ssl
import string
import sys

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 5010
expected_fingerprint = sys.argv[3] if len(sys.argv) > 3 else ""

# DER SubjectPublicKeyInfo of a P-256 point (the curve's generator G) - the format every 1.1 client sends
P256_SPKI_PREFIX = bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d030107034200")
P256_G = bytes.fromhex("04"
                       "6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296"
                       "4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5")
E2E_PUBLIC_KEY = base64.b64encode(P256_SPKI_PREFIX + P256_G).decode()
assert len(P256_SPKI_PREFIX + P256_G) == 91

CB19 = 27185000  # Hz, AM


def der_tlv(data, pos):
    """(tag, start of the value, end of the value) of the DER element at pos."""
    tag = data[pos]
    length = data[pos + 1]
    pos += 2
    if length & 0x80:
        count = length & 0x7F
        length = int.from_bytes(data[pos:pos + count], "big")
        pos += count
    return tag, pos, pos + length


def subject_public_key_info(certificate):
    """The DER SubjectPublicKeyInfo of an X.509 certificate (7th field of the TBSCertificate)."""
    _, cert_start, _ = der_tlv(certificate, 0)  # Certificate
    _, tbs_start, tbs_end = der_tlv(certificate, cert_start)  # TBSCertificate
    fields = []
    pos = tbs_start
    while pos < tbs_end:
        tag, _, end = der_tlv(certificate, pos)
        fields.append((tag, pos, end))
        pos = end
    first = 1 if fields[0][0] == 0xA0 else 0  # optional [0] version
    _, spki_start, spki_end = fields[first + 5]  # serial, signature, issuer, validity, subject, SPKI
    return certificate[spki_start:spki_end]


def fingerprint(certificate):
    return ":".join(f"{b:02X}" for b in hashlib.sha256(subject_public_key_info(certificate)).digest())


class Lines:
    """Reads JSON lines from a socket (keeps what arrives after a line for the next call)."""

    def __init__(self, sock):
        self.sock = sock
        self.buffer = b""

    def next(self):
        while b"\n" not in self.buffer:
            chunk = self.sock.recv(65536)
            if not chunk:
                sys.exit(f"server closed the connection (received {self.buffer!r})")
            self.buffer += chunk
        line, self.buffer = self.buffer.split(b"\n", 1)
        return json.loads(line)


def new_guid():
    return "".join(random.choice(string.ascii_letters + string.digits) for _ in range(22))


def hello_for(guid, name, public_key=E2E_PUBLIC_KEY):
    radios = [{"freq": 1, "modulation": 3}] * 11
    radios[1] = {"freq": CB19, "modulation": 0}  # 27.185 MHz AM
    client = {"ClientGuid": guid, "Name": name, "AllowRecord": False, "RadioInfo": {"radios": radios}}
    if public_key:
        client["E2EPublicKey"] = public_key
    return {"Client": client, "MsgType": 2, "Version": "1.1.0", "Product": "EasyRadioLink"}


context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
context.minimum_version = ssl.TLSVersion.TLSv1_2
context.check_hostname = False
context.verify_mode = ssl.CERT_NONE  # self-signed: the fingerprint (pin) is what identifies the server


def connect():
    raw = socket.create_connection((host, port), timeout=10)
    return context.wrap_socket(raw, server_hostname=host)


def join(tls, guid, name):
    """SYNC handshake; returns the line reader once the SYNC reply with the UDP key arrived."""
    lines = Lines(tls)
    tls.sendall((json.dumps(hello_for(guid, name)) + "\n").encode())
    answer = lines.next()
    if answer.get("MsgType") != 2 or answer.get("Product") != "EasyRadioLink":
        sys.exit(f"{name}: unexpected answer: {answer}")
    key = base64.b64decode(answer.get("UdpKey") or "")
    if len(key) != 32 or not isinstance(answer.get("UdpKeyId"), int):
        sys.exit(f"{name}: the SYNC reply has no valid UDP key")
    me = [c for c in answer.get("Clients") or [] if c.get("ClientGuid") == guid]
    if len(me) != 1 or me[0].get("E2EPublicKey") != E2E_PUBLIC_KEY:
        sys.exit(f"{name}: the client list does not announce the end-to-end public key")
    return lines, answer


# --- TLS + SYNC ------------------------------------------------------------------------------------------------------
alice_guid, bob_guid = new_guid(), new_guid()

with connect() as alice:
    print(f"TLS handshake ok: {alice.version()} {alice.cipher()[0]}")
    server_fingerprint = fingerprint(alice.getpeercert(binary_form=True))
    print("Server identity fingerprint (SHA-256):", server_fingerprint)
    if expected_fingerprint:
        normalise = lambda text: text.replace(":", "").replace(" ", "").upper()
        if normalise(expected_fingerprint) != normalise(server_fingerprint):
            sys.exit(f"fingerprint mismatch: expected {expected_fingerprint}")
        print("Fingerprint matches the server log")

    alice_lines, reply = join(alice, alice_guid, "CI-A")
    print("SYNC handshake ok, server protocol", reply.get("Version"),
          "- UDP key received (32 bytes), end-to-end public key announced")

    # --- VOICE_KEY: forwarded to each listed listener, with only its own entry -----------------------------------
    with connect() as bob:
        bob_lines, _ = join(bob, bob_guid, "CI-B")

        wrapped_for_bob = base64.b64encode(os.urandom(48)).decode()
        voice_key = {"SenderGuid": alice_guid, "TxId": base64.b64encode(os.urandom(8)).decode(),
                     "Frequency": CB19, "Modulation": 0,
                     "Keys": {bob_guid: wrapped_for_bob,
                              new_guid(): base64.b64encode(os.urandom(48)).decode()}}  # not connected: dropped
        alice.sendall((json.dumps({"MsgType": 8, "VoiceKey": voice_key, "Version": "1.1.0",
                                   "Product": "EasyRadioLink"}) + "\n").encode())

        while True:
            message = bob_lines.next()
            if message.get("MsgType") == 8:
                break
        forwarded = message.get("VoiceKey") or {}
        if forwarded.get("SenderGuid") != alice_guid or forwarded.get("Keys") != {bob_guid: wrapped_for_bob}:
            sys.exit(f"VOICE_KEY not forwarded as expected: {message}")
        print("VOICE_KEY forwarded: the listener got only its own wrapped key")

# --- a hello without an end-to-end public key is refused -------------------------------------------------------------
with connect() as keyless:
    keyless.sendall((json.dumps(hello_for(new_guid(), "CI-old", public_key=None)) + "\n").encode())
    answer = Lines(keyless).next()
    if answer.get("MsgType") != 6 or answer.get("UdpKey"):
        sys.exit(f"hello without end-to-end key: expected VERSION_MISMATCH, got {answer}")
    print("Hello without an end-to-end public key gets VERSION_MISMATCH")

# --- an EasyRadioLink 1.0 client (plain JSON) gets VERSION_MISMATCH instead of a timeout ----------------------------
old_hello = dict(hello_for(new_guid(), "CI-1.0", public_key=None), Version="1.0.0")
with socket.create_connection((host, port), timeout=10) as plain:
    plain.sendall((json.dumps(old_hello) + "\n").encode())
    answer = Lines(plain).next()
    if answer.get("MsgType") != 6:
        sys.exit(f"1.0 client: expected VERSION_MISMATCH, got {answer}")
    print("1.0 client gets VERSION_MISMATCH (server protocol", answer.get("Version") + ")")
