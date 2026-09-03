from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session

from database import get_db
from schemas import AddTimeRequest, AddTimeResponse, SessionStatusResponse
from services.session_service import SessionService
from services.membership_service import MembershipService
from api.auth import get_current_admin
from dependencies import verify_client_key
import command_store

router = APIRouter(prefix="/api/session", tags=["session"])

# Granting time and ending sessions are money operations — they require a real
# admin JWT, never the shared client key.  verify_client_key is a no-op when
# CLIENT_API_KEY is empty (the default), so it cannot protect anything that
# matters on a stock install.
AdminDep = Depends(get_current_admin)
_ClientAuth = Depends(verify_client_key)


@router.post("/add-time", response_model=AddTimeResponse)
def add_time(body: AddTimeRequest, db: Session = Depends(get_db), admin=AdminDep):
    """
    Admin/integration endpoint: credit a PC as though coins were inserted.
    Converts pesos to seconds and creates or extends a PC session.
    Member-aware: if a member is logged in, the transaction is associated with them.

    The coin acceptor does NOT come through here — hardware/controller.py calls
    SessionService.add_time_by_pesos() directly, in-process.
    """
    svc = SessionService(db)
    pc = svc.get_pc(body.pc_number)

    if not pc:
        raise HTTPException(404, f"PC {body.pc_number} not registered")
    if not pc.is_online:
        raise HTTPException(400, f"PC {body.pc_number} is offline")
    if body.pesos <= 0:
        raise HTTPException(422, "Pesos must be greater than 0")

    # Check if a member is logged in — associate transaction with them
    user_id = command_store.get_member_for_pc(body.pc_number)

    # If member is in zero-time state, coin insertion cancels the auto-logout timer
    if user_id is not None:
        command_store.clear_zero_time_since(body.pc_number)
        command_store.clear_idle_since(body.pc_number)

    try:
        seconds, session = svc.add_time_by_pesos(
            body.pc_number, body.pesos, user_id=user_id, actor=admin.username
        )
    except ValueError as e:
        raise HTTPException(422, str(e))

    if user_id is not None:
        MembershipService(db).award_coin_points(user_id, session.pc_id, body.pesos)

    return AddTimeResponse(
        pc_number=body.pc_number,
        pesos_added=body.pesos,
        seconds_added=seconds,
        total_seconds=session.granted_seconds,
        session_token=session.session_token,
    )


@router.get("/{pc_number}", response_model=SessionStatusResponse,
            dependencies=[_ClientAuth])
def get_session(pc_number: int, db: Session = Depends(get_db)):
    """Returns the current session status for a given PC."""
    svc = SessionService(db)
    session = svc.get_active_session(pc_number)

    if not session:
        return SessionStatusResponse(
            has_session=False,
            remaining_seconds=0,
        )

    remaining_sec = svc.remaining_seconds(session)
    return SessionStatusResponse(
        has_session=True,
        remaining_seconds=remaining_sec,
        granted_seconds=session.granted_seconds,
        started_at=session.started_at,
        session_token=session.session_token,
    )


@router.post("/{pc_number}/end")
def end_session(pc_number: int, db: Session = Depends(get_db), admin=AdminDep):
    """Admin: end the current session and lock the PC."""
    svc = SessionService(db)
    ok = svc.end_session(pc_number, actor=admin.username)
    if not ok:
        raise HTTPException(404, f"PC {pc_number} not found")
    return {"status": "session ended", "pc_number": pc_number}
