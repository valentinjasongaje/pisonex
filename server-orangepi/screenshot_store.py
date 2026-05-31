"""
In-memory store for the latest screenshot from each PC client.
Stored as raw JPEG bytes. Resets on server restart — no persistence needed.
"""
import asyncio
from datetime import datetime

# {pc_number: bytes}
_screenshots: dict[int, bytes] = {}

# {pc_number: datetime}
_times: dict[int, datetime] = {}

# {pc_number: asyncio.Event} — signaled on every new frame for MJPEG streams
_events: dict[int, asyncio.Event] = {}


def save(pc_number: int, jpeg_bytes: bytes) -> None:
    _screenshots[pc_number] = jpeg_bytes
    _times[pc_number] = datetime.utcnow()
    # Wake any MJPEG stream generators waiting on this PC
    if pc_number in _events:
        _events[pc_number].set()


def get(pc_number: int) -> bytes | None:
    return _screenshots.get(pc_number)


def get_time(pc_number: int) -> datetime | None:
    return _times.get(pc_number)


def get_event(pc_number: int) -> asyncio.Event:
    """Return (creating if needed) the asyncio.Event for this PC's MJPEG stream."""
    if pc_number not in _events:
        _events[pc_number] = asyncio.Event()
    return _events[pc_number]
