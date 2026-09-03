"""Timezone helpers for reporting.

Every timestamp in the database is stored as **naive UTC** — that does not
change, and there is no data migration. What changes is where a *day* starts
when we add money up.

Reports used to bucket by UTC midnight. In the Philippines (UTC+8) that is
8:00 AM local, so a café's "today" ran 8 AM → 8 AM: everything taken between
midnight and 8 AM was booked to the previous day, and the owner checking the
counter over morning coffee was still watching yesterday's total climb. The
schedule engine already used local time (see _run_schedule_tick in main.py),
so the two halves of the system disagreed about what day it was.

Set TIMEZONE in .env to the café's zone (default Asia/Manila).
"""

import logging
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from config import settings

logger = logging.getLogger(__name__)

_FALLBACK_TZ = "Asia/Manila"
_tz_cache: dict[str, ZoneInfo] = {}


def get_tz() -> ZoneInfo:
    """The café's configured timezone, falling back to Asia/Manila if the
    configured name is not installed on this machine (a bad .env value must not
    stop the server from booting)."""
    name = (settings.TIMEZONE or _FALLBACK_TZ).strip()
    if name in _tz_cache:
        return _tz_cache[name]
    try:
        tz = ZoneInfo(name)
    except (ZoneInfoNotFoundError, ValueError, KeyError):
        logger.warning(
            "TIMEZONE=%r is not a known timezone — falling back to %s. "
            "Use an IANA name such as Asia/Manila.", name, _FALLBACK_TZ
        )
        tz = ZoneInfo(_FALLBACK_TZ)
    _tz_cache[name] = tz
    return tz


def utc_now() -> datetime:
    """Naive UTC 'now' — the value every DateTime column in this app stores.

    Equivalent to the deprecated datetime.utcnow(), without the
    DeprecationWarning it emits on Python 3.12+.
    """
    return datetime.now(timezone.utc).replace(tzinfo=None)


def to_local(dt: datetime | None) -> datetime | None:
    """Convert a naive-UTC timestamp from the database to café-local wall time."""
    if dt is None:
        return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(get_tz())


def local_now() -> datetime:
    """Café-local wall clock, timezone-aware."""
    return datetime.now(get_tz())


def local_date_str(dt: datetime | None = None) -> str:
    """Café-local calendar date as 'YYYY-MM-DD'."""
    local = to_local(dt) if dt is not None else local_now()
    return local.strftime("%Y-%m-%d")


def local_day_start_utc(days_ago: int = 0) -> datetime:
    """The naive-UTC instant at which a café-local day begins.

    days_ago=0 is local midnight today, 1 is local midnight yesterday, and so on.
    Use this for `WHERE created_at >= ...` so a filter lines up with the day the
    owner actually experienced.

    Built by taking the local date, pinning it to 00:00 local, then converting
    back to UTC — so zones with DST get the right instant on transition days.
    """
    local_midnight = local_now().replace(
        hour=0, minute=0, second=0, microsecond=0
    ) - timedelta(days=days_ago)
    return local_midnight.astimezone(timezone.utc).replace(tzinfo=None)


def local_week_start_utc() -> datetime:
    """Naive-UTC instant of local midnight on the current week's Monday."""
    return local_day_start_utc(local_now().weekday())


def local_month_start_utc() -> datetime:
    """Naive-UTC instant of local midnight on the 1st of the current month."""
    return local_day_start_utc(local_now().day - 1)


def seconds_until_next_local_midnight() -> int:
    """How long until the café's day rolls over — for scheduling the nightly
    earnings archive so it captures a full local business day."""
    now = local_now()
    tomorrow = (now + timedelta(days=1)).replace(
        hour=0, minute=0, second=0, microsecond=0
    )
    return max(1, int((tomorrow - now).total_seconds()))
