from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session

from database import get_db
from schemas import AddTimeRequest, AddTimeResponse, SessionStatusResponse
from services.session_service import SessionService

router = APIRouter(prefix="/api/session", tags=["session"])


@router.post("/add-time", response_model=AddTimeResponse)
def add_time(body: AddTimeRequest, db: Session = Depends(get_db)):
    """
    Called by the hardware controller (coin slot) after coin insertion.
    Converts pesos to minutes and creates or extends a PC session.
    Also called by admin to manually top-up via the dashboard.
    """
    svc = SessionService(db)
    pc = svc.get_pc(body.pc_number)

    if not pc:
        raise HTTPException(404, f"PC {body.pc_number} not registered")
    if not pc.is_online:
        raise HTTPException(400, f"PC {body.pc_number} is offline")
    if body.pesos <= 0:
        raise HTTPException(422, "Pesos must be greater than 0")

    try:
        minutes, session = svc.add_time_by_pesos(body.pc_number, body.pesos)
    except ValueError as e:
        raise HTTPException(422, str(e))

    return AddTimeResponse(
        pc_number=body.pc_number,
        pesos_added=body.pesos,
        minutes_added=minutes,
        total_minutes=session.minutes_granted,
        session_token=session.session_token,
    )


@router.get("/{pc_number}", response_model=SessionStatusResponse)
def get_session(pc_number: int, db: Session = Depends(get_db)):
    """Returns the current session status for a given PC."""
    svc = SessionService(db)
    session = svc.get_active_session(pc_number)

    if not session:
        return SessionStatusResponse(
            has_session=False,
            remaining_minutes=0,
            remaining_seconds=0,
        )

    remaining_sec = svc.remaining_seconds(session)
    return SessionStatusResponse(
        has_session=True,
        remaining_minutes=remaining_sec // 60,
        remaining_seconds=remaining_sec % 60,
        minutes_granted=session.minutes_granted,
        started_at=session.started_at,
        session_token=session.session_token,
    )


@router.post("/{pc_number}/end")
def end_session(pc_number: int, db: Session = Depends(get_db)):
    """Admin or client: end the current session and lock the PC."""
    svc = SessionService(db)
    ok = svc.end_session(pc_number)
    if not ok:
        raise HTTPException(404, f"PC {pc_number} not found")
    return {"status": "session ended", "pc_number": pc_number}
