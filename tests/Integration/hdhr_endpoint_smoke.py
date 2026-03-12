#!/usr/bin/env python3
"""Smoke-test utility for M3Undle HDHomeRun endpoints.

Examples:
  python3 tests/Integration/hdhr_endpoint_smoke.py --base-url http://127.0.0.1:8080
  python3 tests/Integration/hdhr_endpoint_smoke.py --start-server --base-url http://127.0.0.1:5099
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from typing import Optional


REQUIRED_DISCOVER_KEYS = {
    "FriendlyName",
    "Manufacturer",
    "ModelNumber",
    "FirmwareName",
    "FirmwareVersion",
    "DeviceID",
    "DeviceAuth",
    "BaseURL",
    "LineupURL",
    "TunerCount",
}

LINEUP_ENDPOINTS = ("/lineup.json", "/lineup.xml", "/lineup.m3u")


@dataclass
class CheckResult:
    name: str
    ok: bool
    message: str
    status_code: Optional[int] = None
    warning: bool = False


def build_url(base_url: str, path: str) -> str:
    return urllib.parse.urljoin(base_url.rstrip("/") + "/", path.lstrip("/"))


def fetch(
    base_url: str,
    path: str,
    timeout_seconds: float,
    method: str = "GET",
    headers: Optional[dict[str, str]] = None,
) -> tuple[Optional[int], dict[str, str], bytes, Optional[str]]:
    url = build_url(base_url, path)
    req = urllib.request.Request(url, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(req, timeout=timeout_seconds) as resp:
            return resp.status, dict(resp.headers.items()), resp.read(), None
    except urllib.error.HTTPError as ex:
        body = ex.read() if hasattr(ex, "read") else b""
        return ex.code, dict(ex.headers.items()) if ex.headers else {}, body, None
    except Exception as ex:  # noqa: BLE001
        return None, {}, b"", str(ex)


def parse_json(body: bytes) -> tuple[Optional[object], Optional[str]]:
    try:
        return json.loads(body.decode("utf-8")), None
    except Exception as ex:  # noqa: BLE001
        return None, str(ex)


def validate_discover_json(status: Optional[int], body: bytes, err: Optional[str]) -> CheckResult:
    if err:
        return CheckResult("discover.json", False, f"request error: {err}")
    if status != 200:
        return CheckResult("discover.json", False, f"expected 200, got {status}", status_code=status)

    payload, parse_err = parse_json(body)
    if parse_err:
        return CheckResult("discover.json", False, f"invalid JSON: {parse_err}", status_code=status)
    if not isinstance(payload, dict):
        return CheckResult("discover.json", False, "payload is not an object", status_code=status)

    missing = sorted(REQUIRED_DISCOVER_KEYS.difference(payload.keys()))
    if missing:
        return CheckResult("discover.json", False, f"missing keys: {', '.join(missing)}", status_code=status)

    return CheckResult("discover.json", True, "OK", status_code=status)


def validate_lineup_json(status: Optional[int], body: bytes, err: Optional[str]) -> tuple[CheckResult, list[dict[str, object]]]:
    if err:
        return CheckResult("lineup.json", False, f"request error: {err}"), []
    if status != 200:
        return CheckResult("lineup.json", False, f"expected 200, got {status}", status_code=status), []

    payload, parse_err = parse_json(body)
    if parse_err:
        return CheckResult("lineup.json", False, f"invalid JSON: {parse_err}", status_code=status), []
    if not isinstance(payload, list):
        return CheckResult("lineup.json", False, "payload is not an array", status_code=status), []

    for i, item in enumerate(payload):
        if not isinstance(item, dict):
            return CheckResult("lineup.json", False, f"entry {i} is not an object", status_code=status), []
        for key in ("GuideNumber", "GuideName", "URL"):
            if key not in item:
                return CheckResult("lineup.json", False, f"entry {i} missing '{key}'", status_code=status), []

    return CheckResult("lineup.json", True, f"OK ({len(payload)} channels)", status_code=status), payload


def validate_lineup_xml(status: Optional[int], body: bytes, err: Optional[str]) -> CheckResult:
    if err:
        return CheckResult("lineup.xml", False, f"request error: {err}")
    if status != 200:
        return CheckResult("lineup.xml", False, f"expected 200, got {status}", status_code=status)
    try:
        root = ET.fromstring(body.decode("utf-8"))
    except Exception as ex:  # noqa: BLE001
        return CheckResult("lineup.xml", False, f"invalid XML: {ex}", status_code=status)
    if root.tag != "Lineup":
        return CheckResult("lineup.xml", False, f"unexpected root tag '{root.tag}'", status_code=status)
    return CheckResult("lineup.xml", True, "OK", status_code=status)


def validate_lineup_m3u(status: Optional[int], body: bytes, err: Optional[str]) -> CheckResult:
    if err:
        return CheckResult("lineup.m3u", False, f"request error: {err}")
    if status != 200:
        return CheckResult("lineup.m3u", False, f"expected 200, got {status}", status_code=status)
    text = body.decode("utf-8", errors="replace")
    if not text.startswith("#EXTM3U"):
        return CheckResult("lineup.m3u", False, "missing #EXTM3U header", status_code=status)
    return CheckResult("lineup.m3u", True, "OK", status_code=status)


def validate_device_xml(status: Optional[int], body: bytes, err: Optional[str]) -> CheckResult:
    if err:
        return CheckResult("device.xml", False, f"request error: {err}")
    if status != 200:
        return CheckResult("device.xml", False, f"expected 200, got {status}", status_code=status)
    try:
        ET.fromstring(body.decode("utf-8"))
    except Exception as ex:  # noqa: BLE001
        return CheckResult("device.xml", False, f"invalid XML: {ex}", status_code=status)
    return CheckResult("device.xml", True, "OK", status_code=status)


def validate_lineup_status(status: Optional[int], body: bytes, err: Optional[str]) -> CheckResult:
    if err:
        return CheckResult("lineup_status.json", False, f"request error: {err}")
    if status != 200:
        return CheckResult("lineup_status.json", False, f"expected 200, got {status}", status_code=status)
    payload, parse_err = parse_json(body)
    if parse_err:
        return CheckResult("lineup_status.json", False, f"invalid JSON: {parse_err}", status_code=status)
    if not isinstance(payload, dict):
        return CheckResult("lineup_status.json", False, "payload is not an object", status_code=status)
    for key in ("ScanInProgress", "ScanPossible", "Status", "ChannelCount"):
        if key not in payload:
            return CheckResult("lineup_status.json", False, f"missing key '{key}'", status_code=status)
    return CheckResult("lineup_status.json", True, "OK", status_code=status)


def validate_lineup_post(status: Optional[int], body: bytes, err: Optional[str]) -> CheckResult:
    if err:
        return CheckResult("lineup.post", False, f"request error: {err}")
    if status != 200:
        return CheckResult("lineup.post", False, f"expected 200, got {status}", status_code=status)
    text = body.decode("utf-8", errors="replace").strip()
    if text and text.upper() != "OK":
        return CheckResult("lineup.post", False, f"unexpected body: {text!r}", status_code=status)
    return CheckResult("lineup.post", True, "OK", status_code=status)


def wait_for_health(base_url: str, timeout_seconds: float, process: subprocess.Popen[bytes]) -> bool:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if process.poll() is not None:
            return False
        status, _, _, _ = fetch(base_url, "/health", timeout_seconds=2.0)
        if status == 200:
            return True
        time.sleep(0.5)
    return False


def start_server(
    project_path: str,
    base_url: str,
    data_dir: Optional[str],
) -> tuple[subprocess.Popen[bytes], str, bool]:
    env = os.environ.copy()
    env["ASPNETCORE_URLS"] = base_url
    cleanup_data_dir = False
    effective_data_dir = data_dir
    if not effective_data_dir:
        effective_data_dir = tempfile.mkdtemp(prefix="m3undle-hdhr-smoke-")
        cleanup_data_dir = True
    env["M3UNDLE_DATA_DIR"] = effective_data_dir

    proc = subprocess.Popen(
        ["dotnet", "run", "--project", project_path, "--no-launch-profile"],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        env=env,
    )
    return proc, effective_data_dir, cleanup_data_dir


def maybe_allow_lineup_unavailable(path: str, status: Optional[int], allow: bool) -> Optional[CheckResult]:
    if not allow:
        return None
    if path in LINEUP_ENDPOINTS and status == 503:
        return CheckResult(
            name=path.strip("/"),
            ok=True,
            warning=True,
            status_code=status,
            message="lineup unavailable (503) allowed by flag",
        )
    return None


def probe_stream(url: str, timeout_seconds: float) -> CheckResult:
    req = urllib.request.Request(url, method="GET", headers={"Range": "bytes=0-188"})
    try:
        with urllib.request.urlopen(req, timeout=timeout_seconds) as resp:
            chunk = resp.read(512)
            if not chunk:
                return CheckResult("probe-stream", False, "no stream data received", status_code=resp.status)
            return CheckResult("probe-stream", True, f"received {len(chunk)} bytes", status_code=resp.status)
    except urllib.error.HTTPError as ex:
        return CheckResult("probe-stream", False, f"HTTP {ex.code}", status_code=ex.code)
    except Exception as ex:  # noqa: BLE001
        return CheckResult("probe-stream", False, f"request error: {ex}")


def print_result(result: CheckResult) -> None:
    label = "PASS"
    if not result.ok:
        label = "FAIL"
    elif result.warning:
        label = "WARN"
    status = f" (status={result.status_code})" if result.status_code is not None else ""
    print(f"[{label}] {result.name}{status}: {result.message}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Smoke-test M3Undle HDHomeRun endpoints")
    parser.add_argument("--base-url", default="http://127.0.0.1:8080", help="Base URL for M3Undle")
    parser.add_argument(
        "--start-server",
        action="store_true",
        help="Start M3Undle.Web with dotnet run before testing",
    )
    parser.add_argument(
        "--project",
        default="src/M3Undle.Web/M3Undle.Web.csproj",
        help="Path to M3Undle.Web.csproj when using --start-server",
    )
    parser.add_argument(
        "--data-dir",
        default=None,
        help="Data directory for --start-server (default: temporary isolated directory)",
    )
    parser.add_argument(
        "--startup-timeout",
        type=float,
        default=60.0,
        help="Seconds to wait for /health when using --start-server",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=10.0,
        help="HTTP request timeout in seconds",
    )
    parser.add_argument(
        "--allow-lineup-unavailable",
        action="store_true",
        help="Treat 503 from lineup endpoints as warning instead of failure",
    )
    parser.add_argument(
        "--probe-stream",
        action="store_true",
        help="Try reading a small chunk from the first channel URL in lineup.json",
    )
    args = parser.parse_args()

    proc: Optional[subprocess.Popen[bytes]] = None
    effective_data_dir: Optional[str] = None
    cleanup_data_dir = False
    results: list[CheckResult] = []

    try:
        if args.start_server:
            proc, effective_data_dir, cleanup_data_dir = start_server(args.project, args.base_url, args.data_dir)
            if not wait_for_health(args.base_url, args.startup_timeout, proc):
                print("[FAIL] startup: service did not become healthy in time")
                if proc.stdout is not None:
                    tail = proc.stdout.read().decode("utf-8", errors="replace")
                    if tail.strip():
                        print("\n--- dotnet run output ---")
                        print(tail[-4000:])
                        print("--- end output ---\n")
                return 1
            print("[PASS] startup: /health reached 200")

        status, _, body, err = fetch(args.base_url, "/discover.json", args.timeout)
        results.append(validate_discover_json(status, body, err))

        status, _, body, err = fetch(args.base_url, "/lineup_status.json", args.timeout)
        results.append(validate_lineup_status(status, body, err))

        status, _, body, err = fetch(args.base_url, "/lineup.post", args.timeout)
        results.append(validate_lineup_post(status, body, err))

        status, _, body, err = fetch(args.base_url, "/device.xml", args.timeout)
        results.append(validate_device_xml(status, body, err))

        lineup_channels: list[dict[str, object]] = []

        status, _, body, err = fetch(args.base_url, "/lineup.json", args.timeout)
        allowed = maybe_allow_lineup_unavailable("/lineup.json", status, args.allow_lineup_unavailable)
        if allowed:
            results.append(allowed)
        else:
            lineup_result, lineup_channels = validate_lineup_json(status, body, err)
            results.append(lineup_result)

        status, _, body, err = fetch(args.base_url, "/lineup.xml", args.timeout)
        allowed = maybe_allow_lineup_unavailable("/lineup.xml", status, args.allow_lineup_unavailable)
        if allowed:
            results.append(allowed)
        else:
            results.append(validate_lineup_xml(status, body, err))

        status, _, body, err = fetch(args.base_url, "/lineup.m3u", args.timeout)
        allowed = maybe_allow_lineup_unavailable("/lineup.m3u", status, args.allow_lineup_unavailable)
        if allowed:
            results.append(allowed)
        else:
            results.append(validate_lineup_m3u(status, body, err))

        if args.probe_stream and lineup_channels:
            first_url = lineup_channels[0].get("URL")
            if isinstance(first_url, str) and first_url:
                results.append(probe_stream(first_url, args.timeout))
            else:
                results.append(CheckResult("probe-stream", False, "first lineup entry has no URL"))

        for result in results:
            print_result(result)

        failed = [r for r in results if not r.ok]
        warnings = [r for r in results if r.warning]
        print(f"\nSummary: {len(results) - len(failed)} passed, {len(failed)} failed, {len(warnings)} warnings")
        return 1 if failed else 0
    finally:
        if proc is not None and proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)
        if cleanup_data_dir and effective_data_dir and os.path.isdir(effective_data_dir):
            shutil.rmtree(effective_data_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
