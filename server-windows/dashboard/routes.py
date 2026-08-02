import asyncio
import csv
import hashlib
import io
import os
import sys
from datetime import datetime, date, timedelta, timezone
from pathlib import Path
from typing import Optional
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Request, Depends, Form, Cookie, HTTPException, UploadFile, File, WebSocket, WebSocketDisconnect
from fastapi.responses import HTMLResponse, RedirectResponse, Response, StreamingResponse
from fastapi.templating import Jinja2Templates
from sqlalchemy.orm import Session
from sqlalchemy import func, desc
from jose import JWTError, jwt
import bcrypt

from pydantic import BaseModel

from database import get_db
from models import AdminUser, CoinTransaction, SystemLog, CoinRate, RateProfile, PC, Session as SessionModel, User, MembershipConfig, ServerConfig
from schemas import AdminAddTimeRequest, AdminAddPesosRequest
from services.session_service import SessionService
from services.rate_service import pesos_to_seconds, pesos_for_seconds
from config import settings
import command_store

# Fixed amounts shown as quick-add presets in the "Add Time" modal. The modal
# lets the admin switch between Amount (pesos) and Time (minutes) presets;
# each side shows the other unit's equivalent underneath, using the Default
# rate profile -- rates are configurable, so these aren't hardcoded elsewhere.
_ADD_TIME_PRESET_PESOS = [1, 5, 10, 15, 20, 25]
_ADD_TIME_PRESET_MINUTES = [30, 60, 120, 180, 300, 480]


def _format_compact_duration(seconds: int) -> str:
    mins = seconds // 60
    if mins >= 60:
        h, m = divmod(mins, 60)
        return f"{h}h {m}m" if m else f"{h}h"
    return f"{mins} min"


def _preset_time_labels(db: Session) -> dict:
    return {
        amt: _format_compact_duration(pesos_to_seconds(amt, db))
        for amt in _ADD_TIME_PRESET_PESOS
    }


def _preset_peso_labels(db: Session) -> dict:
    return {
        mins: f"₱{pesos_for_seconds(mins * 60, db)}"
        for mins in _ADD_TIME_PRESET_MINUTES
    }


class RenamePcBody(BaseModel):
    name: str

class SendMessageBody(BaseModel):
    text: str

class SendCommandBody(BaseModel):
    type: str               # "shutdown" | "restart" | "lock" | "open_url"
    payload: str = ""       # URL / app path for open_url

class AnnouncementBody(BaseModel):
    text: str

class CoinSlotBody(BaseModel):
    enabled: bool

router = APIRouter(prefix="/dashboard")
_BUNDLE_DIR = Path(os.environ.get('PISONEX_BUNDLE_DIR', Path(__file__).parent.parent))
templates = Jinja2Templates(directory=str(_BUNDLE_DIR / "dashboard" / "templates"))

# All timestamps are stored as naive UTC (datetime.utcnow()) -- that's correct
# and unchanged. This filter only affects what the dashboard displays: convert
# to settings.TIMEZONE before formatting, so admins see local time instead of
# raw UTC. Falls back to UTC if the configured zone name is invalid, so a
# typo in .env can't break every page that shows a timestamp.
try:
    _LOCAL_TZ = ZoneInfo(settings.TIMEZONE)
except Exception:
    _LOCAL_TZ = ZoneInfo("UTC")


def _localtime_filter(dt, fmt: str = "%Y-%m-%d %H:%M:%S"):
    if dt is None:
        return ""
    aware_utc = dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt
    return aware_utc.astimezone(_LOCAL_TZ).strftime(fmt)


templates.env.filters["localtime"] = _localtime_filter

_ALGORITHM = "HS256"
_COOKIE_NAME = "pisonet_session"


def _require_active_license():
    """Dependency that blocks action endpoints when the license is expired."""
    from main import license_service
    if license_service and not license_service.is_active():
        raise HTTPException(
            status_code=403,
            detail="License expired or not activated. Please activate your software.",
        )


# ── Session cookie helpers ────────────────────────────────────────────────────

def _validate_session(pisonet_session: str = Cookie(default=None)) -> Optional[dict]:
    """Returns {"username": ..., "role": ...} from the session cookie, or None if invalid/absent."""
    if not pisonet_session:
        return None
    try:
        payload = jwt.decode(pisonet_session, settings.SECRET_KEY, algorithms=[_ALGORITHM])
        username: str = payload.get("sub")
        role: str = payload.get("role", "admin")  # default "admin" for legacy tokens
        return {"username": username, "role": role} if username else None
    except JWTError:
        return None


def _create_session_token(username: str, role: str = "admin") -> str:
    payload = {
        "sub": username,
        "role": role,
        "exp": datetime.utcnow() + timedelta(hours=settings.TOKEN_EXPIRE_HOURS),
    }
    return jwt.encode(payload, settings.SECRET_KEY, algorithm=_ALGORITHM)


# ── Login / Logout ────────────────────────────────────────────────────────────

@router.get("/login", response_class=HTMLResponse)
def login_page(
    request: Request,
    current_user: Optional[str] = Depends(_validate_session),
):
    if current_user:
        return RedirectResponse("/dashboard", status_code=302)
    return templates.TemplateResponse("login.html", {"request": request, "error": None})


@router.post("/login")
def login_submit(
    request: Request,
    username: str = Form(...),
    password: str = Form(...),
    db: Session = Depends(get_db),
):
    admin = db.query(AdminUser).filter(AdminUser.username == username).first()
    valid = admin and bcrypt.checkpw(
        password.encode("utf-8"), admin.password.encode("utf-8")
    )
    if not valid:
        return templates.TemplateResponse(
            "login.html",
            {"request": request, "error": "Invalid username or password"},
            status_code=401,
        )

    token = _create_session_token(admin.username, admin.role)
    response = RedirectResponse("/dashboard", status_code=302)
    response.set_cookie(
        _COOKIE_NAME,
        token,
        httponly=True,
        samesite="lax",
        max_age=int(timedelta(hours=settings.TOKEN_EXPIRE_HOURS).total_seconds()),
    )
    return response


