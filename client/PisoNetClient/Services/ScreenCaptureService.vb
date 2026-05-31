Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Timers
Imports System.Windows.Forms
Imports PisoNetClient.Config

Namespace Services

    ''' <summary>
    ''' Captures the primary screen on a configurable interval and uploads a JPEG
    ''' to the server so admins can monitor PCs remotely.
    ''' When the server requests a fast interval (stream mode), resolution and
    ''' quality are reduced automatically so captures stay within the frame budget.
    ''' Timer is sequential (AutoReset=False, restarted after each upload) so
    ''' frames never pile up regardless of how fast the interval is.
    ''' </summary>
    Public Class ScreenCaptureService
        Implements IDisposable

        Private ReadOnly _api     As ApiService
        Private ReadOnly _session As SessionManager
        Private _timer            As System.Timers.Timer
        Private _disposed         As Boolean = False

        ' True while a capture+upload is in progress — guards re-entrancy.
        Private _capturing As Boolean = False

        ' Stream mode: interval requested by server is < 500 ms.
        ' Lower width + quality so each frame is captured and uploaded in < 33 ms.
        Private Const STREAM_WIDTH   As Integer = 640
        Private Const STREAM_QUALITY As Integer = 50

        Public Sub New(api As ApiService, session As SessionManager)
            _api     = api
            _session = session
        End Sub

        Public Sub Start()
            Dim intervalMs = AppConfig.ScreenCaptureIntervalSec * 1_000
            _timer = New System.Timers.Timer(intervalMs)
            AddHandler _timer.Elapsed, AddressOf OnCaptureTick
            _timer.AutoReset = False   ' sequential — restarted after each upload
            _timer.Start()
        End Sub

        Private Async Sub OnCaptureTick(sender As Object, e As ElapsedEventArgs)
            If _disposed Then Return
            If Not AppConfig.ScreenCaptureEnabled Then
                _timer?.Start()   ' keep ticking even when disabled
                Return
            End If

            ' Guard against re-entrancy (should not happen with AutoReset=False,
            ' but SetIntervalMs can restart the timer while a capture is running).
            If _capturing Then Return
            _capturing = True

            Try
                Dim streamMode = _timer.Interval < 500
                Dim jpeg = CaptureScreen(streamMode)
                Await _api.UploadScreenshotAsync(jpeg)
            Catch
                ' Silently skip — network may be temporarily down
            Finally
                _capturing = False
                If Not _disposed Then _timer?.Start()   ' schedule next frame
            End Try
        End Sub

        ''' <summary>
        ''' Captures the primary screen and returns JPEG bytes.
        ''' Stream mode: 640 px wide, quality 50 (fast, small).
        ''' Normal mode: 1280 px wide, quality from AppConfig.
        ''' </summary>
        Private Function CaptureScreen(streamMode As Boolean) As Byte()
            Dim bounds = Screen.PrimaryScreen.Bounds

            Dim targetW = If(streamMode, STREAM_WIDTH, 1280)
            Dim targetH = CInt(bounds.Height * (CDbl(targetW) / bounds.Width))
            Dim quality = If(streamMode, STREAM_QUALITY, AppConfig.ScreenCaptureQuality)

            ' For stream mode use a faster interpolation — the lower resolution
            ' already provides enough sharpness at 640px.
            Dim interpolation = If(streamMode,
                Drawing2D.InterpolationMode.Bilinear,
                Drawing2D.InterpolationMode.HighQualityBicubic)

            Using bmp = New Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb)
                Using g = Graphics.FromImage(bmp)
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size)
                End Using

                Using scaled = New Bitmap(targetW, targetH, PixelFormat.Format32bppArgb)
                    Using g2 = Graphics.FromImage(scaled)
                        g2.InterpolationMode  = interpolation
                        g2.CompositingQuality = Drawing2D.CompositingQuality.HighSpeed
                        g2.SmoothingMode      = Drawing2D.SmoothingMode.None
                        g2.DrawImage(bmp, New Rectangle(0, 0, targetW, targetH))
                    End Using
                    Using ms = New MemoryStream()
                        Dim codec = GetJpegCodec()
                        Using ep = New EncoderParameters(1)
                            ep.Param(0) = New EncoderParameter(Encoder.Quality, CLng(quality))
                            scaled.Save(ms, codec, ep)
                        End Using
                        Return ms.ToArray()
                    End Using
                End Using
            End Using
        End Function

        Private Shared Function GetJpegCodec() As ImageCodecInfo
            For Each codec In ImageCodecInfo.GetImageEncoders()
                If codec.MimeType = "image/jpeg" Then Return codec
            Next
            Return Nothing
        End Function

        ''' <summary>
        ''' Reschedule the capture timer to a server-requested interval.
        ''' Pass 0 to reset to the admin-configured ScreenCaptureIntervalSec.
        ''' Stream mode (interval &lt; 500 ms) automatically uses lower resolution/quality.
        ''' </summary>
        Public Sub SetIntervalMs(ms As Integer)
            If _timer Is Nothing OrElse _disposed Then Return
            Dim target = If(ms > 0, ms, AppConfig.ScreenCaptureIntervalSec * 1_000)
            If _timer.Interval <> target Then
                _timer.Stop()
                _timer.Interval = target
                _timer.Start()
            End If
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                _timer?.Stop()
                _timer?.Dispose()
            End If
        End Sub

    End Class

End Namespace
