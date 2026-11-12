#!/usr/bin/env python3
"""Sync the "Backlog de juegos" artifact from Steam, Steam Family, IGDB, HowLongToBeat (opt-in), OpenCritic, SteamGridDB and Steam reviews.

The artifact database is only writable through Claude Code's ArtifactData tool, so a sync is:
  1. Claude exports `games` to <work>/export/games/*.json and the versions to <work>/export/versions.json
  2. this script fetches the sources, merges and writes batches to <work>/out/batch-NN.json
  3. Claude applies each batch with ArtifactData (if_version pinned) and then meta/sync.

Keys live in the macOS keychain (service "backlog-games"); run `--setup` once in a terminal.
Python 3 standard library only.
"""
import argparse, base64, getpass, json, re, subprocess, sys, time, unicodedata
import urllib.error, urllib.parse, urllib.request
from datetime import datetime, timezone
from pathlib import Path
import os

SERVICE = "backlog-games"
WORK = Path(os.environ.get("BACKLOG_WORK", Path.home() / "Work/tools/backlog-games"))
CACHE = Path.home() / ".cache/backlog-games/info.json"
CACHE_DAYS = 30
OC_CACHE = Path.home() / ".cache/backlog-games/opencritic.json"
HLTB_CACHE = Path.home() / ".cache/backlog-games/hltb.json"
GRID_CACHE = Path.home() / ".cache/backlog-games/grids.json"
# The artifact only shows images from its own asset store: covers are uploaded by Claude (Artifact asset upload)
# and their "/_blob/<id>" urls kept here, keyed like the cache. Covers not uploaded yet are left in out/covers/.
COVER_ASSETS = Path.home() / ".cache/backlog-games/cover-assets.json"
OC_DAILY = 20  # RapidAPI free tier is small: look up a few games per sync, cached for good
USER_FIELDS = ("status", "queueOrder", "notes", "finishedAt", "addedAt")
EXIT_FAMILY_EXPIRED = 3

# ---------- keychain ----------

def kc_get(name):
    r = subprocess.run(["security", "find-generic-password", "-s", SERVICE, "-a", name, "-w"],
                       capture_output=True, text=True)
    return r.stdout.strip() or None if r.returncode == 0 else None

def kc_set(name, value):
    subprocess.run(["security", "add-generic-password", "-U", "-s", SERVICE, "-a", name, "-w", value],
                   check=True, capture_output=True)

def ask_family_cookie():
    print("\nSteam Family: inicia sesión en https://store.steampowered.com en el navegador, abre las")
    print("herramientas de desarrollo → Almacenamiento/Aplicación → Cookies y copia el valor de 'steamLoginSecure'.")
    raw = getpass.getpass("steamLoginSecure (vacío para saltar): ").strip()
    if raw:
        token = urllib.parse.unquote(raw).split("||")[-1]
        kc_set("steam_family_token", token)
        print(f"  Guardado; caduca {datetime.fromtimestamp(jwt_exp(token)):%Y-%m-%d %H:%M}.")

def setup():
    for name, label, secret in [("steam_id", "SteamID64", False), ("steam_key", "Steam Web API key", True),
                                ("twitch_id", "Twitch Client ID", False), ("twitch_secret", "Twitch Client Secret", True),
                                ("opencritic_key", "OpenCritic RapidAPI key (opcional)", True)]:
        cur = kc_get(name)
        value = (getpass.getpass if secret else input)(f"{label}{' [ya guardado, Enter para mantener]' if cur else ''}: ").strip()
        if value:
            kc_set(name, value)
    ask_family_cookie()

def jwt_exp(token):
    try:
        payload = token.split(".")[1]
        return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4))).get("exp", 0)
    except Exception:
        return 0

# ---------- http ----------

def http_json(url, data=None, headers=None):
    req = urllib.request.Request(url, data=data.encode() if isinstance(data, str) else data,
                                 headers={"User-Agent": "backlog-sync/1.0", **(headers or {})})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt < 3:
                time.sleep(2 ** attempt)
                continue
            raise

# ---------- sources ----------

