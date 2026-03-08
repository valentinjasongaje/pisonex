import asyncio
import logging
import logging.handlers
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import RedirectResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from fastapi.middleware.cors import CORSMiddleware

from config import settings
from database import engine, Base, SessionLocal
from models import AdminUser, CoinRate
from api import auth, pc, sessions, admin
from dashboard.routes import router as dashboard_router
from api.auth import hash_password
from services.session_service import SessionService

# ── Logging setup ─────────────────────────────────────────────────────────────

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    handlers=[
        logging.StreamHandler(),
        logging.handlers.RotatingFileHandler(
            "pisonet.log",
            maxBytes=5 * 1024 * 1024,  # 5 MB
            backupCount=3,
        ),
    ],
)
logger = logging.getLogger(__name__)

# ── App lifespan (startup / shutdown) ─────────────────────────────────────────

hw_controller = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global hw_controller

    # Create all DB tables
    Base.metadata.create_all(bind=engine)

    db = SessionLocal()
    try:
        _seed_defaults(db)
    finally:
        db.close()

    # Start hardware controller (only works on actual Raspberry Pi)
    try:
        db = SessionLocal()
        svc = SessionService(db)
        from hardware.controller import HardwareController
        hw_controller = HardwareController(svc)
        logger.info("Hardware controller started")
    except Exception as e:
        logger.warning("Hardware controller not started: %s", e)
        hw_controller = None

    # Background task: expire sessions every 30 seconds
    expire_task = asyncio.create_task(_session_expiry_loop())

    yield

    # Shutdown
    expire_task.cancel()
    if hw_controller:
        hw_controller.cleanup()
    logger.info("PisoNet server shut down")


def _seed_defaults(db):
    """Insert default admin user and coin rate on first run."""
    if not db.query(AdminUser).first():
        admin_user = AdminUser(
            username=settings.ADMIN_USERNAME,
            password=hash_password(settings.ADMIN_PASSWORD),
        )
        db.add(admin_user)
        logger.info("Created default admin user: %s", settings.ADMIN_USERNAME)

    if not db.query(CoinRate).first():
        rate = CoinRate(
            pesos=settings.DEFAULT_RATE_PESOS,
            minutes=settings.DEFAULT_RATE_MINUTES,
            label=f"₱{settings.DEFAULT_RATE_PESOS} = {settings.DEFAULT_RATE_MINUTES} minutes",
        )
        db.add(rate)
        logger.info(
            "Created default coin rate: ₱%d = %d min",
            settings.DEFAULT_RATE_PESOS,
            settings.DEFAULT_RATE_MINUTES,
        )

    db.commit()


async def _session_expiry_loop():
    """Periodically expire sessions that have run out of time."""
    while True:
        await asyncio.sleep(30)
        try:
            db = SessionLocal()
            try:
                svc = SessionService(db)
                svc.expire_sessions()
            finally:
                db.close()
        except Exception as e:
            logger.error("Session expiry error: %s", e)


# ── FastAPI app ───────────────────────────────────────────────────────────────

app = FastAPI(
    title="PisoNet Server",
    version="1.0.0",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],   # Restrict to LAN subnet in production
    allow_methods=["*"],
    allow_headers=["*"],
)

# ── API routers ───────────────────────────────────────────────────────────────

app.include_router(auth.router)
app.include_router(pc.router)
app.include_router(sessions.router)
app.include_router(admin.router)
app.include_router(dashboard_router)

# ── Static files ───────────────────────────────────────────────────────────────

app.mount("/static", StaticFiles(directory="dashboard/static"), name="static")


@app.get("/")
def root():
    return RedirectResponse("/dashboard")


# ── Health check ──────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    return {"status": "ok", "version": "1.0.0"}


# ── Dev entry point ───────────────────────────────────────────────────────────

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(
        "main:app",
        host=settings.SERVER_HOST,
        port=settings.SERVER_PORT,
        reload=False,
        workers=1,
    )
