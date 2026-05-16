"""
Windows Service wrapper for PisoNet Server.

Allows running the FastAPI server as a Windows Service that auto-starts on boot.

Installation:
  python windows_service.py install

Start/Stop:
  net start PisoNetServer
  net stop PisoNetServer

Remove:
  python windows_service.py remove
"""

import sys
import os
import logging
import threading
import servicemanager
import win32serviceutil
import win32service
import win32event
import win32evtlogutil
from pathlib import Path

# ── Working directory resolution ──────────────────────────────────────────────
# When frozen by PyInstaller (onedir), sys.executable is PisoNetServer.exe.
# When running as a Windows service the CWD is C:\Windows\System32 by default,
# so we must change to the folder that contains the exe / this script so that
# relative paths (dashboard/static, dashboard/templates, pisonet.db) resolve.
if getattr(sys, 'frozen', False):
    _BASE_DIR = Path(sys.executable).parent
else:
    _BASE_DIR = Path(__file__).parent

os.chdir(_BASE_DIR)
sys.path.insert(0, str(_BASE_DIR))

# In PyInstaller onedir builds, bundled assets (templates, static files) live in
# sys._MEIPASS (_internal/), NOT next to the exe.  Expose it so main.py and
# dashboard/routes.py can resolve asset paths correctly regardless of CWD.
if getattr(sys, 'frozen', False):
    _BUNDLE_DIR = Path(sys._MEIPASS)
else:
    _BUNDLE_DIR = _BASE_DIR

os.environ['PISONEX_BUNDLE_DIR'] = str(_BUNDLE_DIR)


def _ensure_env():
    """Create .env with secure defaults on first install if it doesn't exist.
    Also migrates SERVER_PORT from the old default (8000) to the current default (80)
    on existing installations that were set up before the port change.
    """
    env_path = _BASE_DIR / ".env"
    if env_path.exists():
        text = env_path.read_text(encoding="utf-8")
        if "SERVER_PORT=8000" in text:
            text = text.replace("SERVER_PORT=8000", "SERVER_PORT=80")
            env_path.write_text(text, encoding="utf-8")
        return

    import secrets
    secret_key = secrets.token_hex(32)

    env_path.write_text(
        f"# Pisonex Server Configuration — auto-generated on first run\n"
        f"# Edit this file to change settings, then restart the service.\n\n"
        f"DATABASE_URL=sqlite:///{(_BASE_DIR / 'pisonet.db').as_posix()}\n"
        f"SECRET_KEY={secret_key}\n"
        f"TOKEN_EXPIRE_HOURS=8\n"
        f"SERVER_HOST=0.0.0.0\n"
        f"SERVER_PORT=80\n\n"
        f"DEFAULT_RATE_PESOS=5\n"
        f"DEFAULT_RATE_SECONDS=1800\n\n"
        f"PC_HEARTBEAT_TIMEOUT=30\n\n"
        f"ADMIN_USERNAME=admin\n"
        f"ADMIN_PASSWORD=admin123\n\n"
        f"CLIENT_API_KEY=\n\n"
        f"MEMBERSHIP_ENABLED=false\n"
        f"ABSORPTION_ENABLED=false\n"
        f"LOGOUT_DEDUCTION_MINUTES=5\n"
        f"MINIMUM_LOGOUT_MINUTES=10\n"
        f"ZERO_TIME_AUTO_LOGOUT_SECONDS=30\n"
        f"IDLE_AUTO_SHUTDOWN_MINUTES=5\n"
        f"MEMBER_HEARTBEAT_TIMEOUT_MINUTES=60\n",
        encoding="utf-8",
    )


# Generate .env before any app imports so pydantic-settings picks it up
_ensure_env()


