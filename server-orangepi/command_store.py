"""
In-memory store for admin-to-client commands, messages, announcements,
coin slot control flags, and wallpaper state.

All state resets to safe defaults on server restart:
  - No pending commands or messages
  - No active announcement
  - Coin slot globally enabled (coins accepted)
  - All per-PC coin slot overrides cleared (coins accepted)
  - No wallpaper set (restored from disk on startup via main.py)
"""

from threading import Lock

_lock = Lock()

# ── Per-PC command queue (latest wins — only one pending at a time) ───────────
_commands: dict[int, dict] = {}   # pc_number → {"type": str, "payload": str}

# ── Per-PC message queue (popped on delivery, shown once) ───────────────────
_messages: dict[int, str] = {}    # pc_number → message text

# ── Shop-wide announcement (persistent until explicitly cleared) ─────────────
_announcement: str | None = None

# ── Coin slot control ────────────────────────────────────────────────────────
_coin_slot_enabled: bool = True              # global relay flag
_pc_coin_enabled: dict[int, bool] = {}       # per-PC override (absent = enabled)
_schedule_blocked: bool = False              # True when a CoinSchedule time range is active

# RateProfile.id currently activated by a RateSchedule window ("Happy Hour"),
# or None if no schedule is in effect. Recomputed every 30s by
# main.py's _run_schedule_tick and read on every coin credit / heartbeat, so
# pricing decisions never do a DB scan of rate_schedules per request.
_active_rate_schedule_profile_id: int | None = None


# ── Commands ─────────────────────────────────────────────────────────────────

def push_command(pc_number: int, type: str, payload: str = "") -> None:
    """Queue a command for a PC. Overwrites any un-delivered previous command."""
    with _lock:
        _commands[pc_number] = {"type": type, "payload": payload}


def pop_command(pc_number: int) -> dict | None:
    """Return and remove the pending command for a PC, or None if none."""
    with _lock:
        return _commands.pop(pc_number, None)


# ── Messages ─────────────────────────────────────────────────────────────────

def push_message(pc_number: int, text: str) -> None:
    """Queue a message for a PC. Overwrites any un-delivered previous message."""
    with _lock:
        _messages[pc_number] = text.strip()


def pop_message(pc_number: int) -> str | None:
    """Return and remove the pending message for a PC, or None if none."""
    with _lock:
        return _messages.pop(pc_number, None)


# ── Announcement ─────────────────────────────────────────────────────────────

def set_announcement(text: str | None) -> None:
    """Set (or clear with None) the shop-wide announcement broadcast to all PCs."""
    global _announcement
    with _lock:
        _announcement = text.strip() if text else None


def get_announcement() -> str | None:
    """Return the current announcement text, or None if cleared."""
    with _lock:
        return _announcement


# ── Coin slot control ─────────────────────────────────────────────────────────

def set_coin_slot_enabled(enabled: bool) -> None:
    """Set the global coin slot relay state (affects all PCs)."""
    global _coin_slot_enabled
    with _lock:
        _coin_slot_enabled = enabled


def is_coin_slot_enabled() -> bool:
    """Return the global coin slot relay state."""
    with _lock:
        return _coin_slot_enabled


def set_pc_coin_enabled(pc_number: int, enabled: bool) -> None:
    """Set a per-PC coin acceptance override."""
    with _lock:
        _pc_coin_enabled[pc_number] = enabled


def is_pc_coin_enabled(pc_number: int) -> bool:
    """Return whether this specific PC is allowed to accept coins (per-PC override)."""
    with _lock:
        return _pc_coin_enabled.get(pc_number, True)


def set_schedule_blocked(blocked: bool) -> None:
    """Set whether a CoinSchedule time range is currently active (blocks all coins)."""
    global _schedule_blocked
    with _lock:
        _schedule_blocked = blocked


def is_schedule_blocked() -> bool:
    """Return True if the coin slot is currently blocked by a schedule."""
    with _lock:
        return _schedule_blocked


def set_active_rate_schedule_profile_id(profile_id: int | None) -> None:
    global _active_rate_schedule_profile_id
    with _lock:
        _active_rate_schedule_profile_id = profile_id


def get_active_rate_schedule_profile_id() -> int | None:
    """The RateProfile.id a Happy Hour window currently activates, or None."""
    with _lock:
        return _active_rate_schedule_profile_id


def get_coin_block_reason() -> str | None:
    """Return 'schedule' if currently blocked by a schedule, None otherwise."""
    with _lock:
        if _schedule_blocked:
            return "schedule"
        return None


