from datetime import datetime, timedelta
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import Response
from sqlalchemy.orm import Session

from database import get_db
from models import PC, Session as SessionModel
from schemas import PCHeartbeatResponse, PCStatusResponse
from services.session_service import SessionService
from config import settings

router = APIRouter(prefix="/api/pc", tags=["pc"])


def _get_client_ip(request: Request) -> str:
    forwarded = request.headers.get("X-Forwarded-For")
    if forwarded:
        return forwarded.split(",")[0].strip()
    return request.client.host if request.client else "unknown"


@router.post("/register")
def register_pc(
    pc_number: int,
    mac_address: str,
    request: Request,
    db: Session = Depends(get_db),
):
    """
    Called by a Windows PC client on startup.
    Registers the PC or updates its MAC/IP if it already exists.
    """
    if pc_number < 1 or pc_number > 99:
        raise HTTPException(400, "PC number must be between 1 and 99")

    ip = _get_client_ip(request)
    svc = SessionService(db)
    pc = svc.register_pc(pc_number, mac_address, ip)

    return {
        "pc_id": pc.id,
        "pc_number": pc.pc_number,
        "name": pc.name,
        "registered": True,
    }


@router.post("/heartbeat/{pc_number}", response_model=PCHeartbeatResponse)
def heartbeat(
    pc_number: int,
    request: Request,
    db: Session = Depends(get_db),
):
    """
    Called every 10 seconds by each PC client.
    Returns the current lock state and remaining session time.
    This is the primary sync mechanism between server and clients.
    """
    svc = SessionService(db)
    pc = svc.update_heartbeat(pc_number, _get_client_ip(request))

    if not pc:
        raise HTTPException(404, f"PC {pc_number} is not registered")

    session = svc.get_active_session(pc_number)

    # If this is the first heartbeat after a new session was created, reset
    # started_at to now so the client receives the full granted time.
    svc.acknowledge_session_start(pc_number, session)

    # Grab minutes-added notification before computing remaining_seconds.
    time_added = svc.pop_pending_notification(pc_number)

    remaining_sec = svc.remaining_seconds(session)

    # Auto-expire: if time ran out, end the session and lock
    if session and remaining_sec == 0:
        svc.end_session(pc_number)
        session = None

    is_locked = pc.is_locked or session is None

    return PCHeartbeatResponse(
        is_locked=is_locked,
        remaining_minutes=remaining_sec // 60,
        remaining_seconds=remaining_sec % 60,
        session_token=session.session_token if session else None,
        time_added_minutes=time_added,
    )


@router.get("/status", response_model=list[PCStatusResponse])
def all_pc_status(db: Session = Depends(get_db)):
    """Returns status of all registered PCs. Used by the admin dashboard."""
    timeout_cutoff = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)
    svc = SessionService(db)
    pcs = svc.get_all_pcs()

    result = []
    for pc in pcs:
        # Mark offline if heartbeat has been missed
        if pc.last_seen and pc.last_seen < timeout_cutoff:
            pc.is_online = False

        session = svc.get_active_session(pc.pc_number)
        remaining_sec = svc.remaining_seconds(session)

        result.append(PCStatusResponse(
            pc_number=pc.pc_number,
            name=pc.name,
            is_online=pc.is_online,
            is_locked=pc.is_locked,
            ip_address=pc.ip_address,
            last_seen=pc.last_seen,
            remaining_minutes=remaining_sec // 60,
        ))

    db.commit()
    return result


@router.post("/{pc_number}/metrics")
async def upload_metrics(pc_number: int, request: Request):
    """
    Called by PC client every ~10 s with a JSON performance snapshot.
    Stored in memory for admin monitoring. No auth required (LAN only).
    """
    try:
        data = await request.json()
    except Exception:
        raise HTTPException(400, "Invalid JSON body")
    if not isinstance(data, dict):
        raise HTTPException(400, "Expected JSON object")
    import metrics_store
    metrics_store.save(pc_number, data)
    return {"status": "ok"}


@router.post("/{pc_number}/screenshot")
async def upload_screenshot(pc_number: int, request: Request):
    """
    Called by PC client every 5 seconds with a JPEG screenshot body.
    Stores in memory for admin monitoring. No authentication required
    (already on the internal LAN, same as heartbeat).
    """
    body = await request.body()
    if not body:
        raise HTTPException(400, "Empty body")
    if len(body) > 2 * 1024 * 1024:  # 2 MB max
        raise HTTPException(413, "Screenshot too large")
    import screenshot_store
    screenshot_store.save(pc_number, body)
    return {"status": "ok"}


@router.post("/{pc_number}/lock")
def lock_pc(pc_number: int, db: Session = Depends(get_db)):
    """Admin: immediately lock a PC and end its session."""
    svc = SessionService(db)
    ok = svc.end_session(pc_number)
    if not ok:
        raise HTTPException(404, f"PC {pc_number} not found")
    return {"status": "locked", "pc_number": pc_number}
