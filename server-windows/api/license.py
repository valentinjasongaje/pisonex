from fastapi import APIRouter, Depends, HTTPException, Cookie
from pydantic import BaseModel
from typing import Optional
from jose import JWTError, jwt

from config import settings

router = APIRouter(prefix="/api/license", tags=["license"])

_ALGORITHM = "HS256"


def _require_admin(pisonet_session: str = Cookie(default=None)):
    """Require a valid admin session cookie."""
    if not pisonet_session:
        raise HTTPException(401, "Authentication required")
    try:
        payload = jwt.decode(pisonet_session, settings.SECRET_KEY, algorithms=[_ALGORITHM])
        if not payload.get("sub"):
            raise HTTPException(401, "Invalid session")
    except JWTError:
        raise HTTPException(401, "Invalid session")


class ActivateRequest(BaseModel):
    license_key: str


@router.post("/activate", dependencies=[Depends(_require_admin)])
async def activate_license(body: ActivateRequest):
    from main import license_service
    result = await license_service.activate(body.license_key)
    if not result.get("success"):
        raise HTTPException(400, result.get("error", "Activation failed"))
    return result


@router.get("/status")
def get_license_status():
    from main import license_service
    return license_service.get_status()


@router.post("/deactivate", dependencies=[Depends(_require_admin)])
async def deactivate_license():
    from main import license_service
    return await license_service.deactivate()
