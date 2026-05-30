import threading
import logging
from hardware.coin_slot import CoinSlot
from config import settings
import command_store

logger = logging.getLogger(__name__)


class HardwareController:
    """
    Coin-slot-only controller.

    The 3x4 keypad and 20x4 I2C LCD have been removed from the hardware build.
    Coin acceptance is now driven entirely by the PC client: when a user clicks
    "Insert Coin" on the locked client, the client calls the server's
    /api/pc/{n}/request-coins endpoint, which calls request_coins_for_pc() here.

    The keypad.py / lcd.py modules are kept in the repository (unused) so the
    keypad/LCD build can be re-enabled later without rewriting this controller.

    Flow:
        IDLE ──(client "Insert Coin")──▶ ACCEPTING   (relay powered, slot live)
        ACCEPTING ──(coin pulses)──────▶ add time to that PC, stay ACCEPTING
        ACCEPTING ──(idle timeout)─────▶ IDLE         (relay off, slot closed)

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

        self._coin = CoinSlot(
            on_coin_complete=self._on_coin,
            on_coin_progress=self._on_coin_progress,
        )
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
                return

            seconds_added, session = self._service.add_time_by_pesos(
                pc_number=pc_number,
                pesos=pesos,
            )
            # Reset idle/zero-time auto-shutdown timers — the PC is receiving time
            command_store.clear_idle_since(pc_number)
            command_store.clear_zero_time_since(pc_number)

            logger.info("PC %02d: ₱%d → +%ds (total %ds)",
                        pc_number, pesos, seconds_added, session.granted_seconds)

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

    # ── Cleanup ───────────────────────────────────────────────────────

    def cleanup(self):
        self._cancel_idle_timer()
        self._coin.cleanup()
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
