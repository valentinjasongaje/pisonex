"""
Membership business logic — admin-issued account creation, login, logout,
change-password, absorption, auto-expiry (heartbeat timeout, zero-time,
idle shutdown).
"""

import re
import time
import uuid
import string
import secrets
import logging
from datetime import datetime
from sqlalchemy.orm import Session as DBSession
from typing import Optional

from models import (
    User, Session, PC, MembershipConfig, SystemLog, PointsTransaction,
    RewardItem, RewardRedemption,
)
from services.session_service import SessionService
from api.auth import hash_password, verify_password
import command_store
import timeutil

logger = logging.getLogger(__name__)

# Password: 4-128 chars (any characters). bcrypt truncates at 72 bytes but
# we allow longer input (the first 72 bytes are still checked).
_USERNAME_RE = re.compile(r"^[a-zA-Z0-9]{3,20}$")
_PASSWORD_MIN = 4
_PASSWORD_MAX = 128

# Admin-issued temp passwords: kept easy to read/type since the admin hands
# this to the member verbally or on paper — lowercase letters + digits only
# (no symbols, no ambiguous punctuation), with 1-2 letters capitalized.
_TEMP_PASSWORD_LENGTH = 8
_rng = secrets.SystemRandom()


def _generate_temp_password(length: int = _TEMP_PASSWORD_LENGTH) -> str:
    pool = string.ascii_lowercase + string.digits
    chars = [_rng.choice(pool) for _ in range(length)]
    letter_positions = [i for i, c in enumerate(chars) if c.isalpha()]
    num_caps = min(_rng.choice((1, 2)), len(letter_positions))
    for i in _rng.sample(letter_positions, num_caps):
        chars[i] = chars[i].upper()
    return "".join(chars)


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

    # ── Admin-issued account creation ───────────────────────────────
    # Self-service registration from the client lock screen was removed
    # (customers were creating multiple accounts to abuse absorption /
    # zero-time login). Accounts are now created only from the dashboard
    # by an admin, who receives a one-time temp password to hand to the
    # member. See docs/admin-only-membership-migration.md.

    def admin_create_member(self, username: str, initial_minutes: int = 0) -> dict:
        """Admin-only: create a member account with an auto-generated temp
        password. Returns the plaintext temp password ONCE — it is never
        stored, only its bcrypt hash. The member must change it via
        POST /api/member/change-password before the account is otherwise
        usable in the normal sense (login still works with the temp
        password, but must_change_password gates the client-side flow).

        initial_minutes: optional time to seed on the account (e.g. a
        membership sold with time already included). Converted to seconds —
        same unit as User.balance_seconds / AdjustBalanceBody.seconds.
        """
        username = username.strip().lower()
        if not _USERNAME_RE.match(username):
            return {"success": False, "error": "Username must be 3-20 alphanumeric characters"}

        existing = self._db.query(User).filter(User.username == username).first()
        if existing:
            return {"success": False, "error": "Username already taken"}

        initial_seconds = max(0, initial_minutes) * 60
        temp_password = _generate_temp_password()

        user = User(
            username=username,
            password_hash=hash_password(temp_password),
            balance_seconds=initial_seconds,
            is_active=True,
            must_change_password=True,
        )
        self._db.add(user)
        self._db.commit()
        self._db.refresh(user)

        self._log("INFO", "membership",
                   f"Admin created member account '{username}' (id={user.id}, "
                   f"initial_balance={initial_seconds}s)")

        return {
            "success": True,
            "user_id": user.id,
            "username": user.username,
            "temp_password": temp_password,
            "balance_seconds": user.balance_seconds,
        }

    def admin_reset_password(self, user_id: int) -> dict:
        """Admin-only: generate a new temp password for an existing member who
        forgot theirs. Same one-time-reveal semantics as admin_create_member —
        the plaintext temp password is returned exactly once, never stored,
        only its bcrypt hash. Does not touch an already-active session or
        force a logout; this only affects their NEXT login attempt, since the
        old password stops verifying immediately but the PC binding is
        untouched.
        """
        user = self._db.query(User).filter(User.id == user_id).first()
        if not user:
            return {"success": False, "error": "Member not found"}

        temp_password = _generate_temp_password()
        user.password_hash = hash_password(temp_password)
        user.must_change_password = True
        self._db.commit()

        self._log("INFO", "membership",
                   f"Admin reset password for member '{user.username}' (id={user.id})")

        return {
            "success": True,
            "username": user.username,
            "temp_password": temp_password,
        }

    # ── Change password (forced on first login, or voluntary from the tray) ──

    def change_password(self, pc_number: int, new_password: str, old_password: str = None) -> dict:
        """Identifies the member via the PC binding set by login_member().

        old_password is None/empty for the forced first-login flow — that
        login already proved identity, so no re-check is needed. It's
        required and verified here for a voluntary change (tray icon
        "Change Password" while already logged in), since an already-open
        session sitting at the PC doesn't by itself prove the person at the
        keyboard right now is the account owner.
        """
        cfg = self.get_config()
        if not cfg.membership_enabled:
            return {"success": False, "error": "Membership is not enabled"}

        if len(new_password) < 6 or len(new_password) > _PASSWORD_MAX:
            return {"success": False, "error": f"Password must be 6-{_PASSWORD_MAX} characters"}

        user_id = command_store.get_member_for_pc(pc_number)
        if user_id is None:
            return {"success": False, "error": "No member logged in on this PC"}

        user = self._db.query(User).filter(User.id == user_id).first()
        if not user:
            return {"success": False, "error": "Member not found"}

        if old_password:
            if not verify_password(old_password, user.password_hash):
                return {"success": False, "error": "Current password is incorrect"}

        user.password_hash = hash_password(new_password)
        user.must_change_password = False
        self._db.commit()

        self._log("INFO", "membership", f"Member {user.username} changed their password")
        return {"success": True}

    # ── Login ─────────────────────────────────────────────────────

    def login_member(self, pc_number: int, username: str, password: str) -> dict:
        cfg = self.get_config()
        if not cfg.membership_enabled:
            return {"success": False, "error": "Membership is not enabled"}

        username = username.strip().lower()

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

        streak_bonus = self._award_login_streak(user, cfg)

        self._db.commit()

        return {
            "success": True,
            "balance_seconds": user.balance_seconds,
            "absorbed_seconds": absorbed_seconds,
            "must_change_password": user.must_change_password,
            "streak_bonus": streak_bonus,
            "login_streak_days": user.login_streak_days,
            "loyalty_points": user.loyalty_points,
        }

    # ── Loyalty points ────────────────────────────────────────────

    def _award_login_streak(self, user: User, cfg: MembershipConfig) -> int:
        """Called from inside login_member(), right before its commit — shares
        that transaction rather than starting its own. Awards points once per
        café-local calendar day, and only when the previous login was exactly
        the day before; any bigger gap resets the streak to 1.

        The day boundary is the café's TIMEZONE (see timeutil), not the server
        OS clock — on a box left on UTC those differ by 8 hours in the
        Philippines, which would roll the streak over mid-morning.
        """
        if not cfg.points_enabled:
            return 0

        today = timeutil.local_date_str()
        if user.last_login_date == today:
            return 0  # already logged in today — no double-dip

        if user.last_login_date:
            try:
                prev = datetime.strptime(user.last_login_date, "%Y-%m-%d").date()
                gap_days = (timeutil.local_now().date() - prev).days
            except ValueError:
                gap_days = None
            user.login_streak_days = user.login_streak_days + 1 if gap_days == 1 else 1
        else:
            user.login_streak_days = 1

        user.last_login_date = today

        bonus = cfg.points_streak_bonus
        if bonus > 0:
            user.loyalty_points += bonus
            self._db.add(PointsTransaction(
                user_id=user.id, pc_id=None,
                kind="earn_streak", points_delta=bonus,
            ))
        return bonus

    def award_coin_points(self, user_id: Optional[int], pc_id: Optional[int], pesos: int) -> int:
        """Called right after a paid time credit for a logged-in member.
        No-op if points are disabled or nobody is logged in. Returns the
        number of points awarded. Commits on its own — unlike the streak
        bonus, this isn't already inside another method's transaction.
        """
        if user_id is None or pesos <= 0:
            return 0
        cfg = self.get_config()
        if not cfg.points_enabled:
            return 0

        points = (pesos * cfg.points_per_10_pesos) // 10
        if points <= 0:
            return 0

        user = self._db.query(User).filter(User.id == user_id).first()
        if not user:
            return 0

        user.loyalty_points += points
        self._db.add(PointsTransaction(
            user_id=user.id, pc_id=pc_id,
            kind="earn_coin", points_delta=points,
        ))
        self._db.commit()
        return points

    # ── Reward catalog ────────────────────────────────────────────

    def list_active_rewards(self) -> list:
        """Client-facing catalog — only items an admin has left active."""
        return (
            self._db.query(RewardItem)
            .filter(RewardItem.is_active == True)
            .order_by(RewardItem.points_cost)
            .all()
        )

    def redeem_reward(self, pc_number: int, reward_item_id: int) -> dict:
        """Member-facing, self-service: identifies the member via the PC
        binding set by login_member() (no re-auth needed — same as
        change_password). A catalog item has one fixed points cost.

        "time" items fulfill themselves immediately, splitting between the
        live session and the stored balance. "food" items can't be
        auto-dispensed: points are deducted now (so they can't be spent
        twice), but the RewardRedemption row is created "pending" for staff
        to fulfill at the counter from the Rewards dashboard page.
        """
        cfg = self.get_config()
        if not cfg.points_enabled:
            return {"success": False, "error": "Loyalty points are not enabled"}

        item = self._db.query(RewardItem).filter(
            RewardItem.id == reward_item_id, RewardItem.is_active == True
        ).first()
        if not item:
            return {"success": False, "error": "Reward not found"}

        user_id = command_store.get_member_for_pc(pc_number)
        if user_id is None:
            return {"success": False, "error": "No member logged in on this PC"}

        user = self._db.query(User).filter(User.id == user_id).first()
        if not user:
            return {"success": False, "error": "Member not found"}

        if item.points_cost > user.loyalty_points:
            return {"success": False, "error": f"Not enough points (you have {user.loyalty_points})"}

        user.loyalty_points -= item.points_cost

        minutes_granted = None
        status = "pending"
        fulfilled_at = None

        if item.kind == "time":
            minutes_granted = item.minutes or 0
            seconds = minutes_granted * 60
            svc = SessionService(self._db)
            pc = svc.get_pc(pc_number)
            session = svc.get_active_session(pc_number) if pc else None
            if session and session.user_id == user.id:
                session.granted_seconds += seconds
            else:
                user.balance_seconds += seconds
            status = "fulfilled"
            fulfilled_at = datetime.utcnow()

        pc = self._db.query(PC).filter(PC.pc_number == pc_number).first()
        redemption = RewardRedemption(
            user_id=user.id,
            pc_id=pc.id if pc else None,
            reward_item_id=item.id,
            item_name=item.name,
            kind=item.kind,
            points_spent=item.points_cost,
            minutes_granted=minutes_granted,
            status=status,
            fulfilled_at=fulfilled_at,
        )
        self._db.add(redemption)
        self._log("INFO", "membership",
                   f"Member {user.username} redeemed '{item.name}' ({item.points_cost} pts) on PC {pc_number:02d}"
                   + (" — pending staff fulfillment" if status == "pending" else ""))
        self._db.commit()

        return {
            "success": True,
            "item_name": item.name,
            "kind": item.kind,
            "points_spent": item.points_cost,
            "minutes_granted": minutes_granted,
            "remaining_points": user.loyalty_points,
            "status": status,
        }

    def admin_create_reward(self, name: str, kind: str, points_cost: int, minutes: int = None) -> dict:
        name = name.strip()
        if not name:
            return {"success": False, "error": "Name cannot be empty"}
        if kind not in ("time", "food"):
            return {"success": False, "error": "kind must be 'time' or 'food'"}
        if points_cost <= 0:
            return {"success": False, "error": "Points cost must be greater than 0"}
        if kind == "time" and (not minutes or minutes <= 0):
            return {"success": False, "error": "Minutes must be greater than 0 for a time reward"}

        item = RewardItem(
            name=name, kind=kind, points_cost=points_cost,
            minutes=minutes if kind == "time" else None,
        )
        self._db.add(item)
        self._db.commit()
        self._db.refresh(item)
        return {"success": True, "item": item}

    def admin_toggle_reward(self, reward_item_id: int) -> dict:
        item = self._db.query(RewardItem).filter(RewardItem.id == reward_item_id).first()
        if not item:
            return {"success": False, "error": "Reward not found"}
        item.is_active = not item.is_active
        self._db.commit()
        return {"success": True, "is_active": item.is_active}

    def admin_delete_reward(self, reward_item_id: int) -> dict:
        item = self._db.query(RewardItem).filter(RewardItem.id == reward_item_id).first()
        if not item:
            return {"success": False, "error": "Reward not found"}
        self._db.delete(item)
        self._db.commit()
        return {"success": True}

    def list_pending_redemptions(self) -> list:
        return (
            self._db.query(RewardRedemption)
            .filter(RewardRedemption.status == "pending")
            .order_by(RewardRedemption.created_at)
            .all()
        )

    def fulfill_redemption(self, redemption_id: int) -> dict:
        """Admin/staff: mark a food redemption as handed over at the counter."""
        redemption = self._db.query(RewardRedemption).filter(
            RewardRedemption.id == redemption_id
        ).first()
        if not redemption:
            return {"success": False, "error": "Redemption not found"}
        if redemption.status == "fulfilled":
            return {"success": False, "error": "Already fulfilled"}

        redemption.status = "fulfilled"
        redemption.fulfilled_at = datetime.utcnow()
        self._db.commit()
        return {"success": True}

    def admin_adjust_points(self, user_id: int, points_delta: int) -> dict:
        """Admin dashboard: mirrors adjust_member_balance but for loyalty
        points. Allows negative adjustments; never lets the balance go
        below 0."""
        user = self._db.query(User).filter(User.id == user_id).first()
        if not user:
            return {"success": False, "error": "Member not found"}

        applied = max(-user.loyalty_points, points_delta)
        user.loyalty_points += applied
        if applied != 0:
            self._db.add(PointsTransaction(
                user_id=user.id, pc_id=None,
                kind="admin_adjust", points_delta=applied,
            ))
        self._db.commit()
        return {"success": True, "loyalty_points": user.loyalty_points}

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
            "points_enabled": cfg.points_enabled,
            "loyalty_points": 0,
            "points_per_minute_redeem": cfg.points_per_minute_redeem,
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
        result["loyalty_points"] = user.loyalty_points

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

            # Guard: if the coin slot is currently open for this PC, the member
            # is actively trying to insert coins — never log them out mid-flow.
            # The countdown will resume (or be cleared by a successful coin) once
            # the slot closes.
            if command_store.is_receiving_coins(pc_number):
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

            # Don't idle-shutdown a PC that has not yet had any session since
            # it last booted. This prevents an infinite reboot loop where the
            # PC shuts down, reboots still idle, and gets shut down again.
            if not command_store.pc_had_session(pc.pc_number):
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