class PisoNetService(win32serviceutil.ServiceFramework):
    """Windows Service for PisoNet Server."""

    _svc_name_ = "PisonexServer"
    _svc_display_name_ = "Pisonex Server"
    _svc_description_ = "Pisonex internet café server — manages sessions, billing, and hardware"

    def __init__(self, args):
        win32serviceutil.ServiceFramework.__init__(self, args)
        self.hWaitStop = win32event.CreateEvent(None, 0, 0, None)
        self._uvicorn_server = None

    def SvcStop(self):
        """Called by SCM to stop the service."""
        self.ReportServiceStatus(win32service.SERVICE_STOP_PENDING)
        # Signal uvicorn to exit
        if self._uvicorn_server:
            self._uvicorn_server.should_exit = True
        # Unblock SvcDoRun
        win32event.SetEvent(self.hWaitStop)

    def SvcDoRun(self):
        """Main service entry point — runs on the SCM service thread."""
        servicemanager.LogMsg(
            servicemanager.EVENTLOG_INFORMATION_TYPE,
            servicemanager.PYS_SERVICE_STARTED,
            (self._svc_name_, ""),
        )

        # Start uvicorn in a background thread so this thread can block on
        # hWaitStop. asyncio.run() inside a Windows service thread exits
        # immediately without this pattern.
        server_thread = threading.Thread(target=self._run_uvicorn, daemon=True)
        server_thread.start()

        # Block until SvcStop signals us
        win32event.WaitForSingleObject(self.hWaitStop, win32event.INFINITE)

        # Wait briefly for uvicorn to shut down cleanly
        server_thread.join(timeout=10)

    def _run_uvicorn(self):
        """Runs in a background thread — starts the FastAPI/uvicorn server."""
        try:
            import asyncio
            import uvicorn
            from config import settings
            from main import app

            # Windows services have no console — sys.stdout/stderr are None.
            # Redirect them to the log file so uvicorn's logger doesn't crash
            # calling isatty() on NoneType.
            log_path = _BASE_DIR / "pisonet.log"
            _log_file = open(log_path, "a", encoding="utf-8", buffering=1)
            sys.stdout = _log_file
            sys.stderr = _log_file

            # Use SelectorEventLoop — ProactorEventLoop can cause issues in
            # Windows service threads.
            asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

            config = uvicorn.Config(
                app,
                host="0.0.0.0",
                port=settings.SERVER_PORT,
                reload=False,
                log_level="info",
                log_config=None,  # disable uvicorn's color formatter (requires TTY)
            )
            self._uvicorn_server = uvicorn.Server(config)
            asyncio.run(self._uvicorn_server.serve())

        except Exception as e:
            import traceback
            try:
                error_log = _BASE_DIR / "service_error.log"
                with open(error_log, "w", encoding="utf-8") as f:
                    f.write(traceback.format_exc())
            except Exception:
                pass
            servicemanager.LogErrorMsg(f"Pisonex service error: {str(e)}")
            win32event.SetEvent(self.hWaitStop)


def handle_command_line():
    """Handle command line arguments."""
    if len(sys.argv) == 1:
        # No args: try to connect as a service started by SCM.
        # If run directly from a terminal this will fail with error 1063/1061.
        try:
            servicemanager.Initialize()
            servicemanager.PrepareToHostSingle(PisoNetService)
            servicemanager.StartServiceCtrlDispatcher()
        except win32service.error as exc:
            if exc.winerror in (1061, 1063):
                exe = Path(sys.executable).name if getattr(sys, 'frozen', False) else "python windows_service.py"
                print(
                    f"\nERROR: This must be run via the Windows Service Control Manager.\n"
                    f"\nUsage (run as Administrator):\n"
                    f"  {exe} install   -- register the service\n"
                    f"  {exe} start     -- start the service\n"
                    f"  {exe} stop      -- stop the service\n"
                    f"  {exe} remove    -- unregister the service\n"
                    f"  {exe} debug     -- run interactively (console mode)\n"
                )
                sys.exit(1)
            raise
    else:
        # Handle install/remove/start/stop/debug
        win32serviceutil.HandleCommandLine(PisoNetService)


if __name__ == "__main__":
    handle_command_line()
