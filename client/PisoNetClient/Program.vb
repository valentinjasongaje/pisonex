Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms
Imports Microsoft.Win32
Imports PisoNetClient.Config
Imports PisoNetClient.Services
Imports PisoNetClient.Forms

Module Program

    Private _api         As ApiService
    Private _lockMgr     As LockManager
    Private _session     As SessionManager
    Private _overlay     As TimerOverlay
    Private _tray        As SystemTray
    Private _capture     As ScreenCaptureService
    Private _notifs      As NotificationService
    Private _guardTimer  As System.Timers.Timer   ' mutual watchdog keeper

    <STAThread>
    Sub Main()
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        ' ── First-run setup ───────────────────────────────────────────────
        If Not AppConfig.IsConfigured Then
            Dim setup = New SetupDialog()
            If setup.ShowDialog() <> DialogResult.OK Then
                ' User closed setup without saving — cannot run
                Application.Exit()
                Return
            End If
        End If

        RegisterStartup()

        ' ── Register exe path + spawn the watchdog guardian ───────────────
        AppConfig.SaveClientExePath(Application.ExecutablePath)
        SpawnGuard()

        ' Mutual watchdog: keep the guard process alive every 30 s
        _guardTimer = New System.Timers.Timer(30_000)
        AddHandler _guardTimer.Elapsed, Sub(s, e) EnsureGuardRunning()
        _guardTimer.AutoReset = True
        _guardTimer.Start()

        ' ── Apply Windows restrictions ────────────────────────────────────
        WindowsPolicy.Apply()

        ' Ensure restrictions are removed even if the process crashes
        AddHandler Application.ApplicationExit, Sub(s, e) WindowsPolicy.RemoveAll()

        ' ── Create all UI objects on the STA thread ───────────────────────
        _api     = New ApiService(AppConfig.ServerUrl, AppConfig.PCNumber)
        _lockMgr = New LockManager()
        _session = New SessionManager(_api, _lockMgr)
        _overlay = New TimerOverlay()
        _tray    = New SystemTray()
        _notifs  = New NotificationService(_overlay)

        ' Force handle creation so InvokeRequired works on background threads
        Dim _fh = _overlay.Handle

        ' ── Wire up events ────────────────────────────────────────────────
        AddHandler _session.TimeUpdated,              AddressOf OnTimeUpdated
        AddHandler _session.SessionStarted,           AddressOf OnSessionStarted
        AddHandler _session.SessionEnded,             AddressOf OnSessionEnded
        AddHandler _session.ServerConnectionLost,     AddressOf OnConnectionLost
        AddHandler _session.ServerConnectionRestored, AddressOf OnConnectionRestored
        AddHandler _session.LowTimeWarning,           AddressOf OnLowTimeWarning
        AddHandler _session.TimeAdded,                AddressOf OnTimeAdded

        ' Admin panel from lock form shortcut and from tray menu
        AddHandler _lockMgr.LockFormAdminRequested, AddressOf OnAdminPanelRequested
        AddHandler _tray.AdminPanelRequested,        AddressOf OnAdminPanelRequested

        ' ── Register PC with server ────────────────────────────────────────
        Task.Run(Async Function()
            Await _api.RegisterAsync()
        End Function)

        ' ── Start heartbeat + local countdown ────────────────────────────
        _session.Start()

        ' ── Start screen capture for remote monitoring ────────────────────
        _capture = New ScreenCaptureService(_api, _session)
        _capture.Start()

        ' ── Lock and enter message loop ───────────────────────────────────
        _lockMgr.LockPC()
        Application.Run()
    End Sub

    ' ── Session event handlers ────────────────────────────────────────────

    Private Sub OnTimeUpdated(minutes As Integer, seconds As Integer)
        _overlay.UpdateTime(minutes, seconds)
        _tray.UpdateStatus($"PisoNet — {minutes:D2}:{seconds:D2} remaining")
    End Sub

    Private Sub OnSessionStarted()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnSessionStarted())
            Return
        End If
        If Not _overlay.Visible Then _overlay.Show()
        _tray.UpdateStatus("PisoNet — Session active")
    End Sub

    Private Sub OnSessionEnded()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnSessionEnded())
            Return
        End If
        _overlay.Hide()
        _tray.UpdateStatus("PisoNet — Waiting for coins")
    End Sub

    Private Sub OnConnectionLost()
        _overlay.ShowOffline()
        _lockMgr.ShowOfflineStatus()
        _tray.UpdateStatus("PisoNet — Server offline")
    End Sub

    Private Sub OnConnectionRestored()
        _overlay.ShowConnected()
        _lockMgr.HideOfflineStatus()
    End Sub

    Private Sub OnLowTimeWarning(minutesLeft As Integer)
        If Not AppConfig.WarnAt5Min AndAlso minutesLeft = 5 Then Return
        If Not AppConfig.WarnAt1Min AndAlso minutesLeft = 1 Then Return
        _notifs.Show(
            $"{minutesLeft} Minute{If(minutesLeft = 1, "", "s")} Left",
            $"Your session will end in {minutesLeft} minute{If(minutesLeft = 1, "", "s")}. Insert more coins to continue.",
            ToastType.Warning)
    End Sub

    Private Sub OnTimeAdded(minutes As Integer)
        _notifs.Show(
            $"+{minutes} Minute{If(minutes = 1, "", "s")} Added",
            $"{minutes} minute{If(minutes = 1, "", "s")} {If(minutes = 1, "has", "have")} been added to your session.",
            ToastType.Success)
    End Sub

    ' ── Admin panel flow ──────────────────────────────────────────────────

    Private Sub OnAdminPanelRequested()
        ' Must run on the UI thread
        If Not _overlay.IsHandleCreated Then Return
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnAdminPanelRequested())
            Return
        End If

        ' Ask for PIN
        Dim enteredPin = AskForPin()
        If enteredPin Is Nothing OrElse enteredPin <> AppConfig.AdminPin Then
            If enteredPin IsNot Nothing Then
                MessageBox.Show("Incorrect PIN.", "Admin Access",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If
            Return
        End If

        Dim panel = New AdminPanel()
        AddHandler panel.ExitRequested, AddressOf ExitApplication
        panel.ShowDialog()

        ' After admin closes panel, refresh the lock screen in case settings changed
        _lockMgr.RefreshLockAppearance()
    End Sub

    Private Sub ExitApplication()
        ' Tell the watchdog not to restart for ~5 minutes (admin intentional exit)
        AppConfig.SaveGracefulShutdown()
        _guardTimer?.Stop()
        _guardTimer?.Dispose()
        WindowsPolicy.RemoveAll()
        _capture?.Dispose()
        _tray?.Dispose()
        _lockMgr.AllowExit()
        Application.Exit()
    End Sub

    ' ── PIN input dialog ──────────────────────────────────────────────────

    Private Function AskForPin() As String
        Dim dlg = New Form() With {
            .Text            = "Admin Access",
            .Size            = New Size(300, 160),
            .StartPosition   = FormStartPosition.CenterScreen,
            .FormBorderStyle = FormBorderStyle.FixedDialog,
            .MaximizeBox     = False, .MinimizeBox = False,
            .TopMost         = True,
            .BackColor       = Color.FromArgb(15, 20, 35),
            .ForeColor       = Color.White
        }

        Dim lbl = New Label() With {
            .Text = "Enter Admin PIN:", .AutoSize = True,
            .Location = New Point(16, 16), .ForeColor = Color.White
        }

        Dim txt = New TextBox() With {
            .PasswordChar = "●"c, .MaxLength = 8,
            .Location = New Point(16, 40), .Width = 252,
            .BackColor = Color.FromArgb(26, 30, 45), .ForeColor = Color.White,
            .BorderStyle = BorderStyle.FixedSingle
        }

        Dim btn = New Button() With {
            .Text = "OK", .DialogResult = DialogResult.OK,
            .Location = New Point(16, 76), .Width = 80,
            .BackColor = Color.FromArgb(79, 142, 247), .ForeColor = Color.White,
            .FlatStyle = FlatStyle.Flat
        }
        btn.FlatAppearance.BorderSize = 0

        dlg.Controls.AddRange({lbl, txt, btn})
        dlg.AcceptButton = btn

        If dlg.ShowDialog() = DialogResult.OK Then Return txt.Text
        Return Nothing
    End Function

    ' ── Watchdog guard (mutual watcher) ───────────────────────────────────

    ''' <summary>
    ''' Starts PisoNetWatchdog.exe as a companion process if it is not already
    ''' running (either as a Windows Service or as a standalone process).
    ''' The watchdog is placed next to PisoNetClient.exe in the same directory.
    ''' </summary>
    Private Sub SpawnGuard()
        If Process.GetProcessesByName("PisoNetWatchdog").Length > 0 Then Return

        Dim watchdogExe = Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "PisoNetWatchdog.exe")
        If Not File.Exists(watchdogExe) Then Return

        Try
            Process.Start(New ProcessStartInfo(watchdogExe) With {
                .UseShellExecute = False,
                .CreateNoWindow  = True
            })
        Catch
            ' Watchdog not available — continue without it
        End Try
    End Sub

    ''' <summary>Called every 30 s to restart the watchdog if someone killed it.</summary>
    Private Sub EnsureGuardRunning()
        If Process.GetProcessesByName("PisoNetWatchdog").Length > 0 Then Return
        SpawnGuard()
    End Sub

    ' ── Windows startup registration ──────────────────────────────────────

    Private Sub RegisterStartup()
        Try
            Dim key = Registry.CurrentUser.OpenSubKey(
                "SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)
            key?.SetValue("PisoNetClient", $"""{Application.ExecutablePath}""")
        Catch
        End Try
    End Sub

End Module
