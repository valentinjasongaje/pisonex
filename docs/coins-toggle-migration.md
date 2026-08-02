# Traditional Café Mode Migration (formerly "Coins Toggle")

> **Unlike the other migration docs in this folder, this feature is likely
> NOT worth porting to `server/` or `server-orangepi/`.** Both of those
> variants exist specifically because they have real physical GPIO
> coin-slot hardware wired in — that's the whole point of choosing an
> RPi/Orange Pi box for a piso-net setup. A shop running one of those
> already has a coin acceptor; there's no real scenario where they'd want
> to hide Insert-Coin UI and run "traditional cashier-only" mode instead,
> unlike `server-windows`, which has no hardware at all and might
> genuinely be used either way (some `server-windows` installs are
> pure cashier-managed cafés with zero coin hardware).
>
> Keep this doc as a **historical reference** of what was built and why,
> not an action item, unless a concrete use case for disabling coins on
> RPi/Orange Pi hardware comes up later.

---

A "Traditional Café Mode" on/off switch has been added to `server-windows/` +
`client/PisoNetClient/`. When turned **on**, the install runs as a
traditional, cashier-run internet café with no coin acceptor hardware — the
client hides all Insert Coin / "+ Add Time" coin-flow UI, and the dashboard
hides coin-slot/relay indicators. Manual add-time / adjust-balance from the
dashboard is **unaffected** either way — that is the entire point of the
toggle. Default is **OFF** (`traditional_mode_enabled = False`) so existing
installs (normal piso-net mode, Insert Coin visible) are unaffected until an
admin explicitly opts in.

**Field name history:** this was originally built and named `coins_enabled`
(bool, default `True`, meaning "coins on" = normal mode). It was renamed and
its polarity inverted to `traditional_mode_enabled` (bool, default `False`,
meaning "traditional/cashier mode" = coins hidden) before anything shipped,
to avoid a confusing double-negative everywhere the "Traditional Café Mode"
label is shown. If you find any stray reference to `coins_enabled` in old
notes/branches, it is the same feature under the old name/polarity.

The client (`client/PisoNetClient/`) is shared by all three server variants —
already done, no further client work needed if this is ever ported, once
`server/` and `server-orangepi/` populate `traditional_mode_enabled` on the
heartbeat response using the same field name and default described below.

---

## What changed, end to end (server-windows + client)

- Added `ServerConfig.traditional_mode_enabled` (bool, default `False`) —
  persistent, admin-set business-model toggle, distinct from the existing
  `command_store` coin-slot pause/resume flag (`is_coin_slot_enabled()` /
  `coin_slot_enabled` on the heartbeat, which is a transient runtime toggle,
  not a business-model one).
- Added `traditional_mode_enabled: bool = False` to `PCHeartbeatResponse`
  (`schemas.py`) and to the client's `HeartbeatResponse` DTO
  (`Services/ApiService.vb`).
- Added `POST /dashboard/api/settings/traditional-mode` (admin-only,
  session-gated) to flip it — applies immediately, no restart needed.
- Added a toggle to the dashboard **Settings → General** card, labeled
  "Traditional Café Mode".
- Client: `SessionManager` fires a `TraditionalModeChanged` event (mirrors
  the existing `CoinSlotChanged` event, but for the persistent flag);
  `Program.vb` forwards it to `LockForm.UpdateTraditionalMode()` (hides
  "Insert Coin") and `TimerOverlay.SetTraditionalMode()` (hides "+ Add Time"
  CTA + the receiving-coins mini card).
- Dashboard: the per-PC "Coins" column (`pcs.html`), the global "🪙 Coins: ON"
  button (`overview.html`), and the "Waiting for coins" PC-tile status text
  (`partials/pc_card.html`) are now all gated on `not traditional_mode_enabled`.
- Server-side defense in depth: `POST /api/pc/{n}/request-coins` now returns
  403 when `traditional_mode_enabled` is True, even if a stale client UI
  still shows the button. `POST /api/pc/{n}/done-coins` (the closing action)
  is intentionally **not** guarded — never block a "stop/close" action.
