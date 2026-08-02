from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    # Server
    DATABASE_URL: str = "sqlite:///./pisonet.db"
    SECRET_KEY: str = "change-this-to-a-random-256-bit-secret-key"
    TOKEN_EXPIRE_HOURS: int = 8
    SERVER_HOST: str = "0.0.0.0"
    SERVER_PORT: int = 80

    # Coin rates (seconds-based)
    DEFAULT_RATE_PESOS: int = 5
    DEFAULT_RATE_SECONDS: int = 1800  # 30 minutes

    # PC monitoring
    PC_HEARTBEAT_TIMEOUT: int = 30  # seconds before PC is marked offline

    # Admin credentials (used to seed admin on first run)
    ADMIN_USERNAME: str = "admin"
    ADMIN_PASSWORD: str = "admin123"

    # Branch name for this installation — shown in pisonex.com customer portal.
    # Set this so the portal can group PCs by branch (e.g. "Tomas Morato Branch").
    # Defaults to "My Internet Cafe" so fresh installs have a usable identifier.
    BRANCH_NAME: str = "My Internet Cafe"

    # IANA timezone used ONLY for displaying timestamps in the dashboard
    # (transactions, logs, last-seen, etc.). All timestamps are still stored
    # in the database as naive UTC (datetime.utcnow()) — that never changes —
    # this just controls what the admin sees on screen. Defaults to the
    # Philippines, where PisoNet cafes are based.
    TIMEZONE: str = "Asia/Manila"

    # PC client API key — shared secret sent in X-API-Key header by all clients.
    # Leave empty ("") to disable auth (default, backward-compatible).
    # Set a strong random value in .env to enable: CLIENT_API_KEY=your-secret-here
    CLIENT_API_KEY: str = ""

    # HMAC secret for signing license API payloads sent to pisonex.com.
    # Must match the value configured on the pisonex.com server.
    # Auto-generated on first startup if left as the default.
    LICENSE_HMAC_SECRET: str = "PISONEX-INTERNAL-2026-CHANGE-BEFORE-RELEASE"

    # Membership defaults (used to seed MembershipConfig on first run)
    MEMBERSHIP_ENABLED: bool = False
    ABSORPTION_ENABLED: bool = False
    LOGOUT_DEDUCTION_MINUTES: int = 5
    MINIMUM_LOGOUT_MINUTES: int = 10
    ZERO_TIME_AUTO_LOGOUT_SECONDS: int = 30
    IDLE_AUTO_SHUTDOWN_MINUTES: int = 5
    MEMBER_HEARTBEAT_TIMEOUT_MINUTES: int = 60



settings = Settings()