def is_coins_allowed(pc_number: int) -> bool:
    """Return True only if global relay, schedule, AND per-PC override all allow coins."""
    with _lock:
        return _coin_slot_enabled and not _schedule_blocked and _pc_coin_enabled.get(pc_number, True)


def get_all_pc_coin_states() -> dict[int, bool]:
    """Return a copy of all per-PC coin overrides."""
    with _lock:
        return dict(_pc_coin_enabled)


# ── Wallpaper ─────────────────────────────────────────────────────────────────

_wallpaper_url: str | None = None       # relative URL e.g. "/static/wallpapers/bg.jpg"
_wallpaper_hash: str | None = None      # MD5 hex digest of the file
_pc_wallpaper: dict[int, dict] = {}     # pc_number → {"url": str, "hash": str}


def set_wallpaper(url: str | None, hash: str | None) -> None:
    """Set (or clear) the global wallpaper pushed to all PCs."""
    global _wallpaper_url, _wallpaper_hash
    with _lock:
        _wallpaper_url = url
        _wallpaper_hash = hash


def get_wallpaper() -> tuple[str | None, str | None]:
    """Return the global (url, hash) tuple."""
    with _lock:
        return _wallpaper_url, _wallpaper_hash


def set_pc_wallpaper(pc_number: int, url: str | None, hash: str | None) -> None:
    """Set a per-PC wallpaper override."""
    with _lock:
        if url is None:
            _pc_wallpaper.pop(pc_number, None)
        else:
            _pc_wallpaper[pc_number] = {"url": url, "hash": hash}


def clear_pc_wallpaper(pc_number: int) -> None:
    """Remove per-PC override so it falls back to global."""
    with _lock:
        _pc_wallpaper.pop(pc_number, None)


def get_pc_wallpaper(pc_number: int) -> tuple[str | None, str | None]:
    """Return (url, hash) for a PC — per-PC override first, then global fallback."""
    with _lock:
        override = _pc_wallpaper.get(pc_number)
        if override:
            return override["url"], override["hash"]
        return _wallpaper_url, _wallpaper_hash


# ── Receiving coins flag (set by hardware controller when PC is selected) ─────
_receiving_coins: dict[int, bool] = {}      # pc_number → True when coins are being inserted


def set_receiving_coins(pc_number: int, active: bool) -> None:
    """Mark whether the hardware controller is currently accepting coins for this PC."""
    with _lock:
        if active:
            _receiving_coins[pc_number] = True
        else:
            _receiving_coins.pop(pc_number, None)


def is_receiving_coins(pc_number: int) -> bool:
    """Return True if the hardware controller is currently accepting coins for this PC."""
    with _lock:
        return _receiving_coins.get(pc_number, False)


# ── Live coin-insertion running total (cumulative pesos this coin session) ────
_coin_progress: dict[int, int] = {}     # pc_number → cumulative pesos inserted


def set_coin_progress(pc_number: int, pesos: int) -> None:
    """Store the running peso total for the PC currently inserting coins."""
    with _lock:
        _coin_progress[pc_number] = pesos


def get_coin_progress(pc_number: int) -> int:
    """Return the running peso total for this PC (0 if none in progress)."""
    with _lock:
        return _coin_progress.get(pc_number, 0)


def clear_coin_progress(pc_number: int) -> None:
    """Reset the running total once the coin slot closes for this PC."""
    with _lock:
        _coin_progress.pop(pc_number, None)


# ── Member-PC binding (volatile, rebuilt from DB on startup) ──────────────────

_member_pc_binding: dict[int, int] = {}     # pc_number → user_id
_pc_idle_since: dict[int, float] = {}       # pc_number → timestamp (time.time())
_zero_time_since: dict[int, float] = {}     # pc_number → timestamp (time.time())
_login_attempts: dict[str, list[float]] = {}  # username → [timestamp, ...]


def bind_member(pc_number: int, user_id: int) -> None:
    """Record that a member is logged into a PC."""
    with _lock:
        _member_pc_binding[pc_number] = user_id


def unbind_member(pc_number: int) -> int | None:
    """Remove member binding for a PC. Returns the user_id that was bound, or None."""
    with _lock:
        return _member_pc_binding.pop(pc_number, None)


def get_member_for_pc(pc_number: int) -> int | None:
    """Return the user_id logged into this PC, or None."""
    with _lock:
        return _member_pc_binding.get(pc_number)


def get_pc_for_member(user_id: int) -> int | None:
    """Return the pc_number this member is logged into, or None."""
    with _lock:
        for pc, uid in _member_pc_binding.items():
            if uid == user_id:
                return pc
        return None


