Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports PisoNetClient.Config

Namespace Services

    ' These DTOs are excluded from obfuscation: System.Text.Json maps
    ' JSON keys to the property names below, so renaming would break member
    ' login/logout/change-password deserialization. DTOs hold no logic worth
    ' protecting, so full exclusion is safe.
    <System.Reflection.Obfuscation(Exclude:=True, ApplyToMembers:=True)>
    Public Class MemberLoginResponse
        Public Property success As Boolean
        Public Property balance_seconds As Integer
        Public Property absorbed_seconds As Integer
        Public Property must_change_password As Boolean
        Public Property [error] As String
    End Class

    <System.Reflection.Obfuscation(Exclude:=True, ApplyToMembers:=True)>
    Public Class MemberLogoutResponse
        Public Property success As Boolean
        Public Property remaining_seconds As Integer
        Public Property deducted_seconds As Integer
        Public Property [error] As String
    End Class

    <System.Reflection.Obfuscation(Exclude:=True, ApplyToMembers:=True)>
    Public Class MemberChangePasswordResponse
        Public Property success As Boolean
        Public Property [error] As String
    End Class

    Public Class MemberService
        Implements IDisposable

        Private ReadOnly _client As HttpClient
        Private ReadOnly _baseUrl As String
        Private _disposed As Boolean = False

        Public Sub New(baseUrl As String)
            _baseUrl = baseUrl.TrimEnd("/"c)
            _client = New HttpClient() With {
                .Timeout = TimeSpan.FromSeconds(8)
            }
            Dim key = AppConfig.ApiKey
            If Not String.IsNullOrEmpty(key) Then
                _client.DefaultRequestHeaders.Add("X-API-Key", key)
            End If
        End Sub

        Public Async Function LoginAsync(pcNumber As Integer, username As String, password As String) As Task(Of MemberLoginResponse)
            Try
                Dim body = New With {
                    .pc_number = pcNumber,
                    .username = username,
                    .password = password
                }
                Dim json = JsonSerializer.Serialize(body)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim response = Await _client.PostAsync($"{_baseUrl}/api/member/login", content)
                Dim responseJson = Await response.Content.ReadAsStringAsync()
                Dim options = New JsonSerializerOptions() With {.PropertyNameCaseInsensitive = True}
                Return JsonSerializer.Deserialize(Of MemberLoginResponse)(responseJson, options)
            Catch ex As Exception
                LogMemberServiceException("LoginAsync", ex)
                Return New MemberLoginResponse() With {
                    .success = False,
                    .[error] = $"Connection error: {ex.Message}"
                }
            End Try
        End Function

        Public Async Function LogoutAsync(pcNumber As Integer) As Task(Of MemberLogoutResponse)
            Try
                Dim body = New With {.pc_number = pcNumber}
                Dim json = JsonSerializer.Serialize(body)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim response = Await _client.PostAsync($"{_baseUrl}/api/member/logout", content)
                Dim responseJson = Await response.Content.ReadAsStringAsync()
                Dim options = New JsonSerializerOptions() With {.PropertyNameCaseInsensitive = True}
                Return JsonSerializer.Deserialize(Of MemberLogoutResponse)(responseJson, options)
            Catch ex As Exception
                LogMemberServiceException("LogoutAsync", ex)
                Return New MemberLogoutResponse() With {
                    .success = False,
                    .[error] = $"Connection error: {ex.Message}"
                }
            End Try
        End Function

        ''' <summary>
        ''' Identifies the member server-side via the PC binding set by login.
        '''
        ''' oldPassword is required by the server for a voluntary change (tray icon
        ''' "Change Password" while already logged in) but not for the forced
        ''' first-login change — pass "" (the default) for the forced case; the
        ''' server treats an empty/omitted old password as "skip verification"
        ''' since that flow's login already proved identity.
        ''' </summary>
        Public Async Function ChangePasswordAsync(pcNumber As Integer, newPassword As String,
                                                    Optional oldPassword As String = "") As Task(Of MemberChangePasswordResponse)
            Try
                Dim body = New With {
                    .pc_number = pcNumber,
                    .new_password = newPassword,
                    .old_password = oldPassword
                }
                Dim json = JsonSerializer.Serialize(body)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim response = Await _client.PostAsync($"{_baseUrl}/api/member/change-password", content)
                Dim responseJson = Await response.Content.ReadAsStringAsync()
                Dim options = New JsonSerializerOptions() With {.PropertyNameCaseInsensitive = True}
                Return JsonSerializer.Deserialize(Of MemberChangePasswordResponse)(responseJson, options)
            Catch ex As Exception
                LogMemberServiceException("ChangePasswordAsync", ex)
                Return New MemberChangePasswordResponse() With {
                    .success = False,
                    .[error] = $"Connection error: {ex.Message}"
                }
            End Try
        End Function

        ''' <summary>
        ''' Logs the real exception type/message/stack behind a "Connection
        ''' error: ..." result. ex.Message alone is often a generic, truncated,
        ''' or misleading summary (e.g. System.Text.Json deserialization
        ''' failures reference the target type only in the full message) --
        ''' this is what actually lets a "Connection error" report be diagnosed
        ''' instead of guessed at from a screenshot.
        ''' </summary>
        Private Shared Sub LogMemberServiceException(source As String, ex As Exception)
            Dim inner = If(ex.InnerException Is Nothing, "",
                $" | inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}")
            DiagnosticLog.Write(
                $"MemberService.{source} exception: {ex.GetType().FullName}: {ex.Message}{inner}{Environment.NewLine}{ex.StackTrace}")
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _client.Dispose()
                _disposed = True
            End If
        End Sub
    End Class

End Namespace
