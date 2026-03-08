import uuid
import logging
from datetime import datetime
from sqlalchemy.orm import Session as DBSession
from models import PC, Session, CoinTransaction, SystemLog
from services.rate_service import pesos_to_minutes

logger = logging.getLogger(__name__)


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
        granted = session.minutes_granted * 60
        return max(0, int(granted - elapsed))

    def add_time_by_pesos(self, pc_number: int, pesos: int) -> tuple[int, Session]:
        """
        Converts pesos to minutes, creates or extends an active session.
        Returns (minutes_added, session).
        """
        pc = self.get_pc(pc_number)
        if not pc:
            raise ValueError(f"PC {pc_number} not found")

        minutes = pesos_to_minutes(pesos, self._db)
        if minutes == 0:
            raise ValueError(f"₱{pesos} does not convert to any minutes")

        session = self.get_active_session(pc_number)

        if session:
            session.minutes_granted += minutes
        else:
            session = Session(
                pc_id=pc.id,
                minutes_granted=minutes,
                session_token=str(uuid.uuid4()),
                started_at=datetime.utcnow(),
            )
            self._db.add(session)
            pc.is_locked = False

        tx = CoinTransaction(
            pc_id=pc.id,
            amount_pesos=pesos,
            minutes_added=minutes,
        )
        self._db.add(tx)
        self._log(
            "INFO", "session",
            f"₱{pesos} → {minutes}min added to PC {pc_number:02d}"
        )
        self._db.commit()
        self._db.refresh(session)
        return minutes, session

    def add_time_minutes(self, pc_number: int, minutes: int) -> Session:
        """Admin: directly add minutes without coin conversion."""
        pc = self.get_pc(pc_number)
        if not pc:
            raise ValueError(f"PC {pc_number} not found")

        session = self.get_active_session(pc_number)
        if session:
            session.minutes_granted += minutes
        else:
            session = Session(
                pc_id=pc.id,
                minutes_granted=minutes,
                session_token=str(uuid.uuid4()),
                started_at=datetime.utcnow(),
            )
            self._db.add(session)
            pc.is_locked = False

        self._log("INFO", "admin", f"Admin added {minutes}min to PC {pc_number:02d}")
        self._db.commit()
        self._db.refresh(session)
        return session

    def end_session(self, pc_number: int) -> bool:
        session = self.get_active_session(pc_number)
        pc = self.get_pc(pc_number)
        if not pc:
            return False
        if session:
            elapsed_min = int(
                (datetime.utcnow() - session.started_at).total_seconds() / 60
            )
            session.minutes_used = min(elapsed_min, session.minutes_granted)
            session.is_active = False
            session.ended_at = datetime.utcnow()
        pc.is_locked = True
        self._log("INFO", "session", f"Session ended for PC {pc_number:02d}")
        self._db.commit()
        return True

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
