Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports System.Timers
Imports System.Windows.Forms
Imports Microsoft.Win32
Imports PisoNetClient.Config

Namespace Services

    ''' <summary>
    ''' Three-layer Task Manager / shortcut lockdown:
    '''
    '''   Layer 1 — Registry  : HKCU policy keys disable Task Manager in the OS,
    '''             including the option that appears after Ctrl+Alt+Del.
    '''             Also disables Lock / Change Password on that screen.
    '''
    '''   Layer 2 — Keyboard hook (WH_KEYBOARD_LL):
    '''             Intercepts Ctrl+Shift+Esc and dangerous Win-key combos
    '''             before they reach any window.
    '''             NOTE: Ctrl+Alt+Delete is a Secure Attention Sequence handled
    '''             by the Windows kernel — no user-mode hook can block it.
    '''             Layers 1 and 3 handle what Ctrl+Alt+Del can launch.
    '''
    '''   Layer 3 — Process killer: polls every 500 ms and kills Taskmgr.exe
    '''             the instant it appears (handles the Ctrl+Alt+Del path).
    '''
    ''' All changes are confined to the current user (HKCU) and are fully
    ''' reversed by RemoveAll() before the app exits.
    ''' </summary>
    Public Module WindowsPolicy

        ' ── Registry key paths ────────────────────────────────────────────────

        Private Const SYS_POL As String = "Software\Microsoft\Windows\CurrentVersion\Policies\System"
        Private Const EXP_POL As String = "Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
        Private Const CMD_POL As String = "Software\Policies\Microsoft\Windows\System"

        ' ── Low-level keyboard hook constants ────────────────────────────────

        Private Const WH_KEYBOARD_LL  As Integer = 13
        Private Const WM_KEYDOWN      As Integer = &H100
        Private Const WM_SYSKEYDOWN   As Integer = &H104

        ' ── P/Invoke (Declare works in VB.NET Modules without needing Shared) ─

        Private Declare Auto Function SetWindowsHookEx Lib "user32.dll" (
            idHook As Integer,
            lpfn   As LowLevelKeyboardProc,
            hMod   As IntPtr,
            dwThreadId As UInteger) As IntPtr

        Private Declare Function UnhookWindowsHookEx Lib "user32.dll" (
            hhk As IntPtr) As Boolean

        Private Declare Function CallNextHookEx Lib "user32.dll" (
            hhk    As IntPtr,
            nCode  As Integer,
            wParam As IntPtr,
            lParam As IntPtr) As IntPtr

        Private Declare Auto Function GetModuleHandle Lib "kernel32.dll" (
            lpModuleName As String) As IntPtr

        ' ── Delegate + struct ────────────────────────────────────────────────

        Private Delegate Function LowLevelKeyboardProc(
            nCode  As Integer,
            wParam As IntPtr,
            lParam As IntPtr) As IntPtr

        <StructLayout(LayoutKind.Sequential)>
        Private Structure KBDLLHOOKSTRUCT
            Public vkCode      As UInteger
            Public scanCode    As UInteger
            Public flags       As UInteger
            Public time        As UInteger
            Public dwExtraInfo As IntPtr
        End Structure

        ' ── Module-level state ────────────────────────────────────────────────

        Private _hookHandle   As IntPtr = IntPtr.Zero
        Private _hookCallback As LowLevelKeyboardProc   ' Keep ref — prevents GC of the delegate
        Private _killerTimer  As System.Timers.Timer

        ' Modifier state tracked inside the hook (avoids GetAsyncKeyState in a callback)
        Private _ctrlDown  As Boolean = False
        Private _shiftDown As Boolean = False
        Private _winDown   As Boolean = False

        ' ── Public API ───────────────────────────────────────────────────────

        ''' <summary>Apply restrictions based on current AppConfig settings.</summary>
        Public Sub Apply()
            Dim blockTm  = AppConfig.DisableTaskManager
            Dim blockReg = AppConfig.DisableRegistryTools
            Dim blockCmd = AppConfig.DisableCmdPrompt
            Dim blockRun = AppConfig.DisableRunDialog

            ' Layer 1 — Registry
            ' Task Manager disabled in all launch paths (Ctrl+Shift+Esc, tray, Ctrl+Alt+Del screen)
            SetDword(SYS_POL, "DisableTaskMgr",         If(blockTm, 1, 0))
            ' Remove Lock / Change Password from the Ctrl+Alt+Del security screen
            SetDword(SYS_POL, "DisableLockWorkstation",  If(blockTm, 1, 0))
            SetDword(SYS_POL, "DisableChangePassword",   If(blockTm, 1, 0))

            SetDword(SYS_POL, "DisableRegistryTools",    If(blockReg, 1, 0))
            SetDword(CMD_POL, "DisableCMD",              If(blockCmd, 1, 0))
            SetDword(EXP_POL, "NoRun",                   If(blockRun, 1, 0))

            ' Layer 2 + 3 — Hook and process killer
            If blockTm Then
                InstallKeyboardHook()
                StartProcessKiller()
            End If
        End Sub

        ''' <summary>
        ''' Remove ALL restrictions unconditionally — always called on clean exit
        ''' so the PC is fully usable when PisoNet is not running.
        ''' </summary>
        Public Sub RemoveAll()
            ClearValue(SYS_POL, "DisableTaskMgr")
            ClearValue(SYS_POL, "DisableLockWorkstation")
            ClearValue(SYS_POL, "DisableChangePassword")
            ClearValue(SYS_POL, "DisableRegistryTools")
            ClearValue(CMD_POL, "DisableCMD")
            ClearValue(EXP_POL, "NoRun")

            UninstallKeyboardHook()
            StopProcessKiller()
        End Sub

        ' ── Layer 2: Low-level keyboard hook ─────────────────────────────────

        Private Sub InstallKeyboardHook()
            If _hookHandle <> IntPtr.Zero Then Return   ' Already installed
            _hookCallback = AddressOf KeyboardHookProc
            Using proc = Process.GetCurrentProcess()
                Using m = proc.MainModule
                    _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback,
                                                   GetModuleHandle(m.ModuleName), 0)
                End Using
            End Using
        End Sub

        Private Sub UninstallKeyboardHook()
            If _hookHandle = IntPtr.Zero Then Return
            UnhookWindowsHookEx(_hookHandle)
            _hookHandle = IntPtr.Zero
            _ctrlDown   = False
            _shiftDown  = False
            _winDown    = False
        End Sub

        ''' <summary>
        ''' Called by Windows for every key event system-wide.
        ''' Returns non-zero to swallow the key; zero (via CallNextHookEx) to pass it through.
        ''' </summary>
        Private Function KeyboardHookProc(nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
            If nCode >= 0 Then
                Dim kb    = Marshal.PtrToStructure(Of KBDLLHOOKSTRUCT)(lParam)
                Dim vk    = CType(kb.vkCode, Keys)
                Dim wpInt = wParam.ToInt32()
                Dim isDown = (wpInt = WM_KEYDOWN OrElse wpInt = WM_SYSKEYDOWN)

                ' ── Track modifier state (each key fires its own hook event) ──
                Select Case vk
                    Case Keys.ControlKey, Keys.LControlKey, Keys.RControlKey
                        _ctrlDown = isDown
                    Case Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey
                        _shiftDown = isDown
                    Case Keys.LWin, Keys.RWin
                        _winDown = isDown
                End Select

                If isDown Then
                    ' ── Ctrl+Shift+Esc → Task Manager ─────────────────────────
                    If vk = Keys.Escape AndAlso _ctrlDown AndAlso _shiftDown Then
                        Return New IntPtr(1)   ' Swallowed
                    End If

                    ' ── Windows-key combinations that can bypass restrictions ──
                    If _winDown Then
                        Select Case vk
                            Case Keys.R              ' Win+R  = Run dialog
                                If AppConfig.DisableRunDialog Then Return New IntPtr(1)
                            Case Keys.X              ' Win+X  = Admin shortcuts menu (has Task Manager)
                                Return New IntPtr(1)
                            Case Keys.Tab            ' Win+Tab = Task View (can open Task Manager from here)
                                Return New IntPtr(1)
                            Case Keys.E              ' Win+E  = File Explorer
                                Return New IntPtr(1)
                            Case Keys.D              ' Win+D  = Show Desktop
                                Return New IntPtr(1)
                            Case Keys.L              ' Win+L  = Lock (we control locking ourselves)
                                Return New IntPtr(1)
                        End Select
                    End If
                End If
            End If

            Return CallNextHookEx(_hookHandle, nCode, wParam, lParam)
        End Function

        ' ── Layer 3: Task Manager process killer ──────────────────────────────
        ' Handles the case where Task Manager opens via Ctrl+Alt+Del (SAS),
        ' which cannot be intercepted by any user-mode keyboard hook.

        Private Sub StartProcessKiller()
            If _killerTimer IsNot Nothing Then Return
            _killerTimer = New System.Timers.Timer(500)   ' 500 ms — fast enough to kill before the user can act
            AddHandler _killerTimer.Elapsed, AddressOf OnKillerTick
            _killerTimer.AutoReset = True
            _killerTimer.Start()
        End Sub

        Private Sub StopProcessKiller()
            _killerTimer?.Stop()
            _killerTimer?.Dispose()
            _killerTimer = Nothing
        End Sub

        Private Sub OnKillerTick(sender As Object, e As ElapsedEventArgs)
            For Each p In Process.GetProcessesByName("Taskmgr")
                Try
                    p.Kill()
                Catch
                    ' May fail if Task Manager was launched elevated — handled by Layer 1
                End Try
            Next
        End Sub

        ' ── Registry helpers ─────────────────────────────────────────────────

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
