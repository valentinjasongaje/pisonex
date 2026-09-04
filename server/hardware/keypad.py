import time
import threading
import logging
from config import settings

logger = logging.getLogger(__name__)

KEY_MAP = [
    ['1', '2', '3'],
    ['4', '5', '6'],
    ['7', '8', '9'],
    ['*', '0', '#'],
]


class Keypad:
    """
    Scans a 3x4 matrix keypad connected to GPIO pins.

    Rows are driven HIGH one at a time; columns are read as inputs
    with pull-down resistors. A HIGH column while a row is driven
    indicates a key press at that (row, col) position.
    """

    def __init__(self, on_key_press):
        """
        on_key_press: callable(key: str)
            Called once per distinct key press.
        """
        self._callback = on_key_press
        self._running = False
        self._thread: threading.Thread | None = None
        # True only when every row/col pin was claimed successfully. False means
        # the scanner runs inert — see _setup_gpio.
        self.ready = False

        self._setup_gpio()

    def _setup_gpio(self):
        try:
            import RPi.GPIO as GPIO
        except ImportError:
            self._GPIO = None
            logger.warning("Keypad: RPi.GPIO not available — running in simulation mode")
            return

        # Claiming a pin can fail for reasons a pin-number check can't catch: the
        # pin is already in use by something else, the kernel won't export it, or
        # the number is valid but not a usable GPIO on this board. None of that
        # may take down the HardwareController — the coin slot shares it, and a
        # keypad typo must never stop the café taking money. Degrade to an inert
        # keypad and say so loudly, the same way CoinSlot handles its pins.
        try:
            self._GPIO = GPIO
            GPIO.setmode(GPIO.BCM)
            for row_pin in settings.KEYPAD_ROWS:
                GPIO.setup(row_pin, GPIO.OUT, initial=GPIO.LOW)
            for col_pin in settings.KEYPAD_COLS:
                GPIO.setup(col_pin, GPIO.IN, pull_up_down=GPIO.PUD_DOWN)
            self.ready = True
            logger.info(
                "Keypad: rows=%s cols=%s",
                settings.KEYPAD_ROWS, settings.KEYPAD_COLS,
            )
        except Exception as e:
            self._GPIO = None
            logger.error(
                "Keypad: pin setup FAILED (rows=%s cols=%s): %s — keypad will not "
                "respond. Check the pins in Settings → Keypad.",
                settings.KEYPAD_ROWS, settings.KEYPAD_COLS, e,
            )

    def start(self):
        self._running = True
        self._thread = threading.Thread(
            target=self._scan_loop,
            name="keypad-scanner",
            daemon=True,
        )
        self._thread.start()
        logger.info("Keypad: scanner started")

    def stop(self):
        self._running = False
        if self._thread:
            self._thread.join(timeout=1)

    def simulate_key(self, key: str):
        """Inject a key press — for development/testing only."""
        logger.info("Keypad: simulating key '%s'", key)
        self._callback(key)

    def _scan_loop(self):
        last_key = None
        while self._running:
            if self._GPIO:
                key = self._scan_once()
            else:
                key = None   # No-op in simulation mode

            if key and key != last_key:
                logger.debug("Keypad: key pressed '%s'", key)
                self._callback(key)
            last_key = key
            time.sleep(settings.KEYPAD_SCAN_INTERVAL)

    def _scan_once(self) -> str | None:
        GPIO = self._GPIO
        for r_idx, row_pin in enumerate(settings.KEYPAD_ROWS):
            GPIO.output(row_pin, GPIO.HIGH)
            for c_idx, col_pin in enumerate(settings.KEYPAD_COLS):
                if GPIO.input(col_pin) == GPIO.HIGH:
                    GPIO.output(row_pin, GPIO.LOW)
                    return KEY_MAP[r_idx][c_idx]
            GPIO.output(row_pin, GPIO.LOW)
        return None

    def cleanup(self):
        self.stop()
