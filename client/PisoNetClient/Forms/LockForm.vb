Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Timers
Imports System.Windows.Forms
Imports PisoNetClient.Config

Namespace Forms

    ''' <summary>
    ''' Full-screen lock overlay shown when no session is active.
    '''
    ''' Focus / Alt+Tab defence strategy (three layers):
    '''   1. WH_KEYBOARD_LL hook — swallows Alt+Tab, Alt+Esc, and Win+Tab at the
    '''      OS level BEFORE they are processed by the shell.  This is the only
    '''      reliable way to block Alt+Tab; OnKeyDown never sees it.
    '''   2. OnDeactivate — if something does steal focus (e.g. a DirectX exclusive
    '''      fullscreen app breaking out on its own) we minimise the intruding window
    '''      and immediately reclaim focus.
    '''   3. FocusTimer — a 750 ms heartbeat that checks the foreground window owner
    '''      and re-asserts our window if it belongs to a different process.
    '''
    ''' Admin shortcut: Ctrl+Shift+F12 → PIN prompt → AdminPanel.
    ''' </summary>
    Public Class LockForm
        Inherits Form

        ' ── UI controls ───────────────────────────────────────────────────────

        Private _lblMessage  As Label
        Private _lblSub      As Label
        Private _lblPCNumber As Label
        Private _lblOffline  As Label
        Private _bgImage     As Image
        Private _allowClose  As Boolean = False

        Public Event AdminPanelRequested()

        ' ── P/Invoke ──────────────────────────────────────────────────────────

        Private Const WH_KEYBOARD_LL   As Integer  = 13
        Private Const WM_KEYDOWN       As Integer  = &H100
        Private Const WM_SYSKEYDOWN    As Integer  = &H104
        Private Const SW_MINIMIZE      As Integer  = 6

        ' Used by ForceToFront — Alt-key trick that lifts Windows' foreground-lock
        Private Const VK_MENU          As Byte     = &H12   ' virtual key code for Alt
        Private Const KEYEVENTF_KEYUP  As UInteger = &H2    ' flag: key-up event
        ' Low-level hook flag — set when a key event was injected by keybd_event / SendInput
        Private Const LLKHF_INJECTED   As UInteger = &H10

        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function SetWindowsHookEx(idHook As Integer,
                                                  lpfn   As LowLevelKeyboardProc,
                                                  hMod   As IntPtr,
                                                  dwThreadId As UInteger) As IntPtr
        End Function

        <DllImport("user32.dll", SetLastError:=True)>
        Private Shared Function UnhookWindowsHookEx(hhk As IntPtr) As Boolean
        End Function

        <DllImport("user32.dll")>
        Private Shared Function CallNextHookEx(hhk As IntPtr, nCode As Integer,
                                               wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        <DllImport("kernel32.dll", CharSet:=CharSet.Auto)>
        Private Shared Function GetModuleHandle(lpModuleName As String) As IntPtr
        End Function

        <DllImport("user32.dll")>
        Private Shared Function SetForegroundWindow(hWnd As IntPtr) As Boolean
        End Function

        <DllImport("user32.dll")>
        Private Shared Function GetForegroundWindow() As IntPtr
        End Function

        <DllImport("user32.dll")>
        Private Shared Function GetWindowThreadProcessId(hWnd As IntPtr,
                                                         ByRef lpdwProcessId As Integer) As Integer
        End Function

        <DllImport("user32.dll")>
        Private Shared Function ShowWindow(hWnd As IntPtr, nCmdShow As Integer) As Boolean
        End Function

        <DllImport("user32.dll")>
        Private Shared Function EnumWindows(lpEnumFunc As EnumWindowsProc,
                                            lParam     As IntPtr) As Boolean
        End Function

        <DllImport("user32.dll")>
        Private Shared Function IsWindowVisible(hWnd As IntPtr) As Boolean
        End Function

        Private Delegate Function EnumWindowsProc(hWnd As IntPtr, lParam As IntPtr) As Boolean

        ''' <summary>
        ''' Injects a synthetic key event.  Used to press/release Alt so that
        ''' SetForegroundWindow is permitted by the OS (it is normally restricted
        ''' to the foreground process, but a pending Alt press lifts that restriction).
        ''' </summary>
        <DllImport("user32.dll")>
        Private Shared Sub keybd_event(bVk As Byte, bScan As Byte,
                                       dwFlags As UInteger, dwExtraInfo As IntPtr)
        End Sub

        Private Delegate Function LowLevelKeyboardProc(nCode As Integer,
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

        ' ── Hook state ────────────────────────────────────────────────────────

        Private _hookHandle   As IntPtr = IntPtr.Zero
        Private _hookCallback As LowLevelKeyboardProc   ' Held to prevent GC
        Private _altDown      As Boolean = False
        Private _winDown      As Boolean = False

        ' ── Focus-recapture timer ─────────────────────────────────────────────

        Private _focusTimer As System.Timers.Timer

        ' ── Constructor ───────────────────────────────────────────────────────

        Public Sub New()
            LoadBackground()
            InitializeComponent()
        End Sub

        Private Sub LoadBackground()
            Dim path = AppConfig.LockBgImagePath
            If Not String.IsNullOrEmpty(path) AndAlso File.Exists(path) Then
                Try
                    _bgImage = Image.FromFile(path)
                Catch
                    _bgImage = Nothing
                End Try
            End If
        End Sub

        Private Sub InitializeComponent()
            Me.SetStyle(ControlStyles.AllPaintingInWmPaint Or
                        ControlStyles.OptimizedDoubleBuffer Or
                        ControlStyles.ResizeRedraw, True)
            Me.FormBorderStyle = FormBorderStyle.None
            Me.WindowState     = FormWindowState.Maximized
            Me.TopMost         = True
            Me.ShowInTaskbar   = False
            Me.BackColor       = Color.FromArgb(AppConfig.LockBgArgb)
            Me.ForeColor       = Color.White
            Me.KeyPreview      = True
            Me.Cursor          = Cursors.Default

            ' PC number badge — position driven by AppConfig.LockPcLabelX/YPct
            _lblPCNumber = New Label() With {
                .Text      = $"PC {AppConfig.PCNumber:D2}",
                .Font      = New Font("Segoe UI", AppConfig.LockPcLabelSize),
                .ForeColor = Color.FromArgb(AppConfig.LockPcLabelForeArgb),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(24, 24)   ' CenterLabels() will override this
            }

            ' Server-offline indicator — top-right, hidden by default
            _lblOffline = New Label() With {
                .Text      = "⚠  Server Offline",
                .Font      = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(245, 158, 11),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Visible   = False
            }

            ' Main message — font/color/position driven by AppConfig
            _lblMessage = New Label() With {
                .Text      = AppConfig.LockMessage,
                .Font      = New Font("Segoe UI", AppConfig.LockMsgSize, FontStyle.Bold),
                .ForeColor = Color.FromArgb(AppConfig.LockMsgForeArgb),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' Sub-message
            _lblSub = New Label() With {
                .Text      = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}",
                .Font      = New Font("Segoe UI", 14),
                .ForeColor = Color.FromArgb(120, 140, 180),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Me.Controls.AddRange({_lblPCNumber, _lblOffline, _lblMessage, _lblSub})
        End Sub

        ' ── ForceToFront: the Alt-trick ───────────────────────────────────────

        ''' <summary>
        ''' Brings this window to the foreground reliably, even when Windows'
        ''' foreground-lock would normally prevent it.
        '''
        ''' The trick: injecting a synthetic Alt key press resets the OS foreground-
        ''' lock timer, temporarily allowing any process to call SetForegroundWindow.
        ''' The injected event is marked LLKHF_INJECTED so our own keyboard hook
        ''' passes it through without treating it as a user keystroke.
        '''
        ''' Must be called on the UI thread.
        ''' </summary>
        Private Sub ForceToFront()
            Me.WindowState = FormWindowState.Maximized
            keybd_event(VK_MENU, 0, 0, IntPtr.Zero)                ' synthetic Alt ↓
            SetForegroundWindow(Me.Handle)
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero)  ' synthetic Alt ↑
            Me.BringToFront()
            Me.Activate()
        End Sub

        ' ── Visibility — install / remove defences ────────────────────────────

        Protected Overrides Sub OnVisibleChanged(e As EventArgs)
            MyBase.OnVisibleChanged(e)
            If Me.Visible Then
                MinimizeAllOtherWindows()     ' Minimize ALL apps so fullscreen games release the display
                InstallHook()
                StartFocusTimer()

                ' DirectX exclusive-fullscreen apps (e.g. CS:S) need ~400 ms to release
                ' the display before another window can paint over them.
                Task.Delay(400).ContinueWith(Sub(t)
                    Try
                        If Me.IsHandleCreated AndAlso Me.Visible Then
                            Me.Invoke(Sub() ForceToFront())
                        End If
                    Catch
                    End Try
                End Sub)
            Else
                UninstallHook()
                StopFocusTimer()
            End If
        End Sub

        ' ── Layer 1: Low-level keyboard hook ─────────────────────────────────

        Private Sub InstallHook()
            If _hookHandle <> IntPtr.Zero Then Return
            _hookCallback = AddressOf KeyboardHookProc
            Using proc = Process.GetCurrentProcess()
                Using m = proc.MainModule
                    _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback,
                                                   GetModuleHandle(m.ModuleName), 0)
                End Using
            End Using
        End Sub

        Private Sub UninstallHook()
            If _hookHandle = IntPtr.Zero Then Return
            UnhookWindowsHookEx(_hookHandle)
            _hookHandle = IntPtr.Zero
            _altDown    = False
            _winDown    = False
        End Sub

        Private Function KeyboardHookProc(nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
            If nCode >= 0 Then
                Dim kb    = Marshal.PtrToStructure(Of KBDLLHOOKSTRUCT)(lParam)

                ' Never block synthetic (injected) key events — these come from our own
                ' ForceToFront() helper (keybd_event Alt-trick) and must not be swallowed.
                If (kb.flags And LLKHF_INJECTED) <> 0 Then
                    Return CallNextHookEx(_hookHandle, nCode, wParam, lParam)
                End If

                Dim vk    = CType(kb.vkCode, Keys)
                Dim wpInt = wParam.ToInt32()
                Dim isDown = (wpInt = WM_KEYDOWN OrElse wpInt = WM_SYSKEYDOWN)

                ' Track Alt and Win modifier state
                Select Case vk
                    Case Keys.Menu          ' VK_MENU — the Alt key
                        _altDown = isDown
                    Case Keys.LWin, Keys.RWin
                        _winDown = isDown
                End Select

                If isDown Then
                    ' ── Block all window-switching shortcuts ──────────────────

                    ' Alt+Tab / Alt+Shift+Tab — the main culprit
                    If vk = Keys.Tab AndAlso _altDown Then Return New IntPtr(1)

                    ' Alt+Escape — cycles through windows in Z-order
                    If vk = Keys.Escape AndAlso _altDown Then Return New IntPtr(1)

                    ' Win+Tab — Task View (second path to task switching)
                    If vk = Keys.Tab AndAlso _winDown Then Return New IntPtr(1)

                    ' Win+D — Show Desktop (another way to lose the lock form)
                    If vk = Keys.D AndAlso _winDown Then Return New IntPtr(1)

                    ' Win+M — Minimize all (same effect as Show Desktop)
                    If vk = Keys.M AndAlso _winDown Then Return New IntPtr(1)

                    ' ── Admin shortcut (pass through) ─────────────────────────
                    ' Ctrl+Shift+F12 is handled in OnKeyDown below — let it through
                End If
            End If

            Return CallNextHookEx(_hookHandle, nCode, wParam, lParam)
        End Function

        ' ── Layer 2: OnDeactivate — immediate response to focus loss ─────────

        Protected Overrides Sub OnDeactivate(e As EventArgs)
            MyBase.OnDeactivate(e)
            Me.TopMost = True

            ' Find the window that just stole focus
            Dim fg = GetForegroundWindow()
            Dim fgPid As Integer = 0
            GetWindowThreadProcessId(fg, fgPid)

            ' If it belongs to a different process, minimize it and reclaim focus
            If fgPid <> Process.GetCurrentProcess().Id Then
                ShowWindow(fg, SW_MINIMIZE)
            End If

            ForceToFront()
        End Sub

        ' ── Layer 3: Focus heartbeat timer ────────────────────────────────────

        Private Sub StartFocusTimer()
            If _focusTimer IsNot Nothing Then Return
            _focusTimer = New System.Timers.Timer(750)
            AddHandler _focusTimer.Elapsed, AddressOf OnFocusTick
            _focusTimer.AutoReset = True
            _focusTimer.Start()
        End Sub

        Private Sub StopFocusTimer()
            _focusTimer?.Stop()
            _focusTimer?.Dispose()
            _focusTimer = Nothing
        End Sub

        Private Sub OnFocusTick(sender As Object, e As ElapsedEventArgs)
            If Not Me.IsHandleCreated OrElse Not Me.Visible Then Return
            Try
                Me.Invoke(Sub()
                    Dim fg = GetForegroundWindow()
                    Dim fgPid As Integer = 0
                    GetWindowThreadProcessId(fg, fgPid)
                    ' Only intervene if the foreground belongs to another process
                    ' (we don't steal focus from our own admin PIN dialog, etc.)
                    If fgPid <> Process.GetCurrentProcess().Id Then
                        ShowWindow(fg, SW_MINIMIZE)
                        ForceToFront()
                    End If
                End Sub)
            Catch
            End Try
        End Sub

        ' ── Minimize ALL other visible windows (handles exclusive fullscreen apps) ──

        Private Sub MinimizeAllOtherWindows()
            Dim myPid = Process.GetCurrentProcess().Id
            EnumWindows(Function(hWnd As IntPtr, lParam As IntPtr) As Boolean
                            If Not IsWindowVisible(hWnd) Then Return True
                            Dim pid As Integer = 0
                            GetWindowThreadProcessId(hWnd, pid)
                            If pid <> myPid Then
                                ShowWindow(hWnd, SW_MINIMIZE)
                            End If
                            Return True
                        End Function, IntPtr.Zero)
        End Sub

        ' ── Background painting ───────────────────────────────────────────────

        Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
            If _bgImage IsNot Nothing Then
                Dim scale = Math.Max(CSng(Me.Width) / _bgImage.Width,
                                     CSng(Me.Height) / _bgImage.Height)
                Dim w = CInt(_bgImage.Width * scale)
                Dim h = CInt(_bgImage.Height * scale)
                e.Graphics.DrawImage(_bgImage, (Me.Width - w) \ 2, (Me.Height - h) \ 2, w, h)
                Using br = New SolidBrush(Color.FromArgb(160, 0, 0, 0))
                    e.Graphics.FillRectangle(br, Me.ClientRectangle)
                End Using
            Else
                e.Graphics.Clear(Me.BackColor)
            End If
        End Sub

        ' ── Layout ───────────────────────────────────────────────────────────

        Protected Overrides Sub OnLoad(e As EventArgs)
            MyBase.OnLoad(e)
            CenterLabels()
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)
            CenterLabels()
        End Sub

        Private Sub CenterLabels()
            ' Main message: position derived from configured percentages.
            ' XPct = % of the slack space (Width - labelWidth), so 50 = centered.
            ' YPct = % of the slack space (Height - labelHeight), so 47 ≈ slightly above middle.
            Dim msgSlackX = Math.Max(0, Me.ClientSize.Width  - _lblMessage.Width)
            Dim msgSlackY = Math.Max(0, Me.ClientSize.Height - _lblMessage.Height)
            _lblMessage.Location = New Point(
                CInt(msgSlackX * AppConfig.LockMsgXPct / 100.0),
                CInt(msgSlackY * AppConfig.LockMsgYPct / 100.0))

            ' Sub-message always sits directly below the main message, centered
            _lblSub.Location = New Point(
                (Me.ClientSize.Width - _lblSub.Width) \ 2,
                _lblMessage.Bottom + 16)

            ' PC label: position as % of screen dimensions (not slack space)
            _lblPCNumber.Location = New Point(
                CInt(Me.ClientSize.Width  * AppConfig.LockPcLabelXPct / 100.0),
                CInt(Me.ClientSize.Height * AppConfig.LockPcLabelYPct / 100.0))

            If _lblOffline.Visible Then
                _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
            End If
        End Sub

        ' ── Server-status API ─────────────────────────────────────────────────

        Public Sub ShowOfflineStatus()
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowOfflineStatus()) : Return
            _lblOffline.Visible  = True
            _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
        End Sub

        Public Sub HideOfflineStatus()
            If Me.InvokeRequired Then Me.Invoke(Sub() HideOfflineStatus()) : Return
            _lblOffline.Visible = False
        End Sub

        Public Sub RefreshAppearance()
            If Me.InvokeRequired Then Me.Invoke(Sub() RefreshAppearance()) : Return
            _bgImage?.Dispose() : _bgImage = Nothing
            LoadBackground()
            Me.BackColor = Color.FromArgb(AppConfig.LockBgArgb)

            ' Main message — text, font size, and color
            _lblMessage.Text      = AppConfig.LockMessage
            _lblMessage.Font      = New Font("Segoe UI", AppConfig.LockMsgSize, FontStyle.Bold)
            _lblMessage.ForeColor = Color.FromArgb(AppConfig.LockMsgForeArgb)

            ' PC label — text, font size, and color (position handled by CenterLabels)
            _lblPCNumber.Text      = $"PC {AppConfig.PCNumber:D2}"
            _lblPCNumber.Font      = New Font("Segoe UI", AppConfig.LockPcLabelSize)
            _lblPCNumber.ForeColor = Color.FromArgb(AppConfig.LockPcLabelForeArgb)

            _lblSub.Text = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
            CenterLabels()
            Me.Invalidate()
        End Sub

        ' ── Keyboard handling (form-level, for keys that reach here) ─────────

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            Select Case True
                ' These are blocked here as a fallback; the hook is the primary gate
                Case e.Alt    AndAlso e.KeyCode = Keys.F4    : e.Handled = True : e.SuppressKeyPress = True
                Case e.Alt    AndAlso e.KeyCode = Keys.Tab   : e.Handled = True : e.SuppressKeyPress = True
                Case e.KeyCode = Keys.LWin OrElse e.KeyCode = Keys.RWin
                    e.Handled = True : e.SuppressKeyPress = True
                ' Admin shortcut: Ctrl+Shift+F12
                Case e.Control AndAlso e.Shift AndAlso e.KeyCode = Keys.F12
                    e.Handled = True : e.SuppressKeyPress = True
                    RaiseEvent AdminPanelRequested()
                Case Else
                    MyBase.OnKeyDown(e)
            End Select
        End Sub

        ' ── Prevent WM_CLOSE unless explicitly allowed ────────────────────────

        Protected Overrides Sub WndProc(ByRef m As Message)
            Const WM_CLOSE As Integer = &H10
            If m.Msg = WM_CLOSE AndAlso Not _allowClose Then Return
            MyBase.WndProc(m)
        End Sub

        Public Sub AllowExit()
            _allowClose = True
            UninstallHook()
            StopFocusTimer()
        End Sub

    End Class

End Namespace
