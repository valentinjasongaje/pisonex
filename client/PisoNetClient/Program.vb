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
    Private _streamCapture As StreamCaptureService
    Private _metrics As MetricsService
    Private _notifs As NotificationService
    Private _guardTimer As System.Timers.Timer   ' mutual watchdog keeper
    ' Accumulates time_added_seconds while the coin slot is open so the
    ' voice/toast notification is not fired until the user clicks Done
    ' and the lock form actually hides.
    Private _pendingTimeAddedSeconds As Integer = 0
    ' Set when a session starts while the coin slot is still open (lock screen
    ' is in front showing the Receiving Coins card).  We defer showing the
    ' TimerOverlay until the slot closes so it doesn't appear behind the lock
    ' screen and isn't visible the moment the user finishes inserting coins.
    Private _overlayShowPending As Boolean = False

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

        ' ── Startup registration + watchdog (production only) ────────────────
        ' In DEBUG builds we skip these entirely and actively remove any stale
        ' startup registry entry left by a previous run, so the app never
        ' auto-launches on boot during development.
#If DEBUG Then
        UnregisterStartup()
#Else
        RegisterStartup()

        ' Register exe path + spawn the watchdog guardian
        AppConfig.SaveClientExePath(Application.ExecutablePath)
        SpawnGuard()

        ' Mutual watchdog: keep the guard process alive every 30 s
        _guardTimer = New System.Timers.Timer(30_000)
        AddHandler _guardTimer.Elapsed, Sub(s, e) EnsureGuardRunning()
        _guardTimer.AutoReset = True
        _guardTimer.Start()
