import asyncio
import logging
import logging.handlers
import os
import sys
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.responses import RedirectResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from fastapi.middleware.cors import CORSMiddleware

from config import settings
from database import engine, Base, SessionLocal
from models import AdminUser, CoinRate, MembershipConfig, ServerConfig
from api import auth, pc, sessions, admin
from api.license import router as license_router
from api.member import router as member_router
from dashboard.routes import router as dashboard_router
from api.auth import hash_password
from services.session_service import SessionService
from services.license_service import LicenseService
from services.membership_service import MembershipService

# ── Logging setup ─────────────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    handlers=[
        logging.StreamHandler(),
        logging.handlers.RotatingFileHandler(
            "pisonet.log",
            maxBytes=5 * 1024 * 1024,  # 5 MB
            backupCount=3,
        ),
    ],
)
# Resolve the directory that contains bundled assets (templates, static files).
# When frozen by PyInstaller onedir, assets are in sys._MEIPASS (_internal/),
# not next to the exe. PISONEX_BUNDLE_DIR is set by windows_service.py before import.
_BUNDLE_DIR = Path(os.environ.get('PISONEX_BUNDLE_DIR', Path(__file__).parent))

logger = logging.getLogger(__name__)

# ── App lifespan (startup / shutdown) ─────────────────────────────────────────

hw_controller = None
license_service: LicenseService = None


@asynccontextmanager
def _ensure_firewall_rule():
    """On Windows, add an inbound firewall rule for the server port (idempotent)."""
    if sys.platform != "win32":
        return
    import subprocess
    port = settings.SERVER_PORT
    rule_name = f"Pisonex Server (TCP {port})"
    try:
        check = subprocess.run(
            ["netsh", "advfirewall", "firewall", "show", "rule", f"name={rule_name}"],
            capture_output=True, text=True,
        )
        if check.returncode == 0:
            return  # rule already exists
        result = subprocess.run(
            ["netsh", "advfirewall", "firewall", "add", "rule",
             f"name={rule_name}", "dir=in", "action=allow",
             "protocol=TCP", f"localport={port}"],
            capture_output=True, text=True,
        )
        if result.returncode == 0:
            logger.info("Firewall rule added: %s", rule_name)
        else:
            logger.warning("Could not add firewall rule (not running as admin?): %s", result.stderr.strip())
    except Exception as e:
        logger.warning("Firewall rule check failed: %s", e)


_DEFAULT_SECRET = "change-this-to-a-random-256-bit-secret-key"
_DEFAULT_HMAC = "PISONEX-INTERNAL-2026-CHANGE-BEFORE-RELEASE"
def _enforce_secure_defaults():
    """Auto-generate SECRET_KEY and LICENSE_HMAC_SECRET if they are still the
    insecure defaults.  Writes the new values into .env so they persist
    across restarts.  This runs once on first startup and is safe to
    re-run (it only acts when the defaults are detected)."""
    import secrets
    env_path = Path(__file__).parent / ".env"

    changed = False
    lines: list[str] = []
    if env_path.exists():
        lines = env_path.read_text(encoding="utf-8").splitlines(keepends=True)

    def _replace_or_append(key: str, value: str):
        nonlocal changed, lines
        found = False
        for i, line in enumerate(lines):
            stripped = line.lstrip()
            if stripped.startswith(f"{key}="):
                lines[i] = f"{key}={value}\n"
                found = True
                break
        if not found:
            lines.append(f"{key}={value}\n")
        changed = True

    if settings.SECRET_KEY == _DEFAULT_SECRET:
        new_key = secrets.token_hex(32)  # 256-bit random key
        _replace_or_append("SECRET_KEY", new_key)
        settings.SECRET_KEY = new_key
        logger.warning("SECRET_KEY was the default — generated a secure random key and saved to .env")

    if settings.LICENSE_HMAC_SECRET == _DEFAULT_HMAC:
        new_hmac = secrets.token_hex(32)  # 256-bit random HMAC key
        _replace_or_append("LICENSE_HMAC_SECRET", new_hmac)
        settings.LICENSE_HMAC_SECRET = new_hmac
        logger.warning("LICENSE_HMAC_SECRET was the default — generated a secure key and saved to .env")

    if changed:
        env_path.write_text("".join(lines), encoding="utf-8")


