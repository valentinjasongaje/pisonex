from sqlalchemy.orm import Session as DBSession
from models import CoinRate
from config import settings


def pesos_to_minutes(amount_pesos: int, db: DBSession) -> int:
    """
    Converts a peso amount to minutes using active coin rates.

    Applies rates greedily from highest to lowest denomination,
    so users automatically get the best rate for their coins.

    Example rates:  ₱10 = 65 min,  ₱5 = 30 min,  ₱1 = 5 min
    Inserting ₱11:  1x₱10 (65min) + 1x₱1 (5min) = 70 minutes
    """
    rates = (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True)
        .order_by(CoinRate.pesos.desc())
        .all()
    )

    if not rates:
        # Fallback to default rate from config if no rates in DB
        pesos_per_block = settings.DEFAULT_RATE_PESOS
        min_per_block = settings.DEFAULT_RATE_MINUTES
        return (amount_pesos // pesos_per_block) * min_per_block

    total_minutes = 0
    remaining = amount_pesos

    for rate in rates:
        if remaining >= rate.pesos:
            multiplier = remaining // rate.pesos
            total_minutes += multiplier * rate.minutes
            remaining -= multiplier * rate.pesos

    # Any leftover pesos use the smallest denomination rate
    if remaining > 0:
        smallest = rates[-1]
        if smallest.pesos > 0:
            total_minutes += int(remaining * (smallest.minutes / smallest.pesos))

    return total_minutes


def get_active_rates(db: DBSession) -> list[CoinRate]:
    return (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True)
        .order_by(CoinRate.pesos.asc())
        .all()
    )
