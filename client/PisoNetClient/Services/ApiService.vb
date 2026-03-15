Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Net.NetworkInformation

Namespace Services

    Public Class HeartbeatResponse
        Public Property is_locked As Boolean
        Public Property remaining_minutes As Integer
        Public Property remaining_seconds As Integer
        Public Property session_token As String
        Public Property time_added_minutes As Integer
    End Class

    Public Class ApiService
        Implements IDisposable

        Private ReadOnly _client As HttpClient
        Private ReadOnly _baseUrl As String
        Private ReadOnly _pcNumber As Integer
        Private _disposed As Boolean = False

        Public Sub New(baseUrl As String, pcNumber As Integer)
            _baseUrl = baseUrl.TrimEnd("/"c)
            _pcNumber = pcNumber
            _client = New HttpClient() With {
                .Timeout = TimeSpan.FromSeconds(8)
            }
        End Sub

        ''' <summary>
        ''' Registers this PC with the server. Called once on startup.
        ''' Safe to call repeatedly — server handles duplicates.
        ''' </summary>
        Public Async Function RegisterAsync() As Task(Of Boolean)
            Try
                Dim mac = GetMacAddress()
                Dim url = $"{_baseUrl}/api/pc/register?pc_number={_pcNumber}&mac_address={mac}"
                Dim response = Await _client.PostAsync(url, Nothing)
                Return response.IsSuccessStatusCode
            Catch
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Sends a heartbeat to the server and gets back session state.
        ''' Returns Nothing if the server is unreachable.
        ''' </summary>
        Public Async Function HeartbeatAsync() As Task(Of HeartbeatResponse)
            Try
                Dim url = $"{_baseUrl}/api/pc/heartbeat/{_pcNumber}"
                Dim response = Await _client.PostAsync(url, Nothing)
                If Not response.IsSuccessStatusCode Then Return Nothing

                Dim json = Await response.Content.ReadAsStringAsync()
                Dim options = New JsonSerializerOptions() With {
                    .PropertyNameCaseInsensitive = True
                }
                Return JsonSerializer.Deserialize(Of HeartbeatResponse)(json, options)
            Catch
                Return Nothing   ' Network unreachable — caller handles this
            End Try
        End Function

        ''' <summary>
        ''' Sends a JSON performance metrics snapshot to the server.
        ''' Fire-and-forget — returns False silently on failure.
        ''' </summary>
        Public Async Function SendMetricsAsync(metrics As Object) As Task(Of Boolean)
            Try
                Dim options = New JsonSerializerOptions() With {
                    .PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                }
                Dim json = JsonSerializer.Serialize(metrics, options)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim url = $"{_baseUrl}/api/pc/{_pcNumber}/metrics"
                Dim response = Await _client.PostAsync(url, content)
                Return response.IsSuccessStatusCode
            Catch
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Uploads a JPEG screenshot to the server for remote monitoring.
        ''' Fire-and-forget — returns False silently on failure.
        ''' </summary>
        Public Async Function UploadScreenshotAsync(jpegBytes As Byte()) As Task(Of Boolean)
            Try
                Dim content = New ByteArrayContent(jpegBytes)
                content.Headers.ContentType =
                    New System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg")
                Dim url = $"{_baseUrl}/api/pc/{_pcNumber}/screenshot"
                Dim response = Await _client.PostAsync(url, content)
                Return response.IsSuccessStatusCode
            Catch
                Return False
            End Try
        End Function

        Private Shared Function GetMacAddress() As String
            For Each nic In NetworkInterface.GetAllNetworkInterfaces()
                If nic.OperationalStatus = OperationalStatus.Up AndAlso
                   nic.NetworkInterfaceType <> NetworkInterfaceType.Loopback Then
                    Return nic.GetPhysicalAddress().ToString()
                End If
            Next
            Return "UNKNOWN"
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _client.Dispose()
                _disposed = True
            End If
        End Sub
    End Class

End Namespace
