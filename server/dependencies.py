from fastapi import Header, HTTPException
from config import settings


def verify_client_key(x_api_key: str = Header(default="")):
    """Validate the shared API key sent by PC clients in the X-API-Key header.

    Auth is skipped when CLIENT_API_KEY is empty (default) so existing
    deployments continue to work without any configuration change.
    """
    if not settings.CLIENT_API_KEY:
        return  # auth disabled
    if x_api_key != settings.CLIENT_API_KEY:
        raise HTTPException(status_code=401, detail="Invalid or missing API key")
