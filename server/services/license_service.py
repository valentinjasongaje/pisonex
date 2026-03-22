import hashlib
import json
import logging
import os
import platform
import uuid
from datetime import datetime, timedelta
from enum import Enum
from pathlib import Path
from typing import Optional

import httpx

logger = logging.getLogger(__name__)

PISONEX_API = "https://www.pisonex.com"
LICENSE_FILE = Path(__file__).resolve().parent.parent / "data" / "license.json"
TRIAL_DAYS = 14
OFFLINE_GRACE_HOURS = 72  # 3 days
VERIFY_INTERVAL_HOURS = 6
BETA_CHECK_INTERVAL_HOURS = 1  # how often to re-fetch beta flag


class LicenseStatus(str, Enum):
    ACTIVATED = "activated"
    TRIAL = "trial"
    EXPIRED = "expired"
    OFFLINE_LOCKED = "offline_locked"


class LicenseService:
    def __init__(self):
        self._data: dict = {}
        self._beta_mode: bool = False  # default: licensing enforced until first successful fetch
        self._beta_last_checked: Optional[datetime] = None
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
        self._load_cached_beta()

    def _save(self):
        LICENSE_FILE.parent.mkdir(parents=True, exist_ok=True)
        LICENSE_FILE.write_text(json.dumps(self._data, indent=2))

    # ── Beta mode (fetched from pisonex.com) ─────────────────────────

    @property
    def beta_mode(self) -> bool:
        return self._beta_mode

    async def fetch_beta_status(self) -> bool:
        """Fetch beta flag from pisonex.com and cache locally."""
        try:
            async with httpx.AsyncClient(timeout=10) as client:
                resp = await client.get(f"{PISONEX_API}/api/status")
            if resp.status_code == 200:
                data = resp.json()
                self._beta_mode = bool(data.get("beta", False))
                self._beta_last_checked = datetime.utcnow()
                self._data["beta_mode"] = self._beta_mode
                self._data["beta_last_checked"] = self._beta_last_checked.isoformat()
                self._save()
                logger.info("Beta status fetched: %s", self._beta_mode)
        except Exception as e:
            logger.warning("Failed to fetch beta status: %s", e)
            # Fall back to cached value
            if "beta_mode" in self._data:
                self._beta_mode = self._data["beta_mode"]
        return self._beta_mode

    def _load_cached_beta(self):
        """Load beta flag from cached data on startup."""
        if "beta_mode" in self._data:
            self._beta_mode = self._data["beta_mode"]
        if "beta_last_checked" in self._data:
            try:
                self._beta_last_checked = datetime.fromisoformat(self._data["beta_last_checked"])
            except (ValueError, TypeError):
                pass

    def should_refresh_beta(self) -> bool:
        """True if enough time has passed to re-fetch beta status."""
        if self._beta_last_checked is None:
            return True
        return (datetime.utcnow() - self._beta_last_checked) > timedelta(hours=BETA_CHECK_INTERVAL_HOURS)

    # ── Device ID ────────────────────────────────────────────────────

    def get_device_id(self) -> str:
        cached = self._data.get("device_id")
        if cached:
            return cached

        raw = f"{platform.node()}|{uuid.getnode()}|{platform.machine()}|{platform.processor()}"
        device_id = hashlib.sha256(raw.encode()).hexdigest()
        self._data["device_id"] = device_id
        self._save()
        return device_id

    # ── Activation ───────────────────────────────────────────────────

    async def activate(self, license_key: str) -> dict:
        device_id = self.get_device_id()
        device_label = f"PisoNet Server ({platform.node()})"

        async with httpx.AsyncClient(timeout=15) as client:
            resp = await client.post(
                f"{PISONEX_API}/api/license/activate",
                json={
                    "license_key": license_key,
                    "device_id": device_id,
                    "device_label": device_label,
                },
            )

        body = resp.json()
        if resp.status_code not in (200, 201):
            return {"success": False, "error": body.get("error", "Activation failed")}

        self._data["license_key"] = license_key
        self._data["activated_at"] = datetime.utcnow().isoformat()
        self._data["expires_at"] = body.get("expires_at")
        self._data["last_verified"] = datetime.utcnow().isoformat()
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

        # Clear local data but keep first_run
        first_run = self._data.get("first_run")
        device = self._data.get("device_id")
        self._data = {"first_run": first_run, "device_id": device}
        self._save()
        return {"success": True}

    # ── Verification ─────────────────────────────────────────────────

    async def verify(self) -> dict:
        key = self._data.get("license_key")
        if not key:
            return {"valid": False, "error": "No license key"}

        device_id = self.get_device_id()

        try:
            async with httpx.AsyncClient(timeout=15) as client:
                resp = await client.post(
                    f"{PISONEX_API}/api/license/verify",
                    json={"license_key": key, "device_id": device_id},
                )

            body = resp.json()
            if resp.status_code == 200 and body.get("valid"):
                self._data["last_verified"] = datetime.utcnow().isoformat()
                self._data["expires_at"] = body.get("expires_at", self._data.get("expires_at"))
                self._save()
                return {"valid": True, "expires_at": body.get("expires_at")}
            else:
                return {"valid": False, "error": body.get("error", "Verification failed")}
        except Exception as e:
            logger.warning("License verification failed (offline?): %s", e)
            return {"valid": False, "error": str(e)}

    def should_verify(self) -> bool:
        """True if enough time has passed since last verification."""
        last = self._data.get("last_verified")
        if not last:
            return True
        try:
            last_dt = datetime.fromisoformat(last)
            return (datetime.utcnow() - last_dt) > timedelta(hours=VERIFY_INTERVAL_HOURS)
        except (ValueError, TypeError):
            return True

    # ── Status checks ────────────────────────────────────────────────

    def is_activated(self) -> bool:
        return bool(self._data.get("license_key") and self._data.get("activated_at"))

    def _first_run_date(self) -> datetime:
        try:
            return datetime.fromisoformat(self._data["first_run"])
        except (KeyError, ValueError):
            return datetime.utcnow()

    def trial_days_remaining(self) -> int:
        elapsed = (datetime.utcnow() - self._first_run_date()).days
        return max(0, TRIAL_DAYS - elapsed)

    def is_trial_expired(self) -> bool:
        return self.trial_days_remaining() <= 0

    def is_license_expired(self) -> bool:
        expires_at = self._data.get("expires_at")
        if not expires_at:
            return False  # lifetime license
        try:
            exp = datetime.fromisoformat(expires_at.replace("Z", "+00:00"))
            return datetime.utcnow() > exp.replace(tzinfo=None)
        except (ValueError, AttributeError):
            return False

    def is_offline_locked(self) -> bool:
        if not self.is_activated():
            return False
        last = self._data.get("last_verified")
        if not last:
            return False
        try:
            last_dt = datetime.fromisoformat(last)
            return (datetime.utcnow() - last_dt) > timedelta(hours=OFFLINE_GRACE_HOURS)
        except (ValueError, AttributeError):
            return False

    def is_active(self) -> bool:
        if self._beta_mode:
            return True
        if self.is_activated():
            if self.is_license_expired():
                return False
            if self.is_offline_locked():
                return False
            return True
        return not self.is_trial_expired()

    def get_status(self) -> dict:
        if self._beta_mode:
            return {
                "status": "beta",
                "is_active": True,
                "license_key": "",
                "device_id": self.get_device_id(),
                "activated_at": None,
                "expires_at": None,
                "last_verified": None,
                "trial_days_remaining": self.trial_days_remaining(),
                "first_run": self._data.get("first_run"),
                "beta_mode": True,
            }

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
