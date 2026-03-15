"""
In-memory store for the latest PC performance metrics from each client.
Stored as a plain dict (JSON-decoded). Resets on server restart — no persistence needed.
"""
from datetime import datetime
from typing import Optional

# {pc_number: dict}
_metrics: dict[int, dict] = {}

# {pc_number: datetime}
_times: dict[int, datetime] = {}


def save(pc_number: int, data: dict) -> None:
    _metrics[pc_number] = data
    _times[pc_number] = datetime.utcnow()


def get(pc_number: int) -> Optional[dict]:
    return _metrics.get(pc_number)


def get_time(pc_number: int) -> Optional[datetime]:
    return _times.get(pc_number)


def get_all() -> dict[int, dict]:
    return dict(_metrics)
