#!/usr/bin/env python3
"""Probe HDHomeRun discovery endpoints (SSDP + SiliconDust UDP protocol).

Examples:
  python3 tests/Integration/hdhr_discovery_probe.py --base-url http://192.168.1.240:8080
  python3 tests/Integration/hdhr_discovery_probe.py --base-url http://192.168.1.240:8080 --skip-ssdp
"""

from __future__ import annotations

import argparse
import ipaddress
import json
import socket
import struct
import time
import urllib.parse
import urllib.request
from dataclasses import dataclass
from typing import Optional


DISCOVER_REQ_TYPE = 0x0002
DISCOVER_RPY_TYPE = 0x0003

TAG_DEVICE_TYPE = 0x01
TAG_DEVICE_ID = 0x02
TAG_TUNER_COUNT = 0x10
TAG_LINEUP_URL = 0x27
TAG_BASE_URL = 0x2A
TAG_DEVICE_AUTH = 0x2B
TAG_MULTI_TYPE = 0x2D

DEVICE_TYPE_TUNER = 0x00000001
DEVICE_TYPE_WILDCARD = 0xFFFFFFFF
DEVICE_ID_WILDCARD = 0xFFFFFFFF

SSDP_MCAST_ADDR = "239.255.255.250"
SSDP_PORT = 1900
SSDP_ST = "urn:schemas-upnp-org:device:MediaServer:1"

SILICONDUST_DISCOVERY_PORT = 65001
BROADCAST_ADDR = "255.255.255.255"


@dataclass
class ProbeResult:
    name: str
    ok: bool
    message: str


@dataclass
class ExpectedDevice:
    device_id: str
    base_url: str
    lineup_url: str
    device_auth: str


@dataclass
class SiliconDustResponse:
    source: tuple[str, int]
    device_id: Optional[str]
    tuner_count: Optional[int]
    base_url: Optional[str]
    lineup_url: Optional[str]
    device_auth: Optional[str]


def calculate_ethernet_crc(data: bytes) -> int:
    crc = 0xFFFFFFFF
    for byte in data:
        x = (crc ^ byte) & 0xFF
        crc >>= 8
        if x & 0x01:
            crc ^= 0x77073096
        if x & 0x02:
            crc ^= 0xEE0E612C
        if x & 0x04:
            crc ^= 0x076DC419
        if x & 0x08:
            crc ^= 0x0EDB8832
        if x & 0x10:
            crc ^= 0x1DB71064
        if x & 0x20:
            crc ^= 0x3B6E20C8
        if x & 0x40:
            crc ^= 0x76DC4190
        if x & 0x80:
            crc ^= 0xEDB88320
    return crc ^ 0xFFFFFFFF


def encode_varlen(value: int) -> bytes:
    if value <= 127:
        return bytes([value])
    return bytes([(value & 0x7F) | 0x80, (value >> 7) & 0xFF])


def build_tlv(tag: int, value: bytes) -> bytes:
    return bytes([tag]) + encode_varlen(len(value)) + value


def build_discover_request_packet() -> bytes:
    payload = b"".join(
        [
            build_tlv(TAG_DEVICE_TYPE, struct.pack(">I", DEVICE_TYPE_TUNER)),
            build_tlv(TAG_DEVICE_ID, struct.pack(">I", DEVICE_ID_WILDCARD)),
        ]
    )
    frame = struct.pack(">HH", DISCOVER_REQ_TYPE, len(payload)) + payload
    crc = calculate_ethernet_crc(frame)
    return frame + struct.pack("<I", crc)


def decode_varlen(data: bytes, offset: int) -> tuple[Optional[int], int]:
    if offset >= len(data):
        return None, offset
    first = data[offset]
    offset += 1
    if first & 0x80 == 0:
        return first, offset
    if offset >= len(data):
        return None, offset
    second = data[offset]
    offset += 1
    value = (first & 0x7F) | (second << 7)
    return value, offset


