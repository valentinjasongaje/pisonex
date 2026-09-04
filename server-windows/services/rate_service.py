from sqlalchemy.orm import Session as DBSession
from models import CoinRate, RateProfile, ServerConfig
from config import settings

_DEFAULT_PROFILE_ID = 1  # The seed always assigns id=1 to the Default profile

# Above this peso amount the exact solver would allocate a needlessly large
# table, so the bulk is filled with the best-value rate (which is always optimal
# once the amount is large enough to absorb any remainder) and only the tail is
# solved exactly. Nobody feeds ₱5,000 into a coin slot; this exists so an admin
# typo in the add-time box can't stall the server.
_EXACT_SOLVE_MAX_PESOS = 5000


def _leftover_mode(db: DBSession) -> str:
    """How to treat pesos that no combination of configured rates can consume.

    "prorate" — credit them at the smallest denomination's per-peso value.
    "discard" — credit nothing for them.

    Defaults to "prorate", which is the behaviour every install had before this
    was configurable, so upgrading changes nothing until an admin opts in.
    """
    cfg = db.query(ServerConfig).first()
    mode = getattr(cfg, "coin_leftover_mode", None) if cfg else None
    return mode if mode in ("prorate", "discard") else "prorate"


def pesos_to_seconds(amount_pesos: int, db: DBSession, profile_id: int = _DEFAULT_PROFILE_ID) -> int:
    """
    Converts a peso amount to seconds using the active CoinRate rows for the
    given profile.

    Lookup order:
      1. Active rates for `profile_id`.
      2. If none found, fall back to active rates for the Default profile.
      3. If still none, use the config.py hardcoded defaults.

    Picks the combination of rates worth the MOST seconds for the amount
    inserted, so the customer always gets the best deal their coins can buy.

    This used to spend the largest denomination first and assume bigger always
    meant better value. That silently shortchanged customers whenever it didn't:
    with a promo making ₱5 = 40 min against ₱10 = 65 min, ₱20 paid out 130 min
    when 4×₱5 was worth 160. Sorting by value-per-peso instead is not enough
    either — with ₱3 = 100 s and ₱2 = 60 s, ₱4 is best spent as 2×₱2 (120 s)
    even though ₱3 is the better per-peso rate. Only an exact solve gets every
    ladder right, and at coin-slot amounts it costs nothing.

    Example:  ₱10 = 3900s (65 min),  ₱5 = 1800s (30 min),  ₱1 = 300s (5 min)
    Inserting ₱11:  1x₱10 (3900s) + 1x₱1 (300s) = 4200 seconds (70 minutes)
    """
    if amount_pesos <= 0:
        return 0

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
        if pesos_per_block <= 0:
            return 0
        return (amount_pesos // pesos_per_block) * sec_per_block

    # Ignore nonsense rows rather than letting them divide by zero below.
    priced = [(r.pesos, r.seconds) for r in rates if r.pesos > 0 and r.seconds > 0]
    if not priced:
        return 0

    total_seconds = 0
    remaining = amount_pesos

    # Very large amounts: fill with the best value-per-peso rate until the tail
    # is small enough to solve exactly.
    best_pesos, best_seconds = max(priced, key=lambda pr: pr[1] / pr[0])
    if remaining > _EXACT_SOLVE_MAX_PESOS:
        bulk = remaining - _EXACT_SOLVE_MAX_PESOS
        units = bulk // best_pesos
        if units:
            total_seconds += units * best_seconds
            remaining -= units * best_pesos

    # Exact solve (unbounded knapsack): best[v] = most seconds obtainable from
    # coins summing to exactly v pesos, or -1 where v is unreachable.
    best = [-1] * (remaining + 1)
    best[0] = 0
    for value in range(1, remaining + 1):
        for pesos, seconds in priced:
            if pesos <= value and best[value - pesos] >= 0:
                candidate = best[value - pesos] + seconds
                if candidate > best[value]:
                    best[value] = candidate

    # Spend as much of the amount as the rates can consume. Ties go to the
    # larger spend so leftover — which may be worth less per peso — is minimised.
    spend = max(
        (v for v in range(remaining + 1) if best[v] >= 0),
        key=lambda v: (best[v], v),
    )
    total_seconds += best[spend]
    leftover = remaining - spend

    if leftover > 0 and _leftover_mode(db) == "prorate":
        smallest_pesos, smallest_seconds = min(priced, key=lambda pr: pr[0])
        total_seconds += int(leftover * (smallest_seconds / smallest_pesos))

    return total_seconds


def pesos_for_seconds(seconds: int, db: DBSession, profile_id: int = _DEFAULT_PROFILE_ID) -> int:
    """
    Estimates a peso amount for a given number of seconds — the reverse of
    pesos_to_seconds(). Used to log a CoinTransaction for manual minutes-based
    add-time in Traditional Café Mode, where there's no physical coin insert
    to derive an amount from.

    Uses the SMALLEST-denomination active rate for `profile_id` as the
    conversion ratio (ascending by pesos, via get_active_rates()). This is an
    approximation for logging/reporting purposes only — real coin-insert
    rates may apply volume bonuses at higher denominations that this doesn't
    replicate.

    Falls back to the Default profile, then config.py defaults, mirroring
    the exact fallback chain in pesos_to_seconds().
    """
    rates = get_active_rates(db, profile_id)

    if not rates and profile_id != _DEFAULT_PROFILE_ID:
        rates = get_active_rates(db, _DEFAULT_PROFILE_ID)

    if not rates:
        pesos_per_block = settings.DEFAULT_RATE_PESOS
        sec_per_block = settings.DEFAULT_RATE_SECONDS
        if sec_per_block <= 0:
            return 0
        return round(seconds * pesos_per_block / sec_per_block)

    smallest = rates[0]  # ascending by pesos — first is smallest denomination
    if smallest.seconds <= 0:
        return 0
    return round(seconds * smallest.pesos / smallest.seconds)


def get_active_rates(db: DBSession, profile_id: int = _DEFAULT_PROFILE_ID) -> list[CoinRate]:
    """Return active rates for a specific profile, ordered by pesos ascending."""
    return (
        db.query(CoinRate)
        .filter(CoinRate.is_active == True, CoinRate.profile_id == profile_id)
        .order_by(CoinRate.pesos.asc())
        .all()
    )


def find_worse_value_rates(rates: list[CoinRate]) -> list[dict]:
    """Flag denominations that are worse value per peso than a smaller one.

    The solver above now pays out the best combination regardless, so this is no
    longer a correctness problem — but it is almost always a pricing mistake the
    owner didn't intend, and it means the bigger coin is one customers learn to
    avoid. Surfaced as a warning on the Coin Rates page.

    Returns [{"pesos", "seconds", "beaten_by_pesos", "beaten_by_seconds"}, ...].
    """
    priced = sorted(
        [r for r in rates if r.pesos > 0 and r.seconds > 0],
        key=lambda r: r.pesos,
    )
    warnings: list[dict] = []
    for i, rate in enumerate(priced):
        rate_value = rate.seconds / rate.pesos
        for smaller in priced[:i]:
            if smaller.seconds / smaller.pesos > rate_value:
                warnings.append({
                    "pesos": rate.pesos,
                    "seconds": rate.seconds,
                    "beaten_by_pesos": smaller.pesos,
                    "beaten_by_seconds": smaller.seconds,
                })
                break
    return warnings


def get_all_profiles(db: DBSession) -> list[RateProfile]:
    """Return all rate profiles ordered by is_default desc, then name."""
    return (
        db.query(RateProfile)
        .order_by(RateProfile.is_default.desc(), RateProfile.name.asc())
        .all()
    )