#End If

        ' ── Licensing ──────────────────────────────────────────────────────
        ' Unlimited-clients model: the PC client has no per-PC activation and never
        ' contacts pisonex.com. There is no trial and no internet requirement on the
        ' client — it always runs locally and trusts its LAN server, authenticating
        ' with the X-API-Key header. Whether the SHOP is licensed is decided by the
        ' Orange Pi server and delivered on every heartbeat via the server_licensed
        ' flag (handled in OnMembershipUpdated → ShowServerLicenseWarning). An
        ' unlicensed server therefore locks every PC in the shop at the source.

        ' ── Apply Windows restrictions ────────────────────────────────────
        WindowsPolicy.Apply()

        ' Ensure restrictions are removed even if the process crashes
        AddHandler Application.ApplicationExit, Sub(s, e) WindowsPolicy.RemoveAll()

        ' ── Create all UI objects on the STA thread ───────────────────────
        _api = New ApiService(AppConfig.ServerUrl, AppConfig.PCNumber)
        _memberSvc = New MemberService(AppConfig.ServerUrl)
        _lockMgr = New LockManager()
        _session = New SessionManager(_api, _lockMgr)
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
        AddHandler _session.CoinProgressChanged, AddressOf OnCoinProgressChanged
        AddHandler _session.CoinSlotChanged, AddressOf OnCoinSlotChanged
        AddHandler _session.MembershipUpdated, AddressOf OnMembershipUpdated
        AddHandler _session.CaptureIntervalChanged, AddressOf OnCaptureIntervalChanged

        ' Insert Coin event from lock form and overlay
        AddHandler _lockMgr.LockFormInsertCoinRequested, AddressOf OnInsertCoinRequested
        AddHandler _lockMgr.LockFormDoneInsertingCoinsRequested, AddressOf OnDoneInsertingCoinsRequested
        AddHandler _overlay.InsertCoinRequested, AddressOf OnInsertCoinRequested
        AddHandler _overlay.DoneInsertingCoinsRequested, AddressOf OnDoneInsertingCoinsRequested

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

            ' Start screen capture for remote monitoring (grid thumbnails)
            _capture = New ScreenCaptureService(_api, _session)
            _capture.Start()

            ' FFmpeg live stream — started/stopped by server-driven CaptureIntervalChanged
            _streamCapture = New StreamCaptureService(_api.BaseWsUrl, AppConfig.PCNumber)

            ' Start performance metrics reporting
            _metrics = New MetricsService(_api)
            _metrics.Start()

            ' Earnings reporting to the customer portal is handled entirely by the
            ' Orange Pi server (daily /api/sync/earnings with startup catch-up), so
            ' the client no longer forwards branch earnings to pisonex.com.
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

        ' If the coin slot is still open (first coin created the session but the
        ' user hasn't pressed Done yet), defer showing the overlay so it doesn't
        ' appear while the lock screen is still in front.  OnReceivingCoinsChanged
        ' will surface it when the slot closes.
        If _lockMgr.IsReceivingCoins Then
            _overlayShowPending = True
            _tray.UpdateStatus("Pisonex — Session active")
            Return
        End If

        ShowOverlayForActiveSession()
    End Sub

    ''' <summary>
    ''' Brings up the TimerOverlay + tray timer for an active session.  Extracted
    ''' from OnSessionStarted so the deferred path (slot was open at session start)
    ''' can reuse the exact same logic when the slot finally closes.
    ''' </summary>
    Private Sub ShowOverlayForActiveSession()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() ShowOverlayForActiveSession())
            Return
        End If
        If Not _overlay.Visible Then _overlay.Show()
        ' Show "Add Time" CTA immediately on session start.
        ' CoinSlotChanged only fires when the value changes, but _lastCoinSlotEnabled
        ' starts True in SessionManager so the event never fires on the first heartbeat
        ' if the slot is enabled (the common case).  We seed it here so the button
        ' appears as soon as the session begins; subsequent CoinSlotChanged events
        ' will still override it if the slot is actually disabled.
        _overlay.ShowAddTimeButton(True)
        _tray.SetTimerVisible(True)
        _tray.UpdateStatus("Pisonex — Session active")
    End Sub

    Private Sub OnSessionEnded()
        If _overlay.InvokeRequired Then
            _overlay.Invoke(Sub() OnSessionEnded())
            Return
        End If
        ' Drop any deferred overlay-show — the session is over, the overlay
        ' must not pop on the next slot close.
        _overlayShowPending = False
        _overlay.ShowAddTimeButton(False)   ' reset CTA state before hiding
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
        ' While the coin slot is still open the lock form is still showing —
        ' the user may be about to insert more coins.  Firing the toast + voice
        ' now would be premature and confusing.  Accumulate the total and fire
        ' once the slot closes (see OnReceivingCoinsChanged below).
        If _lockMgr.IsReceivingCoins Then
            _pendingTimeAddedSeconds += seconds
            Return
        End If
        FireTimeAddedNotification(seconds)
    End Sub

    Private Sub FireTimeAddedNotification(seconds As Integer)
        Dim mins = seconds \ 60
        Dim secs = seconds Mod 60
        Dim timeStr = If(mins > 0, $"{mins}m {secs}s", $"{secs}s")
        _notifs.Show(
            $"+{timeStr} Added",
            $"{timeStr} has been added to your session.",
            ToastType.Success)
    End Sub

    Private Sub OnReceivingCoinsChanged(isReceiving As Boolean)
        _lockMgr.ShowReceivingCoins(isReceiving)
        _overlay.SetReceivingCoins(isReceiving)
        If Not isReceiving Then
            ' Slot just closed — if a session started while the slot was open
            ' the overlay was held back so the lock screen could own the
            ' display.  Surface it now that the lock screen is dismissing.
            If _overlayShowPending Then
                _overlayShowPending = False
                ShowOverlayForActiveSession()
            End If
            ' Fire any time-added notification that was held back while the
            ' lock form was still showing (could be multiple coins summed).
            If _pendingTimeAddedSeconds > 0 Then
                Dim total = _pendingTimeAddedSeconds
                _pendingTimeAddedSeconds = 0
                FireTimeAddedNotification(total)
            End If
        End If
    End Sub

    Private Sub OnCoinProgressChanged(pesos As Integer, seconds As Integer)
        _lockMgr.UpdateCoinProgress(pesos, seconds)
        _overlay.UpdateCoinProgress(pesos, seconds)
    End Sub

    Private Sub OnCoinSlotChanged(enabled As Boolean)
        _lockMgr.UpdateCoinSlot(enabled)
        _overlay.ShowAddTimeButton(enabled)
    End Sub

    Private Sub OnCaptureIntervalChanged(intervalMs As Integer)
        If intervalMs > 0 Then
            ' Admin opened fullscreen — start FFmpeg WebSocket stream
            _streamCapture?.StartStream()
        Else
            ' Admin closed fullscreen — stop FFmpeg, release resources
            _streamCapture?.StopStream()
        End If
        ' JPEG thumbnail capture continues at its normal rate for the grid view
    End Sub

    Private Sub OnInsertCoinRequested()
        Task.Run(Async Function()
            Dim result = Await _api.RequestCoinsAsync()
            If Not result.Ok Then
                _lockMgr.SetInsertCoinResult(False)
                _overlay.SetInsertCoinResult(False)
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

    Private Sub OnDoneInsertingCoinsRequested()
        Task.Run(Async Function()
            Dim result = Await _api.DoneInsertingCoinsAsync()
            If Not result.Ok Then
                _notifs.Show("Coin Slot",
                    "Could not close the coin slot. It may have already closed.",
                    ToastType.Warning)
            End If
            ' On success the next heartbeat reports receiving_coins=False,
            ' which triggers ShowReceivingCoins(False) and restores the screen.
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
        _overlay.SetMemberInfo(If(Not String.IsNullOrEmpty(username), username, Nothing), canLogout, minimumLogoutMinutes)

        ' Show/hide server license warning independently of the client license check
        If Not serverLicensed Then
            Dim dashboardUrl = AppConfig.ServerUrl.TrimEnd("/"c) & "/dashboard"
            _lockMgr.ShowServerLicenseWarning(dashboardUrl)
        Else
            _lockMgr.HideServerLicenseWarning()
        End If
    End Sub

    Private Sub OnMemberLogin(username As String, password As String)
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

    ' ── Windows startup registration ──────────────────────────────────────

    ''' <summary>
    ''' Registers the current exe in HKCU Run so Windows launches it on boot.
    ''' Called only in Release builds.
    ''' </summary>
    Private Sub RegisterStartup()
        Try
            Dim key = Registry.CurrentUser.OpenSubKey(
                "SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)
            key?.SetValue("PisoNetClient", $"""{Application.ExecutablePath}""")
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Removes the startup registry entry entirely.
    ''' Called in Debug builds so development runs never pollute Windows startup.
    ''' </summary>
    Private Sub UnregisterStartup()
        Try
            Dim key = Registry.CurrentUser.OpenSubKey(
                "SOFTWARE\Microsoft\Windows\CurrentVersion\Run", True)
            key?.DeleteValue("PisoNetClient", throwOnMissingValue:=False)
        Catch
        End Try
    End Sub

End Module
