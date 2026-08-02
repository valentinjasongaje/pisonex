# FFmpeg Live-Video-Stream Migration

FFmpeg live-video "watch PC" streaming has been added to `server-windows/`.
When an admin opens the fullscreen monitor view for a PC, the dashboard opens
a WebSocket to the client and (if the client has `ffmpeg.exe` available)
receives real-time MPEG1 video via [jsmpeg](https://github.com/phoboslab/jsmpeg)
instead of relying solely on 1-second JPEG snapshots. This closes the gap
between `server-windows` and `server-orangepi/`, which already had this
feature (it was the reference implementation this migration was ported from).

**This doc is a checklist**, not prose to read once. `server/` (Raspberry Pi)
is now the **only** remaining variant without this feature — `server-orangepi/`
(production) already has it, and `server-windows/` just got it. There is no
shared `core/` layer yet, so `server/` needs the same changes applied
independently. Do NOT copy files wholesale from `server-orangepi/` — `server/`
is structurally very close to `server-orangepi/` (both have real coin-slot
GPIO hardware and an almost-identical `ServerConfig`/`main.py`/`routes.py`),
so most of this port **is** closer to a verbatim copy than the
`server-windows` port was. The one place it is **not** verbatim is
`monitor.html` — see step 8.

The client (`client/PisoNetClient/`) is shared by all three server variants —
**already done, no further client work needed.** It already supports FFmpeg
launch (`StreamCaptureService.vb`), reacts to `capture_interval_ms` changes
(`OnCaptureIntervalChanged` in `Program.vb`), and publishes to
`/dashboard/ws/stream/{pc}/publish` once told to ramp up. Do not touch it.

---

## What changed, end to end (server-windows, and previously server-orangepi)

- Added `stream_store.py` — in-memory pub/sub: one FFmpeg publisher (the PC
  client) and N watchers (admin browsers) per PC number. No server-specific
  code — a straight file copy.
- Added `command_store._watched` / `set_watched()` / `is_watched()` — a
  12-second TTL per PC number, renewed by the dashboard every 8 s while
  fullscreen is open. This is what flips `capture_interval_ms` from 0 to 33 on
  the heartbeat.
- Added `ServerConfig.ffmpeg_streaming_enabled` (bool, default `True`) — admin
  kill-switch for the whole feature; when off, `capture_interval_ms` stays 0
  and the fullscreen view never asks the client to start FFmpeg.
- Heartbeat (`api/pc.py`) now computes
  `capture_interval_ms = 33 if is_watched(pc) and ffmpeg_streaming_enabled else 0`
  instead of a hardcoded `0`.
- Three new dashboard routes: `POST /dashboard/monitor/watch/{pc_number}`
  (keepalive), `WS /dashboard/ws/stream/{pc_number}/publish` (client → server,
  API-key gated), `WS /dashboard/ws/stream/{pc_number}/watch` (server →
  browser, JWT-cookie gated). Plus `POST /dashboard/api/settings/ffmpeg-streaming`
  to flip the toggle.
- `dashboard/templates/settings.html` — new "Monitoring" card with the toggle.
- `dashboard/templates/monitor.html` — jsmpeg canvas layered on top of the
  existing fullscreen `<img>`, switching over on first decoded frame and
  falling back on stall/end/disable.
- Copied `dashboard/static/js/jsmpeg.min.js` (third-party, ~138 KB, vendored —
  copy the file byte-for-byte, do not re-download or modify it).

---

## Checklist — apply to `server/` (Raspberry Pi)

### 1. `stream_store.py` — copy verbatim

Reference: `server-windows/stream_store.py` (itself a byte-identical copy of
`server-orangepi/stream_store.py`). Copy either one straight into
`server/stream_store.py` — it has zero server-specific code (`set_publisher`,
`clear_publisher`, `is_publishing`, `add_watcher`, `remove_watcher`,
`has_watchers`, `broadcast`, `close_all_watchers`).

### 2. `command_store.py` — add the `_watched` block

Reference: `server-windows/command_store.py:285-307` (added right after the
`_LOGIN_WINDOW_SECONDS`/`import time as _time` region — check `server/`'s
current line for `import time as _time` first, since `server/` may order its
sections slightly differently; insert near there, matching this file's
existing `_lock`/`with _lock:` convention).

```python
# ── Watched PCs (live stream) ────────────────────────────────────────────────

_WATCHED_TTL = 12  # seconds

# {pc_number: expire_timestamp}
_watched: dict[int, float] = {}


def set_watched(pc_number: int) -> None:
    """Mark a PC as actively watched by admin (TTL = 12 s). Renewed by dashboard keepalive."""
    with _lock:
        _watched[pc_number] = _time.time() + _WATCHED_TTL


def is_watched(pc_number: int) -> bool:
    """Return True if an admin is currently viewing the live stream for this PC."""
    with _lock:
        expire = _watched.get(pc_number)
        if expire is None:
            return False
        if _time.time() > expire:
            _watched.pop(pc_number, None)
            return False
        return True
```

### 3. `models.py` — add column to `ServerConfig`

Reference: `server-orangepi/models.py:169-172`. `server/models.py:122` is the
equivalent `ServerConfig` class — it already has the GPIO hardware columns
(`coin_pin`, `relay_pin`, `coin_edge`, `coin_debounce_ms`,
`coin_pulse_timeout`, ending around line 144); add this column directly after
those, same as `server-orangepi` does:

```python
# ── Monitoring ────────────────────────────────────────────────────────────
# When True (default), the server requests FFmpeg live-streaming when admin
# opens fullscreen. Set False to fall back to 1-second JPEG snapshots.
ffmpeg_streaming_enabled = Column(Boolean, default=True, nullable=False)
```

### 4. `main.py` — migration guard in `_migrate_schema()`

Reference: `server-orangepi/main.py:261-272`. `server/main.py:256-267` has the
**same** `table_exists("server_config")` guard block with a `new_server_columns`
list — add `ffmpeg_streaming_enabled` to that existing list rather than
writing a second standalone guard (same pattern used for the `coins_enabled`
column in the coins-toggle migration, if that has landed on this variant by
the time you do this):

```python
new_server_columns = [
    ("coin_pin", "INTEGER"),
    ("relay_pin", "INTEGER"),
    ("coin_edge", "VARCHAR(10)"),
    ("coin_debounce_ms", "INTEGER"),
    ("coin_pulse_timeout", "VARCHAR(16)"),
    ("ffmpeg_streaming_enabled", "BOOLEAN DEFAULT 1"),   # ← add this line
]
```

- [ ] Confirm `_seed_defaults()`'s `ServerConfig(id=1, ...)` insert (search for
  `ServerConfig(` in `server/main.py`) does **not** need an explicit
  `ffmpeg_streaming_enabled=` kwarg — the model's `default=True` covers fresh
  inserts, same as `server-orangepi` relies on.