def parse_discover_reply(datagram: bytes, source: tuple[str, int]) -> Optional[SiliconDustResponse]:
    if len(datagram) < 8:
        return None

    packet_type, payload_len = struct.unpack(">HH", datagram[:4])
    if packet_type != DISCOVER_RPY_TYPE:
        return None
    if len(datagram) != 4 + payload_len + 4:
        return None

    frame = datagram[: 4 + payload_len]
    expected_crc = struct.unpack("<I", datagram[4 + payload_len : 8 + payload_len])[0]
    if calculate_ethernet_crc(frame) != expected_crc:
        return None

    payload = datagram[4 : 4 + payload_len]
    offset = 0

    device_id = None
    tuner_count = None
    base_url = None
    lineup_url = None
    device_auth = None

    while offset < len(payload):
        tag = payload[offset]
        offset += 1
        length, offset = decode_varlen(payload, offset)
        if length is None or offset + length > len(payload):
            return None
        value = payload[offset : offset + length]
        offset += length

        if tag == TAG_DEVICE_ID and len(value) == 4:
            device_id = f"{struct.unpack('>I', value)[0]:08X}"
        elif tag == TAG_TUNER_COUNT and len(value) == 1:
            tuner_count = value[0]
        elif tag == TAG_BASE_URL:
            base_url = value.decode("utf-8", errors="replace")
        elif tag == TAG_LINEUP_URL:
            lineup_url = value.decode("utf-8", errors="replace")
        elif tag == TAG_DEVICE_AUTH:
            device_auth = value.decode("utf-8", errors="replace")
        elif tag in (TAG_DEVICE_TYPE, TAG_MULTI_TYPE):
            # Not used for matching in this probe.
            pass

    return SiliconDustResponse(
        source=source,
        device_id=device_id,
        tuner_count=tuner_count,
        base_url=base_url,
        lineup_url=lineup_url,
        device_auth=device_auth,
    )


def fetch_expected_device(base_url: str, timeout: float) -> tuple[Optional[ExpectedDevice], Optional[str]]:
    url = urllib.parse.urljoin(base_url.rstrip("/") + "/", "discover.json")
    try:
        with urllib.request.urlopen(url, timeout=timeout) as resp:
            if resp.status != 200:
                return None, f"GET {url} returned {resp.status}"
            payload = json.loads(resp.read().decode("utf-8"))
    except Exception as ex:  # noqa: BLE001
        return None, f"GET {url} failed: {ex}"

    required = ("DeviceID", "BaseURL", "LineupURL", "DeviceAuth")
    missing = [k for k in required if k not in payload]
    if missing:
        return None, f"discover.json missing keys: {', '.join(missing)}"

    return (
        ExpectedDevice(
            device_id=str(payload["DeviceID"]).upper(),
            base_url=str(payload["BaseURL"]).rstrip("/"),
            lineup_url=str(payload["LineupURL"]),
            device_auth=str(payload["DeviceAuth"]),
        ),
        None,
    )


def probe_ssdp(timeout: float, retries: int, expected: Optional[ExpectedDevice]) -> ProbeResult:
    msearch = "\r\n".join(
        [
            "M-SEARCH * HTTP/1.1",
            f"HOST: {SSDP_MCAST_ADDR}:{SSDP_PORT}",
            'MAN: "ssdp:discover"',
            "MX: 2",
            f"ST: {SSDP_ST}",
            "",
            "",
        ]
    ).encode("ascii")

    responses: list[tuple[tuple[str, int], str]] = []

    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        try:
            sock.settimeout(timeout)
            sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 2)
            sock.bind(("0.0.0.0", 0))

            for _ in range(max(1, retries)):
                sock.sendto(msearch, (SSDP_MCAST_ADDR, SSDP_PORT))
                end = time.time() + timeout
                while time.time() < end:
                    remaining = max(0.1, end - time.time())
                    sock.settimeout(remaining)
                    try:
                        data, addr = sock.recvfrom(4096)
                    except socket.timeout:
                        break
                    text = data.decode("utf-8", errors="replace")
                    if not text.startswith("HTTP/1.1 200"):
                        continue
                    responses.append((addr, text))
        finally:
            sock.close()
    except OSError as ex:
        return ProbeResult("SSDP", False, f"socket error: {ex}")

    if not responses:
        return ProbeResult("SSDP", False, "no SSDP responses received")

    if expected is None:
        return ProbeResult("SSDP", True, f"received {len(responses)} response(s)")

    for _, raw in responses:
        headers = parse_http_headers(raw)
        usn = headers.get("usn", "")
        st = headers.get("st", "")
        location = headers.get("location", "")

        if expected.device_id in usn.upper() and st.lower() == SSDP_ST.lower():
            expected_device_xml = f"{expected.base_url}/device.xml"
            if location.rstrip("/") == expected_device_xml.rstrip("/"):
                return ProbeResult("SSDP", True, f"matched expected device {expected.device_id}")

    return ProbeResult("SSDP", False, "responses received but none matched expected device identity")


def parse_http_headers(raw: str) -> dict[str, str]:
    lines = raw.splitlines()
    headers: dict[str, str] = {}
    for line in lines[1:]:
        if ":" not in line:
            continue
        key, value = line.split(":", 1)
        headers[key.strip().lower()] = value.strip()
    return headers


def resolve_target_ip_from_base_url(base_url: str) -> Optional[str]:
    parsed = urllib.parse.urlparse(base_url)
    host = parsed.hostname
    if not host:
        return None
    try:
        # Prefer literal IP if already provided.
        ipaddress.ip_address(host)
        return host
    except ValueError:
        try:
            return socket.gethostbyname(host)
        except OSError:
            return None


