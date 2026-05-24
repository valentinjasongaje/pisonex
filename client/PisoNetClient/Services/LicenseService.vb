Imports System.Management
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports Microsoft.Win32
Imports PisoNetClient.Config

Namespace Services

    Public Enum LicenseStatus
        Activated
        Trial
        Expired
        OfflineLocked
    End Enum

    Public Class ActivateResult
        Public Property Success As Boolean
        Public Property ErrorMessage As String
        Public Property ExpiresAt As String
    End Class

    Public Module LicenseService

        Private Const PISONEX_API As String = "https://www.pisonex.com"
        Private Const TRIAL_DAYS As Integer = 14
        Private Const OFFLINE_GRACE_HOURS As Integer = 72  ' 3 days

        ' ── Branch / earnings cache (updated by SessionManager on every heartbeat) ──
        ' These are forwarded to pisonex.com on the hourly /api/status ping so the
        ' customer portal can display today's running totals grouped by branch.
        Private _branchName As String = ""
        Private _todayPesos As Integer = 0
        Private _todaySessions As Integer = 0
        Private _todayMinutes As Integer = 0

        ''' <summary>
        ''' Called by SessionManager after every successful heartbeat so the latest
        ''' branch name and today's earnings are available for the next pisonex.com ping.
        ''' </summary>
        Public Sub UpdateBranchEarnings(branchName As String, todayPesos As Integer,
                                         todaySessions As Integer, todayMinutes As Integer)
            _branchName = If(branchName, "")
            _todayPesos = todayPesos
            _todaySessions = todaySessions
            _todayMinutes = todayMinutes
        End Sub

        Private ReadOnly _httpClient As New HttpClient() With {
            .Timeout = TimeSpan.FromSeconds(15)
        }

        Private _verifyTimer As System.Timers.Timer

        ' ── Startup sync (telemetry + trial-clock restore) ───────────────

        Public Async Function SyncStartupStatusAsync() As Task
            Try
                ' POST telemetry to pisonex.com on startup.
                ' The server records first_seen_at on the first ping so deleting
                ' license.dat cannot reset the trial as long as the device_id stays the same.
                Dim payload = New Dictionary(Of String, String) From {
                    {"device_id", GetDeviceId()}
                }
                For Each kv In BuildTelemetry()
                    payload(kv.Key) = kv.Value
                Next

                Dim json = JsonSerializer.Serialize(payload)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim resp = Await _httpClient.PostAsync($"{PISONEX_API}/api/status", content)
                If resp.IsSuccessStatusCode Then
                    Dim body = Await resp.Content.ReadAsStringAsync()
                    Dim doc = JsonDocument.Parse(body)

                    ' Always apply the earlier of local vs server first-run date.
                    ' This prevents trial-clock reset via license.dat deletion.
                    Dim firstSeenProp As JsonElement
                    If doc.RootElement.TryGetProperty("first_seen_at", firstSeenProp) AndAlso
                       firstSeenProp.ValueKind = JsonValueKind.String Then
                        Dim serverDate = firstSeenProp.GetString()
                        If Not String.IsNullOrEmpty(serverDate) Then
                            Dim localDate = LicenseStore.LicenseFirstRunDate
                            Dim shouldUpdate = String.IsNullOrEmpty(localDate)
                            If Not shouldUpdate Then
                                Try
                                    Dim localDt = DateTime.Parse(localDate, Nothing, Globalization.DateTimeStyles.RoundtripKind)
                                    Dim serverDt = DateTime.Parse(serverDate, Nothing, Globalization.DateTimeStyles.RoundtripKind)
                                    If serverDt < localDt Then shouldUpdate = True
                                Catch
                                    shouldUpdate = True
                                End Try
                            End If
                            If shouldUpdate Then
                                LicenseStore.LicenseFirstRunDate = serverDate
                            End If
                        End If
                    End If
                End If
            Catch
                ' Cannot reach pisonex.com — trial date not restored this time
            End Try
        End Function

        ' ── Telemetry helpers ────────────────────────────────────────────

        ''' <summary>
        ''' Returns a dictionary of telemetry fields that pisonex.com records on
        ''' every license API call — version, status, trial days, PC info,
        ''' and today's branch earnings (for the customer portal).
        ''' These fields are ignored by older API versions so they are always safe to send.
        ''' </summary>
        Private Function BuildTelemetry() As Dictionary(Of String, String)
            Dim d = New Dictionary(Of String, String) From {
                {"app_version", GetAppVersion()},
                {"license_status", GetStatus().ToString().ToLower()},
                {"trial_days_remaining", TrialDaysRemaining().ToString()},
                {"pc_number", AppConfig.PCNumber.ToString()},
                {"machine_name", Environment.MachineName}
            }
            ' Branch + today's earnings — forwarded from the last local-server heartbeat
            If Not String.IsNullOrEmpty(_branchName) Then
                d("branch_name") = _branchName
            End If
            d("today_pesos") = _todayPesos.ToString()
            d("today_sessions") = _todaySessions.ToString()
            d("today_minutes") = _todayMinutes.ToString()
            Return d
        End Function

        Private Function GetAppVersion() As String
            Try
                Dim v = Reflection.Assembly.GetExecutingAssembly().GetName().Version
                Return $"{v.Major}.{v.Minor}.{v.Build}"
            Catch
                Return "unknown"
            End Try
        End Function

        ' ── Device ID ────────────────────────────────────────────────────

        Public Function GetDeviceId() As String
            ' Always derive from hardware — never trust the cached value.
            ' If the cached value differs (e.g. file tampered or hardware changed),
            ' self-correct so the stored ID stays consistent.
            Dim raw = GetHardwareId()
            Dim bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw))
            Dim deviceId = BitConverter.ToString(bytes).Replace("-", "").ToLower()

            If LicenseStore.LicenseDeviceId <> deviceId Then
                LicenseStore.LicenseDeviceId = deviceId
            End If
            Return deviceId
        End Function

        ''' <summary>
        ''' Returns the most stable hardware identifier available.
        ''' MachineGuid is set at Windows install and works on diskless (PXE) machines.
        ''' CPU+Disk is a fallback only — disk serial is empty on diskless PCs.
        ''' </summary>
        Private Function GetHardwareId() As String
            Try
                Using rk = Registry.LocalMachine.OpenSubKey("SOFTWARE\Microsoft\Cryptography")
                    Dim guid = rk?.GetValue("MachineGuid")?.ToString()
                    If Not String.IsNullOrEmpty(guid) Then Return guid
                End Using
            Catch
            End Try
            Return GetCpuId() & "|" & GetDiskSerial()
        End Function

        Private Function GetCpuId() As String
            Try
                Using searcher = New ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor")
                    For Each obj In searcher.Get()
                        Return If(obj("ProcessorId")?.ToString(), "")
                    Next
                End Using
            Catch
            End Try
            Return Environment.MachineName
        End Function

        Private Function GetDiskSerial() As String
            Try
                Using searcher = New ManagementObjectSearcher("SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0")
                    For Each obj In searcher.Get()
                        Dim val = obj("SerialNumber")?.ToString()?.Trim()
                        Return If(val, "")
                    Next
                End Using
            Catch
            End Try
            Return ""
        End Function


        ' ── Activation ───────────────────────────────────────────────────

        Public Async Function ActivateAsync(licenseKey As String) As Task(Of ActivateResult)
            Dim deviceId = GetDeviceId()
            Dim deviceLabel = $"Pisonex Client PC{AppConfig.PCNumber:D2} ({Environment.MachineName})"

            Dim payload = New Dictionary(Of String, String) From {
                {"license_key", licenseKey},
                {"device_id", deviceId},
                {"device_label", deviceLabel},
                {"device_type", "pc"}
            }
            For Each kv In BuildTelemetry()
                payload(kv.Key) = kv.Value
            Next

            Try
                Dim json = JsonSerializer.Serialize(payload)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim resp = Await _httpClient.PostAsync($"{PISONEX_API}/api/license/activate", content)
                Dim body = Await resp.Content.ReadAsStringAsync()

                Debug.WriteLine($"[LICENSE] Activate response: HTTP {CInt(resp.StatusCode)} — {body}")

                If resp.IsSuccessStatusCode Then
                    LicenseStore.LicenseKey = licenseKey
                    LicenseStore.LicenseActivatedAt = DateTime.UtcNow.ToString("o")
                    LicenseStore.LicenseLastVerified = DateTime.UtcNow.ToString("o")

                    Dim doc = JsonDocument.Parse(body)
                    Dim expiresAt = ""
                    Dim expProp As JsonElement
                    If doc.RootElement.TryGetProperty("expires_at", expProp) Then
                        If expProp.ValueKind <> JsonValueKind.Null Then
                            expiresAt = expProp.GetString()
                            LicenseStore.LicenseExpiresAt = expiresAt
                        End If
                    End If

                    Dim tokenProp As JsonElement
                    If doc.RootElement.TryGetProperty("token", tokenProp) AndAlso
                       tokenProp.ValueKind = JsonValueKind.String Then
                        LicenseStore.LicenseToken = tokenProp.GetString()
                    End If

                    Return New ActivateResult With {
                        .Success = True,
                        .ExpiresAt = expiresAt
                    }
                Else
                    Dim errMsg = "Activation failed"
                    Try
                        Dim doc = JsonDocument.Parse(body)
                        Dim errProp As JsonElement
                        If doc.RootElement.TryGetProperty("error", errProp) AndAlso
                           errProp.ValueKind = JsonValueKind.String Then
                            errMsg = errProp.GetString()
                        ElseIf doc.RootElement.TryGetProperty("detail", errProp) Then
                            If errProp.ValueKind = JsonValueKind.String Then
                                errMsg = errProp.GetString()
                            Else
                                errMsg = errProp.ToString()
                            End If
                        ElseIf doc.RootElement.TryGetProperty("message", errProp) AndAlso
                               errProp.ValueKind = JsonValueKind.String Then
                            errMsg = errProp.GetString()
                        End If
                    Catch
                    End Try

                    Return New ActivateResult With {
                        .Success = False,
                        .ErrorMessage = errMsg
                    }
                End If
            Catch ex As Exception
                Return New ActivateResult With {
                    .Success = False,
                    .ErrorMessage = $"Network error: {ex.Message}"
                }
            End Try
        End Function

        Public Async Function DeactivateAsync() As Task(Of Boolean)
            Dim key = LicenseStore.LicenseKey
            Dim deviceId = GetDeviceId()

            If Not String.IsNullOrEmpty(key) Then
                Try
                    Dim payload = New Dictionary(Of String, String) From {
                        {"license_key", key},
                        {"device_id", deviceId}
                    }
                    Dim json = JsonSerializer.Serialize(payload)
                    Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                    Await _httpClient.PostAsync($"{PISONEX_API}/api/license/deactivate-device", content)
                Catch
                End Try
            End If

            ' Clear activation fields only — FirstRunDate stays so trial clock is not reset
            LicenseStore.ClearActivation()
            Return True
        End Function

        ' ── Verification ─────────────────────────────────────────────────

        Public Async Function VerifyAsync() As Task(Of Boolean)
            Dim key = LicenseStore.LicenseKey
            If String.IsNullOrEmpty(key) Then Return False

            Dim deviceId = GetDeviceId()

            Try
                Dim payload = New Dictionary(Of String, String) From {
                    {"license_key", key},
                    {"device_id", deviceId}
                }
                For Each kv In BuildTelemetry()
                    payload(kv.Key) = kv.Value
                Next
                Dim json = JsonSerializer.Serialize(payload)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim resp = Await _httpClient.PostAsync($"{PISONEX_API}/api/license/verify", content)

                If resp.IsSuccessStatusCode Then
                    Dim body = Await resp.Content.ReadAsStringAsync()
                    Dim doc = JsonDocument.Parse(body)
                    Dim valid As Boolean = False
                    Dim validProp As JsonElement
                    If doc.RootElement.TryGetProperty("valid", validProp) Then
                        valid = validProp.GetBoolean()
                    End If

                    If valid Then
                        ' Success — clear offline tracking, store fresh token
                        LicenseStore.OfflineSince = ""
                        LicenseStore.ServerRejected = False
                        LicenseStore.LicenseLastVerified = DateTime.UtcNow.ToString("o")

                        Dim expProp As JsonElement
                        If doc.RootElement.TryGetProperty("expires_at", expProp) AndAlso
                           expProp.ValueKind <> JsonValueKind.Null Then
                            LicenseStore.LicenseExpiresAt = expProp.GetString()
                        End If

                        Dim tokenProp As JsonElement
                        If doc.RootElement.TryGetProperty("token", tokenProp) AndAlso
                           tokenProp.ValueKind = JsonValueKind.String Then
                            LicenseStore.LicenseToken = tokenProp.GetString()
                        End If

                        Return True
                    End If
                End If

                ' Server responded but rejected the device (revoked/inactive/expired).
                ' Clear token immediately — server decision overrides any grace period.
                LicenseStore.LicenseToken = ""
                LicenseStore.OfflineSince = ""
                LicenseStore.ServerRejected = True
                Return False

            Catch ex As HttpRequestException
                ' Network failure — start offline grace tracking
                If String.IsNullOrEmpty(LicenseStore.OfflineSince) Then
                    LicenseStore.OfflineSince = DateTime.UtcNow.ToString("o")
                End If
                Return False
            Catch ex As TaskCanceledException
                ' Timeout — treat as network failure
                If String.IsNullOrEmpty(LicenseStore.OfflineSince) Then
                    LicenseStore.OfflineSince = DateTime.UtcNow.ToString("o")
                End If
                Return False
            Catch
                Return False
            End Try
        End Function

        ' ── Status ───────────────────────────────────────────────────────

        Public Function IsActivated() As Boolean
            Return Not String.IsNullOrEmpty(LicenseStore.LicenseKey) AndAlso
                   Not String.IsNullOrEmpty(LicenseStore.LicenseActivatedAt)
        End Function

        Public Function TrialDaysRemaining() As Integer
            Dim firstRun = LicenseStore.LicenseFirstRunDate
            If String.IsNullOrEmpty(firstRun) Then Return TRIAL_DAYS

            Dim firstDt As DateTime
            If DateTime.TryParse(firstRun, firstDt) Then
                Dim elapsed = (DateTime.UtcNow - firstDt).Days
                Return Math.Max(0, TRIAL_DAYS - elapsed)
            End If
            Return TRIAL_DAYS
        End Function

        Public Function IsTrialExpired() As Boolean
            Return TrialDaysRemaining() <= 0
        End Function

        Public Function IsLicenseExpired() As Boolean
            Dim claims = LicenseTokenVerifier.Verify(LicenseStore.LicenseToken, GetDeviceId())
            If claims IsNot Nothing Then
                If Not claims.LicenseExpiresAt.HasValue Then Return False
                Return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > claims.LicenseExpiresAt.Value
            End If
            Dim expiresAt = LicenseStore.LicenseExpiresAt
            If String.IsNullOrEmpty(expiresAt) Then Return False
            Dim expDt As DateTime
            If DateTime.TryParse(expiresAt, expDt) Then Return DateTime.UtcNow > expDt
            Return False
        End Function

        Public Function IsOfflineLocked() As Boolean
            If Not IsActivated() Then Return False

            ' Server explicitly rejected — no grace period
            If LicenseStore.ServerRejected Then Return True

            ' Valid token = verified within 8h — not offline locked
            Dim claims = LicenseTokenVerifier.Verify(LicenseStore.LicenseToken, GetDeviceId())
            If claims IsNot Nothing Then Return False

            ' Token expired — check how long we've been offline
            Dim offlineSince = LicenseStore.OfflineSince
            If Not String.IsNullOrEmpty(offlineSince) Then
                Dim offlineDt As DateTime
                If DateTime.TryParse(offlineSince, offlineDt) Then
                    Return (DateTime.UtcNow - offlineDt).TotalHours > OFFLINE_GRACE_HOURS
                End If
            End If

            ' Fallback for installs without offline_since tracking yet
            Dim lastVerified = LicenseStore.LicenseLastVerified
            If String.IsNullOrEmpty(lastVerified) Then Return False
            Dim lastDt As DateTime
            If DateTime.TryParse(lastVerified, lastDt) Then
                Return (DateTime.UtcNow - lastDt).TotalHours > OFFLINE_GRACE_HOURS
            End If
            Return False
        End Function

        Public Function IsActive() As Boolean
            If IsActivated() Then
                ' Server explicitly rejected — no grace period
                If LicenseStore.ServerRejected Then Return False

                Dim claims = LicenseTokenVerifier.Verify(LicenseStore.LicenseToken, GetDeviceId())
                If claims IsNot Nothing Then
                    If DateTimeOffset.UtcNow.ToUnixTimeSeconds() > claims.ExpiresAt Then
                        Return Not IsOfflineLocked()
                    End If
                    If claims.LicenseExpiresAt.HasValue AndAlso
                       DateTimeOffset.UtcNow.ToUnixTimeSeconds() > claims.LicenseExpiresAt.Value Then Return False
                    Return True
                End If
                If IsLicenseExpired() Then Return False
                If IsOfflineLocked() Then Return False
                Return True
            End If
            Return Not IsTrialExpired()
        End Function

        Public Function GetStatus() As LicenseStatus
            If IsActivated() Then
                If IsLicenseExpired() Then Return LicenseStatus.Expired
                If IsOfflineLocked() Then Return LicenseStatus.OfflineLocked
                Return LicenseStatus.Activated
            End If
            If IsTrialExpired() Then Return LicenseStatus.Expired
            Return LicenseStatus.Trial
        End Function

        Public Function GetMaskedKey() As String
            Dim key = LicenseStore.LicenseKey
            If String.IsNullOrEmpty(key) Then Return ""
            Dim parts = key.Split("-"c)
            If parts.Length >= 5 Then
                Return $"{parts(0)}-****-****-****-{parts(4)}"
            End If
            Return key.Substring(0, Math.Min(4, key.Length)) & "****"
        End Function

        ' ── Background verification timer ────────────────────────────────

        Public Sub StartVerificationTimer()
            If _verifyTimer IsNot Nothing Then Return
            _verifyTimer = New System.Timers.Timer(6 * 60 * 60 * 1000)  ' 6 hours
            AddHandler _verifyTimer.Elapsed, Async Sub(s, e)
                ' Re-anchor the trial clock every 6h so an attacker can't reset it
                ' by deleting license.dat and staying offline until the next reboot.
                Await SyncStartupStatusAsync()
                If IsActivated() Then
                    Dim valid = Await VerifyAsync()
                    If Not valid Then
                        Debug.WriteLine("[LICENSE] Verification failed — clearing local license")
                        LicenseStore.ClearActivation()
                    End If
                End If
            End Sub
            _verifyTimer.AutoReset = True
            _verifyTimer.Start()
        End Sub

        Public Sub EnsureFirstRunDate()
            If String.IsNullOrEmpty(LicenseStore.LicenseFirstRunDate) Then
                LicenseStore.LicenseFirstRunDate = DateTime.UtcNow.ToString("o")
            End If
        End Sub

    End Module

End Namespace