def steam_owned(steam_id, key):
    url = ("https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?" + urllib.parse.urlencode(
        {"key": key, "steamid": steam_id, "include_appinfo": 1, "include_played_free_games": 1, "format": "json"}))
    try:
        games = http_json(url).get("response", {}).get("games")
    except urllib.error.HTTPError as e:
        if e.code in (401, 403):
            sys.exit("Steam rechazó la clave de Web API: vuelve a guardarla con `--setup`.")
        raise
    if games is None:
        sys.exit("Steam devolvió una biblioteca vacía: revisa que los detalles de juego del perfil sean públicos.")
    return [{"appid": str(g["appid"]), "name": g.get("name") or "Unknown",
             "playtimeHours": round(g.get("playtime_forever", 0) / 60, 1), "family": False} for g in games]

class FamilyExpired(Exception):
    pass

def steam_family(token, steam_id):
    hdr = {"Origin": "https://store.steampowered.com", "Referer": "https://store.steampowered.com/"}
    def call(url, data=None):
        try:
            return http_json(url, data, headers=hdr).get("response", {})
        except urllib.error.HTTPError as e:
            if e.code in (401, 403):
                raise FamilyExpired
            raise
    base = "https://api.steampowered.com/IFamilyGroupsService/"
    grp = call(f"{base}GetFamilyGroupForUser/v1/?access_token={token}")
    gid = str(grp.get("family_groupid") or "0")
    if grp.get("is_not_member_of_any_group") or gid == "0":
        return []
    lib = call(f"{base}GetSharedLibraryApps/v1/?" + urllib.parse.urlencode(
        {"access_token": token, "family_groupid": gid, "include_own": "false", "include_excluded": "false",
         "include_free": "false", "include_non_games": "false", "language": "english"}))
    # Your own hours on borrowed games: the family playtime summary (POST only), filtered to your Steam ID.
    played = {str(e["appid"]): e.get("seconds_played", 0) for e in call(
        f"{base}GetPlaytimeSummary/v1/?access_token={token}&family_groupid={gid}", b"").get("entries", [])
        if e.get("steamid") == steam_id}
    return [{"appid": str(a["appid"]), "name": a.get("name") or "Unknown", "family": True,
             "playtimeHours": round(played[str(a["appid"])] / 3600, 1) if str(a["appid"]) in played else None}
            for a in lib.get("apps", []) if not a.get("exclude_reason")]

def igdb_token(cid, secret):
    return http_json("https://id.twitch.tv/oauth2/token", urllib.parse.urlencode(
        {"client_id": cid, "client_secret": secret, "grant_type": "client_credentials"}))["access_token"]

def igdb(cid, token, endpoint, body):
    time.sleep(0.26)  # IGDB allows 4 requests/s
    return http_json(f"https://api.igdb.com/v4/{endpoint}", body,
                     {"Client-ID": cid, "Authorization": f"Bearer {token}"})

def steam_reviews(appid):
    try:
        s = http_json(f"https://store.steampowered.com/appreviews/{appid}?json=1&language=all"
                      "&purchase_type=all&num_per_page=0").get("query_summary", {})
    except Exception:
        return {}
    total = s.get("total_reviews") or 0
    if not total:
        return {}
    return {"steamPositive": round(100 * s.get("total_positive", 0) / total), "steamLabel": s.get("review_score_desc")}