- **Manual add-time transaction logging** (see "Part B" section below) —
  `SessionService.add_time_seconds()` now logs a `CoinTransaction` with a
  reverse-calculated estimated peso amount whenever
  `traditional_mode_enabled` is True, so Reports/earnings totals stay
  meaningful in cafés that never touch coins. This is a runtime behavior
  change, not just naming — if this feature is ever ported, port this too.

---

## Checklist — apply to `server/` and `server-orangepi/` (each independently)

**Read the framing note at the top of this doc first.** These steps are
preserved for reference / in case a concrete need arises, but the default
expectation is that this migration is not applicable to RPi/Orange Pi
deployments.

### 1. `models.py` — add column to `ServerConfig`

Reference: `server-windows/models.py` (class `ServerConfig`).
`server/models.py` and `server-orangepi/models.py` have the equivalent
class definitions — add the column there. Note these variants'
`ServerConfig` already has hardware columns (`coin_pin`, `relay_pin`,
`coin_edge`, `coin_debounce_ms`, `coin_pulse_timeout`, plus
`ffmpeg_streaming_enabled` on server-orangepi) — add
`traditional_mode_enabled` alongside those, not instead of them.

```python
# True = "Traditional Café Mode" — no coin acceptor hardware is used or
# implied. Hides all coin-slot/Insert-Coin UI on the client and dashboard;
# manual add-time/adjust-balance from the dashboard keeps working either way.
# Default False so existing installs (normal piso-net mode, Insert Coin
# visible) are unaffected until an admin explicitly opts in.
traditional_mode_enabled = Column(Boolean, nullable=False, default=False)
```

### 2. `main.py` — migration guard in `_migrate_schema()`

**Important:** unlike `server-windows`, these two variants already have a
`server_config` hardware-columns migration block guarded by
`table_exists("server_config")`. Add `traditional_mode_enabled` to the
**existing** `new_server_columns` list in that block rather than writing a
second standalone guard:

```python
new_server_columns = [
    ("coin_pin", "INTEGER"),
    ("relay_pin", "INTEGER"),
    ("coin_edge", "VARCHAR(10)"),
    ("coin_debounce_ms", "INTEGER"),
    ("coin_pulse_timeout", "VARCHAR(16)"),
    # server-orangepi only: ("ffmpeg_streaming_enabled", "BOOLEAN DEFAULT 1"),
    ("traditional_mode_enabled", "INTEGER NOT NULL DEFAULT 0"),   # ← add this line
]
```

- [ ] Confirm the default row created by `_seed_defaults()` in each variant's
  `main.py` (search for `ServerConfig(id=1`) doesn't need an explicit
  `traditional_mode_enabled=` kwarg — the model's `default=False` covers
  fresh inserts.

### 3. `schemas.py` — add `traditional_mode_enabled` to the heartbeat response

Reference: `server-windows/schemas.py` — added right after the existing
`coin_slot_enabled: bool = True` field. Same insertion point in
`server/schemas.py` and `server-orangepi/schemas.py`:

```python
coin_slot_enabled: bool = True           # combined global + per-PC coin slot state
# Business-model toggle (persistent, admin-set via Settings). True = this
# install runs as a traditional cashier-run cafe with no coin hardware —
# the client hides all Insert Coin / Add Time coin-flow UI. Distinct from
# coin_slot_enabled above, which is a transient runtime pause/resume flag.
traditional_mode_enabled: bool = False
```

### 4. `api/pc.py` — populate the field in `heartbeat()`, guard `request-coins`

Reference: `server-windows/api/pc.py` heartbeat handler (query `ServerConfig`
right before building the response) and the `request_coins` route.

- [ ] In the `heartbeat()` handler, query `ServerConfig` and pass
  `traditional_mode_enabled=srv_cfg.traditional_mode_enabled if srv_cfg else False`
  into the `PCHeartbeatResponse(...)` constructor, alongside the existing
  `coin_slot_enabled=coins_ok` line.
