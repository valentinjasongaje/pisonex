from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8")

    # Server
    DATABASE_URL: str = "sqlite:///./pisonet.db"
    SECRET_KEY: str = "change-this-to-a-random-256-bit-secret-key"
    TOKEN_EXPIRE_HOURS: int = 8
    SERVER_HOST: str = "0.0.0.0"
    SERVER_PORT: int = 8000

    # Coin rates
    DEFAULT_RATE_PESOS: int = 5
    DEFAULT_RATE_MINUTES: int = 30

    # PC monitoring
    PC_HEARTBEAT_TIMEOUT: int = 30  # seconds before PC is marked offline

    # Admin credentials (used to seed admin on first run)
    ADMIN_USERNAME: str = "admin"
    ADMIN_PASSWORD: str = "admin123"

    # GPIO pins (BCM numbering)
    COIN_PIN: int = 4
    RELAY_PIN: int = 6          # BCM 6 (Physical Pin 31) — relay that powers the coin acceptor
    KEYPAD_ROWS: list[int] = [17, 27, 22, 5]   # R1, R2, R3, R4
    KEYPAD_COLS: list[int] = [9, 11, 10]        # C1, C2, C3
    LCD_I2C_ADDRESS: int = 0x27
    LCD_I2C_PORT: int = 1

    # Coin signal edge polarity — depends on whether the custom board inverts the signal.
    #   "RISING"  — direct connection or buffer (signal goes HIGH on coin pulse)
    #   "FALLING" — custom board with optocoupler (signal goes LOW on coin pulse)
    # Run server/test_coin_signal.py to detect which one your board uses.
    COIN_EDGE: str = "FALLING"

    # Timing — tuned for UCB Mini v4 coin acceptor
    # UCB Mini v4 pulse gap is ~50-80 ms; 30 ms debounce catches bounce without
    # swallowing legitimate pulses on ₱5 / ₱10 multi-pulse coins.
    COIN_DEBOUNCE_MS: int = 30
    COIN_PULSE_TIMEOUT: float = 3.0   # seconds of silence before finalizing — allows inserting multiple coins
    KEYPAD_SCAN_INTERVAL: float = 0.05
    PC_IDLE_TIMEOUT: int = 30       # seconds before returning to idle screen
    DISPLAY_CONFIRM_DELAY: int = 3  # seconds to show confirmation message


settings = Settings()
