# Admin-Only Membership Migration

Self-service member registration from the client lock screen has been removed
in `server-windows/` + `client/PisoNetClient/`. Members can no longer create
their own account — only an admin can, via the dashboard. This was done to
stop customers from creating multiple accounts to abuse absorption / zero-time
login.

**This doc is a checklist**, not prose to read once. `server/` (Raspberry Pi)
and `server-orangepi/` (Orange Pi, production) still have the old
self-registration flow (`server/api/member.py`, `server-orangepi/api/member.py`
+ their own `services/membership_service.py`) and need the same changes
applied independently — there is no shared `core/` layer yet, each server
variant duplicates this logic. Do NOT copy files wholesale; each variant's
`main.py`/`routes.py` has its own surrounding code, follow the pattern below
against each file.

The client (`client/PisoNetClient/`) is shared by all three server variants —
**already done, no further client work needed** once `server/` and
`server-orangepi/` implement the same API contract described below.

---

## What changed, end to end

- Removed: `POST /api/member/register` (self-service, gated only by the
  shared client API key — no admin auth).
- Added: `POST /dashboard/api/membership/create-member` (admin dashboard,
  session-gated) — creates a member with an auto-generated temp password and
  optional seeded balance.
- Added: `POST /api/member/change-password` (client-key gated, identifies the
  member via the existing PC↔member binding — no old password needed since
  the caller just authenticated on that PC).
- Added: `User.must_change_password` column (bool, default False).
- Client: `LockForm`'s inline "Register" mode removed; new
  `ChangePasswordForm` forced modal shown right after login when
  `must_change_password=True`.

---

## Checklist — apply to `server/` and `server-orangepi/` (each independently)

### 1. `models.py` — add column to `User`

Reference: `server-windows/models.py:27` (added directly under the existing
membership tracking columns on `User`).

```python
# True for admin-issued accounts until the member sets their own password.
# Set on creation by the dashboard "Create Member" flow; cleared by
# POST /api/member/change-password on first successful password change.
must_change_password = Column(Boolean, default=False, nullable=False)
```

### 2. `main.py` — migration guard in `_migrate_schema()`

Reference: `server-windows/main.py:246-250`, added right after the existing
`new_user_columns` loop (same function, same guard style: check-then-`ALTER
TABLE`).

```python
if not has_column("users", "must_change_password"):
    cursor.execute(
        "ALTER TABLE users ADD COLUMN must_change_password INTEGER NOT NULL DEFAULT 0"
    )
    migrated.append("users.must_change_password (added)")
```

- [ ] Confirm `server/main.py` and `server-orangepi/main.py` both have an
  equivalent `_migrate_schema()` with a `new_user_columns` loop to insert
  this after. (They should — same v2→v3 migration history.)

### 3. `schemas.py` — remove register schemas, add change-password + must_change_password

Reference: `server-windows/schemas.py:143-166`.

- [ ] **Remove** `MemberRegisterRequest` and `MemberRegisterResponse`.
- [ ] Add `must_change_password: bool = False` to `MemberLoginResponse`.
- [ ] Add:
  ```python
  class MemberChangePasswordRequest(BaseModel):
      pc_number: int
      new_password: str

  class MemberChangePasswordResponse(BaseModel):
      success: bool
      error: Optional[str] = None
  ```
- [ ] Add `must_change_password: bool = False` to `MemberListResponse` (used
  by the dashboard members table) — reference `server-windows/schemas.py:215`.

### 4. `api/member.py` — remove register route, add change-password route

Reference: `server-windows/api/member.py` (whole file rewritten — it's short,
easiest to diff directly against the new version rather than patch).