- [ ] In `request_coins(pc_number: int, ...)`, add a `db: Session = Depends(get_db)`
  param (these variants' `request_coins` already takes other params — check
  the current signature) and, as the **first** check before touching
  `hw_controller`, reject with 403 when `traditional_mode_enabled` is True:

  ```python
  srv_cfg = db.query(ServerConfig).first()
  if srv_cfg and srv_cfg.traditional_mode_enabled:
      raise HTTPException(
          status_code=403,
          detail="Coins are disabled on this server. This café uses cashier-managed time only.",
      )
  ```
- [ ] Do **not** add the same guard to `done_coins` (the closing action) —
  intentionally left unguarded so an in-flight coin-insertion session can
  always be closed cleanly even if an admin flips the toggle mid-transaction.
- [ ] Add `ServerConfig` to the `from models import ...` line in `api/pc.py` if
  not already imported.

### 5. `dashboard/routes.py` — helper, page contexts, save endpoint

Reference: `server-windows/dashboard/routes.py` — `_get_traditional_mode_enabled(db)`
helper, and its use in `overview()`, `pc_grid_partial()`, `pcs_page()`,
`settings_page()`.

- [ ] Add:
  ```python
  def _get_traditional_mode_enabled(db: Session) -> bool:
      srv_cfg = db.query(ServerConfig).first()
      return srv_cfg.traditional_mode_enabled if srv_cfg else False
  ```
- [ ] Pass `"traditional_mode_enabled": _get_traditional_mode_enabled(db)`
  into the template context of: `overview()`, `pc_grid_partial()`,
  `pcs_page()`, and `settings_page()` — same four routes as server-windows.
- [ ] Add the save endpoint (admin-only, mirrors `save_branch_name`):
  ```python
  class TraditionalModeBody(BaseModel):
      enabled: bool

  @router.post("/api/settings/traditional-mode")
  def save_traditional_mode(
      body: TraditionalModeBody,
      db: Session = Depends(get_db),
      current_user: Optional[dict] = Depends(_validate_session),
  ):
      if not current_user:
          raise HTTPException(status_code=401, detail="Not authenticated")
      if current_user["role"] != "admin":
          raise HTTPException(status_code=403, detail="Admin access required")
      srv_cfg = db.query(ServerConfig).first()
      if srv_cfg:
          srv_cfg.traditional_mode_enabled = body.enabled
      db.commit()
      return {"status": "ok", "traditional_mode_enabled": body.enabled}
  ```

### 6. `dashboard/templates/settings.html` — toggle in General card

Reference: `server-windows/dashboard/templates/settings.html` General card —
copy the `.toggle-label` checkbox block + `saveTraditionalMode()` JS function
verbatim. Label: **"Traditional Café Mode"**. Description: "Hide coin-slot
features and manage all PC time manually from the dashboard. Turn this on
if this location has no coin acceptor and a cashier handles all sessions."
Checkbox checked state maps directly to `traditional_mode_enabled` (no
inversion needed in the JS — the checkbox represents the new field
directly).

- [ ] **Do not remove or hide the existing "Coin Slot Hardware" card** on
  these two variants — the requirement is to hide the *client-facing*
  Insert Coin UI and dashboard coin-slot/relay *status* indicators, not the
  GPIO configuration itself.

### 7. `dashboard/templates/pcs.html`, `overview.html`, `partials/pc_card.html`

- [ ] Wrap the per-PC "Coins" `<th>`/`<td>` toggle button (desktop table)
  and the mobile tile's coin toggle button in
  `{% if not traditional_mode_enabled %}`.
- [ ] Wrap the global `#global-coin-btn` in `overview.html` in
  `{% if not traditional_mode_enabled %}`.
- [ ] Change the locked-PC status sub-text in `partials/pc_card.html` to
  `{% if traditional_mode_enabled %}Ask cashier for time{% else %}Waiting for coins{% endif %}`.
