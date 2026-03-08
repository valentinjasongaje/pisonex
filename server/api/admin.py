from datetime import datetime, timedelta
from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session
from sqlalchemy import func, desc

from database import get_db
from models import CoinTransaction, SystemLog, CoinRate, PC, Session as SessionModel
from schemas import (
    AdminAddTimeRequest, EarningsResponse, TransactionResponse,
    LogResponse, CoinRateCreate, CoinRateResponse,
)
from services.session_service import SessionService
from api.auth import get_current_admin

router = APIRouter(prefix="/api/admin", tags=["admin"])

# All admin endpoints require a valid JWT token
AdminDep = Depends(get_current_admin)


# ── Earnings ──────────────────────────────────────────────────────

@router.get("/earnings", response_model=EarningsResponse, dependencies=[AdminDep])
def get_earnings(days: int = 30, db: Session = Depends(get_db)):
    since = datetime.utcnow() - timedelta(days=days)
    row = db.query(
        func.coalesce(func.sum(CoinTransaction.amount_pesos), 0).label("total_pesos"),
        func.count(CoinTransaction.id).label("total_transactions"),
    ).filter(CoinTransaction.created_at >= since).first()

    return EarningsResponse(
        total_pesos=row.total_pesos,
        total_transactions=row.total_transactions,
        period_days=days,
    )


@router.get("/earnings/daily", dependencies=[AdminDep])
def get_daily_earnings(days: int = 7, db: Session = Depends(get_db)):
    """Returns per-day earnings for charting."""
    since = datetime.utcnow() - timedelta(days=days)
    rows = db.query(
        func.date(CoinTransaction.created_at).label("date"),
        func.sum(CoinTransaction.amount_pesos).label("total_pesos"),
        func.count(CoinTransaction.id).label("transactions"),
    ).filter(
        CoinTransaction.created_at >= since
    ).group_by(
        func.date(CoinTransaction.created_at)
    ).order_by("date").all()

    return [
        {"date": str(r.date), "total_pesos": r.total_pesos, "transactions": r.transactions}
        for r in rows
    ]


# ── Transactions ──────────────────────────────────────────────────

@router.get("/transactions", response_model=list[TransactionResponse], dependencies=[AdminDep])
def get_transactions(
    limit: int = 100,
    offset: int = 0,
    db: Session = Depends(get_db),
):
    return (
        db.query(CoinTransaction)
        .order_by(desc(CoinTransaction.created_at))
        .offset(offset)
        .limit(limit)
        .all()
    )


# ── Coin Rates ────────────────────────────────────────────────────

@router.get("/rates", response_model=list[CoinRateResponse], dependencies=[AdminDep])
def get_rates(db: Session = Depends(get_db)):
    return (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True)
        .order_by(CoinRate.pesos.asc())
        .all()
    )


@router.post("/rates", response_model=CoinRateResponse, dependencies=[AdminDep])
def set_rate(body: CoinRateCreate, db: Session = Depends(get_db)):
    """Create or update the rate for a given peso amount."""
    if body.pesos <= 0 or body.minutes <= 0:
        raise HTTPException(422, "Pesos and minutes must be greater than 0")

    # Deactivate existing rate for same peso value
    existing = db.query(CoinRate).filter(
        CoinRate.pesos == body.pesos, CoinRate.is_active == True
    ).first()
    if existing:
        existing.is_active = False

    label = body.label or f"₱{body.pesos} = {body.minutes} minutes"
    rate = CoinRate(pesos=body.pesos, minutes=body.minutes, label=label)
    db.add(rate)
    db.commit()
    db.refresh(rate)
    return rate


@router.delete("/rates/{rate_id}", dependencies=[AdminDep])
def delete_rate(rate_id: int, db: Session = Depends(get_db)):
    rate = db.query(CoinRate).filter(CoinRate.id == rate_id).first()
    if not rate:
        raise HTTPException(404, "Rate not found")
    rate.is_active = False
    db.commit()
    return {"status": "deleted"}


# ── PC Management ─────────────────────────────────────────────────

@router.post("/pc/add-time", dependencies=[AdminDep])
def admin_add_time(body: AdminAddTimeRequest, db: Session = Depends(get_db)):
    """Admin manually adds minutes to a PC without coin conversion."""
    svc = SessionService(db)
    pc = svc.get_pc(body.pc_number)
    if not pc:
        raise HTTPException(404, f"PC {body.pc_number} not found")
    if body.minutes <= 0:
        raise HTTPException(422, "Minutes must be greater than 0")

    session = svc.add_time_minutes(body.pc_number, body.minutes)
    return {
        "pc_number": body.pc_number,
        "minutes_added": body.minutes,
        "total_minutes": session.minutes_granted,
    }


@router.post("/pc/{pc_number}/lock", dependencies=[AdminDep])
def admin_lock_pc(pc_number: int, db: Session = Depends(get_db)):
    svc = SessionService(db)
    ok = svc.end_session(pc_number)
    if not ok:
        raise HTTPException(404, f"PC {pc_number} not found")
    return {"status": "locked"}


# ── System Logs ───────────────────────────────────────────────────

@router.get("/logs", response_model=list[LogResponse], dependencies=[AdminDep])
def get_logs(
    limit: int = 200,
    level: str = None,
    source: str = None,
    db: Session = Depends(get_db),
):
    q = db.query(SystemLog).order_by(desc(SystemLog.created_at))
    if level:
        q = q.filter(SystemLog.level == level.upper())
    if source:
        q = q.filter(SystemLog.source == source)
    return q.limit(limit).all()


@router.delete("/logs", dependencies=[AdminDep])
def clear_old_logs(days: int = 30, db: Session = Depends(get_db)):
    """Delete logs older than N days."""
    cutoff = datetime.utcnow() - timedelta(days=days)
    deleted = db.query(SystemLog).filter(SystemLog.created_at < cutoff).delete()
    db.commit()
    return {"deleted": deleted}
