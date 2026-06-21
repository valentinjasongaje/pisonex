Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Namespace Config

    ''' <summary>
    ''' Persists the admin PIN hash as a DPAPI-encrypted binary file in %ProgramData%\PisoNet\.
    ''' The file is encrypted to the local machine key — unreadable and unmodifiable
    ''' by users, and non-portable (decryption fails on any other machine).
    ''' </summary>
    Public Module LicenseStore

        Private ReadOnly _filePath As String = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PisoNet", "license.dat")

        Private _cache As Dictionary(Of String, String) = Nothing
        Private ReadOnly _lock As New Object()

        Private Function Load() As Dictionary(Of String, String)
            SyncLock _lock
                If _cache IsNot Nothing Then Return _cache
                _cache = ReadFromDisk()
                Return _cache
            End SyncLock
        End Function

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

            If String.IsNullOrEmpty(storedHash) Then
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

    End Module

End Namespace
