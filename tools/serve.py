#!/usr/bin/env python3
"""Small HTTP file server used to deliver SamPwn files to the TV.

The TV's `sdb push` is unreliable, so the installer serves the deploy
artifacts over HTTP and has the TV fetch them with curl.  Only regular files
below --root are served; directory listings are disabled.
"""
from __future__ import annotations

import argparse
import functools
import http.server
import os
import socket
import sys


class Handler(http.server.SimpleHTTPRequestHandler):
    server_version = "SamPwnFiles/1.0"

    def list_directory(self, path):  # noqa: D401 - stdlib hook
        self.send_error(403, "Directory listing disabled")
        return None

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def log_message(self, fmt, *args):
        if not self.server.quiet:
            sys.stderr.write("serve: " + (fmt % args) + "\n")
            sys.stderr.flush()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, help="directory to serve")
    parser.add_argument("--bind", default="0.0.0.0", help="bind address")
    parser.add_argument("--port", type=int, default=8000, help="port (0 = pick one)")
    parser.add_argument("--quiet", action="store_true")
    args = parser.parse_args()

    root = os.path.realpath(args.root)
    if not os.path.isdir(root):
        parser.error(f"not a directory: {root}")

    handler = functools.partial(Handler, directory=root)
    httpd = http.server.ThreadingHTTPServer((args.bind, args.port), handler)
    httpd.quiet = args.quiet
    host, port = httpd.server_address[:2]
    print(f"SAMPWN_SERVE_READY {host} {port}", flush=True)
    try:
        httpd.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
