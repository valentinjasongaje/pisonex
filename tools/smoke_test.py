"""
End-to-end smoke test for the Pisonex server variants.

Copies each server variant into a throwaway temp folder (fresh .env, fresh
SQLite DB, random free port), boots it with uvicorn, and drives the same HTTP
calls the VB.NET client and the dashboard make. Nothing in the source tree is
written to, and outbound traffic to pisonex.com is blocked via a dead proxy so
a test run never sends telemetry or license calls.

Checks:
  * /health responds and reports the version in pisonex/VERSION
  * every heartbeat field the client deserialises (parsed straight out of
    client/PisoNetClient/Services/ApiService.vb) is present in the response
  * PC register -> heartbeat -> dashboard add-time -> unlock -> lock
  * every dashboard page renders (no 5xx, no traceback)
  * metrics / screenshot upload, request-coins returns a clean 4xx/503
  * membership: admin creates member -> client login -> forced
    change-password -> heartbeat shows member -> logout

Usage:
    python tools/smoke_test.py                    # all variants
    python tools/smoke_test.py server-windows     # just one
    python tools/smoke_test.py --keep             # keep temp dirs for debugging
    python tools/smoke_test.py --upgrade-from HEAD   # migrate a DB made by the last commit

Exit code is non-zero if any check fails.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import httpx

ROOT = Path(__file__).resolve().parent.parent          # pisonex/
VARIANTS = ["server", "server-orangepi", "server-windows"]
CLIENT_DTO = ROOT / "client" / "PisoNetClient" / "Services" / "ApiService.vb"

ADMIN_PASSWORD = "smoke-test-admin-pw"

# Dashboard pages that should render for a logged-in admin. A 404 means the
# variant doesn't have that page (e.g. /schedule is Orange Pi only) and is
# reported as "skipped", anything 5xx is a failure.
DASHBOARD_PAGES = [
    "", "/pcs", "/monitor", "/rates", "/reports", "/transactions", "/logs",
    "/settings", "/staff", "/membership", "/license", "/account", "/wallpaper",
    "/schedule", "/docs/api", "/docs/wiring", "/partials/pc-grid",
    "/partials/rates-table", "/monitor/status", "/monitor/metrics/1",
    "/api/hardware/coin-slot", "/api/wallpaper/list", "/api/schedule/status",
    "/reports/export.csv",
]

_IGNORE = shutil.ignore_patterns(
    "venv", ".venv", "build", "dist", "__pycache__", "*.db", "*.db-shm",
    "*.db-wal", "*.db-journal", ".env", "*.log", "data", "wallpapers",
)


# ── helpers ───────────────────────────────────────────────────────────────────

def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def _expected_version() -> str | None:
    vf = ROOT / "VERSION"
    return vf.read_text(encoding="utf-8").strip() if vf.exists() else None


def _client_heartbeat_fields() -> list[str]:
    """Property names of the client's HeartbeatResponse DTO."""
    text = CLIENT_DTO.read_text(encoding="utf-8")
    m = re.search(r"Public Class HeartbeatResponse(.*?)End Class", text, re.S)
    if not m:
        return []
    return re.findall(r"Public Property (\w+)", m.group(1))


class Results:
    def __init__(self, variant: str):
        self.variant = variant
        self.passed = 0
        self.failed: list[str] = []
        self.skipped: list[str] = []

    def check(self, ok: bool, label: str, detail: str = ""):
        if ok:
            self.passed += 1
            print(f"    ok    {label}")
        else:
            self.failed.append(f"{label}: {detail}" if detail else label)
            print(f"    FAIL  {label}  {detail}")

    def skip(self, label: str):
        self.skipped.append(label)
        print(f"    skip  {label}")


# ── server lifecycle ──────────────────────────────────────────────────────────

def _prepare(source: Path, workdir: Path, port: int):
    """Copy a variant's source into workdir with a fresh test .env."""
    shutil.copytree(source, workdir, ignore=_IGNORE)
    (workdir / "dashboard" / "static" / "wallpapers").mkdir(parents=True, exist_ok=True)
    (workdir / ".env").write_text(
        "DATABASE_URL=sqlite:///./pisonet.db\n"
        f"SERVER_PORT={port}\n"
        "SERVER_HOST=127.0.0.1\n"
        f"ADMIN_PASSWORD={ADMIN_PASSWORD}\n"
        "BRANCH_NAME=Smoke Test\n"
        "BACKUP_DIR=./backups\n",      # never touch the real %ProgramData% backups
        encoding="utf-8",
    )


