"""
Backups of the café's data: members and their time balances, PCs, sessions,
sales, coin rates, settings and staff — everything in pisonet.db.

A backup is one zip:
    pisonet.db            consistent snapshot (SQLite online-backup API, so it
                          is taken safely while the server keeps running)
    members.csv           readable copy: each member's stored time balance
    active_sessions.csv   readable copy: remaining time on each PC right now
    license.json          the pisonex.com activation (for manual recovery)
    backup.json           version, date, branch, row counts
.env is deliberately NOT included: it holds SECRET_KEY, and a reinstall
generates a fresh one anyway.

Backups live outside the web root, readable by administrators only:
    Windows     %ProgramData%\\Pisonex\\Backups\\server   (the installer locks
                %ProgramData%\\Pisonex\\Backups to Administrators + SYSTEM)
    Linux / Pi  <server folder>/backups                  (chmod 700)
BACKUP_DIR in .env overrides the location.
"""

from __future__ import annotations

import csv
import io
import json
import logging
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import zipfile
from datetime import datetime
from pathlib import Path

from config import settings
from database import engine
from version import __version__

logger = logging.getLogger(__name__)

_NAME_RE = re.compile(r"^pisonex-backup-[A-Za-z0-9_-]+\.zip$")
_REQUIRED_TABLES = {"users", "pcs", "sessions", "coin_transactions"}
MAX_UPLOAD_BYTES = 200 * 1024 * 1024

if getattr(sys, "frozen", False):
    _APP_DIR = Path(sys.executable).parent
else:
    _APP_DIR = Path(__file__).resolve().parent.parent


_locked_down = False


def backup_dir() -> Path:
    global _locked_down
    if settings.BACKUP_DIR:
        d = Path(settings.BACKUP_DIR)
    elif os.name == "nt":
        d = Path(os.environ.get("ProgramData", r"C:\ProgramData")) / "Pisonex" / "Backups" / "server"
    else:
        d = _APP_DIR / "backups"
    d.mkdir(parents=True, exist_ok=True)
    if not _locked_down:
        _lock_down(d.parent if os.name == "nt" and not settings.BACKUP_DIR else d)
        _locked_down = True
    return d


