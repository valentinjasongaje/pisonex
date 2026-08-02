import uuid
import logging
from datetime import datetime, timezone
from sqlalchemy.orm import Session as DBSession
from sqlalchemy import func
from models import PC, Session, CoinTransaction, SystemLog, User, ServerConfig
from services.rate_service import pesos_to_seconds, pesos_for_seconds
import command_store

logger = logging.getLogger(__name__)

# ── Module-level pending state (survives requests, resets on server restart) ──
# Tracks PCs with a brand-new session that the client hasn't acknowledged yet.
# On first client heartbeat we reset started_at so the full time is granted.
_pending_start: set[int] = set()
# How many seconds were added since the client's last heartbeat (for notification).
_pending_notify: dict[int, int] = {}


class SessionService:
    def __init__(self, db: DBSession):
        self._db = db

    # ── PC helpers ────────────────────────────────────────────────

    def get_pc(self, pc_number: int) -> PC | None:
        return (
            self._db.query(PC)
            .filter(PC.pc_number == pc_number)
            .first()
        )

    def get_all_pcs(self) -> list[PC]:
        return self._db.query(PC).order_by(PC.pc_number).all()

    def register_pc(self, pc_number: int, mac_address: str, ip_address: str = None) -> PC:
        pc = self.get_pc(pc_number)
        if pc:
            pc.mac_address = mac_address
            pc.ip_address = ip_address
            pc.last_seen = datetime.utcnow()
            pc.is_online = True
        else:
            pc = PC(
                pc_number=pc_number,
                name=f"PC {pc_number:02d}",
                mac_address=mac_address,
                ip_address=ip_address,
                is_locked=True,
                is_online=True,
                last_seen=datetime.utcnow(),
            )
            self._db.add(pc)
            self._log("INFO", "pc", f"Registered PC {pc_number:02d} ({mac_address})")
        self._db.commit()
        self._db.refresh(pc)
        return pc

    def update_heartbeat(self, pc_number: int, ip_address: str = None) -> PC | None:
        pc = self.get_pc(pc_number)
        if not pc:
            return None
        pc.last_seen = datetime.utcnow()
        pc.is_online = True
        if ip_address:
            pc.ip_address = ip_address
        self._db.commit()
        return pc

    # ── Session helpers ───────────────────────────────────────────

    def get_active_session(self, pc_number: int) -> Session | None:
        pc = self.get_pc(pc_number)
        if not pc:
            return None
        return (
            self._db.query(Session)
            .filter(Session.pc_id == pc.id, Session.is_active == True)
            .first()
        )

    def remaining_seconds(self, session: Session) -> int:
        if not session or not session.is_active:
            return 0
        elapsed = (datetime.utcnow() - session.started_at).total_seconds()
        return max(0, int(session.granted_seconds - elapsed))

    def add_time_by_pesos(self, pc_number: int, pesos: int, user_id: int = None) -> tuple[int, Session]:
        """
        Converts pesos to seconds, creates or extends an active session.
        If user_id is provided, associates the transaction with that member.
        Returns (seconds_added, session).
        """
        pc = self.get_pc(pc_number)
        if not pc:
            raise ValueError(f"PC {pc_number} not found")

        profile_id = pc.rate_profile_id or 1
        seconds = pesos_to_seconds(pesos, self._db, profile_id=profile_id)
        if seconds == 0:
            raise ValueError(f"₱{pesos} does not convert to any time")

        session = self.get_active_session(pc_number)

        if session:
            session.granted_seconds += seconds
        else:
            session = Session(
                pc_id=pc.id,
                user_id=user_id,
                granted_seconds=seconds,
                session_token=str(uuid.uuid4()),
                started_at=datetime.utcnow(),
            )
            self._db.add(session)
            pc.is_locked = False
            _pending_start.add(pc_number)
            command_store.mark_pc_had_session(pc_number)

        _pending_notify[pc_number] = _pending_notify.get(pc_number, 0) + seconds

        tx = CoinTransaction(
            pc_id=pc.id,
            user_id=user_id,
            amount_php=pesos,
            seconds_added=seconds,
        )
        self._db.add(tx)
        self._log(
            "INFO", "session",
            f"₱{pesos} → {seconds}s added to PC {pc_number:02d}"
        )
        self._db.commit()
        self._db.refresh(session)
        return seconds, session

    def add_time_seconds(self, pc_number: int, seconds: int, user_id: int = None) -> Session:
        """Admin: directly add seconds without coin conversion.

        In Traditional Café Mode (ServerConfig.traditional_mode_enabled), this
        is the ONLY way time is ever added — there's no physical coin insert
        to fall back on for earnings data. To keep Reports/the nightly
        earnings sync meaningful, a CoinTransaction is logged with a
        reverse-calculated estimated peso amount (see
        rate_service.pesos_for_seconds). In normal piso-net mode, no
        transaction is created here — coins remain the primary income record
        and this manual add-time path stays untracked, same as before.
        """
        pc = self.get_pc(pc_number)
        if not pc:
            raise ValueError(f"PC {pc_number} not found")

        session = self.get_active_session(pc_number)
        if session:
            session.granted_seconds += seconds
        else:
            session = Session(
                pc_id=pc.id,
                user_id=user_id,
                granted_seconds=seconds,
                session_token=str(uuid.uuid4()),
                started_at=datetime.utcnow(),
            )
            self._db.add(session)
            pc.is_locked = False
            _pending_start.add(pc_number)
            command_store.mark_pc_had_session(pc_number)

        _pending_notify[pc_number] = _pending_notify.get(pc_number, 0) + seconds

        srv_cfg = self._db.query(ServerConfig).first()
        if srv_cfg and srv_cfg.traditional_mode_enabled:
            profile_id = pc.rate_profile_id or 1
            pesos = pesos_for_seconds(seconds, self._db, profile_id=profile_id)
            tx = CoinTransaction(
                pc_id=pc.id,
                user_id=user_id,
                amount_php=pesos,
                seconds_added=seconds,
            )
            self._db.add(tx)
            self._log(
                "INFO", "admin",
                f"₱{pesos} (est.) → {seconds}s manually added to PC {pc_number:02d} "
                f"(Traditional Café Mode)"
            )
        else:
            self._log("INFO", "admin", f"Admin added {seconds}s to PC {pc_number:02d}")

        self._db.commit()
        self._db.refresh(session)
        return session

    def acknowledge_session_start(self, pc_number: int, session) -> None:
        """
        Called on each client heartbeat. Keeps `started_at` pinned to "now"
        while the session is "pending" — i.e. created but not yet acknowledged
        as ticking by the user.

        A session enters pending state in two situations:
          1. A new session was just created (first coin or admin add-time) and
             the client hasn't received it yet — without the reset, the gap
             between server-side creation and the client's next heartbeat poll
             would be deducted from the granted time.
          2. The coin slot is still open (`receiving_coins == True`).  The lock
             screen is in front showing the Receiving Coins card while the user
             keeps inserting coins; the user hasn't "started using" the PC yet,
             so the session clock must not tick during this window.  Without
             this, every second spent feeding coins (often 30 s+) is silently
             deducted before the timer even appears.

        The pending flag is only cleared once the slot is closed AND the client
        has seen the heartbeat — so the timer starts ticking the moment the
        user presses "Done inserting Coins" and the lock screen dismisses.
        """
        if pc_number not in _pending_start or not session:
            return
        session.started_at = datetime.utcnow()
        self._db.commit()
        # Hold the pending flag until the slot actually closes.  As long as
        # the user is still inserting coins, every heartbeat refreshes
        # started_at so the clock effectively pauses.
        if not command_store.is_receiving_coins(pc_number):
            _pending_start.discard(pc_number)

    def pop_pending_notification(self, pc_number: int) -> int:
        """Returns seconds added since the last heartbeat and clears the counter."""
        return _pending_notify.pop(pc_number, 0)

    def end_session(self, pc_number: int) -> bool:
        session = self.get_active_session(pc_number)
        pc = self.get_pc(pc_number)
        if not pc:
            return False

        # ── Member-aware termination ─────────────────────────────────────
        # If a member is currently logged in to this PC AND the session is
        # being ended with time still remaining (admin Lock / forced end —
        # NOT the natural-expiry path where remaining is already 0), preserve
        # their unused time by saving it back to user.balance_seconds and
        # cleanly logging them out.  Without this, an admin lock silently
        # discards paid-for time, leaving the member stuck bound to a locked
        # PC with no session until the zero-time sweep eventually unbinds them.
        #
        # No `logout_deduction_minutes` penalty is applied — that penalty was
        # designed to discourage voluntary mid-session logout, but an admin
        # lock isn't the customer's fault.
        if session:
            remaining_sec = self.remaining_seconds(session)
            member_user_id = command_store.get_member_for_pc(pc_number)
            if remaining_sec > 0 and member_user_id is not None:
                member_user = (
                    self._db.query(User).filter(User.id == member_user_id).first()
                )
                if member_user is not None:
                    member_user.balance_seconds = remaining_sec
                    member_user.logged_in_pc_id = None
                    command_store.unbind_member(pc_number)
                    command_store.clear_zero_time_since(pc_number)
                    command_store.clear_idle_since(pc_number)
                    self._log(
                        "INFO", "session",
                        f"Admin-end on PC {pc_number:02d} preserved {remaining_sec}s "
                        f"to member {member_user.username}'s balance",
                    )

            elapsed_sec = int(
                (datetime.utcnow() - session.started_at).total_seconds()
            )
            session.used_seconds = min(elapsed_sec, session.granted_seconds)
            session.is_active = False
            session.ended_at = datetime.utcnow()
        pc.is_locked = True
        self._log("INFO", "session", f"Session ended for PC {pc_number:02d}")
        self._db.commit()
        return True

    def get_today_earnings(self, pc_number: int | None = None) -> dict:
        """
        Returns today's session earnings (since midnight UTC) for a specific PC
        or for all PCs if pc_number is None.

        The earnings are derived from CoinTransaction records — amount_php is the
        peso amount collected, seconds_added / 60 gives approximate minutes.

        Returns {"total_pesos": int, "total_sessions": int, "total_minutes": int}.
        """
        today_midnight = datetime.utcnow().replace(
            hour=0, minute=0, second=0, microsecond=0
        )

        query = self._db.query(CoinTransaction).filter(
            CoinTransaction.created_at >= today_midnight
        )

        pc_obj = None
        if pc_number is not None:
            pc_obj = self.get_pc(pc_number)
            if not pc_obj:
                return {"total_pesos": 0, "total_sessions": 0, "total_minutes": 0}
            query = query.filter(CoinTransaction.pc_id == pc_obj.id)

        rows = query.all()
        total_pesos = sum(r.amount_php for r in rows)
        total_minutes = sum(r.seconds_added for r in rows) // 60

        # Count distinct sessions started today for this PC (or all PCs)
        session_query = self._db.query(func.count(Session.id)).filter(
            Session.started_at >= today_midnight
        )
        if pc_number is not None:
            if pc_obj:
                session_query = session_query.filter(Session.pc_id == pc_obj.id)
            else:
                session_query = session_query.filter(False)

        total_sessions = session_query.scalar() or 0

        return {
            "total_pesos": int(total_pesos),
            "total_sessions": int(total_sessions),
            "total_minutes": int(total_minutes),
        }

    def get_all_pcs_today_earnings(self) -> list[dict]:
        """
        Returns yesterday's (UTC) earnings for every PC — used by the nightly
        archive task to sync to pisonex.com.

        Returns a list of dicts:
          {"pc_number": int, "total_pesos": int, "total_sessions": int, "total_minutes": int}
        """
        from datetime import timedelta
        today_midnight = datetime.utcnow().replace(
            hour=0, minute=0, second=0, microsecond=0
        )
        yesterday_start = today_midnight - timedelta(days=1)
        yesterday_end = today_midnight

        pcs = self.get_all_pcs()
        result = []
        for pc in pcs:
            tx_rows = self._db.query(CoinTransaction).filter(
                CoinTransaction.pc_id == pc.id,
                CoinTransaction.created_at >= yesterday_start,
                CoinTransaction.created_at < yesterday_end,
            ).all()

            total_pesos = sum(r.amount_php for r in tx_rows)
            total_minutes = sum(r.seconds_added for r in tx_rows) // 60

            total_sessions = self._db.query(func.count(Session.id)).filter(
                Session.pc_id == pc.id,
                Session.started_at >= yesterday_start,
                Session.started_at < yesterday_end,
            ).scalar() or 0

            result.append({
                "pc_number": pc.pc_number,
                "total_pesos": int(total_pesos),
                "total_sessions": int(total_sessions),
                "total_minutes": int(total_minutes),
            })
        return result

    def expire_sessions(self):
        """Called periodically to expire sessions whose time has run out."""
        sessions = (
            self._db.query(Session)
            .filter(Session.is_active == True)
            .all()
        )
        for s in sessions:
            if self.remaining_seconds(s) == 0:
                s.is_active = False
                s.ended_at = datetime.utcnow()
                pc = s.pc
                if pc:
                    pc.is_locked = True
                    self._log(
                        "INFO", "session",
                        f"Session expired for PC {pc.pc_number:02d}"
                    )
        self._db.commit()

    # ── Logging ───────────────────────────────────────────────────

    def _log(self, level: str, source: str, message: str):
        entry = SystemLog(level=level, source=source, message=message)
        self._db.add(entry)
        logger.log(
            logging.getLevelName(level),
            "[%s] %s", source, message
        )