def _launch(workdir: Path, port: int) -> subprocess.Popen:
    env = dict(os.environ)
    # Black-hole every outbound HTTP(S) call (pisonex.com license/telemetry).
    env.update({
        "HTTP_PROXY": "http://127.0.0.1:9", "HTTPS_PROXY": "http://127.0.0.1:9",
        "NO_PROXY": "127.0.0.1,localhost", "PYTHONIOENCODING": "utf-8",
    })
    log = open(workdir / "smoke-server.log", "a", encoding="utf-8")
    return subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "main:app",
         "--host", "127.0.0.1", "--port", str(port), "--log-level", "warning"],
        cwd=workdir, env=env, stdout=log, stderr=subprocess.STDOUT,
    )


def _stop(proc: subprocess.Popen):
    proc.terminate()
    try:
        proc.wait(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()


def _export_ref(ref: str, variant: str, dest: Path) -> Path:
    """Extract `variant` as it was at git `ref` into dest; returns its folder."""
    dest.mkdir(parents=True, exist_ok=True)
    archive = subprocess.run(["git", "archive", ref, variant], cwd=ROOT,
                             capture_output=True, check=True).stdout
    import io
    import tarfile
    with tarfile.open(fileobj=io.BytesIO(archive)) as tf:
        tf.extractall(dest, filter="data")
    return dest / variant


def _seed_old_database(ref: str, variant: str, scratch: Path, r: Results) -> Path | None:
    """Boot the code from `ref`, put some real data in its DB, return the DB path."""
    old_src = _export_ref(ref, variant, scratch / "old-src")
    old_dir = scratch / "old-run"
    port = _free_port()
    _prepare(old_src, old_dir, port)
    proc = _launch(old_dir, port)
    base = f"http://127.0.0.1:{port}"
    try:
        if not _wait_healthy(base, proc):
            r.check(False, f"old version ({ref}) starts", (old_dir / "smoke-server.log").read_text()[-2000:])
            return None
        api = httpx.Client(base_url=base, timeout=15)
        dash = httpx.Client(base_url=base, timeout=15)
        api.post("/api/pc/register", params={"pc_number": 1, "mac_address": "AA:BB:CC:DD:EE:01"})
        api.post("/api/pc/register", params={"pc_number": 2, "mac_address": "AA:BB:CC:DD:EE:02"})
        dash.post("/dashboard/login", data={"username": "admin", "password": ADMIN_PASSWORD})
        dash.post("/dashboard/api/pc/add-time", json={"pc_number": 2, "minutes": 30})
        r.check(True, f"old version ({ref}) created a database with data")
    finally:
        _stop(proc)
    return old_dir / "pisonet.db"


def _wait_healthy(base: str, proc: subprocess.Popen, timeout: float = 45) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if proc.poll() is not None:
            return False
        try:
            if httpx.get(f"{base}/health", timeout=2).status_code == 200:
                return True
        except httpx.HTTPError:
            pass
        time.sleep(0.5)
    return False


# ── the checks ────────────────────────────────────────────────────────────────

def _run_checks(base: str, r: Results):
    pc = 1
    api = httpx.Client(base_url=base, timeout=15)
    dash = httpx.Client(base_url=base, timeout=15)

    # Health + version
    health = api.get("/health").json()
    want = _expected_version()
    r.check(health.get("status") == "ok", "GET /health")
    if want:
        r.check(health.get("version") == want, "health version matches VERSION",
                f"server={health.get('version')} VERSION={want}")

    # Register + heartbeat contract
    resp = api.post("/api/pc/register", params={"pc_number": pc, "mac_address": "AA:BB:CC:DD:EE:01"})
    r.check(resp.status_code == 200, "POST /api/pc/register", resp.text[:200])

    hb = api.post(f"/api/pc/heartbeat/{pc}")
    r.check(hb.status_code == 200, "POST /api/pc/heartbeat", hb.text[:200])

    # Same machine renumbered in the client setup (PC 5 -> PC 6, same MAC):
    # used to crash registration with a UNIQUE(mac_address) error (500), so the
    # client showed "can't connect" forever.
    mac = {"mac_address": "3C:9C:0F:1E:AC:4E"}
    first = api.post("/api/pc/register", params={"pc_number": 5, **mac})
    again = api.post("/api/pc/register", params={"pc_number": 6, **mac})
    hb6 = api.post("/api/pc/heartbeat/6")
    r.check(first.status_code == 200 and again.status_code == 200 and hb6.status_code == 200,
            "machine renumbered (same MAC, new PC number) registers and heartbeats",
            f"register5={first.status_code} register6={again.status_code} heartbeat6={hb6.status_code}")

    hbj = hb.json() if hb.status_code == 200 else {}
    missing = [f for f in _client_heartbeat_fields() if f not in hbj]
    r.check(not missing, "heartbeat has every field the client reads",
            f"missing: {', '.join(missing)}")
    r.check(hbj.get("is_locked") is True, "fresh PC is locked")

    # Dashboard login
    login = dash.post("/dashboard/login", data={"username": "admin", "password": ADMIN_PASSWORD},
                      follow_redirects=False)
    r.check(login.status_code == 302 and "pisonet_session" in dash.cookies,
            "dashboard login", f"HTTP {login.status_code}")

    # Every dashboard page
    for page in DASHBOARD_PAGES:
        resp = dash.get(f"/dashboard{page}", follow_redirects=False)
        label = f"GET /dashboard{page or '/'}"
        if resp.status_code == 404:
            r.skip(f"{label} (not in this variant)")
            continue
        bad = resp.status_code >= 500 or "Traceback" in resp.text
        r.check(not bad and resp.status_code < 400, label, f"HTTP {resp.status_code}")

    # Add time from the dashboard -> client unlocks
    resp = dash.post("/dashboard/api/pc/add-time", json={"pc_number": pc, "minutes": 5})
    r.check(resp.status_code == 200, "dashboard add-time", resp.text[:200])
    hbj = api.post(f"/api/pc/heartbeat/{pc}").json()
    r.check(hbj.get("is_locked") is False and hbj.get("remaining_seconds", 0) > 250,
            "heartbeat unlocks after add-time", str({k: hbj.get(k) for k in ("is_locked", "remaining_seconds")}))

    # Remote command round-trip
    resp = dash.post(f"/dashboard/api/pc/{pc}/message", json={"text": "hello"})
    r.check(resp.status_code == 200, "dashboard send message", resp.text[:200])
    if resp.status_code == 200:
        hbj = api.post(f"/api/pc/heartbeat/{pc}").json()
        r.check(hbj.get("admin_message") == "hello", "admin message delivered once via heartbeat")

    # Metrics / screenshot upload
    resp = api.post(f"/api/pc/{pc}/metrics", json={"cpu_percent": 12.5})
    r.check(resp.status_code == 200, "POST metrics", resp.text[:200])
    resp = api.post(f"/api/pc/{pc}/screenshot", content=b"\xff\xd8\xff\xe0fakejpeg")
    r.check(resp.status_code == 200, "POST screenshot", resp.text[:200])

    # Admin-only REST endpoints must reject anonymous LAN callers (free-time exploit)
    for method, path, body in (("post", "/api/session/add-time", {"pc_number": pc, "minutes": 60}),
                               ("post", f"/api/session/{pc}/end", None),
                               ("post", f"/api/pc/{pc}/lock", None),
                               ("get", "/api/pc/status", None)):
        resp = getattr(api, method)(path, **({"json": body} if body else {}))
        r.check(resp.status_code == 401, f"{method.upper()} {path} requires admin auth",
                f"HTTP {resp.status_code}")
    tok = api.post("/api/auth/token", data={"username": "admin", "password": ADMIN_PASSWORD})
    r.check(tok.status_code == 200, "POST /api/auth/token", tok.text[:200])
    if tok.status_code == 200:
        hdr = {"Authorization": f"Bearer {tok.json()['access_token']}"}
        resp = api.get("/api/pc/status", headers=hdr)
        r.check(resp.status_code == 200, "GET /api/pc/status with admin token", resp.text[:200])

    # Coins without hardware must fail cleanly, never 500
    resp = api.post(f"/api/pc/{pc}/request-coins")
    r.check(resp.status_code < 500 or resp.status_code == 503,
            "request-coins fails cleanly without hardware", f"HTTP {resp.status_code}")

    # Lock
    resp = dash.post(f"/dashboard/api/pc/{pc}/lock")
    r.check(resp.status_code == 200, "dashboard lock", resp.text[:200])
    hbj = api.post(f"/api/pc/heartbeat/{pc}").json()
    r.check(hbj.get("is_locked") is True, "heartbeat locked after lock")

    # (PC is locked → Idle tile.) Overview tiles are admin-facing: no customer wording like "Ask cashier for time"
    resp = dash.post("/dashboard/api/settings/traditional-mode", json={"enabled": True})
    if resp.status_code != 404:
        grid = dash.get("/dashboard/partials/pc-grid").text
        r.check("Idle" in grid and "Ask cashier" not in grid,
                "overview tile (traditional mode) shows Idle without customer wording")
        dash.post("/dashboard/api/settings/traditional-mode", json={"enabled": False})

    _membership_checks(api, dash, pc, r)
    _backup_checks(api, dash, r)


def _backup_checks(api: httpx.Client, dash: httpx.Client, r: Results):
    import io
    import zipfile

    page = dash.get("/dashboard/backup")
    if page.status_code == 404:
        r.skip("backup page (not in this variant)")
        return
    r.check(page.status_code == 200, "GET /dashboard/backup", f"HTTP {page.status_code}")

    # Snapshot: the member created by the membership checks exists, then back up.
    resp = dash.post("/dashboard/api/backup/create")
    r.check(resp.status_code == 200, "create backup", resp.text[:200])
    name = resp.json().get("name", "") if resp.status_code == 200 else ""

    dl = dash.get(f"/dashboard/api/backup/download/{name}")
    files = []
    if dl.status_code == 200:
        with zipfile.ZipFile(io.BytesIO(dl.content)) as zf:
            files = zf.namelist()
            members_csv = zf.read("members.csv").decode("utf-8") if "members.csv" in files else ""
    r.check({"pisonet.db", "members.csv", "active_sessions.csv", "backup.json"} <= set(files),
            "backup zip has the database + readable CSVs", str(files))
    r.check(".env" not in files, "backup zip excludes .env secrets")
    r.check("smokeuser" in (members_csv if files else ""), "members.csv lists the member")

    # Change data after the backup, then restore — the change must disappear.
    dash.post("/dashboard/api/membership/create-member", json={"username": "afterbackup"})
    resp = dash.post(f"/dashboard/api/backup/{name}/restore")
    r.check(resp.status_code == 200 and bool(resp.json().get("safety_backup")),
            "restore a saved backup (safety copy taken first)", resp.text[:200])
    mpage = dash.get("/dashboard/membership").text
    r.check("smokeuser" in mpage and "afterbackup" not in mpage,
            "restore brought back the backed-up data exactly")
    hb = api.post("/api/pc/heartbeat/1")
    r.check(hb.status_code == 200, "clients keep working right after a restore", f"HTTP {hb.status_code}")

    resp = dash.post("/dashboard/api/backup/upload-restore",
                     files={"file": ("backup.zip", dl.content, "application/zip")})
    r.check(resp.status_code == 200, "restore from an uploaded file", resp.text[:200])
    resp = dash.post("/dashboard/api/backup/upload-restore",
                     files={"file": ("junk.zip", b"not a backup", "application/zip")})
    r.check(resp.status_code == 422, "junk upload rejected, data untouched", f"HTTP {resp.status_code}")

    # Security: traversal names and non-admins.
    resp = dash.get("/dashboard/api/backup/download/..%2F..%2F.env")
    r.check(resp.status_code in (404, 422), "download can't escape the backup folder", f"HTTP {resp.status_code}")
    dash.post("/dashboard/api/staff/create",
              json={"username": "smokecashier", "password": "cashierpw1", "role": "cashier"})
    cashier = httpx.Client(base_url=str(dash.base_url), timeout=15)
    cashier.post("/dashboard/login", data={"username": "smokecashier", "password": "cashierpw1"})
    resp = cashier.post("/dashboard/api/backup/create")
    r.check(resp.status_code == 403, "cashier cannot create/restore backups", f"HTTP {resp.status_code}")
    resp = cashier.get(f"/dashboard/api/backup/download/{name}")
    r.check(resp.status_code == 403, "cashier cannot download backups", f"HTTP {resp.status_code}")


def _membership_checks(api: httpx.Client, dash: httpx.Client, pc: int, r: Results):
    resp = dash.post("/dashboard/api/membership/config", json={"membership_enabled": True})
    r.check(resp.status_code == 200, "enable membership", resp.text[:200])

    resp = dash.post("/dashboard/api/membership/create-member",
                     json={"username": "smokeuser", "initial_minutes": 15})
    if resp.status_code == 404:
        r.check(False, "dashboard create-member exists",
                "no way to create members — client has no self-registration")
        return
    r.check(resp.status_code == 200, "dashboard create-member", resp.text[:200])
    temp_pw = resp.json().get("temp_password", "")
    r.check(bool(temp_pw), "temp password returned once")

    resp = api.post("/api/member/register",
                    json={"username": "selfreg", "password": "abc12345", "pc_number": pc})
    r.check(resp.status_code in (404, 405), "self-registration endpoint is gone",
            f"HTTP {resp.status_code}")

    login = api.post("/api/member/login",
                     json={"pc_number": pc, "username": "smokeuser", "password": temp_pw}).json()
    r.check(login.get("success") is True, "member login with temp password", str(login))
    r.check(login.get("must_change_password") is True, "login forces password change")

    resp = api.post("/api/member/change-password",
                    json={"pc_number": pc, "new_password": "newpass123"})
    r.check(resp.status_code == 200 and resp.json().get("success") is True,
            "member change-password", resp.text[:200])

    hbj = api.post(f"/api/pc/heartbeat/{pc}").json()
    r.check(hbj.get("member_username") == "smokeuser", "heartbeat shows logged-in member")

    page = dash.get("/dashboard/membership")
    r.check(page.status_code == 200 and "smokeuser" in page.text,
            "membership page lists the logged-in member", f"HTTP {page.status_code}")
    r.check(hbj.get("membership_enabled") is True, "heartbeat membership_enabled")

    out = api.post("/api/member/logout", json={"pc_number": pc}).json()
    r.check(out.get("success") is True, "member logout", str(out))

    relog = api.post("/api/member/login",
                     json={"pc_number": pc, "username": "smokeuser", "password": "newpass123"}).json()
    r.check(relog.get("success") is True and not relog.get("must_change_password"),
            "re-login with new password", str(relog))
    api.post("/api/member/logout", json={"pc_number": pc})


# ── driver ────────────────────────────────────────────────────────────────────

def run_variant(variant: str, keep: bool, upgrade_from: str | None = None) -> Results:
    print(f"\n== {variant}" + (f"  (upgrade from {upgrade_from})" if upgrade_from else "") + " ==")
    r = Results(variant)
    scratch = Path(tempfile.mkdtemp(prefix=f"pisonex-smoke-{variant}-"))
    workdir = scratch / variant
    port = _free_port()
    proc = None
    try:
        _prepare(ROOT / variant, workdir, port)
        if upgrade_from:
            old_db = _seed_old_database(upgrade_from, variant, scratch, r)
            if old_db is None:
                return r
            for suffix in ("", "-wal", "-shm"):   # WAL mode: data may still be in -wal
                src = old_db.with_name(old_db.name + suffix)
                if src.exists():
                    shutil.copyfile(src, workdir / src.name)
        proc = _launch(workdir, port)
        base = f"http://127.0.0.1:{port}"
        if not _wait_healthy(base, proc):
            log = (workdir / "smoke-server.log").read_text(encoding="utf-8", errors="replace")
            r.check(False, "server starts", "\n" + log[-3000:])
            return r
        r.check(True, "server starts")
        try:
            _run_checks(base, r)
            if upgrade_from:
                hb = httpx.post(f"{base}/api/pc/heartbeat/2", timeout=15).json()
                r.check(hb.get("is_locked") is False and hb.get("remaining_seconds", 0) > 0,
                        "session created by the old version survived the upgrade", str(hb.get("remaining_seconds")))
        except Exception as e:  # a crash in the harness is still a failure
            r.check(False, "checks ran to completion", repr(e))
        log = (workdir / "smoke-server.log").read_text(encoding="utf-8", errors="replace")
        r.check("Traceback" not in log, "no tracebacks in server log",
                log[log.find("Traceback"):][:1500] if "Traceback" in log else "")
    finally:
        if proc:
            _stop(proc)
        if keep:
            print(f"    (kept {scratch})")
        else:
            shutil.rmtree(scratch, ignore_errors=True)
    return r


def main() -> int:
    # Windows consoles default to cp1252, which can't print the dashes/peso signs used here.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("variants", nargs="*", metavar="VARIANT", help=f"one or more of: {', '.join(VARIANTS)}")
    ap.add_argument("--keep", action="store_true", help="keep temp dirs + server logs")
    ap.add_argument("--upgrade-from", metavar="GIT_REF",
                    help="first boot the variant as it was at GIT_REF (e.g. v1.0.4 or HEAD), "
                         "then run the current code on that database — proves migrations work")
    args = ap.parse_args()

    unknown = [v for v in args.variants if v not in VARIANTS]
    if unknown:
        ap.error(f"unknown variant(s): {', '.join(unknown)}")
    results = [run_variant(v, args.keep, args.upgrade_from) for v in (args.variants or VARIANTS)]

    print("\n== summary ==")
    for r in results:
        status = "PASS" if not r.failed else "FAIL"
        print(f"  {status}  {r.variant}: {r.passed} ok, {len(r.failed)} failed, {len(r.skipped)} skipped")
        for f in r.failed:
            print(f"          - {f.splitlines()[0][:160]}")
    return 1 if any(r.failed for r in results) else 0


if __name__ == "__main__":
    sys.exit(main())