- [ ] These variants' `pcs.html`/`overview.html` may have **additional**
  coin-slot-hardware-specific UI server-windows doesn't — grep each
  template for `coin` and `relay` first and judge case by case whether it's
  a live status/control (hide) or reference documentation (leave alone).

### 8. `services/rate_service.py` — reverse peso calculation helper

Reference: `server-windows/services/rate_service.py::pesos_for_seconds()`.
Add the equivalent function to each variant's `rate_service.py` — it queries
`get_active_rates()` for the smallest-denomination active rate and
reverse-calculates an estimated peso amount from a seconds value:

```python
def pesos_for_seconds(seconds: int, db: DBSession, profile_id: int = _DEFAULT_PROFILE_ID) -> int:
    rates = get_active_rates(db, profile_id)
    if not rates and profile_id != _DEFAULT_PROFILE_ID:
        rates = get_active_rates(db, _DEFAULT_PROFILE_ID)
    if not rates:
        pesos_per_block = settings.DEFAULT_RATE_PESOS
        sec_per_block = settings.DEFAULT_RATE_SECONDS
        if sec_per_block <= 0:
            return 0
        return round(seconds * pesos_per_block / sec_per_block)
    smallest = rates[0]  # ascending by pesos
    if smallest.seconds <= 0:
        return 0
    return round(seconds * smallest.pesos / smallest.seconds)
```

### 9. `services/session_service.py` — log manual add-time transactions

Reference: `server-windows/services/session_service.py::add_time_seconds()`.

- [ ] Query `ServerConfig.traditional_mode_enabled` inside `add_time_seconds()`.
- [ ] If True: reverse-calculate `pesos_for_seconds(seconds, db, profile_id=pc.rate_profile_id or 1)`
  and create a `CoinTransaction(pc_id=pc.id, user_id=user_id, amount_php=<computed>, seconds_added=seconds)`,
  log it distinctly (e.g. `f"₱{pesos} (est.) → {seconds}s manually added to PC {pc_number:02d} (Traditional Café Mode)"`)
  so it's distinguishable from real coin-pesos adds in `SystemLog`.
- [ ] If False: keep existing behavior exactly — no transaction created.
- [ ] Do **not** change `add_time_by_pesos()` — it already logs
  unconditionally regardless of traditional mode, and should keep doing so.

This matters most in Traditional Café Mode since that's the only way time
ever gets added there (no physical coin inserts to fall back on for
earnings data) — without it, Reports/Transactions/the nightly earnings
sync to pisonex.com would show zero income for an entire café that's
actually earning money via cashier-managed sessions.

### 10. Client — no changes needed

`client/PisoNetClient/` is shared by all three server variants. Once steps
1-9 are applied and the heartbeat response includes
`traditional_mode_enabled` with the same name/default, the existing client
wiring (`SessionManager.TraditionalModeChanged` →
`Program.OnTraditionalModeChanged` → `LockForm.UpdateTraditionalMode` /
`TimerOverlay.SetTraditionalMode`) picks it up automatically.

### 11. Verify

```bash
grep -rn "traditional_mode_enabled" server/ server-orangepi/
```

Should show the new column, migration guard, schema field, heartbeat
population, the `request-coins` guard, the dashboard helper/contexts/endpoint,
the three template changes, the `pesos_for_seconds` helper, and the
`add_time_seconds` transaction-logging change — nothing more. Run
`python -m py_compile` on every touched file in each variant before
considering it done.

---

## Not part of this migration (already correct, no action needed)

- Client (`client/PisoNetClient/`) — see step 10 above.
- Coin **rate** configuration (peso-to-time pricing) stays visible on all
  variants regardless of `traditional_mode_enabled` — if an admin later
  flips coins back on, those rates still matter. Only the live
  Insert-Coin/relay-status UI is hidden, not the pricing config.
- `POST /api/pc/{n}/done-coins` — deliberately left unguarded (see step 4).
- `add_time_by_pesos()` — always logs a `CoinTransaction`, regardless of
  `traditional_mode_enabled`; not touched by this feature.