async def lifespan(app: FastAPI):
    global hw_controller, license_service

    # Auto-generate SECRET_KEY and LICENSE_HMAC_SECRET if still at insecure defaults
    _enforce_secure_defaults()

    # Initialize license service and sync startup status (telemetry + trial-clock restore)
    license_service = LicenseService()
    await license_service.sync_startup_status()
    logger.info("License status: %s", license_service.get_status()["status"])

    # Migrate existing DB columns (v2 → v3 seconds-based rename)
    _migrate_schema()

    # Ensure Windows Firewall allows inbound connections on the server port
    _ensure_firewall_rule()

    # Create all DB tables (creates new tables like membership_config)
    Base.metadata.create_all(bind=engine)

    db = SessionLocal()
    try:
        _seed_defaults(db)
        # Load client API key from DB into settings so dependencies.py picks it up
        srv_cfg = db.query(ServerConfig).first()
        if srv_cfg and srv_cfg.client_api_key:
            settings.CLIENT_API_KEY = srv_cfg.client_api_key
            logger.info("Client API key loaded from database (auth enabled)")
        else:
            settings.CLIENT_API_KEY = ""
            logger.info("Client API key not set — client auth disabled")
    finally:
        db.close()

    # Rebuild member-PC bindings from DB
    db = SessionLocal()
    try:
        msvc = MembershipService(db)
        msvc.rebuild_bindings()
    finally:
        db.close()

    # Start hardware controller (only works on actual Raspberry Pi)
    try:
        db = SessionLocal()
        svc = SessionService(db)
        from hardware.controller import HardwareController
        hw_controller = HardwareController(svc)
        logger.info("Hardware controller started")
    except Exception as e:
        logger.warning("Hardware controller not started: %s", e)
        hw_controller = None

    # Ensure wallpapers directory exists and restore last active wallpaper
    _init_wallpapers()

    # Background task: expire sessions every 30 seconds
    expire_task = asyncio.create_task(_session_expiry_loop())

    # Background task: verify license every 6 hours
    license_task = asyncio.create_task(_license_verify_loop())

    # Background task: membership auto-expiry (heartbeat timeout, zero-time, idle shutdown)
    membership_task = asyncio.create_task(_membership_expiry_loop())

    # Background task: nightly earnings archive to pisonex.com
    earnings_task = asyncio.create_task(_nightly_earnings_sync_loop())

    yield

    # Shutdown
    expire_task.cancel()
    license_task.cancel()
    membership_task.cancel()
    earnings_task.cancel()
    if hw_controller:
        hw_controller.cleanup()
    logger.info("Pisonex server shut down")


def _migrate_schema():
    """Rename columns from v2 (minutes-based) to v3 (seconds-based) if needed.

    Safe to run repeatedly — each rename is guarded by a column-existence check.
    SQLite ≥ 3.25.0 supports ALTER TABLE RENAME COLUMN.
    """
    import sqlite3
    from config import settings

    db_path = settings.DATABASE_URL.replace("sqlite:///./", "")
    if not db_path or not __import__("os").path.exists(db_path):
        return  # Fresh install — nothing to migrate

    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()

    def has_column(table: str, column: str) -> bool:
        cursor.execute(f"PRAGMA table_info({table})")
        return any(row[1] == column for row in cursor.fetchall())

    renames = [
        # (table, old_column, new_column)
        ("users", "pin", "password_hash"),
        ("users", "balance_min", "balance_seconds"),
        ("sessions", "minutes_granted", "granted_seconds"),
        ("sessions", "minutes_used", "used_seconds"),
        ("coin_transactions", "amount_pesos", "amount_php"),
        ("coin_transactions", "minutes_added", "seconds_added"),
        ("coin_rates", "minutes", "seconds"),
    ]

    migrated = []
    for table, old_col, new_col in renames:
        if has_column(table, old_col) and not has_column(table, new_col):
            cursor.execute(f"ALTER TABLE {table} RENAME COLUMN {old_col} TO {new_col}")
            migrated.append(f"{table}.{old_col} → {new_col}")

    # Add new columns to users table if missing
    new_user_columns = [
        ("logged_in_pc_id", "INTEGER"),
        ("last_login_at", "DATETIME"),
        ("last_activity_at", "DATETIME"),
    ]
    for col_name, col_type in new_user_columns:
        if not has_column("users", col_name):
            cursor.execute(f"ALTER TABLE users ADD COLUMN {col_name} {col_type}")
            migrated.append(f"users.{col_name} (added)")

    # Add role column to admin_users if missing
    if not has_column("admin_users", "role"):
        cursor.execute("ALTER TABLE admin_users ADD COLUMN role VARCHAR(20) NOT NULL DEFAULT 'admin'")
        migrated.append("admin_users.role (added)")

    # Add new columns to membership_config if missing
    new_membership_columns = [
        ("preset_amounts_enabled", "INTEGER"),
    ]
    for col_name, col_type in new_membership_columns:
        if not has_column("membership_config", col_name):
            cursor.execute(f"ALTER TABLE membership_config ADD COLUMN {col_name} {col_type} DEFAULT 0")
            migrated.append(f"membership_config.{col_name} (added)")

    # Convert existing minutes values to seconds where applicable
    if "sessions.minutes_granted → granted_seconds" in migrated:
        cursor.execute("UPDATE sessions SET granted_seconds = granted_seconds * 60 WHERE granted_seconds > 0")
        cursor.execute("UPDATE sessions SET used_seconds = used_seconds * 60 WHERE used_seconds > 0")
        migrated.append("sessions: converted minutes → seconds values")

    if "coin_transactions.minutes_added → seconds_added" in migrated:
        cursor.execute("UPDATE coin_transactions SET seconds_added = seconds_added * 60 WHERE seconds_added > 0")
        migrated.append("coin_transactions: converted minutes → seconds values")

    if "coin_rates.minutes → seconds" in migrated:
        cursor.execute("UPDATE coin_rates SET seconds = seconds * 60 WHERE seconds > 0")
        migrated.append("coin_rates: converted minutes → seconds values")

    if "users.balance_min → balance_seconds" in migrated:
        cursor.execute("UPDATE users SET balance_seconds = balance_seconds * 60 WHERE balance_seconds > 0")
        migrated.append("users: converted minutes → seconds values")

    conn.commit()
    conn.close()

    if migrated:
        logger.info("Schema migration (v2→v3): %s", ", ".join(migrated))
    else:
        logger.debug("Schema migration: no changes needed")


