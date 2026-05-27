Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Namespace Config

    ''' <summary>
    ''' Persists license data as a DPAPI-encrypted binary file in %ProgramData%\PisoNet\.
    ''' The file is encrypted to the local machine key — unreadable and unmodifiable
    ''' by users, and non-portable (decryption fails on any other machine).
    ''' </summary>
    Public Module LicenseStore

        Private ReadOnly _filePath As String = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PisoNet", "license.dat")

        ' ── In-memory cache (loaded once on first access) ─────────────────

        Private _cache As Dictionary(Of String, String) = Nothing
        Private ReadOnly _lock As New Object()

        Private Function Load() As Dictionary(Of String, String)
            SyncLock _lock
                If _cache IsNot Nothing Then Return _cache
                _cache = ReadFromDisk()
                Return _cache
            End SyncLock
        End Function

        Private Sub Flush()
            SyncLock _lock
                WriteToDisk(_cache)
            End SyncLock
        End Sub

        ' ── Disk I/O ─────────────────────────────────────────────────────

        Private Function ReadFromDisk() As Dictionary(Of String, String)
            Try
                If Not File.Exists(_filePath) Then
                    Return New Dictionary(Of String, String)()
                End If

                Dim cipher = File.ReadAllBytes(_filePath)
                Dim plain = ProtectedData.Unprotect(cipher, Nothing, DataProtectionScope.LocalMachine)
                Dim json = Encoding.UTF8.GetString(plain)
                Dim doc = JsonDocument.Parse(json)

                Dim result As New Dictionary(Of String, String)()
                For Each prop In doc.RootElement.EnumerateObject()
                    result(prop.Name) = prop.Value.GetString()
                Next
                Return result
            Catch
                ' File is missing, corrupted, or was tampered with — start fresh
                Return New Dictionary(Of String, String)()
            End Try
        End Function

        Private Sub WriteToDisk(data As Dictionary(Of String, String))
            Try
                Dim dir = Path.GetDirectoryName(_filePath)
                If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)

                Dim json = JsonSerializer.Serialize(data)
                Dim plain = Encoding.UTF8.GetBytes(json)
                Dim cipher = ProtectedData.Protect(plain, Nothing, DataProtectionScope.LocalMachine)
                File.WriteAllBytes(_filePath, cipher)
            Catch ex As Exception
                Debug.WriteLine($"[LICENSESTORE] Write failed: {ex.Message}")
            End Try
        End Sub

        ' ── Generic get/set ──────────────────────────────────────────────

        Private Function GetValue(key As String) As String
            Dim d = Load()
            Dim v As String = Nothing
            Return If(d.TryGetValue(key, v), v, "")
        End Function

        Private Sub SetValue(key As String, value As String)
            SyncLock _lock
                If _cache Is Nothing Then _cache = ReadFromDisk()
                _cache(key) = If(value, "")
                WriteToDisk(_cache)
            End SyncLock
        End Sub

        Private Sub RemoveValue(key As String)
            SyncLock _lock
                If _cache Is Nothing Then _cache = ReadFromDisk()
                _cache.Remove(key)
                WriteToDisk(_cache)
            End SyncLock
        End Sub

        ' ── License fields ───────────────────────────────────────────────

        Public Property LicenseKey As String
            Get
                Return GetValue("LicenseKey")
            End Get
            Set(v As String)
                SetValue("LicenseKey", v)
            End Set
        End Property

        Public Property LicenseDeviceId As String
            Get
                Return GetValue("LicenseDeviceId")
            End Get
            Set(v As String)
                SetValue("LicenseDeviceId", v)
            End Set
        End Property

        Public Property LicenseActivatedAt As String
            Get
                Return GetValue("LicenseActivatedAt")
            End Get
            Set(v As String)
                SetValue("LicenseActivatedAt", v)
            End Set
        End Property

        Public Property LicenseExpiresAt As String
            Get
                Return GetValue("LicenseExpiresAt")
            End Get
            Set(v As String)
                SetValue("LicenseExpiresAt", v)
            End Set
        End Property

        Public Property LicenseLastVerified As String
            Get
                Return GetValue("LicenseLastVerified")
            End Get
            Set(v As String)
                SetValue("LicenseLastVerified", v)
            End Set
        End Property

        Public Property LicenseFirstRunDate As String
            Get
                Return GetValue("LicenseFirstRunDate")
            End Get
            Set(v As String)
                SetValue("LicenseFirstRunDate", v)
            End Set
        End Property

        ''' <summary>
        ''' "true" once pisonex.com has anchored the trial clock at least once for
        ''' this install. Required to run in trial mode — refusing to start when
        ''' missing kills the offline-install trial-reset attack (install offline,
        ''' use for 14 days, repeat with a fresh first_seen_at on first online ping).
        ''' Persisted in the DPAPI-encrypted license.dat so deleting the file forces
        ''' a re-anchor.
        ''' </summary>
        Public Property TrialAnchored As Boolean
            Get
                Return GetValue("TrialAnchored") = "true"
            End Get
            Set(v As Boolean)
                SetValue("TrialAnchored", If(v, "true", ""))
            End Set
        End Property

        ''' <summary>
        ''' ES256-signed JWT from pisonex.com. Stored alongside license data.
        ''' Verified on every load — file tampering breaks the signature.
        ''' </summary>
        Public Property LicenseToken As String
            Get
                Return GetValue("LicenseToken")
            End Get
            Set(v As String)
                SetValue("LicenseToken", v)
            End Set
        End Property

        ''' <summary>ISO timestamp of first consecutive network failure. Cleared on success.</summary>
        Public Property OfflineSince As String
            Get
                Return GetValue("OfflineSince")
            End Get
            Set(v As String)
                SetValue("OfflineSince", v)
            End Set
        End Property

        ''' <summary>
        ''' Set to "true" when pisonex.com explicitly rejects the device (revoked/inactive).
        ''' No offline grace applies — is_active returns False immediately.
        ''' Cleared on next successful verification.
        ''' </summary>
        Public Property ServerRejected As Boolean
            Get
                Return GetValue("ServerRejected") = "true"
            End Get
            Set(v As Boolean)
                SetValue("ServerRejected", If(v, "true", ""))
            End Set
        End Property

        ' ── Admin PIN (hashed) ────────────────────────────────────────────

        Private Const DEFAULT_PIN As String = "1234"

        ''' <summary>
        ''' Saves the admin PIN as a SHA-256 hash. The plain text is never persisted.
        ''' </summary>
        Public Sub SaveAdminPinHash(plainPin As String)
            Dim hash = HashPin(If(String.IsNullOrWhiteSpace(plainPin), DEFAULT_PIN, plainPin))
            SetValue("AdminPinHash", hash)
        End Sub

        ''' <summary>
        ''' Verifies an entered PIN against the stored hash.
        ''' On first use (no hash stored yet), migrates the plain-text registry PIN or falls back to "1234".
        ''' </summary>
        Public Function VerifyAdminPin(enteredPin As String) As Boolean
            Dim storedHash = GetValue("AdminPinHash")

            ' First-time migration: no hash in the encrypted store yet
            If String.IsNullOrEmpty(storedHash) Then
                ' Read legacy plain-text PIN from registry (one-time migration)
                Dim legacyPin = AppConfig.AdminPin
                storedHash = HashPin(legacyPin)
                SetValue("AdminPinHash", storedHash)
            End If

            Return HashPin(If(enteredPin, "")) = storedHash
        End Function

        ''' <summary>Returns True if an admin PIN hash has been stored.</summary>
        Public Function HasAdminPin() As Boolean
            Return Not String.IsNullOrEmpty(GetValue("AdminPinHash"))
        End Function

        ''' <summary>
        ''' Returns the current PIN in plain text ONLY for pre-populating the setup
        ''' dialog during initial configuration. Returns empty once migrated to hash.
        ''' </summary>
        Public Function GetLegacyPinForSetup() As String
            If Not String.IsNullOrEmpty(GetValue("AdminPinHash")) Then Return ""
            Return AppConfig.AdminPin
        End Function

        Private Function HashPin(pin As String) As String
            Dim bytes = System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(If(pin, "")))
            Return BitConverter.ToString(bytes).Replace("-", "").ToLower()
        End Function

        ''' <summary>
        ''' Clears activation fields only. Keeps FirstRunDate so the trial clock
        ''' is not reset on deactivation.
        ''' </summary>
        Public Sub ClearActivation()
            SyncLock _lock
                If _cache Is Nothing Then _cache = ReadFromDisk()
                _cache.Remove("LicenseKey")
                _cache.Remove("LicenseActivatedAt")
                _cache.Remove("LicenseExpiresAt")
                _cache.Remove("LicenseLastVerified")
                _cache.Remove("LicenseToken")
                _cache.Remove("OfflineSince")
                _cache.Remove("ServerRejected")
                WriteToDisk(_cache)
            End SyncLock
        End Sub

    End Module

End Namespace
