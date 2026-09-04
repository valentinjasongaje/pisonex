from datetime import datetime
from sqlalchemy import (
    Column, Integer, String, Boolean, DateTime, ForeignKey, Text, func
)
from sqlalchemy.orm import relationship
from database import Base


class RateProfile(Base):
    """A named set of coin rates.  All CoinRate rows belong to exactly one profile.
    The profile with is_default=True is used as a fallback when a PC has no
    profile assigned, or when the assigned profile has no active rates.
    """
    __tablename__ = "rate_profiles"

    id         = Column(Integer, primary_key=True, index=True)
    name       = Column(String(50), nullable=False, unique=True)
    color      = Column(String(20), default="#4f8ef7")   # hex color for badge in dashboard
    is_default = Column(Boolean, default=False)
    created_at = Column(DateTime, default=datetime.utcnow)

    rates = relationship("CoinRate", back_populates="profile")
    pcs   = relationship("PC",       back_populates="rate_profile")


class User(Base):
    __tablename__ = "users"

    id              = Column(Integer, primary_key=True, index=True)
    username        = Column(String(50), unique=True, nullable=False, index=True)
    password_hash   = Column(String(255), nullable=False)  # bcrypt hashed (any characters)
    balance_seconds = Column(Integer, default=0, nullable=False)
    created_at      = Column(DateTime, default=datetime.utcnow)
    is_active       = Column(Boolean, default=True)

    # Membership tracking
    logged_in_pc_id  = Column(Integer, ForeignKey("pcs.id"), nullable=True)
    last_login_at    = Column(DateTime, nullable=True)
    last_activity_at = Column(DateTime, nullable=True)

    # True for admin-issued accounts until the member sets their own password.
    # Set on creation by the dashboard "Create Member" flow; cleared by
    # POST /api/member/change-password on first successful password change.
    must_change_password = Column(Boolean, default=False, nullable=False)

    # Loyalty points — earned by paying for time while logged in and by
    # logging in on consecutive days, redeemed for bonus time or catalog
    # items. See services/membership_service.py.
    loyalty_points    = Column(Integer, default=0, nullable=False)
    login_streak_days = Column(Integer, default=0, nullable=False)
    last_login_date   = Column(String(10), nullable=True)  # "YYYY-MM-DD", café-local date

    sessions     = relationship("Session", back_populates="user")
    transactions = relationship("CoinTransaction", back_populates="user")
    logged_in_pc = relationship("PC", foreign_keys=[logged_in_pc_id])


class PC(Base):
    __tablename__ = "pcs"

    id              = Column(Integer, primary_key=True, index=True)
    pc_number       = Column(Integer, unique=True, nullable=False, index=True)
    name            = Column(String(50))
    mac_address     = Column(String(50), unique=True)
    ip_address      = Column(String(50))
    is_online       = Column(Boolean, default=False)
    is_locked       = Column(Boolean, default=True)
    last_seen       = Column(DateTime, nullable=True)
    registered_at   = Column(DateTime, default=datetime.utcnow)
    # NULL means "use the Default rate profile" — see RateProfile above.
    rate_profile_id = Column(Integer, ForeignKey("rate_profiles.id"), nullable=True)

    sessions     = relationship("Session", back_populates="pc")
    transactions = relationship("CoinTransaction", back_populates="pc")
    rate_profile = relationship("RateProfile", back_populates="pcs")


class Session(Base):
    __tablename__ = "sessions"

    id              = Column(Integer, primary_key=True, index=True)
    user_id         = Column(Integer, ForeignKey("users.id"), nullable=True)
    pc_id           = Column(Integer, ForeignKey("pcs.id"), nullable=False)
    started_at      = Column(DateTime, default=datetime.utcnow)
    ended_at        = Column(DateTime, nullable=True)
    granted_seconds = Column(Integer, default=0, nullable=False)
    used_seconds    = Column(Integer, default=0, nullable=False)
    is_active       = Column(Boolean, default=True, index=True)
    session_token   = Column(String(36), unique=True, nullable=False)

    user = relationship("User", back_populates="sessions")
    pc   = relationship("PC", back_populates="sessions")


class CoinTransaction(Base):
    __tablename__ = "coin_transactions"

    id            = Column(Integer, primary_key=True, index=True)
    pc_id         = Column(Integer, ForeignKey("pcs.id"), nullable=True)
    user_id       = Column(Integer, ForeignKey("users.id"), nullable=True)
    amount_php    = Column(Integer, nullable=False)
    seconds_added = Column(Integer, nullable=False)
    created_at    = Column(DateTime, default=datetime.utcnow, index=True)

    pc   = relationship("PC", back_populates="transactions")
    user = relationship("User", back_populates="transactions")


