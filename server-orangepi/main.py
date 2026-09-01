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
from models import AdminUser, CoinRate, MembershipConfig, RateProfile, ServerConfig, CoinSchedule, ScheduledAnnouncement
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
_BUNDLE_DIR = Path(__file__).parent

logger = logging.getLogger(__name__)

# ── App lifespan (startup / shutdown) ─────────────────────────────────────────

hw_controller = None
license_service: LicenseService = None



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


@asynccontextmanager
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
        # Load admin-configured coin GPIO settings from DB into live settings
        _apply_coin_config(srv_cfg)
        logger.info(
            "Coin slot config: COIN_PIN=%s RELAY_PIN=%s EDGE=%s DEBOUNCE=%sms TIMEOUT=%ss",
            settings.COIN_PIN, settings.RELAY_PIN, settings.COIN_EDGE,
            settings.COIN_DEBOUNCE_MS, settings.COIN_PULSE_TIMEOUT,
        )
        # Load admin-configured keypad settings from DB into live settings
        _apply_keypad_config(srv_cfg)
        _apply_lcd_config(srv_cfg)
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

    # Background task: hourly status ping to pisonex.com (live branch totals)
    status_task = asyncio.create_task(_hourly_status_ping_loop())

    # Background task: enforce coin block schedules and fire timed announcements
    schedule_task = asyncio.create_task(_schedule_loop())

    yield

    # Shutdown
    expire_task.cancel()
    license_task.cancel()
    membership_task.cancel()
    earnings_task.cancel()
    status_task.cancel()
    schedule_task.cancel()
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

    # Admin-only membership: members created via the dashboard get a temp
    # password and must change it on first login. Defaults to 0 (False) for
    # existing self-registered accounts.
    if not has_column("users", "must_change_password"):
        cursor.execute(
            "ALTER TABLE users ADD COLUMN must_change_password INTEGER NOT NULL DEFAULT 0"
        )
        migrated.append("users.must_change_password (added)")

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

    # Add coin-slot GPIO config columns to server_config if missing
    # (only applies to installs that already have a server_config table — fresh
    # installs get the columns from Base.metadata.create_all instead).
    def table_exists(table: str) -> bool:
        cursor.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name=?", (table,)
        )
        return cursor.fetchone() is not None

    if table_exists("server_config"):
        new_server_columns = [
            ("coin_pin", "INTEGER"),
            ("relay_pin", "INTEGER"),
            ("coin_edge", "VARCHAR(10)"),
            ("coin_debounce_ms", "INTEGER"),
            ("coin_pulse_timeout", "VARCHAR(16)"),
            ("ffmpeg_streaming_enabled", "BOOLEAN DEFAULT 1"),
        ]
        for col_name, col_type in new_server_columns:
            if not has_column("server_config", col_name):
                cursor.execute(f"ALTER TABLE server_config ADD COLUMN {col_name} {col_type}")
                migrated.append(f"server_config.{col_name} (added)")

    # Rate Profiles: add profile_id FK to coin_rates and pcs if missing
    if table_exists("coin_rates"):
        if not has_column("coin_rates", "profile_id"):
            cursor.execute("ALTER TABLE coin_rates ADD COLUMN profile_id INTEGER")
            migrated.append("coin_rates.profile_id (added)")

    if table_exists("pcs"):
        if not has_column("pcs", "rate_profile_id"):
            cursor.execute("ALTER TABLE pcs ADD COLUMN rate_profile_id INTEGER")
            migrated.append("pcs.rate_profile_id (added)")

    # Standalone kiosk keypad/LCD config columns on server_config if missing
    if table_exists("server_config"):
        new_keypad_columns = [
            ("keypad_enabled", "BOOLEAN NOT NULL DEFAULT 0"),
            ("keypad_row_pins", "VARCHAR(64)"),
            ("keypad_col_pins", "VARCHAR(64)"),
        ]
        for col_name, col_type in new_keypad_columns:
            if not has_column("server_config", col_name):
                cursor.execute(f"ALTER TABLE server_config ADD COLUMN {col_name} {col_type}")
                migrated.append(f"server_config.{col_name} (added)")

        new_lcd_columns = [
            ("lcd_i2c_address", "INTEGER"),
            ("lcd_i2c_port", "INTEGER"),
        ]
        for col_name, col_type in new_lcd_columns:
            if not has_column("server_config", col_name):
                cursor.execute(f"ALTER TABLE server_config ADD COLUMN {col_name} {col_type}")
                migrated.append(f"server_config.{col_name} (added)")

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

    # Ensure the Default rate profile exists (id=1, is_default=True).
    # On fresh installs this runs before create_all so the table may not exist yet —
    # that is fine; create_all runs right before _seed_defaults and will have created it.
    default_profile = db.query(RateProfile).filter_by(is_default=True).first()
    if not default_profile:
        default_profile = RateProfile(name="Default", color="#4f8ef7", is_default=True)
        db.add(default_profile)
        db.flush()   # get the id assigned
        logger.info("Created Default rate profile (id=%d)", default_profile.id)

    # Any CoinRate rows with profile_id=None are owned by the Default profile.
    if default_profile.id:
        db.query(CoinRate).filter(CoinRate.profile_id == None).update(  # noqa: E711
            {"profile_id": default_profile.id}, synchronize_session=False
        )

    if not db.query(CoinRate).first():
        rate = CoinRate(
            pesos=settings.DEFAULT_RATE_PESOS,
            seconds=settings.DEFAULT_RATE_SECONDS,
            label=f"₱{settings.DEFAULT_RATE_PESOS} = {settings.DEFAULT_RATE_SECONDS // 60} minutes",
            profile_id=default_profile.id,
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
        db.add(ServerConfig(
            id=1,
            client_api_key="",
            coin_pin=settings.COIN_PIN,
            relay_pin=settings.RELAY_PIN,
            coin_edge=settings.COIN_EDGE,
            coin_debounce_ms=settings.COIN_DEBOUNCE_MS,
            coin_pulse_timeout=str(settings.COIN_PULSE_TIMEOUT),
        ))
        logger.info("Created default server config (client auth disabled)")

    db.commit()


def _apply_coin_config(srv_cfg) -> None:
    """Copy admin-editable coin GPIO settings from a ServerConfig row into the
    live `settings` object so the CoinSlot picks them up. NULL columns leave the
    .env / config.py default in place. Safe to call repeatedly."""
    if srv_cfg is None:
        return
    if srv_cfg.coin_pin is not None:
        settings.COIN_PIN = srv_cfg.coin_pin
    if srv_cfg.relay_pin is not None:
        settings.RELAY_PIN = srv_cfg.relay_pin
    if srv_cfg.coin_edge:
        settings.COIN_EDGE = srv_cfg.coin_edge
    if srv_cfg.coin_debounce_ms is not None:
        settings.COIN_DEBOUNCE_MS = srv_cfg.coin_debounce_ms
    if srv_cfg.coin_pulse_timeout:
        try:
            settings.COIN_PULSE_TIMEOUT = float(srv_cfg.coin_pulse_timeout)
        except (TypeError, ValueError):
            pass


def _apply_keypad_config(srv_cfg) -> None:
    """Copy admin-editable keypad settings from a ServerConfig row into the
    live `settings` object so HardwareController picks them up. NULL/empty
    columns leave the .env / config.py defaults in place. Safe to call
    repeatedly."""
    if srv_cfg is None:
        return
    settings.KEYPAD_ENABLED = bool(srv_cfg.keypad_enabled)
    if srv_cfg.keypad_row_pins:
        try:
            settings.KEYPAD_ROWS = [int(p) for p in srv_cfg.keypad_row_pins.split(",") if p.strip()]
        except ValueError:
            pass
    if srv_cfg.keypad_col_pins:
        try:
            settings.KEYPAD_COLS = [int(p) for p in srv_cfg.keypad_col_pins.split(",") if p.strip()]
        except ValueError:
            pass


def _apply_lcd_config(srv_cfg) -> None:
    """Copy admin-editable LCD I2C settings from a ServerConfig row into the
    live `settings` object so the LCD picks them up. NULL columns leave the
    .env / config.py default in place. Safe to call repeatedly."""
    if srv_cfg is None:
        return
    if srv_cfg.lcd_i2c_address is not None:
        settings.LCD_I2C_ADDRESS = srv_cfg.lcd_i2c_address
    if srv_cfg.lcd_i2c_port is not None:
        settings.LCD_I2C_PORT = srv_cfg.lcd_i2c_port


def rebuild_hardware_controller() -> bool:
    """Tear down and recreate the coin-slot hardware controller so that changed
    GPIO pin settings take effect without a full server restart.

    Returns True if a controller is running afterwards, False if hardware is
    unavailable (e.g. running off-Pi in dev). Callers should _apply_coin_config /
    _apply_keypad_config / _apply_lcd_config (or update `settings`) BEFORE
    calling this so the new CoinSlot/Keypad/LCD read the updated settings.
    """
    global hw_controller
    old = hw_controller
    hw_controller = None
    if old is not None:
        try:
            old.cleanup()
        except Exception as e:
            logger.warning("Error cleaning up old hardware controller: %s", e)

    try:
        from hardware.controller import HardwareController
        db = SessionLocal()
        svc = SessionService(db)
        hw_controller = HardwareController(svc)
        logger.info("Hardware controller rebuilt with new coin settings")
        return True
    except Exception as e:
        logger.warning("Hardware controller not started after rebuild: %s", e)
        hw_controller = None
        return False


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
    """Periodically verify the license and re-anchor the trial clock."""
    while True:
        await asyncio.sleep(60 * 60)  # wake up every hour
        try:
            if license_service and license_service.should_verify():
                # Re-anchor trial clock every 6 hours alongside license verify.
                # Catches the case where the server started offline and
                # sync_startup_status() failed at boot.
                await license_service.sync_startup_status()

                if license_service.is_activated():
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


def _time_in_range(start: str, end: str, current: str) -> bool:
    """Return True if current HH:MM falls within [start, end]. Handles midnight crossings."""
    if start <= end:
        return start <= current <= end
    # Crosses midnight e.g. "22:00" to "06:00"
    return current >= start or current <= end


def _next_minute(t: str) -> str:
    """Return HH:MM for t + 1 minute (used to build a 1-minute fire window)."""
    h, m = map(int, t.split(":"))
    m += 1
    if m >= 60:
        m, h = 0, (h + 1) % 24
    return f"{h:02d}:{m:02d}"


def _run_schedule_tick():
    """
    Called every 30 s from _schedule_loop.

    1. Evaluates all active CoinSchedule rows → sets command_store._schedule_blocked.
    2. Fires any ScheduledAnnouncement whose fire_time window is now and hasn't
       fired today yet.

    Uses local server time (datetime.now()), NOT UTC, so schedules match the
    café's timezone.
    """
    import command_store
    from datetime import datetime as _dt
    now      = _dt.now()
    cur_time = now.strftime("%H:%M")
    cur_date = now.strftime("%Y-%m-%d")
    cur_dow  = str(now.weekday())   # "0"=Mon … "6"=Sun

    with SessionLocal() as db:
        # ── Coin block ────────────────────────────────────────────────────────
        schedules = db.query(CoinSchedule).filter(CoinSchedule.is_active == True).all()
        blocked = any(
            cur_dow in s.days_of_week
            and _time_in_range(s.start_time, s.end_time, cur_time)
            for s in schedules
        )
        old_blocked = command_store.is_schedule_blocked()
        if blocked != old_blocked:
            command_store.set_schedule_blocked(blocked)
            logger.info(
                "Coin slot %s by schedule at %s",
                "BLOCKED" if blocked else "UNBLOCKED",
                cur_time,
            )

        # ── Announcements ─────────────────────────────────────────────────────
        announcements = db.query(ScheduledAnnouncement).filter(
            ScheduledAnnouncement.is_active == True
        ).all()
        for ann in announcements:
            if cur_dow not in ann.days_of_week:
                continue
            if ann.last_fired_date == cur_date:
                continue   # already fired today
            # Fire if we're within the 1-minute window of fire_time
            if ann.fire_time <= cur_time < _next_minute(ann.fire_time):
                command_store.set_announcement(ann.message)
                ann.last_fired_date = cur_date
                db.commit()
                logger.info(
                    "Scheduled announcement fired: %s",
                    ann.label or ann.message[:50],
                )


async def _schedule_loop():
    """Background task: enforce coin schedules and fire timed announcements."""
    while True:
        await asyncio.sleep(30)
        try:
            _run_schedule_tick()
        except Exception as exc:
            logger.error("_schedule_loop error: %s", exc)


async def _nightly_earnings_sync_loop():
    """
    Once per day at UTC midnight, POST the previous day's per-PC earnings to
    pisonex.com /api/sync/earnings.

    On startup, also syncs any recent days the server missed while it was off
    (e.g. shop closed at 10pm before UTC midnight ran).  The pisonex.com endpoint
    uses onConflictDoUpdate so re-syncing the same date is always safe.
    """
    import httpx
    from datetime import datetime as _dt, timedelta

    def _seconds_until_next_midnight() -> float:
        now = _dt.utcnow()
        tomorrow = (now + timedelta(days=1)).replace(
            hour=0, minute=0, second=0, microsecond=0
        )
        return (tomorrow - now).total_seconds()

    async def _sync_date(target_date: "_dt") -> bool:
        """Sync earnings for one specific UTC calendar date. Returns True on success."""
        branch_name = settings.BRANCH_NAME
        if not branch_name:
            return False
        license_key = ""
        if license_service and hasattr(license_service, "_data"):
            license_key = license_service._data.get("license_key", "") or ""
        if not license_key:
            return False

        db = SessionLocal()
        try:
            svc = SessionService(db)
            pc_earnings = svc.get_earnings_for_utc_date(target_date)
            date_str = target_date.strftime("%Y-%m-%d")

            from services.license_service import _signed_payload
            raw_payload = {
                "license_key": license_key,
                "branch_name": branch_name,
                "date": date_str,
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
                logger.info("Earnings synced: %s, %d PCs", date_str, len(pc_earnings))
                return True
            else:
                logger.warning("Earnings sync HTTP %d for %s: %s",
                               resp.status_code, date_str, resp.text[:200])
                return False
        finally:
            db.close()

    # ── Startup catch-up ──────────────────────────────────────────────────────
    # If the server was off at UTC midnight (e.g. shop closed at 10pm), those
    # days were never archived. On startup, sync the past 7 UTC days so any
    # missed nights are recovered. onConflictDoUpdate makes this idempotent.
    now = _dt.utcnow()
    for days_back in range(1, 8):
        missed_day = now - timedelta(days=days_back)
        try:
            await _sync_date(missed_day)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            logger.warning("Startup catch-up sync failed for %s: %s",
                           missed_day.strftime("%Y-%m-%d"), e)

    # ── Nightly loop ──────────────────────────────────────────────────────────
    initial_sleep = _seconds_until_next_midnight()
    logger.info("Nightly earnings sync: next run in %.0f seconds (at next UTC midnight)", initial_sleep)
    await asyncio.sleep(initial_sleep)

    while True:
        try:
            yesterday = _dt.utcnow() - timedelta(days=1)
            await _sync_date(yesterday)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            logger.error("Nightly earnings sync error: %s", e)

        await asyncio.sleep(24 * 60 * 60)


async def _hourly_status_ping_loop():
    """
    Once per hour, POST today's branch totals to pisonex.com /api/status.
    Used by the customer portal to display live (≤ 1 h stale) branch earnings.
    Replaces the per-PC client-side ping that was removed alongside client
    licensing — the server now owns this telemetry.

    Skipped if no pisonex.com license_key is available.
    """
    import httpx

    # Sleep 5 minutes after startup so the first ping carries real activity
    # rather than zeros, then enter the hourly cadence.
    await asyncio.sleep(5 * 60)

    while True:
        try:
            license_key = ""
            if license_service and hasattr(license_service, "_data"):
                license_key = license_service._data.get("license_key", "") or ""

            if not license_key:
                logger.debug("Hourly status ping skipped — no pisonex.com license key")
            else:
                device_id = license_service.get_device_id()
                db = SessionLocal()
                try:
                    svc = SessionService(db)
                    totals = svc.get_today_earnings()
                finally:
                    db.close()

                payload = {
                    "device_id": device_id,
                    "device_type": "server",
                    "license_key": license_key,
                    "branch_name": settings.BRANCH_NAME,
                    "today_pesos": totals["total_pesos"],
                    "today_sessions": totals["total_sessions"],
                    "today_minutes": totals["total_minutes"],
                }

                async with httpx.AsyncClient(timeout=10.0) as client:
                    resp = await client.post(
                        "https://www.pisonex.com/api/status",
                        json=payload,
                    )
                    if resp.status_code in (200, 201):
                        logger.debug(
                            "Hourly status ping ok — ₱%d, %d sessions, %d min",
                            totals["total_pesos"],
                            totals["total_sessions"],
                            totals["total_minutes"],
                        )
                    else:
                        logger.warning(
                            "Hourly status ping HTTP %d: %s",
                            resp.status_code,
                            resp.text[:200],
                        )
        except asyncio.CancelledError:
            raise
        except Exception as e:
            logger.error("Hourly status ping error: %s", e)

        await asyncio.sleep(60 * 60)


# ── FastAPI app ───────────────────────────────────────────────────────────────

app = FastAPI(
    title="Pisonex Server",
    version="1.0.4",
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

    # ── License state for dashboard banner ─────────────────────────────────
    # Only computed for /dashboard/* requests so API / static / heartbeat
    # paths stay zero-cost. The layout template reads these to decide whether
    # to render the activation banner (suppressed on /dashboard/license so
    # the activation flow isn't blocked by its own warning).
    request.state.show_license_banner = False
    request.state.license_status = None
    request.state.license_trial_days = 0
    path = request.url.path
    if path.startswith("/dashboard") and license_service is not None:
        try:
            status = license_service.get_status()
            request.state.license_status = status.get("status")
            request.state.license_trial_days = int(status.get("trial_days_remaining") or 0)
            if not status.get("is_active") and path != "/dashboard/license":
                request.state.show_license_banner = True
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
    return {"status": "ok", "version": "1.0.4"}


# ── Dev entry point ───────────────────────────────────────────────────────────

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(
        "main:app",
        host=settings.SERVER_HOST,
        port=settings.SERVER_PORT,
        reload=False,
        workers=1,
        ws_ping_interval=None,   # disable server-initiated pings — the VB.NET
        ws_ping_timeout=None,    # ClientWebSocket only handles pings during ReceiveAsync
    )
