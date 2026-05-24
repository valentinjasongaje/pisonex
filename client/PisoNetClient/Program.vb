Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms
Imports Microsoft.Win32
Imports PisoNetClient.Config
Imports PisoNetClient.Services
Imports PisoNetClient.Forms

Module Program

    Private _singleInstanceMutex As System.Threading.Mutex
    Private _api As ApiService
    Private _memberSvc As MemberService
    Private _lockMgr As LockManager
    Private _session As SessionManager
    Private _overlay As TimerOverlay
    Private _tray As SystemTray
    Private _capture As ScreenCaptureService
    Private _metrics As MetricsService
    Private _notifs As NotificationService
    Private _guardTimer As System.Timers.Timer   ' mutual watchdog keeper

    <STAThread>
    Sub Main()
        ' ── Single-instance guard ─────────────────────────────────────────────
        ' Prevents a second copy from starting when both the Run-key entry and the
        ' pnxsystem watchdog service fire at boot before the process is visible in
        ' the process list. The mutex is Global\ so it is visible across sessions.
        Dim ownsMutex As Boolean = False
        _singleInstanceMutex = New System.Threading.Mutex(
            False, "Global\PisoNetClient_SingleInstance")
        Try
            ownsMutex = _singleInstanceMutex.WaitOne(0, False)
        Catch ex As System.Threading.AbandonedMutexException
            ' Previous instance crashed without releasing — we now own it
            ownsMutex = True
        End Try
        If Not ownsMutex Then
            _singleInstanceMutex.Dispose()
            Return   ' Another instance is already running — exit silently
        End If

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

        ' ── License initialization ────────────────────────────────────────
        ' SyncStartupStatusAsync runs first: it restores LicenseFirstRunDate from
        ' pisonex.com (first_seen_at) if license.dat was deleted. EnsureFirstRunDate
        ' then only writes a fresh date if the server had no record either.
        LicenseService.SyncStartupStatusAsync().GetAwaiter().GetResult()
        LicenseService.EnsureFirstRunDate()
        LicenseService.StartVerificationTimer()

        ' Periodic license enforcement — catches expiry during active sessions
        Dim licenseEnforcementTimer = New System.Timers.Timer(10 * 60 * 1000) ' every 10 min
        AddHandler licenseEnforcementTimer.Elapsed, Sub(s, e)
            If Not LicenseService.IsActive() Then
                _lockMgr.LockPC()
                CheckLicenseStatus()
            End If
        End Sub
        licenseEnforcementTimer.AutoReset = True
        licenseEnforcementTimer.Start()

        ' ── Apply Windows restrictions ────────────────────────────────────
        WindowsPolicy.Apply()

        ' Ensure restrictions are removed even if the process crashes
        AddHandler Application.ApplicationExit, Sub(s, e) WindowsPolicy.RemoveAll()

        ' ── Create all UI objects on the STA thread ───────────────────────
        _api = New ApiService(AppConfig.ServerUrl, AppConfig.PCNumber)
        _memberSvc = New MemberService(AppConfig.ServerUrl)
        _lockMgr = New LockManager()
        _session = New SessionManager(_api, _lockMgr, Function() LicenseService.IsActive())
        _overlay = New TimerOverlay()
        _tray = New SystemTray()
        _notifs = New NotificationService(_overlay)

        ' Force handle creation so InvokeRequired works on background threads
        Dim _fh = _overlay.Handle

        ' ── Wire up events ────────────────────────────────────────────────
        AddHandler _session.TimeUpdated, AddressOf OnTimeUpdated
        AddHandler _session.SessionStarted, AddressOf OnSessionStarted
        AddHandler _session.SessionEnded, AddressOf OnSessionEnded
        AddHandler _session.ServerConnectionLost, AddressOf OnConnectionLost
        AddHandler _session.ServerConnectionRestored, AddressOf OnConnectionRestored
        AddHandler _session.LowTimeWarning, AddressOf OnLowTimeWarning
        AddHandler _session.TimeAdded, AddressOf OnTimeAdded
        AddHandler _session.MessageReceived, AddressOf OnMessageReceived
        AddHandler _session.AnnouncementChanged, AddressOf OnAnnouncementChanged
        AddHandler _session.CommandReceived, AddressOf OnCommandReceived
        AddHandler _session.WallpaperChanged, AddressOf OnWallpaperChanged
        AddHandler _session.ReceivingCoinsChanged, AddressOf OnReceivingCoinsChanged
        AddHandler _session.CoinSlotChanged, AddressOf OnCoinSlotChanged
        AddHandler _session.MembershipUpdated, AddressOf OnMembershipUpdated

        ' Insert Coin event from lock form
        AddHandler _lockMgr.LockFormInsertCoinRequested, AddressOf OnInsertCoinRequested

        ' Membership events from lock form and overlay
        AddHandler _lockMgr.LockFormLoginRequested, AddressOf OnMemberLogin
        AddHandler _lockMgr.LockFormRegisterRequested, AddressOf OnMemberRegister
        AddHandler _lockMgr.LockFormLogoutRequested, AddressOf OnMemberLogout
        AddHandler _overlay.MemberLogoutRequested, AddressOf OnMemberLogoutFromOverlay

        ' Admin panel from lock form shortcut and from tray menu
        AddHandler _lockMgr.LockFormAdminRequested, AddressOf OnAdminPanelRequested
        AddHandler _tray.AdminPanelRequested, AddressOf OnAdminPanelRequested
        AddHandler _tray.TimerToggleRequested, AddressOf OnTimerToggleRequested
        AddHandler _overlay.TimerHiddenByUser, Sub() _tray.SetTimerVisible(False)

        ' ── Lock immediately on startup ──────────────────────────────────
        _lockMgr.LockPC()

        ' ── Check license status ────────────────────────────────────────
        CheckLicenseStatus()

        ' ── Defer session start until the message loop is running ────────
        ' This ensures Invoke works correctly for UnlockPC and other UI
        ' calls triggered by the first heartbeat response.
        Dim startTimer = New Timer() With {.Interval = 200}
        AddHandler startTimer.Tick, Sub(s, ev)
            startTimer.Stop()
            startTimer.Dispose()

            ' Register PC with server
            Task.Run(Async Function()
                         Await _api.RegisterAsync()
                     End Function)

            ' Start heartbeat + local countdown
            _session.Start()

            ' Start screen capture for remote monitoring
            _capture = New ScreenCaptureService(_api, _session)
            _capture.Start()

            ' Start performance metrics reporting
            _metrics = New MetricsService(_api)
            _metrics.Start()
        End Sub
        startTimer.Start()

        Application.Run()
    End Sub

    ' ── Session event handlers ────────────────────────────────────────────

    Private Sub OnTimeUpdated(minutes As Integer, seconds As Integer)
        _overlay.UpdateTime(minutes, seconds)
        _tray.UpdateStatus($"Pisonex — {minutes:D2}:{seconds:D2} remaining")
    End Sub

    Private Sub OnSessionStarted()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnSessionStarted())
            Return
        End If

        ' Block session start if license is not active
        If Not LicenseService.IsActive() Then
            CheckLicenseStatus()
            _lockMgr.LockPC()   ' re-lock — UnlockPC was already called in SessionManager
            Return
        End If

        If Not _overlay.Visible Then _overlay.Show()
        _tray.SetTimerVisible(True)
        _tray.UpdateStatus("Pisonex — Session active")
    End Sub

    Private Sub OnSessionEnded()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnSessionEnded())
            Return
        End If
        _overlay.Hide()
        _tray.SetTimerVisible(False)
        _tray.UpdateStatus("Pisonex — Waiting for coins")
    End Sub

    Private Sub OnTimerToggleRequested()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnTimerToggleRequested())
            Return
        End If
        If _overlay.Visible Then
            _overlay.Hide()
            _tray.SetTimerVisible(False)
        Else
            _overlay.Show()
            _tray.SetTimerVisible(True)
        End If
    End Sub

    Private Sub OnConnectionLost()
        _overlay.ShowOffline()
        _lockMgr.ShowOfflineStatus()
        _tray.UpdateStatus("Pisonex — Server offline")
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

    Private Sub OnTimeAdded(seconds As Integer)
        Dim mins = seconds \ 60
        Dim secs = seconds Mod 60
        Dim timeStr = If(mins > 0, $"{mins}m {secs}s", $"{secs}s")
        _notifs.Show(
            $"+{timeStr} Added",
            $"{timeStr} has been added to your session.",
            ToastType.Success)
    End Sub

    Private Sub OnReceivingCoinsChanged(isReceiving As Boolean)
        _lockMgr.ShowReceivingCoins(If(LicenseService.IsActive(), isReceiving, False))
    End Sub

    Private Sub OnCoinSlotChanged(enabled As Boolean)
        _lockMgr.UpdateCoinSlot(enabled)
    End Sub

    Private Sub OnInsertCoinRequested()
        Task.Run(Async Function()
            Dim result = Await _api.RequestCoinsAsync()
            If Not result.Ok Then
                _lockMgr.SetInsertCoinResult(False)
                Dim msg As String
                Dim detail = result.Detail.ToLowerInvariant()
                If detail.Contains("busy") Then
                    msg = "The coin slot is currently in use by another PC. Please wait a moment and try again."
                ElseIf detail.Contains("offline") Then
                    msg = "This PC appears offline to the server. Please check your connection."
                ElseIf detail.Contains("disabled") Then
                    msg = "The coin slot has been disabled. Please contact the cashier."
                ElseIf detail.Contains("license") Then
                    msg = "The server license has expired. Please contact the administrator."
                ElseIf detail.Contains("hardware") OrElse detail.Contains("not available") Then
                    msg = "This server has no coin slot hardware. Please contact the cashier."
                Else
                    msg = "Could not open the coin slot. Please use the keypad unit or contact staff."
                End If
                _notifs.Show("Coin Slot Unavailable", msg, ToastType.Warning)
            End If
            ' On success the next heartbeat returns receiving_coins=True,
            ' which triggers ShowReceivingCoins(True) and hides the button.
        End Function)
    End Sub

    ' ── Remote control handlers ────────────────────────────────────────────

    Private Sub OnMessageReceived(text As String)
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnMessageReceived(text))
            Return
        End If
        Dim dlg = New MessageOverlay("Message from Admin", text)
        dlg.Show()
    End Sub

    ' Tracks the current announcement form so we don't stack duplicates
    Private _announcementOverlay As AnnouncementOverlay

    Private Sub OnAnnouncementChanged(text As String)
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnAnnouncementChanged(text))
            Return
        End If
        ' Close the previous announcement overlay if still visible
        If _announcementOverlay IsNot Nothing AndAlso Not _announcementOverlay.IsDisposed Then
            _announcementOverlay.Close()
        End If
        _announcementOverlay = Nothing
        If Not String.IsNullOrEmpty(text) Then
            _announcementOverlay = New AnnouncementOverlay(text)
            _announcementOverlay.Show()
        End If
    End Sub

    Private Sub OnCommandReceived(type As String, payload As String)
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnCommandReceived(type, payload))
            Return
        End If
        Select Case type
            Case "lock"
                ' Lock is already handled server-side via is_locked flag on next heartbeat,
                ' but we also end locally for instant response.
                _lockMgr.LockPC()

            Case "shutdown"
                Dim dlg = New MessageOverlay("shutdown")
                dlg.Show()

            Case "restart"
                Dim dlg = New MessageOverlay("restart")
                dlg.Show()

            Case "open_url"
                If Not String.IsNullOrWhiteSpace(payload) Then
                    Try
                        Process.Start(New ProcessStartInfo(payload) With {
                            .UseShellExecute = True
                        })
                    Catch
                    End Try
                End If
        End Select
    End Sub

    ' ── Wallpaper handler ─────────────────────────────────────────────────

    Private Sub OnWallpaperChanged(url As String, hash As String)
        ' Skip if local admin panel override is active
        If AppConfig.UseLocalWallpaper AndAlso Not String.IsNullOrEmpty(AppConfig.LockBgImagePath) Then
            Return
        End If

        ' Server cleared wallpaper — remove cached file reference
        If String.IsNullOrEmpty(url) OrElse String.IsNullOrEmpty(hash) Then
            AppConfig.SaveServerWallpaperHash("")
            AppConfig.SaveServerWallpaperPath("")
            _lockMgr.RefreshLockAppearance()
            Return
        End If

        ' Same hash as cached — already downloaded
        If hash = AppConfig.ServerWallpaperHash Then Return

        ' Download in background (never blocks heartbeat or UI)
        Task.Run(Async Function()
            Dim cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Pisonex", "wallpapers")
            Directory.CreateDirectory(cacheDir)

            Dim ext = ".jpg"
            Try
                ext = Path.GetExtension(New Uri(url).AbsolutePath)
                If String.IsNullOrEmpty(ext) Then ext = ".jpg"
            Catch
            End Try
            Dim savePath = Path.Combine(cacheDir, $"server-wallpaper{ext}")

            Dim ok = Await _api.DownloadWallpaperAsync(url, savePath)
            If ok Then
                AppConfig.SaveServerWallpaperHash(hash)
                AppConfig.SaveServerWallpaperPath(savePath)
                _lockMgr.RefreshLockAppearance()
            End If
        End Function)
    End Sub

    ' ── Membership event handlers ────────────────────────────────────────

    Private Sub OnMembershipUpdated(enabled As Boolean, absorption As Boolean, username As String,
                                     balanceSeconds As Integer, canLogout As Boolean,
                                     zeroTimeLogoutSeconds As Integer, idleShutdownSeconds As Integer,
                                     minimumLogoutMinutes As Integer, serverLicensed As Boolean)
        _lockMgr.UpdateMembershipUI(enabled, absorption, username, balanceSeconds,
                                     canLogout, zeroTimeLogoutSeconds, idleShutdownSeconds,
                                     minimumLogoutMinutes, serverLicensed)
        _overlay.SetMemberInfo(If(Not String.IsNullOrEmpty(username), username, Nothing), canLogout)

        ' Show/hide server license warning independently of the client license check
        If Not serverLicensed Then
            Dim dashboardUrl = AppConfig.ServerUrl.TrimEnd("/"c) & "/dashboard"
            _lockMgr.ShowServerLicenseWarning(dashboardUrl)
        Else
            _lockMgr.HideServerLicenseWarning()
        End If
    End Sub

    Private Sub OnMemberLogin(username As String, password As String)
        If Not LicenseService.IsActive() Then
            _lockMgr.ShowMemberError("License expired. Contact administrator to activate this PC.")
            Return
        End If
        Task.Run(Async Function()
            Dim result = Await _memberSvc.LoginAsync(AppConfig.PCNumber, username, password)
            If result.success Then
                         ' Clear credentials from the form immediately — do not leave them in the text boxes
                         _lockMgr.ClearMemberForm()
                         Dim absMsg = ""
                         If result.absorbed_seconds > 0 Then
                             Dim aMins = result.absorbed_seconds \ 60
                             absMsg = $" (+{aMins}m absorbed from previous session)"
                         End If
                         _notifs.Show("Login Successful", $"Welcome back, {username}!{absMsg}", ToastType.Success)
                     Else
                         _lockMgr.ShowMemberError(If(result.[error], "Login failed"))
                     End If
                 End Function)
    End Sub

    Private Sub OnMemberRegister(username As String, password As String)
        If Not LicenseService.IsActive() Then
            _lockMgr.ShowMemberError("License expired. Contact administrator to activate this PC.")
            Return
        End If
        Task.Run(Async Function()
                     Dim result = Await _memberSvc.RegisterAsync(AppConfig.PCNumber, username, password)
                     If result.success Then
                         ' Clear credentials from the form immediately after successful registration
                         _lockMgr.ClearMemberForm()
                         Dim absMsg = ""
                         If result.absorbed_seconds > 0 Then
                    Dim aMins = result.absorbed_seconds \ 60
                    absMsg = $" (+{aMins}m absorbed)"
                End If
                _notifs.Show("Registration Successful", $"Account created for {result.username}!{absMsg}", ToastType.Success)
            Else
                _lockMgr.ShowMemberError(If(result.[error], "Registration failed"))
            End If
        End Function)
    End Sub

    Private Sub OnMemberLogout()
        Task.Run(Async Function()
            Dim result = Await _memberSvc.LogoutAsync(AppConfig.PCNumber)
            If result.success Then
                Dim mins = result.remaining_seconds \ 60
                Dim secs = result.remaining_seconds Mod 60
                Dim dedMins = result.deducted_seconds \ 60
                _notifs.Show("Logged Out",
                    $"Time saved: {mins}m {secs}s (deducted {dedMins}m)", ToastType.Success)
            Else
                _lockMgr.ShowMemberError(If(result.[error], "Logout failed"))
            End If
        End Function)
    End Sub

    Private Sub OnMemberLogoutFromOverlay()
        OnMemberLogout()
    End Sub

    ' ── Admin panel flow ──────────────────────────────────────────────────

    Private Sub OnAdminPanelRequested()
        ' Must run on the UI thread
        If Not _overlay.IsHandleCreated Then Return
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnAdminPanelRequested())
            Return
        End If

        ' Ask for password
        Dim enteredPin = AskForPin()
        If enteredPin Is Nothing OrElse Not Config.LicenseStore.VerifyAdminPin(enteredPin) Then
            If enteredPin IsNot Nothing Then
                MessageBox.Show("Incorrect password.", "Admin Access",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If
            Return
        End If

        Dim panel = New AdminPanel()
        AddHandler panel.ExitRequested, AddressOf ExitApplication
        ' Refresh live UI immediately on save so the user sees changes right away
        AddHandler panel.SettingsSaved, Sub()
                                            _overlay.ApplyConfig()
                                            _lockMgr.RefreshLockAppearance()
                                        End Sub
        panel.ShowDialog()

        ' Also refresh once more when the panel is closed (catches unsaved drag/position changes)
        _overlay.ApplyConfig()
        _lockMgr.RefreshLockAppearance()
    End Sub

    Private Sub ExitApplication()
        ' Tell the watchdog not to restart for ~5 minutes (admin intentional exit)
        AppConfig.SaveGracefulShutdown()
        _guardTimer?.Stop()
        _guardTimer?.Dispose()
        WindowsPolicy.RemoveAll()
        _session?.Dispose()   ' stop heartbeat + countdown timers before closing lock form
        _capture?.Dispose()
        _metrics?.Dispose()
        _memberSvc?.Dispose()
        _tray?.Dispose()
        Try
            _singleInstanceMutex?.ReleaseMutex()
            _singleInstanceMutex?.Dispose()
        Catch
        End Try
        _lockMgr.AllowExit()
        Application.Exit()
    End Sub

    ' ── Password input dialog ────────────────────────────────────────────

    Private Function AskForPin() As String
        Dim dlg = New Form() With {
            .Size = New Size(300, 120),
            .TopMost = True
        }

        Dim lbl = Forms.FormStyles.CreateLabel("Enter Admin Password", bold:=True)
        lbl.Location = New Point(24, 16)

        Dim txt = Forms.FormStyles.CreateInput(New Point(24, 40), 236, pwChar:="●"c)

        Dim btn = Forms.FormStyles.CreateButton("Unlock", 236, 36)
        btn.DialogResult = DialogResult.OK
        btn.Location = New Point(24, 76)

        dlg.Controls.AddRange({lbl, txt, btn})
        dlg.AcceptButton = btn

        Forms.FormStyles.MakeBorderless(dlg, "Admin Access")

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
        If Process.GetProcessesByName("pnxsystem").Length > 0 Then Return

        Dim watchdogExe = Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "pnxsystem.exe")
        If Not File.Exists(watchdogExe) Then Return

        Try
            Process.Start(New ProcessStartInfo(watchdogExe) With {
                .UseShellExecute = False,
                .CreateNoWindow = True
            })
        Catch
            ' Watchdog not available — continue without it
        End Try
    End Sub

    ''' <summary>Called every 30 s to restart the watchdog if someone killed it.</summary>
    Private Sub EnsureGuardRunning()
        If Process.GetProcessesByName("pnxsystem").Length > 0 Then Return
        SpawnGuard()
    End Sub

    ' ── License check ────────────────────────────────────────────────────

    Private Sub CheckLicenseStatus()
        Dim status = LicenseService.GetStatus()
        Select Case status
            Case LicenseStatus.Expired
                _lockMgr.ShowLicenseWarning(
                    "Trial expired. Contact administrator to activate this PC.")
            Case LicenseStatus.OfflineLocked
                _lockMgr.ShowLicenseWarning(
                    "Activation cannot be confirmed. Please connect to the internet.")
            Case LicenseStatus.Trial
                ' Trial active — no warning needed, but could show subtle trial indicator
            Case LicenseStatus.Activated
                _lockMgr.HideLicenseWarning()
        End Select
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
