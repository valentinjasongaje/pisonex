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

        ''' <summary>
        ''' True after at least one successful POST to /api/status has anchored the
        ''' trial clock with pisonex.com. Trial mode requires this to start — refusing
        ''' to run when neither LicenseFirstRunDate nor a server anchor exists kills
        ''' the offline-install + delete-license.dat trial-reset attack.
        ''' </summary>
        Private _lastSyncOk As Boolean = False
        Public ReadOnly Property LastSyncOk As Boolean
            Get
                Return _lastSyncOk
            End Get
        End Property

        Public Async Function SyncStartupStatusAsync() As Task
            ' Unlimited-clients model: the PC client no longer activates against, nor
            ' reports telemetry to, pisonex.com. Licensing is enforced solely at the
            ' Orange Pi server and delivered to the client on every heartbeat via the
            ' server_licensed flag (see Program.OnMembershipUpdated). Kept as an
            ' awaitable no-op so existing call sites continue to compile.
            _lastSyncOk = True
            Await Task.CompletedTask
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
                {"device_type", "pc"},
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
            For Each kv In HardwareFingerprint.Collect()
                payload(kv.Key) = kv.Value
            Next

            Try
                Dim json = JsonSerializer.Serialize(payload)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim resp = Await _httpClient.PostAsync($"{PISONEX_API}/api/license/activate", content)
                Dim body = Await resp.Content.ReadAsStringAsync()

                Debug.WriteLine($"[LICENSE] Activate response: HTTP {CInt(resp.StatusCode)} — {body}")

                If resp.IsSuccessStatusCode Then
                    Dim doc = JsonDocument.Parse(body)

                    ' Activation is only real if the server returns a token that
                    ' actually verifies on THIS device. Without a verifiable token
                    ' IsActivated() can never return True, so reporting success would
                    ' silently drop the user back to trial mode. Validate first,
                    ' persist second — never leave a half-activated state.
                    Dim token As String = ""
                    Dim tokenProp As JsonElement
                    If doc.RootElement.TryGetProperty("token", tokenProp) AndAlso
                       tokenProp.ValueKind = JsonValueKind.String Then
                        token = tokenProp.GetString()
                    End If

                    If String.IsNullOrEmpty(token) Then
                        Return New ActivateResult With {
                            .Success = False,
                            .ErrorMessage = "Activation incomplete: the licensing server did not issue a token. Contact support (server signing key may be missing)."
                        }
                    End If

                    Dim activateClaims = LicenseTokenVerifier.VerifySignature(token, GetDeviceId())
                    If activateClaims Is Nothing Then
                        Return New ActivateResult With {
                            .Success = False,
                            .ErrorMessage = "Activation failed: the license token could not be verified on this device. Contact support (signing key mismatch)."
                        }
                    End If

                    ' Token is valid — persist activation state.
                    LicenseStore.LicenseKey = licenseKey
                    LicenseStore.LicenseActivatedAt = DateTime.UtcNow.ToString("o")
                    LicenseStore.LicenseLastVerified = DateTime.UtcNow.ToString("o")
                    LicenseStore.LicenseToken = token
                    LicenseStore.OfflineSince = ""
                    LicenseStore.ServerRejected = False

                    Dim expiresAt = ""
                    Dim expProp As JsonElement
                    If doc.RootElement.TryGetProperty("expires_at", expProp) Then
                        If expProp.ValueKind <> JsonValueKind.Null Then
                            expiresAt = expProp.GetString()
                            LicenseStore.LicenseExpiresAt = expiresAt
                        End If
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
                For Each kv In HardwareFingerprint.Collect()
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

        ''' <summary>
        ''' Soft check: the user-visible activation strings exist. Returns True
        ''' for both real activations and forged ones — used ONLY to decide
        ''' whether the verify timer should attempt to fetch/refresh a token.
        ''' Never use this to gate access to features.
        ''' </summary>
        Public Function HasActivationRecord() As Boolean
            Return Not String.IsNullOrEmpty(LicenseStore.LicenseKey) AndAlso
                   Not String.IsNullOrEmpty(LicenseStore.LicenseActivatedAt)
        End Function

        ''' <summary>
        ''' True only when this install holds a pisonex.com-signed token bound
        ''' to the current device. The token's signature is the only field in
        ''' license.dat an attacker cannot forge — every other field (LicenseKey,
        ''' LicenseActivatedAt, LicenseLastVerified) can be written by anyone
        ''' with admin on the box, so they cannot be used as proof of activation
        ''' on their own.
        '''
        ''' Accepts EXPIRED tokens (signature-only verify) so the 72h offline
        ''' grace period continues to work — IsOfflineLocked() handles the
        ''' freshness check separately.
        ''' </summary>
        Public Function IsActivated() As Boolean
            ' Per-PC activation removed (unlimited-clients model). The client is
            ' always considered licensed locally; shop licensing lives on the server.
            Return True
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

            ' Fallback when offline_since isn't set yet. Use the token's `iat`
            ' (issued-at) claim as the freshness anchor — it's part of the
            ' signed payload, so an attacker cannot rewind it without
            ' invalidating the signature. This replaces the old check against
            ' the forgeable LicenseLastVerified string.
            Dim sigClaims = LicenseTokenVerifier.VerifySignature(
                LicenseStore.LicenseToken, GetDeviceId())
            If sigClaims IsNot Nothing AndAlso sigClaims.IssuedAt > 0 Then
                Dim issuedAt = DateTimeOffset.FromUnixTimeSeconds(sigClaims.IssuedAt).UtcDateTime
                Return (DateTime.UtcNow - issuedAt).TotalHours > OFFLINE_GRACE_HOURS
            End If

            ' Defensive: no signed token reached us (IsActivated should have
            ' caught that, but in case the call path changes), treat as locked.
            ' The old default of "Return False" here let a hand-edited
            ' license.dat with empty last_verified run forever offline.
            Return True
        End Function

        Public Function IsActive() As Boolean
            ' Unlimited-clients model: the client is always permitted to run. Whether
            ' the SHOP is licensed is decided by the Orange Pi server and surfaced via
            ' the heartbeat's server_licensed flag — never by per-PC activation.
            Return True
        End Function

        Public Function GetStatus() As LicenseStatus
            ' Per-PC activation removed — the client is always considered licensed.
            Return LicenseStatus.Activated
        End Function

        ''' <summary>
        ''' Human-readable token state for the Admin Panel diagnostic. Reports
        ''' whether a signed token is present, whether its signature verifies,
        ''' whether it is bound to this device, and its freshness — so support
        ''' can see at a glance WHY a machine shows trial vs activated.
        ''' </summary>
        Public Function GetTokenDiagnostic() As String
            Dim token = LicenseStore.LicenseToken
            If String.IsNullOrEmpty(token) Then
                Return "No token stored — activation never returned one (check server signing key)."
            End If

            ' Signature-only check, device binding ignored, to isolate the cause.
            Dim sigClaims = LicenseTokenVerifier.VerifySignature(token, Nothing)
            If sigClaims Is Nothing Then
                Return "Token present but signature INVALID — key mismatch or tampered file."
            End If

            ' Now enforce device binding.
            Dim boundClaims = LicenseTokenVerifier.VerifySignature(token, GetDeviceId())
            If boundClaims Is Nothing Then
                Return "Signature OK but token is bound to a DIFFERENT device."
            End If

            Dim expDt = DateTimeOffset.FromUnixTimeSeconds(boundClaims.ExpiresAt).UtcDateTime
            Dim fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= boundClaims.ExpiresAt
            Dim freshLabel = If(fresh, "fresh", "expired (offline grace applies)")
            Return $"Valid — signature OK, device match, token {freshLabel} (exp {expDt:yyyy-MM-dd HH:mm} UTC)."
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
            ' No-op: per-PC license verification against pisonex.com has been removed.
            ' Shop licensing is enforced at the Orange Pi server (heartbeat
            ' server_licensed flag), so the client has nothing to verify remotely.
        End Sub

        Public Sub EnsureFirstRunDate()
            If String.IsNullOrEmpty(LicenseStore.LicenseFirstRunDate) Then
                LicenseStore.LicenseFirstRunDate = DateTime.UtcNow.ToString("o")
            End If
        End Sub

        ''' <summary>
        ''' Returns True only when this install is allowed to run in trial mode.
        ''' Trial requires at least one successful pisonex.com anchor at some
        ''' point — either now (LastSyncOk) or persisted from a previous run
        ''' (LicenseStore.TrialAnchored).  Activated installs bypass this gate
        ''' entirely; the existing IsActive() / IsOfflineLocked() rules govern
        ''' them.
        '''
        ''' Closes the "install offline, get fresh 14 days, repeat" loop: if
        ''' the very first run has no internet AND no prior anchor, the trial
        ''' cannot start.
        ''' </summary>
        Public Function IsTrialAnchored() As Boolean
            ' Unlimited-clients model: no trial, no anchor requirement. Always allowed.
            Return True
        End Function

    End Module

End Namespace
