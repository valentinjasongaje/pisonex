import threading
import logging
from config import settings

logger = logging.getLogger(__name__)

COLS = 20
ROWS = 4


class LCD:
    """
    Controls a 20x4 character LCD via I2C (PCF8574 backpack).
    Thread-safe: all writes are serialized through a lock.
    """

    def __init__(self):
        self._lock = threading.Lock()
        self._lcd = None
        self._displayed: list[str] = [""] * ROWS   # tracks what is on screen
        self._setup()

    def _setup(self):
        try:
            from RPLCD.i2c import CharLCD
            self._lcd = CharLCD(
                i2c_expander="PCF8574",
                address=settings.LCD_I2C_ADDRESS,
                port=settings.LCD_I2C_PORT,
                cols=COLS,
                rows=ROWS,
                dotsize=8,
                auto_linebreaks=False,
            )
            self.clear()
            logger.info(
                "LCD: initialized at I2C 0x%02X", settings.LCD_I2C_ADDRESS
            )
        except (ImportError, Exception) as e:
            self._lcd = None
            logger.warning("LCD: not available (%s) — running in simulation mode", e)

    def show(self, lines: list[str]):
        """
        Display up to 4 lines on the LCD.
        Only rewrites lines whose content has changed — eliminates flicker.
        Each line is truncated / padded to exactly 20 characters.
        """
        with self._lock:
            if self._lcd:
                for i in range(ROWS):
                    text = self._pad(lines[i] if i < len(lines) else "")
                    if text != self._displayed[i]:
                        self._lcd.cursor_pos = (i, 0)
                        self._lcd.write_string(text)
                        self._displayed[i] = text
            else:
                # Simulation: print to console
                border = "+" + "-" * COLS + "+"
                print(border)
                for i in range(ROWS):
                    text = lines[i] if i < len(lines) else ""
                    print(f"|{self._pad(text)}|")
                print(border)

    def clear(self):
        with self._lock:
            if self._lcd:
                self._lcd.clear()
            self._displayed = [""] * ROWS

    def cleanup(self):
        self.clear()
        if self._lcd:
            try:
                self._lcd.close(clear=True)
            except Exception:
                pass

    @staticmethod
    def _pad(text: str) -> str:
        return text[:COLS].ljust(COLS)


# ── Predefined screens ─────────────────────────────────────────────────────────

class Screen:
    @staticmethod
    def idle() -> list[str]:
        return [
            "  ** PISONET v1.0 **",
            "                    ",
            "  Enter PC Number:  ",
            "  [01-50] then [#]  ",
        ]

    @staticmethod
    def pc_entry(digits: str) -> list[str]:
        cursor = f"PC: {digits}_"
        return [
            "  Select PC Number  ",
            f"  {cursor:<18}",
            "                    ",
            "  Press # to confirm",
        ]

    @staticmethod
    def pc_selected(pc_number: int) -> list[str]:
        return [
            f"   PC {pc_number:02d} Selected   ",
            "                    ",
            "    Insert Coins    ",
            "   P5 = 30 minutes  ",
        ]

    @staticmethod
    def inserting_coins(pc_number: int, pesos: int, minutes: int) -> list[str]:
        """Real-time progress screen shown while coins are still being inserted."""
        return [
            f"   PC {pc_number:02d} Selected   ",
            "  Inserting coins...",
            f"  Inserted: P{pesos}",
            f"  = {minutes} min so far",
        ]

    @staticmethod
    def coin_inserted(pesos: int, minutes: int, total_min: int) -> list[str]:
        return [
            f"   +P{pesos} Inserted!    ",
            f"   +{minutes} min added      ",
            "                    ",
            f"   Total: {total_min} min     ",
        ]

    @staticmethod
    def time_added(pc_number: int, total_min: int) -> list[str]:
        return [
            f"  PC {pc_number:02d} Unlocked!  ",
            f"  Time: {total_min} minutes    ",
            "                    ",
            "     Enjoy!         ",
        ]

    @staticmethod
    def error(message: str) -> list[str]:
        return [
            "      !! ERROR !!   ",
            f"  {message[:16]:<16}    ",
            "                    ",
            "   Please try again ",
        ]

    @staticmethod
    def offline(pc_number: int) -> list[str]:
        return [
            f"   PC {pc_number:02d} Offline   ",
            "                    ",
            " PC is not connected",
            "  Try another PC    ",
        ]

    @staticmethod
    def timeout() -> list[str]:
        return [
            "   No coins inserted",
            "                    ",
            "  Session cancelled ",
            "                    ",
        ]
