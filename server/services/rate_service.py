from sqlalchemy.orm import Session as DBSession
from models import CoinRate, RateProfile
from config import settings

_DEFAULT_PROFILE_ID = 1  # The seed always assigns id=1 to the Default profile


def pesos_to_seconds(amount_pesos: int, db: DBSession, profile_id: int = _DEFAULT_PROFILE_ID) -> int:
    """
    Converts a peso amount to seconds using the active CoinRate rows for the
    given profile.

    Lookup order:
      1. Active rates for `profile_id`.
      2. If none found, fall back to active rates for the Default profile
         (profile_id = _DEFAULT_PROFILE_ID).
      3. If still none, use the config.py hardcoded defaults.

    Applies rates greedily from highest to lowest denomination so users
    automatically get the best rate for their coins.

    Example rates:  ₱10 = 3900s (65 min),  ₱5 = 1800s (30 min),  ₱1 = 300s (5 min)
    Inserting ₱11:  1x₱10 (3900s) + 1x₱1 (300s) = 4200 seconds (70 minutes)
    """
    def _query_rates(pid: int) -> list[CoinRate]:
        return (
            db.query(CoinRate)
            .filter(CoinRate.is_active == True, CoinRate.profile_id == pid)
            .order_by(CoinRate.pesos.desc())
            .all()
        )

    rates = _query_rates(profile_id)

    # Fall back to Default profile when the assigned profile has no rates
    if not rates and profile_id != _DEFAULT_PROFILE_ID:
        rates = _query_rates(_DEFAULT_PROFILE_ID)

    if not rates:
        # Last-resort fallback: config.py defaults
        pesos_per_block = settings.DEFAULT_RATE_PESOS
        sec_per_block = settings.DEFAULT_RATE_SECONDS
        return (amount_pesos // pesos_per_block) * sec_per_block

    total_seconds = 0
    remaining = amount_pesos

    for rate in rates:
        if remaining >= rate.pesos:
            multiplier = remaining // rate.pesos
            total_seconds += multiplier * rate.seconds
            remaining -= multiplier * rate.pesos

    # Any leftover pesos use the smallest denomination rate
    if remaining > 0:
        smallest = rates[-1]
        if smallest.pesos > 0:
            total_seconds += int(remaining * (smallest.seconds / smallest.pesos))

    return total_seconds


def get_active_rates(db: DBSession, profile_id: int = _DEFAULT_PROFILE_ID) -> list[CoinRate]:
    """Return active rates for a specific profile, ordered by pesos ascending."""
    return (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True, CoinRate.profile_id == profile_id)
        .order_by(CoinRate.pesos.asc())
        .all()
    )


def get_all_profiles(db: DBSession) -> list[RateProfile]:
    """Return all rate profiles ordered by is_default desc, then name."""
    return (
        db.query(RateProfile)
        .order_by(RateProfile.is_default.desc(), RateProfile.name.asc())
        .all()
    )
