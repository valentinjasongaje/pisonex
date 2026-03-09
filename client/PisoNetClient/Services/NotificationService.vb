Imports System.Threading
Imports System.Windows.Forms
Imports PisoNetClient.Config
Imports PisoNetClient.Forms

Namespace Services

    ''' <summary>
    ''' Centralized notification dispatcher.
    ''' • Shows a custom animated toast overlay (when NotificationsEnabled = True).
    ''' • Optionally speaks the message via Windows TTS SAPI (when VoiceEnabled = True).
    '''
    ''' Voice selection priority:
    '''   1. Partial name match from AppConfig.VoiceName (case-insensitive)
    '''   2. First installed female voice
    '''   3. System default
    '''
    ''' Show() is thread-safe — marshals to the UI thread automatically.
    ''' Multiple toasts stack vertically so they don't overlap.
    ''' </summary>
    Public Class NotificationService

        Private ReadOnly _syncForm As Form   ' used for UI-thread marshaling
        Private _activeToasts As Integer = 0
        Private Const TOAST_H   As Integer = 84   ' must match NotificationToast.FORM_H
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

        Private Shared Sub SpeakAsync(text As String)
            ' SAPI.SpVoice is an STA COM object — must run on a dedicated STA thread
            Dim t = New Thread(Sub()
                Try
                    Dim voice = CreateObject("SAPI.SpVoice")
                    voice.Volume = AppConfig.VoiceVolume
                    voice.Rate   = -1   ' slightly slower than default → more natural pacing
                    SelectVoice(voice)
                    voice.Speak(text, 0)   ' 0 = synchronous on this thread
                Catch
                    ' SAPI engine not available — silently skip
                End Try
            End Sub)
            t.IsBackground = True
            t.SetApartmentState(ApartmentState.STA)
            t.Start()
        End Sub

        ''' <summary>
        ''' Picks the best available SAPI voice in priority order:
        '''   1. Partial name match from AppConfig.VoiceName
        '''   2. First installed female voice
        '''   3. Keep system default
        ''' </summary>
        Private Shared Sub SelectVoice(voice As Object)
            Try
                Dim voices     = voice.GetVoices()
                Dim count      = CInt(voices.Count)
                Dim configured = AppConfig.VoiceName.Trim()

                ' Pass 1 — configured voice name (partial, case-insensitive)
                If Not String.IsNullOrEmpty(configured) Then
                    For i = 0 To count - 1
                        Dim token = voices.Item(i)
                        Dim desc  = CStr(token.GetDescription())
                        If desc.IndexOf(configured, StringComparison.OrdinalIgnoreCase) >= 0 Then
                            voice.Voice = token
                            Return
                        End If
                    Next
                End If

                ' Pass 2 — any female voice
                For i = 0 To count - 1
                    Dim token = voices.Item(i)
                    Try
                        Dim gender = CStr(token.GetAttribute("Gender"))
                        If gender.Equals("Female", StringComparison.OrdinalIgnoreCase) Then
                            voice.Voice = token
                            Return
                        End If
                    Catch
                        ' Token may not expose Gender — skip
                    End Try
                Next

                ' Pass 3 — keep system default (do nothing)
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Returns display names of all installed SAPI voices.
        ''' Used by AdminPanel to populate the voice-selection dropdown.
        ''' Returns an empty list if SAPI is unavailable.
        ''' </summary>
        Public Shared Function GetInstalledVoiceNames() As List(Of String)
            Dim names = New List(Of String)()
            Try
                Dim voice  = CreateObject("SAPI.SpVoice")
                Dim voices = voice.GetVoices()
                For i = 0 To CInt(voices.Count) - 1
                    names.Add(CStr(voices.Item(i).GetDescription()))
                Next
            Catch
            End Try
            Return names
        End Function

    End Class

End Namespace
