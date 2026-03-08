Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Timers
Imports System.Windows.Forms
Imports PisoNetClient.Config

Namespace Services

    ''' <summary>
    ''' Captures the primary screen every 5 seconds and uploads a JPEG
    ''' thumbnail to the server so admins can monitor PCs remotely.
    ''' Captures continuously while enabled — including the lock screen.
    ''' </summary>
    Public Class ScreenCaptureService
        Implements IDisposable

        Private ReadOnly _api     As ApiService
        Private ReadOnly _session As SessionManager
        Private _timer            As System.Timers.Timer
        Private _disposed         As Boolean = False

        Public Sub New(api As ApiService, session As SessionManager)
            _api     = api
            _session = session
        End Sub

        Public Sub Start()
            Dim intervalMs = AppConfig.ScreenCaptureIntervalSec * 1_000
            _timer = New System.Timers.Timer(intervalMs)
            AddHandler _timer.Elapsed, AddressOf OnCaptureTick
            _timer.AutoReset = True
            _timer.Start()
        End Sub

        Private Async Sub OnCaptureTick(sender As Object, e As ElapsedEventArgs)
            If Not AppConfig.ScreenCaptureEnabled Then Return
            Try
                Dim jpeg = CaptureScreen()
                Await _api.UploadScreenshotAsync(jpeg)
            Catch
                ' Silently skip — network may be temporarily down
            End Try
        End Sub

        ''' <summary>Captures primary screen, scales to 960 px wide, returns JPEG bytes.</summary>
        Private Function CaptureScreen() As Byte()
            Dim bounds = Screen.PrimaryScreen.Bounds

            Using bmp = New Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb)
                Using g = Graphics.FromImage(bmp)
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size)
                End Using

                ' Scale to 960 wide, maintain aspect ratio
                Dim targetW = 960
                Dim targetH = CInt(bounds.Height * (960.0 / bounds.Width))

                Using scaled = New Bitmap(bmp, New Size(targetW, targetH))
                    Using ms = New MemoryStream()
                        Dim codec = GetJpegCodec()
                        Using ep = New EncoderParameters(1)
                            ep.Param(0) = New EncoderParameter(Encoder.Quality, CLng(AppConfig.ScreenCaptureQuality))
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

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                _timer?.Stop()
                _timer?.Dispose()
            End If
        End Sub

    End Class

End Namespace
