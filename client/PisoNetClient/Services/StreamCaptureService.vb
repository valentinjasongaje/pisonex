Imports System.Diagnostics
Imports System.IO
Imports System.Net.WebSockets
Imports System.Threading
Imports System.Threading.Tasks
Imports PisoNetClient.Config

Namespace Services

    ''' <summary>
    ''' Drives the live MPEG1 video stream sent to the admin dashboard.
    ''' When activated, launches FFmpeg to capture the primary screen and
    ''' pipes the MPEG-TS output over a WebSocket to the OPi relay server.
    ''' The browser-side jsmpeg player decodes and displays the stream.
    '''
    ''' FFmpeg is located by checking (in order):
    '''   1. ffmpeg.exe beside the running executable
    '''   2. System PATH via where.exe
    '''
    ''' If FFmpeg is not found, the service marks itself <see cref="StreamState.Disabled"/>
    ''' and exits — the dashboard falls back to the MJPEG stream automatically.
    '''
    ''' Every step writes to <see cref="StreamLog"/> so the operator can see
    ''' WHY the stream is or isn't running from the Admin Panel.  Bare `Catch`
    ''' blocks used to swallow ffmpeg crashes, WebSocket refusals, missing
    ''' binaries — anything that went wrong was invisible.
    ''' </summary>
    Public Class StreamCaptureService
        Implements IDisposable

        ' ── FFmpeg capture settings ──────────────────────────────────────────
        ' MPEG-TS / MPEG1 video — required by jsmpeg.
        ' 1280×720 at 2 Mbps gives sharp, readable text on a LAN.
        ' -bf 0 disables B-frames for minimum latency.
        ' -muxdelay 0 / -muxpreload 0 eliminates muxer buffering.
        Private Const FFMPEG_ARGS As String =
            "-fflags nobuffer " &
            "-f gdigrab -framerate 30 -i desktop " &
            "-f mpegts -codec:v mpeg1video " &
            "-s 1280x720 -b:v 2000k -bf 0 " &
            "-muxdelay 0 -muxpreload 0 " &
            "-flush_packets 1 " &
            "pipe:1"

        ' Bound the WebSocket connect.  A bad ServerUrl used to leave the
        ' task hanging forever on the default tcp connect timeout.
        Private Const CONNECT_TIMEOUT_MS As Integer = 5000

        Private ReadOnly _baseWsUrl As String   ' e.g. ws://192.168.1.21
        Private ReadOnly _pcNumber  As Integer

        Private _ffmpeg  As Process
        Private _ws      As ClientWebSocket
        Private _cts     As CancellationTokenSource
        Private _disposed As Boolean = False

        Private ReadOnly _lock As New Object()
        Private _running As Boolean = False

        Private _stderrCaptured As Integer = 0   ' cap log spam at ~10 lines/attempt
        Private _bytesSent As Long = 0
        Private _firstChunkLogged As Boolean = False

        Public Sub New(baseWsUrl As String, pcNumber As Integer)
            _baseWsUrl = baseWsUrl
            _pcNumber  = pcNumber
        End Sub

        ' ── Public control ───────────────────────────────────────────────────

        Public Sub StartStream()
            SyncLock _lock
                If _running OrElse _disposed Then Return
                _running = True
            End SyncLock
            _stderrCaptured = 0
            _bytesSent = 0
            _firstChunkLogged = False
            StreamLog.Info($"StartStream requested (PC {_pcNumber}, target {_baseWsUrl})")
            _cts = New CancellationTokenSource()
            Task.Run(AddressOf RunAsync)
        End Sub

        Public Sub StopStream()
            SyncLock _lock
                If Not _running Then Return
            End SyncLock
            StreamLog.Info("StopStream requested")
            _cts?.Cancel()
            ' Cleanup() will set _running = False once the task exits
        End Sub

        ' ── Core streaming loop ──────────────────────────────────────────────

        Private Async Function RunAsync() As Task
            Try
                ' ── 1. Locate ffmpeg ────────────────────────────────────────
                Dim ffmpegPath = FindFfmpeg()
                If String.IsNullOrEmpty(ffmpegPath) Then
                    StreamLog.SetState(StreamState.Disabled,
                        "ffmpeg.exe not found beside PisoNetClient.exe and not on PATH")
                    StreamLog.Err("Cannot start: ffmpeg.exe not found. Place ffmpeg.exe next to PisoNetClient.exe or add it to PATH.")
                    Return
                End If
                StreamLog.Info($"ffmpeg located at {ffmpegPath}")

                ' ── 2. Open WebSocket to publish endpoint ───────────────────
                StreamLog.SetState(StreamState.Connecting)
                _ws = New ClientWebSocket()
                If Not String.IsNullOrEmpty(AppConfig.ApiKey) Then
                    _ws.Options.SetRequestHeader("X-API-Key", AppConfig.ApiKey)
                    StreamLog.Info("X-API-Key header attached")
                End If
                Dim wsUri = New Uri($"{_baseWsUrl}/dashboard/ws/stream/{_pcNumber}/publish")
                StreamLog.Info($"Connecting WebSocket → {wsUri}")

                ' Bound the connect — a wrong URL used to hang the whole task.
                Using connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
                    connectCts.CancelAfter(CONNECT_TIMEOUT_MS)
                    Try
                        Await _ws.ConnectAsync(wsUri, connectCts.Token)
                    Catch ex As OperationCanceledException When Not _cts.IsCancellationRequested
                        StreamLog.SetState(StreamState.Failed,
                            $"WebSocket connect timed out after {CONNECT_TIMEOUT_MS} ms")
                        StreamLog.Err($"Connect timeout — is the server reachable at {_baseWsUrl}? Wrong port or firewall?")
                        Return
                    End Try
                End Using
                StreamLog.Info("WebSocket connected")

                ' ── 3. Launch ffmpeg ────────────────────────────────────────
                Dim psi As New ProcessStartInfo(ffmpegPath, FFMPEG_ARGS) With {
                    .RedirectStandardOutput = True,
                    .RedirectStandardError  = True,
                    .UseShellExecute        = False,
                    .CreateNoWindow         = True
                }
                _ffmpeg = Process.Start(psi)
                If _ffmpeg Is Nothing Then
                    StreamLog.SetState(StreamState.Failed, "ffmpeg.exe could not be launched")
                    StreamLog.Err("Process.Start returned Nothing for ffmpeg")
                    Return
                End If
                StreamLog.Info($"ffmpeg launched (pid {_ffmpeg.Id})")

                ' Capture the first ~10 ffmpeg stderr lines so a crash on
                ' startup (missing codec, gdigrab failure, etc.) is visible.
                AddHandler _ffmpeg.ErrorDataReceived, AddressOf OnFfmpegStderr
                _ffmpeg.BeginErrorReadLine()

                ' ── 4. Drain incoming WS frames so server keepalive works ─
                Dim drainTask = Task.Run(Async Function()
                    Dim recvBuf = New Byte(1023) {}
                    Try
                        While Not _cts.Token.IsCancellationRequested
                            Dim result = Await _ws.ReceiveAsync(
                                New ArraySegment(Of Byte)(recvBuf), _cts.Token)
                            If result.MessageType = WebSocketMessageType.Close Then
                                StreamLog.Info($"WebSocket close received from server ({result.CloseStatus}: {result.CloseStatusDescription})")
                                Exit While
                            End If
                        End While
                    Catch
                        ' Connection closed — exit silently
                    End Try
                End Function)

                StreamLog.SetState(StreamState.Running)

                ' ── 5. Pipe ffmpeg stdout → WebSocket ──────────────────────
                Dim buf       = New Byte(65535) {}
                Dim outStream = _ffmpeg.StandardOutput.BaseStream
                While Not _cts.Token.IsCancellationRequested
                    Dim n = Await outStream.ReadAsync(buf, 0, buf.Length, _cts.Token)
                    If n = 0 Then
                        ' ffmpeg closed stdout — most often it crashed.  Wait
                        ' briefly for it to actually exit so we can report
                        ' the exit code.
                        Try
                            _ffmpeg.WaitForExit(500)
                        Catch
                        End Try
                        StreamLog.Err($"ffmpeg stdout closed (exit code {SafeExitCode()}) after {_bytesSent} bytes")
                        StreamLog.SetState(StreamState.Failed,
                            $"ffmpeg exited early (code {SafeExitCode()}). See log for stderr.")
                        Exit While
                    End If
                    Await _ws.SendAsync(
                        New ArraySegment(Of Byte)(buf, 0, n),
                        WebSocketMessageType.Binary,
                        endOfMessage:=True,    ' each chunk is a complete message for the relay
                        cancellationToken:=_cts.Token)
                    _bytesSent += n
                    If Not _firstChunkLogged Then
                        _firstChunkLogged = True
                        StreamLog.Info($"First chunk sent: {n} bytes")
                    End If
                End While

            Catch ex As OperationCanceledException
                ' Normal stop — swallow
            Catch ex As WebSocketException
                StreamLog.SetState(StreamState.Failed,
                    $"WebSocket error: {ex.Message} (code {ex.WebSocketErrorCode})")
                StreamLog.Err($"WebSocketException: {ex.Message} / WebSocketErrorCode={ex.WebSocketErrorCode}")
            Catch ex As Exception
                StreamLog.SetState(StreamState.Failed, $"Unexpected error: {ex.Message}")
                StreamLog.Err($"{ex.GetType().Name}: {ex.Message}")
            Finally
                Cleanup()
            End Try
        End Function

        Private Sub OnFfmpegStderr(sender As Object, e As DataReceivedEventArgs)
            If String.IsNullOrEmpty(e?.Data) Then Return
            ' Cap how many lines we capture per attempt so we don't fill the
            ' buffer with ffmpeg's noisy frame stats.
            If _stderrCaptured >= 10 Then Return
            _stderrCaptured += 1
            StreamLog.Info("ffmpeg> " & e.Data.Trim())
        End Sub

        Private Function SafeExitCode() As String
            Try
                If _ffmpeg IsNot Nothing AndAlso _ffmpeg.HasExited Then
                    Return _ffmpeg.ExitCode.ToString()
                End If
            Catch
            End Try
            Return "?"
        End Function

        ' ── Cleanup ─────────────────────────────────────────────────────────

        Private Sub Cleanup()
            Try : _ffmpeg?.Kill() : Catch : End Try
            Try : _ffmpeg?.Dispose() : Catch : End Try
            _ffmpeg = Nothing

            Try
                If _ws IsNot Nothing AndAlso _ws.State = WebSocketState.Open Then
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "",
                                   CancellationToken.None).Wait(2000)
                End If
            Catch
            End Try
            Try : _ws?.Dispose() : Catch : End Try
            _ws = Nothing

            SyncLock _lock
                _running = False
            End SyncLock

            ' Only revert to Idle from Running/Connecting.  Failed/Disabled
            ' should stay on the panel so the operator can still see WHY.
            If StreamLog.State = StreamState.Running OrElse StreamLog.State = StreamState.Connecting Then
                StreamLog.Info($"Stream stopped cleanly after {_bytesSent} bytes")
                StreamLog.SetState(StreamState.Idle)
            End If
        End Sub

        ' ── FFmpeg discovery ─────────────────────────────────────────────────

        Private Shared Function FindFfmpeg() As String
            ' 1. Beside the running exe (bundled)
            Dim beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe")
            If File.Exists(beside) Then Return beside

            ' 2. System PATH
            Try
                Dim psi As New ProcessStartInfo("where.exe", "ffmpeg") With {
                    .RedirectStandardOutput = True,
                    .UseShellExecute        = False,
                    .CreateNoWindow         = True
                }
                Using p = Process.Start(psi)
                    Dim line = p?.StandardOutput.ReadLine()?.Trim()
                    If Not String.IsNullOrEmpty(line) AndAlso File.Exists(line) Then
                        Return line
                    End If
                End Using
            Catch
            End Try

            Return Nothing
        End Function

        ' ── IDisposable ──────────────────────────────────────────────────────

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                StopStream()
            End If
        End Sub

    End Class

End Namespace
