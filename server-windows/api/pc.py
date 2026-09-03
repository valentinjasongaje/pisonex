import time
from datetime import datetime, timedelta
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import Response
from sqlalchemy.orm import Session

from database import get_db
from models import PC, Session as SessionModel, User, MembershipConfig, ServerConfig
from schemas import PCHeartbeatResponse, PCStatusResponse
from services.session_service import SessionService
from config import settings
from dependencies import verify_client_key
from api.auth import get_current_admin
import command_store

router = APIRouter(prefix="/api/pc", tags=["pc"])

_ClientAuth = Depends(verify_client_key)
# Admin JWT. Unlike verify_client_key (a no-op while CLIENT_API_KEY is empty,
# which is the default), this is always enforced — so anything that exposes the
# whole estate or changes a paid session hangs off this one.
_AdminAuth = Depends(get_current_admin)


def _get_client_ip(request: Request) -> str:
    forwarded = request.headers.get("X-Forwarded-For")
    if forwarded:
        return forwarded.split(",")[0].strip()
    return request.client.host if request.client else "unknown"


@router.post("/register", dependencies=[_ClientAuth])
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

    # Client just booted — discard any commands queued before shutdown
    # (e.g. idle-shutdown command that caused the previous shutdown would
    # be re-delivered on the first heartbeat and cause an infinite reboot loop).
    command_store.pop_command(pc_number)
    command_store.clear_idle_since(pc_number)
    command_store.clear_pc_had_session(pc_number)

    return {
        "pc_id": pc.id,
        "pc_number": pc.pc_number,
        "name": pc.name,
        "registered": True,
    }


@router.post("/heartbeat/{pc_number}", response_model=PCHeartbeatResponse,
             dependencies=[_ClientAuth])
def heartbeat(
    pc_number: int,
    request: Request,
    db: Session = Depends(get_db),
):
    """
    Called every 1 second by each PC client.
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

    # Grab seconds-added notification before computing remaining_seconds.
    time_added = svc.pop_pending_notification(pc_number)

    remaining_sec = svc.remaining_seconds(session)

    # Auto-expire: if time ran out, end the session and lock
    if session and remaining_sec == 0:
        svc.end_session(pc_number)
        session = None

    is_locked = pc.is_locked or session is None

    # Remote-control payloads delivered via heartbeat
    cmd = command_store.pop_command(pc_number)
    msg = command_store.pop_message(pc_number)
    ann = command_store.get_announcement()
    coins_ok = command_store.is_coins_allowed(pc_number)

    # Server-pushed wallpaper (persistent, not popped)
    wp_url, wp_hash = command_store.get_pc_wallpaper(pc_number)
    # Build absolute URL so client can download directly
    if wp_url:
        host = request.headers.get("host", "localhost")
        scheme = request.url.scheme
        wp_url = f"{scheme}://{host}{wp_url}"

    # ── Membership fields ─────────────────────────────────────────
    membership_enabled = False
    absorption_enabled = False
    member_username = None
    member_balance_seconds = 0
    member_can_logout = False
    zero_time_logout_seconds = 0
    idle_shutdown_seconds = 0

    cfg = db.query(MembershipConfig).first()
    if cfg:
        membership_enabled = cfg.membership_enabled
        absorption_enabled = cfg.absorption_enabled

    member_user_id = command_store.get_member_for_pc(pc_number)
    if member_user_id is not None:
        member_user = db.query(User).filter(User.id == member_user_id).first()
        if member_user:
            member_username = member_user.username
            member_balance_seconds = member_user.balance_seconds

            # Update last_activity_at (heartbeat = activity proof)
            member_user.last_activity_at = datetime.utcnow()
            db.commit()

            # Can logout? (Case 26: zero-time always allowed)
            if not session or remaining_sec == 0:
                member_can_logout = True
            elif cfg and remaining_sec >= cfg.minimum_logout_minutes * 60:
                member_can_logout = True

            # Zero-time countdown
            if cfg and not session:
                zt_since = command_store.get_zero_time_since(pc_number)
                if zt_since is not None:
                    elapsed = time.time() - zt_since
                    zero_time_logout_seconds = max(0, int(cfg.zero_time_auto_logout_seconds - elapsed))

    # Idle auto-shutdown countdown (only when no session, no member, PC locked)
    if is_locked and session is None and member_user_id is None and cfg:
        idle_since = command_store.get_idle_since(pc_number)
        if idle_since is None:
            command_store.set_idle_since(pc_number, time.time())
        else:
            elapsed = time.time() - idle_since
            timeout = cfg.idle_auto_shutdown_minutes * 60
            if timeout > 0:
                idle_shutdown_seconds = max(0, int(timeout - elapsed))
    else:
        command_store.clear_idle_since(pc_number)

    # Is the hardware controller currently accepting coins for this PC?
    receiving_coins = command_store.is_receiving_coins(pc_number)

    # Business-model toggle: "Traditional Café Mode" hides all coin-flow UI
    # client-side. Defaults False (row is always seeded by _seed_defaults()).
    srv_cfg = db.query(ServerConfig).first()
    traditional_mode_enabled = srv_cfg.traditional_mode_enabled if srv_cfg else False

    # Minimum logout minutes (from membership config, 0 if not configured)
    minimum_logout_minutes = cfg.minimum_logout_minutes if cfg else 0

    return PCHeartbeatResponse(
        is_locked=is_locked,
        remaining_seconds=remaining_sec,
        session_token=session.session_token if session else None,
        time_added_seconds=time_added,
        pending_command=cmd["type"] if cmd else None,
        command_payload=cmd["payload"] if cmd else None,
        admin_message=msg,
        announcement=ann,
        coin_slot_enabled=coins_ok,
        traditional_mode_enabled=traditional_mode_enabled,
        wallpaper_url=wp_url,
        wallpaper_hash=wp_hash,
        membership_enabled=membership_enabled,
        absorption_enabled=absorption_enabled,
        member_username=member_username,
        member_balance_seconds=member_balance_seconds,
        member_can_logout=member_can_logout,
        zero_time_logout_seconds=zero_time_logout_seconds,
        idle_shutdown_seconds=idle_shutdown_seconds,
        receiving_coins=receiving_coins,
        # Coin progress fields are always 0 on the Windows variant — there is
        # no hardware to populate them.  Present so the schema matches
        # server-orangepi/ and the client deserialises cleanly.
        coin_progress_pesos=0,
        coin_progress_seconds=0,
        minimum_logout_minutes=minimum_logout_minutes,
        # When an admin is watching this PC AND FFmpeg streaming is enabled,
        # tell the client to ramp up; 0 = use own config (1 s JPEG snapshots).
        capture_interval_ms=(
            33
            if command_store.is_watched(pc_number)
            and getattr(srv_cfg, "ffmpeg_streaming_enabled", True)
            else 0
        ),
    )


@router.get("/status", response_model=list[PCStatusResponse],
            dependencies=[_AdminAuth])
def all_pc_status(db: Session = Depends(get_db)):
    """Returns status of all registered PCs (number, name, IP, lock state,
    remaining time). Admin-only — this is the estate inventory, and unauthenticated
    it told an attacker exactly which PC numbers were live and worth targeting.
    The dashboard renders the same data server-side via its own cookie-authed routes.
    """
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
            remaining_seconds=remaining_sec,
        ))

    db.commit()
    return result


@router.post("/{pc_number}/metrics", dependencies=[_ClientAuth])
async def upload_metrics(pc_number: int, request: Request):
    """
    Called by PC client every ~10 s with a JSON performance snapshot.
    Stored in memory for admin monitoring.
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


