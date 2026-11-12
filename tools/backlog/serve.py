#!/usr/bin/env python3
"""Serve the "Backlog de juegos" page locally and save what you change in it.

  python3 serve.py        → http://127.0.0.1:8765

GET  /                    the page (backlog-de-juegos.html, next to this file)
GET  /covers/<id>.jpg     covers from <work>/data/covers
GET  /api/games           <work>/data/games.json
GET  /api/meta            <work>/data/meta.json ({sync, recs})
POST /api/games/<id>      merge a JSON patch into that game (null deletes a field; creates the game if new)

Python 3 standard library only; listens on localhost only.
"""
import json, re, threading
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from sync_backlog import DATA, save_json

PAGE = Path(__file__).with_name("backlog-de-juegos.html")
GAMES, META, COVERS = DATA / "games.json", DATA / "meta.json", DATA / "covers"
PORT = 8765
lock = threading.Lock()


def load(path):
    return json.loads(path.read_text()) if path.exists() else {}


class Handler(SimpleHTTPRequestHandler):
    def send_json(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_file(self, path, ctype):
        if not path.is_file():
            return self.send_error(404)
        body = path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = self.path.split("?")[0]
        if path == "/":
            return self.send_file(PAGE, "text/html; charset=utf-8")
        if path == "/api/games":
            return self.send_json(load(GAMES))
        if path == "/api/meta":
            return self.send_json(load(META))
        if m := re.fullmatch(r"/covers/([\w.-]+\.jpg)", path):
            return self.send_file(COVERS / m[1], "image/jpeg")
        self.send_error(404)

    def do_POST(self):
        m = re.fullmatch(r"/api/games/([\w.~:@+-]+)", self.path)
        if not m:
            return self.send_error(404)
        patch = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        with lock:  # one write at a time; re-read so a running sync's save is not lost
            games = load(GAMES)
            game = games.setdefault(m[1], {})
            for k, v in patch.items():
                if v is None:
                    game.pop(k, None)
                else:
                    game[k] = v
            save_json(GAMES, games)
        self.send_json(game)

    def log_message(self, fmt, *args):  # quiet: only errors
        if args and str(args[1])[0] in "45":
            super().log_message(fmt, *args)


if __name__ == "__main__":
    print(f"Backlog en http://127.0.0.1:{PORT}  (Ctrl+C para parar)")
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
