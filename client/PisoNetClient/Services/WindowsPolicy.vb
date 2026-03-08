Imports Microsoft.Win32
Imports PisoNetClient.Config

Namespace Services

    ''' <summary>
    ''' Applies and removes Windows user-level restrictions (via HKCU registry policies).
    ''' All changes are confined to the current user and are fully reversible.
    ''' Call Apply() on startup and RemoveAll() before the app exits.
    ''' </summary>
    Public Module WindowsPolicy

        ' Registry paths for per-user policy keys
        Private Const SYS_POL As String = "Software\Microsoft\Windows\CurrentVersion\Policies\System"
        Private Const EXP_POL As String = "Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
        Private Const CMD_POL As String = "Software\Policies\Microsoft\Windows\System"

        ''' <summary>Apply restrictions based on current AppConfig settings.</summary>
        Public Sub Apply()
            SetDword(SYS_POL, "DisableTaskMgr",       If(AppConfig.DisableTaskManager,   1, 0))
            SetDword(SYS_POL, "DisableRegistryTools",  If(AppConfig.DisableRegistryTools, 1, 0))
            SetDword(CMD_POL, "DisableCMD",            If(AppConfig.DisableCmdPrompt,     1, 0))
            SetDword(EXP_POL, "NoRun",                 If(AppConfig.DisableRunDialog,     1, 0))
        End Sub

        ''' <summary>
        ''' Remove ALL restrictions unconditionally — always called on clean exit
        ''' so the PC is usable if the app is not running.
        ''' </summary>
        Public Sub RemoveAll()
            ClearValue(SYS_POL, "DisableTaskMgr")
            ClearValue(SYS_POL, "DisableRegistryTools")
            ClearValue(CMD_POL, "DisableCMD")
            ClearValue(EXP_POL, "NoRun")
        End Sub

        ' ── Helpers ──────────────────────────────────────────────────────────

        Private Sub SetDword(keyPath As String, name As String, value As Integer)
            Try
                Dim key = Registry.CurrentUser.CreateSubKey(keyPath, writable:=True)
                If value = 0 Then
                    key?.DeleteValue(name, throwOnMissingValue:=False)
                Else
                    key?.SetValue(name, value, RegistryValueKind.DWord)
                End If
            Catch
            End Try
        End Sub

        Private Sub ClearValue(keyPath As String, name As String)
            Try
                Dim key = Registry.CurrentUser.OpenSubKey(keyPath, writable:=True)
                key?.DeleteValue(name, throwOnMissingValue:=False)
            Catch
            End Try
        End Sub

    End Module

End Namespace
