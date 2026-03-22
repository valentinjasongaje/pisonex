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


def is_coins_allowed(pc_number: int) -> bool:
    """Return True only if BOTH global relay AND per-PC override allow coin acceptance."""
    with _lock:
        return _coin_slot_enabled and _pc_coin_enabled.get(pc_number, True)


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

_LOGIN_WINDOW_SECONDS = 60
_LOGIN_MAX_ATTEMPTS = 5


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
