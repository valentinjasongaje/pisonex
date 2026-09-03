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
    traditional_mode_enabled: bool = False   # cashier-run café, no coin hardware — client hides coin-flow UI
    # Server-pushed wallpaper — optional, ignored by older clients
    wallpaper_url:  Optional[str] = None   # full URL to download wallpaper image
    wallpaper_hash: Optional[str] = None   # MD5 hex — client compares to cached hash
    # Membership fields
    membership_enabled: bool = False
    absorption_enabled: bool = False
    member_username: Optional[str] = None
    member_balance_seconds: int = 0
    member_can_logout: bool = False
    zero_time_logout_seconds: int = 0
    idle_shutdown_seconds: int = 0
    receiving_coins: bool = False
    coin_progress_pesos: int = 0
    coin_progress_seconds: int = 0
    minimum_logout_minutes: int = 0
    # Loyalty points — live balance, so the client tray can show it and gate the
    # Rewards Menu item without an extra round trip. The old flat
    # points_per_minute_redeem rate is NOT sent: per-item costs now come from the
    # reward catalog (GET /api/member/rewards), so shipping it to every PC every
    # second was pure waste. The DB column stays (additive-only migrations).
    points_enabled: bool = False
    member_loyalty_points: int = 0
    # Live-stream hint: >0 means server wants client to capture at this rate (ms),
    # 0 means client should use its own configured interval.
    capture_interval_ms: int = 0

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


# ── Rate Profile ──────────────────────────────────────────────────

class RateProfileCreate(BaseModel):
    name: str
    color: str = "#4f8ef7"

class RateProfileOut(BaseModel):
    id: int
    name: str
    color: str
    is_default: bool

    class Config:
        from_attributes = True


# ── Coin Rate ─────────────────────────────────────────────────────

class CoinRateCreate(BaseModel):
    pesos: int
    seconds: int
    label: Optional[str] = None
    profile_id: int = 1

class CoinRateResponse(BaseModel):
    id: int
    pesos: int
    seconds: int
    label: Optional[str]
    is_active: bool
    profile_id: Optional[int] = None

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

class MemberLoginRequest(BaseModel):
    pc_number: int
    username: str
    password: str

class MemberLogoutRequest(BaseModel):
    pc_number: int

class MemberLoginResponse(BaseModel):
    success: bool
    balance_seconds: int = 0
    absorbed_seconds: int = 0
    must_change_password: bool = False
    loyalty_points: int = 0
    error: Optional[str] = None

class MemberChangePasswordRequest(BaseModel):
    pc_number: int
    new_password: str

class MemberChangePasswordResponse(BaseModel):
    success: bool
    error: Optional[str] = None

class RewardItemResponse(BaseModel):
    id: int
    name: str
    kind: str
    points_cost: int
    minutes: Optional[int] = None
    is_active: bool = True

    class Config:
        from_attributes = True

class MemberRedeemRewardRequest(BaseModel):
    pc_number: int
    reward_item_id: int

class MemberRedeemRewardResponse(BaseModel):
    success: bool
    item_name: Optional[str] = None
    kind: Optional[str] = None
    points_spent: int = 0
    minutes_granted: Optional[int] = None
    remaining_points: int = 0
    status: Optional[str] = None  # "fulfilled" | "pending"
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
    points_enabled: bool = False
    loyalty_points: int = 0
    points_per_minute_redeem: int = 0

class MembershipConfigResponse(BaseModel):
    membership_enabled: bool
    absorption_enabled: bool
    logout_deduction_minutes: int
    minimum_logout_minutes: int
    zero_time_auto_logout_seconds: int
    idle_auto_shutdown_minutes: int
    member_heartbeat_timeout_minutes: int
    points_enabled: bool = False
    points_per_10_pesos: int = 1
    points_streak_bonus: int = 5
    points_per_minute_redeem: int = 2

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
    preset_amounts_enabled: Optional[bool] = None
    points_enabled: Optional[bool] = None
    points_per_10_pesos: Optional[int] = None
    points_streak_bonus: Optional[int] = None
    points_per_minute_redeem: Optional[int] = None


class AdminAddPesosRequest(BaseModel):
    pc_number: int
    pesos: int

class MemberListResponse(BaseModel):
    id: int
    username: str
    balance_seconds: int
    is_active: bool
    logged_in_pc_id: Optional[int]
    last_login_at: Optional[datetime]
    created_at: datetime
    must_change_password: bool = False
    loyalty_points: int = 0

    class Config:
        from_attributes = True
