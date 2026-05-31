from datetime import datetime
from sqlalchemy import (
    Column, Integer, String, Boolean, DateTime, ForeignKey, Text, func
)
from sqlalchemy.orm import relationship
from database import Base


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

    sessions     = relationship("Session", back_populates="user")
    transactions = relationship("CoinTransaction", back_populates="user")
    logged_in_pc = relationship("PC", foreign_keys=[logged_in_pc_id])


class PC(Base):
    __tablename__ = "pcs"

    id            = Column(Integer, primary_key=True, index=True)
    pc_number     = Column(Integer, unique=True, nullable=False, index=True)
    name          = Column(String(50))
    mac_address   = Column(String(50), unique=True)
    ip_address    = Column(String(50))
    is_online     = Column(Boolean, default=False)
    is_locked     = Column(Boolean, default=True)
    last_seen     = Column(DateTime, nullable=True)
    registered_at = Column(DateTime, default=datetime.utcnow)

    sessions     = relationship("Session", back_populates="pc")
    transactions = relationship("CoinTransaction", back_populates="pc")


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


class CoinRate(Base):
    __tablename__ = "coin_rates"

    id         = Column(Integer, primary_key=True, index=True)
    pesos      = Column(Integer, nullable=False)
    seconds    = Column(Integer, nullable=False)
    label      = Column(String(100))            # e.g. "₱5 = 30 minutes"
    is_active  = Column(Boolean, default=True)
    created_at = Column(DateTime, default=datetime.utcnow)


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

    # ── Coin slot GPIO configuration (admin-editable, hot-reloaded) ──────────
    # These mirror the COIN_* values in config.py / .env so an admin can re-pin
    # the coin acceptor and relay from the dashboard without editing files or
    # SSHing into the Pi. On save the hardware controller is rebuilt so the new
    # pins take effect immediately. NULL columns fall back to the .env defaults.
    coin_pin           = Column(Integer, nullable=True)   # BCM pin reading coin pulses
    relay_pin          = Column(Integer, nullable=True)   # BCM pin powering the acceptor
    coin_edge          = Column(String(10), nullable=True)  # "RISING" | "FALLING"
    coin_debounce_ms   = Column(Integer, nullable=True)   # software debounce window (ms)
    coin_pulse_timeout = Column(String(16), nullable=True)  # seconds of silence to finalize (stored as text to allow decimals)


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