### 5. `schemas.py` — no change needed

`server/schemas.py:43` already has `capture_interval_ms: int = 0` on
`PCHeartbeatResponse` (same as `server-windows` did before this migration) —
nothing to add here, just confirm it's present.

### 6. `api/pc.py` — replace hardcoded `capture_interval_ms=0`

Reference: `server-orangepi/api/pc.py:198-205`. `server/api/pc.py:199` has
the hardcoded `capture_interval_ms=0` inside the `PCHeartbeatResponse(...)`
constructor in the `heartbeat()` handler. Check whether `srv_cfg` is already
queried in that function (grep `db.query(ServerConfig).first()` in
`server/api/pc.py` — it's likely fetched already for other `ServerConfig`
fields such as coin GPIO overrides used elsewhere in the same handler); reuse
it rather than adding a second query. Replace:

```python
capture_interval_ms=0,
```

with:

```python
capture_interval_ms=(
    33
    if command_store.is_watched(pc_number)
    and getattr(srv_cfg, "ffmpeg_streaming_enabled", True)
    else 0
),
```

### 7. `dashboard/routes.py` — three routes + settings context + toggle endpoint

Reference: `server-orangepi/dashboard/routes.py:931-1016` (the three routes)
and `:1654`, `:1673-1688` (settings context + toggle endpoint). Equivalently,
`server-windows/dashboard/routes.py:714-806` (routes, after this migration)
and `:1372`, `:1450-1466` (context + toggle) — whichever you find easier to
diff against.

- [ ] Add `WebSocket, WebSocketDisconnect` to the `from fastapi import ...`
  line at the top of `server/dashboard/routes.py`, and `import asyncio` at
  the top of the file (check first — `server/routes.py` may already import
  `asyncio` inline inside the existing `/api/pc/{pc_number}/stream` handler if
  that route exists there; if so, promote to a top-level import or keep the
  existing inline-import convention, whichever this file already does
  elsewhere for other stdlib modules).
