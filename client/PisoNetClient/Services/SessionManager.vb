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
        Private _serverReachable As Boolean = True
        Private _sessionToken As String = Nothing

        ' Low-time warning flags — reset each time a new session starts
        Private _warned5Min As Boolean = False
        Private _warned1Min As Boolean = False

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
        ''' <summary>Fired when the server reports that time was added to this PC's session.</summary>
        Public Event TimeAdded(minutes As Integer)

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
            Dim response = Await _api.HeartbeatAsync()

            If response Is Nothing Then
                ' ── Server unreachable ──────────────────────────────
                ' Local countdown continues uninterrupted.
                ' Do NOT lock. Do NOT change lock state.
                If _serverReachable Then
                    _serverReachable = False
                    RaiseEvent ServerConnectionLost()
                End If
                Return
            End If

            ' ── Server reachable ────────────────────────────────────
            If Not _serverReachable Then
                _serverReachable = True
                RaiseEvent ServerConnectionRestored()
            End If

            Dim lockedNow As Boolean   ' captured inside the lock, used below

            SyncLock _stateLock
                ' Server is always the source of truth for remaining time
                Dim serverSeconds = (response.remaining_minutes * 60) + response.remaining_seconds
                _remainingSeconds = serverSeconds
                _sessionToken = response.session_token

                Dim serverSaysLocked = response.is_locked

                If serverSaysLocked AndAlso Not _isLocked Then
                    ' Server locked us (time expired server-side, admin locked, etc.)
                    _isLocked = True
                    _lock.LockPC()
                    RaiseEvent SessionEnded()

                ElseIf Not serverSaysLocked AndAlso _isLocked Then
                    ' Server unlocked us (coins inserted) — reset warning flags for the new session
                    _isLocked   = False
                    _warned5Min = False
                    _warned1Min = False
                    _lock.UnlockPC()
                    RaiseEvent SessionStarted()
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

            ' Notify the user if the server reports that time was added
            If response.time_added_minutes > 0 Then
                RaiseEvent TimeAdded(response.time_added_minutes)
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
                Return _serverReachable
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
