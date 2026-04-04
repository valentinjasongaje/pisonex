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
import servicemanager
import win32serviceutil
import win32service
import win32event
import win32evtlogutil
import asyncio
from pathlib import Path

# Add parent directory to path for imports
sys.path.insert(0, str(Path(__file__).parent))


class PisoNetService(win32serviceutil.ServiceFramework):
    """Windows Service for PisoNet Server."""

    _svc_name_ = "PisoNetServer"
    _svc_display_name_ = "Pisonex Internet Café Server"
    _svc_description_ = "Pisonex server for managing internet café sessions and hardware"

    def __init__(self, args):
        win32serviceutil.ServiceFramework.__init__(self, args)
        self.hWaitStop = win32event.CreateEvent(None, 0, 0, None)
        self.is_alive = True
        self.uvicorn_server = None

    def SvcStop(self):
        """Stop the service."""
        self.ReportServiceStatus(win32service.SERVICE_STOP_PENDING)
        win32event.SetEvent(self.hWaitStop)
        self.is_alive = False
        if self.uvicorn_server:
            asyncio.create_task(self.uvicorn_server.shutdown())

    def SvcDoRun(self):
        """Run the service."""
        servicemanager.LogMsg(
            servicemanager.EVENTLOG_INFORMATION_TYPE,
            servicemanager.PYS_SERVICE_STARTED,
            (self._svc_name_, ""),
        )

        try:
            import uvicorn
            from config import settings

            # Bind to localhost only for dashboard security
            config = uvicorn.Config(
                "main:app",
                host="127.0.0.1",  # Localhost only
                port=settings.SERVER_PORT,
                reload=False,
                workers=1,
                log_level="info",
            )
            self.uvicorn_server = uvicorn.Server(config)

            # Run asyncio event loop
            asyncio.run(self.uvicorn_server.serve())

        except Exception as e:
            servicemanager.LogErrorMsg(f"Service error: {str(e)}")
            self.SvcStop()


def handle_command_line():
    """Handle command line arguments."""
    if len(sys.argv) == 1:
        # Run as service (no args)
        servicemanager.Initialize()
        servicemanager.PrepareToHostSingle(PisoNetService)
        servicemanager.StartServiceCtrlDispatcher()
    else:
        # Handle install/remove/start/stop
        win32serviceutil.HandleCommandLine(PisoNetService)


if __name__ == "__main__":
    handle_command_line()
