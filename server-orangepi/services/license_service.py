import hashlib
import hmac
import json
import logging
import os
import platform
import sys
import time
import uuid
from datetime import datetime, timedelta, timezone
from enum import Enum
from pathlib import Path
from typing import Optional


def _parse_iso_utc_naive(value: Optional[str]) -> Optional[datetime]:
    """Parse an ISO-8601 string into a NAIVE UTC datetime.

    pisonex.com returns timestamps with a 'Z' suffix (e.g.
    "2026-05-24T13:56:42.295Z"), which datetime.fromisoformat parses as
    timezone-AWARE. The rest of this module compares against datetime.utcnow()
    (naive), and mixing aware/naive raises TypeError. Normalising everything to
    naive UTC here keeps subtraction/comparison safe.
    """
    if not value:
        return None
    try:
        dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (ValueError, TypeError, AttributeError):
        return None
    if dt.tzinfo is not None:
        dt = dt.astimezone(timezone.utc).replace(tzinfo=None)
    return dt

import httpx
from jose import jwt, exceptions as jose_exceptions

logger = logging.getLogger(__name__)

PISONEX_API = "https://www.pisonex.com"

# Resolve the data directory relative to the executable when frozen by PyInstaller,
# or relative to this source file when running normally.
if getattr(sys, "frozen", False):
    _APP_DIR = Path(sys.executable).parent
else:
    _APP_DIR = Path(__file__).resolve().parent.parent

from config import settings as _settings

# ── ES256 public key (matches LICENSE_SIGNING_PRIVATE_KEY on pisonex.com) ────
# Generated 2026-05-28 (regenerated; the 2026-05-23 pair's private half was
# never deployed). Replace both keys together if rotating.
_LICENSE_PUBLIC_KEY = """-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfs2Dsuq02WpHzOFovGzqPZ+QrKu+
pVBtuCV3iJrWgskL98kRiXaujl935uEhHtO/nCcL+qna9OsX4li5fevKVg==
-----END PUBLIC KEY-----"""


def _verify_license_token(token: str) -> Optional[dict]:
    """Verify an ES256-signed token from pisonex.com. Returns claims or None."""
    try:
        claims = jwt.decode(
            token,
            _LICENSE_PUBLIC_KEY,
            algorithms=["ES256"],
            options={"verify_aud": False},
        )
        return claims
    except Exception:
        return None


def _get_hmac_secret() -> bytes:
    return _settings.LICENSE_HMAC_SECRET.encode()


def _signed_payload(body: dict) -> dict:
    """Add timestamp + HMAC-SHA256 signature to an outgoing payload.
    The pisonex.com server validates _ts (5-minute window) to block replays.
    """
    ts = str(int(time.time()))
    canonical = json.dumps(body, separators=(",", ":"), sort_keys=True) + ts
    sig = hmac.new(_get_hmac_secret(), canonical.encode(), hashlib.sha256).hexdigest()
    return {**body, "_ts": ts, "_sig": sig}


LICENSE_FILE = _APP_DIR / "data" / "license.json"
TRIAL_DAYS = 14
OFFLINE_GRACE_HOURS = 72
VERIFY_INTERVAL_HOURS = 6


def _get_machine_id() -> str:
    """Return a stable hardware-bound identifier for this machine.

    Primary: /etc/machine-id (systemd UUID, set at OS install, unique per device)
    Fallback: hostname + architecture (no MAC — easily spoofed)
    """
    mid_path = Path("/etc/machine-id")
    if mid_path.exists():
        val = mid_path.read_text().strip()
        if val:
            return val

    return f"{platform.node()}|{platform.machine()}|{platform.processor()}"


class LicenseStatus(str, Enum):
    ACTIVATED = "activated"
    TRIAL = "trial"
    EXPIRED = "expired"
    OFFLINE_LOCKED = "offline_locked"