def fetch_info(targets, cid, secret, cache):
    """targets: {key: {"appid"|None, "name"}} -> fills cache[key] with IGDB + review data."""
    now = time.time()
    todo = {k: t for k, t in targets.items() if now - cache.get(k, {}).get("fetchedAt", 0) > CACHE_DAYS * 86400}
    if not todo:
        return
    token = igdb_token(cid, secret)
    ids = {}
    steam = {t["appid"]: k for k, t in todo.items() if t.get("appid")}
    uids = list(steam)
    for i in range(0, len(uids), 400):
        chunk = ",".join(f'"{u}"' for u in uids[i:i + 400])
        for row in igdb(cid, token, "external_games",
                        f"fields game,uid; where external_game_source = 1 & uid = ({chunk}); limit 500;"):
            ids.setdefault(steam[row["uid"]], row["game"])
    def search(name):
        rows = igdb(cid, token, "games", f'search "{name.replace(chr(34), "")}"; fields id; '
                                         'where version_parent = null; limit 1;')
        return rows[0]["id"] if rows else None

    def lookup(idl):
        details, ttb = {}, {}
        for i in range(0, len(idl), 400):
            lst = ",".join(map(str, idl[i:i + 400]))
            for r in igdb(cid, token, "games", f"fields aggregated_rating,first_release_date,genres.name,game_modes.name; "
                                              f"where id = ({lst}); limit 500;"):
                details[r["id"]] = r
            for r in igdb(cid, token, "game_time_to_beats",
                          f"fields game_id,hastily,normally,completely; where game_id = ({lst}); limit 500;"):
                ttb[r["game_id"]] = r
        return details, ttb

    for k, t in todo.items():
        if k not in ids and (gid := search(t["name"])):
            ids[k] = gid
    details, ttb = lookup(list(set(ids.values())))
    # Editions ("GOTY", "Enhanced", "- Test Server"...) often match an IGDB record with no length or score:
    # retry those with the cleaned base name and keep the result if it has data.
    retry = {k: clean_name(t["name"]) for k, t in todo.items() if not NON_GAME.search(t["name"])
             and not ttb.get(ids.get(k)) and not details.get(ids.get(k), {}).get("aggregated_rating")}
    alt = {k: gid for k, n in retry.items() if (gid := search(n)) and gid != ids.get(k)}
    d2, t2 = lookup(list(set(alt.values())))
    for k, gid in alt.items():
        if t2.get(gid) or d2.get(gid, {}).get("aggregated_rating"):
            ids[k] = gid
    details.update(d2)
    ttb.update(t2)
    hours = lambda s: round(s / 3600, 1) if s else None
    for n, (k, t) in enumerate(todo.items(), 1):
        print(f"\r  Info {n}/{len(todo)}", end="", flush=True)
        info = {"fetchedAt": now}
        gid = ids.get(k)
        if gid:
            d, tb = details.get(gid, {}), ttb.get(gid, {})
            info.update(mainHours=hours(tb.get("hastily")), extrasHours=hours(tb.get("normally")),
                        completionistHours=hours(tb.get("completely")),
                        criticScore=round(d["aggregated_rating"]) if d.get("aggregated_rating") else None,
                        genres=[g["name"] for g in d.get("genres", [])],
                        modes=[m["name"] for m in d.get("game_modes", [])],
                        releaseYear=datetime.fromtimestamp(d["first_release_date"], timezone.utc).year
                        if d.get("first_release_date") else None)
        if t.get("appid"):
            info.update(steam_reviews(t["appid"]))
        cache[k] = info
    print()

def opencritic(targets, key, cache, budget):
    """Fill cache[key] = {"score": int|None, "id": int|None} for up to `budget` uncached targets (2 calls each)."""
    hdr = {"X-RapidAPI-Key": key, "X-RapidAPI-Host": "opencritic-api.p.rapidapi.com"}
    api = lambda path: http_json(f"https://opencritic-api.p.rapidapi.com/{path}", headers=hdr)
    todo = [(k, t) for k, t in targets.items() if k not in cache and not NON_GAME.search(t["name"])][:budget]
    for n, (k, t) in enumerate(todo, 1):
        print(f"\r  OpenCritic {n}/{len(todo)}", end="", flush=True)
        try:
            hits = api("game/search?" + urllib.parse.urlencode({"criteria": clean_name(t["name"])}))
            hit = hits[0] if hits and hits[0].get("dist", 1) <= 0.2 else None  # dist: 0 = exact name
            score = api(f"game/{hit['id']}").get("topCriticScore") if hit else None
        except urllib.error.HTTPError as e:
            if e.code in (401, 403, 429):
                print(f"\n  OpenCritic paró ({e.code}): {'clave rechazada' if e.code != 429 else 'cupo agotado'}.")
                break
            raise
        cache[k] = {"id": hit and hit["id"], "score": round(score) if score and score > 0 else None}
    if todo:
        print()

def steamgriddb(targets, key, cache):
    """Fill cache[key] = cover thumbnail URL (600x900 grid) or None, for every uncached target."""
    hdr = {"Authorization": f"Bearer {key}"}
    api = lambda path: http_json(f"https://www.steamgriddb.com/api/v2/{path}", headers=hdr).get("data") or []
    todo = [(k, t) for k, t in targets.items() if k not in cache and not NON_GAME.search(t["name"])]
    for n, (k, t) in enumerate(todo, 1):
        print(f"\r  Portadas {n}/{len(todo)}", end="", flush=True)
        try:
            if t.get("appid"):
                grids = api(f"grids/steam/{t['appid']}?dimensions=600x900&types=static&limit=1")
            else:
                hits = api("search/autocomplete/" + urllib.parse.quote(clean_name(t["name"]), safe=""))
                grids = api(f"grids/game/{hits[0]['id']}?dimensions=600x900&types=static&limit=1") if hits else []
        except urllib.error.HTTPError as e:
            if e.code in (401, 403):
                print("\n  SteamGridDB rechazó la clave.")
                break
            if e.code != 404:  # 404: SteamGridDB doesn't know the game
                raise
            grids = []
        cache[k] = grids[0]["thumb"] if grids else None
    if todo:
        print()

