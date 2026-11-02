Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports Microsoft.Win32

''' <summary>
''' Prepares this PC so the server's dashboard can turn it on (Wake-on-LAN).
'''
''' Runs once per service start (= every boot, and right after install) as
''' SYSTEM — the client itself runs as the logged-in user and can't change
''' network-card or power settings. For every wired network card it enables
''' "wake on magic packet" (plus the vendor switches: Realtek "Shutdown
''' Wake-On-Lan", Intel "PME") and lets the card wake the PC; it also turns
''' Windows Fast Startup off, which otherwise stops most cards from waking a
''' PC that was shut down.
'''
''' Only settings that differ are changed, with -NoRestart, so the network
''' never drops; they take effect from the next shutdown. The BIOS "Wake on
''' LAN" switch can't be set from Windows — that stays a one-time manual step.
'''
''' Opt out per PC: HKLM\SOFTWARE\PisoNet\Client  WakeOnLanSetup (DWORD) = 0.
''' </summary>
Public Module WakeOnLanSetup

    Private Const REG_KEY As String = "SOFTWARE\PisoNet\Client"
    Private Const TIMEOUT_MS As Integer = 120_000

    ' Windows PowerShell 5.1 script (NetAdapter module ships with Windows 8+).
    ' No double quotes inside: it's a VB string literal.
    Private Const Script As String = "
$ErrorActionPreference = 'SilentlyContinue'
$done = New-Object System.Collections.Generic.List[string]
$wired = @(Get-NetAdapter -Physical | Where-Object {
    $_.MediaType -eq '802.3' -and $_.InterfaceDescription -notmatch 'Wireless|Wi-Fi|WLAN|802\.11|Bluetooth|Virtual'
})
# Wi-Fi-only PC: Wake-on-LAN can't work, so leave its settings (Fast Startup) alone.
if ($wired.Count -eq 0) { 'no wired network card found - nothing changed'; exit }
foreach ($a in $wired) {
    $pm = Get-NetAdapterPowerManagement -Name $a.Name
    if ($pm -and [string]$pm.WakeOnMagicPacket -eq 'Disabled') {
        Set-NetAdapterPowerManagement -Name $a.Name -WakeOnMagicPacket Enabled -NoRestart
        $done.Add($a.Name + ': wake on magic packet')
    }
    foreach ($k in '*WakeOnMagicPacket', 'S5WakeOnLan', 'EnablePME') {
        $p = Get-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $k
        if ($p -and [string]$p.RegistryValue -ne '1') {
            Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $k -RegistryValue 1 -NoRestart
            $done.Add($a.Name + ': ' + $k)
        }
    }
    $armed = @(powercfg /devicequery wake_armed)
    if ($armed -notcontains $a.InterfaceDescription) {
        powercfg /deviceenablewake $a.InterfaceDescription | Out-Null
        if ($LASTEXITCODE -eq 0) { $done.Add($a.Name + ': allowed to wake the PC') }
    }
}
$pw = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
if ((Get-ItemProperty -Path $pw -Name HiberbootEnabled).HiberbootEnabled -ne 0) {
    Set-ItemProperty -Path $pw -Name HiberbootEnabled -Value 0 -Type DWord
    $done.Add('Fast Startup off')
}
if ($done.Count -gt 0) { 'changed: ' + ($done -join '; ') }
else { 'already set on ' + (($wired | ForEach-Object { $_.Name }) -join ', ') }
"

    Public Sub RunInBackground(log As Action(Of String))
        Dim t As New Thread(Sub() Run(log)) With {.IsBackground = True, .Name = "WakeOnLanSetup"}
        t.Start()
    End Sub

    Private Sub Run(log As Action(Of String))
        Try
            If IsDisabled() Then
                log("Wake-on-LAN setup skipped (WakeOnLanSetup = 0 in the registry).")
                Return
            End If
            Dim psi As New ProcessStartInfo With {
                .FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\v1.0\powershell.exe"),
                .Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " &
                             Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)),
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .CreateNoWindow = True
            }
            Using p = Process.Start(psi)
                Dim output = p.StandardOutput.ReadToEndAsync()
                If Not p.WaitForExit(TIMEOUT_MS) Then
                    Try : p.Kill() : Catch : End Try
                    log("Wake-on-LAN setup timed out.")
                    Return
                End If
                log("Wake-on-LAN setup: " & output.Result.Trim())
            End Using
        Catch ex As Exception
            log("Wake-on-LAN setup failed: " & ex.Message)
        End Try
    End Sub

    Private Function IsDisabled() As Boolean
        Try
            Using key = Registry.LocalMachine.OpenSubKey(REG_KEY)
                Dim v = key?.GetValue("WakeOnLanSetup")
                Return v IsNot Nothing AndAlso Convert.ToInt32(v) = 0
            End Using
        Catch
            Return False
        End Try
    End Function

End Module
