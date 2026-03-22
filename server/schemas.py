from datetime import datetime
from typing import Optional
from pydantic import BaseModel


# ── PC ────────────────────────────────────────────────────────────

class PCRegisterRequest(BaseModel):
    pc_number: int
    mac_address: str

class PCHeartbeatResponse(BaseModel):
    is_locked: bool
    remaining_seconds: int
    session_token: Optional[str] = None
    time_added_seconds: int = 0
    # Remote control fields — all optional, ignored by older clients
    pending_command:  Optional[str] = None   # "shutdown" | "restart" | "lock" | "open_url"
    command_payload:  Optional[str] = None   # URL / app path for "open_url"
    admin_message:    Optional[str] = None   # per-PC message (popped on delivery)
    announcement:     Optional[str] = None   # shop-wide broadcast (persistent)
    coin_slot_enabled: bool = True           # combined global + per-PC coin slot state
    # Server-pushed wallpaper — optional, ignored by older clients
    wallpaper_url:  Optional[str] = None   # full URL to download wallpaper image
    wallpaper_hash: Optional[str] = None   # MD5 hex — client compares to cached hash
    # Server license status — clients can show warnings if server is unlicensed
    server_licensed: bool = True
    # Membership fields
    membership_enabled: bool = False
    absorption_enabled: bool = False
    member_username: Optional[str] = None
    member_balance_seconds: int = 0
    member_can_logout: bool = False
    zero_time_logout_seconds: int = 0
    idle_shutdown_seconds: int = 0
    receiving_coins: bool = False

class PCStatusResponse(BaseModel):
    pc_number: int
    name: Optional[str]
    is_online: bool
    is_locked: bool
    ip_address: Optional[str]
    last_seen: Optional[datetime]
    remaining_seconds: int = 0


# ── Session ───────────────────────────────────────────────────────

class AddTimeRequest(BaseModel):
    pc_number: int
    pesos: int

class AddTimeResponse(BaseModel):
    pc_number: int
    pesos_added: int
    seconds_added: int
    total_seconds: int
    session_token: str

class SessionStatusResponse(BaseModel):
    has_session: bool
    remaining_seconds: int
    granted_seconds: int = 0
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
    seconds: int
    label: Optional[str] = None

class CoinRateResponse(BaseModel):
    id: int
    pesos: int
    seconds: int
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
    amount_php: int
    seconds_added: int
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


# ── Member ────────────────────────────────────────────────────────

class MemberRegisterRequest(BaseModel):
    username: str
    password: str
    pc_number: int

class MemberLoginRequest(BaseModel):
    pc_number: int
    username: str
    password: str

class MemberLogoutRequest(BaseModel):
    pc_number: int

class MemberRegisterResponse(BaseModel):
    success: bool
    user_id: int = 0
    username: str = ""
    absorbed_seconds: int = 0
    error: Optional[str] = None

class MemberLoginResponse(BaseModel):
    success: bool
    balance_seconds: int = 0
    absorbed_seconds: int = 0
    error: Optional[str] = None

class MemberLogoutResponse(BaseModel):
    success: bool
    remaining_seconds: int = 0
    deducted_seconds: int = 0
    error: Optional[str] = None

class MemberStatusResponse(BaseModel):
    membership_enabled: bool = False
    absorption_enabled: bool = False
    logged_in_user: Optional[str] = None
    balance_seconds: int = 0
    can_logout: bool = False
    logout_denied_reason: Optional[str] = None

class MembershipConfigResponse(BaseModel):
    membership_enabled: bool
    absorption_enabled: bool
    logout_deduction_minutes: int
    minimum_logout_minutes: int
    zero_time_auto_logout_seconds: int
    idle_auto_shutdown_minutes: int
    member_heartbeat_timeout_minutes: int

    class Config:
        from_attributes = True

class MembershipConfigUpdate(BaseModel):
    membership_enabled: Optional[bool] = None
    absorption_enabled: Optional[bool] = None
    logout_deduction_minutes: Optional[int] = None
    minimum_logout_minutes: Optional[int] = None
    zero_time_auto_logout_seconds: Optional[int] = None
    idle_auto_shutdown_minutes: Optional[int] = None
    member_heartbeat_timeout_minutes: Optional[int] = None

class MemberListResponse(BaseModel):
    id: int
    username: str
    balance_seconds: int
    is_active: bool
    logged_in_pc_id: Optional[int]
    last_login_at: Optional[datetime]
    created_at: datetime

    class Config:
        from_attributes = True