@router.get("/account", response_class=HTMLResponse)
def account_page(
    request: Request,
    current_user: Optional[str] = Depends(_validate_session),
    db: Session = Depends(get_db),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    return templates.TemplateResponse("account.html", {
        "request": request,
        "current_username": current_user["username"],
        "success": None,
        "error": None,
    })


@router.post("/account", response_class=HTMLResponse)
def account_update(
    request: Request,
    current_password: str = Form(...),
    new_username: str = Form(""),
    new_password: str = Form(""),
    confirm_password: str = Form(""),
    current_user: Optional[str] = Depends(_validate_session),
    db: Session = Depends(get_db),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)

    admin = db.query(AdminUser).filter(AdminUser.username == current_user["username"]).first()

    def _render(error=None, success=None):
        return templates.TemplateResponse("account.html", {
            "request": request,
            "current_username": new_username.strip() or current_user["username"],
            "error": error,
            "success": success,
        })

    # Verify current password
    if not admin or not bcrypt.checkpw(current_password.encode(), admin.password.encode()):
        return _render(error="Current password is incorrect.")

    new_username = new_username.strip()
    changed = False

    # Update username if changed
    if new_username and new_username != current_user["username"]:
        exists = db.query(AdminUser).filter(
            AdminUser.username == new_username,
            AdminUser.id != admin.id,
        ).first()
        if exists:
            return _render(error="That username is already taken.")
        admin.username = new_username
        changed = True

    # Update password if provided
    if new_password:
        if len(new_password) < 6:
            return _render(error="New password must be at least 6 characters.")
        if new_password != confirm_password:
            return _render(error="New passwords do not match.")
        admin.password = bcrypt.hashpw(new_password.encode(), bcrypt.gensalt()).decode()
        changed = True

    if not changed:
        return _render(error="No changes were made.")

    db.commit()

    # Re-issue session cookie with updated username
    token = _create_session_token(admin.username, admin.role)
    response = templates.TemplateResponse("account.html", {
        "request": request,
        "current_username": admin.username,
        "error": None,
        "success": "Credentials updated successfully.",
    })
    response.set_cookie(
        _COOKIE_NAME,
        token,
        httponly=True,
        samesite="lax",
        max_age=int(timedelta(hours=settings.TOKEN_EXPIRE_HOURS).total_seconds()),
    )
    return response


@router.get("/logout")
def logout():
    response = RedirectResponse("/dashboard/login", status_code=302)
    response.delete_cookie(_COOKIE_NAME)
    return response


# ── Shared data helper ────────────────────────────────────────────────────────

def _get_traditional_mode_enabled(db: Session) -> bool:
    """Returns the persistent "Traditional Café Mode" business-model toggle
    (ServerConfig.traditional_mode_enabled). True = cashier-run cafe with no
    coin acceptor hardware — Insert-Coin/coin-slot UI is hidden.

    Defaults False (row is always seeded on startup by _seed_defaults()).
    """
    srv_cfg = db.query(ServerConfig).first()
    return srv_cfg.traditional_mode_enabled if srv_cfg else False


def _get_membership_info(db: Session) -> tuple[bool, dict[int, str]]:
    """Returns (membership_enabled, {pc_number: username}) for all logged-in members."""
    cfg = db.query(MembershipConfig).first()
    enabled = cfg.membership_enabled if cfg else False
    if not enabled:
        return False, {}

    bindings = command_store.get_all_member_bindings()  # {pc_number: user_id}
    if not bindings:
        return True, {}

    user_ids = list(bindings.values())
    users = db.query(User).filter(User.id.in_(user_ids)).all()
    user_map = {u.id: u.username for u in users}
    pc_members = {pc_num: user_map.get(uid, "") for pc_num, uid in bindings.items()}
    return True, pc_members


def _pc_overview_data(db: Session):
    svc = SessionService(db)
    pcs = svc.get_all_pcs()
    timeout = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)
    membership_enabled, pc_members = _get_membership_info(db)

    pc_data = []
    online_count = 0
    active_count = 0

    for pc in pcs:
        if pc.last_seen and pc.last_seen < timeout:
            pc.is_online = False
        session = svc.get_active_session(pc.pc_number)
        remaining_sec = svc.remaining_seconds(session)
        if pc.is_online:
            online_count += 1
        if pc.is_online and not pc.is_locked:
            active_count += 1

        pc_data.append({
            "pc_number": pc.pc_number,
            "name": pc.name,
            "is_online": pc.is_online,
            "is_locked": pc.is_locked,
            "ip_address": pc.ip_address,
            "last_seen": pc.last_seen,
            "remaining_minutes": remaining_sec // 60,
            "remaining_seconds": remaining_sec % 60,
            "member_username": pc_members.get(pc.pc_number),
            "membership_enabled": membership_enabled,
        })

    db.commit()

    today = datetime.utcnow().date()
    today_row = db.query(
        func.coalesce(func.sum(CoinTransaction.amount_php), 0)
    ).filter(
        func.date(CoinTransaction.created_at) == today
    ).scalar()

    return pc_data, online_count, active_count, int(today_row)


# ── Protected page routes ─────────────────────────────────────────────────────

