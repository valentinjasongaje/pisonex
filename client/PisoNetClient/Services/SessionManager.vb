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

        ' Guards _remainingSeconds from concurrent access
        Private ReadOnly _stateLock As New Object()

        ' ── Events ──────────────────────────────────────────────────
        Public Event TimeUpdated(minutes As Integer, seconds As Integer)
        Public Event SessionStarted()
        Public Event SessionEnded()
        Public Event ServerConnectionLost()
        Public Event ServerConnectionRestored()

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

            ' 10-second server sync
            _heartbeatTimer = New Timer(10_000)
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
                    ' Server unlocked us (coins inserted)
                    _isLocked = False
                    _lock.UnlockPC()
                    RaiseEvent SessionStarted()
                End If
            End SyncLock
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
