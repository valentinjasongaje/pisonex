from datetime import datetime
from typing import Optional
from pydantic import BaseModel


# ── PC ────────────────────────────────────────────────────────────

class PCRegisterRequest(BaseModel):
    pc_number: int
    mac_address: str

class PCHeartbeatResponse(BaseModel):
    is_locked: bool
    remaining_minutes: int
    remaining_seconds: int
    session_token: Optional[str] = None

class PCStatusResponse(BaseModel):
    pc_number: int
    name: Optional[str]
    is_online: bool
    is_locked: bool
    ip_address: Optional[str]
    last_seen: Optional[datetime]
    remaining_minutes: int = 0


# ── Session ───────────────────────────────────────────────────────

class AddTimeRequest(BaseModel):
    pc_number: int
    pesos: int

class AddTimeResponse(BaseModel):
    pc_number: int
    pesos_added: int
    minutes_added: int
    total_minutes: int
    session_token: str

class SessionStatusResponse(BaseModel):
    has_session: bool
    remaining_minutes: int
    remaining_seconds: int
    minutes_granted: int = 0
    started_at: Optional[datetime] = None
    session_token: Optional[str] = None


# ── Auth ──────────────────────────────────────────────────────────

class AdminLoginRequest(BaseModel):
    username: str
    password: str

class TokenResponse(BaseModel):
    access_token: str
    token_type: str = "bearer"


# ── Coin Rate ─────────────────────────────────────────────────────

class CoinRateCreate(BaseModel):
    pesos: int
    minutes: int
    label: Optional[str] = None

class CoinRateResponse(BaseModel):
    id: int
    pesos: int
    minutes: int
    label: Optional[str]
    is_active: bool

    class Config:
        from_attributes = True


# ── Admin ─────────────────────────────────────────────────────────

class AdminAddTimeRequest(BaseModel):
    pc_number: int
    minutes: int

class EarningsResponse(BaseModel):
    total_pesos: int
    total_transactions: int
    period_days: int

class TransactionResponse(BaseModel):
    id: int
    pc_id: Optional[int]
    amount_pesos: int
    minutes_added: int
    created_at: datetime

    class Config:
        from_attributes = True

class LogResponse(BaseModel):
    id: int
    level: str
    source: str
    message: str
    created_at: datetime

    class Config:
        from_attributes = True
