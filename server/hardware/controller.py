import threading
import time
import logging
from enum import Enum, auto
from hardware.coin_slot import CoinSlot
from hardware.keypad import Keypad
from hardware.lcd import LCD, Screen
from config import settings
import command_store

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
        AWAITING_COINS → IDLE         : # pressed (user done inserting)
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
        self._countdown_timer: threading.Timer | None = None
        self._countdown_remaining: int = 0
        self._coins_inserted: bool = False  # tracks if any coin was inserted this session
        self._total_pesos: int = 0          # running total pesos this session
        self._total_seconds: int = 0        # running total seconds this session

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
        self._cancel_countdown()
        self._coin.disable()
        # Clear receiving-coins flag for the previously selected PC
        if self._selected_pc is not None:
            command_store.set_receiving_coins(self._selected_pc, False)
        self._digits = ""
        self._selected_pc = None
        self._coins_inserted = False
        self._total_pesos = 0
        self._total_seconds = 0
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

        # Start the visual countdown on the LCD
        self._start_countdown(settings.PC_IDLE_TIMEOUT)

    def _cancel_idle_timer(self):
        if self._idle_timer:
            self._idle_timer.cancel()
            self._idle_timer = None

    def _start_countdown(self, seconds: int):
        """Start a 1-second countdown that updates the LCD."""
        self._cancel_countdown()
        self._countdown_remaining = seconds
        # Schedule first tick via timer — calling _tick_countdown() directly
        # here would deadlock because _on_key already holds self._lock.
        self._countdown_timer = threading.Timer(1.0, self._tick_countdown)
        self._countdown_timer.daemon = True
        self._countdown_timer.start()

    def _cancel_countdown(self):
        self._countdown_remaining = 0
        if self._countdown_timer:
            self._countdown_timer.cancel()
            self._countdown_timer = None

    def _tick_countdown(self):
        """Called every second to refresh the LCD with the countdown."""
        if self._countdown_remaining <= 0:
            return

        with self._lock:
            if self._state == State.AWAITING_COINS and self._selected_pc is not None:
                if self._coins_inserted:
                    self._lcd.show(Screen.inserting_coins(
                        self._selected_pc,
                        self._total_pesos,
                        self._total_seconds,
                        self._countdown_remaining,
                    ))
                else:
                    self._lcd.show(Screen.pc_selected(
                        self._selected_pc,
                        self._countdown_remaining,
                    ))

        self._countdown_remaining -= 1

        if self._countdown_remaining >= 0:
            self._countdown_timer = threading.Timer(1.0, self._tick_countdown)
            self._countdown_timer.daemon = True
            self._countdown_timer.start()

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
                self._handle_awaiting_key(key)

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

    def _handle_awaiting_key(self, key: str):
        """Handle # press while awaiting coins — user is done inserting."""
        if key == CONFIRM_KEY:
            self._confirm_done()

    def _confirm_done(self):
        """User pressed # to signal they are done inserting coins."""
        pc = self._selected_pc
        if not pc:
            return

        self._cancel_idle_timer()
        self._cancel_countdown()
        self._coin.disable()

        if self._coins_inserted:
            # Show confirmation with total time
            session = self._service.get_active_session(pc)
            total_sec = session.granted_seconds if session else 0
            self._lcd.show(Screen.confirmed(pc, total_sec))
            logger.info("PC %02d: user confirmed done (total %ds)", pc, total_sec)
        else:
            self._lcd.show(Screen.timeout())
            logger.info("PC %02d: user pressed # with no coins — cancelling", pc)

        time.sleep(2)
        self._show_idle()

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

        # Reachability check — prefer a live ping when we have the PC's IP address
        # because the is_online flag can be stale for up to PC_HEARTBEAT_TIMEOUT
        # seconds after a PC actually goes offline.  Fall back to the DB flag +
        # last_seen timestamp only when no IP has been recorded yet.
        if pc.ip_address:
            if not self._check_pc_reachable_live(pc.ip_address):
                self._lcd.show(Screen.offline(pc_number))
                logger.info("PC %02d live ping to %s failed — unreachable",
                            pc_number, pc.ip_address)
                threading.Timer(2, self._show_idle).start()
                return
        else:
            # No IP recorded — fall back to DB flag + last_seen timestamp
            if not pc.is_online:
                self._lcd.show(Screen.offline(pc_number))
                threading.Timer(2, self._show_idle).start()
                return
            if pc.last_seen:
                from datetime import datetime, timedelta
                cutoff = datetime.utcnow() - timedelta(seconds=settings.PC_HEARTBEAT_TIMEOUT)
                if pc.last_seen < cutoff:
                    self._lcd.show(Screen.offline(pc_number))
                    logger.info("PC %02d last_seen is stale — unreachable", pc_number)
                    threading.Timer(2, self._show_idle).start()
                    return
            else:
                # Never seen — treat as unreachable
                self._lcd.show(Screen.offline(pc_number))
                threading.Timer(2, self._show_idle).start()
                return

        self._selected_pc = pc_number
        self._coins_inserted = False
        self._transition(State.AWAITING_COINS)

        if not command_store.is_coins_allowed(pc_number):
            self._lcd.show(Screen.error("Coin slot disabled"))
            logger.info("PC %02d selected but coin slot is disabled", pc_number)
            threading.Timer(2, self._show_idle).start()
            return

        # Signal that this PC is now receiving coins (shown on client lock screen)
        command_store.set_receiving_coins(pc_number, True)

        self._coin.enable()
        self._lcd.show(Screen.pc_selected(pc_number, settings.PC_IDLE_TIMEOUT))
        self._reset_idle_timer()
        logger.info("PC %02d selected — awaiting coins", pc_number)

    # ── Live reachability probe ───────────────────────────────────

    def _check_pc_reachable_live(self, ip_address: str) -> bool:
        """
        Send one ICMP ping to the PC's IP address.
        Returns True if the PC responds within ~1 s; False on timeout or error.
        Falls back to True (allow) if the ping binary is not found so that
        dev environments without a ping command don't block coin acceptance.
        """
        import subprocess
        import platform
        try:
            os_name = platform.system().lower()
            if os_name == "windows":
                cmd = ["ping", "-n", "1", "-w", "1000", ip_address]
            else:
                cmd = ["ping", "-c", "1", "-W", "1", ip_address]
            result = subprocess.run(cmd, capture_output=True, timeout=4)
            return result.returncode == 0
        except FileNotFoundError:
            logger.warning("ping binary not found — skipping live check for %s", ip_address)
            return True   # Can't verify — allow optimistically
        except Exception as exc:
            logger.warning("Live ping to %s raised %s — treating as unreachable", ip_address, exc)
            return False

    # ── Coin insertion handling ───────────────────────────────────

    def _on_coin_progress(self, pesos: int):
        """Called on each debounced pulse — updates LCD with running total."""
        with self._lock:
            if self._state != State.AWAITING_COINS:
                return
            pc = self._selected_pc
        if not command_store.is_coins_allowed(pc):
            return  # discard pulse — coin slot was disabled mid-session
        seconds_preview = (pesos // settings.DEFAULT_RATE_PESOS) * settings.DEFAULT_RATE_SECONDS
        self._lcd.show(Screen.inserting_coins(
            pc, pesos, seconds_preview, self._countdown_remaining
        ))

    def _on_coin(self, pesos: int):
        with self._lock:
            if self._state != State.AWAITING_COINS:
                logger.warning(
                    "Coin ₱%d received but state is %s — ignoring",
                    pesos, self._state.name,
                )
                return
            if not command_store.is_coins_allowed(self._selected_pc):
                logger.info(
                    "Coin ₱%d received for PC %02d but coin slot is disabled — ignoring",
                    pesos, self._selected_pc,
                )
                return
            self._transition(State.PROCESSING)
            self._cancel_idle_timer()
            self._cancel_countdown()
            self._coins_inserted = True

        # Run in a separate thread so GPIO ISR isn't blocked
        threading.Thread(
            target=self._process_coin,
            args=(pesos,),
            daemon=True,
            name="coin-processor",
        ).start()

    def _process_coin(self, pesos: int):
        try:
            seconds_added, session = self._service.add_time_by_pesos(
                pc_number=self._selected_pc,
                pesos=pesos,
            )
            # Reset idle/zero-time auto-shutdown timers — the PC is receiving time
            command_store.clear_idle_since(self._selected_pc)
            command_store.clear_zero_time_since(self._selected_pc)

            total_sec = session.granted_seconds
            self._lcd.show(Screen.coin_inserted(pesos, seconds_added, total_sec))
            time.sleep(settings.DISPLAY_CONFIRM_DELAY)

            with self._lock:
                self._total_pesos += pesos
                self._total_seconds = total_sec
                self._transition(State.AWAITING_COINS)
                self._lcd.show(Screen.inserting_coins(
                    self._selected_pc,
                    self._total_pesos,
                    self._total_seconds,
                    settings.PC_IDLE_TIMEOUT,
                ))
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
        self._cancel_countdown()
        self._keypad.cleanup()
        self._coin.cleanup()
        self._lcd.cleanup()
        try:
            import RPi.GPIO as GPIO
            GPIO.cleanup()
        except (ImportError, RuntimeError):
            pass
        logger.info("HardwareController: cleaned up")
