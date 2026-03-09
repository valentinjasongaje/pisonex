import threading
import time
import logging
from config import settings

logger = logging.getLogger(__name__)


class CoinSlot:
    """
    Detects coin pulses from a UCB Mini v4 coin acceptor (GPIO pin, BCM 4).

    The UCB Mini v4 outputs 5V active-HIGH pulses (normally LOW).
    Each denomination sends N pulses: ₱1 = 1 pulse, ₱5 = 5 pulses, ₱10 = 10 pulses.
    Pulses are batched: once COIN_PULSE_TIMEOUT seconds pass without a new pulse,
    the batch is finalised and on_coin_complete is called with the total peso amount.

    Wiring:
        UCB Mini v4 12V  →  Relay COM/NO (switched 12V line from PSU)
        UCB Mini v4 GND  →  External 12V PSU (−) + Pi GND  (common ground)
        UCB Mini v4 SIG  →  1 kΩ → BCM 4 (Pin 7)
                                         ↓
                                        2 kΩ   (voltage divider: 5V → 3.3V)
                                         ↓
                                        GND (Pin 9)

        Relay (custom board):
            Relay signal  →  BCM 6 (Pin 31)   HIGH = relay ON = coin acceptor powered
            Relay VCC     →  Pi Pin 2  (5V)
            Relay GND     →  Pi Pin 14 (GND)
            Relay COM     →  12V PSU (+)
            Relay NO      →  UCB Mini v4 12V
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

            # Relay pin — OUTPUT, starts LOW (coin acceptor unpowered at boot)
            GPIO.setup(settings.RELAY_PIN, GPIO.OUT, initial=GPIO.LOW)

            # Coin signal pin — INPUT with pull-down; UCB Mini v4 pulses HIGH
            GPIO.setup(
                settings.COIN_PIN,
                GPIO.IN,
                pull_up_down=GPIO.PUD_DOWN,
            )
            GPIO.add_event_detect(
                settings.COIN_PIN,
                GPIO.RISING,
                callback=self._pulse_detected,
                bouncetime=settings.COIN_DEBOUNCE_MS,
            )
            logger.info(
                "CoinSlot: coin pin BCM %d, relay pin BCM %d — initialized",
                settings.COIN_PIN, settings.RELAY_PIN,
            )
        except (ImportError, RuntimeError):
            # Running on non-RPi hardware (dev mode)
            self._GPIO = None
            logger.warning("CoinSlot: RPi.GPIO not available — running in simulation mode")

    def enable(self):
        """Power the coin acceptor via relay and enable pulse detection."""
        if self._GPIO:
            self._GPIO.output(settings.RELAY_PIN, self._GPIO.HIGH)
        self._enabled = True
        logger.debug("CoinSlot: relay ON — coin acceptor powered")

    def disable(self):
        """Cut power to coin acceptor via relay and clear any pending pulses."""
        self._enabled = False
        if self._GPIO:
            self._GPIO.output(settings.RELAY_PIN, self._GPIO.LOW)
        with self._lock:
            self._pulse_count = 0
            if self._timer:
                self._timer.cancel()
                self._timer = None
        logger.debug("CoinSlot: relay OFF — coin acceptor unpowered")

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
        self.disable()   # also sets relay LOW
        if self._GPIO:
            try:
                self._GPIO.remove_event_detect(settings.COIN_PIN)
            except Exception:
                pass