def probe_silicondust(
    timeout: float,
    retries: int,
    expected: Optional[ExpectedDevice],
    target_ip: Optional[str],
) -> ProbeResult:
    packet = build_discover_request_packet()

    targets: list[tuple[str, int]] = [(BROADCAST_ADDR, SILICONDUST_DISCOVERY_PORT)]
    if target_ip:
        directed = (target_ip, SILICONDUST_DISCOVERY_PORT)
        if directed not in targets:
            targets.append(directed)

    responses: list[SiliconDustResponse] = []
    seen: set[tuple[str, int, Optional[str]]] = set()

    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            sock.bind(("0.0.0.0", 0))

            for _ in range(max(1, retries)):
                for target in targets:
                    sock.sendto(packet, target)

                end = time.time() + timeout
                while time.time() < end:
                    remaining = max(0.1, end - time.time())
                    sock.settimeout(remaining)
                    try:
                        datagram, addr = sock.recvfrom(4096)
                    except socket.timeout:
                        break
                    parsed = parse_discover_reply(datagram, addr)
                    if parsed is None:
                        continue
                    key = (addr[0], addr[1], parsed.device_id)
                    if key in seen:
                        continue
                    seen.add(key)
                    responses.append(parsed)
        finally:
            sock.close()
    except OSError as ex:
        return ProbeResult("SiliconDust UDP", False, f"socket error: {ex}")

    if not responses:
        return ProbeResult("SiliconDust UDP", False, "no discovery replies received")

    if expected is None:
        return ProbeResult(
            "SiliconDust UDP",
            True,
            f"received {len(responses)} response(s): {', '.join(r.device_id or 'unknown' for r in responses)}",
        )

    for response in responses:
        if not response.device_id:
            continue
        if response.device_id.upper() != expected.device_id.upper():
            continue
        if response.base_url and response.base_url.rstrip("/") != expected.base_url.rstrip("/"):
            continue
        if response.lineup_url and response.lineup_url != expected.lineup_url:
            continue
        if response.device_auth and response.device_auth != expected.device_auth:
            continue
        return ProbeResult("SiliconDust UDP", True, f"matched expected device {expected.device_id}")

    return ProbeResult("SiliconDust UDP", False, "replies received but none matched expected device identity")


def print_result(result: ProbeResult) -> None:
    status = "PASS" if result.ok else "FAIL"
    print(f"[{status}] {result.name}: {result.message}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Probe HDHomeRun discovery protocols")
    parser.add_argument(
        "--base-url",
        default=None,
        help="Base URL of M3Undle (used to pull expected identity from /discover.json)",
    )
    parser.add_argument(
        "--expect-device-id",
        default=None,
        help="Expected DeviceID if --base-url is not used",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=2.0,
        help="Per-attempt listen timeout in seconds",
    )
    parser.add_argument(
        "--retries",
        type=int,
        default=3,
        help="Number of send/listen rounds per protocol",
    )
    parser.add_argument(
        "--target-ip",
        default=None,
        help="Optional directed IPv4 target for SiliconDust probe",
    )
    parser.add_argument(
        "--skip-ssdp",
        action="store_true",
        help="Skip SSDP probe",
    )
    parser.add_argument(
        "--skip-silicondust",
        action="store_true",
        help="Skip SiliconDust UDP 65001 probe",
    )
    args = parser.parse_args()

    expected: Optional[ExpectedDevice] = None
    if args.base_url:
        expected, err = fetch_expected_device(args.base_url, timeout=max(args.timeout, 2.0))
        if err:
            print(f"[FAIL] discover.json check: {err}")
            return 1
        print(f"[PASS] discover.json check: expected DeviceID={expected.device_id} BaseURL={expected.base_url}")
    elif args.expect_device_id:
        expected = ExpectedDevice(
            device_id=args.expect_device_id.upper(),
            base_url="",
            lineup_url="",
            device_auth="",
        )

    target_ip = args.target_ip
    if not target_ip and args.base_url:
        target_ip = resolve_target_ip_from_base_url(args.base_url)

    results: list[ProbeResult] = []
    if not args.skip_ssdp:
        results.append(probe_ssdp(args.timeout, args.retries, expected))
    if not args.skip_silicondust:
        results.append(probe_silicondust(args.timeout, args.retries, expected, target_ip))

    if not results:
        print("[FAIL] No probes selected")
        return 1

    for result in results:
        print_result(result)

    failed = [r for r in results if not r.ok]
    print(f"\nSummary: {len(results) - len(failed)} passed, {len(failed)} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())

