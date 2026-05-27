Imports System.Management
Imports System.Net.NetworkInformation
Imports System.Security.Cryptography
Imports System.Text
Imports Microsoft.Win32

Namespace Services

    ''' <summary>
    ''' Collects per-component hardware identifiers and returns SHA-256 hashes for
    ''' each one.  Sent to pisonex.com on every status/activate/verify call so the
    ''' server can fuzzy-match a returning device whose composite device_id has
    ''' changed (e.g. MachineGuid edited or license.dat deleted).
    '''
    ''' Match rule on the server: 4 of 7 components → same device → original
    ''' first_seen_at is restored → trial clock cannot be reset by editing one
    ''' identifier.  See pisonex-web/src/lib/devicePing.ts for the matcher.
    '''
    ''' Each Get* helper returns "" when the underlying source is unavailable
    ''' (e.g. WMI fails, diskless PXE PC, virtual machine with no SMBIOS UUID).
    ''' Empty results are NOT hashed — they're left empty so a missing field
    ''' doesn't falsely match other missing fields on different machines.
    ''' </summary>
    Public Module HardwareFingerprint

        ''' <summary>
        ''' Returns a dictionary of per-component SHA-256 hex hashes ready to
        ''' send as fp_* fields on a license API call.  Missing components are
        ''' present but empty so the JSON payload shape is stable.
        ''' </summary>
        Public Function Collect() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String) From {
                {"fp_machine_guid",     HashOrEmpty(GetMachineGuid())},
                {"fp_cpu_id",           HashOrEmpty(GetCpuId())},
                {"fp_bios_serial",      HashOrEmpty(GetBiosSerial())},
                {"fp_baseboard_serial", HashOrEmpty(GetBaseboardSerial())},
                {"fp_disk_serial",      HashOrEmpty(GetDiskSerial())},
                {"fp_system_uuid",      HashOrEmpty(GetSystemUuid())},
                {"fp_mac_address",      HashOrEmpty(GetMacAddress())}
            }
        End Function

        ' ── Per-component readers ────────────────────────────────────────

        Public Function GetMachineGuid() As String
            Try
                Using rk = Registry.LocalMachine.OpenSubKey("SOFTWARE\Microsoft\Cryptography")
                    Return If(rk?.GetValue("MachineGuid")?.ToString(), "")
                End Using
            Catch
                Return ""
            End Try
        End Function

        Public Function GetCpuId() As String
            Try
                Using searcher = New ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor")
                    For Each obj In searcher.Get()
                        Dim v = obj("ProcessorId")?.ToString()?.Trim()
                        If Not String.IsNullOrEmpty(v) Then Return v
                    Next
                End Using
            Catch
            End Try
            Return ""
        End Function

        Public Function GetBiosSerial() As String
            Try
                Using searcher = New ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS")
                    For Each obj In searcher.Get()
                        Dim v = obj("SerialNumber")?.ToString()?.Trim()
                        If Not String.IsNullOrEmpty(v) AndAlso Not IsGenericSerial(v) Then Return v
                    Next
                End Using
            Catch
            End Try
            Return ""
        End Function

        Public Function GetBaseboardSerial() As String
            Try
                Using searcher = New ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard")
                    For Each obj In searcher.Get()
                        Dim v = obj("SerialNumber")?.ToString()?.Trim()
                        If Not String.IsNullOrEmpty(v) AndAlso Not IsGenericSerial(v) Then Return v
                    Next
                End Using
            Catch
            End Try
            Return ""
        End Function

        Public Function GetDiskSerial() As String
            Try
                Using searcher = New ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0")
                    For Each obj In searcher.Get()
                        Dim v = obj("SerialNumber")?.ToString()?.Trim()
                        If Not String.IsNullOrEmpty(v) Then Return v
                    Next
                End Using
            Catch
            End Try
            Return ""
        End Function

        Public Function GetSystemUuid() As String
            Try
                Using searcher = New ManagementObjectSearcher(
                    "SELECT UUID FROM Win32_ComputerSystemProduct")
                    For Each obj In searcher.Get()
                        Dim v = obj("UUID")?.ToString()?.Trim()
                        ' Some BIOSes report all-zero UUID — useless, treat as missing.
                        If Not String.IsNullOrEmpty(v) AndAlso v <> "00000000-0000-0000-0000-000000000000" Then
                            Return v
                        End If
                    Next
                End Using
            Catch
            End Try
            Return ""
        End Function

        Public Function GetMacAddress() As String
            ' First physical, non-virtual, operational adapter by deterministic name order.
            ' Sorting by name keeps the chosen MAC stable across reboots (NetworkInterface
            ' enumeration order can otherwise drift).
            Try
                Dim adapters = NetworkInterface.GetAllNetworkInterfaces() _
                    .Where(Function(n)
                               If n.NetworkInterfaceType = NetworkInterfaceType.Loopback Then Return False
                               If n.NetworkInterfaceType = NetworkInterfaceType.Tunnel Then Return False
                               Dim desc = n.Description?.ToLowerInvariant()
                               If desc IsNot Nothing Then
                                   If desc.Contains("virtual") Then Return False
                                   If desc.Contains("vpn") Then Return False
                                   If desc.Contains("vmware") Then Return False
                                   If desc.Contains("hyper-v") Then Return False
                                   If desc.Contains("loopback") Then Return False
                               End If
                               Dim mac = n.GetPhysicalAddress()?.ToString()
                               Return Not String.IsNullOrEmpty(mac) AndAlso mac <> "000000000000"
                           End Function) _
                    .OrderBy(Function(n) n.Name) _
                    .ToList()
                If adapters.Count > 0 Then
                    Return adapters(0).GetPhysicalAddress().ToString()
                End If
            Catch
            End Try
            Return ""
        End Function

        ' ── Helpers ──────────────────────────────────────────────────────

        ''' <summary>
        ''' SHA-256 hex of the input, or empty string when input is empty.
        ''' Empty inputs MUST stay empty so missing fields cannot accidentally
        ''' match across machines (hashing "" would otherwise produce a
        ''' constant value that matches every other unfilled field).
        ''' </summary>
        Private Function HashOrEmpty(value As String) As String
            If String.IsNullOrEmpty(value) Then Return ""
            Dim bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))
            Return BitConverter.ToString(bytes).Replace("-", "").ToLower()
        End Function

        ''' <summary>
        ''' Filters out the generic placeholder serials many OEMs leave in the
        ''' BIOS/baseboard fields ("To Be Filled By O.E.M.", "Default string", etc.)
        ''' so they don't cause cross-machine false positives in fuzzy matching.
        ''' </summary>
        Private Function IsGenericSerial(value As String) As Boolean
            Dim v = value.ToLowerInvariant()
            If v = "to be filled by o.e.m." Then Return True
            If v = "default string" Then Return True
            If v = "none" Then Return True
            If v = "system serial number" Then Return True
            If v = "0" Then Return True
            Return False
        End Function

    End Module

End Namespace