def _seed_defaults(db):
    """Insert default admin user, coin rate, and membership config on first run."""
    existing_admin = db.query(AdminUser).first()
    if not existing_admin:
        admin_user = AdminUser(
            username=settings.ADMIN_USERNAME,
            password=hash_password(settings.ADMIN_PASSWORD),
        )
        db.add(admin_user)
        logger.info("Created default admin user: %s", settings.ADMIN_USERNAME)

    if not db.query(CoinRate).first():
        rate = CoinRate(
            pesos=settings.DEFAULT_RATE_PESOS,
            seconds=settings.DEFAULT_RATE_SECONDS,
            label=f"₱{settings.DEFAULT_RATE_PESOS} = {settings.DEFAULT_RATE_SECONDS // 60} minutes",
        )
        db.add(rate)
        logger.info(
            "Created default coin rate: ₱%d = %ds (%d min)",
            settings.DEFAULT_RATE_PESOS,
            settings.DEFAULT_RATE_SECONDS,
            settings.DEFAULT_RATE_SECONDS // 60,
        )

    if not db.query(MembershipConfig).first():
        cfg = MembershipConfig(
            id=1,
            membership_enabled=settings.MEMBERSHIP_ENABLED,
            absorption_enabled=settings.ABSORPTION_ENABLED,
            logout_deduction_minutes=settings.LOGOUT_DEDUCTION_MINUTES,
            minimum_logout_minutes=settings.MINIMUM_LOGOUT_MINUTES,
            zero_time_auto_logout_seconds=settings.ZERO_TIME_AUTO_LOGOUT_SECONDS,
            idle_auto_shutdown_minutes=settings.IDLE_AUTO_SHUTDOWN_MINUTES,
            member_heartbeat_timeout_minutes=settings.MEMBER_HEARTBEAT_TIMEOUT_MINUTES,
        )
        db.add(cfg)
        logger.info("Created default membership config")

    if not db.query(ServerConfig).first():
        db.add(ServerConfig(id=1, client_api_key=""))
        logger.info("Created default server config (client auth disabled)")

    db.commit()


