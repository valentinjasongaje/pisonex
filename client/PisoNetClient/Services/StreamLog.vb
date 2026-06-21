Imports System.Collections.Concurrent

Namespace Services

    ''' <summary>
    ''' Stream state — drives the small status pill in the Admin Panel.
    ''' </summary>
    Public Enum StreamState
        Idle
        Connecting
        Running
        Failed
        Disabled    ' ffmpeg.exe could not be located
    End Enum

    ''' <summary>
    ''' Thread-safe circular log buffer for the FFmpeg live-stream pipeline.
    ''' Captures every meaningful step (ffmpeg discovery, WebSocket connect,
    ''' stderr lines, errors) so the operator can diagnose silent failures
    ''' from the Admin Panel's Security tab.
    '''
    ''' Previously the entire `StreamCaptureService.RunAsync` swallowed errors
    ''' with bare `Catch` blocks, so users had no way to tell whether ffmpeg
    ''' was even running, the WebSocket was refused, or the binary was missing.
    ''' </summary>
    Public Module StreamLog

        Private Const MAX_ENTRIES As Integer = 60

        Private ReadOnly _entries As New Queue(Of String)()
        Private ReadOnly _lock As New Object()

        Private _state As StreamState = StreamState.Idle
        Private _lastError As String = ""
        Private _lastStateChange As DateTime = DateTime.Now

        Public Event StateChanged()

        Public ReadOnly Property State As StreamState
            Get
                Return _state
            End Get
        End Property

        Public ReadOnly Property LastError As String
            Get
                Return _lastError
            End Get
        End Property

        Public ReadOnly Property LastStateChange As DateTime
            Get
                Return _lastStateChange
            End Get
        End Property

        ''' <summary>Append a log line with timestamp + level.  Pass "" for default INFO.</summary>
        Public Sub Append(level As String, message As String)
            If String.IsNullOrEmpty(level) Then level = "INFO"
            Dim line = $"{DateTime.Now:HH:mm:ss.fff}  {level,-5}  {message}"
            SyncLock _lock
                _entries.Enqueue(line)
                While _entries.Count > MAX_ENTRIES
                    _entries.Dequeue()
                End While
            End SyncLock
            ' Mirror to Debug.WriteLine so you can also tail this from the IDE
            ' when running a Debug build.
            Try : Debug.WriteLine($"[STREAM] {line}") : Catch : End Try
        End Sub

        Public Sub Info(msg As String)
            Append("INFO", msg)
        End Sub
        Public Sub Warn(msg As String)
            Append("WARN", msg)
        End Sub
        Public Sub Err(msg As String)
            Append("ERROR", msg)
        End Sub

        ''' <summary>Update the high-level state used by the Admin Panel pill.</summary>
        Public Sub SetState(newState As StreamState, Optional reason As String = "")
            Dim changed = (newState <> _state)
            _state = newState
            _lastStateChange = DateTime.Now
            If newState = StreamState.Failed Then
                _lastError = If(String.IsNullOrEmpty(reason), "Unknown failure", reason)
            ElseIf newState = StreamState.Running OrElse newState = StreamState.Connecting Then
                _lastError = ""
            End If
            If changed Then
                Try : RaiseEvent StateChanged() : Catch : End Try
            End If
        End Sub

        ''' <summary>Returns the full log buffer as a single string, newest line last.</summary>
        Public Function Snapshot() As String
            SyncLock _lock
                Return String.Join(Environment.NewLine, _entries)
            End SyncLock
        End Function

        ''' <summary>Erases the buffer.  Called by the Admin Panel "Clear log" button.</summary>
        Public Sub Clear()
            SyncLock _lock
                _entries.Clear()
            End SyncLock
        End Sub

    End Module

End Namespace
