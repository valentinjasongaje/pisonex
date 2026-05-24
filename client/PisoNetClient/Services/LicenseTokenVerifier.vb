Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Namespace Services

    ''' <summary>
    ''' Verifies ES256-signed JWTs issued by pisonex.com.
    ''' The public key is embedded here; the private key never leaves the server.
    ''' Any tampering with the stored token breaks the signature and is rejected.
    ''' </summary>
    Public Module LicenseTokenVerifier

        ' ES256 public key â€” matches LICENSE_SIGNING_PRIVATE_KEY on pisonex.com.
        ' Generated 2026-05-23. Replace both keys together if rotating.
        Private Const PUBLIC_KEY_PEM As String =
            "-----BEGIN PUBLIC KEY-----" & vbLf &
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEMPqOTqe2MzuA4WZDi5LkXR8eDsDZ" & vbLf &
            "LzZRzgFpKVqJaolIe9zRoFAZTEbuB0solxxBxQe0BZOGB35P377p6o6ppw==" & vbLf &
            "-----END PUBLIC KEY-----"

        Public Class TokenClaims
            Public Property Subject As String       ' license_key
            Public Property DeviceId As String      ' did
            Public Property LicenseStatus As String ' stat
            Public Property IssuedAt As Long        ' iat
            Public Property ExpiresAt As Long       ' exp  (token lifetime = offline grace)
            Public Property LicenseExpiresAt As Long? ' exp_lic (subscription expiry, null = lifetime)
        End Class

        ''' <summary>
        ''' Verifies signature, checks expiry, and returns claims.
        ''' Pass expectedDeviceId to also enforce device binding (did claim must match).
        ''' Returns Nothing if the token is missing, tampered, expired, or bound to a different device.
        ''' </summary>
        Public Function Verify(token As String, Optional expectedDeviceId As String = Nothing) As TokenClaims
            If String.IsNullOrWhiteSpace(token) Then Return Nothing

            Try
                Dim parts = token.Split("."c)
                If parts.Length <> 3 Then Return Nothing

                ' Verify ES256 signature (IEEE P1363 format = 64 bytes: râ€–s)
                Dim dataToVerify = Encoding.ASCII.GetBytes($"{parts(0)}.{parts(1)}")
                Dim sigBytes = Base64UrlDecode(parts(2))

                Using ecKey As ECDsa = ECDsa.Create()
                    ecKey.ImportFromPem(PUBLIC_KEY_PEM)
                    If Not ecKey.VerifyData(
                            dataToVerify,
                            sigBytes,
                            HashAlgorithmName.SHA256,
                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation) Then
                        Return Nothing
                    End If
                End Using

                ' Decode payload
                Dim payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts(1)))
                Dim doc = JsonDocument.Parse(payloadJson)
                Dim root = doc.RootElement

                ' Check token expiry
                Dim expElem As JsonElement
                If Not root.TryGetProperty("exp", expElem) Then Return Nothing
                Dim expUnix = expElem.GetInt64()
                If DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expUnix Then Return Nothing

                Dim claims As New TokenClaims()
                claims.ExpiresAt = expUnix

                Dim subElem As JsonElement
                If root.TryGetProperty("sub", subElem) Then claims.Subject = subElem.GetString()

                Dim didElem As JsonElement
                If root.TryGetProperty("did", didElem) Then claims.DeviceId = didElem.GetString()

                Dim statElem As JsonElement
                If root.TryGetProperty("stat", statElem) Then claims.LicenseStatus = statElem.GetString()

                Dim iatElem As JsonElement
                If root.TryGetProperty("iat", iatElem) Then claims.IssuedAt = iatElem.GetInt64()

                Dim expLicElem As JsonElement
                If root.TryGetProperty("exp_lic", expLicElem) AndAlso
                   expLicElem.ValueKind = JsonValueKind.Number Then
                    claims.LicenseExpiresAt = expLicElem.GetInt64()
                End If

                ' Enforce device binding when the caller supplies the expected device ID.
                ' A token issued for a different device is rejected even if the signature is valid.
                If Not String.IsNullOrEmpty(expectedDeviceId) AndAlso
                   Not String.IsNullOrEmpty(claims.DeviceId) AndAlso
                   Not String.Equals(claims.DeviceId, expectedDeviceId, StringComparison.OrdinalIgnoreCase) Then
                    Return Nothing
                End If

                Return claims

            Catch
                Return Nothing
            End Try
        End Function

        Private Function Base64UrlDecode(input As String) As Byte()
            Dim s = input.Replace("-"c, "+"c).Replace("_"c, "/"c)
            Select Case s.Length Mod 4
                Case 2 : s &= "=="
                Case 3 : s &= "="
            End Select
            Return Convert.FromBase64String(s)
        End Function

    End Module

End Namespace