def hltb(targets, cache):
    """--hltb only, run by the user: fill cache[key] = {mainHours, extrasHours, completionistHours} or {} via the
    community library howlongtobeatpy (HLTB has no public API; the library uses its site search)."""
    try:
        from howlongtobeatpy import HowLongToBeat
    except ImportError:
        sys.exit("Falta la librería: pip install howlongtobeatpy")
    todo = [(k, t) for k, t in targets.items() if k not in cache and not NON_GAME.search(t["name"])]
    for n, (k, t) in enumerate(todo, 1):
        print(f"\r  HowLongToBeat {n}/{len(todo)}", end="", flush=True)
        try:
            hits = HowLongToBeat().search(clean_name(t["name"])) or []
        except Exception as e:  # site change, block or network: keep what we have and stop
            print(f"\n  HowLongToBeat falló ({e}); se guarda lo obtenido hasta ahora.")
            break
        best = max(hits, key=lambda h: h.similarity, default=None)
        hours = lambda v: round(v, 1) if v else None
        cache[k] = {} if not best or best.similarity < 0.8 else clean(
            {"mainHours": hours(best.main_story), "extrasHours": hours(best.main_extra),
             "completionistHours": hours(best.completionist)})
        time.sleep(0.5)
    if todo:
        print()

NON_GAME = re.compile(r"test server|\bPTS\b|playtest|public test|test client|\bdemo\b|sneak peek|non-game|"
                      r"lossless scaling|controller companion|xbplay|aimlabs|deathmatch|\bmod:|^the lab$", re.I)

EDITION_WORDS = (r"enhanced|remaster(ed)?|deluxe|goty|game of the year|edition|director'?s cut|complete|gold pack|"
                 r"ultimate|special|definitive|collection|anniversary|master collection version|classic|steam")

def clean_name(name):
    """'Batman: Arkham Asylum GOTY Edition' -> 'Batman: Arkham Asylum'; drops ™®, (...) and '- Test Server'-style tails."""
    s = re.sub(r"[™®]|\(.*?\)", "", name)
    s = re.sub(r"\s+-\s+.*$", "", s)
    s = re.sub(rf"\b({EDITION_WORDS})\b", "", s, flags=re.I)
    return re.sub(r"\s+", " ", s).strip(" :-") or name

# ---------- merge ----------

def norm(name):
    s = unicodedata.normalize("NFKD", name.lower().replace("™", "").replace("®", ""))
    return re.sub(r"[^a-z0-9]+", " ", s.encode("ascii", "ignore").decode()).strip()

def suggests_evergreen(info):
    genres, modes = set(info.get("genres") or []), set(info.get("modes") or [])
    multiplayer_only = modes and "Single player" not in modes
    endless_modes = {"Battle Royale", "Massively Multiplayer Online (MMO)"} & modes
    return bool({"Simulator", "MOBA", "Sport", "Racing"} & genres or endless_modes or multiplayer_only)