- [ ] Insert, right after the existing `serve_screenshot()` route:
  ```python
  @router.post("/monitor/watch/{pc_number}")
  async def watch_pc(
      pc_number: int,
      current_user: Optional[str] = Depends(_validate_session),
  ):
      if not current_user:
          raise HTTPException(status_code=401, detail="Not authenticated")
      command_store.set_watched(pc_number)
      return {"status": "ok"}


  @router.websocket("/ws/stream/{pc_number}/publish")
  async def ws_stream_publish(websocket: WebSocket, pc_number: int):
      import logging, stream_store
      _log = logging.getLogger("stream.publish")

      api_key = (
          websocket.headers.get("x-api-key", "")
          or websocket.query_params.get("api_key", "")
      )
      if settings.CLIENT_API_KEY and api_key != settings.CLIENT_API_KEY:
          _log.warning("PC %d publish rejected — bad API key", pc_number)
          await websocket.close(code=1008)
          return

      await websocket.accept()
      stream_store.set_publisher(pc_number, websocket)
      chunks = 0
      try:
          while True:
              data = await websocket.receive_bytes()
              chunks += 1
              await stream_store.broadcast(pc_number, data)
      except WebSocketDisconnect:
          pass
      except Exception as exc:
          _log.warning("PC %d publish error after %d chunks: %s", pc_number, chunks, exc)
      finally:
          stream_store.clear_publisher(pc_number)
          await stream_store.close_all_watchers(pc_number)


  @router.websocket("/ws/stream/{pc_number}/watch")
  async def ws_stream_watch(websocket: WebSocket, pc_number: int):
      import logging, stream_store
      _log = logging.getLogger("stream.watch")

      token = websocket.cookies.get("pisonet_session")
      if not token:
          await websocket.close(code=1008)
          return
      try:
          jwt.decode(token, settings.SECRET_KEY, algorithms=[_ALGORITHM])
      except Exception:
          await websocket.close(code=1008)
          return

      await websocket.accept()
      stream_store.add_watcher(pc_number, websocket)
      try:
          while True:
              await asyncio.sleep(20)
      except (WebSocketDisconnect, Exception):
          pass
      finally:
          stream_store.remove_watcher(pc_number, websocket)
  ```
  Check `server/dashboard/routes.py` for its `_validate_session` return type
  and JWT constant name (`_ALGORITHM` vs. inline `"HS256"`) before pasting —
  `server-orangepi` uses an inline `from jose import jwt as _jwt` import,
  `server-windows` reuses the module-level `jwt` + `_ALGORITHM` already
  imported at the top of the file. Use whichever `server/routes.py` already
  does (it should match `server-windows`'s convention, both use `jose.jwt` +
  `HS256` + `settings.SECRET_KEY` per this repo's shared dashboard-auth
  pattern).
- [ ] Add `"ffmpeg_streaming_enabled": getattr(srv_cfg, "ffmpeg_streaming_enabled", True) if srv_cfg else True`
  to the `settings_page()` template context (`server/dashboard/routes.py:1252-1286`
  region) — same `srv_cfg` already queried there for `api_key`/coin GPIO.
- [ ] Add the toggle endpoint (mirrors `save_branch_name`/`save_coins_enabled`
  if present):
  ```python
  class FfmpegToggleBody(BaseModel):
      enabled: bool


  @router.post("/api/settings/ffmpeg-streaming")
  def save_ffmpeg_streaming(
      body: FfmpegToggleBody,
      db: Session = Depends(get_db),
      current_user: Optional[dict] = Depends(_validate_session),
  ):
      if not current_user:
          raise HTTPException(status_code=401, detail="Not authenticated")
      srv_cfg = db.query(ServerConfig).first()
      if not srv_cfg:
          srv_cfg = ServerConfig(id=1, client_api_key="")
          db.add(srv_cfg)
      srv_cfg.ffmpeg_streaming_enabled = body.enabled
      db.commit()
      return {"status": "ok", "ffmpeg_streaming_enabled": body.enabled}
  ```

### 8. `dashboard/templates/settings.html` — Monitoring card

