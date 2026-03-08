Imports System.Windows.Forms
Imports Microsoft.Win32
Imports PisoNetClient.Config
Imports PisoNetClient.Services
Imports PisoNetClient.Forms

Module Program

    Private _api As ApiService
    Private _lockMgr As LockManager
    Private _session As SessionManager
    Private _overlay As TimerOverlay

    <STAThread>
    Sub Main()
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        ' Register for auto-start on Windows boot
        RegisterStartup()

        ' Initialize services
        _api = New ApiService(AppConfig.ServerUrl, AppConfig.PCNumber)
        _lockMgr = New LockManager()
        _session = New SessionManager(_api, _lockMgr)
        _overlay = New TimerOverlay()

        ' Wire up events
        AddHandler _session.TimeUpdated, AddressOf OnTimeUpdated
        AddHandler _session.SessionStarted, AddressOf OnSessionStarted
        AddHandler _session.SessionEnded, AddressOf OnSessionEnded
        AddHandler _session.ServerConnectionLost, AddressOf OnConnectionLost
        AddHandler _session.ServerConnectionRestored, AddressOf OnConnectionRestored

        ' Register this PC with the server (fire and forget)
        Task.Run(Async Function()
            Await _api.RegisterAsync()
        End Function)

        ' Start polling
        _session.Start()

        ' Lock on startup — server will unlock when it responds
        _lockMgr.LockPC()

        ' Keep the app alive (message loop)
        Application.Run()
    End Sub

    ' ── Event handlers ────────────────────────────────────────────────

    Private Sub OnTimeUpdated(minutes As Integer, seconds As Integer)
        _overlay.UpdateTime(minutes, seconds)
    End Sub

    Private Sub OnSessionStarted()
        If Not _overlay.Visible Then
            _overlay.Show()
        End If
    End Sub

    Private Sub OnSessionEnded()
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
                "SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True
            )
            Dim exePath = Application.ExecutablePath
            key?.SetValue("PisoNetClient", $"""{exePath}""")
        Catch ex As Exception
            ' Non-fatal: app will still work, just won't auto-start
        End Try
    End Sub

End Module
