import threading
import logging
from config import settings

logger = logging.getLogger(__name__)

COLS = 20
ROWS = 4

# ── Custom character bitmaps for double-height digits ─────────────────────────
# Each digit uses 2 rows. Top row uses characters 0-4, bottom row uses 0-4 + 0xFF (full block).
# We define 5 reusable building blocks that combine to form all digits 0-9.

# Custom char slots (0-7) — building blocks for big numbers
_CUSTOM_CHARS = [
    [0x1F, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],  # 0: top bar
    [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F, 0x1F],  # 1: bottom bar
    [0x1F, 0x1F, 0x00, 0x00, 0x00, 0x00, 0x1F, 0x1F],  # 2: top+bottom bar
    [0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x03, 0x07],  # 3: bottom-left curve
    [0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x18, 0x1C],  # 4: bottom-right curve
]


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
            # Load custom characters for big number display
            for i, bitmap in enumerate(_CUSTOM_CHARS):
                self._lcd.create_char(i, bitmap)
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


# ── Helper: center text ───────────────────────────────────────────────────────

def _center(text: str) -> str:
    """Center text within 20 characters."""
    return text.center(COLS)[:COLS]


def _bar(char: str = "\u2500") -> str:
    """Full-width horizontal divider."""
    return char * COLS


# ── Predefined screens ─────────────────────────────────────────────────────────

class Screen:
    @staticmethod
    def idle() -> list[str]:
        rate_p = settings.DEFAULT_RATE_PESOS
        rate_m = settings.DEFAULT_RATE_MINUTES
        return [
            _center("=== PISONET ==="),
            _center(f"P{rate_p} = {rate_m} min"),
            _center("Enter PC Number:"),
            _center("[01-50] then [#]"),
        ]

    @staticmethod
    def pc_entry(digits: str) -> list[str]:
        display = f"[ {digits}_ ]"
        return [
            _center("Select PC Number"),
            "",
            _center(display),
            _center("# Confirm  * Cancel"),
        ]

    @staticmethod
    def pc_selected(pc_number: int, remaining_secs: int = 0) -> list[str]:
        """
        Awaiting coins screen.
        remaining_secs: seconds left before timeout (0 = don't show countdown).
        """
        rate_p = settings.DEFAULT_RATE_PESOS
        rate_m = settings.DEFAULT_RATE_MINUTES

        pc_line = _center(f">>> PC {pc_number:02d} <<<")
        rate_line = _center(f"P{rate_p} = {rate_m} min")

        if remaining_secs > 0:
            countdown = f"Insert coin ({remaining_secs}s)"
        else:
            countdown = "Insert coins now"

        return [
            pc_line,
            rate_line,
            _center(countdown),
            _center("# Done  * Cancel"),
        ]

    @staticmethod
    def inserting_coins(pc_number: int, pesos: int, minutes: int,
                        remaining_secs: int = 0) -> list[str]:
        """Real-time progress screen shown while coins are still being inserted."""
        if remaining_secs > 0:
            timer_str = f"({remaining_secs}s)"
        else:
            timer_str = ""

        return [
            _center(f">>> PC {pc_number:02d} <<<"),
            _center(f"Inserted: P{pesos}"),
            _center(f"= {minutes} min {timer_str}"),
            _center("# Done  * Cancel"),
        ]

    @staticmethod
    def coin_inserted(pesos: int, minutes: int, total_min: int) -> list[str]:
        return [
            _center(f"+P{pesos} Inserted!"),
            _center(f"+{minutes} min added"),
            "",
            _center(f"Total: {total_min} min"),
        ]

    @staticmethod
    def confirmed(pc_number: int, total_min: int) -> list[str]:
        """Shown when user presses # to confirm they are done inserting."""
        return [
            _center(f"PC {pc_number:02d} Ready!"),
            _center(f"Time: {total_min} min"),
            "",
            _center("Enjoy!"),
        ]

    @staticmethod
    def time_added(pc_number: int, total_min: int) -> list[str]:
        return [
            _center(f"PC {pc_number:02d} Unlocked!"),
            _center(f"Time: {total_min} min"),
            "",
            _center("Enjoy!"),
        ]

    @staticmethod
    def error(message: str) -> list[str]:
        return [
            _center("!! ERROR !!"),
            _center(message[:18]),
            "",
            _center("Please try again"),
        ]

    @staticmethod
    def offline(pc_number: int) -> list[str]:
        return [
            _center(f"PC {pc_number:02d} Offline"),
            "",
            _center("PC not connected"),
            _center("Try another PC"),
        ]

    @staticmethod
    def timeout() -> list[str]:
        return [
            _center("Time's up!"),
            "",
            _center("Session cancelled"),
            "",
        ]