class PointsTransaction(Base):
    """Audit log of every loyalty-points earn/redeem/admin-adjust event."""
    __tablename__ = "points_transactions"

    id               = Column(Integer, primary_key=True, index=True)
    user_id          = Column(Integer, ForeignKey("users.id"), nullable=False)
    pc_id            = Column(Integer, ForeignKey("pcs.id"), nullable=True)
    kind             = Column(String(20), nullable=False)  # "earn_coin"|"earn_streak"|"redeem"|"admin_adjust"
    points_delta     = Column(Integer, nullable=False)     # +/-
    seconds_redeemed = Column(Integer, nullable=True)      # only set for "redeem" rows
    created_at       = Column(DateTime, default=datetime.utcnow, index=True)


class RewardItem(Base):
    """Admin-defined catalog entry a member can redeem loyalty points for."""
    __tablename__ = "reward_items"

    id          = Column(Integer, primary_key=True, index=True)
    name        = Column(String(120), nullable=False)     # "30 Minutes Bonus", "Chips"
    kind        = Column(String(10), nullable=False)      # "time" | "food"
    points_cost = Column(Integer, nullable=False)
    minutes     = Column(Integer, nullable=True)           # only meaningful for kind="time"
    is_active   = Column(Boolean, default=True, nullable=False)
    created_at  = Column(DateTime, default=datetime.utcnow)


class RewardRedemption(Base):
    """One member's claim against the reward catalog. Snapshots the item's
    name/kind/cost at redemption time so editing or deleting a catalog item
    later never corrupts history — this is a receipt, not a live join.
    """
    __tablename__ = "reward_redemptions"

    id              = Column(Integer, primary_key=True, index=True)
    user_id         = Column(Integer, ForeignKey("users.id"), nullable=False)
    pc_id           = Column(Integer, ForeignKey("pcs.id"), nullable=True)
    reward_item_id  = Column(Integer, ForeignKey("reward_items.id"), nullable=True)  # nullable: item may be deleted later
    item_name       = Column(String(120), nullable=False)
    kind            = Column(String(10), nullable=False)
    points_spent    = Column(Integer, nullable=False)
    minutes_granted = Column(Integer, nullable=True)       # set only for "time" kind
    status          = Column(String(10), nullable=False, default="pending")  # "pending" | "fulfilled"
    created_at      = Column(DateTime, default=datetime.utcnow, index=True)
    fulfilled_at    = Column(DateTime, nullable=True)


class CoinRate(Base):
    __tablename__ = "coin_rates"

    id         = Column(Integer, primary_key=True, index=True)
    pesos      = Column(Integer, nullable=False)
    seconds    = Column(Integer, nullable=False)
    label      = Column(String(100))            # e.g. "₱5 = 30 minutes"
    is_active  = Column(Boolean, default=True)
    created_at = Column(DateTime, default=datetime.utcnow)
    # NULL profile_id means "belongs to Default profile (id=1)"
    profile_id = Column(Integer, ForeignKey("rate_profiles.id"), nullable=True)

    profile = relationship("RateProfile", back_populates="rates")


class MembershipConfig(Base):
    __tablename__ = "membership_config"

    id                              = Column(Integer, primary_key=True, default=1)
    membership_enabled              = Column(Boolean, default=False, nullable=False)
    absorption_enabled              = Column(Boolean, default=False, nullable=False)
    logout_deduction_minutes        = Column(Integer, default=5, nullable=False)
    minimum_logout_minutes          = Column(Integer, default=10, nullable=False)
    zero_time_auto_logout_seconds   = Column(Integer, default=30, nullable=False)
    idle_auto_shutdown_minutes      = Column(Integer, default=5, nullable=False)
    member_heartbeat_timeout_minutes = Column(Integer, default=60, nullable=False)
    preset_amounts_enabled          = Column(Boolean, default=False, nullable=False)

    # ── Loyalty points ────────────────────────────────────────────────────────
    points_enabled          = Column(Boolean, default=False, nullable=False)
    points_per_10_pesos      = Column(Integer, default=1, nullable=False)
    points_streak_bonus      = Column(Integer, default=5, nullable=False)
    points_per_minute_redeem = Column(Integer, default=2, nullable=False)


class SystemLog(Base):
    __tablename__ = "system_logs"

    id         = Column(Integer, primary_key=True, index=True)
    level      = Column(String(10), nullable=False)   # INFO, WARNING, ERROR
    source     = Column(String(50), nullable=False)   # hardware, api, session
    message    = Column(Text, nullable=False)
    created_at = Column(DateTime, default=datetime.utcnow, index=True)


