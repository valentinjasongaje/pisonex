Imports Microsoft.Win32

Namespace Config
    Public Module AppConfig
        Private Const REG_KEY As String = "SOFTWARE\PisoNet\Client"

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
                Return 1   ' Default if not configured
            End Get
        End Property

        Public ReadOnly Property HeartbeatIntervalMs As Integer
            Get
                Return 10_000   ' 10 seconds
            End Get
        End Property

        Public Sub SaveServerUrl(url As String)
            WriteReg("ServerUrl", url)
        End Sub

        Public Sub SavePCNumber(number As Integer)
            WriteReg("PCNumber", number.ToString())
        End Sub

        Private Function ReadReg(key As String) As String
            Try
                Dim rk = Registry.LocalMachine.OpenSubKey(REG_KEY)
                Return If(rk?.GetValue(key)?.ToString(), "")
            Catch
                Return ""
            End Try
        End Function

        Private Sub WriteReg(key As String, value As String)
            Try
                Dim rk = Registry.LocalMachine.CreateSubKey(REG_KEY, True)
                rk?.SetValue(key, value)
            Catch ex As Exception
                ' May need elevation — fall back to CurrentUser
                Dim rk = Registry.CurrentUser.CreateSubKey(REG_KEY, True)
                rk?.SetValue(key, value)
            End Try
        End Sub
    End Module
End Namespace
