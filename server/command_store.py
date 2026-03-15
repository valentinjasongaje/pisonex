"""
In-memory store for admin-to-client commands, messages, announcements,
and coin slot control flags.

All state resets to safe defaults on server restart:
  - No pending commands or messages
  - No active announcement
  - Coin slot globally enabled (coins accepted)
  - All per-PC coin slot overrides cleared (coins accepted)
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
