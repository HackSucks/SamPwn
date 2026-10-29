#!/usr/bin/env python3
"""Run a command through the SamPwn persistent root shell.

The TV daemon listens on TCP 2323 and starts a real bash.  One command is sent
per connection, followed by a sentinel echo.  The exit status of the remote
command becomes this program's exit status.

Examples:
    tools/rootshell.py 'id'
    tools/rootshell.py --host 192.168.1.93 --port 2323 'cat /opt/etc/sampwn/shell.state'
"""
from __future__ import annotations

import argparse
import os
import re
import socket
import sys

SENTINEL = "__SAMPWN_DONE__"
PATTERN = re.compile(rb"__SAMPWN_DONE__(\d+)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", nargs="?", default="id")
    parser.add_argument("timeout", nargs="?", type=float, default=20.0)
    parser.add_argument("--host", default=os.environ.get("TV_IP", ""))
    parser.add_argument("--port", type=int, default=2323)
    parser.add_argument("--quiet", action="store_true", help="do not echo output")
    args = parser.parse_args()
    if not args.host:
        parser.error("--host is required (or set TV_IP)")

    try:
        sock = socket.create_connection((args.host, args.port), timeout=args.timeout)
    except OSError as error:
        print(f"rootshell: cannot connect to {args.host}:{args.port}: {error}",
              file=sys.stderr)
        return 2
    sock.settimeout(args.timeout)
    payload = f"{args.command}\necho {SENTINEL}$?\n".encode()
    try:
        sock.sendall(payload)
    except OSError as error:
        print(f"rootshell: send failed: {error}", file=sys.stderr)
        sock.close()
        return 2

    data = bytearray()
    status = None
    try:
        while len(data) < 4 * 1024 * 1024:
            chunk = sock.recv(65536)
            if not chunk:
                break
            data.extend(chunk)
            match = PATTERN.search(bytes(data))
            if match:
                status = int(match.group(1))
                break
    except socket.timeout:
        pass
    finally:
        sock.close()

    text = data.decode("utf-8", errors="replace")
    if not args.quiet:
        print(text, end="" if text.endswith("\n") else "\n")
    if status is None:
        print("rootshell: no completion marker from remote shell", file=sys.stderr)
        return 1
    return status


if __name__ == "__main__":
    raise SystemExit(main())
