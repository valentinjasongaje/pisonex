# Rate Profiles Migration

"Rate Profiles" lets an admin create named pricing tiers (e.g. "VIP",
"Standard"), each with its own peso→time coin rates and a color badge, then
assign a specific profile to individual PCs so certain PCs get better/worse
rates than the shop default. This has been ported to `server-windows/`
(reference implementation — see file:line refs below) from `server-orangepi/`
(original implementation, production). **`server/` (Raspberry Pi) is the only
remaining variant without it.**

**This doc is a checklist**, not prose to read once. There is no shared
`core/` layer yet — each server variant duplicates this logic — so `server/`
needs the same feature applied independently. Do NOT copy files wholesale;
`server/`'s surrounding code differs from server-windows in one relevant way:
`server/` has real coin-slot GPIO hardware and a `ServerConfig` with hardware
columns (`coin_pin`, `relay_pin`, `coin_edge`, `coin_debounce_ms`,
`coin_pulse_timeout`) plus a `table_exists()` migration helper already used to
guard those columns — server-windows has neither. `server/` does **not** have
the `coins_enabled` toggle or `ffmpeg_streaming_enabled` column that
server-windows has (those are unrelated, separately-migrated features) — don't
add them as part of this change.

The client (`client/PisoNetClient/`) needs **no changes at all** — confirmed
via `grep -rn profile client/PisoNetClient/` (zero matches). The client only
ever receives already-computed `remaining_seconds` / `time_added_seconds` on
the heartbeat; rate/profile math happens entirely server-side.

---

## What changed, end to end (server-windows reference implementation)

- Added `RateProfile` model (`server-windows/models.py:9-23`) — `id`, `name`
  (unique), `color` (hex, for the dashboard badge), `is_default`,
  `created_at`. The row with `is_default=True` (always seeded as id=1,
  name="Default") is the fallback used when a PC has no profile assigned, or
  when its assigned profile has no active rates.
- Added `CoinRate.profile_id` FK (`server-windows/models.py:96-103`, nullable
  — NULL is only possible on rows from before this migration ran; the seed
  backfills them). Added `PC.rate_profile_id` FK
  (`server-windows/models.py:37-49`, nullable — NULL means "use Default").
- `services/rate_service.py` — `pesos_to_seconds()` and `get_active_rates()`
  both gained a `profile_id: int = 1` parameter with a fallback chain: rates
  for the assigned profile → rates for the Default profile → `config.py`
  hardcoded constants. Added `get_all_profiles(db)`.
- `services/session_service.py:98-99` — the one call site now resolves
  `profile_id = pc.rate_profile_id or 1` and passes it through.
