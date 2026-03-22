"""
Membership business logic — registration, login, logout, absorption,
auto-expiry (heartbeat timeout, zero-time, idle shutdown).
"""

import re
import time
import uuid
import logging
from datetime import datetime
from sqlalchemy.orm import Session as DBSession

from models import User, Session, PC, MembershipConfig, SystemLog
from services.session_service import SessionService
from api.auth import hash_password, verify_password
import command_store

logger = logging.getLogger(__name__)

# Password: 4-128 chars (any characters). bcrypt truncates at 72 bytes but
# we allow longer input (the first 72 bytes are still checked).
_USERNAME_RE = re.compile(r"^[a-zA-Z0-9]{3,20}$")
_PASSWORD_MIN = 4
_PASSWORD_MAX = 128


class MembershipService:
    def __init__(self, db: DBSession):
        self._db = db

    # ── Config ────────────────────────────────────────────────────

    def get_config(self) -> MembershipConfig:
        cfg = self._db.query(MembershipConfig).first()
        if not cfg:
            cfg = MembershipConfig(id=1)
            self._db.add(cfg)
            self._db.commit()
            self._db.refresh(cfg)
        return cfg

    def update_config(self, **kwargs) -> MembershipConfig:
        cfg = self.get_config()
        for k, v in kwargs.items():
            if v is not None and hasattr(cfg, k):
                setattr(cfg, k, v)
        self._db.commit()
        self._db.refresh(cfg)

        # If membership was just disabled, auto-logout all members
        if kwargs.get("membership_enabled") is False:
            self._auto_logout_all_members(deduction=False)

        return cfg

    # ── Registration ──────────────────────────────────────────────

    def register_member(self, username: str, password: str, pc_number: int) -> dict:
        cfg = self.get_config()
        if not cfg.membership_enabled:
            return {"success": False, "error": "Membership is not enabled"}

        if not _USERNAME_RE.match(username):
            return {"success": False, "error": "Username must be 3-20 alphanumeric characters"}

        if len(password) < _PASSWORD_MIN or len(password) > _PASSWORD_MAX:
            return {"success": False, "error": f"Password must be {_PASSWORD_MIN}-{_PASSWORD_MAX} characters"}

        existing = self._db.query(User).filter(User.username == username).first()
        if existing:
            return {"success": False, "error": "Username already taken"}

        svc = SessionService(self._db)
        pc = svc.get_pc(pc_number)
        if not pc:
            return {"success": False, "error": f"PC {pc_number} not found"}

        # Check anonymous session + absorption policy
        session = svc.get_active_session(pc_number)
        absorbed_seconds = 0

        if session and session.user_id is not None:
            return {"success": False, "error": "Session already owned by a member"}

        if session and not cfg.absorption_enabled:
            return {"success": False, "error": "Cannot register during active anonymous session"}

        # Create user
        user = User(
            username=username,
            password_hash=hash_password(password),
            balance_seconds=0,
            is_active=True,
        )
        self._db.add(user)
        self._db.flush()  # get user.id

        # Absorb anonymous session if applicable
        if session and cfg.absorption_enabled:
            absorbed_seconds = svc.remaining_seconds(session)
            session.is_active = False
            session.ended_at = datetime.utcnow()

            new_session = Session(
                pc_id=pc.id,
                user_id=user.id,
                granted_seconds=absorbed_seconds,
                session_token=str(uuid.uuid4()),
                started_at=datetime.utcnow(),
            )
            self._db.add(new_session)
            pc.is_locked = False
            self._log("INFO", "membership",
                       f"Absorbed {absorbed_seconds}s from anonymous session on PC {pc_number:02d} for new member {username}")
        else:
            # Zero-time login
            command_store.set_zero_time_since(pc_number, time.time())

        # Bind member to PC
        user.logged_in_pc_id = pc.id
        user.last_login_at = datetime.utcnow()
        user.last_activity_at = datetime.utcnow()
        command_store.bind_member(pc_number, user.id)
        command_store.clear_idle_since(pc_number)

        self._db.commit()
        self._db.refresh(user)

        return {
            "success": True,
            "user_id": user.id,
            "username": user.username,
            "absorbed_seconds": absorbed_seconds,
        }

    # ── Login ─────────────────────────────────────────────────────

    def login_member(self, pc_number: int, username: str, password: str) -> dict:
        cfg = self.get_config()
        if not cfg.membership_enabled:
            return {"success": False, "error": "Membership is not enabled"}

        # Rate limiting
        if not command_store.check_login_rate(username):
            return {"success": False, "error": "Too many login attempts. Please wait a moment."}

        user = self._db.query(User).filter(User.username == username).first()
        if not user or not verify_password(password, user.password_hash):
            return {"success": False, "error": "Invalid username or password"}

        if not user.is_active:
            return {"success": False, "error": "Account is deactivated"}

        # Check if already logged in elsewhere
        if user.logged_in_pc_id is not None:
            other_pc = self._db.query(PC).filter(PC.id == user.logged_in_pc_id).first()
            other_num = other_pc.pc_number if other_pc else "unknown"
            return {"success": False, "error": f"Account already logged in on PC {other_num}"}

        svc = SessionService(self._db)
        pc = svc.get_pc(pc_number)
        if not pc:
            return {"success": False, "error": f"PC {pc_number} not found"}

        # Check anonymous session + absorption policy
        session = svc.get_active_session(pc_number)
        absorbed_seconds = 0

        if session and session.user_id is not None:
            return {"success": False, "error": "Session already owned by a member"}

        if session and not cfg.absorption_enabled:
            return {"success": False, "error": "Cannot login during active anonymous session"}

        # Absorb anonymous session if applicable
        if session and cfg.absorption_enabled:
            absorbed_seconds = svc.remaining_seconds(session)
            session.is_active = False
            session.ended_at = datetime.utcnow()

            total_seconds = absorbed_seconds + user.balance_seconds
            new_session = Session(
                pc_id=pc.id,
                user_id=user.id,
                granted_seconds=total_seconds,
                session_token=str(uuid.uuid4()),
                started_at=datetime.utcnow(),
            )
            self._db.add(new_session)
            user.balance_seconds = 0
            pc.is_locked = False
            self._log("INFO", "membership",
                       f"Absorbed {absorbed_seconds}s from anonymous session on PC {pc_number:02d} for member {username}")
        elif user.balance_seconds > 0:
            # Create member session with stored balance
            new_session = Session(
                pc_id=pc.id,
                user_id=user.id,
                granted_seconds=user.balance_seconds,
                session_token=str(uuid.uuid4()),
                started_at=datetime.utcnow(),
            )
            self._db.add(new_session)
            user.balance_seconds = 0
            pc.is_locked = False
        else:
            # Zero-time login — PC stays locked, start auto-logout timer
            command_store.set_zero_time_since(pc_number, time.time())

        # Bind member to PC
        user.logged_in_pc_id = pc.id
        user.last_login_at = datetime.utcnow()
        user.last_activity_at = datetime.utcnow()
        command_store.bind_member(pc_number, user.id)
        command_store.clear_idle_since(pc_number)

        self._db.commit()

        return {
            "success": True,
            "balance_seconds": user.balance_seconds,
            "absorbed_seconds": absorbed_seconds,
        }

    # ── Logout ────────────────────────────────────────────────────

    def logout_member(self, pc_number: int) -> dict:
        cfg = self.get_config()
        user_id = command_store.get_member_for_pc(pc_number)
        if user_id is None:
            return {"success": False, "error": "No member logged in on this PC"}

        user = self._db.query(User).filter(User.id == user_id).first()
        if not user:
            command_store.unbind_member(pc_number)
            return {"success": False, "error": "Member not found"}

        svc = SessionService(self._db)
        session = svc.get_active_session(pc_number)
        remaining = svc.remaining_seconds(session) if session else 0

        # Zero-time members can always logout (Case 26)
        if session and remaining > 0:
            min_seconds = cfg.minimum_logout_minutes * 60
            if remaining < min_seconds:
                return {
                    "success": False,
                    "error": f"Cannot logout. Minimum {cfg.minimum_logout_minutes} minutes remaining required.",
                    "remaining_seconds": remaining,
                    "deducted_seconds": 0,
                }

        # Apply deduction
        deducted = 0
        if session and remaining > 0:
            deduction = cfg.logout_deduction_minutes * 60
            deducted = min(deduction, remaining)
            final_balance = max(0, remaining - deduction)
            user.balance_seconds = final_balance

            session.used_seconds = int(
                (datetime.utcnow() - session.started_at).total_seconds()
            )
            session.is_active = False
            session.ended_at = datetime.utcnow()
        # else: zero-time, no session to end, balance stays 0

        # Unbind member
        pc = svc.get_pc(pc_number)
        if pc:
            pc.is_locked = True
        user.logged_in_pc_id = None
        command_store.unbind_member(pc_number)
        command_store.clear_zero_time_since(pc_number)

        self._log("INFO", "membership",
                   f"Member {user.username} logged out from PC {pc_number:02d} "
                   f"(remaining={remaining}s, deducted={deducted}s, saved={user.balance_seconds}s)")
        self._db.commit()

        return {
            "success": True,
            "remaining_seconds": remaining,
            "deducted_seconds": deducted,
        }

    # ── Status ────────────────────────────────────────────────────

    def get_member_status(self, pc_number: int) -> dict:
        cfg = self.get_config()
        user_id = command_store.get_member_for_pc(pc_number)

        result = {
            "membership_enabled": cfg.membership_enabled,
            "absorption_enabled": cfg.absorption_enabled,
            "logged_in_user": None,
            "balance_seconds": 0,
            "can_logout": False,
            "logout_denied_reason": None,
        }

        if user_id is None:
            return result

        user = self._db.query(User).filter(User.id == user_id).first()
        if not user:
            return result

        svc = SessionService(self._db)
        session = svc.get_active_session(pc_number)
        remaining = svc.remaining_seconds(session) if session else 0

        result["logged_in_user"] = user.username
        result["balance_seconds"] = user.balance_seconds

        # Can logout? Zero-time always allowed (Case 26)
        if not session or remaining == 0:
            result["can_logout"] = True
        elif remaining >= cfg.minimum_logout_minutes * 60:
            result["can_logout"] = True
        else:
            result["can_logout"] = False
            result["logout_denied_reason"] = (
                f"Cannot logout. Minimum {cfg.minimum_logout_minutes} minutes remaining required."
            )

        return result

    # ── Auto-expiry: heartbeat timeout ────────────────────────────

    def auto_expire_members(self) -> None:
        """
        Expire members who haven't sent a heartbeat within the timeout.
        Called by the background task every 30 seconds.
        """
        cfg = self.get_config()
        if not cfg.membership_enabled:
            return

        timeout_seconds = cfg.member_heartbeat_timeout_minutes * 60
        now = datetime.utcnow()

        users = (
            self._db.query(User)
            .filter(User.logged_in_pc_id.isnot(None))
            .all()
        )

        svc = SessionService(self._db)

        for user in users:
            if not user.last_activity_at:
                continue
            elapsed = (now - user.last_activity_at).total_seconds()
            if elapsed <= timeout_seconds:
                continue

            # Find PC number for this user
            pc = self._db.query(PC).filter(PC.id == user.logged_in_pc_id).first()
            if not pc:
                continue

            # Save remaining time (no deduction)
            session = svc.get_active_session(pc.pc_number)
            if session:
                remaining = svc.remaining_seconds(session)
                user.balance_seconds = max(0, remaining)
                session.used_seconds = int(
                    (now - session.started_at).total_seconds()
                )
                session.is_active = False
                session.ended_at = now

            pc.is_locked = True
            user.logged_in_pc_id = None
            command_store.unbind_member(pc.pc_number)
            command_store.clear_zero_time_since(pc.pc_number)

            self._log("INFO", "membership",
                       f"Member {user.username} heartbeat-timeout expired on PC {pc.pc_number:02d} "
                       f"(saved={user.balance_seconds}s)")

        self._db.commit()

    # ── Auto-expiry: zero-time auto-logout ─────────────────────────

    def check_zero_time_timeouts(self) -> None:
        """
        Auto-logout zero-time members after the configured timeout.
        Called by the background task every 30 seconds.
        """
        cfg = self.get_config()
        if not cfg.membership_enabled:
            return

        timeout = cfg.zero_time_auto_logout_seconds
        now = time.time()
        bindings = command_store.get_all_member_bindings()

        for pc_number, user_id in bindings.items():
            since = command_store.get_zero_time_since(pc_number)
            if since is None:
                continue
            if now - since < timeout:
                continue

            # Guard: if the member now has an active session (coins inserted after zero-time
            # started), they are no longer in zero-time state — cancel the auto-logout.
            svc_check = SessionService(self._db)
            active = svc_check.get_active_session(pc_number)
            if active and svc_check.remaining_seconds(active) > 0:
                command_store.clear_zero_time_since(pc_number)
                continue

            # Zero-time expired — logout member
            user = self._db.query(User).filter(User.id == user_id).first()
            if not user:
                command_store.unbind_member(pc_number)
                command_store.clear_zero_time_since(pc_number)
                continue

            svc = SessionService(self._db)
            pc = svc.get_pc(pc_number)
            if pc:
                pc.is_locked = True

            user.logged_in_pc_id = None
            command_store.unbind_member(pc_number)
            command_store.clear_zero_time_since(pc_number)

            self._log("INFO", "membership",
                       f"Zero-time auto-logout: member {user.username} on PC {pc_number:02d}")

        self._db.commit()

    # ── Idle auto-shutdown ────────────────────────────────────────

    def check_idle_shutdown(self) -> None:
        """
        Send shutdown command to PCs that have been locked-idle beyond the timeout.
        Conditions: PC locked, no active session, no member logged in.
        Called by the background task every 30 seconds.
        """
        cfg = self.get_config()
        timeout = cfg.idle_auto_shutdown_minutes * 60
        if timeout <= 0:
            return

        now = time.time()
        pcs = self._db.query(PC).filter(PC.is_online == True).all()
        svc = SessionService(self._db)

        for pc in pcs:
            if not pc.is_locked:
                command_store.clear_idle_since(pc.pc_number)
                continue

            session = svc.get_active_session(pc.pc_number)
            member = command_store.get_member_for_pc(pc.pc_number)

            if session or member:
                command_store.clear_idle_since(pc.pc_number)
                continue

            since = command_store.get_idle_since(pc.pc_number)
            if since is None:
                command_store.set_idle_since(pc.pc_number, now)
                continue

            if now - since >= timeout:
                command_store.push_command(pc.pc_number, "shutdown")
                command_store.clear_idle_since(pc.pc_number)
                self._log("INFO", "membership",
                           f"Idle auto-shutdown sent to PC {pc.pc_number:02d}")

        self._db.commit()

    # ── Rebuild bindings from DB (startup) ────────────────────────

    def rebuild_bindings(self) -> None:
        """Rebuild in-memory member-PC bindings from the database. Called on startup."""
        users = (
            self._db.query(User)
            .filter(User.logged_in_pc_id.isnot(None))
            .all()
        )
        bindings = {}
        for user in users:
            pc = self._db.query(PC).filter(PC.id == user.logged_in_pc_id).first()
            if pc:
                bindings[pc.pc_number] = user.id
        command_store.rebuild_member_bindings(bindings)
        logger.info("Rebuilt %d member-PC bindings from DB", len(bindings))

    # ── Force-logout (admin, no deduction) ────────────────────────

    def force_logout_member(self, user_id: int) -> bool:
        """Admin force-logout: saves remaining time, no deduction."""
        user = self._db.query(User).filter(User.id == user_id).first()
        if not user or user.logged_in_pc_id is None:
            return False

        pc = self._db.query(PC).filter(PC.id == user.logged_in_pc_id).first()
        if not pc:
            user.logged_in_pc_id = None
            self._db.commit()
            return False

        svc = SessionService(self._db)
        session = svc.get_active_session(pc.pc_number)
        if session:
            remaining = svc.remaining_seconds(session)
            user.balance_seconds = max(0, remaining)
            session.used_seconds = int(
                (datetime.utcnow() - session.started_at).total_seconds()
            )
            session.is_active = False
            session.ended_at = datetime.utcnow()

        pc.is_locked = True
        user.logged_in_pc_id = None
        command_store.unbind_member(pc.pc_number)
        command_store.clear_zero_time_since(pc.pc_number)

        self._log("INFO", "membership",
                   f"Admin force-logout: member {user.username} from PC {pc.pc_number:02d}")
        self._db.commit()
        return True

    # ── Internal: auto-logout all members (e.g., membership disabled) ─────

    def _auto_logout_all_members(self, deduction: bool = False) -> None:
        """Logout all logged-in members. Used when membership is disabled."""
        users = (
            self._db.query(User)
            .filter(User.logged_in_pc_id.isnot(None))
            .all()
        )
        svc = SessionService(self._db)
        for user in users:
            pc = self._db.query(PC).filter(PC.id == user.logged_in_pc_id).first()
            if not pc:
                user.logged_in_pc_id = None
                continue

            session = svc.get_active_session(pc.pc_number)
            if session:
                remaining = svc.remaining_seconds(session)
                user.balance_seconds = max(0, remaining)
                session.used_seconds = int(
                    (datetime.utcnow() - session.started_at).total_seconds()
                )
                session.is_active = False
                session.ended_at = datetime.utcnow()

            pc.is_locked = True
            user.logged_in_pc_id = None
            command_store.unbind_member(pc.pc_number)
            command_store.clear_zero_time_since(pc.pc_number)

            self._log("INFO", "membership",
                       f"Auto-logout (membership disabled): member {user.username} "
                       f"from PC {pc.pc_number:02d} (saved={user.balance_seconds}s)")
        self._db.commit()

    # ── Logging ────────────────────────────────────────────────────

    def _log(self, level: str, source: str, message: str):
        entry = SystemLog(level=level, source=source, message=message)
        self._db.add(entry)
        logger.log(logging.getLevelName(level), "[%s] %s", source, message)
