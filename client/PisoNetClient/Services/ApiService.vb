Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Net.NetworkInformation
Imports PisoNetClient.Config

Namespace Services

    ' Excluded from obfuscation: System.Text.Json maps JSON keys to these
    ' property names. Renaming them would break heartbeat deserialization.
    ' DTOs hold no logic worth protecting, so full exclusion is safe.
    <System.Reflection.Obfuscation(Exclude:=True, ApplyToMembers:=True)>
    Public Class HeartbeatResponse
        Public Property is_locked As Boolean
        Public Property remaining_seconds As Integer
        Public Property session_token As String
        Public Property time_added_seconds As Integer
        ' Remote control fields — populated by server when admin sends actions
        Public Property pending_command As String   ' "shutdown"|"restart"|"lock"|"open_url"
        Public Property command_payload As String   ' URL/path for open_url
        Public Property admin_message As String     ' per-PC message, shown once
        Public Property announcement As String      ' shop-wide broadcast (persistent)
        Public Property coin_slot_enabled As Boolean = True
        ' Persistent "Traditional Café Mode" business-model toggle (admin-set
        ' via Settings). Distinct from coin_slot_enabled above, which is a
        ' transient runtime pause/resume flag. Defaults False so
        ' cached/pre-first-heartbeat state never accidentally hides the
        ' Insert Coin / Add Time UI.
        Public Property traditional_mode_enabled As Boolean = False
        ' Server-pushed wallpaper
        Public Property wallpaper_url As String
        Public Property wallpaper_hash As String
        ' Membership fields
        Public Property membership_enabled As Boolean = False
        Public Property absorption_enabled As Boolean = False
        Public Property member_username As String
        Public Property member_balance_seconds As Integer = 0
        Public Property member_can_logout As Boolean = False
        Public Property zero_time_logout_seconds As Integer = 0
        Public Property idle_shutdown_seconds As Integer = 0
        Public Property receiving_coins As Boolean = False
        Public Property coin_progress_pesos As Integer = 0
        Public Property coin_progress_seconds As Integer = 0
        Public Property minimum_logout_minutes As Integer = 0
        ' Live-stream hint: >0 = server wants client to capture at this interval (ms);
        ' 0 = use the client's own configured interval.
        Public Property capture_interval_ms As Integer = 0
    End Class

    Public Class ApiService
        Implements IDisposable

        Private ReadOnly _client As HttpClient
        Private ReadOnly _baseUrl As String
        Private ReadOnly _pcNumber As Integer
        Private _disposed As Boolean = False

        ' Heartbeat failure diagnostics + self-heal (see HeartbeatAsync).
        Private _lastFailure As String = ""
        Private _lastReRegister As DateTime = DateTime.MinValue

        ''' <summary>HTTP base URL converted to WebSocket scheme (ws:// or wss://).</summary>
        Public ReadOnly Property BaseWsUrl As String
            Get
                Return _baseUrl.Replace("https://", "wss://").Replace("http://", "ws://")
            End Get
        End Property

        Public Sub New(baseUrl As String, pcNumber As Integer)
            _baseUrl = baseUrl.TrimEnd("/"c)
            _pcNumber = pcNumber
            _client = New HttpClient() With {
                .Timeout = TimeSpan.FromSeconds(8)
            }
            ' Attach shared API key if configured (matches CLIENT_API_KEY in server .env)
            Dim key = AppConfig.ApiKey
            If Not String.IsNullOrEmpty(key) Then
                _client.DefaultRequestHeaders.Add("X-API-Key", key)
            End If
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
                If Not response.IsSuccessStatusCode Then
                    DiagnosticLog.Write($"Register: server answered HTTP {CInt(response.StatusCode)} for PC {_pcNumber}")
                End If
                Return response.IsSuccessStatusCode
            Catch ex As Exception
                DiagnosticLog.Write($"Register: {ex.GetType().Name}: {ex.Message}")
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Asks the server to open the coin slot for this PC so the user can insert coins.
        ''' Returns (True, "") on success, or (False, reason) if the request was rejected.
        ''' reason is the server's detail string, e.g. "Coin slot is busy".
        ''' </summary>
        Public Async Function RequestCoinsAsync() As Task(Of (Ok As Boolean, Detail As String))
            Try
                Dim url = $"{_baseUrl}/api/pc/{_pcNumber}/request-coins"
                Dim response = Await _client.PostAsync(url, Nothing)
                If response.IsSuccessStatusCode Then
                    Return (True, String.Empty)
                End If
                ' Read the server's error detail for a user-friendly message
                Dim body = Await response.Content.ReadAsStringAsync()
                Dim detail As String = "Unknown error"
                Try
                    Dim doc = Text.Json.JsonDocument.Parse(body)
                    Dim d = doc.RootElement.GetProperty("detail").GetString()
                    If Not String.IsNullOrEmpty(d) Then detail = d
                Catch
                End Try
                Return (False, detail)
            Catch
                Return (False, "Server unreachable")
            End Try
        End Function

        ''' <summary>
        ''' Tells the server the user is done inserting coins; the slot is flushed
        ''' and closed immediately. Returns (True, "") on success, or (False, reason).
        ''' </summary>
        Public Async Function DoneInsertingCoinsAsync() As Task(Of (Ok As Boolean, Detail As String))
            Try
                Dim url = $"{_baseUrl}/api/pc/{_pcNumber}/done-coins"
                Dim response = Await _client.PostAsync(url, Nothing)
                If response.IsSuccessStatusCode Then
                    Return (True, String.Empty)
                End If
                ' Read the server's error detail for a user-friendly message
                Dim body = Await response.Content.ReadAsStringAsync()
                Dim detail As String = "Unknown error"
                Try
                    Dim doc = Text.Json.JsonDocument.Parse(body)
                    Dim d = doc.RootElement.GetProperty("detail").GetString()
                    If Not String.IsNullOrEmpty(d) Then detail = d
                Catch
                End Try
                Return (False, detail)
            Catch
                Return (False, "Server unreachable")
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
                If Not response.IsSuccessStatusCode Then
                    NoteFailure($"server answered HTTP {CInt(response.StatusCode)} {response.StatusCode}")
                    ' 404 = the server doesn't know this PC: registration failed at
                    ' startup (e.g. the machine was renumbered), or the server was
                    ' reinstalled / restored from a backup. Register again instead
                    ' of failing forever until someone restarts the client.
                    If response.StatusCode = Net.HttpStatusCode.NotFound AndAlso
                       (DateTime.UtcNow - _lastReRegister).TotalSeconds >= 10 Then
                        _lastReRegister = DateTime.UtcNow
                        Dim ok = Await RegisterAsync()
                        DiagnosticLog.Write($"Heartbeat: PC {_pcNumber} unknown to server — re-register ok={ok}")
                    End If
                    Return Nothing
                End If

                Dim json = Await response.Content.ReadAsStringAsync()
                Dim options = New JsonSerializerOptions() With {
                    .PropertyNameCaseInsensitive = True
                }
                Dim result = JsonSerializer.Deserialize(Of HeartbeatResponse)(json, options)
                _lastFailure = ""
                Return result
            Catch ex As Exception
                NoteFailure($"{ex.GetType().Name}: {ex.Message}")
                Return Nothing   ' Network unreachable — caller handles this
            End Try
        End Function

        ''' <summary>
        ''' Logs WHY a heartbeat failed, once per distinct reason (not every
        ''' second). "Unreachable" alone hid a server-side 500/404 for hours.
        ''' </summary>
        Private Sub NoteFailure(reason As String)
            If reason = _lastFailure Then Return
            _lastFailure = reason
            DiagnosticLog.Write($"Heartbeat: failed — {reason}")
        End Sub

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

        ''' <summary>
        ''' Downloads a wallpaper image from the server and saves it to a local path.
        ''' Returns True on success, False on any failure (fire-and-forget safe).
        ''' </summary>
        Public Async Function DownloadWallpaperAsync(url As String, savePath As String) As Task(Of Boolean)
            Try
                Dim response = Await _client.GetAsync(url)
                If Not response.IsSuccessStatusCode Then Return False
                Dim bytes = Await response.Content.ReadAsByteArrayAsync()
                Dim dir = IO.Path.GetDirectoryName(savePath)
                If Not String.IsNullOrEmpty(dir) Then IO.Directory.CreateDirectory(dir)
                IO.File.WriteAllBytes(savePath, bytes)
                Return True
            Catch
                Return False
            End Try
        End Function

        ' Adapter descriptions that are never the PC's real network card.
        Private Shared ReadOnly VirtualNicHints As String() = {
            "virtual", "hyper-v", "vmware", "virtualbox", "vpn", "tap-", "wan miniport",
            "bluetooth", "loopback", "npcap", "zerotier", "tailscale", "wireguard"}

        ''' <summary>
        ''' MAC of the wired card that reaches the LAN. The server sends the
        ''' Wake-on-LAN "magic packet" to this address, so a VPN / Hyper-V / Wi-Fi
        ''' adapter (which the old "first adapter that is up" rule could pick)
        ''' would make Wake silently do nothing.
        ''' Preference: wired + gateway → wired → any real adapter with a gateway → any.
        ''' </summary>
        Private Shared Function GetMacAddress() As String
            Try
                Dim up = NetworkInterface.GetAllNetworkInterfaces().
                    Where(Function(n) n.OperationalStatus = OperationalStatus.Up AndAlso
                                      n.NetworkInterfaceType <> NetworkInterfaceType.Loopback AndAlso
                                      n.NetworkInterfaceType <> NetworkInterfaceType.Tunnel AndAlso
                                      n.GetPhysicalAddress().GetAddressBytes().Length = 6).
                    ToList()

                Dim isReal = Function(n As NetworkInterface) As Boolean
                                 Dim d = (n.Description & " " & n.Name).ToLowerInvariant()
                                 Return Not VirtualNicHints.Any(Function(h) d.Contains(h))
                             End Function
                Dim isWired = Function(n As NetworkInterface) As Boolean
                                  Select Case n.NetworkInterfaceType
                                      Case NetworkInterfaceType.Ethernet, NetworkInterfaceType.GigabitEthernet,
                                           NetworkInterfaceType.FastEthernetT, NetworkInterfaceType.FastEthernetFx
                                          Return True
                                  End Select
                                  Return False
                              End Function
                Dim hasGateway = Function(n As NetworkInterface) As Boolean
                                     Return n.GetIPProperties().GatewayAddresses.Any(
                                         Function(g) g.Address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork AndAlso
                                                     Not g.Address.Equals(Net.IPAddress.Any))
                                 End Function

                Dim pick = If(up.FirstOrDefault(Function(n) isWired(n) AndAlso isReal(n) AndAlso hasGateway(n)),
                           If(up.FirstOrDefault(Function(n) isWired(n) AndAlso isReal(n)),
                           If(up.FirstOrDefault(Function(n) isReal(n) AndAlso hasGateway(n)),
                              up.FirstOrDefault())))
                If pick IsNot Nothing Then Return pick.GetPhysicalAddress().ToString()
            Catch
            End Try
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