@router.post("/{pc_number}/screenshot", dependencies=[_ClientAuth])
async def upload_screenshot(pc_number: int, request: Request):
    """
    Called by PC client every 5 seconds with a JPEG screenshot body.
    Stores in memory for admin monitoring.
    """
    body = await request.body()
    if not body:
        raise HTTPException(400, "Empty body")
    if len(body) > 2 * 1024 * 1024:  # 2 MB max
        raise HTTPException(413, "Screenshot too large")
    import screenshot_store
    screenshot_store.save(pc_number, body)
    return {"status": "ok"}


# NOTE: the "lock a PC" endpoint used to live here, unauthenticated, letting
# anyone on the LAN end a paying customer's session. It was an exact duplicate of
# POST /api/admin/pc/{pc_number}/lock (api/admin.py), which is correctly gated
# behind an admin JWT — so it was removed rather than re-gated. Use the admin
# route, or the dashboard's own /dashboard/api/pc/{n}/lock.


@router.post("/{pc_number}/request-coins", dependencies=[_ClientAuth])
def request_coins(pc_number: int, db: Session = Depends(get_db)):
    """
    Called by a PC client when the user presses 'Insert Coin'.
    Signals the server's hardware controller to open the coin slot for this PC.
    Returns 503 on Windows/no-hardware deployments where the controller is absent.
    """
    srv_cfg = db.query(ServerConfig).first()
    if srv_cfg and srv_cfg.traditional_mode_enabled:
        # Defense in depth: the client hides this button when
        # traditional_mode_enabled is True, but reject here too in case a
        # stale/cached client UI still shows it (e.g. Traditional Café Mode
        # was just turned on).
        raise HTTPException(
            status_code=403,
            detail="Coins are disabled on this server. This café uses cashier-managed time only.",
        )

    from main import hw_controller
    if hw_controller is None:
        raise HTTPException(
            status_code=503,
            detail="No coin slot hardware on this server. Use the keypad unit instead.",
        )
    ok, message = hw_controller.request_coins_for_pc(pc_number)
    if not ok:
        raise HTTPException(status_code=409, detail=message)
    return {"status": "ok"}


@router.post("/{pc_number}/done-coins", dependencies=[_ClientAuth])
def done_coins(pc_number: int):
    """
    Called by a PC client when the user presses 'Done inserting Coins'.
    Flushes any coins still being counted and closes the slot immediately.
    Returns 503 on Windows/no-hardware deployments where the controller is absent
    (the client will receive a clean error instead of a 404, which would otherwise
    leave it stuck waiting for the receiving-coins panel to close).
    """
    from main import hw_controller
    if hw_controller is None:
        raise HTTPException(
            status_code=503,
            detail="No coin slot hardware on this server. Use the keypad unit instead.",
        )
    ok, message = hw_controller.close_coins_for_pc(pc_number)
    if not ok:
        raise HTTPException(status_code=409, detail=message)
    return {"status": "ok"}
