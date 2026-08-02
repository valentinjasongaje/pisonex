Imports System.Diagnostics
Imports System.Timers

Namespace Services

    Public Class SessionManager
        Implements IDisposable

        Private ReadOnly _api As ApiService
        Private ReadOnly _lock As LockManager

        Private _heartbeatTimer As Timer
        Private _countdownTimer As Timer

        ' Local session state — authoritative while server is unreachable
        Private _remainingSeconds As Integer = 0
        Private _isLocked As Boolean = True
        ' Nothing = not yet determined, so BOTH edges fire exactly once: the first
        ' successful heartbeat fires ServerConnectionRestored (drives HideOfflineStatus
        ' → UpdateInsertCoinVisibility, so Insert Coin shows once connectivity + coin-slot
        ' state are known), and the first FAILED heartbeat fires ServerConnectionLost.
        ' A plain Boolean defaulting to False broke the latter: if the server is
        ' unreachable from the very first heartbeat (e.g. a bad ServerUrl), the "already
        ' False" guard below never saw a True→False transition, so ServerConnectionLost
        ' never fired and the lock screen's offline indicator (which itself defaults to
        ' "connected") was never corrected — showing "Connected to server" indefinitely
        ' even though no connection was ever established.
        Private _serverReachable As Boolean? = Nothing
        Private _sessionToken As String = Nothing

        ' Low-time warning flags — reset each time a new session starts
        Private _warned5Min As Boolean = False
        Private _warned1Min As Boolean = False

        ' Announcement dedup — only fire event when text changes
        Private _lastAnnouncement As String = Nothing

        ' Wallpaper dedup — only fire event when hash changes
        Private _lastWallpaperHash As String = Nothing

        ' Receiving-coins dedup — only fire event when state changes
        Private _lastReceivingCoins As Boolean = False

        ' Coin-progress dedup — only fire event when the running total changes
        Private _lastCoinProgressPesos As Integer = -1

        ' Coin-slot-enabled dedup — only fire event when state changes
        Private _lastCoinSlotEnabled As Boolean = True

        ' Traditional-mode dedup — only fire event when the persistent
        ' business-model toggle changes (default False, matches the wire default).
        Private _lastTraditionalModeEnabled As Boolean = False

        ' Capture-interval dedup — only fire event when server-requested interval changes
        Private _lastCaptureIntervalMs As Integer = 0

        ' Guards _remainingSeconds from concurrent access
        Private ReadOnly _stateLock As New Object()

        ' ── Events ──────────────────────────────────────────────────
        Public Event TimeUpdated(minutes As Integer, seconds As Integer)
        Public Event SessionStarted()
        Public Event SessionEnded()
        Public Event ServerConnectionLost()
        Public Event ServerConnectionRestored()
        ''' <summary>Fired at 5 and 1 minute(s) remaining. minutesLeft = 5 or 1.</summary>
        Public Event LowTimeWarning(minutesLeft As Integer)
        ''' <summary>Fired when the server reports that time was added to this PC's session (in seconds).</summary>
        Public Event TimeAdded(seconds As Integer)
        ''' <summary>Fired when the server delivers a one-time message for this PC.</summary>
        Public Event MessageReceived(text As String)
        ''' <summary>Fired when the shop-wide announcement text changes (or is cleared with Nothing).</summary>
        Public Event AnnouncementChanged(text As String)
        ''' <summary>Fired when the server queues a remote command for this PC.</summary>
        Public Event CommandReceived(type As String, payload As String)
        ''' <summary>Fired when the server-pushed wallpaper changes (or is cleared with empty strings).</summary>
        Public Event WallpaperChanged(url As String, hash As String)
        ''' <summary>Fired when the server signals that the hardware controller is accepting coins for this PC.</summary>
        Public Event ReceivingCoinsChanged(isReceiving As Boolean)

        Public Event CoinProgressChanged(pesos As Integer, seconds As Integer)
        ''' <summary>Fired when the server's coin_slot_enabled flag changes (global or per-PC).</summary>
        Public Event CoinSlotChanged(enabled As Boolean)
        ''' <summary>Fired when the server's persistent traditional_mode_enabled (Traditional Café Mode) toggle changes.</summary>
        Public Event TraditionalModeChanged(enabled As Boolean)
        ''' <summary>Fired when membership state changes in heartbeat (enabled, username, balance, etc.).</summary>
        Public Event MembershipUpdated(enabled As Boolean, absorption As Boolean, username As String,
                                        balanceSeconds As Integer, canLogout As Boolean,
                                        zeroTimeLogoutSeconds As Integer, idleShutdownSeconds As Integer,
                                        minimumLogoutMinutes As Integer)
        ''' <summary>Fired when the server requests a specific capture interval (ms). 0 means reset to configured.</summary>
        Public Event CaptureIntervalChanged(intervalMs As Integer)

        ' ── Heartbeat interval ───────────────────────────────────────
        ' 1-second poll in all states.  On a local LAN with FastAPI +
        ' SQLite this is negligible (< 1 ms server work per request,
        ' even with 20+ PCs polling simultaneously on a Raspberry Pi).
        ' Coins / admin time-adds are reflected on the client within 1 s.
        Private Const HEARTBEAT_LOCKED_MS As Integer = 1_000    ' 1 s (waiting for coins)
        Private Const HEARTBEAT_ACTIVE_MS As Integer = 1_000    ' 1 s (session running)

        Public Sub New(api As ApiService, lockMgr As LockManager)
            _api = api
            _lock = lockMgr
        End Sub

        Public Sub Start()
            DiagnosticLog.Write("SessionManager.Start — heartbeat/countdown timers starting")
            ' 1-second local countdown — independent of network
            _countdownTimer = New Timer(1_000)
            AddHandler _countdownTimer.Elapsed, AddressOf OnLocalTick
            _countdownTimer.AutoReset = True
            _countdownTimer.Start()

            ' Server sync — start at the fast locked rate so the first coin
            ' insertion is detected within 3 s.  Rate is adjusted live by
            ' SendHeartbeat once a session becomes active.
            _heartbeatTimer = New Timer(HEARTBEAT_LOCKED_MS)
            AddHandler _heartbeatTimer.Elapsed, AddressOf OnHeartbeat
            _heartbeatTimer.AutoReset = True
            _heartbeatTimer.Start()

            ' Immediate first heartbeat
            Task.Run(AddressOf SendHeartbeat)
        End Sub

        ' ── Local countdown (every 1 second) ────────────────────────

        Private Sub OnLocalTick(sender As Object, e As ElapsedEventArgs)
            SyncLock _stateLock
                If _isLocked OrElse _remainingSeconds <= 0 Then Return

                _remainingSeconds -= 1

                Dim mins = _remainingSeconds \ 60
                Dim secs = _remainingSeconds Mod 60
                RaiseEvent TimeUpdated(mins, secs)

                ' Low-time warnings (fire once per session at 5 min and 1 min)
                If _remainingSeconds = 300 AndAlso Not _warned5Min Then
                    _warned5Min = True
                    RaiseEvent LowTimeWarning(5)
                ElseIf _remainingSeconds = 60 AndAlso Not _warned1Min Then
                    _warned1Min = True
                    RaiseEvent LowTimeWarning(1)
                End If

                ' Time ran out locally — lock regardless of server state
                If _remainingSeconds = 0 Then
                    DiagnosticLog.Write("LOCK — local countdown reached 0 (was isLocked=False)")
                    _isLocked = True
                    _lock.LockPC()
                    RaiseEvent SessionEnded()
                End If
            End SyncLock
        End Sub

        ' ── Server heartbeat (every 10 seconds) ─────────────────────

        Private Async Sub OnHeartbeat(sender As Object, e As ElapsedEventArgs)
            Await SendHeartbeat()
        End Sub

        Private Async Function SendHeartbeat() As Task
            Dim sw = Stopwatch.StartNew()
            DiagnosticLog.Write("Heartbeat: send")
            Dim response = Await _api.HeartbeatAsync()
            DiagnosticLog.Write($"Heartbeat: recv after {sw.ElapsedMilliseconds}ms — " &
                If(response Is Nothing, "unreachable (null response)",
                   $"is_locked={response.is_locked} remaining={response.remaining_seconds} token={If(response.session_token, "null")}"))

            If response Is Nothing Then
                ' ── Server unreachable ──────────────────────────────
                ' Local countdown continues uninterrupted.
                ' Do NOT lock. Do NOT change lock state.
                If Not _serverReachable.HasValue OrElse _serverReachable.Value Then
                    DiagnosticLog.Write("OFFLINE — server unreachable")
                    _serverReachable = False
                    RaiseEvent ServerConnectionLost()
                End If
                Return
            End If

            ' ── Server reachable ────────────────────────────────────
            If Not _serverReachable.HasValue OrElse Not _serverReachable.Value Then
                DiagnosticLog.Write("ONLINE — server reachable")
                _serverReachable = True
                RaiseEvent ServerConnectionRestored()
            End If

            Dim lockedNow As Boolean   ' captured inside the lock, used below

            SyncLock _stateLock
                ' Server is always the source of truth for remaining time.
                _remainingSeconds = response.remaining_seconds
                _sessionToken = response.session_token

                Dim serverSaysLocked = response.is_locked

                If serverSaysLocked AndAlso Not _isLocked Then
                    ' Server locked us (time expired server-side, admin locked, etc.)
                    DiagnosticLog.Write($"LOCK — heartbeat: server is_locked=true (was unlocked, remaining was {_remainingSeconds})")
                    _isLocked = True
                    _lock.LockPC()
                    RaiseEvent SessionEnded()

                ElseIf Not serverSaysLocked AndAlso _isLocked Then
                    ' Server unlocked us (coins inserted) — reset warning flags for the new session
                    DiagnosticLog.Write($"UNLOCK — heartbeat: server is_locked=false (remaining={response.remaining_seconds}, token={If(response.session_token, "null")})")
                    _isLocked   = False
                    _warned5Min = False
                    _warned1Min = False
                    _lock.UnlockPC()
                    RaiseEvent SessionStarted()

                ElseIf Not serverSaysLocked AndAlso Not _isLocked AndAlso _remainingSeconds <= 0 Then
                    ' Session has genuinely run out of granted time, but the server's
                    ' background expiry sweep (runs every 30 s) hasn't flipped is_locked
                    ' yet. Waiting for it would leave the TimerOverlay stuck showing no
                    ' time until the sweep catches up — lock immediately instead.
                    DiagnosticLog.Write("LOCK — heartbeat: server is_locked=false but remaining<=0 (pre-empting expiry sweep)")
                    _isLocked = True
                    _lock.LockPC()
                    RaiseEvent SessionEnded()
                End If

                lockedNow = _isLocked
            End SyncLock

            ' ── Adaptive poll rate ────────────────────────────────────────────
            ' Fast while locked — coin / time-add detected within 3 s.
            ' Slower while active — local 1-second countdown handles display;
            ' server sync is only needed for drift correction and top-ups.
            Dim targetMs = If(lockedNow, HEARTBEAT_LOCKED_MS, HEARTBEAT_ACTIVE_MS)
            If _heartbeatTimer.Interval <> targetMs Then
                _heartbeatTimer.Interval = targetMs   ' resets the countdown to new interval
            End If

            If response.time_added_seconds > 0 Then
                RaiseEvent TimeAdded(response.time_added_seconds)
            End If

            ' ── Remote control delivery ────────────────────────────────────────

            ' Per-PC message (popped on delivery — show only once)
            If Not String.IsNullOrEmpty(response.admin_message) Then
                RaiseEvent MessageReceived(response.admin_message)
            End If

            ' Shop-wide announcement (persistent — only raise event when text changes)
            Dim ann = If(String.IsNullOrEmpty(response.announcement), Nothing, response.announcement)
            If ann <> _lastAnnouncement Then
                _lastAnnouncement = ann
                RaiseEvent AnnouncementChanged(ann)
            End If

            ' Remote command (popped on delivery — execute once)
            If Not String.IsNullOrEmpty(response.pending_command) Then
                RaiseEvent CommandReceived(response.pending_command,
                                           If(response.command_payload, String.Empty))
            End If

            ' Server-pushed wallpaper — only fire event when hash changes
            Dim wpHash = If(String.IsNullOrEmpty(response.wallpaper_hash), Nothing, response.wallpaper_hash)
            If wpHash <> _lastWallpaperHash Then
                _lastWallpaperHash = wpHash
                RaiseEvent WallpaperChanged(
                    If(response.wallpaper_url, String.Empty),
                    If(wpHash, String.Empty))
            End If

            ' Receiving-coins state — only fire event when state changes
            If response.receiving_coins <> _lastReceivingCoins Then
                _lastReceivingCoins = response.receiving_coins
                RaiseEvent ReceivingCoinsChanged(response.receiving_coins)
            End If

            ' Live coin-insertion running total — fire when the peso total changes
            If response.coin_progress_pesos <> _lastCoinProgressPesos Then
                _lastCoinProgressPesos = response.coin_progress_pesos
                RaiseEvent CoinProgressChanged(response.coin_progress_pesos, response.coin_progress_seconds)
            End If

            ' Coin slot enabled — only fire event when state changes
            If response.coin_slot_enabled <> _lastCoinSlotEnabled Then
                _lastCoinSlotEnabled = response.coin_slot_enabled
                RaiseEvent CoinSlotChanged(response.coin_slot_enabled)
            End If

            ' Traditional Café Mode toggle — only fire when it changes
            If response.traditional_mode_enabled <> _lastTraditionalModeEnabled Then
                _lastTraditionalModeEnabled = response.traditional_mode_enabled
                RaiseEvent TraditionalModeChanged(response.traditional_mode_enabled)
            End If

            ' Membership state — always fire so the UI can update
            RaiseEvent MembershipUpdated(
                response.membership_enabled,
                response.absorption_enabled,
                response.member_username,
                response.member_balance_seconds,
                response.member_can_logout,
                response.zero_time_logout_seconds,
                response.idle_shutdown_seconds,
                response.minimum_logout_minutes)

            ' Server-driven capture interval — ramp up when admin is watching this PC,
            ' reset to configured when not watched (0 = use own config).
            If response.capture_interval_ms <> _lastCaptureIntervalMs Then
                _lastCaptureIntervalMs = response.capture_interval_ms
                RaiseEvent CaptureIntervalChanged(response.capture_interval_ms)
            End If
        End Function

        ' ── Public state ─────────────────────────────────────────────

        Public ReadOnly Property IsLocked As Boolean
            Get
                SyncLock _stateLock
                    Return _isLocked
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property RemainingSeconds As Integer
            Get
                SyncLock _stateLock
                    Return _remainingSeconds
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property ServerReachable As Boolean
            Get
                Return _serverReachable.GetValueOrDefault(False)
            End Get
        End Property

        Public Sub Dispose() Implements IDisposable.Dispose
            _heartbeatTimer?.Stop()
            _heartbeatTimer?.Dispose()
            _countdownTimer?.Stop()
            _countdownTimer?.Dispose()
        End Sub
    End Class

End Namespace
