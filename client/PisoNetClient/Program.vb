Imports System.Windows.Forms
Imports Microsoft.Win32
Imports PisoNetClient.Config
Imports PisoNetClient.Services
Imports PisoNetClient.Forms

Module Program

    Private _api     As ApiService
    Private _lockMgr As LockManager
    Private _session As SessionManager
    Private _overlay As TimerOverlay

    <STAThread>
    Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        ' Register for auto-start on Windows boot
        RegisterStartup()

        ' ── Create all services on the UI (STA) thread ────────────────
        ' LockManager creates the LockForm here — must be on UI thread
        _api     = New ApiService(AppConfig.ServerUrl, AppConfig.PCNumber)
        _lockMgr = New LockManager()
        _session = New SessionManager(_api, _lockMgr)
        _overlay = New TimerOverlay()
        ' Force window handle creation on this UI thread NOW, before the
        ' heartbeat timers start.  Without this, InvokeRequired returns False
        ' on background threads (no handle = no thread affinity), so Show()
        ' gets called on the wrong thread and the overlay never appears.
        Dim _forceHandle = _overlay.Handle

        ' ── Wire up events ────────────────────────────────────────────
        AddHandler _session.TimeUpdated,           AddressOf OnTimeUpdated
        AddHandler _session.SessionStarted,        AddressOf OnSessionStarted
        AddHandler _session.SessionEnded,          AddressOf OnSessionEnded
        AddHandler _session.ServerConnectionLost,  AddressOf OnConnectionLost
        AddHandler _session.ServerConnectionRestored, AddressOf OnConnectionRestored

        ' ── Register PC with server (fire and forget) ─────────────────
        Task.Run(Async Function()
            Await _api.RegisterAsync()
        End Function)

        ' ── Start heartbeat + local countdown ────────────────────────
        _session.Start()

        ' ── Show lock screen and start message loop ───────────────────
        ' LockForm was created on this thread so Show() is safe here.
        ' Application.Run() keeps the message loop alive until the
        ' process exits — it does NOT block on the lock form alone.
        _lockMgr.LockPC()
        Application.Run()
    End Sub

    ' ── Event handlers (called from background threads via Invoke) ────

    Private Sub OnTimeUpdated(minutes As Integer, seconds As Integer)
        _overlay.UpdateTime(minutes, seconds)
    End Sub

    Private Sub OnSessionStarted()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnSessionStarted())
            Return
        End If
        If Not _overlay.Visible Then _overlay.Show()
    End Sub

    Private Sub OnSessionEnded()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnSessionEnded())
            Return
        End If
        _overlay.Hide()
    End Sub

    Private Sub OnConnectionLost()
        _overlay.ShowOffline()
    End Sub

    Private Sub OnConnectionRestored()
        _overlay.ShowConnected()
    End Sub

    ' ── Windows startup registration ──────────────────────────────────

    Private Sub RegisterStartup()
        Try
            Dim key = Registry.CurrentUser.OpenSubKey(
                "SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)
            Dim exePath = Application.ExecutablePath
            key?.SetValue("PisoNetClient", $"""{exePath}""")
        Catch
            ' Non-fatal — app still works, just won't auto-start next boot
        End Try
    End Sub

End Module