def clean(d):
    return {k: v for k, v in d.items() if v is not None}

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--setup", action="store_true", help="guardar o cambiar claves en el llavero (interactivo)")
    ap.add_argument("--family-login", action="store_true", help="volver a pegar la cookie de Steam (interactivo)")
    ap.add_argument("--no-family", action="store_true", help="saltar Steam Family")
    ap.add_argument("--limit", type=int, help="solo los N juegos de Steam con más horas (para probar)")
    ap.add_argument("--hltb", action="store_true", help="pedir duraciones a HowLongToBeat (pip install howlongtobeatpy)")
    ap.add_argument("--dry-run", action="store_true", help="mostrar el resumen sin escribir lotes")
    a = ap.parse_args()
    if a.setup:
        return setup()
    if a.family_login:
        return ask_family_cookie()

    keys = {n: kc_get(n) for n in ("steam_id", "steam_key", "twitch_id", "twitch_secret")}
    if not all(keys.values()):
        sys.exit("Faltan claves en el llavero: ejecuta `python3 sync_backlog.py --setup` en una terminal.")

    export = WORK / "export"
    existing = {p.stem: json.loads(p.read_text()) for p in (export / "games").glob("*.json")}
    vfile = export / "versions.json"
    versions = json.loads(vfile.read_text()) if vfile.exists() else {}

    print("Steam: biblioteca…")
    steam = sorted(steam_owned(keys["steam_id"], keys["steam_key"]), key=lambda g: -g["playtimeHours"])
    owned = {g["appid"] for g in steam}
    family, family_note = [], "familia omitida"
    if not a.no_family:
        token = kc_get("steam_family_token")
        if not token or jwt_exp(token) < time.time() + 60:
            print("El token de Steam Family falta o ha caducado: ejecuta `python3 sync_backlog.py --family-login`.")
            sys.exit(EXIT_FAMILY_EXPIRED)
        try:
            family = [g for g in steam_family(token, keys["steam_id"]) if g["appid"] not in owned]
            family_note = "familia"
        except FamilyExpired:
            print("Steam rechazó el token de Family: ejecuta `python3 sync_backlog.py --family-login`.")
            sys.exit(EXIT_FAMILY_EXPIRED)
    synced = steam + family
    if a.limit:
        synced = steam[:a.limit] + family[:max(1, a.limit // 5)]
    print(f"  {len(steam)} propios, {len(family)} de la familia; procesando {len(synced)}.")

    # Non-Steam docs that still need info (seeds, manual, unmatched) are looked up by name.
    by_norm = {norm(g["name"]): did for did, g in existing.items() if not did.startswith("steam-")}
    synced_norms = {norm(g["name"]) for g in synced}
    targets = {f"steam:{g['appid']}": {"appid": g["appid"], "name": g["name"]} for g in synced}
    for did, g in existing.items():
        if not did.startswith("steam-") and g.get("needsInfo") and norm(g["name"]) not in synced_norms:
            targets[f"name:{norm(g['name'])}"] = {"name": g["name"]}

    print("IGDB y reseñas…")
    cache = json.loads(CACHE.read_text()) if CACHE.exists() else {}
    try:
        fetch_info(targets, keys["twitch_id"], keys["twitch_secret"], cache)
    finally:
        CACHE.parent.mkdir(parents=True, exist_ok=True)
        CACHE.write_text(json.dumps(cache))
    hl = json.loads(HLTB_CACHE.read_text()) if HLTB_CACHE.exists() else {}
    if a.hltb:
        try:
            hltb(targets, hl)
        finally:
            HLTB_CACHE.write_text(json.dumps(hl))
    for k, h in hl.items():  # HLTB lengths win over IGDB's, even on later syncs without --hltb
        if h and k in cache:
            cache[k].update(h)
    if oc_key := kc_get("opencritic_key"):
        oc = json.loads(OC_CACHE.read_text()) if OC_CACHE.exists() else {}
        try:
            opencritic(targets, oc_key, oc, OC_DAILY)
        finally:
            OC_CACHE.write_text(json.dumps(oc))
        for k, o in oc.items():  # OpenCritic wins over IGDB's aggregated rating when it has a score
            if o.get("score") and k in cache:
                cache[k]["criticScore"] = o["score"]
    if grid_key := kc_get("steamgriddb_key"):
        grids = json.loads(GRID_CACHE.read_text()) if GRID_CACHE.exists() else {}
        try:
            steamgriddb(targets, grid_key, grids)
        finally:
            GRID_CACHE.write_text(json.dumps(grids))
        assets = json.loads(COVER_ASSETS.read_text()) if COVER_ASSETS.exists() else {}
        pending = {k: url for k, url in grids.items() if url and k in targets and k not in assets}
        covers = WORK / "out/covers"
        covers.mkdir(parents=True, exist_ok=True)
        for old in covers.glob("*.jpg"):
            old.unlink()
        for k, url in pending.items():  # file name = cache key with ":" -> "-" ("steam-620.jpg")
            req = urllib.request.Request(url, headers={"User-Agent": "backlog-sync/1.0"})
            (covers / f"{k.replace(':', '-')}.jpg").write_bytes(urllib.request.urlopen(req, timeout=30).read())
        if pending:
            print(f"  {len(pending)} portadas nuevas en {covers}: súbelas al artefacto y añádelas a {COVER_ASSETS}")
        for k, url in assets.items():
            cache.setdefault(k, {})["coverUrl"] = url
    # Manual data (web searches) lives in overrides.json, keyed like the cache ("steam:<appid>" or "name:<norm>").
    ovf = Path(__file__).with_name("overrides.json")
    for key, o in (json.loads(ovf.read_text()) if ovf.exists() else {}).items():
        cache[key] = {**cache.get(key, {}), **{k: v for k, v in o.items() if not k.startswith("_")}}

    now = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
    info_fields = ("mainHours", "extrasHours", "completionistHours", "criticScore", "steamPositive", "steamLabel",
                   "genres", "releaseYear", "coverUrl")
    writes, stats = [], {"nuevos": 0, "actualizados": 0, "fusionados": 0, "solo_info": 0, "sin_cambios": 0}

    def put_changes(did, data, stat):
        """Update only the fields that differ from the exported doc; skip it when nothing changed."""
        diff = {k: v for k, v in data.items() if existing[did].get(k) != v}
        if diff:
            put("update", did, diff)
        stats[stat if diff else "sin_cambios"] += 1

    def put(op, did, data):
        e = {"op": op, "collection": "games", "doc_id": did}
        if data is not None:
            e["data"] = data
        if did in versions:
            e["if_version"] = versions[did]
        writes.append(e)

    for g in synced:
        did = f"steam-{g['appid']}"
        info = cache.get(f"steam:{g['appid']}", {})
        data = clean({"name": g["name"], "platform": "steam", "family": g["family"], "steamAppId": g["appid"],
                      "playtimeHours": g["playtimeHours"], "source": "sync",
                      **{f: info.get(f) for f in info_fields}})
        data["needsInfo"] = not info.get("mainHours") and not info.get("criticScore")
        if did in existing:
            put_changes(did, data, "actualizados")
            continue
        old_id = by_norm.pop(norm(g["name"]), None)
        if old_id:
            old = existing[old_id]
            data.update({f: old[f] for f in USER_FIELDS if f in old})
            put("set", did, data)
            put("delete", old_id, None)
            stats["fusionados"] += 1
        else:
            data.update(status="hidden" if NON_GAME.search(g["name"]) else
                        "evergreen" if suggests_evergreen(info) else "backlog", addedAt=now, notes="")
            put("set", did, data)
            stats["nuevos"] += 1

    for key, t in targets.items():
        if key.startswith("name:"):
            did = by_norm[key[5:]]
            info = cache.get(key, {})
            data = clean({f: info.get(f) for f in info_fields if f not in ("steamPositive", "steamLabel")})
            if data:
                data["needsInfo"] = not data.get("mainHours") and not data.get("criticScore")
                put_changes(did, data, "solo_info")

    print("\nResumen:", ", ".join(f"{v} {k}" for k, v in stats.items()))
    for e in writes:
        if e["op"] != "update":
            print(f"  {e['op']:6} {e['doc_id']:40} {e.get('data', {}).get('name', '')} {e.get('data', {}).get('status', '')}")
    unpinned = [e["doc_id"] for e in writes if e["doc_id"] in existing and "if_version" not in e]
    if unpinned:
        print(f"\nSin versión ({len(unpinned)}), leerlas antes de aplicar: {' '.join(unpinned)}")
    if a.dry_run:
        return

    out = WORK / "out"
    out.mkdir(exist_ok=True)
    docs = out / "docs"
    docs.mkdir(exist_ok=True)
    for old in [*out.glob("*.json"), *docs.glob("*.json")]:
        old.unlink()
    for e in writes:  # ArtifactData reads each document from its own file (file_path)
        if "data" in e:
            path = docs / f"{e['doc_id']}.json"
            path.write_text(json.dumps(e.pop("data"), ensure_ascii=False))
            e["file_path"] = str(path)
    # A merge's set + delete must land in the same batch: start a new one if the pair would straddle.
    batches, cur = [], []
    for i, e in enumerate(writes):
        pair = e["op"] == "set" and i + 1 < len(writes) and writes[i + 1]["op"] == "delete"
        if len(cur) + (2 if pair else 1) > 50:
            batches.append(cur)
            cur = []
        cur.append(e)
    if cur:
        batches.append(cur)
    for n, b in enumerate(batches, 1):
        (out / f"batch-{n:02}.json").write_text(json.dumps(b, ensure_ascii=False))
    total = len(steam) + len(family)
    meta = {"lastSyncAt": now, "source": "Steam + " + family_note,
            "counts": {"steam": len(steam), "family": len(family), "processed": len(synced), **stats},
            "partial": bool(a.limit and len(synced) < total)}
    (out / "meta-sync.json").write_text(json.dumps(meta, ensure_ascii=False))
    print(f"\n{len(batches)} lote(s) en {out}")

if __name__ == "__main__":
    main()
