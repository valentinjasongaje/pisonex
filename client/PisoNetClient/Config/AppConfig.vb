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
