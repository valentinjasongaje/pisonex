# Reward Menu (Catalog-Based Redemption)

Replaces the flat "points per minute" redemption from
`docs/member-loyalty-points.md` with an admin-defined catalog a member
browses and picks from — bonus time is just one item in it now, alongside
food/drink items an admin adds. Food items can't be auto-dispensed by
software, so redeeming one doesn't hand anything over immediately — it
queues a claim that staff fulfills at the counter, the same way a
piso-net attendant already handles any in-person request.

**Scope: both `server-orangepi/` and `server/` in the same pass** (per
explicit instruction — unlike every earlier feature this session, which
built on `server-orangepi/` first and ported afterward). Practically this
still means: implement and verify fully on `server-orangepi/`, then port
the verified diff to `server/` immediately in the same work session,
confirming byte-for-byte parity before moving on — the fastest reliable
way to keep both variants honestly identical.

---

## What changes vs. the existing points system

- `MembershipService.redeem_points(pc_number, points)` and
  `POST /api/member/redeem-points` are **replaced** by
  `redeem_reward(pc_number, reward_item_id)` /
  `POST /api/member/redeem-reward`. The client is being reworked in the
  same pass (task below), so there's no deployed client depending on the
  old shape to keep compatible.
- `MembershipConfig.points_per_minute_redeem` **stays in the schema**
  (this codebase never drops columns — SQLite `ALTER TABLE` migrations are
  additive-only throughout `_migrate_schema()`) but is **no longer shown
  in the Settings UI** or read by the new redemption path. It's inert,
  not deleted.
- Everything else from the points doc is unchanged: earning (coins +
  streak), `loyalty_points` balance, `PointsTransaction` audit log for
  earn events. `PointsTransaction` rows are NOT created for redemptions
  anymore — `RewardRedemption` (below) is the new, richer record of every
  redemption, replacing the `"redeem"`-kind `PointsTransaction` rows the
  old flow wrote.

## Data model

```python
class RewardItem(Base):
    """Admin-defined catalog entry a member can redeem points for."""
    __tablename__ = "reward_items"

    id           = Column(Integer, primary_key=True, index=True)
    name         = Column(String(120), nullable=False)     # "30 Minutes Bonus", "Chips"
    kind         = Column(String(10), nullable=False)      # "time" | "food"
    points_cost  = Column(Integer, nullable=False)
    minutes      = Column(Integer, nullable=True)           # only meaningful for kind="time"
    is_active    = Column(Boolean, default=True, nullable=False)
    created_at   = Column(DateTime, default=datetime.utcnow)


class RewardRedemption(Base):
    """One member's claim against the catalog. Snapshots the item's name/kind/
    cost at redemption time so editing or deleting a catalog item later never
    corrupts history — this is a receipt, not a live join.
    """
    __tablename__ = "reward_redemptions"

    id               = Column(Integer, primary_key=True, index=True)
    user_id          = Column(Integer, ForeignKey("users.id"), nullable=False)
    pc_id            = Column(Integer, ForeignKey("pcs.id"), nullable=True)
    reward_item_id   = Column(Integer, ForeignKey("reward_items.id"), nullable=True)  # nullable: item may be deleted later
    item_name        = Column(String(120), nullable=False)
    kind             = Column(String(10), nullable=False)
    points_spent     = Column(Integer, nullable=False)
    minutes_granted  = Column(Integer, nullable=True)       # set only for "time" kind
    status           = Column(String(10), nullable=False, default="pending")  # "pending" | "fulfilled"
    created_at       = Column(DateTime, default=datetime.utcnow, index=True)
    fulfilled_at     = Column(DateTime, nullable=True)
```

Both are brand-new tables — no migration step needed, `Base.metadata.create_all()`
creates them on any existing DB (same reasoning as `CoinSchedule` and
`PointsTransaction` before them).

## Redemption rules

- **`kind="time"`**: fulfills itself immediately — same live-session-vs-balance
  split the old `redeem_points` used (extend an active session directly if
  the member has one right now, otherwise bank to `balance_seconds`).
  `RewardRedemption.status` is created as `"fulfilled"` with `fulfilled_at`
  set right away.
