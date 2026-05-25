"""
In-memory store for the latest screenshot from each PC client.
Stored as raw JPEG bytes. Resets on server restart — no persistence needed.
"""
from datetime import datetime

# {pc_number: bytes}
_screenshots: dict[int, bytes] = {}

# {pc_number: datetime}
_times: dict[int, datetime] = {}


def save(pc_number: int, jpeg_bytes: bytes) -> None:
    _screenshots[pc_number] = jpeg_bytes
    _times[pc_number] = datetime.utcnow()


def get(pc_number: int) -> bytes | None:
    return _screenshots.get(pc_number)


def get_time(pc_number: int) -> datetime | None:
    return _times.get(pc_number)
