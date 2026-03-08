from datetime import datetime, timedelta
from fastapi import APIRouter, Request, Depends, Form
from fastapi.responses import HTMLResponse, RedirectResponse
from fastapi.templating import Jinja2Templates
from sqlalchemy.orm import Session
from sqlalchemy import func, desc

from database import get_db, SessionLocal
from models import CoinTransaction, SystemLog, CoinRate
from services.session_service import SessionService
from config import settings

router = APIRouter(prefix="/dashboard")
templates = Jinja2Templates(directory="dashboard/templates")


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
        })

    db.commit()

    today = datetime.utcnow().date()
    today_row = db.query(
        func.coalesce(func.sum(CoinTransaction.amount_pesos), 0)
    ).filter(
        func.date(CoinTransaction.created_at) == today
    ).scalar()

    return pc_data, online_count, active_count, int(today_row)


@router.get("", response_class=HTMLResponse)
@router.get("/", response_class=HTMLResponse)
def overview(request: Request, db: Session = Depends(get_db)):
    pcs, online_count, active_count, today_pesos = _pc_overview_data(db)
    return templates.TemplateResponse("overview.html", {
        "request": request,
        "pcs": pcs,
        "online_count": online_count,
        "active_count": active_count,
        "today_pesos": today_pesos,
    })


@router.get("/partials/pc-grid", response_class=HTMLResponse)
def pc_grid_partial(request: Request, db: Session = Depends(get_db)):
    """HTMX partial — returns only the PC grid div for auto-refresh."""
    pcs, online_count, active_count, today_pesos = _pc_overview_data(db)
    return templates.TemplateResponse("partials/pc_grid.html", {
        "request": request,
        "pcs": pcs,
    })


@router.get("/rates", response_class=HTMLResponse)
def rates_page(request: Request, db: Session = Depends(get_db)):
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
):
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
):
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
def logs_page(request: Request, db: Session = Depends(get_db)):
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
