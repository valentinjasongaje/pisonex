# Member Loyalty Points

A points/rewards system for members: earn points by inserting coins while
logged in (plus a small bonus for logging in on consecutive days), redeem
points for bonus time from the client tray icon — self-service, no admin
involved.

**Scope of this pass: `server-orangepi/` (production) + the shared VB.NET
client only.** Off by default (`MembershipConfig.points_enabled = False`).
Porting to `server/` and `server-windows/` is a follow-up, once this is
proven — same reasoning as every other feature this session: no shared
`core/` layer yet, each server variant duplicates this logic.

---

## Mechanic

- **Earn (coins):** `points_per_10_pesos` points per ₱10 inserted while a
  member is logged in. Awarded the moment the coin is credited — same event
  that already creates a `CoinTransaction` row.
- **Earn (streak):** `points_streak_bonus` points for logging in on a day
  that immediately follows the previous login day (server-local date, not
  UTC — consistent with how `CoinSchedule`/`ScheduledAnnouncement` already
  use local time for day-of-week logic). Gap of more than 1 day resets the
  streak counter to 1; same-day repeat login awards nothing (no double-dip).
- **Redeem:** member spends `points_per_minute_redeem` points per minute of
  bonus time, self-service from the tray icon. If they have a live session
  right now, the time is added directly to it (immediate effect, matches how
  a coin insertion behaves); otherwise it's credited to `balance_seconds`
  (banked for next login, matches how a stored balance already works).
  Points below one minute's worth are never spent — e.g. redeeming 25 points
  at a 20-points/minute rate spends 20 and leaves 5.

## A required side-fix: hardware-inserted coins don't currently attribute `user_id`

`hardware/controller.py`'s `_process_coin()` calls
`SessionService.add_time_by_pesos(pc_number, pesos)` **without** `user_id` —
unlike `api/sessions.py`'s REST route, which already looks up
`command_store.get_member_for_pc()` and passes it through. This means a
physical coin inserted while a member is logged in currently creates an
**anonymous** `CoinTransaction` (and, for a member starting from zero-time,
an anonymous `Session` too — the very first coin from zero-time doesn't
actually get tied to their account). This has to be fixed as part of this
feature — there is no way to award coin points correctly otherwise — and
it's a legitimate data-quality fix on its own (member coin transactions
should already have been attributed).

---

## Data model

`models.py`:

```python
# User — add:
loyalty_points    = Column(Integer, default=0, nullable=False)
login_streak_days = Column(Integer, default=0, nullable=False)
last_login_date   = Column(String(10), nullable=True)   # "YYYY-MM-DD", local date

# MembershipConfig — add:
points_enabled          = Column(Boolean, default=False, nullable=False)
points_per_10_pesos      = Column(Integer, default=1, nullable=False)
points_streak_bonus      = Column(Integer, default=5, nullable=False)
points_per_minute_redeem = Column(Integer, default=2, nullable=False)

class PointsTransaction(Base):
    __tablename__ = "points_transactions"
    id              = Column(Integer, primary_key=True, index=True)
    user_id         = Column(Integer, ForeignKey("users.id"), nullable=False)
    pc_id           = Column(Integer, ForeignKey("pcs.id"), nullable=True)
    kind            = Column(String(20), nullable=False)  # "earn_coin"|"earn_streak"|"redeem"|"admin_adjust"
    points_delta    = Column(Integer, nullable=False)      # +/-
    seconds_redeemed = Column(Integer, nullable=True)      # only for "redeem" rows
    created_at      = Column(DateTime, default=datetime.utcnow, index=True)
```

`main.py` `_migrate_schema()`: guarded `ALTER TABLE` for the 3 new `users`
columns and 4 new `membership_config` columns, same `has_column()` pattern
as every other migration in this file. `points_transactions` needs **no**
migration step — it's a brand-new table, `Base.metadata.create_all()`
creates it on any existing DB automatically (confirmed: this is exactly how
`coin_schedules`/`scheduled_announcements` were added, no ALTER TABLE for
either).

---

## Server changes

- `services/membership_service.py`:
  - `award_coin_points(user_id, pc_id, pesos) -> int` — no-op if points
    disabled, `user_id` is None, or `pesos <= 0`.
  - `_award_login_streak(user, cfg) -> int` — called from inside
    `login_member()`, right before the PC-binding step, so it shares that
    method's existing commit.
  - `redeem_points(pc_number, points) -> dict` — identifies the member via
    `command_store.get_member_for_pc()` (same as `change_password`, no
    re-auth needed since the client is already authenticated for that PC).
- `hardware/controller.py` `_process_coin()`: look up
  `command_store.get_member_for_pc(pc_number)` and pass it as `user_id` to
  `add_time_by_pesos()`; call `award_coin_points()` right after.
- `api/member.py`: new `POST /api/member/redeem-points`.
- `schemas.py`: `MemberRedeemPointsRequest/Response`; add
  `loyalty_points`/`points_enabled`/`points_per_minute_redeem` to
  `MemberLoginResponse`, `MemberStatusResponse`, `MemberListResponse`; add
  the 4 new config fields to `MembershipConfigResponse`/`Update`; add
  `points_enabled`, `member_loyalty_points`, `points_per_minute_redeem` to
  `PCHeartbeatResponse` (mirrors how `member_balance_seconds` already works
  — live value in every heartbeat, no extra round trip needed client-side).
- `dashboard/routes.py` / `membership.html`: Points column + "Adjust
  Points" action (mirrors the existing "Adjust Balance"), config
  toggle/rate fields on the same form as `membership_enabled` etc.

## Client changes (`client/PisoNetClient/`)

- `ApiService.HeartbeatResponse`: + `points_enabled`, `member_loyalty_points`,
  `points_per_minute_redeem`.
- `SessionManager.MembershipUpdated` event: extended with the 3 fields above.
- `Forms/RedeemPointsForm.vb` — new, modeled directly on
  `ChangePasswordForm.vb`: shows current points + equivalent minutes at the
  server-supplied rate, a "Redeem" button, freely cancelable (never forced).
- `MemberService.vb`: `RedeemPointsAsync(pcNumber, points)` →
  `POST /api/member/redeem-points`.
- `SystemTray.vb`: new "Redeem Points..." menu item, visible only when
  `points_enabled` AND a member is currently logged in on this PC (mirrors
  the existing conditional visibility on the "Change Password" item).
- `Program.vb`: wires the new tray event to open `RedeemPointsForm`.

---

## Verification

- Dev server (server-orangepi, scratch DB): enable membership + points,
  create a member, log in with zero balance, insert simulated coins via
  `hardware/controller.py`'s test hooks — confirm `CoinTransaction.user_id`
  is now set and `loyalty_points` increases by the configured rate. Log out
  and back in on a fresh day (or manipulate `last_login_date`) to confirm
  the streak bonus fires once per day, not per login. Redeem points via the
  new endpoint — confirm it extends a live session directly when one exists,
  banks to `balance_seconds` otherwise, and never spends a partial minute's
  worth of points.
- `dotnet build` the client after the VB.NET changes; can't launch a full
  Windows GUI session in this sandbox, so the client build succeeding
  (obfuscation-exclusion attributes intact on the new DTOs, no missing
  references) is the practical verification ceiling here — flag this
  explicitly rather than claiming a UI test that didn't happen.