@router.get("", response_class=HTMLResponse)
@router.get("/", response_class=HTMLResponse)
def overview(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    pcs, online_count, active_count, today_pesos = _pc_overview_data(db)
    cfg = db.query(MembershipConfig).first()
    return templates.TemplateResponse("overview.html", {
        "request": request,
        "pcs": pcs,
        "online_count": online_count,
        "active_count": active_count,
        "today_pesos": today_pesos,
        "preset_amounts_enabled": cfg.preset_amounts_enabled if cfg else False,
        "preset_time_labels": _preset_time_labels(db),
        "preset_peso_labels": _preset_peso_labels(db),
        "traditional_mode_enabled": _get_traditional_mode_enabled(db),
    })


@router.get("/partials/pc-grid", response_class=HTMLResponse)
def pc_grid_partial(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    """HTMX partial — returns only the PC grid div for auto-refresh."""
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    pcs, _, _, _ = _pc_overview_data(db)
    return templates.TemplateResponse("partials/pc_grid.html", {
        "request": request,
        "pcs": pcs,
        "traditional_mode_enabled": _get_traditional_mode_enabled(db),
    })


@router.get("/license", response_class=HTMLResponse)
def license_page(
    request: Request,
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)
    from main import license_service
    lic = license_service.get_status() if license_service else {}
    return templates.TemplateResponse("license.html", {
        "request": request,
        "lic": lic,
    })


@router.get("/rates", response_class=HTMLResponse)
def rates_page(
    request: Request,
    profile: int = 1,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)

    from services.rate_service import get_all_profiles
    profiles = get_all_profiles(db)

    # Resolve the selected profile — fall back to the default if the id is unknown
    selected_profile = db.query(RateProfile).filter(RateProfile.id == profile).first()
    if not selected_profile and profiles:
        selected_profile = next((p for p in profiles if p.is_default), profiles[0])

    selected_id = selected_profile.id if selected_profile else 1

    rates = (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True, CoinRate.profile_id == selected_id)
        .order_by(CoinRate.pesos.asc())
        .all()
    )
    return templates.TemplateResponse("rates.html", {
        "request": request,
        "rates": rates,
        "profiles": profiles,
        "selected_profile": selected_profile,
        "selected_id": selected_id,
    })


@router.get("/partials/rates-table", response_class=HTMLResponse)
def rates_table_partial(
    request: Request,
    profile_id: int = 1,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """HTMX partial — returns just the rates table for the selected profile."""
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    if current_user["role"] != "admin":
        return HTMLResponse(status_code=403, content="")
    rates = (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True, CoinRate.profile_id == profile_id)
        .order_by(CoinRate.pesos.asc())
        .all()
    )
    return templates.TemplateResponse("partials/rates_table.html", {
        "request": request,
        "rates": rates,
        "selected_id": profile_id,
    })


@router.post("/rates", response_class=HTMLResponse)
def save_rate(
    request: Request,
    pesos: int = Form(...),
    minutes: int = Form(...),
    profile_id: int = Form(1),
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    if current_user["role"] != "admin":
        return HTMLResponse(status_code=403, content="")

    # Ensure the profile exists
    profile = db.query(RateProfile).filter(RateProfile.id == profile_id).first()
    if not profile:
        return HTMLResponse(status_code=404, content="Profile not found")

    existing = db.query(CoinRate).filter(
        CoinRate.pesos == pesos,
        CoinRate.is_active == True,
        CoinRate.profile_id == profile_id,
    ).first()
    if existing:
        existing.is_active = False

    seconds = minutes * 60
    rate = CoinRate(
        pesos=pesos,
        seconds=seconds,
        label=f"₱{pesos} = {minutes} minutes",
        profile_id=profile_id,
    )
    db.add(rate)
    db.commit()

    rates = (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True, CoinRate.profile_id == profile_id)
        .order_by(CoinRate.pesos.asc())
        .all()
    )
    return templates.TemplateResponse("partials/rates_table.html", {
        "request": request,
        "rates": rates,
        "selected_id": profile_id,
    })


@router.delete("/rates/{rate_id}", response_class=HTMLResponse)
def delete_rate(
    rate_id: int,
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    if current_user["role"] != "admin":
        return HTMLResponse(status_code=403, content="")
    rate = db.query(CoinRate).filter(CoinRate.id == rate_id).first()
    profile_id = rate.profile_id if rate else 1
    if rate:
        rate.is_active = False
        db.commit()
    rates = (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True, CoinRate.profile_id == profile_id)
        .order_by(CoinRate.pesos.asc())
        .all()
    )
    return templates.TemplateResponse("partials/rates_table.html", {
        "request": request,
        "rates": rates,
        "selected_id": profile_id,
    })


# ── Rate Profile CRUD ────────────────────────────────────────────────────────

@router.post("/rates/profiles", response_class=HTMLResponse)
def create_profile(
    request: Request,
    name: str = Form(...),
    color: str = Form("#4f8ef7"),
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    if current_user["role"] != "admin":
        return HTMLResponse(status_code=403, content="")

    name = name.strip()
    if not name:
        return HTMLResponse(status_code=422, content="Name cannot be empty")

    existing = db.query(RateProfile).filter(RateProfile.name == name).first()
    if existing:
        return HTMLResponse(status_code=409, content="A profile with that name already exists")

    profile = RateProfile(name=name, color=color, is_default=False)
    db.add(profile)
    db.commit()
    db.refresh(profile)

    from services.rate_service import get_all_profiles
    profiles = get_all_profiles(db)
    return templates.TemplateResponse("partials/profile_tabs.html", {
        "request": request,
        "profiles": profiles,
        "selected_id": profile.id,
    })


@router.delete("/rates/profiles/{profile_id}", response_class=HTMLResponse)
def delete_profile(
    profile_id: int,
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    if current_user["role"] != "admin":
        return HTMLResponse(status_code=403, content="")

    profile = db.query(RateProfile).filter(RateProfile.id == profile_id).first()
    if not profile:
        return HTMLResponse(status_code=404, content="Profile not found")
    if profile.is_default:
        return HTMLResponse(status_code=400, content="Cannot delete the Default profile")

    # Reassign PCs to Default profile
    default_profile = db.query(RateProfile).filter(RateProfile.is_default == True).first()
    default_id = default_profile.id if default_profile else 1
    db.query(PC).filter(PC.rate_profile_id == profile_id).update(
        {"rate_profile_id": None}, synchronize_session=False
    )

    # Soft-delete this profile's rates (mark inactive)
    db.query(CoinRate).filter(CoinRate.profile_id == profile_id).update(
        {"is_active": False}, synchronize_session=False
    )

    db.delete(profile)
    db.commit()

    from services.rate_service import get_all_profiles
    profiles = get_all_profiles(db)
    return templates.TemplateResponse("partials/profile_tabs.html", {
        "request": request,
        "profiles": profiles,
        "selected_id": default_id,
    })


@router.post("/rates/profiles/{profile_id}/name", response_class=HTMLResponse)
def rename_profile(
    profile_id: int,
    request: Request,
    name: str = Form(...),
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    if current_user["role"] != "admin":
        return HTMLResponse(status_code=403, content="")

    profile = db.query(RateProfile).filter(RateProfile.id == profile_id).first()
    if not profile:
        return HTMLResponse(status_code=404, content="Profile not found")
    if profile.is_default:
        return HTMLResponse(status_code=400, content="Cannot rename the Default profile")

    name = name.strip()
    if not name:
        return HTMLResponse(status_code=422, content="Name cannot be empty")

    conflict = db.query(RateProfile).filter(
        RateProfile.name == name, RateProfile.id != profile_id
    ).first()
    if conflict:
        return HTMLResponse(status_code=409, content="A profile with that name already exists")

    profile.name = name
    db.commit()

    from services.rate_service import get_all_profiles
    profiles = get_all_profiles(db)
    return templates.TemplateResponse("partials/profile_tabs.html", {
        "request": request,
        "profiles": profiles,
        "selected_id": profile_id,
    })


# ── PC profile assignment ────────────────────────────────────────────────────

@router.post("/pcs/{pc_number}/profile", response_class=HTMLResponse)
def set_pc_profile(
    pc_number: int,
    request: Request,
    profile_id: int = Form(...),
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Assign a rate profile to a PC.  Returns an updated profile-badge partial."""
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    if current_user["role"] != "admin":
        return HTMLResponse(status_code=403, content="")

    pc = db.query(PC).filter(PC.pc_number == pc_number).first()
    if not pc:
        return HTMLResponse(status_code=404, content=f"PC {pc_number} not found")

    # profile_id=0 means "clear assignment → Default"
    if profile_id == 0:
        pc.rate_profile_id = None
    else:
        profile = db.query(RateProfile).filter(RateProfile.id == profile_id).first()
        if not profile:
            return HTMLResponse(status_code=404, content="Profile not found")
        pc.rate_profile_id = profile_id

    db.commit()
    db.refresh(pc)

    # Determine the effective profile for display
    effective_profile = None
    if pc.rate_profile_id:
        effective_profile = db.query(RateProfile).filter(RateProfile.id == pc.rate_profile_id).first()
    if not effective_profile:
        effective_profile = db.query(RateProfile).filter(RateProfile.is_default == True).first()

    return templates.TemplateResponse("partials/pc_profile_badge.html", {
        "request": request,
        "pc_number": pc_number,
        "profile": effective_profile,
    })


@router.get("/transactions", response_class=HTMLResponse)
def transactions_page(
    request: Request,
    days: int = 0,
    pc_id: Optional[int] = None,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)

    query = db.query(CoinTransaction).join(PC, CoinTransaction.pc_id == PC.id, isouter=True)
    if days and days > 0:
        since = datetime.utcnow() - timedelta(days=days)
        query = query.filter(CoinTransaction.created_at >= since)
    if pc_id:
        query = query.filter(PC.pc_number == pc_id)

    transactions = (
        query
        .order_by(desc(CoinTransaction.created_at))
        .limit(1000)
        .all()
    )
    total_pesos = sum(t.amount_php for t in transactions)
    pcs = db.query(PC).order_by(PC.pc_number).all()
    return templates.TemplateResponse("transactions.html", {
        "request": request,
        "transactions": transactions,
        "total_pesos": total_pesos,
        "days": days,
        "pc_id": pc_id,
        "pcs": pcs,
    })


@router.get("/logs", response_class=HTMLResponse)
def logs_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)
    logs = (
        db.query(SystemLog)
        .order_by(desc(SystemLog.created_at))
        .limit(300)
        .all()
    )
    return templates.TemplateResponse("logs.html", {
        "request": request,
        "logs": logs,
    })


# ── Dashboard action API (cookie-authenticated) ───────────────────────────────
# These endpoints are called by admin.js — they use the session cookie
# instead of a JWT Bearer header, so no token management is needed in JS.

@router.post("/api/pc/add-time", dependencies=[Depends(_require_active_license)])
def dashboard_add_time(
    body: AdminAddTimeRequest,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    """Admin manually adds minutes to a PC — called from the dashboard UI."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    svc = SessionService(db)
    pc = svc.get_pc(body.pc_number)
    if not pc:
        raise HTTPException(status_code=404, detail=f"PC {body.pc_number} not found")
    if body.minutes <= 0:
        raise HTTPException(status_code=422, detail="Minutes must be greater than 0")

    seconds = body.minutes * 60
    session = svc.add_time_seconds(body.pc_number, seconds)
    return {
        "pc_number": body.pc_number,
        "seconds_added": seconds,
        "total_seconds": session.granted_seconds,
    }


@router.post("/api/pc/add-time-pesos", dependencies=[Depends(_require_active_license)])
def dashboard_add_time_pesos(
    body: AdminAddPesosRequest,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    """Admin adds time by PHP amount — creates a CoinTransaction with amount_php."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if body.pesos <= 0:
        raise HTTPException(status_code=422, detail="Pesos must be greater than 0")

    svc = SessionService(db)
    pc = svc.get_pc(body.pc_number)
    if not pc:
        raise HTTPException(status_code=404, detail=f"PC {body.pc_number} not found")

    try:
        seconds_added, session = svc.add_time_by_pesos(body.pc_number, body.pesos)
    except ValueError as e:
        raise HTTPException(status_code=422, detail=str(e))

    return {
        "pc_number": body.pc_number,
        "pesos_added": body.pesos,
        "seconds_added": seconds_added,
        "total_seconds": session.granted_seconds,
    }


@router.post("/api/pc/{pc_number}/lock", dependencies=[Depends(_require_active_license)])
def dashboard_lock_pc(
    pc_number: int,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    """Admin remotely locks (ends session on) a PC — called from the dashboard UI."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    svc = SessionService(db)
    ok = svc.end_session(pc_number)
    if not ok:
        raise HTTPException(status_code=404, detail=f"PC {pc_number} not found")
    return {"status": "locked", "pc_number": pc_number}


@router.post("/api/pc/{pc_number}/rename", dependencies=[Depends(_require_active_license)])
def dashboard_rename_pc(
    pc_number: int,
    body: RenamePcBody,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Rename a PC — called from the PC Management page."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    name = body.name.strip()
    if not name:
        raise HTTPException(status_code=422, detail="Name cannot be empty")

    pc = db.query(PC).filter(PC.pc_number == pc_number).first()
    if not pc:
        raise HTTPException(status_code=404, detail=f"PC {pc_number} not found")

    pc.name = name
    db.commit()
    return {"pc_number": pc_number, "name": pc.name}


# ── PC Monitor page ───────────────────────────────────────────────────────────

@router.get("/monitor", response_class=HTMLResponse)
def monitor_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)

    svc = SessionService(db)
    pcs = svc.get_all_pcs()
    timeout = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)

    membership_enabled, pc_members = _get_membership_info(db)
    import screenshot_store
    pc_data = []
    for pc in pcs:
        if pc.last_seen and pc.last_seen < timeout:
            pc.is_online = False
        session = svc.get_active_session(pc.pc_number)
        remaining_sec = svc.remaining_seconds(session)
        screenshot_time = screenshot_store.get_time(pc.pc_number)
        pc_data.append({
            "pc_number": pc.pc_number,
            "name": pc.name,
            "is_online": pc.is_online,
            "is_locked": pc.is_locked,
            "remaining_minutes": remaining_sec // 60,
            "remaining_seconds": remaining_sec % 60,
            "has_screenshot": screenshot_store.get(pc.pc_number) is not None,
            "screenshot_age": (
                int((datetime.utcnow() - screenshot_time).total_seconds())
                if screenshot_time else None
            ),
            "member_username": pc_members.get(pc.pc_number),
        })
    db.commit()

    return templates.TemplateResponse("monitor.html", {
        "request": request,
        "pcs": pc_data,
        "membership_enabled": membership_enabled,
    })


@router.get("/monitor/status")
def monitor_status(
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    """JSON snapshot of all PC statuses for the monitor page live-update."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    import screenshot_store
    svc = SessionService(db)
    membership_enabled, pc_members = _get_membership_info(db)
    pcs = svc.get_all_pcs()
    timeout = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)

    result = []
    for pc in pcs:
        if pc.last_seen and pc.last_seen < timeout:
            pc.is_online = False
        session = svc.get_active_session(pc.pc_number)
        remaining_sec = svc.remaining_seconds(session)
        result.append({
            "pc_number": pc.pc_number,
            "name": pc.name,
            "is_online": pc.is_online,
            "is_locked": pc.is_locked,
            "remaining_minutes": remaining_sec // 60,
            "remaining_seconds": remaining_sec % 60,
            "remaining_total_sec": remaining_sec,
            "has_screenshot": screenshot_store.get(pc.pc_number) is not None,
            "member_username": pc_members.get(pc.pc_number),
            "membership_enabled": membership_enabled,
        })
    db.commit()
    return result


@router.get("/api/pc/{pc_number}/screenshot")
def serve_screenshot(
    pc_number: int,
    current_user: Optional[str] = Depends(_validate_session),
):
    """Serves the latest screenshot for a PC to the admin dashboard."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    import screenshot_store
    data = screenshot_store.get(pc_number)
    if not data:
        raise HTTPException(status_code=404, detail="No screenshot available")

    return Response(
        content=data,
        media_type="image/jpeg",
        headers={"Cache-Control": "no-store"},
    )


@router.post("/monitor/watch/{pc_number}")
async def watch_pc(
    pc_number: int,
    current_user: Optional[str] = Depends(_validate_session),
):
    """Called by dashboard when admin opens fullscreen view of a PC.
    Sets a 12-second TTL that tells the PC client to ramp up to ~30 fps.
    The dashboard renews it every 8 s while the view is open."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.set_watched(pc_number)
    return {"status": "ok"}


@router.websocket("/ws/stream/{pc_number}/publish")
async def ws_stream_publish(websocket: WebSocket, pc_number: int):
    """PC client connects here and pushes raw MPEG-TS/MPEG1 bytes from FFmpeg.
    The server broadcasts every chunk to all watching browser connections."""
    import logging, stream_store
    _log = logging.getLogger("stream.publish")

    api_key = (
        websocket.headers.get("x-api-key", "")
        or websocket.query_params.get("api_key", "")
    )
    if settings.CLIENT_API_KEY and api_key != settings.CLIENT_API_KEY:
        _log.warning("PC %d publish rejected — bad API key", pc_number)
        await websocket.close(code=1008)
        return

    await websocket.accept()
    stream_store.set_publisher(pc_number, websocket)
    _log.info("PC %d publish connected (watchers: %d)",
              pc_number, len(stream_store._watchers.get(pc_number, [])))
    chunks = 0
    try:
        while True:
            data = await websocket.receive_bytes()
            chunks += 1
            if chunks <= 3 or chunks % 30 == 0:
                _log.info("PC %d chunk #%d  %d bytes  watchers: %d",
                          pc_number, chunks, len(data),
                          len(stream_store._watchers.get(pc_number, [])))
            await stream_store.broadcast(pc_number, data)
    except WebSocketDisconnect:
        _log.info("PC %d publish disconnected after %d chunks", pc_number, chunks)
    except Exception as exc:
        _log.warning("PC %d publish error after %d chunks: %s", pc_number, chunks, exc)
    finally:
        stream_store.clear_publisher(pc_number)
        await stream_store.close_all_watchers(pc_number)


@router.websocket("/ws/stream/{pc_number}/watch")
async def ws_stream_watch(websocket: WebSocket, pc_number: int):
    """Admin browser connects here to receive the live MPEG1 video stream.
    Authentication uses the pisonet_session JWT cookie (sent automatically)."""
    import logging, stream_store
    _log = logging.getLogger("stream.watch")

    token = websocket.cookies.get("pisonet_session")
    if not token:
        _log.warning("PC %d watch rejected — no session cookie", pc_number)
        await websocket.close(code=1008)
        return
    try:
        _jwt_decode = jwt.decode(token, settings.SECRET_KEY, algorithms=[_ALGORITHM])
    except Exception as exc:
        _log.warning("PC %d watch rejected — bad JWT: %s", pc_number, exc)
        await websocket.close(code=1008)
        return

    await websocket.accept()
    stream_store.add_watcher(pc_number, websocket)
    _log.info("PC %d watch connected (total watchers: %d)",
              pc_number, len(stream_store._watchers.get(pc_number, [])))
    try:
        while True:
            await asyncio.sleep(20)
    except (WebSocketDisconnect, Exception):
        pass
    finally:
        stream_store.remove_watcher(pc_number, websocket)
        _log.info("PC %d watch disconnected", pc_number)


# ── Documentation pages ───────────────────────────────────────────────────────

@router.get("/docs/api", response_class=HTMLResponse)
def docs_api_page(
    request: Request,
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)
    return templates.TemplateResponse("docs_api.html", {"request": request})


@router.get("/docs/wiring", response_class=HTMLResponse)
def docs_wiring_page(
    request: Request,
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)
    return templates.TemplateResponse("docs_wiring.html", {"request": request})


# ── PC Metrics partial (HTMX polling) ────────────────────────────────────────

@router.get("/monitor/metrics/{pc_number}", response_class=HTMLResponse)
def monitor_metrics(
    pc_number: int,
    request: Request,
    current_user: Optional[str] = Depends(_validate_session),
):
    """HTMX partial — live performance metrics panel for one PC."""
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    import metrics_store
    data = metrics_store.get(pc_number)
    updated = metrics_store.get_time(pc_number)
    age_sec = (
        int((datetime.utcnow() - updated).total_seconds()) if updated else None
    )
    return templates.TemplateResponse("partials/pc_metrics.html", {
        "request": request,
        "pc_number": pc_number,
        "m": data,
        "age_sec": age_sec,
    })


# ── PC Management page ────────────────────────────────────────────────────────

@router.get("/pcs", response_class=HTMLResponse)
def pcs_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)

    from services.rate_service import get_all_profiles

    svc = SessionService(db)
    pcs = svc.get_all_pcs()
    timeout = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)
    membership_enabled, pc_members = _get_membership_info(db)
    cfg = db.query(MembershipConfig).first()
    profiles = get_all_profiles(db)
    default_profile = next((p for p in profiles if p.is_default), None)
    profile_map = {p.id: p for p in profiles}

    pc_data = []
    for pc in pcs:
        if pc.last_seen and pc.last_seen < timeout:
            pc.is_online = False
        session = svc.get_active_session(pc.pc_number)
        remaining_sec = svc.remaining_seconds(session)
        # Effective profile: the one assigned, else the Default
        eff_profile = profile_map.get(pc.rate_profile_id) if pc.rate_profile_id else default_profile
        pc_data.append({
            "pc_number": pc.pc_number,
            "name": pc.name,
            "mac_address": pc.mac_address,
            "ip_address": pc.ip_address or "—",
            "is_online": pc.is_online,
            "is_locked": pc.is_locked,
            "last_seen": pc.last_seen,
            "remaining_minutes": remaining_sec // 60,
            "remaining_seconds": remaining_sec % 60,
            "member_username": pc_members.get(pc.pc_number),
            "rate_profile": eff_profile,
            "rate_profile_id": pc.rate_profile_id,
        })
    db.commit()

    return templates.TemplateResponse("pcs.html", {
        "request": request,
        "pcs": pc_data,
        "total": len(pc_data),
        "membership_enabled": membership_enabled,
        "preset_amounts_enabled": cfg.preset_amounts_enabled if cfg else False,
        "preset_time_labels": _preset_time_labels(db),
        "preset_peso_labels": _preset_peso_labels(db),
        "traditional_mode_enabled": _get_traditional_mode_enabled(db),
        "profiles": profiles,
    })


# ── Reports page ──────────────────────────────────────────────────────────────

def _reports_data(days: int, db):
    """Shared helper — compute all aggregate data for the reports page/export."""
    today_start = datetime.utcnow().replace(hour=0, minute=0, second=0, microsecond=0)
    week_start  = today_start - timedelta(days=today_start.weekday())
    month_start = today_start.replace(day=1)

    def _sum(q):
        row = q.with_entities(
            func.coalesce(func.sum(CoinTransaction.amount_php), 0).label("pesos"),
            func.count(CoinTransaction.id).label("count"),
        ).first()
        return int(row.pesos), int(row.count)

    base = db.query(CoinTransaction)
    today_pesos,  today_tx  = _sum(base.filter(CoinTransaction.created_at >= today_start))
    week_pesos,   week_tx   = _sum(base.filter(CoinTransaction.created_at >= week_start))
    month_pesos,  month_tx  = _sum(base.filter(CoinTransaction.created_at >= month_start))
    total_pesos,  total_tx  = _sum(base)

    # Sessions count (all time)
    total_sessions = db.query(func.count(SessionModel.id)).scalar() or 0

    # Per-PC revenue this month
    pc_rows = (
        db.query(
            PC.pc_number,
            PC.name,
            func.coalesce(func.sum(CoinTransaction.amount_php), 0).label("pesos"),
            func.count(CoinTransaction.id).label("tx_count"),
        )
        .outerjoin(CoinTransaction, (CoinTransaction.pc_id == PC.id) &
                   (CoinTransaction.created_at >= month_start))
        .group_by(PC.id)
        .order_by(desc("pesos"))
        .all()
    )
    per_pc = [
        {"pc_number": r.pc_number, "name": r.name,
         "pesos": int(r.pesos), "tx_count": int(r.tx_count)}
        for r in pc_rows
    ]
    max_pc_pesos = max((r["pesos"] for r in per_pc), default=1) or 1

    # Daily revenue — last 30 days (or custom range)
    chart_days = days if days and days > 0 else 30
    chart_since = today_start - timedelta(days=chart_days - 1)
    daily_rows = (
        db.query(
            func.date(CoinTransaction.created_at).label("day"),
            func.sum(CoinTransaction.amount_php).label("pesos"),
            func.count(CoinTransaction.id).label("count"),
        )
        .filter(CoinTransaction.created_at >= chart_since)
        .group_by(func.date(CoinTransaction.created_at))
        .order_by("day")
        .all()
    )
    daily_map = {str(r.day): (int(r.pesos), int(r.count)) for r in daily_rows}
    daily = []
    for i in range(chart_days):
        d = (chart_since + timedelta(days=i)).date()
        pesos, count = daily_map.get(str(d), (0, 0))
        daily.append({"date": d, "pesos": pesos, "count": count})
    max_day_pesos = max((d["pesos"] for d in daily), default=1) or 1

    return {
        "today_pesos": today_pesos, "today_tx": today_tx,
        "week_pesos":  week_pesos,  "week_tx":  week_tx,
        "month_pesos": month_pesos, "month_tx": month_tx,
        "total_pesos": total_pesos, "total_tx": total_tx,
        "total_sessions": total_sessions,
        "per_pc": per_pc, "max_pc_pesos": max_pc_pesos,
        "daily": daily,  "max_day_pesos": max_day_pesos,
        "chart_days": chart_days,
    }


@router.get("/reports", response_class=HTMLResponse)
def reports_page(
    request: Request,
    days: int = 30,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)
    ctx = _reports_data(days, db)
    return templates.TemplateResponse("reports.html", {
        "request": request,
        "selected_days": days,
        **ctx,
    })


@router.get("/reports/export.csv")
def reports_export_csv(
    days: int = 0,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    query = (
        db.query(CoinTransaction)
        .outerjoin(PC, CoinTransaction.pc_id == PC.id)
        .add_columns(PC.pc_number, PC.name)
        .order_by(desc(CoinTransaction.created_at))
    )
    if days and days > 0:
        since = datetime.utcnow() - timedelta(days=days)
        query = query.filter(CoinTransaction.created_at >= since)

    buf = io.StringIO()
    writer = csv.writer(buf)
    writer.writerow(["Date", "PC Number", "PC Name", "Amount (₱)", "Seconds Added"])
    for tx, pc_number, pc_name in query.all():
        writer.writerow([
            tx.created_at.strftime("%Y-%m-%d %H:%M:%S"),
            pc_number or "",
            pc_name or "",
            tx.amount_php,
            tx.seconds_added,
        ])

    buf.seek(0)
    filename = f"pisonet-transactions-{date.today()}.csv"
    return StreamingResponse(
        iter([buf.getvalue()]),
        media_type="text/csv",
        headers={"Content-Disposition": f'attachment; filename="{filename}"'},
    )


# ── Remote control endpoints ─────────────────────────────────────────────────

@router.post("/api/pc/{pc_number}/message", dependencies=[Depends(_require_active_license)])
def send_pc_message(
    pc_number: int,
    body: SendMessageBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if not body.text.strip():
        raise HTTPException(status_code=422, detail="Message text cannot be empty")
    command_store.push_message(pc_number, body.text.strip())
    return {"status": "queued", "pc_number": pc_number}


@router.post("/api/pc/{pc_number}/command", dependencies=[Depends(_require_active_license)])
def send_pc_command(
    pc_number: int,
    body: SendCommandBody,
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")
    allowed = {"shutdown", "restart", "lock", "open_url"}
    if body.type not in allowed:
        raise HTTPException(status_code=422, detail=f"Unknown command type: {body.type}")
    if body.type == "open_url" and not body.payload.strip():
        raise HTTPException(status_code=422, detail="open_url requires a payload URL")
    command_store.push_command(pc_number, body.type, body.payload.strip())
    return {"status": "queued", "pc_number": pc_number, "command": body.type}


@router.post("/api/pcs/command-all", dependencies=[Depends(_require_active_license)])
def send_command_to_all_pcs(
    body: SendCommandBody,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Bulk-queue a command (shutdown / restart / lock) to every online PC.

    "Online" uses the same `last_seen` cut-off as the overview grid, so a PC
    that has gone silent without explicitly going offline is skipped.  Offline
    PCs are counted in the response so the UI can report N skipped without
    needing a second round-trip.
    """
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")
    allowed = {"shutdown", "restart", "lock"}
    if body.type not in allowed:
        raise HTTPException(status_code=422, detail=f"Unknown bulk command type: {body.type}")

    payload = body.payload.strip()
    svc = SessionService(db)
    pcs = svc.get_all_pcs()
    timeout = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)

    queued = 0
    skipped = 0
    for pc in pcs:
        online = pc.is_online and pc.last_seen and pc.last_seen >= timeout
        if not online:
            skipped += 1
            continue
        command_store.push_command(pc.pc_number, body.type, payload)
        queued += 1

    return {
        "status": "queued",
        "command": body.type,
        "queued_count": queued,
        "skipped_count": skipped,
    }


@router.post("/api/announcement", dependencies=[Depends(_require_active_license)])
def set_announcement(
    body: AnnouncementBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if not body.text.strip():
        raise HTTPException(status_code=422, detail="Announcement text cannot be empty")
    command_store.set_announcement(body.text.strip())
    return {"status": "set"}


@router.delete("/api/announcement", dependencies=[Depends(_require_active_license)])
def clear_announcement(
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.set_announcement(None)
    return {"status": "cleared"}


@router.get("/api/hardware/coin-slot")
def get_coin_slot_state(
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    pcs = db.query(PC).order_by(PC.pc_number).all()
    return {
        "global_enabled": command_store.is_coin_slot_enabled(),
        "announcement": command_store.get_announcement(),
        "per_pc": {
            pc.pc_number: command_store.is_pc_coin_enabled(pc.pc_number)
            for pc in pcs
        },
    }


@router.post("/api/hardware/coin-slot", dependencies=[Depends(_require_active_license)])
def set_global_coin_slot(
    body: CoinSlotBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.set_coin_slot_enabled(body.enabled)
    return {"status": "ok", "global_enabled": body.enabled}


@router.post("/api/pc/{pc_number}/coin-slot", dependencies=[Depends(_require_active_license)])
def set_pc_coin_slot(
    pc_number: int,
    body: CoinSlotBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.set_pc_coin_enabled(pc_number, body.enabled)
    return {"status": "ok", "pc_number": pc_number, "enabled": body.enabled}


# ── Wallpaper management ─────────────────────────────────────────────────────

_WALLPAPER_DIR = os.path.join("dashboard", "static", "wallpapers")
_ALLOWED_IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}
_MAX_WALLPAPER_SIZE = 5 * 1024 * 1024  # 5 MB


@router.get("/wallpaper", response_class=HTMLResponse)
def wallpaper_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)

    wallpapers = _list_wallpaper_files()
    wp_url, wp_hash = command_store.get_wallpaper()
    pcs = db.query(PC).order_by(PC.pc_number).all()

    return templates.TemplateResponse("wallpaper.html", {
        "request": request,
        "wallpapers": wallpapers,
        "active_url": wp_url,
        "active_hash": wp_hash,
        "pcs": pcs,
    })


@router.post("/api/wallpaper/upload", dependencies=[Depends(_require_active_license)])
async def upload_wallpaper(
    file: UploadFile = File(...),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    ext = os.path.splitext(file.filename or "")[1].lower()
    if ext not in _ALLOWED_IMAGE_EXTS:
        raise HTTPException(status_code=422, detail=f"Invalid file type: {ext}")

    data = await file.read()
    if len(data) > _MAX_WALLPAPER_SIZE:
        raise HTTPException(status_code=413, detail="File too large (max 5 MB)")

    file_hash = hashlib.md5(data).hexdigest()
    timestamp = datetime.utcnow().strftime("%Y%m%d%H%M%S")
    filename = f"wallpaper-{timestamp}{ext}"
    filepath = os.path.join(_WALLPAPER_DIR, filename)

    os.makedirs(_WALLPAPER_DIR, exist_ok=True)
    with open(filepath, "wb") as f:
        f.write(data)

    url = f"/static/wallpapers/{filename}"
    command_store.set_wallpaper(url, file_hash)

    return {"status": "ok", "url": url, "hash": file_hash, "filename": filename}


class SetWallpaperBody(BaseModel):
    filename: str
    pc_number: Optional[int] = None  # None = global, number = per-PC


@router.post("/api/wallpaper/set", dependencies=[Depends(_require_active_license)])
def set_wallpaper(
    body: SetWallpaperBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    filepath = os.path.join(_WALLPAPER_DIR, body.filename)
    if not os.path.isfile(filepath):
        raise HTTPException(status_code=404, detail="Wallpaper file not found")

    with open(filepath, "rb") as f:
        file_hash = hashlib.md5(f.read()).hexdigest()
    url = f"/static/wallpapers/{body.filename}"

    if body.pc_number is not None:
        command_store.set_pc_wallpaper(body.pc_number, url, file_hash)
    else:
        command_store.set_wallpaper(url, file_hash)

    return {"status": "ok", "url": url, "hash": file_hash}


@router.delete("/api/wallpaper", dependencies=[Depends(_require_active_license)])
def clear_wallpaper(
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.set_wallpaper(None, None)
    return {"status": "cleared"}


@router.delete("/api/wallpaper/pc/{pc_number}", dependencies=[Depends(_require_active_license)])
def clear_pc_wallpaper(
    pc_number: int,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.clear_pc_wallpaper(pc_number)
    return {"status": "cleared", "pc_number": pc_number}


@router.get("/api/wallpaper/list")
def list_wallpapers(
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    return _list_wallpaper_files()


@router.delete("/api/wallpaper/file/{filename}", dependencies=[Depends(_require_active_license)])
def delete_wallpaper_file(
    filename: str,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    # Sanitize filename to prevent path traversal
    safe = os.path.basename(filename)
    filepath = os.path.join(_WALLPAPER_DIR, safe)
    if not os.path.isfile(filepath):
        raise HTTPException(status_code=404, detail="File not found")

    # If this file is the active wallpaper, clear it
    wp_url, _ = command_store.get_wallpaper()
    if wp_url and wp_url.endswith(f"/{safe}"):
        command_store.set_wallpaper(None, None)

    os.remove(filepath)
    return {"status": "deleted", "filename": safe}


def _list_wallpaper_files() -> list[dict]:
    """List all image files in the wallpapers directory."""
    if not os.path.isdir(_WALLPAPER_DIR):
        return []
    files = []
    for f in sorted(os.listdir(_WALLPAPER_DIR), reverse=True):
        ext = os.path.splitext(f)[1].lower()
        if ext in _ALLOWED_IMAGE_EXTS:
            fpath = os.path.join(_WALLPAPER_DIR, f)
            stat = os.stat(fpath)
            files.append({
                "filename": f,
                "url": f"/static/wallpapers/{f}",
                "size_kb": round(stat.st_size / 1024, 1),
                "modified": datetime.fromtimestamp(stat.st_mtime).isoformat(),
            })
    return files


# ── Membership admin page & API ──────────────────────────────────────────────

from models import User, MembershipConfig
from schemas import MembershipConfigUpdate
from services.membership_service import MembershipService


@router.get("/settings", response_class=HTMLResponse)
def settings_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)

    msvc = MembershipService(db)
    cfg = msvc.get_config()
    srv_cfg = db.query(ServerConfig).first()
    api_key = srv_cfg.client_api_key if srv_cfg else ""
    return templates.TemplateResponse("settings.html", {
        "request": request,
        "config": cfg,
        "api_key_enabled": bool(api_key),
        "api_key_masked": (api_key[:4] + "••••••••" + api_key[-4:]) if len(api_key) >= 8 else ("••••••••" if api_key else ""),
        "branch_name": settings.BRANCH_NAME,
        "traditional_mode_enabled": srv_cfg.traditional_mode_enabled if srv_cfg else False,
        "ffmpeg_streaming_enabled": getattr(srv_cfg, "ffmpeg_streaming_enabled", True) if srv_cfg else True,
    })


class BranchNameBody(BaseModel):
    branch_name: str


@router.post("/api/settings/branch-name")
def save_branch_name(
    body: BranchNameBody,
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Save BRANCH_NAME to .env so it persists across server restarts."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    name = body.branch_name.strip()

    # Write to .env (same pattern as _enforce_secure_defaults in main.py)
    env_path = Path(__file__).parent.parent / ".env"
    lines: list[str] = []
    if env_path.exists():
        lines = env_path.read_text(encoding="utf-8").splitlines(keepends=True)

    found = False
    for i, line in enumerate(lines):
        if line.lstrip().startswith("BRANCH_NAME="):
            lines[i] = f"BRANCH_NAME={name}\n"
            found = True
            break
    if not found:
        lines.append(f"BRANCH_NAME={name}\n")

    env_path.write_text("".join(lines), encoding="utf-8")

    # Apply immediately without restart
    settings.BRANCH_NAME = name
    return {"status": "ok", "branch_name": name}


class TraditionalModeBody(BaseModel):
    enabled: bool


@router.post("/api/settings/traditional-mode")
def save_traditional_mode(
    body: TraditionalModeBody,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Toggle "Traditional Café Mode" (no coin hardware).

    Applies immediately — the next heartbeat picks it up and the client
    hides/shows Insert Coin + Add Time coin-flow UI accordingly. Manual
    add-time / adjust-balance from the dashboard is unaffected either way.
    """
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    srv_cfg = db.query(ServerConfig).first()
    if srv_cfg:
        srv_cfg.traditional_mode_enabled = body.enabled
    else:
        db.add(ServerConfig(id=1, client_api_key="", traditional_mode_enabled=body.enabled))
    db.commit()

    return {"status": "ok", "traditional_mode_enabled": body.enabled}


class FfmpegToggleBody(BaseModel):
    enabled: bool


@router.post("/api/settings/ffmpeg-streaming")
def save_ffmpeg_streaming(
    body: FfmpegToggleBody,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Enable or disable FFmpeg live streaming for the fullscreen monitor view."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    srv_cfg = db.query(ServerConfig).first()
    if not srv_cfg:
        srv_cfg = ServerConfig(id=1, client_api_key="")
        db.add(srv_cfg)
    srv_cfg.ffmpeg_streaming_enabled = body.enabled
    db.commit()
    return {"status": "ok", "ffmpeg_streaming_enabled": body.enabled}


@router.post("/api/security/generate-key")
def generate_api_key(
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Generate a new random client API key and store it in the database."""
    import secrets
    if not current_user:
        raise HTTPException(status_code=401)
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    new_key = secrets.token_hex(24)  # 48-char hex key

    srv_cfg = db.query(ServerConfig).first()
    if srv_cfg:
        srv_cfg.client_api_key = new_key
    else:
        db.add(ServerConfig(id=1, client_api_key=new_key))
    db.commit()

    # Apply immediately without restart
    settings.CLIENT_API_KEY = new_key
    return {"key": new_key}


@router.post("/api/security/clear-key")
def clear_api_key(
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Disable client API key authentication."""
    if not current_user:
        raise HTTPException(status_code=401)
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    srv_cfg = db.query(ServerConfig).first()
    if srv_cfg:
        srv_cfg.client_api_key = ""
        db.commit()

    settings.CLIENT_API_KEY = ""
    return {"status": "disabled"}


@router.get("/membership", response_class=HTMLResponse)
def membership_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)

    msvc = MembershipService(db)
    cfg = msvc.get_config()
    members = (
        db.query(User)
        .order_by(desc(User.created_at))
        .all()
    )

    # Lifetime totals per member — one aggregate query for all members rather
    # than a query per row. Pulled from CoinTransaction, which already covers
    # every way a member's balance grows: real coin inserts, admin peso-mode
    # adds, and (in Traditional Café Mode) minutes-mode adds too, since those
    # all write a CoinTransaction row (see SessionService.add_time_seconds /
    # add_time_by_pesos). This is monitoring-only — it doesn't affect balance
    # or session logic at all.
    totals_rows = (
        db.query(
            CoinTransaction.user_id,
            func.coalesce(func.sum(CoinTransaction.amount_php), 0).label("total_pesos"),
            func.coalesce(func.sum(CoinTransaction.seconds_added), 0).label("total_seconds"),
        )
        .filter(CoinTransaction.user_id.isnot(None))
        .group_by(CoinTransaction.user_id)
        .all()
    )
    totals_by_user = {row.user_id: (row.total_pesos, row.total_seconds) for row in totals_rows}

    member_data = []
    svc = SessionService(db)
    for m in members:
        logged_in_pc = None
        remaining_sec = 0
        if m.logged_in_pc_id:
            pc = db.query(PC).filter(PC.id == m.logged_in_pc_id).first()
            if pc:
                logged_in_pc = pc.pc_number
                session = svc.get_active_session(pc.pc_number)
                remaining_sec = svc.remaining_seconds(session) if session else 0

        total_pesos, total_seconds = totals_by_user.get(m.id, (0, 0))

        member_data.append({
            "id": m.id,
            "username": m.username,
            "balance_seconds": m.balance_seconds,
            "is_active": m.is_active,
            "logged_in_pc": logged_in_pc,
            "remaining_seconds": remaining_sec,
            "last_login_at": m.last_login_at,
            "created_at": m.created_at,
            "must_change_password": m.must_change_password,
            "total_pesos": total_pesos,
            "total_seconds": total_seconds,
        })

    return templates.TemplateResponse("membership.html", {
        "request": request,
        "config": cfg,
        "members": member_data,
    })


class CreateMemberBody(BaseModel):
    username: str
    initial_minutes: int = 0  # optional time to seed at creation (e.g. membership sold with time included)


@router.post("/api/membership/create-member", dependencies=[Depends(_require_active_license)])
def create_member(
    body: CreateMemberBody,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Admin-only: create a member account with an auto-generated temp password.

    Self-service registration from the client lock screen has been removed
    (see api/member.py) — this is now the only way to create a member
    account. The temp password is returned once in the JSON response for
    the admin to copy; it is never stored in plaintext or logged.

    initial_minutes seeds User.balance_seconds at creation (converted to
    seconds here, same underlying unit as AdjustBalanceBody.seconds used by
    POST .../members/{id}/adjust-balance) — used when a membership is sold
    with time already included.
    """
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    username = body.username.strip()
    if not username:
        raise HTTPException(status_code=422, detail="Username cannot be empty")
    if body.initial_minutes < 0:
        raise HTTPException(status_code=422, detail="Initial time cannot be negative")

    msvc = MembershipService(db)
    result = msvc.admin_create_member(username, initial_minutes=body.initial_minutes)
    if not result["success"]:
        raise HTTPException(status_code=422, detail=result["error"])

    return {
        "status": "created",
        "username": result["username"],
        "temp_password": result["temp_password"],
        "balance_seconds": result["balance_seconds"],
    }


@router.post("/api/membership/config", dependencies=[Depends(_require_active_license)])
def update_membership_config(
    body: MembershipConfigUpdate,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    msvc = MembershipService(db)
    cfg = msvc.update_config(**body.model_dump(exclude_unset=True))
    return {"status": "ok", "config": {
        "membership_enabled": cfg.membership_enabled,
        "absorption_enabled": cfg.absorption_enabled,
        "logout_deduction_minutes": cfg.logout_deduction_minutes,
        "minimum_logout_minutes": cfg.minimum_logout_minutes,
        "zero_time_auto_logout_seconds": cfg.zero_time_auto_logout_seconds,
        "idle_auto_shutdown_minutes": cfg.idle_auto_shutdown_minutes,
        "member_heartbeat_timeout_minutes": cfg.member_heartbeat_timeout_minutes,
    }}


class AdjustBalanceBody(BaseModel):
    seconds: int


@router.post("/api/membership/members/{member_id}/deactivate", dependencies=[Depends(_require_active_license)])
def deactivate_member(
    member_id: int,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    user = db.query(User).filter(User.id == member_id).first()
    if not user:
        raise HTTPException(status_code=404, detail="Member not found")

    # Force-logout if currently logged in
    if user.logged_in_pc_id:
        msvc = MembershipService(db)
        msvc.force_logout_member(member_id)

    user.is_active = False
    db.commit()
    return {"status": "deactivated", "member_id": member_id}


@router.post("/api/membership/members/{member_id}/activate", dependencies=[Depends(_require_active_license)])
def activate_member(
    member_id: int,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    user = db.query(User).filter(User.id == member_id).first()
    if not user:
        raise HTTPException(status_code=404, detail="Member not found")
    user.is_active = True
    db.commit()
    return {"status": "activated", "member_id": member_id}


@router.post("/api/membership/members/{member_id}/adjust-balance", dependencies=[Depends(_require_active_license)])
def adjust_member_balance(
    member_id: int,
    body: AdjustBalanceBody,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    user = db.query(User).filter(User.id == member_id).first()
    if not user:
        raise HTTPException(status_code=404, detail="Member not found")

    user.balance_seconds = max(0, user.balance_seconds + body.seconds)
    db.commit()
    return {"status": "ok", "balance_seconds": user.balance_seconds}


@router.post("/api/membership/members/{member_id}/force-logout", dependencies=[Depends(_require_active_license)])
def force_logout_member(
    member_id: int,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

    msvc = MembershipService(db)
    ok = msvc.force_logout_member(member_id)
    if not ok:
        raise HTTPException(status_code=404, detail="Member not found or not logged in")
    return {"status": "logged_out", "member_id": member_id}


@router.delete("/api/membership/members/{member_id}", dependencies=[Depends(_require_active_license)])
def delete_member(
    member_id: int,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Admin-only: permanently delete a member account.

    Historical Sessions/CoinTransactions are preserved (their user_id is
    cleared, not the rows themselves) so past earnings/reports stay
    accurate. If the member is currently logged in, they are
    force-logged-out first — same behavior as deactivate_member above.
    """
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    user = db.query(User).filter(User.id == member_id).first()
    if not user:
        raise HTTPException(status_code=404, detail="Member not found")

    if user.logged_in_pc_id:
        msvc = MembershipService(db)
        msvc.force_logout_member(member_id)
        db.refresh(user)

    db.query(SessionModel).filter(SessionModel.user_id == member_id).update(
        {"user_id": None}, synchronize_session=False
    )
    db.query(CoinTransaction).filter(CoinTransaction.user_id == member_id).update(
        {"user_id": None}, synchronize_session=False
    )

    username = user.username
    db.delete(user)
    db.commit()
    return {"status": "deleted", "member_id": member_id, "username": username}


@router.post("/api/membership/members/{member_id}/reset-password", dependencies=[Depends(_require_active_license)])
def reset_member_password(
    member_id: int,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    """Admin-only: generate a new temp password for a member who forgot
    theirs. They'll be required to set their own password on next login,
    same as a freshly created account. Does not disturb an already-active
    session — only their next login is affected.
    """
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    msvc = MembershipService(db)
    result = msvc.admin_reset_password(member_id)
    if not result["success"]:
        raise HTTPException(status_code=404, detail=result["error"])

    return {
        "status": "reset",
        "username": result["username"],
        "temp_password": result["temp_password"],
    }


# ── Staff management (admin only) ────────────────────────────────────────────

class CreateStaffBody(BaseModel):
    username: str
    password: str
    role: str  # "admin" | "cashier"


@router.get("/staff", response_class=HTMLResponse)
def staff_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    if current_user["role"] != "admin":
        return RedirectResponse("/dashboard", status_code=302)

    staff = db.query(AdminUser).order_by(AdminUser.created_at).all()
    return templates.TemplateResponse("staff.html", {
        "request": request,
        "staff": staff,
        "current_username": current_user["username"],
        "success": request.query_params.get("success"),
        "error": request.query_params.get("error"),
    })


@router.post("/api/staff/create")
def create_staff_user(
    body: CreateStaffBody,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    if body.role not in ("admin", "cashier"):
        raise HTTPException(status_code=422, detail="Role must be 'admin' or 'cashier'")

    username = body.username.strip()
    if not username:
        raise HTTPException(status_code=422, detail="Username cannot be empty")
    if len(body.password) < 6:
        raise HTTPException(status_code=422, detail="Password must be at least 6 characters")

    existing = db.query(AdminUser).filter(AdminUser.username == username).first()
    if existing:
        raise HTTPException(status_code=409, detail="Username already exists")

    hashed = bcrypt.hashpw(body.password.encode(), bcrypt.gensalt()).decode()
    new_user = AdminUser(username=username, password=hashed, role=body.role)
    db.add(new_user)
    db.commit()
    return {"status": "created", "username": username, "role": body.role}


@router.post("/api/staff/{user_id}/delete")
def delete_staff_user(
    user_id: int,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    target = db.query(AdminUser).filter(AdminUser.id == user_id).first()
    if not target:
        raise HTTPException(status_code=404, detail="User not found")
    if target.username == current_user["username"]:
        raise HTTPException(status_code=400, detail="Cannot delete your own account")

    db.delete(target)
    db.commit()
    return {"status": "deleted", "user_id": user_id}
