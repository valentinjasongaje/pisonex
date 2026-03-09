import queue
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

    Detection uses a 1 ms polling thread instead of RPi.GPIO sysfs interrupts.
    This avoids the "Failed to add edge detection" RuntimeError that occurs when
    BCM 4 (GPCLK0) or any other pin has a stuck kernel interrupt from a previous run.

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

    def __init__(self, on_coin_complete, on_coin_progress=None):
        """
        on_coin_complete: callable(amount_pesos: int)
            Called when a full coin insertion is detected.
        on_coin_progress: callable(pesos_so_far: int) | None
            Called on each debounced pulse so the UI can show a running total.
        """
        self._on_complete = on_coin_complete
        self._on_progress = on_coin_progress
        self._pulse_count = 0
        self._lock = threading.Lock()
        self._timer: threading.Timer | None = None
        self._last_pulse_time = 0.0
        self._enabled = False
        self._detect_edge = "FALLING"   # overwritten in _setup_gpio
        self._stop_polling: threading.Event | None = None
        self._poll_thread: threading.Thread | None = None

        # Progress display queue — keeps the poller unblocked while LCD writes
        self._progress_queue: queue.SimpleQueue = queue.SimpleQueue()
        if on_coin_progress:
            self._progress_thread = threading.Thread(
                target=self._progress_worker,
                name="coin-progress",
                daemon=True,
            )
            self._progress_thread.start()

        self._setup_gpio()

    def _setup_gpio(self):
        try:
            import RPi.GPIO as GPIO
            self._GPIO = GPIO
            GPIO.setmode(GPIO.BCM)
        except ImportError:
            self._GPIO = None
            logger.warning("CoinSlot: RPi.GPIO not available — running in simulation mode")
            return

        # ── Relay pin ────────────────────────────────────────────────────────
        try:
            GPIO.setup(settings.RELAY_PIN, GPIO.OUT, initial=GPIO.LOW)
            logger.info("CoinSlot: relay pin BCM %d ready — starts LOW (unpowered)",
                        settings.RELAY_PIN)
        except Exception as e:
            logger.error("CoinSlot: relay pin BCM %d setup FAILED: %s — relay will not work",
                         settings.RELAY_PIN, e)

        # ── Coin signal pin (polling — no sysfs interrupt) ───────────────────
        # FALLING + PUD_UP  → custom board with optocoupler (active-LOW pulse)
        # RISING  + PUD_DOWN → direct / buffered signal     (active-HIGH pulse)
        edge_name = settings.COIN_EDGE.upper()
        if edge_name == "FALLING":
            pull, pull_name = GPIO.PUD_UP, "PUD_UP"
        else:
            pull, pull_name = GPIO.PUD_DOWN, "PUD_DOWN"

        self._detect_edge = edge_name

        try:
            GPIO.setup(settings.COIN_PIN, GPIO.IN, pull_up_down=pull)
            logger.info("CoinSlot: coin pin BCM %d ready (%s, %s) — polling mode",
                        settings.COIN_PIN, pull_name, edge_name)

            self._stop_polling = threading.Event()
            self._poll_thread = threading.Thread(
                target=self._poll_loop,
                name="coin-poller",
                daemon=True,
            )
            self._poll_thread.start()

        except Exception as e:
            logger.error("CoinSlot: coin pin BCM %d setup FAILED: %s — coin detection will not work",
                         settings.COIN_PIN, e)

    # ── Polling thread ────────────────────────────────────────────────────────

    def _poll_loop(self):
        """
        Polls BCM COIN_PIN every 1 ms and fires _pulse_detected on each
        matching edge transition.  1 ms is fast enough to catch the UCB Mini
        v4's ~50 ms pulses while adding negligible CPU load on the Pi.
        """
        GPIO = self._GPIO
        last_level = GPIO.input(settings.COIN_PIN)

        while not self._stop_polling.is_set():
            level = GPIO.input(settings.COIN_PIN)

            if level != last_level:
                is_match = (
                    (self._detect_edge == "FALLING" and level == 0) or
                    (self._detect_edge == "RISING"  and level == 1)
                )
                if is_match:
                    self._pulse_detected(settings.COIN_PIN)
                last_level = level

            time.sleep(0.001)   # 1 ms poll interval

    # ── Enable / disable relay ────────────────────────────────────────────────

    def enable(self):
        """Power the coin acceptor via relay and enable pulse detection."""
        if self._GPIO:
            self._GPIO.output(settings.RELAY_PIN, self._GPIO.HIGH)
            logger.info("CoinSlot: relay BCM %d → HIGH (coin acceptor powered)",
                        settings.RELAY_PIN)
        self._enabled = True

    def disable(self):
        """Cut power to coin acceptor via relay and clear any pending pulses."""
        self._enabled = False
        if self._GPIO:
            self._GPIO.output(settings.RELAY_PIN, self._GPIO.LOW)
            logger.info("CoinSlot: relay BCM %d → LOW (coin acceptor unpowered)",
                        settings.RELAY_PIN)
        with self._lock:
            self._pulse_count = 0
            if self._timer:
                self._timer.cancel()
                self._timer = None

    # ── Simulation ────────────────────────────────────────────────────────────

    def simulate_coin(self, pesos: int):
        """Simulate a coin insertion — for development/testing only."""
        logger.info("CoinSlot: simulating ₱%d", pesos)
        self._on_complete(pesos)

    # ── Pulse handling ────────────────────────────────────────────────────────

    def _pulse_detected(self, _channel):
        if not self._enabled:
            return

        now = time.monotonic()
        current_count = None
        with self._lock:
            # Software debounce — ignore transitions closer together than COIN_DEBOUNCE_MS
            if now - self._last_pulse_time < (settings.COIN_DEBOUNCE_MS / 1000):
                return
            self._last_pulse_time = now
            self._pulse_count += 1
            current_count = self._pulse_count
            logger.debug("CoinSlot: pulse #%d", self._pulse_count)

            if self._timer:
                self._timer.cancel()
            self._timer = threading.Timer(
                settings.COIN_PULSE_TIMEOUT,
                self._finalize,
            )
            self._timer.daemon = True
            self._timer.start()

        if self._on_progress:
            self._progress_queue.put(current_count)

    def _progress_worker(self):
        """
        Dedicated thread that drains the progress queue and calls _on_progress
        with only the latest value.  Keeps the polling thread unblocked while
        the LCD does slow I2C writes.
        """
        while True:
            pesos = self._progress_queue.get()   # blocks until a value arrives
            if pesos is None:                    # sentinel from cleanup()
                return
            # Drain the queue — if more pulses arrived while LCD was writing,
            # skip stale values and only display the latest count.
            while True:
                try:
                    latest = self._progress_queue.get_nowait()
                    if latest is None:
                        return
                    pesos = latest
                except queue.Empty:
                    break
            self._on_progress(pesos)

    def _finalize(self):
        with self._lock:
            amount = self._pulse_count
            self._pulse_count = 0
            self._timer = None

        if amount > 0:
            logger.info("CoinSlot: finalized ₱%d", amount)
            self._on_complete(amount)

    # ── Cleanup ───────────────────────────────────────────────────────────────

    def cleanup(self):
        self.disable()   # also sets relay LOW
        if self._stop_polling:
            self._stop_polling.set()
        self._progress_queue.put(None)  # stop progress worker