def _lock_down(d: Path) -> None:
    """Backups hold password hashes and the license key: owner/admins only.
    On Windows this mirrors the installer (Administrators + SYSTEM, no /T —
    setting the folder propagates to its contents). Best-effort."""
    try:
        if os.name == "nt":
            # Only the installed service (frozen exe running as SYSTEM). A dev
            # run from source as a normal user would lock ITSELF out.
            if not getattr(sys, "frozen", False):
                return
            import subprocess
            subprocess.run(
                ["icacls", str(d), "/inheritance:r", "/grant:r",
                 "*S-1-5-32-544:(OI)(CI)F", "*S-1-5-18:(OI)(CI)F", "/C", "/Q"],
                capture_output=True, timeout=30,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        else:
            os.chmod(d, 0o700)
    except Exception as e:  # never block a backup over permissions
        logger.warning("Could not restrict permissions on %s: %s", d, e)


def _db_path() -> Path:
    return Path(engine.url.database).resolve()


def _slug(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "-", text).strip("-")[:40] or "cafe"


def valid_name(name: str) -> bool:
    return bool(_NAME_RE.match(name)) and (backup_dir() / name).is_file()


# ── Create ────────────────────────────────────────────────────────────────────

def create_backup(kind: str = "manual") -> dict:
    """kind: manual | auto | before-restore. Returns the backup's info dict."""
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    name = f"pisonex-backup-{_slug(settings.BRANCH_NAME)}-{stamp}-{kind}.zip"
    dest = backup_dir() / name

    with tempfile.TemporaryDirectory() as tmp:
        snap = Path(tmp) / "pisonet.db"
        src = sqlite3.connect(str(_db_path()), timeout=30)
        try:
            out = sqlite3.connect(str(snap))
            try:
                src.backup(out)
            finally:
                out.close()
        finally:
            src.close()

        info = {
            "app_version": __version__,
            "created_at": datetime.now().isoformat(timespec="seconds"),
            "branch_name": settings.BRANCH_NAME,
            "kind": kind,
            **_counts(snap),
        }
        members_csv, sessions_csv = _readable_exports(snap)

        tmp_zip = dest.with_suffix(".zip.part")
        with zipfile.ZipFile(tmp_zip, "w", zipfile.ZIP_DEFLATED) as zf:
            zf.write(snap, "pisonet.db")
            zf.writestr("members.csv", members_csv)
            zf.writestr("active_sessions.csv", sessions_csv)
            zf.writestr("backup.json", json.dumps(info, indent=2))
            lic = _APP_DIR / "data" / "license.json"
            if lic.is_file():
                zf.write(lic, "license.json")
        os.replace(tmp_zip, dest)

    if kind == "auto":
        _prune_auto(settings.BACKUP_KEEP)
    logger.info("Backup created: %s (%d members, %d PCs)", name, info["members"], info["pcs"])
    return {"name": name, "size": dest.stat().st_size, **info}


def _counts(db: Path) -> dict:
    con = sqlite3.connect(str(db))
    try:
        def n(sql: str) -> int:
            try:
                return int(con.execute(sql).fetchone()[0])
            except sqlite3.Error:
                return 0
        return {
            "members": n("SELECT COUNT(*) FROM users"),
            "member_seconds": n("SELECT COALESCE(SUM(balance_seconds), 0) FROM users"),
            "pcs": n("SELECT COUNT(*) FROM pcs"),
            "active_sessions": n("SELECT COUNT(*) FROM sessions WHERE is_active = 1"),
            "transactions": n("SELECT COUNT(*) FROM coin_transactions"),
        }
    finally:
        con.close()


def _hms(seconds: int) -> str:
    seconds = max(0, int(seconds or 0))
    return f"{seconds // 3600}:{seconds % 3600 // 60:02d}:{seconds % 60:02d}"


def _readable_exports(db: Path) -> tuple[str, str]:
    con = sqlite3.connect(str(db))
    con.row_factory = sqlite3.Row
    try:
        m = io.StringIO()
        w = csv.writer(m)
        w.writerow(["username", "balance (h:mm:ss)", "balance_seconds", "active",
                    "logged_in_pc", "last_login_at_utc", "created_at_utc"])
        for r in con.execute(
            "SELECT u.username, u.balance_seconds, u.is_active, p.pc_number, "
            "u.last_login_at, u.created_at FROM users u "
            "LEFT JOIN pcs p ON p.id = u.logged_in_pc_id ORDER BY u.username"
        ):
            w.writerow([r["username"], _hms(r["balance_seconds"]), r["balance_seconds"],
                        "yes" if r["is_active"] else "no", r["pc_number"] or "",
                        r["last_login_at"] or "", r["created_at"] or ""])

        s = io.StringIO()
        w = csv.writer(s)
        w.writerow(["pc_number", "member", "remaining (h:mm:ss)", "remaining_seconds",
                    "granted_seconds", "started_at_utc"])
        now = datetime.utcnow()
        for r in con.execute(
            "SELECT p.pc_number, u.username, s.granted_seconds, s.started_at "
            "FROM sessions s JOIN pcs p ON p.id = s.pc_id "
            "LEFT JOIN users u ON u.id = s.user_id WHERE s.is_active = 1 ORDER BY p.pc_number"
        ):
            remaining = r["granted_seconds"] or 0
            try:
                started = datetime.fromisoformat(str(r["started_at"]))
                remaining = max(0, int(remaining - (now - started).total_seconds()))
            except (TypeError, ValueError):
                pass
            w.writerow([r["pc_number"], r["username"] or "", _hms(remaining), remaining,
                        r["granted_seconds"], r["started_at"] or ""])
        return m.getvalue(), s.getvalue()
    finally:
        con.close()


# ── List / delete ─────────────────────────────────────────────────────────────

def list_backups() -> list[dict]:
    out = []
    for p in sorted(backup_dir().glob("pisonex-backup-*.zip"), reverse=True):
        info: dict = {}
        try:
            with zipfile.ZipFile(p) as zf:
                info = json.loads(zf.read("backup.json"))
        except (zipfile.BadZipFile, KeyError, ValueError, OSError):
            info = {"kind": "unknown"}
        out.append({"name": p.name, "size": p.stat().st_size,
                    "modified": datetime.fromtimestamp(p.stat().st_mtime), **info})
    return out


def delete_backup(name: str) -> bool:
    if not valid_name(name):
        return False
    (backup_dir() / name).unlink()
    return True


def _prune_auto(keep: int) -> None:
    autos = sorted(backup_dir().glob("pisonex-backup-*-auto.zip"), reverse=True)
    for old in autos[max(1, keep):]:
        try:
            old.unlink()
        except OSError as e:
            logger.warning("Could not prune old backup %s: %s", old.name, e)


def latest_auto_age_hours() -> float | None:
    autos = sorted(backup_dir().glob("pisonex-backup-*-auto.zip"), key=lambda p: p.stat().st_mtime)
    if not autos:
        return None
    return (datetime.now().timestamp() - autos[-1].stat().st_mtime) / 3600


# ── Restore ───────────────────────────────────────────────────────────────────

def _extract_db(source: Path, workdir: Path) -> Path:
    """Accepts a backup .zip or a bare pisonet.db; returns a validated DB path."""
    db = workdir / "restore.db"
    if zipfile.is_zipfile(source):
        with zipfile.ZipFile(source) as zf:
            if "pisonet.db" not in zf.namelist():
                raise ValueError("This zip is not a Pisonex backup (no pisonet.db inside).")
            with zf.open("pisonet.db") as fin, open(db, "wb") as fout:
                shutil.copyfileobj(fin, fout)
    else:
        shutil.copyfile(source, db)

    try:
        con = sqlite3.connect(str(db))
        try:
            ok = con.execute("PRAGMA integrity_check").fetchone()[0]
            tables = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        finally:
            con.close()
    except sqlite3.DatabaseError:
        raise ValueError("The file is not a valid database.")
    if ok != "ok":
        raise ValueError(f"The backup database is damaged ({ok}).")
    missing = _REQUIRED_TABLES - tables
    if missing:
        raise ValueError(f"Not a Pisonex database (missing: {', '.join(sorted(missing))}).")
    return db


def restore(source: Path) -> dict:
    """Replace the live database with the one in `source` (zip or .db).
    A 'before-restore' backup of the current data is always taken first."""
    with tempfile.TemporaryDirectory() as tmp:
        new_db = _extract_db(source, Path(tmp))
        safety = create_backup("before-restore")

        # Copy page-by-page INTO the live database with the online-backup API,
        # so open connections never see a half-written file; then drop pooled
        # connections so every request reads the restored data.
        src = sqlite3.connect(str(new_db))
        dst = sqlite3.connect(str(_db_path()), timeout=30)
        try:
            dst.execute("PRAGMA busy_timeout = 30000")
            src.backup(dst)
        finally:
            src.close()
            dst.close()
        engine.dispose()

    # A backup from an older version gets the same schema upgrades as a boot.
    import main
    from database import Base, SessionLocal
    from models import ServerConfig
    from services.membership_service import MembershipService
    main._migrate_schema()
    Base.metadata.create_all(bind=engine)
    db = SessionLocal()
    try:
        main._seed_defaults(db)
        srv = db.query(ServerConfig).first()
        settings.CLIENT_API_KEY = (srv.client_api_key if srv else "") or ""
        MembershipService(db).rebuild_bindings()
    finally:
        db.close()

    counts = _counts(_db_path())
    logger.warning("Database RESTORED from backup (%d members); previous data saved as %s",
                   counts["members"], safety["name"])
    return {"safety_backup": safety["name"], **counts}
