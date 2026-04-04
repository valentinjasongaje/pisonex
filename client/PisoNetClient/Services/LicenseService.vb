Imports System.Management
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
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
        Private Const BETA_CHECK_INTERVAL_HOURS As Integer = 1

        ''' <summary>Beta mode flag fetched from pisonex.com. Defaults to False (licensing enforced) until first successful fetch.</summary>
        Private _betaMode As Boolean = False

        Public ReadOnly Property BetaMode As Boolean
            Get
                Return _betaMode
            End Get
        End Property

        Private ReadOnly _httpClient As New HttpClient() With {
            .Timeout = TimeSpan.FromSeconds(15)
        }

        Private _verifyTimer As System.Timers.Timer
        Private _betaTimer As System.Timers.Timer

        ' ── Beta mode (fetched from pisonex.com) ────────────────────────

        Public Sub LoadCachedBetaMode()
            Dim cached = AppConfig.LicenseBetaMode
            If Not String.IsNullOrEmpty(cached) Then
                _betaMode = cached.Equals("1", StringComparison.OrdinalIgnoreCase) OrElse
                            cached.Equals("true", StringComparison.OrdinalIgnoreCase)
            End If
        End Sub

        Public Async Function FetchBetaStatusAsync() As Task
            Try
                Dim resp = Await _httpClient.GetAsync($"{PISONEX_API}/api/status")
                If resp.IsSuccessStatusCode Then
                    Dim body = Await resp.Content.ReadAsStringAsync()
                    Dim doc = JsonDocument.Parse(body)
                    Dim betaProp As JsonElement
                    If doc.RootElement.TryGetProperty("beta", betaProp) Then
                        _betaMode = betaProp.GetBoolean()
                    End If
                    AppConfig.SaveLicenseBetaMode(If(_betaMode, "1", "0"))
                    AppConfig.SaveLicenseBetaCheckedAt(DateTime.UtcNow.ToString("o"))
                End If
            Catch
                ' Offline — use cached value
                LoadCachedBetaMode()
            End Try
        End Function

        Public Sub StartBetaCheckTimer()
            If _betaTimer IsNot Nothing Then Return
            _betaTimer = New System.Timers.Timer(BETA_CHECK_INTERVAL_HOURS * 60 * 60 * 1000)
            AddHandler _betaTimer.Elapsed, Async Sub(s, e)
                Await FetchBetaStatusAsync()
            End Sub
            _betaTimer.AutoReset = True
            _betaTimer.Start()
        End Sub

        ' ── Device ID ────────────────────────────────────────────────────

        Public Function GetDeviceId() As String
            Dim cached = AppConfig.LicenseDeviceId
            If Not String.IsNullOrEmpty(cached) Then Return cached

            Dim raw = GetCpuId() & "|" & GetDiskSerial() & "|" & GetMacAddress()
            Dim bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw))
            Dim deviceId = BitConverter.ToString(bytes).Replace("-", "").ToLower()

            AppConfig.SaveLicenseDeviceId(deviceId)
            Return deviceId
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

        Private Function GetMacAddress() As String
            Try
                Dim nic = Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().
                    Where(Function(n) n.OperationalStatus = Net.NetworkInformation.OperationalStatus.Up AndAlso
                                      n.NetworkInterfaceType <> Net.NetworkInformation.NetworkInterfaceType.Loopback).
                    OrderByDescending(Function(n) n.Speed).
                    FirstOrDefault()
                If nic IsNot Nothing Then Return nic.GetPhysicalAddress().ToString()
            Catch
            End Try
            Return ""
        End Function

        ' ── Activation ───────────────────────────────────────────────────

        Public Async Function ActivateAsync(licenseKey As String) As Task(Of ActivateResult)
            Dim deviceId = GetDeviceId()
            Dim deviceLabel = $"PisoNet Client PC{AppConfig.PCNumber:D2} ({Environment.MachineName})"

            Dim payload = New Dictionary(Of String, String) From {
                {"license_key", licenseKey},
                {"device_id", deviceId},
                {"device_label", deviceLabel},
                {"device_type", "pc"}
            }

            Try
                Dim json = JsonSerializer.Serialize(payload)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim resp = Await _httpClient.PostAsync($"{PISONEX_API}/api/license/activate", content)
                Dim body = Await resp.Content.ReadAsStringAsync()

                Debug.WriteLine($"[LICENSE] Activate response: HTTP {CInt(resp.StatusCode)} — {body}")

                If resp.IsSuccessStatusCode Then
                    AppConfig.SaveLicenseKey(licenseKey)
                    AppConfig.SaveLicenseActivatedAt(DateTime.UtcNow.ToString("o"))
                    AppConfig.SaveLicenseLastVerified(DateTime.UtcNow.ToString("o"))

                    Dim doc = JsonDocument.Parse(body)
                    Dim expiresAt = ""
                    Dim expProp As JsonElement
                    If doc.RootElement.TryGetProperty("expires_at", expProp) Then
                        If expProp.ValueKind <> JsonValueKind.Null Then
                            expiresAt = expProp.GetString()
                            AppConfig.SaveLicenseExpiresAt(expiresAt)
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
            Dim key = AppConfig.LicenseKey
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

            ' Clear local license data (keep FirstRunDate)
            AppConfig.SaveLicenseKey("")
            AppConfig.SaveLicenseActivatedAt("")
            AppConfig.SaveLicenseExpiresAt("")
            AppConfig.SaveLicenseLastVerified("")
            Return True
        End Function

        ' ── Verification ─────────────────────────────────────────────────

        Public Async Function VerifyAsync() As Task(Of Boolean)
            Dim key = AppConfig.LicenseKey
            If String.IsNullOrEmpty(key) Then Return False

            Dim deviceId = GetDeviceId()

            Try
                Dim payload = New Dictionary(Of String, String) From {
                    {"license_key", key},
                    {"device_id", deviceId}
                }
                Dim json = JsonSerializer.Serialize(payload)
                Dim content = New StringContent(json, Encoding.UTF8, "application/json")
                Dim resp = Await _httpClient.PostAsync($"{PISONEX_API}/api/license/verify", content)

                If resp.IsSuccessStatusCode Then
                    Dim body = Await resp.Content.ReadAsStringAsync()
                    Dim doc = JsonDocument.Parse(body)
                    Dim valid = doc.RootElement.GetProperty("valid").GetBoolean()
                    If valid Then
                        AppConfig.SaveLicenseLastVerified(DateTime.UtcNow.ToString("o"))

                        Dim expProp As JsonElement
                        If doc.RootElement.TryGetProperty("expires_at", expProp) AndAlso
                           expProp.ValueKind <> JsonValueKind.Null Then
                            AppConfig.SaveLicenseExpiresAt(expProp.GetString())
                        End If
                        Return True
                    End If
                End If
            Catch
            End Try

            Return False
        End Function

        ' ── Status ───────────────────────────────────────────────────────

        Public Function IsActivated() As Boolean
            Return Not String.IsNullOrEmpty(AppConfig.LicenseKey) AndAlso
                   Not String.IsNullOrEmpty(AppConfig.LicenseActivatedAt)
        End Function

        Public Function TrialDaysRemaining() As Integer
            Dim firstRun = AppConfig.LicenseFirstRunDate
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
            Dim expiresAt = AppConfig.LicenseExpiresAt
            If String.IsNullOrEmpty(expiresAt) Then Return False  ' lifetime

            Dim expDt As DateTime
            If DateTime.TryParse(expiresAt, expDt) Then
                Return DateTime.UtcNow > expDt
            End If
            Return False
        End Function

        Public Function IsOfflineLocked() As Boolean
            If Not IsActivated() Then Return False

            Dim lastVerified = AppConfig.LicenseLastVerified
            If String.IsNullOrEmpty(lastVerified) Then Return False

            Dim lastDt As DateTime
            If DateTime.TryParse(lastVerified, lastDt) Then
                Return (DateTime.UtcNow - lastDt).TotalHours > OFFLINE_GRACE_HOURS
            End If
            Return False
        End Function

        Public Function IsActive() As Boolean
            If _betaMode Then Return True
            If IsActivated() Then
                If IsLicenseExpired() Then Return False
                If IsOfflineLocked() Then Return False
                Return True
            End If
            Return Not IsTrialExpired()
        End Function

        Public Function GetStatus() As LicenseStatus
            If _betaMode Then Return LicenseStatus.Activated
            If IsActivated() Then
                If IsLicenseExpired() Then Return LicenseStatus.Expired
                If IsOfflineLocked() Then Return LicenseStatus.OfflineLocked
                Return LicenseStatus.Activated
            End If
            If IsTrialExpired() Then Return LicenseStatus.Expired
            Return LicenseStatus.Trial
        End Function

        Public Function GetMaskedKey() As String
            Dim key = AppConfig.LicenseKey
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
                If IsActivated() Then
                    Dim valid = Await VerifyAsync()
                    If Not valid Then
                        ' Server rejected — device was revoked or license invalidated
                        Debug.WriteLine("[LICENSE] Verification failed — clearing local license")
                        AppConfig.SaveLicenseKey("")
                        AppConfig.SaveLicenseActivatedAt("")
                        AppConfig.SaveLicenseExpiresAt("")
                        AppConfig.SaveLicenseLastVerified("")
                    End If
                End If
            End Sub
            _verifyTimer.AutoReset = True
            _verifyTimer.Start()
        End Sub

        Public Sub EnsureFirstRunDate()
            If String.IsNullOrEmpty(AppConfig.LicenseFirstRunDate) Then
                AppConfig.SaveLicenseFirstRunDate(DateTime.UtcNow.ToString("o"))
            End If
        End Sub

    End Module

End Namespace