def _init_wallpapers():
    """Create wallpapers directory and restore last active wallpaper from disk."""
    import os
    import hashlib
    import command_store

    wp_dir = str(_BUNDLE_DIR / "dashboard" / "static" / "wallpapers")
    os.makedirs(wp_dir, exist_ok=True)

    # Find the most recently modified image and set it as global wallpaper
    image_exts = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
    best_file = None
    best_mtime = 0
    for f in os.listdir(wp_dir):
        ext = os.path.splitext(f)[1].lower()
        if ext in image_exts:
            fpath = os.path.join(wp_dir, f)
            mtime = os.path.getmtime(fpath)
            if mtime > best_mtime:
                best_mtime = mtime
                best_file = (f, fpath)

    if best_file:
        filename, filepath = best_file
        with open(filepath, "rb") as fh:
            file_hash = hashlib.md5(fh.read()).hexdigest()
        url = f"/static/wallpapers/{filename}"
        command_store.set_wallpaper(url, file_hash)
        logger.info("Restored wallpaper: %s", filename)


_SYNC_STATUS_INTERVAL_HOURS = 24  # re-anchor trial clock once a day


async def _license_verify_loop():
    """Periodically verify the license and re-anchor the trial clock."""
    last_sync = 0.0  # epoch seconds of last successful sync_startup_status call
    while True:
        await asyncio.sleep(60 * 60)  # wake up every hour
        try:
            if license_service:
                # Re-anchor trial clock every 24 hours.
                # Catches the case where the server started offline and
                # sync_startup_status() failed at boot — once connectivity
                # returns the tampered first_run gets corrected.
                now = asyncio.get_event_loop().time()
                if now - last_sync >= _SYNC_STATUS_INTERVAL_HOURS * 3600:
                    await license_service.sync_startup_status()
                    last_sync = now

                # Verify license every 6 hours
                if license_service.is_activated() and license_service.should_verify():
                    result = await license_service.verify()
                    logger.info("License verification: %s", result)
        except Exception as e:
            logger.error("License verification error: %s", e)


async def _session_expiry_loop():
    """Periodically expire sessions that have run out of time."""
    while True:
        await asyncio.sleep(30)
        try:
            db = SessionLocal()
            try:
                svc = SessionService(db)
                svc.expire_sessions()
            finally:
                db.close()
        except Exception as e:
            logger.error("Session expiry error: %s", e)


async def _membership_expiry_loop():
    """Periodically check membership timeouts: heartbeat, zero-time, idle shutdown."""
    while True:
        await asyncio.sleep(30)
        try:
            db = SessionLocal()
            try:
                msvc = MembershipService(db)
                msvc.auto_expire_members()
                msvc.check_zero_time_timeouts()
                msvc.check_idle_shutdown()
            finally:
                db.close()
        except Exception as e:
            logger.error("Membership expiry error: %s", e)


async def _nightly_earnings_sync_loop():
    """
    Once per day, near midnight UTC, POST yesterday's per-PC earnings to
    pisonex.com /api/sync/earnings.  Authenticated with the server's pisonex.com
    license key (from data/license.json via LicenseService._data).
    Skipped if BRANCH_NAME is empty or no license key is found.
    """
    import httpx
    from datetime import datetime as _dt, timedelta

    def _seconds_until_next_midnight() -> float:
        """Returns seconds remaining until the next UTC midnight."""
        now = _dt.utcnow()
        tomorrow = (now + timedelta(days=1)).replace(
            hour=0, minute=0, second=0, microsecond=0
        )
        return (tomorrow - now).total_seconds()

    # Sleep until next midnight before entering the daily loop
    initial_sleep = _seconds_until_next_midnight()
    logger.info("Nightly earnings sync: first run in %.0f seconds (at next UTC midnight)", initial_sleep)
    await asyncio.sleep(initial_sleep)

    while True:
        try:
            branch_name = settings.BRANCH_NAME
            if not branch_name:
                logger.debug("Nightly earnings sync skipped — BRANCH_NAME not set")
            else:
                db = SessionLocal()
                try:
                    # The license key for pisonex.com is held by the server-side LicenseService
                    # (stored in data/license.json, _data["license_key"]).
                    license_key = ""
                    if license_service and hasattr(license_service, "_data"):
                        license_key = license_service._data.get("license_key", "") or ""

                    if not license_key:
                        logger.debug("Nightly earnings sync skipped — no pisonex.com license key available")
                    else:
                        svc = SessionService(db)
                        pc_earnings = svc.get_all_pcs_today_earnings()

                        yesterday = (_dt.utcnow() - timedelta(days=1)).strftime("%Y-%m-%d")

                        from services.license_service import _signed_payload
                        raw_payload = {
                            "license_key": license_key,
                            "branch_name": branch_name,
                            "date": yesterday,
                            "pcs": [
                                {
                                    "pc_number": e["pc_number"],
                                    "total_pesos": e["total_pesos"],
                                    "total_sessions": e["total_sessions"],
                                    "total_minutes": e["total_minutes"],
                                }
                                for e in pc_earnings
                            ],
                        }

                        async with httpx.AsyncClient(timeout=30.0) as client:
                            resp = await client.post(
                                "https://www.pisonex.com/api/sync/earnings",
                                json=_signed_payload(raw_payload),
                            )
                            if resp.status_code in (200, 201):
                                logger.info(
                                    "Nightly earnings synced to pisonex.com: %s, %d PCs",
                                    yesterday,
                                    len(pc_earnings),
                                )
                            else:
                                logger.warning(
                                    "Nightly earnings sync returned HTTP %d: %s",
                                    resp.status_code,
                                    resp.text[:200],
                                )
                finally:
                    db.close()
        except asyncio.CancelledError:
            raise
        except Exception as e:
            logger.error("Nightly earnings sync error: %s", e)

        # Sleep 24 hours until the next midnight cycle
        await asyncio.sleep(24 * 60 * 60)