Reference: `server-orangepi/dashboard/templates/settings.html:181-209` (card
+ `saveFfmpegSetting()` JS). `server/settings.html` already has a "Coin Slot
Hardware" card (`server/dashboard/templates/settings.html:80-82` region) —
insert the new "Monitoring" card as its own card, adjacent to it (order
doesn't matter functionally; `server-orangepi` places it next to Security).
Copy the card markup and the `saveFfmpegSetting()` function directly — the
toggle-label/status-span pattern is identical to every other toggle already
in this file (`coins_enabled` if present, `preset_amounts_enabled`, etc.), so
match `server/`'s existing card style rather than pasting orangepi's raw
`<div>` structure/inline-styles wholesale (same judgment call made for
`server-windows` in this migration and for `server-windows` in the
coins-toggle migration).

### 9. `dashboard/static/js/jsmpeg.min.js` — copy verbatim

Copy `server-orangepi/dashboard/static/js/jsmpeg.min.js` (or
`server-windows/dashboard/static/js/jsmpeg.min.js` — byte-identical) into
`server/dashboard/static/js/jsmpeg.min.js`. Binary-safe copy, ~138 KB,
third-party vendored file — do not re-download or edit it.

### 10. `dashboard/templates/monitor.html` — **not a verbatim port, adapt like server-windows did**

This is the one step where you should reference `server-windows/dashboard/templates/monitor.html`
(after this migration) **instead of** `server-orangepi`'s. Here's why:
`server-orangepi`'s fullscreen fallback is a persistent server-push MJPEG
stream (`<img src="/dashboard/api/pc/{pc}/stream">`, backed by a
`StreamingResponse` route at `server-orangepi/dashboard/routes.py:1018`).
**`server/` does not have that MJPEG route** (confirmed — grep
`/api/pc/{pc_number}/stream` in `server/dashboard/routes.py` before starting;
it wasn't there as of this writing) — its fullscreen fallback is the same
1-second **client-driven polling** of the single-shot `/screenshot` endpoint
that `server-windows` had before this migration (`_fullscreenImgTimer` +
`refreshScreenshots()`). If `server/` still matches that pattern when you do
this migration:

- [ ] Do **not** add the `/api/pc/{pc_number}/stream` MJPEG route or try to
  match orangepi's `<img>`-based fallback transport. Keep `server/`'s
  existing JPEG-polling fallback exactly as-is.
- [ ] Port the jsmpeg **overlay** on top of it using
  `server-windows/dashboard/templates/monitor.html` as the reference instead:
  add the `<canvas id="fullscreen-canvas">` + `#fullscreen-live-badge` span
  to the fullscreen modal HTML, add `<script src="/static/js/jsmpeg.min.js">`,
  add the `_jsmpegPlayer`/`_streamSwitched`/`_watchKeepalive` state vars,
  wrap the existing JPEG-polling start/stop logic into a
  `_startJpegFallback(pcNumber)` helper (called both on `openFullscreen()`
  and from `_fallbackToMjpeg()`), and layer the `JSMpeg.Player(...)` call
  with `onVideoDecode` (switch to canvas, stop the JPEG timer),
  `onStalled`/`onEnded`/`onSourceCompleted` (call `_fallbackToMjpeg()`) —
  copy this wiring near-verbatim from `server-windows/dashboard/templates/monitor.html`
  since it already solved the "polling fallback, not MJPEG" adaptation.
  Also guard `refreshScreenshots()` so it skips updating `fullscreen-img`
  once `_streamSwitched` is true (`server-windows/dashboard/templates/monitor.html:207-211`).
- [ ] If `server/` has since grown its own persistent MJPEG stream route by
  the time you read this (double-check — it may have been added independently
  of this feature), then port `server-orangepi`'s version instead and ignore
  this adaptation note.

### 11. `dashboard/static/css/admin.css` — live badge + canvas styles

Reference: `server-windows/dashboard/static/css/admin.css` (`#fullscreen-live-badge`,
`@keyframes live-pulse`, `#fullscreen-canvas`, and the `.fullscreen-content`
`align-items: center; justify-content: center;` additions, all inserted right
after the existing `.fullscreen-stats-btn:hover` rule). `server/admin.css`'s
`.fullscreen-content`/`#fullscreen-img` rules should look the same as
`server-windows`'s did pre-migration (same shared dashboard CSS lineage) —
diff against `server-windows/dashboard/static/css/admin.css` post-migration
rather than `server-orangepi`'s (which has a slightly different `.fullscreen-box`
max-width rule unrelated to this feature — don't pull that unrelated diff in).

---

## Verify

```bash
grep -rn "ffmpeg_streaming_enabled\|is_watched\|set_watched\|stream_store" server/
```

Should show: the new model column, the migration guard, the `command_store`
functions, the `stream_store` import inside the two new websocket routes, the
heartbeat's `capture_interval_ms` expression, the settings context/toggle
route, and the template references. Run `python -m py_compile` on every
touched `server/*.py` file. Manually confirm no leftover hardcoded
`capture_interval_ms=0`. Start `python server/main.py` and hit
`/dashboard/settings` to confirm it renders without a Jinja error (a real
FFmpeg stream isn't needed to verify this — just that the page and the two
new WebSocket routes don't crash the app at import time).

---

## Not part of this migration (already correct, no action needed)

- Client (`client/PisoNetClient/`) — see intro above. Already supports FFmpeg
  launch, capture-interval reaction, and WebSocket publish for all three
  server variants once they speak the same heartbeat/WebSocket contract.
- `server-orangepi/` — already has this feature; it was the reference this
  migration (and the `server-windows` port before it) was ported from. Do not
  touch it.