class LicenseService:
    def __init__(self):
        self._data: dict = {}
        self._load()

    # ── Persistence ──────────────────────────────────────────────────

    def _load(self):
        if LICENSE_FILE.exists():
            try:
                self._data = json.loads(LICENSE_FILE.read_text())
            except Exception:
                self._data = {}
        if "first_run" not in self._data:
            self._data["first_run"] = datetime.utcnow().isoformat()
            self._save()

    def _save(self):
        LICENSE_FILE.parent.mkdir(parents=True, exist_ok=True)
        LICENSE_FILE.write_text(json.dumps(self._data, indent=2))

    # ── Startup sync (telemetry + trial-clock restore) ───────────────

    async def sync_startup_status(self) -> None:
        """POST to /api/status for telemetry and to anchor the trial clock.

        The server records first_seen_at on the first ping for this device_id.
        We always use the EARLIER of local vs server dates, so:
          - Deleting license.json → local first_run resets to now, but server
            returns the original date and we correct it immediately.
          - Editing first_run to a future date → server date is earlier, we fix it.
        """
        try:
            device_id = self.get_device_id()
            status_info = self.get_status()
            payload = {
                "device_id": device_id,
                "device_type": "server",
                "license_status": status_info["status"],
                "app_version": "1.0.4",
                "machine_name": platform.node(),
                "trial_days_remaining": status_info["trial_days_remaining"],
            }
            if self._data.get("license_key"):
                payload["license_key"] = self._data["license_key"]
            async with httpx.AsyncClient(timeout=10) as client:
                resp = await client.post(
                    f"{PISONEX_API}/api/status",
                    json=payload,
                )
            if resp.status_code == 200:
                data = resp.json()
                server_date = data.get("first_seen_at")
                server_dt = _parse_iso_utc_naive(server_date)
                if server_dt is not None:
                    local_dt = _parse_iso_utc_naive(self._data.get("first_run"))
                    # Use the EARLIER of local vs server (both normalised to naive
                    # UTC). Store as a naive ISO string so downstream subtraction
                    # against datetime.utcnow() never mixes aware/naive datetimes.
                    if local_dt is None or server_dt < local_dt:
                        self._data["first_run"] = server_dt.isoformat()
                        self._save()
                        logger.info("Trial start date anchored to server: %s", server_dt.isoformat())
        except Exception as e:
            logger.warning("Failed to sync status with pisonex.com: %s", e)

    # ── Device ID ────────────────────────────────────────────────────

    def get_device_id(self) -> str:
        # Always derive from hardware — never trust the cached value.
        # This prevents an attacker from editing device_id in license.json
        # to impersonate a different device or reset the server-side trial clock.
        raw = _get_machine_id()
        device_id = hashlib.sha256(raw.encode()).hexdigest()
        if self._data.get("device_id") != device_id:
            self._data["device_id"] = device_id
            self._save()
        return device_id

    # ── Token verification ───────────────────────────────────────────

    def _get_valid_token_claims(self) -> Optional[dict]:
        """Return verified token claims if a valid, unexpired token is stored.
        Returns None if no token, signature is bad, or token is expired.
        """
        token = self._data.get("license_token")
        if not token:
            return None
        claims = _verify_license_token(token)
        if claims is None:
            logger.warning("license_token signature invalid — ignoring stored token")
            return None
        # Verify device binding: token must have been issued for this device
        if claims.get("did") != self.get_device_id():
            logger.warning("license_token device_id mismatch — ignoring stored token")
            return None
        # Verify the token's license_key (sub) matches the stored license_key.
        # Blocks an attacker who pastes a token from a different (still-valid)
        # license that they own onto a machine whose license has been revoked.
        local_key = self._data.get("license_key")
        token_sub = claims.get("sub")
        if local_key and token_sub and local_key.strip().upper() != str(token_sub).strip().upper():
            logger.warning("license_token license_key mismatch — ignoring stored token")
            return None
        return claims

    # ── Activation ───────────────────────────────────────────────────

    async def activate(self, license_key: str) -> dict:
        device_id = self.get_device_id()
        device_label = f"Pisonex Server ({platform.node()})"

        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                f"{PISONEX_API}/api/license/activate",
                # Unsigned payload — pisonex.com's verifyRequestHmac only validates
                # _sig when present, and the local server's LICENSE_HMAC_SECRET is a
                # per-install random value that pisonex.com cannot verify (it would
                # return "Invalid request signature"). The VB client sends unsigned
                # too. Replay/abuse is already gated by license-key + device checks.
                json={
                    "license_key": license_key,
                    "device_id": device_id,
                    "device_label": device_label,
                    "device_type": "server",
                },
            )

        body = resp.json()
        if resp.status_code not in (200, 201):
            return {"success": False, "error": body.get("error", "Activation failed")}

        self._data["license_key"] = license_key
        self._data["activated_at"] = datetime.utcnow().isoformat()
        self._data["expires_at"] = body.get("expires_at")
        self._data["last_verified"] = datetime.utcnow().isoformat()
        if body.get("token"):
            self._data["license_token"] = body["token"]
        self._save()

        return {"success": True, "expires_at": body.get("expires_at")}

    async def deactivate(self) -> dict:
        key = self._data.get("license_key")
        device_id = self.get_device_id()

        if key:
            try:
                async with httpx.AsyncClient(timeout=15) as client:
                    await client.post(
                        f"{PISONEX_API}/api/license/deactivate-device",
                        json={"license_key": key, "device_id": device_id},
                    )
            except Exception as e:
                logger.warning("Remote deactivation failed: %s", e)

        first_run = self._data.get("first_run")
        device = self._data.get("device_id")
        self._data = {"first_run": first_run, "device_id": device}
        self._save()
        return {"success": True}

    async def verify(self) -> dict:
        key = self._data.get("license_key")
        if not key:
            return {"valid": False, "error": "No license key"}

        device_id = self.get_device_id()
        status_info = self.get_status()

        try:
            async with httpx.AsyncClient(timeout=15) as client:
                resp = await client.post(
                    f"{PISONEX_API}/api/license/verify",
                    json={
                        "license_key": key,
                        "device_id": device_id,
                        "device_type": "server",
                        "license_status": status_info["status"],
                        "app_version": "1.0.4",
                        "machine_name": platform.node(),
                        "trial_days_remaining": status_info["trial_days_remaining"],
                    },
                )

            try:
                body = resp.json()
            except Exception:
                body = {}

            if resp.status_code == 200 and body.get("valid"):
                # Successful verify — clear offline tracking, store new token
                self._data.pop("offline_since", None)
                self._data.pop("server_rejected", None)
                self._data["last_verified"] = datetime.utcnow().isoformat()
                self._data["expires_at"] = body.get("expires_at", self._data.get("expires_at"))
                if body.get("token"):
                    self._data["license_token"] = body["token"]
                self._save()
                return {"valid": True, "expires_at": body.get("expires_at")}
            else:
                # Server explicitly rejected (revoked, inactive, expired).
                # Clear token immediately — no offline grace for a server decision.
                self._data.pop("license_token", None)
                self._data.pop("offline_since", None)
                self._data["server_rejected"] = True
                self._save()
                logger.warning("License rejected by pisonex.com: %s", body.get("error"))
                return {"valid": False, "revoked": True, "error": body.get("error", "License rejected")}

        except (httpx.ConnectError, httpx.TimeoutException, httpx.NetworkError) as e:
            # Genuine network failure — start offline grace tracking
            if "offline_since" not in self._data:
                self._data["offline_since"] = datetime.utcnow().isoformat()
                self._save()
            logger.warning("Cannot reach pisonex.com (offline?): %s", e)
            return {"valid": False, "error": "offline"}
        except Exception as e:
            # Unexpected error — treat as offline, don't punish for server-side bugs
            logger.warning("License verification error: %s", e)
            return {"valid": False, "error": str(e)}

    # ── Verification ─────────────────────────────────────────────────

    def should_verify(self) -> bool:
        last_dt = _parse_iso_utc_naive(self._data.get("last_verified"))
        if last_dt is None:
            return True
        now = datetime.utcnow()
        # Clock-rollback defense: a last_verified in the future means the clock
        # was moved backwards — force a fresh verify (the server-side timestamp
        # check rejects if the gap is too large).
        if last_dt > now:
            return True
        return (now - last_dt) > timedelta(hours=VERIFY_INTERVAL_HOURS)

    # ── Status checks ────────────────────────────────────────────────

    def is_activated(self) -> bool:
        return bool(self._data.get("license_key") and self._data.get("activated_at"))

    def _first_run_date(self) -> datetime:
        dt = _parse_iso_utc_naive(self._data.get("first_run"))
        return dt if dt is not None else datetime.utcnow()

    def trial_days_remaining(self) -> int:
        elapsed = (datetime.utcnow() - self._first_run_date()).days
        return max(0, TRIAL_DAYS - elapsed)

    def is_trial_expired(self) -> bool:
        return self.trial_days_remaining() <= 0

    def is_license_expired(self) -> bool:
        claims = self._get_valid_token_claims()
        if claims is not None:
            exp_lic = claims.get("exp_lic")
            if exp_lic is None:
                return False  # lifetime license
            return time.time() > exp_lic

        # Fallback for installs that haven't received a token yet
        expires_at = self._data.get("expires_at")
        if not expires_at:
            return False
        try:
            exp = datetime.fromisoformat(expires_at.replace("Z", "+00:00"))
            return datetime.utcnow() > exp.replace(tzinfo=None)
        except (ValueError, AttributeError):
            return False

    def is_offline_locked(self) -> bool:
        if not self.is_activated():
            return False

        # Server explicitly rejected (revoked/inactive) — no grace period
        if self._data.get("server_rejected"):
            return True

        claims = self._get_valid_token_claims()
        if claims is not None:
            # Valid token = verified within 8h — not offline locked
            return False

        # Token missing or expired — measure how long we've been offline
        offline_since = self._data.get("offline_since")
        if offline_since:
            try:
                offline_dt = datetime.fromisoformat(offline_since)
                return (datetime.utcnow() - offline_dt) > timedelta(hours=OFFLINE_GRACE_HOURS)
            except (ValueError, AttributeError):
                pass

        # Fallback for installs without offline_since tracking yet
        last = self._data.get("last_verified")
        if not last:
            return False
        try:
            last_dt = datetime.fromisoformat(last)
            return (datetime.utcnow() - last_dt) > timedelta(hours=OFFLINE_GRACE_HOURS)
        except (ValueError, AttributeError):
            return False

    def is_active(self) -> bool:
        if self.is_activated():
            # Server explicitly rejected — no grace period
            if self._data.get("server_rejected"):
                return False

            claims = self._get_valid_token_claims()
            if claims is not None:
                if time.time() > claims["exp"]:
                    # Token expired — defer to offline grace logic
                    return not self.is_offline_locked()
                exp_lic = claims.get("exp_lic")
                if exp_lic and time.time() > exp_lic:
                    return False
                return True

            # No valid token — fall back to timestamp checks
            if self.is_license_expired():
                return False
            if self.is_offline_locked():
                return False
            return True

        return not self.is_trial_expired()

    def get_status(self) -> dict:
        if self.is_activated():
            if self.is_license_expired():
                status = LicenseStatus.EXPIRED
            elif self.is_offline_locked():
                status = LicenseStatus.OFFLINE_LOCKED
            else:
                status = LicenseStatus.ACTIVATED
        elif self.is_trial_expired():
            status = LicenseStatus.EXPIRED
        else:
            status = LicenseStatus.TRIAL

        key = self._data.get("license_key", "")
        masked_key = ""
        if key:
            parts = key.split("-")
            if len(parts) >= 5:
                masked_key = f"{parts[0]}-****-****-****-{parts[4]}"
            else:
                masked_key = key[:4] + "****"

        return {
            "status": status.value,
            "is_active": self.is_active(),
            "license_key": masked_key,
            "device_id": self.get_device_id(),
            "activated_at": self._data.get("activated_at"),
            "expires_at": self._data.get("expires_at"),
            "last_verified": self._data.get("last_verified"),
            "trial_days_remaining": self.trial_days_remaining(),
            "first_run": self._data.get("first_run"),
        }
