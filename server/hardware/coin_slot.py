import threading
import time
import logging
from config import settings

logger = logging.getLogger(__name__)


class CoinSlot:
    """
    Detects coin pulses from a coin slot connected to a GPIO pin.

    Each pulse = ₱1.  Pulses are batched: once PULSE_TIMEOUT seconds
    pass without a new pulse, the batch is considered complete and
    on_coin_complete is called with the total peso amount.
    """

    def __init__(self, on_coin_complete):
        """
        on_coin_complete: callable(amount_pesos: int)
            Called when a full coin insertion is detected.
        """
        self._on_complete = on_coin_complete
        self._pulse_count = 0
        self._lock = threading.Lock()
        self._timer: threading.Timer | None = None
        self._last_pulse_time = 0.0
        self._enabled = False

        self._setup_gpio()

    def _setup_gpio(self):
        try:
            import RPi.GPIO as GPIO
            self._GPIO = GPIO
            GPIO.setmode(GPIO.BCM)
            GPIO.setup(
                settings.COIN_PIN,
                GPIO.IN,
                pull_up_down=GPIO.PUD_UP,
            )
            GPIO.add_event_detect(
                settings.COIN_PIN,
                GPIO.RISING,
                callback=self._pulse_detected,
                bouncetime=settings.COIN_DEBOUNCE_MS,
            )
            logger.info("CoinSlot: GPIO %d initialized", settings.COIN_PIN)
        except (ImportError, RuntimeError):
            # Running on non-RPi hardware (dev mode)
            self._GPIO = None
            logger.warning("CoinSlot: RPi.GPIO not available — running in simulation mode")

    def enable(self):
        """Enable coin detection. Call after a PC is selected."""
        self._enabled = True
        logger.debug("CoinSlot: enabled")

    def disable(self):
        """Disable coin detection. Call when returning to idle."""
        self._enabled = False
        with self._lock:
            self._pulse_count = 0
            if self._timer:
                self._timer.cancel()
                self._timer = None
        logger.debug("CoinSlot: disabled")

    def simulate_coin(self, pesos: int):
        """Simulate a coin insertion — for development/testing only."""
        logger.info("CoinSlot: simulating ₱%d", pesos)
        self._on_complete(pesos)

    def _pulse_detected(self, _channel):
        if not self._enabled:
            return

        now = time.monotonic()
        with self._lock:
            # Extra software debounce on top of RPi hardware debounce
            if now - self._last_pulse_time < (settings.COIN_DEBOUNCE_MS / 1000):
                return
            self._last_pulse_time = now
            self._pulse_count += 1
            logger.debug("CoinSlot: pulse #%d", self._pulse_count)

            if self._timer:
                self._timer.cancel()
            self._timer = threading.Timer(
                settings.COIN_PULSE_TIMEOUT,
                self._finalize,
            )
            self._timer.daemon = True
            self._timer.start()

    def _finalize(self):
        with self._lock:
            amount = self._pulse_count
            self._pulse_count = 0
            self._timer = None

        if amount > 0:
            logger.info("CoinSlot: finalized ₱%d", amount)
            self._on_complete(amount)

    def cleanup(self):
        self.disable()
        if self._GPIO:
            try:
                self._GPIO.remove_event_detect(settings.COIN_PIN)
            except Exception:
                pass
