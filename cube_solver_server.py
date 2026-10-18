"""
Rubik's Cube Solver Server

Small local HTTP server that solves a Rubik's Cube from a 54-character
facelet string using the two-phase Kociemba algorithm.

### Install:
    pip install kociemba

### Run:
    python cube_solver_server.py [--host 127.0.0.1] [--port 8090]

### Health check:
    GET /health -> {"status": "healthy"}

### Request:
    POST /solve
    {"facelets": "<54-character URFDLB facelet string>"}

    The facelet string is 54 characters, each one of U R F D L B, ordered as
    all 9 stickers of the U face, then R, then F, then D, then L, then B -
    each face read left-to-right, top-to-bottom looking directly at it. Each
    letter names the FACE whose center shares that sticker's color (so the
    5th character of every 9-character block equals the block's own face
    letter, e.g. position 5 is always 'U').

### Response (success):
    {"success": true, "solution": "R U R' ...", "length": 18}

### Response (failure):
    {"success": false, "error": "<message>"}
"""

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import kociemba

SOLVED_FACELETS = "UUUUUUUUURRRRRRRRRFFFFFFFFFDDDDDDDDDLLLLLLLLLBBBBBBBBB"


class SolveHandler(BaseHTTPRequestHandler):
    def _send_json(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send_json(200, {"status": "healthy"})
        else:
            self._send_json(404, {"success": False, "error": f"Unknown endpoint: {self.path}"})

    def do_POST(self):
        if self.path != "/solve":
            self._send_json(404, {"success": False, "error": f"Unknown endpoint: {self.path}"})
            return

        content_length = int(self.headers.get("Content-Length", 0))
        raw_body = self.rfile.read(content_length) if content_length else b""

        try:
            data = json.loads(raw_body or b"{}")
        except json.JSONDecodeError:
            self._send_json(400, {"success": False, "error": "Invalid JSON body"})
            return

        facelets = data.get("facelets", "")
        if not isinstance(facelets, str) or len(facelets) != 54:
            got = len(facelets) if isinstance(facelets, str) else type(facelets).__name__
            self._send_json(400, {"success": False, "error": f"'facelets' must be a 54-character string, got {got}"})
            return

        # The kociemba package answers an already solved cube with a long move sequence that changes nothing
        # (R L U2 R L' B2 U2 R2 F2 L2 D2 L2 F2). A solved cube needs no moves.
        if facelets == SOLVED_FACELETS:
            self._send_json(200, {"success": True, "solution": "", "length": 0, "already_solved": True})
            return

        try:
            solution = kociemba.solve(facelets)
        except Exception as ex:
            self._send_json(422, {"success": False, "error": f"Could not solve cube: {ex}"})
            return

        moves = solution.split()
        self._send_json(200, {"success": True, "solution": solution, "length": len(moves)})

    def log_message(self, format, *args):
        print(f"[cube_solver] {self.address_string()} - {format % args}")


def main():
    parser = argparse.ArgumentParser(description="Rubik's Cube solver HTTP server")
    parser.add_argument("--host", default="127.0.0.1", help="Host to bind to (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=8090, help="Port to listen on (default: 8090)")
    args = parser.parse_args()

    server = ThreadingHTTPServer((args.host, args.port), SolveHandler)
    print(f"Cube solver server listening on http://{args.host}:{args.port} (POST /solve)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("Shutting down cube solver server...")
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
