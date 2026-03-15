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

        ''' <summary>How the background image is scaled: Contain | Cover | Stretch (default Contain).</summary>
        Public ReadOnly Property LockBgImageFit As String
            Get
                Dim v = ReadReg("LockBgImageFit")
                If v = "Cover" OrElse v = "Stretch" Then Return v
                Return "Contain"
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

        ' ── Performance metrics ────────────────────────────────────────────
        ''' <summary>Whether to send CPU/RAM/disk/network metrics to the server.</summary>
        Public ReadOnly Property MetricsEnabled As Boolean
            Get
                Return ReadBool("MetricsEnabled", defaultVal:=True)
            End Get
        End Property

        ''' <summary>How often to collect and send metrics, in seconds (5–60).</summary>
        Public ReadOnly Property MetricsIntervalSec As Integer
            Get
                Dim val = ReadReg("MetricsIntervalSec")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 5 AndAlso n <= 60 Then Return n
                Return 10
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
                Return 3
            End Get
        End Property

        ''' <summary>JPEG quality for screenshots (30–100). Higher = clearer but larger upload.</summary>
        Public ReadOnly Property ScreenCaptureQuality As Integer
            Get
                Dim val = ReadReg("ScreenCaptureQuality")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 30 AndAlso n <= 100 Then Return n
                Return 85
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

        ''' <summary>
        ''' Partial name of the preferred SAPI voice (e.g. "Zira", "David", "Aria").
        ''' Empty string = auto-select: prefer the first female voice, then first available.
        ''' </summary>
        Public ReadOnly Property VoiceName As String
            Get
                Return If(ReadReg("VoiceName"), "")
            End Get
        End Property

        ' ── Timer overlay appearance ────────────────────────────────────────
        ''' <summary>ARGB color of the large time text when the session has more than 5 minutes left.</summary>
        Public ReadOnly Property TimerTimeArgb As Integer
            Get
                Dim val = ReadReg("TimerTimeArgb")
                Dim n As Integer
                If Integer.TryParse(val, n) Then Return n
                Return Color.FromArgb(34, 197, 94).ToArgb()   ' default: green
            End Get
        End Property

        ''' <summary>ARGB color of the time text when fewer than 5 minutes remain (low-time warning).</summary>
        Public ReadOnly Property TimerLowTimeArgb As Integer
            Get
                Dim val = ReadReg("TimerLowTimeArgb")
                Dim n As Integer
                If Integer.TryParse(val, n) Then Return n
                Return Color.FromArgb(239, 68, 68).ToArgb()   ' default: red
            End Get
        End Property

        ''' <summary>Show a green/red filled circle in the upper-right of the timer
        ''' instead of (or in addition to) a status text label.</summary>
        Public ReadOnly Property TimerShowConnDot As Boolean
            Get
                Return ReadBool("TimerShowConnDot", defaultVal:=True)
            End Get
        End Property

        ''' <summary>Show the PC number label on the timer overlay.</summary>
        Public ReadOnly Property TimerShowPcLabel As Boolean
            Get
                Return ReadBool("TimerShowPcLabel", defaultVal:=True)
            End Get
        End Property

        ''' <summary>"Above" (default) or "Side" — controls where PC label appears relative to the time.</summary>
        Public ReadOnly Property TimerPcLabelPosition As String
            Get
                Dim val = ReadReg("TimerPcLabelPosition")
                Return If(String.IsNullOrWhiteSpace(val), "Above", val)
            End Get
        End Property

        ' ── Lock screen text appearance ─────────────────────────────────────
        ''' <summary>ARGB color of the main lock message text (default: White).</summary>
        Public ReadOnly Property LockMsgForeArgb As Integer
            Get
                Dim val = ReadReg("LockMsgForeArgb")
                Dim n As Integer
                If Integer.TryParse(val, n) Then Return n
                Return Color.White.ToArgb()
            End Get
        End Property

        ''' <summary>Font size for the main lock message (18–72 pt, default 36).</summary>
        Public ReadOnly Property LockMsgSize As Integer
            Get
                Dim val = ReadReg("LockMsgSize")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 18 AndAlso n <= 72 Then Return n
                Return 36
            End Get
        End Property

        ''' <summary>Horizontal position of main message as % of (screen width – label width).
        ''' 50 = horizontally centered (default).</summary>
        Public ReadOnly Property LockMsgXPct As Integer
            Get
                Dim val = ReadReg("LockMsgXPct")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 0 AndAlso n <= 100 Then Return n
                Return 50
            End Get
        End Property

        ''' <summary>Vertical position of main message as % of (screen height – label height).
        ''' 47 = slightly above center (default).</summary>
        Public ReadOnly Property LockMsgYPct As Integer
            Get
                Dim val = ReadReg("LockMsgYPct")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 0 AndAlso n <= 100 Then Return n
                Return 47
            End Get
        End Property

        ''' <summary>ARGB color of the PC number badge on the lock screen.</summary>
        Public ReadOnly Property LockPcLabelForeArgb As Integer
            Get
                Dim val = ReadReg("LockPcLabelForeArgb")
                Dim n As Integer
                If Integer.TryParse(val, n) Then Return n
                Return Color.FromArgb(100, 120, 160).ToArgb()
            End Get
        End Property

        ''' <summary>Font size for the PC number label on the lock screen (8–24 pt, default 11).</summary>
        Public ReadOnly Property LockPcLabelSize As Integer
            Get
                Dim val = ReadReg("LockPcLabelSize")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 8 AndAlso n <= 76 Then Return n
                Return 11
            End Get
        End Property

        ''' <summary>When True, main message is horizontally centered (ignores LockMsgXPct).</summary>
        Public ReadOnly Property LockMsgCenterX As Boolean
            Get
                Return ReadBool("LockMsgCenterX", defaultVal:=True)
            End Get
        End Property

        ''' <summary>When True, PC label is horizontally centered (ignores LockPcLabelXPct).</summary>
        Public ReadOnly Property LockPcLabelCenterX As Boolean
            Get
                Return ReadBool("LockPcLabelCenterX", defaultVal:=False)
            End Get
        End Property

        ''' <summary>PC label horizontal position as % of screen width (default 4 = near left).</summary>
        Public ReadOnly Property LockPcLabelXPct As Integer
            Get
                Dim val = ReadReg("LockPcLabelXPct")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 0 AndAlso n <= 100 Then Return n
                Return 4
            End Get
        End Property

        ''' <summary>PC label vertical position as % of screen height (default 4 = near top).</summary>
        Public ReadOnly Property LockPcLabelYPct As Integer
            Get
                Dim val = ReadReg("LockPcLabelYPct")
                Dim n As Integer
                If Integer.TryParse(val, n) AndAlso n >= 0 AndAlso n <= 100 Then Return n
                Return 4
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
        Public Sub SaveLockBgImageFit(v As String)
            WriteReg("LockBgImageFit", v)
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
        Public Sub SaveMetricsEnabled(v As Boolean)
            WriteReg("MetricsEnabled", If(v, "1", "0"))
        End Sub
        Public Sub SaveMetricsIntervalSec(n As Integer)
            WriteReg("MetricsIntervalSec", n.ToString())
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
        Public Sub SaveVoiceName(name As String)
            WriteReg("VoiceName", If(name, ""))
        End Sub
        Public Sub SaveTimerTimeArgb(argb As Integer)
            WriteReg("TimerTimeArgb", argb.ToString())
        End Sub
        Public Sub SaveTimerLowTimeArgb(argb As Integer)
            WriteReg("TimerLowTimeArgb", argb.ToString())
        End Sub
        Public Sub SaveTimerShowConnDot(v As Boolean)
            WriteReg("TimerShowConnDot", If(v, "1", "0"))
        End Sub
        Public Sub SaveTimerShowPcLabel(v As Boolean)
            WriteReg("TimerShowPcLabel", If(v, "1", "0"))
        End Sub
        Public Sub SaveTimerPcLabelPosition(pos As String)
            WriteReg("TimerPcLabelPosition", If(pos, "Above"))
        End Sub
        Public Sub SaveLockMsgForeArgb(argb As Integer)
            WriteReg("LockMsgForeArgb", argb.ToString())
        End Sub
        Public Sub SaveLockMsgSize(n As Integer)
            WriteReg("LockMsgSize", n.ToString())
        End Sub
        Public Sub SaveLockMsgXPct(n As Integer)
            WriteReg("LockMsgXPct", n.ToString())
        End Sub
        Public Sub SaveLockMsgCenterX(v As Boolean)
            WriteReg("LockMsgCenterX", If(v, "1", "0"))
        End Sub
        Public Sub SaveLockMsgYPct(n As Integer)
            WriteReg("LockMsgYPct", n.ToString())
        End Sub
        Public Sub SaveLockPcLabelForeArgb(argb As Integer)
            WriteReg("LockPcLabelForeArgb", argb.ToString())
        End Sub
        Public Sub SaveLockPcLabelSize(n As Integer)
            WriteReg("LockPcLabelSize", n.ToString())
        End Sub
        Public Sub SaveLockPcLabelXPct(n As Integer)
            WriteReg("LockPcLabelXPct", n.ToString())
        End Sub
        Public Sub SaveLockPcLabelCenterX(v As Boolean)
            WriteReg("LockPcLabelCenterX", If(v, "1", "0"))
        End Sub
        Public Sub SaveLockPcLabelYPct(n As Integer)
            WriteReg("LockPcLabelYPct", n.ToString())
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