def get_all_member_bindings() -> dict[int, int]:
    """Return a copy of all member-PC bindings."""
    with _lock:
        return dict(_member_pc_binding)


def rebuild_member_bindings(bindings: dict[int, int]) -> None:
    """Replace all member-PC bindings (used on startup to rebuild from DB)."""
    with _lock:
        _member_pc_binding.clear()
        _member_pc_binding.update(bindings)


# ── Idle auto-shutdown tracking ───────────────────────────────────────────────

def set_idle_since(pc_number: int, timestamp: float) -> None:
    with _lock:
        _pc_idle_since[pc_number] = timestamp


def get_idle_since(pc_number: int) -> float | None:
    with _lock:
        return _pc_idle_since.get(pc_number)


def clear_idle_since(pc_number: int) -> None:
    with _lock:
        _pc_idle_since.pop(pc_number, None)


# ── Had-session-since-boot tracking ───────────────────────────────────────────
# Prevents idle auto-shutdown from looping: a PC that just booted and has not
# yet had any session will not be idle-shutdown again until it is actually used.

_pc_had_session: dict[int, bool] = {}   # pc_number → True once a session starts


def mark_pc_had_session(pc_number: int) -> None:
    with _lock:
        _pc_had_session[pc_number] = True


def clear_pc_had_session(pc_number: int) -> None:
    with _lock:
        _pc_had_session.pop(pc_number, None)


def pc_had_session(pc_number: int) -> bool:
    with _lock:
        return _pc_had_session.get(pc_number, False)


# ── Zero-time auto-logout tracking ───────────────────────────────────────────

def set_zero_time_since(pc_number: int, timestamp: float) -> None:
    with _lock:
        _zero_time_since[pc_number] = timestamp


def get_zero_time_since(pc_number: int) -> float | None:
    with _lock:
        return _zero_time_since.get(pc_number)


def clear_zero_time_since(pc_number: int) -> None:
    with _lock:
        _zero_time_since.pop(pc_number, None)


# ── Login rate limiting ───────────────────────────────────────────────────────

import time as _time

# ── Watched PCs (MJPEG live stream) ──────────────────────────────────────────

_WATCHED_TTL = 12  # seconds

# {pc_number: expire_timestamp}
_watched: dict[int, float] = {}


def set_watched(pc_number: int) -> None:
    """Mark a PC as actively watched by admin (TTL = 12 s). Renewed by dashboard keepalive."""
    with _lock:
        _watched[pc_number] = _time.time() + _WATCHED_TTL


def is_watched(pc_number: int) -> bool:
    """Return True if an admin is currently viewing the MJPEG stream for this PC."""
    with _lock:
        expire = _watched.get(pc_number)
        if expire is None:
            return False
        if _time.time() > expire:
            _watched.pop(pc_number, None)
            return False
        return True


# ── Purge (called when a PC is deleted) ──────────────────────────────────────

def purge_pc(pc_number: int) -> None:
    """Remove every per-PC entry so nothing stale resurfaces if the number is reused."""
    with _lock:
        _commands.pop(pc_number, None)
        _messages.pop(pc_number, None)
        _pc_coin_enabled.pop(pc_number, None)
        _pc_wallpaper.pop(pc_number, None)
        _receiving_coins.pop(pc_number, None)
        _coin_progress.pop(pc_number, None)
        _member_pc_binding.pop(pc_number, None)
        _pc_idle_since.pop(pc_number, None)
        _pc_had_session.pop(pc_number, None)
        _zero_time_since.pop(pc_number, None)
        _watched.pop(pc_number, None)


_LOGIN_WINDOW_SECONDS = 60
_LOGIN_MAX_ATTEMPTS = 5

# Admin dashboard login. Keyed by client IP, NOT username: keying the admin
# limiter by username would let anyone lock the owner out of their own café by
# hammering "admin" from the customer Wi-Fi. Per-IP throttles the attacker while
# the owner, on a different address, is unaffected.
_ADMIN_LOGIN_WINDOW_SECONDS = 300
_ADMIN_LOGIN_MAX_ATTEMPTS = 10
_admin_login_attempts: dict[str, list[float]] = {}


def check_admin_login_rate(client_ip: str) -> bool:
    """Return True if an admin dashboard login attempt is allowed."""
    now = _time.time()
    with _lock:
        attempts = [
            t for t in _admin_login_attempts.get(client_ip, [])
            if now - t < _ADMIN_LOGIN_WINDOW_SECONDS
        ]
        _admin_login_attempts[client_ip] = attempts
        if len(attempts) >= _ADMIN_LOGIN_MAX_ATTEMPTS:
            return False
        attempts.append(now)
        return True