# ── FastAPI app ───────────────────────────────────────────────────────────────

app = FastAPI(
    title="Pisonex Server",
    version="1.0.0",
    lifespan=lifespan,
)

# CORS: only allow the dashboard's own origin and localhost for dev.
# PC clients don't use CORS (they're native apps, not browsers).
# Browsers omit the port from Origin when using the default (80 for http, 443 for https).
_port_suffix = f":{settings.SERVER_PORT}" if settings.SERVER_PORT not in (80, 443) else ""
_cors_origins = [
    "http://pisonex.local",
    f"http://localhost{_port_suffix}",
    f"http://127.0.0.1{_port_suffix}",
]
app.add_middleware(
    CORSMiddleware,
    allow_origins=_cors_origins,
    allow_methods=["*"],
    allow_headers=["*"],
)

# ── Session context middleware ────────────────────────────────────────────────
# Parses the pisonet_session cookie and attaches user info to request.state so
# Jinja2 templates can access it via request.state.user_role / .username without
# requiring each route to pass it explicitly in the template context dict.

@app.middleware("http")
async def attach_session_to_request(request: Request, call_next):
    cookie = request.cookies.get("pisonet_session")
    request.state.username = None
    request.state.user_role = None
    if cookie:
        try:
            from jose import jwt as _jwt, JWTError
            payload = _jwt.decode(cookie, settings.SECRET_KEY, algorithms=["HS256"])
            request.state.username = payload.get("sub")
            request.state.user_role = payload.get("role", "admin")
        except Exception:
            pass
    return await call_next(request)


# ── License enforcement middleware ─────────────────────────────────────────
# Blocks API requests when license is expired or offline-locked.
# Allows: dashboard, static, auth, license, health, heartbeat (so clients know status).

_LICENSE_EXEMPT_PREFIXES = (
    "/dashboard", "/static", "/api/auth", "/api/license",
    "/api/pc/heartbeat", "/api/pc/register", "/health", "/",
)


@app.middleware("http")
async def license_middleware(request: Request, call_next):
    path = request.url.path

    # Always allow exempt routes
    if path == "/" or any(
        path == prefix or path.startswith(prefix + "/") or path.startswith(prefix + "?")
        for prefix in _LICENSE_EXEMPT_PREFIXES
        if prefix != "/"
    ):
        return await call_next(request)

    # Check license
    if license_service and not license_service.is_active():
        status = license_service.get_status()
        return JSONResponse(
            status_code=403,
            content={
                "detail": "License expired or not activated. Please activate your software.",
                "license_status": status["status"],
            },
        )

    return await call_next(request)


# ── API routers ───────────────────────────────────────────────────────────────

app.include_router(auth.router)
app.include_router(pc.router)
app.include_router(sessions.router)
app.include_router(admin.router)
app.include_router(license_router)
app.include_router(member_router)
app.include_router(dashboard_router)

# ── Static files ───────────────────────────────────────────────────────────────

app.mount("/static", StaticFiles(directory=str(_BUNDLE_DIR / "dashboard" / "static")), name="static")


@app.get("/")
def root():
    return RedirectResponse("/dashboard")


# ── Health check ──────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    return {"status": "ok", "version": "1.0.0"}


# ── Dev entry point ───────────────────────────────────────────────────────────

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(
        "main:app",
        host=settings.SERVER_HOST,
        port=settings.SERVER_PORT,
        reload=False,
        workers=1,
    )
