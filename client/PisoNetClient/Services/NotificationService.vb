Imports System.Threading
Imports System.Windows.Forms
Imports PisoNetClient.Config
Imports PisoNetClient.Forms

Namespace Services

    ''' <summary>
    ''' Centralized notification dispatcher.
    ''' • Shows a custom animated toast overlay (when NotificationsEnabled = True).
    ''' • Optionally speaks the message via Windows TTS (when VoiceEnabled = True).
    ''' Show() is thread-safe — marshals to the UI thread automatically.
    ''' Multiple toasts stack vertically so they don't overlap.
    ''' </summary>
    Public Class NotificationService

        Private ReadOnly _syncForm As Form   ' used for UI-thread marshaling
        Private _activeToasts As Integer = 0
        Private Const TOAST_H As Integer = 80
        Private Const TOAST_GAP As Integer = 8

        Public Sub New(syncForm As Form)
            _syncForm = syncForm
        End Sub

        ''' <summary>Show a toast and optionally speak the message.</summary>
        Public Sub Show(title As String, message As String, type As ToastType)
            If Not AppConfig.NotificationsEnabled Then
                ' Voice-only mode — still speak even without visual toast
                If AppConfig.VoiceEnabled Then SpeakAsync($"{title}. {message}")
                Return
            End If

            ' Must create and show WinForms Form on the UI thread
            If _syncForm.InvokeRequired Then
                _syncForm.Invoke(Sub() Show(title, message, type))
                Return
            End If

            Dim offset = _activeToasts * (TOAST_H + TOAST_GAP)
            _activeToasts += 1

            Dim toast = New NotificationToast(title, message, type, offset)
            AddHandler toast.FormClosed, Sub(s, e)
                _activeToasts = Math.Max(0, _activeToasts - 1)
            End Sub
            toast.Show()

            If AppConfig.VoiceEnabled Then SpeakAsync($"{title}. {message}")
        End Sub

        ' ── TTS via Windows SAPI COM (no NuGet required) ─────────────────────

        Private Sub SpeakAsync(text As String)
            ' SAPI.SpVoice is an STA COM object — must run on a dedicated STA thread
            Dim t = New Thread(Sub()
                Try
                    Dim voice = CreateObject("SAPI.SpVoice")
                    voice.Volume = AppConfig.VoiceVolume
                    voice.Rate   = 0   ' normal speed
                    voice.Speak(text, 0)   ' 0 = synchronous on this thread
                Catch
                    ' TTS engine not installed or unavailable — silently skip
                End Try
            End Sub)
            t.IsBackground = True
            t.SetApartmentState(ApartmentState.STA)
            t.Start()
        End Sub

    End Class

End Namespace
