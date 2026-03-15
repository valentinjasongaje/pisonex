import csv
import io
from datetime import datetime, date, timedelta
from typing import Optional

from fastapi import APIRouter, Request, Depends, Form, Cookie, HTTPException
from fastapi.responses import HTMLResponse, RedirectResponse, Response, StreamingResponse
from fastapi.templating import Jinja2Templates
from sqlalchemy.orm import Session
from sqlalchemy import func, desc
from jose import JWTError, jwt
import bcrypt

from pydantic import BaseModel

from database import get_db
from models import AdminUser, CoinTransaction, SystemLog, CoinRate, PC, Session as SessionModel
from schemas import AdminAddTimeRequest
from services.session_service import SessionService
from config import settings
import command_store


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


@router.delete("/rates/{rate_id}", response_class=HTMLResponse)
def delete_rate(
    rate_id: int,
    request: Request,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return HTMLResponse(status_code=401, content="")
    rate = db.query(CoinRate).filter(CoinRate.id == rate_id).first()
    if rate:
        rate.is_active = False
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
    days: int = 0,
    pc_id: Optional[int] = None,
    db: Session = Depends(get_db),
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)

    query = db.query(CoinTransaction)
    if days and days > 0:
        since = datetime.utcnow() - timedelta(days=days)
        query = query.filter(CoinTransaction.created_at >= since)
    if pc_id:
        query = query.filter(CoinTransaction.pc_id == pc_id)

    transactions = (
        query
        .order_by(desc(CoinTransaction.created_at))
        .limit(1000)
        .all()
    )
    total_pesos = sum(t.amount_pesos for t in transactions)
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


# ── Documentation pages ───────────────────────────────────────────────────────

@router.get("/docs/api", response_class=HTMLResponse)
def docs_api_page(
    request: Request,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
    return templates.TemplateResponse("docs_api.html", {"request": request})


@router.get("/docs/wiring", response_class=HTMLResponse)
def docs_wiring_page(
    request: Request,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
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


# ── Reports page ──────────────────────────────────────────────────────────────

def _reports_data(days: int, db):
    """Shared helper — compute all aggregate data for the reports page/export."""
    today_start = datetime.utcnow().replace(hour=0, minute=0, second=0, microsecond=0)
    week_start  = today_start - timedelta(days=today_start.weekday())
    month_start = today_start.replace(day=1)

    def _sum(q):
        row = q.with_entities(
            func.coalesce(func.sum(CoinTransaction.amount_pesos), 0).label("pesos"),
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
            func.coalesce(func.sum(CoinTransaction.amount_pesos), 0).label("pesos"),
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
            func.sum(CoinTransaction.amount_pesos).label("pesos"),
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
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)
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
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        return RedirectResponse("/dashboard/login", status_code=302)

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
    writer.writerow(["Date", "PC Number", "PC Name", "Amount (₱)", "Minutes Added"])
    for tx, pc_number, pc_name in query.all():
        writer.writerow([
            tx.created_at.strftime("%Y-%m-%d %H:%M:%S"),
            pc_number or "",
            pc_name or "",
            tx.amount_pesos,
            tx.minutes_added,
        ])

    buf.seek(0)
    filename = f"pisonet-transactions-{date.today()}.csv"
    return StreamingResponse(
        iter([buf.getvalue()]),
        media_type="text/csv",
        headers={"Content-Disposition": f'attachment; filename="{filename}"'},
    )


# ── Remote control endpoints ─────────────────────────────────────────────────

@router.post("/api/pc/{pc_number}/message")
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


@router.post("/api/pc/{pc_number}/command")
def send_pc_command(
    pc_number: int,
    body: SendCommandBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    allowed = {"shutdown", "restart", "lock", "open_url"}
    if body.type not in allowed:
        raise HTTPException(status_code=422, detail=f"Unknown command type: {body.type}")
    if body.type == "open_url" and not body.payload.strip():
        raise HTTPException(status_code=422, detail="open_url requires a payload URL")
    command_store.push_command(pc_number, body.type, body.payload.strip())
    return {"status": "queued", "pc_number": pc_number, "command": body.type}


@router.post("/api/announcement")
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


@router.delete("/api/announcement")
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


@router.post("/api/hardware/coin-slot")
def set_global_coin_slot(
    body: CoinSlotBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.set_coin_slot_enabled(body.enabled)
    return {"status": "ok", "global_enabled": body.enabled}


@router.post("/api/pc/{pc_number}/coin-slot")
def set_pc_coin_slot(
    pc_number: int,
    body: CoinSlotBody,
    current_user: Optional[str] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    command_store.set_pc_coin_enabled(pc_number, body.enabled)
    return {"status": "ok", "pc_number": pc_number, "enabled": body.enabled}