- **`kind="food"`**: points are deducted immediately (so they can't be spent
  twice while waiting), but the row is created `"pending"`. It shows up on
  the new **Rewards** dashboard page's "Pending Redemptions" queue for
  staff to mark `"fulfilled"` once they've handed the item over.
- No partial-cost logic like the old flat rate had (spend-only-whole-minutes) —
  a catalog item has one fixed cost. If a member can't afford it, the item
  is just unavailable to pick.
- Only `points_enabled` gates all of this, same flag as before, still off
  by default.

## Server changes (both variants)

- `models.py`: `RewardItem`, `RewardRedemption`.
- `services/membership_service.py`:
  - `list_active_rewards() -> list[RewardItem]`
  - `redeem_reward(pc_number, reward_item_id) -> dict`
  - `admin_create_reward(name, kind, points_cost, minutes=None) -> dict`
  - `admin_toggle_reward(reward_item_id) -> dict`
  - `admin_delete_reward(reward_item_id) -> dict`
  - `list_pending_redemptions() -> list[RewardRedemption]`
  - `fulfill_redemption(redemption_id) -> dict`
  - **Remove** `redeem_points()` (superseded by `redeem_reward()`).
- `schemas.py`: `RewardItemResponse`, `MemberRedeemRewardRequest/Response`
  (replacing `MemberRedeemPointsRequest/Response`).
- `api/member.py`: `GET /api/member/rewards` (client-key gated, returns
  active catalog items), `POST /api/member/redeem-reward` (replaces
  `/redeem-points`).
- `dashboard/routes.py` + new `dashboard/templates/rewards.html` + a
  `/dashboard/rewards` nav link (placed next to Membership, same
  admin-only gating): catalog CRUD card + Pending Redemptions queue card
  with a "Mark Fulfilled" action per row, polling every 10s so a fresh
  claim shows up without a manual refresh (same pattern as the Overview
  page's Server Health widget).
- `dashboard/templates/membership.html`: remove the "Redemption Rate"
  field from the Loyalty Points config section — no longer meaningful
  now that each item has its own cost. Everything else on that page
  (points toggle, points-per-10-pesos, streak bonus, Points column,
  Adjust Points) is unchanged.

## Client changes (`client/PisoNetClient/`, shared)

- `ApiService`/`MemberService`: new `RewardItem` DTO, `GetRewardsAsync()`,
  `RedeemRewardAsync(pcNumber, rewardItemId)`, replacing
  `RedeemPointsAsync`.
- `Forms/RedeemPointsForm.vb` reworked from a single point-amount input
  into a **browsable list**: fetches the catalog on open, renders each
  item as a row (name, cost, and — for time items — "+N min"), grayed out
  if the member can't afford it. Selecting an affordable item redeems it
  immediately (with a confirm step) and shows the result inline —
  "+30 minutes added!" for time, "Show this to staff to collect: Chips"
  for food — then refreshes the displayed balance and re-renders the list
  so a member with enough points can claim more than one thing in a
  single visit, without reopening the dialog.
- Tray wiring (`SystemTray`'s "Redeem Points..." item, `Program.vb`'s
  handler) is unchanged — same trigger, the dialog's contents just
  changed from a text box to a menu.

## Verification

- server-orangepi dev server: create a time item and a food item via the
  new catalog routes, confirm both list correctly for the client
  (`GET /api/member/rewards` only returns active ones), redeem the time
  item — confirm points deducted, minutes credited immediately, row is
  `"fulfilled"`. Redeem the food item — confirm points deducted, row is
  `"pending"`, then fulfill it via the admin route and confirm it flips
  to `"fulfilled"` with a timestamp. Confirm redeeming with insufficient
  points is rejected without touching the balance. Confirm the Rewards
  page renders both cards and the Membership page no longer shows a
  Redemption Rate field.
- Port to `server/`, diff-confirm byte-identical model/service/route
  sections against `server-orangepi/`, repeat the same verification
  sequence.
- Client: same ceiling as the points feature itself — no .NET SDK in
  this sandbox, so verification is a careful manual read-through against
  existing VB.NET patterns (`ChangePasswordForm`'s dialog structure,
  `MemberLoginForm`'s result-property pattern), not an actual
  `dotnet build`. Flag this explicitly rather than claiming a build that
  didn't happen.
