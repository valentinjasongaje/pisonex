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
from models import AdminUser, CoinRate, MembershipConfig
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
async def lifespan(app: FastAPI):
    global hw_controller, license_service

    # Initialize license service and fetch beta status from pisonex.com
    license_service = LicenseService()
    await license_service.fetch_beta_status()
    logger.info("License status: %s (beta=%s)", license_service.get_status()["status"], license_service.beta_mode)

    # Migrate existing DB columns (v2 → v3 seconds-based rename)
    _migrate_schema()

    # Create all DB tables (creates new tables like membership_config)
    Base.metadata.create_all(bind=engine)

    db = SessionLocal()
    try:
        _seed_defaults(db)
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

    yield

    # Shutdown
    expire_task.cancel()
    license_task.cancel()
    membership_task.cancel()
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
    if not db.query(AdminUser).first():
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


async def _license_verify_loop():
    """Periodically verify the license and refresh beta status."""
    while True:
        await asyncio.sleep(60 * 60)  # check every hour
        try:
            if license_service:
                # Refresh beta flag from pisonex.com
                if license_service.should_refresh_beta():
                    await license_service.fetch_beta_status()

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


# ── FastAPI app ───────────────────────────────────────────────────────────────

app = FastAPI(
    title="Pisonex Server",
    version="1.0.0",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],   # Restrict to LAN subnet in production
    allow_methods=["*"],
    allow_headers=["*"],
)

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
