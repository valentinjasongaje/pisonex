import threading
import time
import logging
from enum import Enum, auto
from hardware.coin_slot import CoinSlot
from hardware.keypad import Keypad
from hardware.lcd import LCD, Screen
from config import settings

logger = logging.getLogger(__name__)

CONFIRM_KEY = '#'
CLEAR_KEY = '*'


class State(Enum):
    IDLE           = auto()   # Waiting for PC number input
    PC_SELECTION   = auto()   # User is typing PC number
    AWAITING_COINS = auto()   # PC selected, waiting for coins
    PROCESSING     = auto()   # Coin received, calling API/DB


class HardwareController:
    """
    Central state machine that coordinates the keypad, LCD, and coin slot.

    State transitions:
        IDLE → PC_SELECTION     : first digit pressed
        PC_SELECTION → IDLE     : * pressed (clear/cancel)
        PC_SELECTION → AWAITING_COINS : # pressed with valid PC
        AWAITING_COINS → PROCESSING   : coin inserted
        AWAITING_COINS → IDLE         : timeout or * pressed
        PROCESSING → AWAITING_COINS   : coin processed successfully
        PROCESSING → IDLE             : error during processing
    """

    def __init__(self, session_service):
        self._service = session_service
        self._state = State.IDLE
        self._digits = ""
        self._selected_pc: int | None = None
        self._lock = threading.Lock()
        self._idle_timer: threading.Timer | None = None

        self._lcd = LCD()
        self._coin = CoinSlot(
            on_coin_complete=self._on_coin,
            on_coin_progress=self._on_coin_progress,
        )
        self._keypad = Keypad(on_key_press=self._on_key)

        self._keypad.start()
        self._show_idle()
        logger.info("HardwareController: started")

    # ── State transition helpers ───────────────────────────────────

    def _transition(self, new_state: State):
        logger.debug("State: %s → %s", self._state.name, new_state.name)
        self._state = new_state

    def _show_idle(self):
        """Reset everything and show the idle/welcome screen."""
        self._cancel_idle_timer()
        self._coin.disable()
        self._digits = ""
        self._selected_pc = None
        self._transition(State.IDLE)
        self._lcd.show(Screen.idle())

    def _reset_idle_timer(self):
        """Restart the inactivity timer. Fires _on_timeout if user goes quiet."""
        self._cancel_idle_timer()
        self._idle_timer = threading.Timer(
            settings.PC_IDLE_TIMEOUT,
            self._on_timeout,
        )
        self._idle_timer.daemon = True
        self._idle_timer.start()

    def _cancel_idle_timer(self):
        if self._idle_timer:
            self._idle_timer.cancel()
            self._idle_timer = None

    def _on_timeout(self):
        with self._lock:
            if self._state == State.AWAITING_COINS:
                logger.info("Timeout waiting for coins on PC %02d", self._selected_pc)
                self._lcd.show(Screen.timeout())
                time.sleep(2)
            self._show_idle()

    # ── Key press handling ────────────────────────────────────────

    def _on_key(self, key: str):
        with self._lock:
            if self._state == State.PROCESSING:
                return  # Ignore input while processing

            if key == CLEAR_KEY:
                self._handle_clear()
                return

            if self._state == State.IDLE:
                self._handle_idle_key(key)
            elif self._state == State.PC_SELECTION:
                self._handle_selection_key(key)
            elif self._state == State.AWAITING_COINS:
                pass  # Only * is meaningful here (handled above)

    def _handle_clear(self):
        if self._state in (State.PC_SELECTION, State.AWAITING_COINS):
            logger.debug("Clear pressed — returning to IDLE")
            self._show_idle()

    def _handle_idle_key(self, key: str):
        if key.isdigit():
            self._digits = key
            self._transition(State.PC_SELECTION)
            self._lcd.show(Screen.pc_entry(self._digits))
            self._reset_idle_timer()

    def _handle_selection_key(self, key: str):
        if key.isdigit() and len(self._digits) < 2:
            self._digits += key
            self._lcd.show(Screen.pc_entry(self._digits))
            self._reset_idle_timer()

        elif key == CONFIRM_KEY:
            self._confirm_pc()

    def _confirm_pc(self):
        if not self._digits:
            self._lcd.show(Screen.error("No PC entered"))
            threading.Timer(2, self._show_idle).start()
            return

        pc_number = int(self._digits)
        pc = self._service.get_pc(pc_number)

        if not pc:
            self._lcd.show(Screen.error(f"PC {pc_number:02d} not found"))
            threading.Timer(2, self._show_idle).start()
            return

        if not pc.is_online:
            self._lcd.show(Screen.offline(pc_number))
            threading.Timer(2, self._show_idle).start()
            return

        self._selected_pc = pc_number
        self._transition(State.AWAITING_COINS)
        self._coin.enable()
        self._lcd.show(Screen.pc_selected(pc_number))
        self._reset_idle_timer()
        logger.info("PC %02d selected — awaiting coins", pc_number)

    # ── Coin insertion handling ───────────────────────────────────

    def _on_coin_progress(self, pesos: int):
        """Called on each debounced pulse — updates LCD with running total."""
        with self._lock:
            if self._state != State.AWAITING_COINS:
                return
            pc = self._selected_pc
        minutes_preview = (pesos // settings.DEFAULT_RATE_PESOS) * settings.DEFAULT_RATE_MINUTES
        self._lcd.show(Screen.inserting_coins(pc, pesos, minutes_preview))

    def _on_coin(self, pesos: int):
        with self._lock:
            if self._state != State.AWAITING_COINS:
                logger.warning(
                    "Coin ₱%d received but state is %s — ignoring",
                    pesos, self._state.name,
                )
                return
            self._transition(State.PROCESSING)
            self._cancel_idle_timer()

        # Run in a separate thread so GPIO ISR isn't blocked
        threading.Thread(
            target=self._process_coin,
            args=(pesos,),
            daemon=True,
            name="coin-processor",
        ).start()

    def _process_coin(self, pesos: int):
        try:
            minutes, session = self._service.add_time_by_pesos(
                pc_number=self._selected_pc,
                pesos=pesos,
            )
            total_min = session.minutes_granted
            self._lcd.show(Screen.coin_inserted(pesos, minutes, total_min))
            time.sleep(settings.DISPLAY_CONFIRM_DELAY)

            with self._lock:
                self._transition(State.AWAITING_COINS)
                self._lcd.show(Screen.pc_selected(self._selected_pc))
                self._reset_idle_timer()

        except Exception as e:
            logger.error("Error processing ₱%d for PC %02d: %s",
                         pesos, self._selected_pc, e)
            self._lcd.show(Screen.error("Processing error"))
            time.sleep(2)
            with self._lock:
                self._show_idle()

    # ── Public API (for testing/dev) ──────────────────────────────

    def simulate_key(self, key: str):
        """Inject a key press without physical hardware."""
        self._keypad.simulate_key(key)

    def simulate_coin(self, pesos: int):
        """Inject a coin without physical hardware."""
        self._coin.simulate_coin(pesos)

    # ── Cleanup ───────────────────────────────────────────────────

    def cleanup(self):
        self._cancel_idle_timer()
        self._keypad.cleanup()
        self._coin.cleanup()
        self._lcd.cleanup()
        try:
            import RPi.GPIO as GPIO
            GPIO.cleanup()
        except (ImportError, RuntimeError):
            pass
        logger.info("HardwareController: cleaned up")
