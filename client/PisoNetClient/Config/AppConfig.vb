Imports Microsoft.Win32
Imports System.Drawing

Namespace Config
    Public Module AppConfig
        Private Const REG_KEY As String = "SOFTWARE\PisoNet\Client"

        ' ── Connection ────────────────────────────────────────────────────
        Public ReadOnly Property ServerUrl As String
            Get
                Dim val = ReadReg("ServerUrl")
                Return If(String.IsNullOrWhiteSpace(val), "http://192.168.1.21:8000", val)
            End Get
        End Property

        Public ReadOnly Property PCNumber As Integer
            Get
                Dim val = ReadReg("PCNumber")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n > 0 Then Return n
                Return 1
            End Get
        End Property

        Public ReadOnly Property HeartbeatIntervalMs As Integer
            Get
                Return 5_000
            End Get
        End Property

        ' ── Security ──────────────────────────────────────────────────────
        ''' <summary>Admin PIN to access the admin panel or exit the app.</summary>
        Public ReadOnly Property AdminPin As String
            Get
                Dim val = ReadReg("AdminPin")
                Return If(String.IsNullOrWhiteSpace(val), "1234", val)
            End Get
        End Property

        ' ── Lock screen appearance ─────────────────────────────────────────
        Public ReadOnly Property LockBgArgb As Integer
            Get
                Dim val = ReadReg("LockBgArgb")
                Dim n As Integer
                If Integer.TryParse(val, n) Then Return n
                Return Color.FromArgb(10, 14, 23).ToArgb()   ' default dark navy
            End Get
        End Property

        Public ReadOnly Property LockBgImagePath As String
            Get
                Return If(ReadReg("LockBgImagePath"), "")
            End Get
        End Property

        Public ReadOnly Property LockMessage As String
            Get
                Dim val = ReadReg("LockMessage")
                Return If(String.IsNullOrWhiteSpace(val), "Insert Coins to Start", val)
            End Get
        End Property

        ' ── Windows restrictions ───────────────────────────────────────────
        Public ReadOnly Property DisableTaskManager As Boolean
            Get
                Return ReadBool("DisableTaskManager", defaultVal:=True)
            End Get
        End Property

        Public ReadOnly Property DisableCmdPrompt As Boolean
            Get
                Return ReadBool("DisableCmdPrompt", defaultVal:=True)
            End Get
        End Property

        Public ReadOnly Property DisableRegistryTools As Boolean
            Get
                Return ReadBool("DisableRegistryTools", defaultVal:=True)
            End Get
        End Property

        Public ReadOnly Property DisableRunDialog As Boolean
            Get
                Return ReadBool("DisableRunDialog", defaultVal:=False)
            End Get
        End Property

        ' ── Low-time warnings ──────────────────────────────────────────────
        Public ReadOnly Property WarnAt5Min As Boolean
            Get
                Return ReadBool("WarnAt5Min", defaultVal:=True)
            End Get
        End Property

        Public ReadOnly Property WarnAt1Min As Boolean
            Get
                Return ReadBool("WarnAt1Min", defaultVal:=True)
            End Get
        End Property

        ' ── Screen monitoring ──────────────────────────────────────────────
        ''' <summary>Whether to upload screenshots for remote admin monitoring.</summary>
        Public ReadOnly Property ScreenCaptureEnabled As Boolean
            Get
                Return ReadBool("ScreenCaptureEnabled", defaultVal:=True)
            End Get
        End Property

        ''' <summary>How often to capture a screenshot, in seconds (3–60).</summary>
        Public ReadOnly Property ScreenCaptureIntervalSec As Integer
            Get
                Dim val = ReadReg("ScreenCaptureIntervalSec")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 3 AndAlso n <= 60 Then Return n
                Return 5
            End Get
        End Property

        ''' <summary>JPEG quality for screenshots (30–95). Higher = clearer but larger upload.</summary>
        Public ReadOnly Property ScreenCaptureQuality As Integer
            Get
                Dim val = ReadReg("ScreenCaptureQuality")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 30 AndAlso n <= 95 Then Return n
                Return 75
            End Get
        End Property

        ' ── Notifications ─────────────────────────────────────────────────────
        ''' <summary>Master toggle — when False all in-app toast notifications are suppressed.</summary>
        Public ReadOnly Property NotificationsEnabled As Boolean
            Get
                Return ReadBool("NotificationsEnabled", defaultVal:=True)
            End Get
        End Property

        ''' <summary>When True, a TTS voice message is spoken for every notification.</summary>
        Public ReadOnly Property VoiceEnabled As Boolean
            Get
                Return ReadBool("VoiceEnabled", defaultVal:=False)
            End Get
        End Property

        ''' <summary>TTS volume 10–100.</summary>
        Public ReadOnly Property VoiceVolume As Integer
            Get
                Dim val = ReadReg("VoiceVolume")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 10 AndAlso n <= 100 Then Return n
                Return 80
            End Get
        End Property

        ' ── First-run flag ─────────────────────────────────────────────────
        Public ReadOnly Property IsConfigured As Boolean
            Get
                Return ReadBool("IsConfigured", defaultVal:=False)
            End Get
        End Property

        ' ── Save helpers ───────────────────────────────────────────────────
        Public Sub SaveServerUrl(url As String)
            WriteReg("ServerUrl", url)
        End Sub
        Public Sub SavePCNumber(n As Integer)
            WriteReg("PCNumber", n.ToString())
        End Sub
        Public Sub SaveAdminPin(pin As String)
            WriteReg("AdminPin", pin)
        End Sub
        Public Sub SaveLockBgArgb(argb As Integer)
            WriteReg("LockBgArgb", argb.ToString())
        End Sub
        Public Sub SaveLockBgImagePath(p As String)
            WriteReg("LockBgImagePath", p)
        End Sub
        Public Sub SaveLockMessage(msg As String)
            WriteReg("LockMessage", msg)
        End Sub
        Public Sub SaveDisableTaskManager(v As Boolean)
            WriteReg("DisableTaskManager", If(v, "1", "0"))
        End Sub
        Public Sub SaveDisableCmdPrompt(v As Boolean)
            WriteReg("DisableCmdPrompt", If(v, "1", "0"))
        End Sub
        Public Sub SaveDisableRegistryTools(v As Boolean)
            WriteReg("DisableRegistryTools", If(v, "1", "0"))
        End Sub
        Public Sub SaveDisableRunDialog(v As Boolean)
            WriteReg("DisableRunDialog", If(v, "1", "0"))
        End Sub
        Public Sub SaveWarnAt5Min(v As Boolean)
            WriteReg("WarnAt5Min", If(v, "1", "0"))
        End Sub
        Public Sub SaveWarnAt1Min(v As Boolean)
            WriteReg("WarnAt1Min", If(v, "1", "0"))
        End Sub
        Public Sub SaveIsConfigured(v As Boolean)
            WriteReg("IsConfigured", If(v, "1", "0"))
        End Sub
        Public Sub SaveScreenCaptureEnabled(v As Boolean)
            WriteReg("ScreenCaptureEnabled", If(v, "1", "0"))
        End Sub
        Public Sub SaveScreenCaptureIntervalSec(n As Integer)
            WriteReg("ScreenCaptureIntervalSec", n.ToString())
        End Sub
        Public Sub SaveScreenCaptureQuality(n As Integer)
            WriteReg("ScreenCaptureQuality", n.ToString())
        End Sub
        Public Sub SaveNotificationsEnabled(v As Boolean)
            WriteReg("NotificationsEnabled", If(v, "1", "0"))
        End Sub
        Public Sub SaveVoiceEnabled(v As Boolean)
            WriteReg("VoiceEnabled", If(v, "1", "0"))
        End Sub
        Public Sub SaveVoiceVolume(n As Integer)
            WriteReg("VoiceVolume", n.ToString())
        End Sub
        ''' <summary>Saves own exe path so the watchdog can find it after a restart.</summary>
        Public Sub SaveClientExePath(path As String)
            WriteReg("ClientExePath", path)
        End Sub
        ''' <summary>
        ''' Stamps the current UTC Unix timestamp so the watchdog knows the admin
        ''' intentionally shut down and should not restart for ~5 minutes.
        ''' </summary>
        Public Sub SaveGracefulShutdown()
            WriteReg("ShutdownAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        End Sub

        ' ── Registry helpers ───────────────────────────────────────────────
        Private Function ReadReg(key As String) As String
            Try
                Dim rk = Registry.LocalMachine.OpenSubKey(REG_KEY)
                Dim v = rk?.GetValue(key)?.ToString()
                If v IsNot Nothing Then Return v
            Catch
            End Try
            Try
                Dim rk = Registry.CurrentUser.OpenSubKey(REG_KEY)
                Return If(rk?.GetValue(key)?.ToString(), "")
            Catch
                Return ""
            End Try
        End Function

        Private Function ReadBool(key As String, defaultVal As Boolean) As Boolean
            Dim val = ReadReg(key)
            If String.IsNullOrEmpty(val) Then Return defaultVal
            Return val = "1" OrElse val.ToLower() = "true"
        End Function

        Private Sub WriteReg(key As String, value As String)
            Try
                Dim rk = Registry.LocalMachine.CreateSubKey(REG_KEY, True)
                rk?.SetValue(key, value)
            Catch
                ' Elevation not available — fall back to CurrentUser
                Try
                    Dim rk = Registry.CurrentUser.CreateSubKey(REG_KEY, True)
                    rk?.SetValue(key, value)
                Catch
                End Try
            End Try
        End Sub
    End Module
End Namespace
