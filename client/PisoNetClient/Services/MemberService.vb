Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports PisoNetClient.Config

Namespace Services

    Public Class MemberRegisterResponse
        Public Property success As Boolean
        Public Property user_id As Integer
        Public Property username As String
        Public Property absorbed_seconds As Integer
        Public Property error As String
    End Class

    Public Class MemberLoginResponse
        Public Property success As Boolean
        Public Property balance_seconds As Integer
        Public Property absorbed_seconds As Integer
        Public Property error As String
    End Class

    Public Class MemberLogoutResponse
        Public Property success As Boolean
        Public Property remaining_seconds As Integer
        Public Property deducted_seconds As Integer
        Public Property error As String
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

        Public Async Function RegisterAsync(pcNumber As Integer, username As String, password As String) As Task(Of MemberRegisterResponse)
            Try
                Dim body = New With {
                    .username = username,
                    .password = password,
                    .pc_number = pcNumber
                }
                Dim json = JsonSerializer.Serialize(body)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim response = Await _client.PostAsync($"{_baseUrl}/api/member/register", content)
                Dim responseJson = Await response.Content.ReadAsStringAsync()
                Dim options = New JsonSerializerOptions() With {.PropertyNameCaseInsensitive = True}
                Return JsonSerializer.Deserialize(Of MemberRegisterResponse)(responseJson, options)
            Catch ex As Exception
                Return New MemberRegisterResponse() With {
                    .success = False,
                    .[error] = $"Connection error: {ex.Message}"
                }
            End Try
        End Function

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
                Return New MemberLogoutResponse() With {
                    .success = False,
                    .[error] = $"Connection error: {ex.Message}"
                }
            End Try
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _client.Dispose()
                _disposed = True
            End If
        End Sub
    End Class

End Namespace
