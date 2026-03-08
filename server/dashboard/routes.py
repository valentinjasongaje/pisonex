from datetime import datetime, timedelta
from typing import Optional

from fastapi import APIRouter, Request, Depends, Form, Cookie, HTTPException
from fastapi.responses import HTMLResponse, RedirectResponse, Response
from fastapi.templating import Jinja2Templates
from sqlalchemy.orm import Session
from sqlalchemy import func, desc
from jose import JWTError, jwt
import bcrypt

from pydantic import BaseModel

from database import get_db
from models import AdminUser, CoinTransaction, SystemLog, CoinRate, PC
from schemas import AdminAddTimeRequest
from services.session_service import SessionService
from config import settings


class RenamePcBody(BaseModel):
    name: str

router = APIRouter(prefix="/dashboard")
templates = Jinja2Templates(directory="dashboard/templates")

_ALGORITHM = "HS256"
_COOKIE_NAME = "pisonet_session"


# ── Session cookie helpers ────────────────────────────────────────────────────

def _validate_session(pisonet_session: str = Cookie(default=None)) -> Optional[str]:
    """Returns the admin username from the session cookie, or None if invalid/absent."""
    if not pisonet_session:
        return None
    try:
        payload = jwt.decode(pisonet_session, settings.SECRET_KEY, algorithms=[_ALGORITHM])
        username: str = payload.get("sub")
        return username if username else None
    except JWTError:
        return None


def _create_session_token(username: str) -> str:
    payload = {
        "sub": username,
        "role": "admin",
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

    token = _create_session_token(admin.username)
    response = RedirectResponse("/dashboard", status_code=302)
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

def _pc_overview_data(db: Session):
    svc = SessionService(db)
    pcs = svc.get_all_pcs()
    timeout = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)

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
        })

    db.commit()

    today = datetime.utcnow().date()
    today_row = db.query(
        func.coalesce(func.sum(CoinTransaction.amount_pesos), 0)
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
    return templates.TemplateResponse("overview.html", {
        "request": request,
        "pcs": pcs,
        "online_count": online_count,
        "active_count": active_count,
        "today_pesos": today_pesos,
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
    })


@router.get("/rates", response_class=HTMLResponse)
def rates_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    rates = (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True)
        .order_by(CoinRate.pesos.asc())
        .all()
    )
    return templates.TemplateResponse("rates.html", {
        "request": request,
        "rates": rates,
    })


@router.post("/rates", response_class=HTMLResponse)
def save_rate(
    request: Request,
    pesos: int = Form(...),
    minutes: int = Form(...),
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    existing = db.query(CoinRate).filter(
        CoinRate.pesos == pesos, CoinRate.is_active == True
    ).first()
    if existing:
        existing.is_active = False

    rate = CoinRate(
        pesos=pesos,
        minutes=minutes,
        label=f"₱{pesos} = {minutes} minutes",
    )
    db.add(rate)
    db.commit()

    rates = (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True)
        .order_by(CoinRate.pesos.asc())
        .all()
    )
    return templates.TemplateResponse("partials/rates_table.html", {
        "request": request,
        "rates": rates,
    })


@router.get("/transactions", response_class=HTMLResponse)
def transactions_page(
    request: Request,
    days: int = 30,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    since = datetime.utcnow() - timedelta(days=days)
    transactions = (
        db.query(CoinTransaction)
        .filter(CoinTransaction.created_at >= since)
        .order_by(desc(CoinTransaction.created_at))
        .limit(500)
        .all()
    )
    total_pesos = sum(t.amount_pesos for t in transactions)
    return templates.TemplateResponse("transactions.html", {
        "request": request,
        "transactions": transactions,
        "total_pesos": total_pesos,
        "days": days,
    })


@router.get("/logs", response_class=HTMLResponse)
def logs_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
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

@router.post("/api/pc/add-time")
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

    session = svc.add_time_minutes(body.pc_number, body.minutes)
    return {
        "pc_number": body.pc_number,
        "minutes_added": body.minutes,
        "total_minutes": session.minutes_granted,
    }


@router.post("/api/pc/{pc_number}/lock")
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


@router.post("/api/pc/{pc_number}/rename")
def dashboard_rename_pc(
    pc_number: int,
    body: RenamePcBody,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    """Rename a PC — called from the PC Management page."""
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")

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
        })
    db.commit()

    return templates.TemplateResponse("monitor.html", {
        "request": request,
        "pcs": pc_data,
    })


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


# ── PC Management page ────────────────────────────────────────────────────────

@router.get("/pcs", response_class=HTMLResponse)
def pcs_page(
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)

    svc = SessionService(db)
    pcs = svc.get_all_pcs()
    timeout = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)

    pc_data = []
    for pc in pcs:
        if pc.last_seen and pc.last_seen < timeout:
            pc.is_online = False
        session = svc.get_active_session(pc.pc_number)
        remaining_sec = svc.remaining_seconds(session)
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
        })
    db.commit()

    return templates.TemplateResponse("pcs.html", {
        "request": request,
        "pcs": pc_data,
        "total": len(pc_data),
    })
