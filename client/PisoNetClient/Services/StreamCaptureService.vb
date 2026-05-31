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
    ''' If FFmpeg is not found, the service exits silently — the dashboard
    ''' falls back to the MJPEG stream automatically.
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

        Private ReadOnly _baseWsUrl As String   ' e.g. ws://192.168.1.21
        Private ReadOnly _pcNumber  As Integer

        Private _ffmpeg  As Process
        Private _ws      As ClientWebSocket
        Private _cts     As CancellationTokenSource
        Private _disposed As Boolean = False

        Private ReadOnly _lock As New Object()
        Private _running As Boolean = False

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
            _cts = New CancellationTokenSource()
            Task.Run(AddressOf RunAsync)
        End Sub

        Public Sub StopStream()
            SyncLock _lock
                If Not _running Then Return
            End SyncLock
            _cts?.Cancel()
            ' Cleanup() will set _running = False once the task exits
        End Sub

        ' ── Core streaming loop ──────────────────────────────────────────────

        Private Async Function RunAsync() As Task
            Try
                Dim ffmpegPath = FindFfmpeg()
                If String.IsNullOrEmpty(ffmpegPath) Then Return  ' no FFmpeg — silent exit

                ' Connect WebSocket to OPi publish endpoint
                _ws = New ClientWebSocket()
                If Not String.IsNullOrEmpty(AppConfig.ApiKey) Then
                    _ws.Options.SetRequestHeader("X-API-Key", AppConfig.ApiKey)
                End If
                Dim wsUri = New Uri($"{_baseWsUrl}/dashboard/ws/stream/{_pcNumber}/publish")
                Await _ws.ConnectAsync(wsUri, _cts.Token)

                ' Launch FFmpeg — redirect stdout for pipe, stderr discarded
                Dim psi As New ProcessStartInfo(ffmpegPath, FFMPEG_ARGS) With {
                    .RedirectStandardOutput = True,
                    .RedirectStandardError  = True,
                    .UseShellExecute        = False,
                    .CreateNoWindow         = True
                }
                _ffmpeg = Process.Start(psi)
                _ffmpeg.BeginErrorReadLine()   ' drain stderr so it never blocks

                ' Drain incoming WebSocket frames (pings, close) concurrently so the
                ' server's ping/pong keepalive doesn't time out the publish connection.
                Dim drainTask = Task.Run(Async Function()
                    Dim recvBuf = New Byte(1023) {}
                    Try
                        While Not _cts.Token.IsCancellationRequested
                            Dim result = Await _ws.ReceiveAsync(
                                New ArraySegment(Of Byte)(recvBuf), _cts.Token)
                            If result.MessageType = WebSocketMessageType.Close Then Exit While
                        End While
                    Catch
                        ' Connection closed — exit silently
                    End Try
                End Function)

                ' Pipe FFmpeg stdout → WebSocket in 64 KB chunks
                Dim buf       = New Byte(65535) {}
                Dim outStream = _ffmpeg.StandardOutput.BaseStream
                While Not _cts.Token.IsCancellationRequested
                    Dim n = Await outStream.ReadAsync(buf, 0, buf.Length, _cts.Token)
                    If n = 0 Then Exit While   ' FFmpeg exited
                    Await _ws.SendAsync(
                        New ArraySegment(Of Byte)(buf, 0, n),
                        WebSocketMessageType.Binary,
                        endOfMessage:=True,    ' each chunk is a complete message for the relay
                        cancellationToken:=_cts.Token)
                End While

            Catch ex As OperationCanceledException
                ' Normal stop — swallow
            Catch
                ' Network drop, FFmpeg crash, etc. — swallow; MJPEG fallback covers view
            Finally
                Cleanup()
            End Try
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