- `dashboard/routes.py` — extended the existing flat `/rates` CRUD
  (`GET/POST /rates`, `DELETE /rates/{id}`) to be profile-aware, and added:
  `GET /partials/rates-table` (HTMX partial), `POST /rates/profiles` (create),
  `DELETE /rates/profiles/{id}` (delete — blocked for Default, reassigns its
  PCs to Default, soft-deletes its rates), `POST /rates/profiles/{id}/name`
  (rename — blocked for Default), `POST /pcs/{pc_number}/profile` (assign/clear
  a PC's profile, `profile_id=0` clears to Default).
- Templates: `rates.html` gained a profile-tabs strip (create/rename/delete)
  above the existing rates table; `partials/rates_table.html` now shows an
  empty-state message when a non-Default profile has no rates of its own;
  added `partials/profile_tabs.html` and `partials/pc_profile_badge.html`;
  `pcs.html` gained a "Rate Profile" column (desktop table) / row (mobile
  tile) with a color-coded badge + pencil-icon-toggles-to-dropdown pattern
  (`showProfileEdit()` / `hideProfileEdit()` / `assignProfile()` JS functions,
  `.profile-badge-pill` / `.profile-cell` / `.profile-select` CSS).

Verified end-to-end on server-windows: assigning a non-Default profile to a
PC, adding a profile-specific rate cheaper/richer than Default, then calling
`SessionService.add_time_by_pesos()` for that PC actually grants the
profile's seconds, not Default's (confirmed with a real DB — inserting ₱5 on
a PC assigned to a "VIP" profile with a ₱5=60min rate granted 3600s, not the
Default profile's 1800s for the same ₱5).

---

## Checklist — apply to `server/` (Raspberry Pi)

### 1. `models.py` — add `RateProfile`, plus FK columns

Reference: `server-windows/models.py:9-23` (new `RateProfile` class, insert it
before `class User(Base):`), `:37-49` (`PC` — add `rate_profile_id` column +
`rate_profile` relationship), `:96-103` (`CoinRate` — add `profile_id` column
+ `profile` relationship).

`server/models.py:9` is the equivalent `class User(Base):` line — insert
`RateProfile` above it. `server/models.py:29` is `class PC(Base):`,
`server/models.py:77` is `class CoinRate(Base):` — add the new columns to
those existing classes, don't duplicate them.

```python
class RateProfile(Base):
    """A named set of coin rates.  All CoinRate rows belong to exactly one profile.
    The profile with is_default=True is used as a fallback when a PC has no
    profile assigned, or when the assigned profile has no active rates.
    """
    __tablename__ = "rate_profiles"

    id         = Column(Integer, primary_key=True, index=True)
    name       = Column(String(50), nullable=False, unique=True)
    color      = Column(String(20), default="#4f8ef7")
    is_default = Column(Boolean, default=False)
    created_at = Column(DateTime, default=datetime.utcnow)

    rates = relationship("CoinRate", back_populates="profile")
    pcs   = relationship("PC",       back_populates="rate_profile")
```

Add to `PC`:
```python
rate_profile_id = Column(Integer, ForeignKey("rate_profiles.id"), nullable=True)
# ...
rate_profile = relationship("RateProfile", back_populates="pcs")
```

Add to `CoinRate`:
```python
profile_id = Column(Integer, ForeignKey("rate_profiles.id"), nullable=True)
# ...
profile = relationship("RateProfile", back_populates="rates")
```

### 2. `main.py` — import, migration guard, seeding

Reference: `server-windows/main.py:16` (import line),
`server-windows/main.py:280-291` (migration guard, added right after the
`ffmpeg_streaming_enabled` guard — `server/` has no `ffmpeg_streaming_enabled`
column, so on `server/` add it right after the existing
`new_membership_columns` loop, i.e. right before the
`table_exists("server_config")` block at `server/main.py:247-267`).

- [ ] Import: change `server/main.py:17` from
  `from models import AdminUser, CoinRate, MembershipConfig, ServerConfig` to
  `from models import AdminUser, CoinRate, MembershipConfig, RateProfile, ServerConfig`.
- [ ] Migration guard — insert into `_migrate_schema()` (no `table_exists()`
  wrapper needed; `coin_rates` and `pcs` are core tables present since the
  very first schema version, so if the db file exists at all — the
  precondition for this function to run — they exist too):
  ```python
  if not has_column("coin_rates", "profile_id"):
      cursor.execute("ALTER TABLE coin_rates ADD COLUMN profile_id INTEGER")
      migrated.append("coin_rates.profile_id (added)")

  if not has_column("pcs", "rate_profile_id"):
      cursor.execute("ALTER TABLE pcs ADD COLUMN rate_profile_id INTEGER")
      migrated.append("pcs.rate_profile_id (added)")
  ```
- [ ] Seeding — in `_seed_defaults()` (`server/main.py:296`), insert **before**
  the existing `if not db.query(CoinRate).first():` block (`server/main.py:307`)
  so the Default profile's id exists before the fallback default rate is
  created:
  ```python
  default_profile = db.query(RateProfile).filter_by(is_default=True).first()
  if not default_profile:
      default_profile = RateProfile(name="Default", color="#4f8ef7", is_default=True)
      db.add(default_profile)
      db.flush()   # get the id assigned before we reference it below
      logger.info("Created Default rate profile (id=%d)", default_profile.id)

  # Any CoinRate rows with profile_id=None (pre-existing installs, from
  # before Rate Profiles existed) are owned by the Default profile.
  if default_profile.id:
      db.query(CoinRate).filter(CoinRate.profile_id == None).update(  # noqa: E711
          {"profile_id": default_profile.id}, synchronize_session=False
      )
  ```
  Then add `profile_id=default_profile.id` to the existing
  `CoinRate(pesos=..., seconds=..., label=...)` constructor call right below
  it (`server/main.py:308-312`).
- [ ] Verify: on a pre-existing `pisonet.db`, restart the server and confirm
  the log shows `coin_rates.profile_id (added), pcs.rate_profile_id (added)`
  followed by `Created Default rate profile (id=1)`, and that the pre-existing
  coin rate row(s) get `profile_id=1` (not left NULL/orphaned). Run the
  migration twice in a row and confirm the second run is a no-op (no
  duplicate Default profile, no re-migration log line) — this is what proves
  the guard/seed logic is idempotent.

### 3. `services/rate_service.py` — make it profile-aware

Reference: `server-windows/services/rate_service.py` (whole file, 80 lines —
copy verbatim, it has no server-variant-specific code). `server/`'s current
version (`server/services/rate_service.py`, 54 lines) is the pre-profile flat
version — replace it wholesale with the server-windows version; there's
nothing hardware-specific in this file to preserve.

Key API changes (all backward compatible via defaults):
- `pesos_to_seconds(amount_pesos, db, profile_id=1)` — filters `CoinRate` by
  `is_active==True` AND `profile_id==pid`; falls back to Default profile's
  rates if none found for `pid`; falls back to `config.py` hardcoded defaults
  if still none.
- `get_active_rates(db, profile_id=1)` — same profile filter.
- New: `get_all_profiles(db)` — all `RateProfile` rows, `is_default` desc then
  `name` asc.

### 4. `services/session_service.py` — pass the PC's profile through

Reference: `server-windows/services/session_service.py:98-99`.

- [ ] `server/services/session_service.py:98` currently reads
  `seconds = pesos_to_seconds(pesos, self._db)` — change to:
  ```python
  profile_id = pc.rate_profile_id or 1
  seconds = pesos_to_seconds(pesos, self._db, profile_id=profile_id)
  ```

### 5. `dashboard/routes.py` — extend existing rates CRUD, add profile CRUD

Reference: `server-windows/dashboard/routes.py` rates section (the whole
block from `rates_page` through `set_pc_profile`, ~330 lines) — copy this
whole section verbatim, it's not hardware-specific.

`server/dashboard/routes.py` already has the pre-profile flat versions at:
`GET /rates` (`:345`), `POST /rates` (`:367`), `DELETE /rates/{rate_id}`
(`:406`) — you're **extending/replacing these three**, not adding new ones
alongside. Then add net-new: `GET /partials/rates-table`,
`POST /rates/profiles`, `DELETE /rates/profiles/{profile_id}`,
`POST /rates/profiles/{profile_id}/name`, `POST /pcs/{pc_number}/profile`.

- [ ] Import: add `RateProfile` to the `from models import ...` line at
  `server/dashboard/routes.py:21`.
- [ ] `pcs_page()` (`server/dashboard/routes.py:754-795`) — add
  `from services.rate_service import get_all_profiles`, compute
  `profiles = get_all_profiles(db)`, `default_profile`, `profile_map`, and
  per-PC `eff_profile` / `rate_profile` / `rate_profile_id` exactly as in
  `server-windows/dashboard/routes.py` `pcs_page()` — but do **not** add a
  `coins_enabled` context key; `server/` doesn't have that toggle. Add
  `"profiles": profiles` to the `TemplateResponse` context.
- [ ] Every admin-only route (`create_profile`, `delete_profile`,
  `rename_profile`, `set_pc_profile`) uses the same
  `if not current_user / if current_user["role"] != "admin"` idiom already
  present in `server/dashboard/routes.py`'s other admin routes — copy it
  verbatim, don't invent a different pattern.

### 6. Templates

Reference: `server-windows/dashboard/templates/rates.html`,
`partials/rates_table.html`, `partials/profile_tabs.html` (new),
`partials/pc_profile_badge.html` (new) — copy all four verbatim, no
hardware-specific markup in any of them.

- [ ] `server/dashboard/templates/rates.html` and
  `partials/rates_table.html` — replace wholesale with the server-windows
  versions (same reasoning as step 3: nothing variant-specific here).
- [ ] `pcs.html` — port the "Rate Profile" `<th>`/`<td>` (desktop table) and
  the mobile-tile `.pc-mgmt-row` block from `server-windows/dashboard/templates/pcs.html`,
  plus the `.profile-badge-pill` / `.profile-cell` / `.profile-view` /
  `.profile-edit-mode` / `.profile-edit-btn` / `.profile-select` CSS and the
  `showProfileEdit()` / `hideProfileEdit()` / `assignProfile()` JS functions
  at the bottom of the file. **`server/`'s `pcs.html` has no `coins_enabled`
  gating** (unlike server-windows) — insert the new column/row unconditionally,
  matching whatever `server/`'s current Coins column situation is (check
  first; if `server/` has a real hardware Coins column with no toggle gate,
  just add the Rate Profile column alongside it, don't add gating that
  doesn't exist elsewhere in this file).

### 7. Verify

```bash
cd server && python -m py_compile models.py main.py services/rate_service.py services/session_service.py dashboard/routes.py
```

- [ ] Confirm existing (pre-profile) coin rates aren't lost — start the
  server against a real pre-existing `pisonet.db` and confirm the backfill UPDATE
  runs (see step 2's verify note).
- [ ] Start the dev server, log into `/dashboard`, hit `/dashboard/rates` and
  `/dashboard/pcs` — confirm both return 200 with no Jinja2 template errors.
  If there are no PCs registered yet in the dev DB, insert one manually (raw
  SQL `INSERT INTO pcs (...)`) before checking `/dashboard/pcs` — the
  profile-cell markup lives inside the `{% for pc in pcs %}` loop and won't be
  exercised by the empty-state branch alone.
  ```bash
  grep -in "error\|traceback" saved_page.html   # after curl -s .../dashboard/pcs -o saved_page.html
  ```
- [ ] Manually trace (or exercise via the dashboard UI / curl): create a
  second profile, give it a richer rate than Default, assign it to a PC, then
  confirm `SessionService.add_time_by_pesos()` for that PC actually returns
  the profile's seconds — the check that matters isn't "does the page
  render," it's "does the profile assignment change what a coin insertion is
  worth."
- [ ] Stop the server, then diff the `.env`/`pisonet.db` you tested against
  back to its original state if it's a shared dev database, since the
  migration is idempotent and will simply re-run cleanly on next real
  startup — don't leave test profiles/PCs behind in a database other
  developers use.

---

## Not part of this migration (already correct, no action needed)

- Client (`client/PisoNetClient/`) — confirmed no profile references anywhere
  in the client; purely a server-side rate calculation.
- `server-windows/` and `server-orangepi/` — both already have this feature,
  do not touch as part of this migration.
- `server/`'s `coins_enabled` toggle and `ffmpeg_streaming_enabled` column —
  server-windows-only features (separately migrated); do not add them to
  `server/` as a side effect of this change.
