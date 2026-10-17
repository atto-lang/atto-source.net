#!/usr/bin/env python3
"""Execute the NativeAOT Oreslang Lambda ORW1 worker end to end."""

from __future__ import annotations

import argparse
import concurrent.futures
import struct
import subprocess
import time

MAGIC = b"ORW1"
VERSION = 1
HEADER = struct.Struct(">4sBBHQqi")
READY = 1
INVOKE = 2
RESPONSE = 4
ERROR = 5
SHUTDOWN = 6
MAX_PAYLOAD = 1024 * 1024


def frame(kind: int, request_id: int, deadline_ms: int, payload: str = "") -> bytes:
    body = payload.encode("utf-8", errors="strict")
    if len(body) > MAX_PAYLOAD:
        raise AssertionError("test payload exceeds ORW1 maximum")
    return HEADER.pack(
        MAGIC, VERSION, kind, 0, request_id, deadline_ms, len(body)
    ) + body


def read_exact(stream, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = stream.read(size - len(data))
        if not chunk:
            raise EOFError(f"truncated ORW1 stream at {len(data)}/{size}")
        data.extend(chunk)
    return bytes(data)


def read_frame(stream):
    raw = read_exact(stream, HEADER.size)
    magic, version, kind, flags, request_id, deadline, length = HEADER.unpack(raw)
    if magic != MAGIC or version != VERSION or flags != 0:
        raise AssertionError("invalid ORW1 response header")
    if length < 0 or length > MAX_PAYLOAD:
        raise AssertionError("invalid ORW1 response payload length")
    payload = read_exact(stream, length).decode("utf-8", errors="strict")
    return kind, request_id, deadline, payload


def timed_read(executor, stream, seconds: float = 5.0):
    future = executor.submit(read_frame, stream)
    return future.result(timeout=seconds)


def require(actual, expected, why: str) -> None:
    if actual != expected:
        raise AssertionError(f"{why}: expected {expected!r}, got {actual!r}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("binary")
    args = parser.parse_args()

    process = subprocess.Popen(
        [args.binary, "--worker"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    assert process.stdin is not None
    assert process.stdout is not None
    assert process.stderr is not None

    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
            require(
                timed_read(executor, process.stdout),
                (READY, 0, 0, "oreslang-dotnet-worker/1"),
                "ready frame",
            )

            deadline = int(time.time() * 1000) + 5_000
            process.stdin.write(frame(INVOKE, 1, deadline, "ping"))
            process.stdin.flush()
            require(
                timed_read(executor, process.stdout),
                (RESPONSE, 1, 0, "pong"),
                "ping response",
            )

            deadline = int(time.time() * 1000) + 5_000
            process.stdin.write(frame(INVOKE, 2, deadline, "other"))
            process.stdin.flush()
            require(
                timed_read(executor, process.stdout),
                (RESPONSE, 2, 0, "unsupported"),
                "fallback response",
            )

            # Replay must fail without invoking guest code.
            deadline = int(time.time() * 1000) + 5_000
            process.stdin.write(frame(INVOKE, 2, deadline, "ping"))
            process.stdin.flush()
            require(
                timed_read(executor, process.stdout),
                (ERROR, 2, 0, "request-id-replay"),
                "request-id replay rejection",
            )

            process.stdin.write(frame(SHUTDOWN, 0, 0))
            process.stdin.flush()
            process.stdin.close()
            process.wait(timeout=5)

        require(process.returncode, 0, "worker exit status")
        stderr = process.stderr.read()
        if len(stderr) > MAX_PAYLOAD:
            raise AssertionError("worker stderr exceeded verification budget")
        if process.stdout.read(1) not in (b"",):
            raise AssertionError("worker emitted bytes after shutdown")
        print("Oreslang .NET NativeAOT ORW1 Lambda worker: OK")
        return 0
    except BaseException:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