- [ ] **Delete** the `POST /register` route entirely (was gated only by
  `verify_client_key`, no admin check — that's the vulnerability being closed).
- [ ] Add:
  ```python
  @router.post("/change-password", response_model=MemberChangePasswordResponse, dependencies=[_ClientAuth])
  def change_password(req: MemberChangePasswordRequest, db: Session = Depends(get_db)):
      svc = MembershipService(db)
      result = svc.change_password(req.pc_number, req.new_password)
      return MemberChangePasswordResponse(**result)
  ```
- [ ] Keep `login`, `logout`, `status` routes and their `verify_client_key`
  dependency unchanged — only `register` is removed.

### 5. `services/membership_service.py` — repurpose `register_member`, add `change_password`

Reference: `server-windows/services/membership_service.py:68-176`.

- [ ] **Replace** `register_member(username, password, pc_number)` (which did
  username/password validation + PC session absorption for a
  self-registering client) with `admin_create_member(username, initial_minutes=0)`:
  - Reuses the same `_USERNAME_RE` validation and uniqueness check.
  - Does **not** take a `password` param or `pc_number` — no session
    absorption logic, this isn't tied to a live client session.
  - Generates a temp password via `_generate_temp_password()`: 8 chars,
    lowercase letters + digits only (no symbols), with 1-2 letters
    capitalized — kept easy to read/type since the admin hands it to the
    member verbally or on paper.
  - `initial_minutes * 60` seeds `User.balance_seconds` at creation (see
    step 7 below for why this exists — optional, admin sells a membership
    with time already included).
  - Sets `must_change_password=True` on the new `User`.
  - Returns `{"success", "user_id", "username", "temp_password", "balance_seconds"}`
    — `temp_password` is plaintext, returned exactly once, never logged, never
    stored (only its bcrypt hash via `hash_password()` is persisted).
- [ ] Add `change_password(pc_number, new_password)`:
  - Validates `cfg.membership_enabled` and password length (6-128 chars —
    note this is a **different minimum than registration's old 4-char min**;
    match `server-windows`'s 6-char minimum for consistency).
  - Identifies the member via `command_store.get_member_for_pc(pc_number)` —
    **not** a re-check of the old password. This is safe because the client
    only calls this endpoint immediately after `login_member()` already
    authenticated and bound the member to that PC.
  - Updates `password_hash` via `hash_password()`, clears
    `must_change_password`, commits.
- [ ] In `login_member()`, add `"must_change_password": user.must_change_password`
  to the returned dict (reference: right before the `return` at the end of
  the absorption/balance branch, `server-windows/services/membership_service.py:147-224`
  region) so the client knows to force the modal.

### 6. `dashboard/routes.py` — admin-only create-member route

Reference: `server-windows/dashboard/routes.py:1410-1444` (inserted right
before `update_membership_config`, follows the existing `CreateStaffBody` /
`create_staff_user` pattern at `~routes.py:1509-1564` for validation/response
style — check role via `current_user["role"] != "admin"`, not just "any
logged-in session").

```python
class CreateMemberBody(BaseModel):
    username: str
    initial_minutes: int = 0  # optional — membership sold with time included

@router.post("/api/membership/create-member", dependencies=[Depends(_require_active_license)])
def create_member(
    body: CreateMemberBody,
    db: Session = Depends(get_db),
    current_user: Optional[dict] = Depends(_validate_session),
):
    if not current_user:
        raise HTTPException(status_code=401, detail="Not authenticated")
    if current_user["role"] != "admin":
        raise HTTPException(status_code=403, detail="Admin access required")

    username = body.username.strip()
    if not username:
        raise HTTPException(status_code=422, detail="Username cannot be empty")
    if body.initial_minutes < 0:
        raise HTTPException(status_code=422, detail="Initial time cannot be negative")

    msvc = MembershipService(db)
    result = msvc.admin_create_member(username, initial_minutes=body.initial_minutes)
    if not result["success"]:
        raise HTTPException(status_code=422, detail=result["error"])

    return {
        "status": "created",
        "username": result["username"],
        "temp_password": result["temp_password"],
        "balance_seconds": result["balance_seconds"],
    }
```

- [ ] Also add `"must_change_password": m.must_change_password` to the
  per-member dict built in `membership_page()` (reference:
  `server-windows/dashboard/routes.py:1361-1406`, the `member_data.append({...})`
  block) so the template can show a badge.
- [ ] Confirm `_require_active_license` and `_validate_session` exist with
  the same names/behavior in this variant's `routes.py` before reusing them
  verbatim (they should — same dashboard auth pattern across variants).

### 7. `dashboard/templates/membership.html` — Create Member form + one-time password reveal

Reference: `server-windows/dashboard/templates/membership.html` — copy the
whole "Create Member" card (username input + **initial time (minutes)**
input + submit button + one-time temp-password reveal box with a Copy
button) and the `createMember()` / `copyMemberPassword()` JS functions
directly from this file into the target variant's template of the same name.
The copy-to-clipboard pattern (`navigator.clipboard.writeText`) is copied
from this same file's existing `copyKey()` function used for the
`CLIENT_API_KEY` reveal in `settings.html` — reuse that idiom, don't
reinvent it.

- [ ] Add a `Temp Password` badge (`badge-warning` class, already defined in
  `staff.html`/`license.html`) next to `is_active` in both the desktop table
  row and the mobile tile, gated on `m.must_change_password`.
- [ ] The initial-time field is **optional and in minutes** — convert to
  seconds server-side (step 6), not client-side, so the wire format for
  `initial_minutes` stays a plain integer count of minutes.

### 8. Verify nothing else references the removed self-registration route

Before considering the variant done, grep it for stragglers:

```bash
grep -rn "register_member\|MemberRegisterRequest\|MemberRegisterResponse\|/api/member/register" server/ server-orangepi/
```

Should return zero hits once steps 3-6 are applied (aside from a comment you
may choose to leave noting why it was removed, as `server-windows/api/member.py`
does at the top of the file).

---

## Not part of this migration (already correct, no action needed)

- Client (`client/PisoNetClient/`) — `LockForm.vb` register-mode UI,
  `LockManager.vb`/`Program.vb` wiring, and `MemberService.vb`'s
  `RegisterAsync` were all removed; `ChangePasswordForm.vb` +
  `MemberService.ChangePasswordAsync` + `LockManager.ShowChangePasswordDialog`
  were added. This is shared by all three server variants and needs no
  further changes once `server/` and `server-orangepi/` implement the same
  `/api/member/change-password` contract (request: `{pc_number, new_password}`,
  response: `{success, error}`) and add `must_change_password` to the login
  response.
- `POST /api/membership/members/{id}/adjust-balance` (existing, unchanged) —
  still the correct way to top up an *existing* member's balance later. The
  new `initial_minutes` field on create-member only seeds the balance at
  creation time; it does not replace or change this endpoint.
