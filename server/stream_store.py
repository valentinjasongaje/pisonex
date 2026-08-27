"""
In-memory store for active FFmpeg WebSocket streaming connections.

One publisher (PC client pushing MPEG1 via FFmpeg) per PC number;
multiple watchers (admin browsers) per PC number.

All access is from async FastAPI handlers in the same event loop, so
no locks are needed — asyncio is single-threaded.
"""
from fastapi import WebSocket

# {pc_number: WebSocket} — the PC client currently streaming
_publishers: dict[int, WebSocket] = {}

# {pc_number: list[WebSocket]} — browser(s) watching the stream
_watchers: dict[int, list[WebSocket]] = {}


def set_publisher(pc_number: int, ws: WebSocket) -> None:
    _publishers[pc_number] = ws
    _watchers.setdefault(pc_number, [])


def clear_publisher(pc_number: int) -> None:
    _publishers.pop(pc_number, None)


def is_publishing(pc_number: int) -> bool:
    return pc_number in _publishers


def add_watcher(pc_number: int, ws: WebSocket) -> None:
    _watchers.setdefault(pc_number, []).append(ws)


def remove_watcher(pc_number: int, ws: WebSocket) -> None:
    lst = _watchers.get(pc_number, [])
    try:
        lst.remove(ws)
    except ValueError:
        pass


def has_watchers(pc_number: int) -> bool:
    return bool(_watchers.get(pc_number))


async def broadcast(pc_number: int, data: bytes) -> None:
    """Push a chunk to every browser watching this PC. Prune dead connections."""
    watchers = list(_watchers.get(pc_number, []))
    if not watchers:
        return
    dead = []
    for ws in watchers:
        try:
            await ws.send_bytes(data)
        except Exception:
            dead.append(ws)
    lst = _watchers.get(pc_number, [])
    for ws in dead:
        try:
            lst.remove(ws)
        except ValueError:
            pass


async def close_all_watchers(pc_number: int) -> None:
    """Close every watching browser connection (e.g. publisher disconnected)."""
    watchers = list(_watchers.get(pc_number, []))
    _watchers[pc_number] = []
    for ws in watchers:
        try:
            await ws.close(code=1000)
        except Exception:
            pass
