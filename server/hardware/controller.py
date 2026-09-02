import threading
import logging
from hardware.coin_slot import CoinSlot
from hardware.keypad import Keypad
from hardware.lcd import LCD, Screen
from config import settings
import command_store

logger = logging.getLogger(__name__)


class HardwareController:
    """
    Coin-slot controller, with an optional keypad + LCD kiosk front-end.

    Coin acceptance is always driven by the PC client: when a user clicks
    "Insert Coin" on the locked client, the client calls the server's
    /api/pc/{n}/request-coins endpoint, which calls request_coins_for_pc() here.

    When settings.KEYPAD_ENABLED is True (dashboard Settings → Keypad, default
    off), a standalone 3x4 keypad + 20x4 I2C LCD box also drives the same coin
    slot: a customer types which PC they're paying for and confirms with '#'
    (see Screen.pc_entry / _on_key_press), which calls request_coins_for_pc()
    the same way the client does. Keypad/LCD are otherwise fully inert.

    Flow:
        IDLE ──(client "Insert Coin" OR keypad PC# + '#')──▶ ACCEPTING
        ACCEPTING ──(coin pulses)──────▶ add time to that PC, stay ACCEPTING
        ACCEPTING ──(idle timeout, client "Done", or keypad '#'/'*')──▶ IDLE

    Only one PC can use the slot at a time; a request for a different PC while
    the slot is busy is rejected so coins are never credited to the wrong PC.
    """

    def __init__(self, session_service):
        self._service = session_service
        self._lock = threading.Lock()
        self._selected_pc: int | None = None
        self._accepting: bool = False
        self._idle_timer: threading.Timer | None = None
        self._total_pesos: int = 0
        self._entry_digits: str = ""

        self._coin = CoinSlot(
            on_coin_complete=self._on_coin,
            on_coin_progress=self._on_coin_progress,
        )

        self._lcd: LCD | None = None
        self._keypad: Keypad | None = None
        if settings.KEYPAD_ENABLED:
            self._lcd = LCD()
            self._lcd.show(Screen.idle())
            self._keypad = Keypad(on_key_press=self._on_key_press)
            self._keypad.start()
            logger.info("HardwareController: started (coin-slot + keypad/LCD enabled)")
        else:
            logger.info("HardwareController: started (coin-slot only — keypad & LCD disabled)")

    # ── Idle timeout ──────────────────────────────────────────────────

    def _reset_idle_timer(self):
        """(Re)start the inactivity timer that closes the slot if no coins come."""
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
            if self._accepting:
                logger.info("Coin slot idle timeout for PC %02d — closing",
                            self._selected_pc or 0)
                self._close_slot()

    def _close_slot(self):
        """Power down the acceptor and return to IDLE. Caller must hold _lock."""
        self._cancel_idle_timer()
        self._coin.disable()
        if self._selected_pc is not None:
            command_store.set_receiving_coins(self._selected_pc, False)
            command_store.clear_coin_progress(self._selected_pc)
        self._selected_pc = None
        self._accepting = False
        self._total_pesos = 0
        self._show_lcd(Screen.idle())

    # ── LCD helpers ──────────────────────────────────────────────────

    def _show_lcd(self, lines: list[str]):
        """Update the LCD, if one is attached. No-op when keypad/LCD is disabled."""
        if self._lcd:
            self._lcd.show(lines)

    def _show_lcd_error(self, lines: list[str]):
        """Show a transient message, then revert to idle after DISPLAY_CONFIRM_DELAY
        unless the state has moved on (a session started, or digits are being typed)."""
        self._show_lcd(lines)
        if not self._lcd:
            return
        t = threading.Timer(settings.DISPLAY_CONFIRM_DELAY, self._revert_to_idle_if_no_activity)
        t.daemon = True
        t.start()

    def _revert_to_idle_if_no_activity(self):
        with self._lock:
            idle = not self._accepting and not self._entry_digits
        if idle:
            self._show_lcd(Screen.idle())

    # ── License check ─────────────────────────────────────────────────

    def _is_license_active(self) -> bool:
        """Check whether the software license/trial is still active."""
        try:
            from main import license_service
            if license_service and not license_service.is_active():
                return False
        except ImportError:
            pass
        return True

    # ── Public API — called by the REST layer ─────────────────────────

    def request_coins_for_pc(self, pc_number: int) -> tuple[bool, str]:
        """
        Open the coin slot for a PC. Called when a client presses 'Insert Coin'.
        Returns (success, message).
        """
        with self._lock:
            # Idempotent: already accepting for this exact PC — extend the window
            if self._accepting and self._selected_pc == pc_number:
                self._reset_idle_timer()
                return True, "Already accepting coins"

            # Don't hijack a slot that's busy with another PC
            if self._accepting:
                return False, "Coin slot is busy"

            if not self._is_license_active():
                logger.warning("Coin request blocked — license expired or not activated")
                return False, "License expired"

            if not command_store.is_coins_allowed(pc_number):
                return False, "Coin slot disabled for this PC"

            pc = self._service.get_pc(pc_number)
            if not pc:
                return False, f"PC {pc_number} not found"
            if not pc.is_online:
                return False, f"PC {pc_number} is offline"

            self._selected_pc = pc_number
            self._accepting = True
            self._total_pesos = 0
            command_store.set_receiving_coins(pc_number, True)
            self._coin.enable()
            self._reset_idle_timer()
            self._show_lcd(Screen.pc_selected(pc_number))
            logger.info("PC %02d: coin slot opened via client Insert Coin request", pc_number)

        return True, "Ready to accept coins"

    def close_coins_for_pc(self, pc_number: int) -> tuple[bool, str]:
        """
        Close the coin slot for a PC immediately. Called when a client presses
        'Done inserting Coins'. Flushes any coins still being counted so they
        are credited, then powers down the acceptor. Returns (success, message).
        """
        # Validate ownership without holding the lock across the flush.
        with self._lock:
            if not self._accepting or self._selected_pc != pc_number:
                return False, "Coin slot is not open for this PC"

        # Flush the in-progress batch OUTSIDE the lock: flush_pending() → _finalize
        # → _on_complete → _on_coin, which acquires self._lock itself (non-reentrant).
        self._coin.flush_pending()

        with self._lock:
            if self._accepting and self._selected_pc == pc_number:
                self._close_slot()
        logger.info("PC %02d: coin slot closed via client Done request", pc_number)
        return True, "Coin slot closed"

    def simulate_coin(self, pesos: int):
        """Inject a coin without physical hardware — for development/testing."""
        self._coin.simulate_coin(pesos)

    # ── Keypad handling (standalone kiosk unit — keypad + LCD only) ────

    def _on_key_press(self, key: str):
        """Drive the customer-facing PC-entry flow on the keypad + LCD.

        Not accepting coins yet: digits build up a 2-digit PC number, '*'
        clears it, '#' confirms and starts a session for that PC.
        Already accepting: '#' and '*' are treated identically — both finish
        the session via close_coins_for_pc(), which flushes and credits any
        coins already inserted. There's no "cancel and lose the money" path.
        """
        with self._lock:
            accepting = self._accepting
            pc = self._selected_pc

        if accepting:
            if key in ("#", "*") and pc is not None:
                self.close_coins_for_pc(pc)
            return

        if key.isdigit():
            with self._lock:
                if len(self._entry_digits) < 2:
                    self._entry_digits += key
                digits = self._entry_digits
            self._show_lcd(Screen.pc_entry(digits))
        elif key == "*":
            with self._lock:
                self._entry_digits = ""
            self._show_lcd(Screen.idle())
        elif key == "#":
            with self._lock:
                digits = self._entry_digits
                self._entry_digits = ""
            if digits:
                self._start_kiosk_session(int(digits))

    def _start_kiosk_session(self, pc_number: int):
        """Attempt to open the coin slot for a keypad-entered PC number."""
        pc = self._service.get_pc(pc_number)
        if not pc:
            self._show_lcd_error(Screen.error(f"PC {pc_number:02d} not found"))
            return

        ok, msg = self.request_coins_for_pc(pc_number)
        if not ok:
            if "offline" in msg.lower():
                self._show_lcd_error(Screen.offline(pc_number))
            else:
                self._show_lcd_error(Screen.error(msg))

    # ── Coin insertion handling ───────────────────────────────────────

    def _on_coin_progress(self, pesos: int):
        """Called on each debounced pulse — keeps the slot open while the user
        is still inserting coins (the running total is shown on the client)."""
        with self._lock:
            if not self._accepting:
                return
            pc = self._selected_pc
        if pc is None or not command_store.is_coins_allowed(pc):
            return
        if not self._is_license_active():
            return
        # Extend the idle window so multi-coin insertion doesn't time out mid-way,
        # and publish the live running total (finalized batches + current batch)
        # so the client can display it.
        with self._lock:
            if self._accepting:
                self._reset_idle_timer()
                command_store.set_coin_progress(pc, self._total_pesos + pesos)

    def _on_coin(self, pesos: int):
        with self._lock:
            if not self._accepting:
                logger.warning("Coin ₱%d received but slot is idle — ignoring", pesos)
                return
            if not command_store.is_coins_allowed(self._selected_pc):
                logger.info(
                    "Coin ₱%d received for PC %02d but coin slot is disabled — ignoring",
                    pesos, self._selected_pc,
                )
                return
            pc = self._selected_pc
            self._cancel_idle_timer()

        # Run DB/API work off the GPIO polling thread
        threading.Thread(
            target=self._process_coin,
            args=(pesos, pc),
            daemon=True,
            name="coin-processor",
        ).start()

    def _process_coin(self, pesos: int, pc_number: int):
        try:
            # Block coin processing when trial/license is expired
            if not self._is_license_active():
                logger.warning("Coin ₱%d for PC %02d rejected — license expired",
                               pesos, pc_number)
                with self._lock:
                    self._close_slot()
                self._show_lcd_error(Screen.error("License expired"))
                return

            # Attribute the transaction to whoever is logged in on this PC, if
            # anyone — mirrors api/sessions.py's REST add-time route. Without
            # this, a coin inserted while a member is logged in would create
            # an anonymous CoinTransaction (and, from a zero-time login, even
            # an anonymous Session), and loyalty points below would have no
            # member to award to.
            member_user_id = command_store.get_member_for_pc(pc_number)

            seconds_added, session = self._service.add_time_by_pesos(
                pc_number=pc_number,
                pesos=pesos,
                user_id=member_user_id,
            )
            # Reset idle/zero-time auto-shutdown timers — the PC is receiving time
            command_store.clear_idle_since(pc_number)
            command_store.clear_zero_time_since(pc_number)

            if member_user_id is not None:
                from services.membership_service import MembershipService
                MembershipService(self._service._db).award_coin_points(
                    member_user_id, session.pc_id, pesos
                )

            logger.info("PC %02d: ₱%d → +%ds (total %ds)",
                        pc_number, pesos, seconds_added, session.granted_seconds)
            self._show_lcd(Screen.coin_inserted(pesos, seconds_added, session.granted_seconds))

            # Keep the slot open for more coins (if still the active PC)
            with self._lock:
                if self._accepting and self._selected_pc == pc_number:
                    self._total_pesos += pesos
                    self._reset_idle_timer()
                    # Keep the displayed total monotonic across batch finalization
                    command_store.set_coin_progress(pc_number, self._total_pesos)

        except Exception as e:
            logger.error("Error processing ₱%d for PC %02d: %s", pesos, pc_number, e)
            with self._lock:
                self._close_slot()
            self._show_lcd_error(Screen.error("Error - try again"))

    # ── Cleanup ───────────────────────────────────────────────────────

    def cleanup(self):
        self._cancel_idle_timer()
        self._coin.cleanup()
        if self._keypad:
            self._keypad.cleanup()
        if self._lcd:
            self._lcd.cleanup()
        # Best-effort GPIO cleanup — works on either Raspberry Pi (RPi.GPIO)
        # or Orange Pi (OPi.GPIO), whichever is installed.
        for mod_name in ("RPi.GPIO", "OPi.GPIO"):
            try:
                import importlib
                importlib.import_module(mod_name).cleanup()
                break
            except (ImportError, RuntimeError):
                continue
        logger.info("HardwareController: cleaned up")