class AdminUser(Base):
    __tablename__ = "admin_users"

    id         = Column(Integer, primary_key=True, index=True)
    username   = Column(String(50), unique=True, nullable=False)
    password   = Column(String(255), nullable=False)  # bcrypt hashed
    role       = Column(String(20), nullable=False, default="admin")  # "admin" | "cashier"
    created_at = Column(DateTime, default=datetime.utcnow)


class ServerConfig(Base):
    """Singleton server configuration row (id always = 1).

    Stores settings that the admin can change via the dashboard at runtime,
    without requiring .env edits or a server restart.
    """
    __tablename__ = "server_config"

    id             = Column(Integer, primary_key=True, default=1)
    # Empty string = client auth disabled (default). Non-empty = all PC clients
    # must send this value in the X-API-Key header.
    client_api_key = Column(String(128), nullable=False, default="")
    # True = "Traditional Café Mode" — no coin acceptor hardware is used or
    # implied. Hides all coin-slot/Insert-Coin UI on the client and dashboard;
    # manual add-time/adjust-balance from the dashboard keeps working either way.
    # Default False so existing installs (normal piso-net mode, Insert Coin
    # visible) are unaffected until an admin explicitly opts in.
    traditional_mode_enabled = Column(Boolean, nullable=False, default=False)

    # What to do with pesos no combination of configured rates can consume —
    # e.g. a ₱1 coin when the cheapest configured rate is ₱5.
    #   "prorate" — credit them at the smallest denomination's per-peso value
    #               (the behaviour every install had before this was a setting)
    #   "discard" — credit nothing for them
    coin_leftover_mode = Column(String(10), nullable=False, default="prorate")

    # ── Monitoring ────────────────────────────────────────────────────────────
    # When True (default), the server requests FFmpeg live-streaming when admin
    # opens fullscreen. Set False to fall back to 1-second JPEG snapshots.
    ffmpeg_streaming_enabled = Column(Boolean, default=True, nullable=False)


class CoinSchedule(Base):
    """Time ranges when the coin slot is automatically blocked."""
    __tablename__ = "coin_schedules"

    id           = Column(Integer, primary_key=True)
    label        = Column(String(120), default="")        # human name e.g. "Night block"
    start_time   = Column(String(5),  nullable=False)     # "HH:MM" 24h
    end_time     = Column(String(5),  nullable=False)     # "HH:MM" 24h
    days_of_week = Column(String(7),  default="0123456")  # subset of "0123456" (Mon=0…Sun=6)
    is_active    = Column(Boolean, default=True)
    created_at   = Column(DateTime, default=datetime.utcnow)


class RateSchedule(Base):
    """Time-window override of which RateProfile is active — "Happy Hour".

    Only applies to PCs with rate_profile_id = NULL (the common case — no
    per-PC profile assignment). A PC explicitly pinned to a profile (e.g. a
    VIP-lounge machine priced on purpose) always keeps its own rates and is
    never swapped out by a schedule meant for the rest of the shop.

    When multiple active schedules overlap the same moment, the lowest id
    (oldest-created) wins — see _run_schedule_tick in main.py.
    """
    __tablename__ = "rate_schedules"

    id           = Column(Integer, primary_key=True)
    label        = Column(String(120), default="")        # human name e.g. "Weekday Happy Hour"
    profile_id   = Column(Integer, ForeignKey("rate_profiles.id"), nullable=False)
    start_time   = Column(String(5),  nullable=False)     # "HH:MM" 24h
    end_time     = Column(String(5),  nullable=False)     # "HH:MM" 24h
    days_of_week = Column(String(7),  default="0123456")  # subset of "0123456" (Mon=0…Sun=6)
    is_active    = Column(Boolean, default=True)
    created_at   = Column(DateTime, default=datetime.utcnow)

    profile = relationship("RateProfile")


class ScheduledAnnouncement(Base):
    """Announcements that fire automatically at a set time each day."""
    __tablename__ = "scheduled_announcements"

    id              = Column(Integer, primary_key=True)
    label           = Column(String(120), default="")
    fire_time       = Column(String(5),   nullable=False)  # "HH:MM" 24h
    message         = Column(String(500), nullable=False)
    days_of_week    = Column(String(7),   default="0123456")
    is_active       = Column(Boolean, default=True)
    last_fired_date = Column(String(10),  nullable=True)   # "YYYY-MM-DD", prevents double-fire
    created_at      = Column(DateTime, default=datetime.utcnow)
