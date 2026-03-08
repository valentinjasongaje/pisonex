from datetime import datetime
from sqlalchemy import (
    Column, Integer, String, Boolean, DateTime, ForeignKey, Text, func
)
from sqlalchemy.orm import relationship
from database import Base


class User(Base):
    __tablename__ = "users"

    id          = Column(Integer, primary_key=True, index=True)
    username    = Column(String(50), unique=True, nullable=False, index=True)
    pin         = Column(String(255), nullable=False)   # bcrypt hashed
    balance_min = Column(Integer, default=0, nullable=False)
    created_at  = Column(DateTime, default=datetime.utcnow)
    is_active   = Column(Boolean, default=True)

    sessions     = relationship("Session", back_populates="user")
    transactions = relationship("CoinTransaction", back_populates="user")


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
    minutes_granted = Column(Integer, default=0, nullable=False)
    minutes_used    = Column(Integer, default=0, nullable=False)
    is_active       = Column(Boolean, default=True, index=True)
    session_token   = Column(String(36), unique=True, nullable=False)

    user = relationship("User", back_populates="sessions")
    pc   = relationship("PC", back_populates="sessions")


class CoinTransaction(Base):
    __tablename__ = "coin_transactions"

    id            = Column(Integer, primary_key=True, index=True)
    pc_id         = Column(Integer, ForeignKey("pcs.id"), nullable=True)
    user_id       = Column(Integer, ForeignKey("users.id"), nullable=True)
    amount_pesos  = Column(Integer, nullable=False)
    minutes_added = Column(Integer, nullable=False)
    created_at    = Column(DateTime, default=datetime.utcnow, index=True)

    pc   = relationship("PC", back_populates="transactions")
    user = relationship("User", back_populates="transactions")


class CoinRate(Base):
    __tablename__ = "coin_rates"

    id         = Column(Integer, primary_key=True, index=True)
    pesos      = Column(Integer, nullable=False)
    minutes    = Column(Integer, nullable=False)
    label      = Column(String(100))            # e.g. "₱5 = 30 minutes"
    is_active  = Column(Boolean, default=True)
    created_at = Column(DateTime, default=datetime.utcnow)


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
    created_at = Column(DateTime, default=datetime.utcnow)
