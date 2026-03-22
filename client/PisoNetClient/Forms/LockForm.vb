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

        Private _lblMessage   As Label
        Private _lblSub       As Label
        Private _lblPCNumber  As Label
        Private _lblOffline   As Label
        Private _pnlStatus    As Panel        ' connection status pill (bottom-center)
        Private _lblStatusDot As Label        ' colored dot inside pill
        Private _lblStatusTxt As Label        ' "Connected" / "Disconnected"
        Private _lblLicenseWarn As Label
        Private _bgImage      As Image
        Private _allowClose   As Boolean = False
        Private _isConnected  As Boolean = True
        Private _licenseActive As Boolean = True

        ' Membership UI controls
        Private _pnlMember      As Panel        ' container for membership UI
        Private _lblMemberTitle As Label        ' "Member Access" header
        Private _btnLogin       As Button
        Private _btnRegister    As Button
        Private _lblMemberInfo  As Label        ' "Logged in as: [name]"
        Private _lblMemberTime  As Label        ' balance / zero-time countdown
        Private _btnLogout      As Button
        Private _membershipEnabled As Boolean = False
        Private _memberLoggedIn    As Boolean = False

        ' Receiving-coins indicator
        Private _pnlReceivingCoins As Panel
        Private _lblCoinIcon       As Label
        Private _lblCoinText       As Label
        Private _coinPulseTimer    As System.Windows.Forms.Timer
        Private _coinPulseAlpha    As Integer = 255
        Private _coinPulseUp       As Boolean = False

        ' Membership colors
        Private Shared ReadOnly MemberAccent As Color = Color.FromArgb(79, 142, 247)
        Private Shared ReadOnly MemberBg     As Color = Color.FromArgb(220, 8, 12, 24)
        Private Shared ReadOnly MemberBorder As Color = Color.FromArgb(50, 80, 120, 200)

        Public Event AdminPanelRequested()
        Public Event MemberLoginRequested(username As String, password As String)
        Public Event MemberRegisterRequested(username As String, password As String)
        Public Event MemberLogoutRequested()

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
            ' Priority: local admin override > server-pushed wallpaper > local fallback
            Dim path As String = Nothing

            If AppConfig.UseLocalWallpaper AndAlso Not String.IsNullOrEmpty(AppConfig.LockBgImagePath) Then
                path = AppConfig.LockBgImagePath
            ElseIf Not String.IsNullOrEmpty(AppConfig.ServerWallpaperPath) Then
                path = AppConfig.ServerWallpaperPath
            Else
                path = AppConfig.LockBgImagePath
            End If

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
                .Font      = New Font("Segoe UI", AppConfig.LockPcLabelSize, FontStyle.Bold),
                .ForeColor = Color.FromArgb(AppConfig.LockPcLabelForeArgb),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(24, 24)   ' CenterLabels() will override this
            }

            ' Server-offline indicator — top-right, hidden by default
            _lblOffline = New Label() With {
                .Text      = "Server Offline",
                .Font      = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(245, 158, 11),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Visible   = False
            }

            ' Main message — font/color/position driven by AppConfig (editable in admin panel)
            _lblMessage = New Label() With {
                .Text      = AppConfig.LockMessage,
                .Font      = New Font("Segoe UI", AppConfig.LockMsgSize, FontStyle.Bold),
                .ForeColor = Color.FromArgb(AppConfig.LockMsgForeArgb),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' Sub-message — lighter, thinner
            _lblSub = New Label() With {
                .Text      = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}",
                .Font      = New Font("Segoe UI", 13),
                .ForeColor = Color.FromArgb(140, 160, 200),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' ── Connection status pill (bottom-center) ───────────────────────────
            _pnlStatus = New Panel() With {
                .Size      = New Size(200, 32),
                .BackColor = Color.FromArgb(100, 16, 20, 36)
            }
            AddHandler _pnlStatus.Paint, AddressOf OnStatusPillPaint

            _lblStatusDot = New Label() With {
                .Text      = "●",
                .Font      = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(14, 7)
            }

            _lblStatusTxt = New Label() With {
                .Text      = "Connected to server",
                .Font      = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(140, 160, 200),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(32, 7)
            }

            _pnlStatus.Controls.AddRange({_lblStatusDot, _lblStatusTxt})

            ' License warning — shown when software is not activated
            _lblLicenseWarn = New Label() With {
                .Text      = "Software Not Activated",
                .Font      = New Font("Segoe UI", 11, FontStyle.Bold),
                .ForeColor = Color.FromArgb(239, 68, 68),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Visible   = False
            }

            ' ── Membership UI ────────────────────────────────────────────────────
            _pnlMember = New Panel() With {
                .Size      = New Size(340, 160),
                .BackColor = Color.Transparent,
                .Visible   = False
            }
            AddHandler _pnlMember.Paint, AddressOf OnMemberPanelPaint

            _lblMemberTitle = New Label() With {
                .Text      = "Member Access",
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.FromArgb(120, 150, 200),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(300, 20),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Location  = New Point(20, 14)
            }

            _btnLogin = New Button() With {
                .Text      = "  Login",
                .Size      = New Size(140, 40),
                .Font      = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = MemberAccent,
                .FlatStyle = FlatStyle.Flat,
                .Cursor    = Cursors.Hand,
                .TextAlign = ContentAlignment.MiddleCenter
            }
            _btnLogin.FlatAppearance.BorderSize = 0
            _btnLogin.FlatAppearance.MouseOverBackColor = Color.FromArgb(100, 160, 255)
            AddHandler _btnLogin.Click, AddressOf OnLoginClick

            _btnRegister = New Button() With {
                .Text      = "  Register",
                .Size      = New Size(140, 40),
                .Font      = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(180, 195, 220),
                .BackColor = Color.FromArgb(30, 38, 58),
                .FlatStyle = FlatStyle.Flat,
                .Cursor    = Cursors.Hand,
                .TextAlign = ContentAlignment.MiddleCenter
            }
            _btnRegister.FlatAppearance.BorderSize = 1
            _btnRegister.FlatAppearance.BorderColor = Color.FromArgb(60, 80, 120, 200)
            _btnRegister.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 50, 72)
            AddHandler _btnRegister.Click, AddressOf OnRegisterClick

            _lblMemberInfo = New Label() With {
                .Text      = "",
                .Font      = New Font("Segoe UI", 11, FontStyle.Bold),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(300, 24),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible   = False
            }

            _lblMemberTime = New Label() With {
                .Text      = "",
                .Font      = New Font("Segoe UI", 10),
                .ForeColor = Color.FromArgb(140, 160, 200),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(300, 22),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible   = False
            }

            _btnLogout = New Button() With {
                .Text      = "Logout",
                .Size      = New Size(120, 34),
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = Color.FromArgb(180, 50, 50),
                .FlatStyle = FlatStyle.Flat,
                .Cursor    = Cursors.Hand,
                .Visible   = False
            }
            _btnLogout.FlatAppearance.BorderSize = 0
            _btnLogout.FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 60, 60)
            AddHandler _btnLogout.Click, AddressOf OnLogoutClick

            _pnlMember.Controls.AddRange({_lblMemberTitle, _btnLogin, _btnRegister, _lblMemberInfo, _lblMemberTime, _btnLogout})

            ' ── Receiving-coins indicator (shown when hardware controller is accepting coins for this PC) ──
            _pnlReceivingCoins = New Panel() With {
                .Size      = New Size(280, 44),
                .BackColor = Color.Transparent,
                .Visible   = False
            }
            AddHandler _pnlReceivingCoins.Paint, AddressOf OnReceivingCoinsPaint

            _lblCoinIcon = New Label() With {
                .Text      = "●",
                .Font      = New Font("Segoe UI", 14, FontStyle.Bold),
                .ForeColor = Color.FromArgb(250, 204, 21),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(16, 10)
            }

            _lblCoinText = New Label() With {
                .Text      = "Receiving Coins…",
                .Font      = New Font("Segoe UI", 12, FontStyle.Bold),
                .ForeColor = Color.FromArgb(250, 204, 21),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(42, 11)
            }

            _pnlReceivingCoins.Controls.AddRange({_lblCoinIcon, _lblCoinText})

            ' Pulse animation timer — coin icon gently fades in/out
            _coinPulseTimer = New System.Windows.Forms.Timer() With {.Interval = 60}
            AddHandler _coinPulseTimer.Tick, AddressOf OnCoinPulseTick

            Me.Controls.AddRange({_lblPCNumber, _lblOffline, _lblMessage, _lblSub, _pnlMember, _pnlReceivingCoins, _pnlStatus, _lblLicenseWarn})
        End Sub

        Private Sub OnStatusPillPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

            ' Rounded pill background
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim r = pnl.Height \ 2
            Using path = New Drawing2D.GraphicsPath()
                Dim d = r * 2
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()
                Using br = New SolidBrush(Color.FromArgb(140, 10, 14, 28))
                    g.FillPath(br, path)
                End Using
                Using pen = New Pen(Color.FromArgb(60, 80, 110, 180), 1)
                    g.DrawPath(pen, path)
                End Using
            End Using
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
            Dim g = e.Graphics

            If _bgImage IsNot Nothing Then
                Dim w As Integer, h As Integer
                Select Case AppConfig.LockBgImageFit
                    Case "Cover"
                        Dim s = Math.Max(CSng(Me.Width) / _bgImage.Width, CSng(Me.Height) / _bgImage.Height)
                        w = CInt(_bgImage.Width * s) : h = CInt(_bgImage.Height * s)
                    Case "Stretch"
                        w = Me.Width : h = Me.Height
                    Case Else ' Contain (default)
                        Dim s = Math.Min(CSng(Me.Width) / _bgImage.Width, CSng(Me.Height) / _bgImage.Height)
                        w = CInt(_bgImage.Width * s) : h = CInt(_bgImage.Height * s)
                End Select
                g.DrawImage(_bgImage, (Me.Width - w) \ 2, (Me.Height - h) \ 2, w, h)
                ' Dark overlay
                Using br = New SolidBrush(Color.FromArgb(160, 0, 0, 0))
                    g.FillRectangle(br, Me.ClientRectangle)
                End Using
            Else
                g.Clear(Me.BackColor)
            End If

            ' Bottom gradient fade (subtle vignette for status pill area)
            Dim fadeH = 120
            Dim fadeRect = New Rectangle(0, Me.ClientSize.Height - fadeH, Me.ClientSize.Width, fadeH)
            Using br = New Drawing2D.LinearGradientBrush(
                    fadeRect,
                    Color.FromArgb(0, 0, 0, 0),
                    Color.FromArgb(100, 0, 0, 0),
                    Drawing2D.LinearGradientMode.Vertical)
                g.FillRectangle(br, fadeRect)
            End Using
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
            Dim msgX = If(AppConfig.LockMsgCenterX,
                          msgSlackX \ 2,
                          CInt(msgSlackX * AppConfig.LockMsgXPct / 100.0))
            _lblMessage.Location = New Point(msgX, CInt(msgSlackY * AppConfig.LockMsgYPct / 100.0))

            ' Sub-message always sits directly below the main message, centered
            _lblSub.Location = New Point(
                (Me.ClientSize.Width - _lblSub.Width) \ 2,
                _lblMessage.Bottom + 16)

            ' PC label: centered based on rendered width, or % of screen width
            Dim pcX = If(AppConfig.LockPcLabelCenterX,
                         (Me.ClientSize.Width - _lblPCNumber.Width) \ 2,
                         CInt(Me.ClientSize.Width * AppConfig.LockPcLabelXPct / 100.0))
            _lblPCNumber.Location = New Point(
                pcX,
                CInt(Me.ClientSize.Height * AppConfig.LockPcLabelYPct / 100.0))

            If _lblOffline.Visible Then
                _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
            End If

            ' Status pill — bottom center, 48px from bottom
            _pnlStatus.Location = New Point(
                (Me.ClientSize.Width - _pnlStatus.Width) \ 2,
                Me.ClientSize.Height - _pnlStatus.Height - 48)

            ' Receiving-coins indicator — centered, below sub-message
            If _pnlReceivingCoins.Visible Then
                _pnlReceivingCoins.Location = New Point(
                    (Me.ClientSize.Width - _pnlReceivingCoins.Width) \ 2,
                    _lblSub.Bottom + 20)
            End If

            ' Membership panel — centered, below sub-message (or below receiving-coins)
            If _pnlMember.Visible Then
                _pnlMember.Location = New Point(
                    (Me.ClientSize.Width - _pnlMember.Width) \ 2,
                    _lblSub.Bottom + 28)
                ' Keep title centered within panel
                _lblMemberTitle.Location = New Point(20, 14)
                _lblMemberTitle.Size = New Size(_pnlMember.Width - 40, 20)
            End If

            ' License warning — centered, below sub-message (or below member panel)
            If _lblLicenseWarn.Visible Then
                Dim warnY = If(_pnlMember.Visible, _pnlMember.Bottom + 16, _lblSub.Bottom + 24)
                _lblLicenseWarn.Location = New Point(
                    (Me.ClientSize.Width - _lblLicenseWarn.Width) \ 2,
                    warnY)
            End If
        End Sub

        ' ── License status ───────────────────────────────────────────────────

        Public Sub ShowLicenseWarning(message As String)
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowLicenseWarning(message)) : Return
            _licenseActive = False
            _lblLicenseWarn.Text = message
            _lblLicenseWarn.Visible = True
            _lblMessage.Text = "Software Not Activated"
            _lblSub.Text = "Contact administrator to activate this PC"
            CenterLabels()
        End Sub

        Public Sub HideLicenseWarning()
            If Me.InvokeRequired Then Me.Invoke(Sub() HideLicenseWarning()) : Return
            _licenseActive = True
            _lblLicenseWarn.Visible = False
            _lblMessage.Text = AppConfig.LockMessage
            _lblSub.Text = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
            CenterLabels()
        End Sub

        Public ReadOnly Property IsLicenseActive As Boolean
            Get
                Return _licenseActive
            End Get
        End Property

        ' ── Server-status API ─────────────────────────────────────────────────

        Public Sub ShowOfflineStatus()
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowOfflineStatus()) : Return
            _isConnected = False
            _lblOffline.Visible  = True
            _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
            UpdateStatusPill()
        End Sub

        Public Sub HideOfflineStatus()
            If Me.InvokeRequired Then Me.Invoke(Sub() HideOfflineStatus()) : Return
            _isConnected = True
            _lblOffline.Visible = False
            UpdateStatusPill()
        End Sub

        Private Sub UpdateStatusPill()
            If _isConnected Then
                _lblStatusDot.ForeColor = Color.FromArgb(34, 197, 94)    ' green
                _lblStatusTxt.Text = "Connected to server"
                _lblStatusTxt.ForeColor = Color.FromArgb(140, 160, 200)
            Else
                _lblStatusDot.ForeColor = Color.FromArgb(239, 68, 68)    ' red
                _lblStatusTxt.Text = "Disconnected"
                _lblStatusTxt.ForeColor = Color.FromArgb(239, 68, 68)
            End If
            _pnlStatus.Invalidate()
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

        ' ── Membership panel paint ──────────────────────────────────────────
        Private Sub OnMemberPanelPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim r = 14
            Using path = New Drawing2D.GraphicsPath()
                Dim d = r * 2
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()
                ' Dark glass background
                Using br = New SolidBrush(Color.FromArgb(210, 8, 12, 24))
                    g.FillPath(br, path)
                End Using
                ' Subtle border
                Using pen = New Pen(Color.FromArgb(45, 80, 120, 200), 1)
                    g.DrawPath(pen, path)
                End Using
            End Using
            ' Top accent line (matches timer overlay style)
            Dim accentRect = New Rectangle(r, 0, pnl.Width - r * 2, 2)
            Using br = New Drawing2D.LinearGradientBrush(accentRect, MemberAccent,
                    Color.FromArgb(124, 58, 237), 0F)
                g.FillRectangle(br, accentRect)
            End Using
        End Sub

        ' ── Membership click handlers ────────────────────────────────────────

        Private Sub OnLoginClick(sender As Object, e As EventArgs)
            Dim result = ShowMemberDialog("Member Login", showConfirmPassword:=False)
            If result IsNot Nothing Then
                RaiseEvent MemberLoginRequested(result.Item1, result.Item2)
            End If
        End Sub

        Private Sub OnRegisterClick(sender As Object, e As EventArgs)
            Dim result = ShowMemberDialog("Register Account", showConfirmPassword:=True)
            If result IsNot Nothing Then
                RaiseEvent MemberRegisterRequested(result.Item1, result.Item2)
            End If
        End Sub

        Private Sub OnLogoutClick(sender As Object, e As EventArgs)
            If ConfirmLogout() Then
                RaiseEvent MemberLogoutRequested()
            End If
        End Sub

        Private Function ConfirmLogout() As Boolean
            Dim dlg = New Form() With {
                .Size = New Size(340, 130),
                .TopMost = True
            }

            Dim lbl = New Label() With {
                .Text = "Are you sure you want to logout?" & vbCrLf & "Your remaining time will be saved to your account.",
                .Font = New Font("Segoe UI", 9.5F),
                .ForeColor = FormStyles.TextDim,
                .Location = New Point(24, 16), .Size = New Size(280, 42),
                .AutoSize = False
            }

            Dim btnYes = FormStyles.CreateButton("Logout", 130, 34, FormStyles.DangerRed, Color.White, Color.FromArgb(220, 60, 60))
            btnYes.DialogResult = DialogResult.Yes
            btnYes.Location = New Point(24, 68)

            Dim btnNo = FormStyles.CreateButton("Cancel", 130, 34, Color.FromArgb(30, 38, 58),
                FormStyles.TextDim, Color.FromArgb(40, 50, 72))
            btnNo.DialogResult = DialogResult.No
            btnNo.Location = New Point(166, 68)
            btnNo.FlatAppearance.BorderSize = 1
            btnNo.FlatAppearance.BorderColor = Color.FromArgb(60, 80, 120, 200)

            dlg.Controls.AddRange({lbl, btnYes, btnNo})
            dlg.AcceptButton = btnNo
            dlg.CancelButton = btnNo

            FormStyles.MakeBorderless(dlg, "Confirm Logout", closable:=False)

            Return dlg.ShowDialog() = DialogResult.Yes
        End Function

        Private Function ShowMemberDialog(title As String, showConfirmPassword As Boolean) As Tuple(Of String, String)
            Dim fieldH = 28
            Dim labelH = 18
            Dim gap = 6
            Dim padX = 24
            Dim padTop = 20
            Dim fieldW = 292

            ' Calculate content height
            Dim curY = padTop
            curY += labelH + gap + fieldH + 12    ' username
            curY += labelH + gap + fieldH + 12    ' password
            If showConfirmPassword Then
                curY += labelH + gap + fieldH + 12 ' confirm
            End If
            curY += 40 + 20                        ' button + bottom padding

            Dim dlg = New Form() With {
                .Size = New Size(padX * 2 + fieldW + 16, curY),
                .TopMost = True
            }

            Dim y = padTop

            ' Username
            Dim lblUser = FormStyles.CreateLabel("Username")
            lblUser.Location = New Point(padX, y)
            y += labelH + gap
            Dim txtUser = FormStyles.CreateInput(New Point(padX, y), fieldW, maxLen:=20)
            y += fieldH + 12

            ' Password
            Dim lblPass = FormStyles.CreateLabel("Password")
            lblPass.Location = New Point(padX, y)
            y += labelH + gap
            Dim txtPass = FormStyles.CreateInput(New Point(padX, y), fieldW, maxLen:=128, pwChar:="●"c)
            y += fieldH + 12

            dlg.Controls.AddRange({lblUser, txtUser, lblPass, txtPass})

            Dim txtConf As TextBox = Nothing
            If showConfirmPassword Then
                Dim lblConf = FormStyles.CreateLabel("Confirm Password")
                lblConf.Location = New Point(padX, y)
                y += labelH + gap
                txtConf = FormStyles.CreateInput(New Point(padX, y), fieldW, maxLen:=128, pwChar:="●"c)
                y += fieldH + 12
                dlg.Controls.AddRange({lblConf, txtConf})
            End If

            ' Action button
            Dim btnText = If(showConfirmPassword, "Create Account", "Login")
            Dim btnOk = FormStyles.CreateButton(btnText, fieldW, 38)
            btnOk.DialogResult = DialogResult.OK
            btnOk.Location = New Point(padX, y)
            dlg.Controls.Add(btnOk)
            dlg.AcceptButton = btnOk

            FormStyles.MakeBorderless(dlg, title)

            If dlg.ShowDialog() = DialogResult.OK Then
                If String.IsNullOrWhiteSpace(txtUser.Text) OrElse String.IsNullOrWhiteSpace(txtPass.Text) Then
                    MessageBox.Show("Username and password are required.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return Nothing
                End If
                If showConfirmPassword AndAlso txtPass.Text <> txtConf.Text Then
                    MessageBox.Show("Passwords do not match.", title, MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return Nothing
                End If
                Return Tuple.Create(txtUser.Text.Trim(), txtPass.Text)
            End If
            Return Nothing
        End Function

        ' ── Membership UI update (called from heartbeat) ─────────────────────

        Public Sub UpdateMembershipUI(enabled As Boolean, absorption As Boolean, username As String,
                                       balanceSeconds As Integer, canLogout As Boolean,
                                       zeroTimeLogoutSeconds As Integer, idleShutdownSeconds As Integer)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateMembershipUI(enabled, absorption, username, balanceSeconds,
                                                    canLogout, zeroTimeLogoutSeconds, idleShutdownSeconds))
                Return
            End If

            _membershipEnabled = enabled
            _memberLoggedIn = Not String.IsNullOrEmpty(username)

            If Not enabled Then
                _pnlMember.Visible = False
                Return
            End If

            _pnlMember.Visible = True
            Dim pw = 340   ' panel width
            Dim cx = pw \ 2 ' center x

            If _memberLoggedIn Then
                ' ── Logged-in state ─────────────────────────────
                _lblMemberTitle.Text = "Member"
                _lblMemberTitle.Visible = True
                _btnLogin.Visible = False
                _btnRegister.Visible = False

                ' Username
                _lblMemberInfo.Visible = True
                _lblMemberInfo.Text = username
                _lblMemberInfo.ForeColor = Color.FromArgb(34, 197, 94)
                _lblMemberInfo.Location = New Point(20, 38)
                _lblMemberInfo.Size = New Size(pw - 40, 24)

                ' Time / status
                _lblMemberTime.Visible = True
                If zeroTimeLogoutSeconds > 0 Then
                    _lblMemberTime.Text = $"No time — auto-logout in {zeroTimeLogoutSeconds}s"
                    _lblMemberTime.ForeColor = Color.FromArgb(245, 158, 11)
                    _lblSub.Text = "Insert coin to add time"
                ElseIf balanceSeconds > 0 Then
                    Dim mins = balanceSeconds \ 60
                    Dim secs = balanceSeconds Mod 60
                    _lblMemberTime.Text = $"Balance: {mins}m {secs}s"
                    _lblMemberTime.ForeColor = Color.FromArgb(140, 160, 200)
                Else
                    _lblMemberTime.Text = "No time remaining"
                    _lblMemberTime.ForeColor = Color.FromArgb(160, 120, 140, 170)
                End If
                _lblMemberTime.Location = New Point(20, 64)
                _lblMemberTime.Size = New Size(pw - 40, 22)

                ' Logout button — centered at bottom
                _btnLogout.Visible = True
                _btnLogout.Enabled = canLogout
                _btnLogout.Location = New Point(cx - _btnLogout.Width \ 2, 96)

                _pnlMember.Size = New Size(pw, 144)
            Else
                ' ── Not logged in — show Login / Register ──────
                _lblMemberTitle.Text = "Member Access"
                _lblMemberTitle.Visible = True
                _btnLogin.Visible = True
                _btnRegister.Visible = True
                _lblMemberInfo.Visible = False
                _lblMemberTime.Visible = False
                _btnLogout.Visible = False

                ' Center the two buttons with a gap between them
                Dim btnGap = 12
                Dim totalBtnW = _btnLogin.Width + btnGap + _btnRegister.Width
                Dim btnStartX = (pw - totalBtnW) \ 2
                Dim btnY = 42
                _btnLogin.Location = New Point(btnStartX, btnY)
                _btnRegister.Location = New Point(btnStartX + _btnLogin.Width + btnGap, btnY)

                _pnlMember.Size = New Size(pw, 98)
            End If

            ' Idle-shutdown warning in sub-message
            If idleShutdownSeconds > 0 AndAlso Not _memberLoggedIn Then
                _lblSub.Text = $"PC will shut down in {idleShutdownSeconds}s"
                _lblSub.ForeColor = Color.FromArgb(239, 68, 68)
            ElseIf Not _memberLoggedIn Then
                _lblSub.Text = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
                _lblSub.ForeColor = Color.FromArgb(140, 160, 200)
            End If

            _pnlMember.Invalidate()
            CenterLabels()
        End Sub

        ' ── Receiving-coins indicator ──────────────────────────────────────────

        Private Sub OnReceivingCoinsPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim r = pnl.Height \ 2
            Using path = New Drawing2D.GraphicsPath()
                Dim d = r * 2
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()
                ' Semi-transparent dark bg with gold tint
                Using br = New SolidBrush(Color.FromArgb(200, 20, 18, 8))
                    g.FillPath(br, path)
                End Using
                Using pen = New Pen(Color.FromArgb(100, 250, 204, 21), 1.5F)
                    g.DrawPath(pen, path)
                End Using
            End Using
        End Sub

        Private Sub OnCoinPulseTick(sender As Object, e As EventArgs)
            If _coinPulseUp Then
                _coinPulseAlpha += 8
                If _coinPulseAlpha >= 255 Then
                    _coinPulseAlpha = 255 : _coinPulseUp = False
                End If
            Else
                _coinPulseAlpha -= 8
                If _coinPulseAlpha <= 80 Then
                    _coinPulseAlpha = 80 : _coinPulseUp = True
                End If
            End If
            _lblCoinIcon.ForeColor = Color.FromArgb(_coinPulseAlpha, 250, 204, 21)
        End Sub

        Public Sub ShowReceivingCoins(isReceiving As Boolean)
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowReceivingCoins(isReceiving)) : Return

            _pnlReceivingCoins.Visible = isReceiving
            If isReceiving Then
                _coinPulseAlpha = 255
                _coinPulseUp = False
                _coinPulseTimer.Start()
                ' Update sub-message to indicate coins are being loaded
                _lblSub.Text = "Coins are being loaded to this PC…"
                _lblSub.ForeColor = Color.FromArgb(250, 204, 21)
            Else
                _coinPulseTimer.Stop()
                ' Restore default sub-message (unless overridden by membership UI)
                If Not _memberLoggedIn Then
                    _lblSub.Text = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
                    _lblSub.ForeColor = Color.FromArgb(140, 160, 200)
                End If
            End If
            CenterLabels()
        End Sub

        Public Sub ShowMemberError(message As String)
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowMemberError(message)) : Return
            MessageBox.Show(message, "Membership", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Sub

        Public Sub ShowMemberSuccess(message As String)
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowMemberSuccess(message)) : Return
            MessageBox.Show(message, "Membership", MessageBoxButtons.OK, MessageBoxIcon.Information)
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
