"""Add only this relay's Caddy route; keep a restorable snapshot before insertion.

The unprivileged timer restores the route after a Caddyfile reload. It never
rewrites other routes or /etc/caddy/Caddyfile. --remove removes only our route.
"""
import datetime
import json
import os
from pathlib import Path
import sys
import urllib.request
import urllib.error

BASE = "http://127.0.0.1:2019"
ROOT = Path(__file__).resolve().parent
ROUTE_ID = "sasarisa-phone-relay"
ROUTE = {"@id": ROUTE_ID, "match": [{"host": ["sasarisa.xsw.kr"]}], "handle": [{"handler": "reverse_proxy", "upstreams": [{"dial": "127.0.0.1:17846"}]}], "terminal": True}


def request(path, data=None, method=None, etag=None):
    headers = {"Content-Type": "application/json"}
    if etag:
        headers["If-Match"] = etag
    req = urllib.request.Request(BASE + path, data=None if data is None else json.dumps(data).encode(), headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=8) as response:
        raw = response.read()
        return (json.loads(raw) if raw else None), response.headers.get("ETag")


def main():
    config, _ = request("/config/")
    servers = config["apps"]["http"]["servers"]
    existing = []
    for name, server in servers.items():
        for route in server.get("routes", []):
            if route.get("@id") == ROUTE_ID or any("sasarisa.xsw.kr" in item.get("host", []) for item in route.get("match", [])):
                existing.append((name, route))
    if "--inspect" in sys.argv:
        print(json.dumps({"servers": {k: v.get("listen") for k, v in servers.items()}, "existing": existing}))
        return
    if existing:
        if len(existing) != 1 or existing[0][1] != ROUTE:
            raise RuntimeError("Existing host route differs; refusing to replace it")
        if "--remove" in sys.argv:
            snapshot(config)
            _, etag = request("/id/" + ROUTE_ID)
            request("/id/" + ROUTE_ID, method="DELETE", etag=etag)
            print("Removed relay route only")
        return
    if "--remove" in sys.argv:
        return
    candidates = [name for name, server in servers.items() if ":443" in server.get("listen", [])]
    if len(candidates) != 1:
        raise RuntimeError("Expected one existing HTTPS server")
    target = "/config/apps/http/servers/" + candidates[0] + "/routes"
    _, etag = request(target)
    snapshot(config)
    request(target, ROUTE, method="POST", etag=etag)
    current, _ = request("/id/" + ROUTE_ID)
    if current != ROUTE:
        raise RuntimeError("Relay route verification failed")
    print("Verified relay route")


def snapshot(config):
    folder = ROOT / "backups"
    folder.mkdir(mode=0o700, exist_ok=True)
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    file = folder / ("caddy-before-" + stamp + ".json")
    fd = os.open(file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(config, stream, indent=2)
    if json.loads(file.read_text()) != config:
        raise RuntimeError("Caddy backup failed verification")


if __name__ == "__main__":
    main()