def clear_admin_login_rate(client_ip: str) -> None:
    """Reset the counter after a successful login, so a legitimate admin who
    fumbled their password a few times isn't throttled afterwards."""
    with _lock:
        _admin_login_attempts.pop(client_ip, None)


def check_login_rate(username: str) -> bool:
    """Return True if the login attempt is allowed, False if rate-limited."""
    now = _time.time()
    with _lock:
        attempts = _login_attempts.get(username, [])
        # Prune old attempts outside the window
        attempts = [t for t in attempts if now - t < _LOGIN_WINDOW_SECONDS]
        _login_attempts[username] = attempts
        if len(attempts) >= _LOGIN_MAX_ATTEMPTS:
            return False
        attempts.append(now)
        return True


# ── Keypad self-test ──────────────────────────────────────────────────────────
# Backs the dashboard's Settings → Keypad tester. The server already owns the
# GPIO pins through the running Keypad scanner, so the test taps that live key
# stream instead of claiming the pins itself — which is why, unlike
# test_keypad.py, this needs no service stop.
#
# While a test is running the controller SWALLOWS key presses (see
# HardwareController._on_key_press): during a test, typing a PC number and '#'
# must not open the coin slot on a real PC.
#
# _keypad_test_expires_at is a dead-man switch. If the admin closes the tab
# mid-test the capture would otherwise stay on forever and the kiosk keypad
# would be dead; polling the status endpoint pushes the deadline out, so the
# test only stays alive while someone is actually watching it.

KEYPAD_ALL_KEYS = ['1', '2', '3', '4', '5', '6', '7', '8', '9', '*', '0', '#']

_KEYPAD_TEST_TTL_SECONDS = 180

_keypad_test_active: bool = False
_keypad_test_expires_at: float = 0.0
_keypad_test_detected: set[str] = set()
_keypad_test_events: list[dict] = []      # [{"key": str, "at": float}, ...]
_keypad_test_started_at: float = 0.0


def start_keypad_test() -> None:
    """Begin capturing key presses, clearing any previous run."""
    global _keypad_test_active, _keypad_test_expires_at, _keypad_test_started_at
    with _lock:
        _keypad_test_active = True
        _keypad_test_started_at = _time.monotonic()
        _keypad_test_expires_at = _keypad_test_started_at + _KEYPAD_TEST_TTL_SECONDS
        _keypad_test_detected.clear()
        _keypad_test_events.clear()


def stop_keypad_test() -> None:
    """End capture. Detected keys are kept so the summary can still be read."""
    global _keypad_test_active
    with _lock:
        _keypad_test_active = False


def is_keypad_test_active() -> bool:
    """True while a test is capturing. Self-expires once the TTL lapses so a
    closed browser tab can never leave the kiosk keypad swallowing presses."""
    global _keypad_test_active
    with _lock:
        if _keypad_test_active and _time.monotonic() > _keypad_test_expires_at:
            _keypad_test_active = False
        return _keypad_test_active


def record_keypad_test_key(key: str) -> None:
    """Record one key press. Called from the keypad scanner thread."""
    with _lock:
        if not _keypad_test_active:
            return
        _keypad_test_detected.add(key)
        _keypad_test_events.append({
            "key": key,
            "at": round(_time.monotonic() - _keypad_test_started_at, 3),
        })
        # Bound the log — a stuck key must not grow this without limit.
        if len(_keypad_test_events) > 200:
            del _keypad_test_events[:-200]


def get_keypad_test_state(extend: bool = False) -> dict:
    """Snapshot of the current test. `extend` renews the dead-man deadline,
    so only an actively-polling dashboard keeps the capture alive."""
    global _keypad_test_active, _keypad_test_expires_at
    with _lock:
        if _keypad_test_active and _time.monotonic() > _keypad_test_expires_at:
            _keypad_test_active = False
        if _keypad_test_active and extend:
            _keypad_test_expires_at = _time.monotonic() + _KEYPAD_TEST_TTL_SECONDS
        return {
            "active": _keypad_test_active,
            "detected": [k for k in KEYPAD_ALL_KEYS if k in _keypad_test_detected],
            "missing": [k for k in KEYPAD_ALL_KEYS if k not in _keypad_test_detected],
            "events": list(_keypad_test_events[-12:]),
            "total_keys": len(KEYPAD_ALL_KEYS),
        }
