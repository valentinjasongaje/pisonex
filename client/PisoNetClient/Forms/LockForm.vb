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
    ''' Admin shortcut: Ctrl+Shift+F12 → PASSWORD prompt → AdminPanel.
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
        Private _pnlServerLicenseWarn As Panel   ' top banner when server license is expired
        Private _serverDashboardUrl As String = ""
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
        Private _membershipEnabled    As Boolean = False
        Private _memberLoggedIn       As Boolean = False

        ' Inline member login / register form
        Private _isRegisterMode       As Boolean = False
        Private _canLogout            As Boolean = False
        Private _minimumLogoutMinutes As Integer = 0
        Private _lastLayoutKey As String = ""
        ' Tracks current member-panel mode so layout is only rebuilt on actual transitions,
        ' not on every 1-second heartbeat tick.
        ' Values: "" (uninitialised) | "off" | "member" | "login" | "register"
        Private _lastMemberFormMode As String = ""
        Private _lblModeToggle        As Label       ' "Register" / "Back to Login" link
        Private _lblUsernameHint      As Label
        Private _txtMemberUser        As TextBox
        Private _lblPasswordHint      As Label
        Private _txtMemberPass        As TextBox
        Private _lblConfirmHint       As Label
        Private _txtMemberConf        As TextBox
        Private _lblInlineError       As Label       ' red inline error text

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
            ' Priority: local admin override > server-pushed wallpaper > local fallback > embedded default
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

            ' Fallback to embedded default wallpaper
            If _bgImage Is Nothing Then
                _bgImage = Resources.LogoHelper.GetDefaultWallpaper()
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

            ' Server license expired banner — shown at top of screen
            _pnlServerLicenseWarn = New Panel() With {
                .BackColor = Color.FromArgb(220, 120, 53, 15),
                .Visible   = False,
                .Dock      = DockStyle.Top,
                .Height    = 40
            }
            Dim _lblSrvWarnIcon = New Label() With {
                .Text      = "⚠",
                .Font      = New Font("Segoe UI", 12),
                .ForeColor = Color.FromArgb(251, 191, 36),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(16, 8)
            }
            Dim _lblSrvWarnText = New Label() With {
                .Text      = "Server license expired",
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(42, 4)
            }
            Dim _lblSrvWarnSub = New Label() With {
                .Text      = "Open the server dashboard to activate — Ctrl+Shift+F12 for admin panel",
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(253, 230, 138),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(42, 22)
            }
            Dim _lblSrvWarnLink = New Label() With {
                .Text      = "Open Dashboard",
                .Font      = New Font("Segoe UI", 8, FontStyle.Underline),
                .ForeColor = Color.FromArgb(147, 210, 255),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Cursor    = Cursors.Hand
            }
            AddHandler _lblSrvWarnLink.Click, Sub(s, ev)
                If Not String.IsNullOrEmpty(_serverDashboardUrl) Then
                    Try
                        Process.Start(New ProcessStartInfo(_serverDashboardUrl) With {.UseShellExecute = True})
                    Catch
                    End Try
                End If
            End Sub
            ' Position the link label after the sub text is laid out
            AddHandler _pnlServerLicenseWarn.Layout, Sub(s, ev)
                _lblSrvWarnLink.Location = New Point(
                    _pnlServerLicenseWarn.Width - _lblSrvWarnLink.Width - 16,
                    (_pnlServerLicenseWarn.Height - _lblSrvWarnLink.Height) \ 2)
            End Sub
            _pnlServerLicenseWarn.Controls.AddRange({_lblSrvWarnIcon, _lblSrvWarnText, _lblSrvWarnSub, _lblSrvWarnLink})

            ' ── Membership UI ────────────────────────────────────────────────────
            _pnlMember = New Panel() With {
                .Size      = New Size(340, 160),
                .BackColor = Color.Transparent,
                .Visible   = False
            }
            AddHandler _pnlMember.Paint, AddressOf OnMemberPanelPaint

            ' Enable double buffering on the member panel to prevent repaint flicker.
            ' Panel.DoubleBuffered is a protected property; reflection is the standard
            ' WinForms trick to enable it without needing a subclass.
            Dim _pnlMemberDblBuf = GetType(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance Or System.Reflection.BindingFlags.NonPublic)
            If _pnlMemberDblBuf IsNot Nothing Then
                _pnlMemberDblBuf.SetValue(_pnlMember, True, Nothing)
            End If

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

            ' ── Inline member login / register form controls ──────────────────────
            Dim fldW = 312   ' field width = panel width (360) - padX (24) * 2

            _lblUsernameHint = FormStyles.CreateLabel("Username")
            _lblUsernameHint.Visible = False

            _txtMemberUser = FormStyles.CreateInput(New Point(0, 0), fldW, maxLen:=20)
            _txtMemberUser.Visible = False
            AddHandler _txtMemberUser.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then OnLoginClick(s, e)

            _lblPasswordHint = FormStyles.CreateLabel("Password")
            _lblPasswordHint.Visible = False

            _txtMemberPass = FormStyles.CreateInput(New Point(0, 0), fldW, maxLen:=128, pwChar:="●"c)
            _txtMemberPass.Visible = False
            AddHandler _txtMemberPass.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then OnLoginClick(s, e)

            _lblConfirmHint = FormStyles.CreateLabel("Confirm Password")
            _lblConfirmHint.Visible = False

            _txtMemberConf = FormStyles.CreateInput(New Point(0, 0), fldW, maxLen:=128, pwChar:="●"c)
            _txtMemberConf.Visible = False
            AddHandler _txtMemberConf.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then OnLoginClick(s, e)

            _lblInlineError = New Label() With {
                .Text      = "",
                .Font      = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(239, 68, 68),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(fldW, 18),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible   = False
            }

            _lblModeToggle = New Label() With {
                .Text      = "Register",
                .Font      = New Font("Segoe UI", 8.5F, FontStyle.Underline),
                .ForeColor = MemberAccent,
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Cursor    = Cursors.Hand,
                .Visible   = False
            }
            AddHandler _lblModeToggle.Click, AddressOf OnModeToggleClick

            _pnlMember.Controls.AddRange({_lblUsernameHint, _txtMemberUser,
                                           _lblPasswordHint, _txtMemberPass,
                                           _lblConfirmHint, _txtMemberConf,
                                           _lblInlineError, _lblModeToggle})

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

            Me.Controls.AddRange({_pnlServerLicenseWarn, _lblPCNumber, _lblOffline, _lblMessage, _lblSub, _pnlMember, _pnlReceivingCoins, _pnlStatus, _lblLicenseWarn})
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
                    ' (we don't steal focus from our own admin PASSWORD dialog, etc.)
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

        Private Function GetLayoutKey() As String
            Return $"{_pnlMember.Visible}|{_pnlMember.Height}|{_pnlReceivingCoins.Visible}|{_lblLicenseWarn.Visible}"
        End Function

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
                ' In logged-in state keep the title centered within the panel.
                ' In inline form state the title is positioned by LayoutMemberForm()
                ' and must not be overridden here (it's left-aligned with a toggle).
                If _memberLoggedIn Then
                    _lblMemberTitle.Location = New Point(20, 14)
                    _lblMemberTitle.Size     = New Size(_pnlMember.Width - 40, 20)
                End If
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

        Public Sub ShowServerLicenseWarning(dashboardUrl As String)
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowServerLicenseWarning(dashboardUrl)) : Return
            _serverDashboardUrl = dashboardUrl
            _pnlServerLicenseWarn.Visible = True
        End Sub

        Public Sub HideServerLicenseWarning()
            If Me.InvokeRequired Then Me.Invoke(Sub() HideServerLicenseWarning()) : Return
            _serverDashboardUrl = ""
            _pnlServerLicenseWarn.Visible = False
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

        ' ── Inline form action (Login or Create Account depending on mode) ──────

        Private Sub OnLoginClick(sender As Object, e As EventArgs)
            _lblInlineError.Visible = False
            _lblInlineError.Text = ""

            Dim user = _txtMemberUser.Text.Trim()
            Dim pass = _txtMemberPass.Text

            If String.IsNullOrEmpty(user) OrElse String.IsNullOrEmpty(pass) Then
                _lblInlineError.Text = "Username and password are required."
                _lblInlineError.Visible = True
                Return
            End If

            If _isRegisterMode Then
                If pass <> _txtMemberConf.Text Then
                    _lblInlineError.Text = "Passwords do not match."
                    _lblInlineError.Visible = True
                    Return
                End If
                RaiseEvent MemberRegisterRequested(user, pass)
            Else
                RaiseEvent MemberLoginRequested(user, pass)
            End If
        End Sub

        ' _btnRegister is hidden in inline mode; kept as no-op for safety
        Private Sub OnRegisterClick(sender As Object, e As EventArgs)
        End Sub

        Private Sub OnModeToggleClick(sender As Object, e As EventArgs)
            _isRegisterMode = Not _isRegisterMode
            _txtMemberUser.Text     = ""
            _txtMemberPass.Text     = ""
            _txtMemberConf.Text     = ""
            _lblInlineError.Visible = False
            _lblInlineError.Text    = ""
            _pnlMember.SuspendLayout()
            LayoutMemberForm()
            _pnlMember.ResumeLayout(True)
            ' Keep the mode cache in sync so the next heartbeat doesn't re-run LayoutMemberForm
            _lastMemberFormMode = If(_isRegisterMode, "register", "login")
            _pnlMember.Invalidate()
            CenterLabels()
        End Sub

        Private Sub OnLogoutClick(sender As Object, e As EventArgs)
            If Not _canLogout Then
                ShowCannotLogoutDialog()
                Return
            End If
            If ConfirmLogout() Then
                RaiseEvent MemberLogoutRequested()
            End If
        End Sub

        Private Sub ShowCannotLogoutDialog()
            Dim minTime = _minimumLogoutMinutes
            Dim timeStr = If(minTime > 0,
                             $"{minTime} minute{If(minTime = 1, "", "s")}",
                             "enough")
            Dim msg = $"You need at least {timeStr} of remaining session time to be able to log out."

            Dim dlg = New Form() With {.Size = New Size(420, 138), .TopMost = True}

            Dim lbl = New Label() With {
                .Text      = msg,
                .Font      = New Font("Segoe UI", 10),
                .ForeColor = FormStyles.TextDim,
                .Location  = New Point(24, 16),
                .Size      = New Size(372, 48),
                .AutoSize  = False
            }

            Dim btn = FormStyles.CreateButton("Got it", 140, 36)
            btn.DialogResult = DialogResult.OK
            btn.Location = New Point(140, 76)

            dlg.Controls.AddRange({lbl, btn})
            dlg.AcceptButton = btn
            FormStyles.MakeBorderless(dlg, "Cannot Log Out")
            dlg.ShowDialog()
        End Sub

        ' ── Inline form layout (positions all form controls in _pnlMember) ──────

        Private Sub LayoutMemberForm()
            Const PW     As Integer = 360   ' panel width
            Const PadX   As Integer = 24    ' horizontal padding
            Const PadTop As Integer = 14    ' top padding
            Const FldW   As Integer = PW - PadX * 2    ' = 312
            Const InputH As Integer = 28
            Const LabelH As Integer = 16
            Const Gap    As Integer = 4     ' label → input gap
            Const Block  As Integer = 10    ' between field groups

            Dim y = PadTop

            ' Title (left-aligned, leaves room for toggle)
            _lblMemberTitle.Text      = If(_isRegisterMode, "Create Account", "Member Login")
            _lblMemberTitle.AutoSize  = False
            _lblMemberTitle.Size      = New Size(FldW - 90, 20)
            _lblMemberTitle.TextAlign = ContentAlignment.MiddleLeft
            _lblMemberTitle.Location  = New Point(PadX, y)
            _lblMemberTitle.Visible   = True

            ' Mode toggle link (right side of title row)
            _lblModeToggle.Text    = If(_isRegisterMode, "Back to Login", "Register")
            _lblModeToggle.Visible = True
            Dim toggleW = _lblModeToggle.PreferredWidth
            _lblModeToggle.Location = New Point(PW - PadX - toggleW, y + 2)
            y += 26 + 8

            ' Username
            _lblUsernameHint.Location = New Point(PadX, y)
            _lblUsernameHint.Visible  = True
            y += LabelH + Gap
            _txtMemberUser.Location = New Point(PadX, y)
            _txtMemberUser.Width    = FldW
            _txtMemberUser.Visible  = True
            y += InputH + Block

            ' Password
            _lblPasswordHint.Location = New Point(PadX, y)
            _lblPasswordHint.Visible  = True
            y += LabelH + Gap
            _txtMemberPass.Location = New Point(PadX, y)
            _txtMemberPass.Width    = FldW
            _txtMemberPass.Visible  = True
            y += InputH + Block

            ' Confirm (register mode only)
            If _isRegisterMode Then
                _lblConfirmHint.Location = New Point(PadX, y)
                _lblConfirmHint.Visible  = True
                y += LabelH + Gap
                _txtMemberConf.Location = New Point(PadX, y)
                _txtMemberConf.Width    = FldW
                _txtMemberConf.Visible  = True
                y += InputH + Block
            Else
                _lblConfirmHint.Visible = False
                _txtMemberConf.Visible  = False
            End If

            ' Inline error (space always reserved so layout doesn't jump)
            _lblInlineError.Location = New Point(PadX, y)
            _lblInlineError.Size     = New Size(FldW, 18)
            y += 22

            ' Action button (full field width)
            _btnLogin.Text     = If(_isRegisterMode, "Create Account", "Login")
            _btnLogin.Size     = New Size(FldW, 40)
            _btnLogin.Location = New Point(PadX, y)
            _btnLogin.Visible  = True
            y += 40 + PadTop

            ' Finalize panel height
            _pnlMember.Size = New Size(PW, y)
        End Sub

        Private Sub HideMemberFormControls()
            _lblModeToggle.Visible   = False
            _lblUsernameHint.Visible = False
            _txtMemberUser.Visible   = False
            _lblPasswordHint.Visible = False
            _txtMemberPass.Visible   = False
            _lblConfirmHint.Visible  = False
            _txtMemberConf.Visible   = False
            _lblInlineError.Visible  = False
        End Sub

        ''' <summary>
        ''' Clears all login/register form fields and resets the inline error.
        ''' Called after a successful login or registration to ensure stale
        ''' credentials are not left in the text boxes when the form is next shown.
        ''' </summary>
        Public Sub ClearMemberForm()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ClearMemberForm())
                Return
            End If
            _txtMemberUser.Text     = ""
            _txtMemberPass.Text     = ""
            _txtMemberConf.Text     = ""
            _lblInlineError.Visible = False
            _lblInlineError.Text    = ""
            _isRegisterMode         = False
            ' Reset mode cache so next heartbeat rebuilds the login layout fresh
            _lastMemberFormMode = ""
        End Sub

        Private Function ConfirmLogout() As Boolean
            ' Content coordinates are pre-shift (MakeBorderless adds 38px to every Y).
            ' Final window: 440 × 264 px (226 content + 38 title bar).
            Dim dlg = New Form() With {.Size = New Size(440, 226), .TopMost = True}

            ' ── Icon circle ──────────────────────────────────────────────────────
            Dim iconPanel = New Panel() With {
                .Size      = New Size(52, 52),
                .Location  = New Point((440 - 52) \ 2, 18),
                .BackColor = Color.Transparent
            }
            AddHandler iconPanel.Paint, Sub(s2, ev)
                Dim g = ev.Graphics
                g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
                Using br = New SolidBrush(Color.FromArgb(40, 220, 60, 60))
                    g.FillEllipse(br, 0, 0, 51, 51)
                End Using
                Using pen = New Pen(Color.FromArgb(80, 220, 60, 60), 1.5F)
                    g.DrawEllipse(pen, 1, 1, 49, 49)
                End Using
            End Sub
            Dim iconLbl = New Label() With {
                .Text      = ChrW(&H2715),
                .Font      = New Font("Segoe UI", 16, FontStyle.Bold),
                .ForeColor = Color.FromArgb(220, 90, 90),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(52, 52),
                .Location  = New Point(0, 0),
                .TextAlign = ContentAlignment.MiddleCenter
            }
            iconPanel.Controls.Add(iconLbl)

            ' ── Title ────────────────────────────────────────────────────────────
            Dim lblTitle = New Label() With {
                .Text      = "Log out?",
                .Font      = New Font("Segoe UI", 14, FontStyle.Bold),
                .ForeColor = FormStyles.TextPrimary,
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(400, 30),
                .Location  = New Point(20, 82),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' ── Message ──────────────────────────────────────────────────────────
            Dim lblMsg = New Label() With {
                .Text      = "Your remaining time will be saved to your member account.",
                .Font      = New Font("Segoe UI", 9.5F),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(380, 36),
                .Location  = New Point(30, 118),
                .TextAlign = ContentAlignment.TopCenter
            }

            ' ── Buttons ──────────────────────────────────────────────────────────
            Const BtnW As Integer = 160
            Const BtnH As Integer = 38
            Const BtnGap As Integer = 16
            Dim btnsX = (440 - BtnW * 2 - BtnGap) \ 2

            Dim btnCancel = FormStyles.CreateButton("Cancel", BtnW, BtnH,
                Color.FromArgb(30, 38, 58), FormStyles.TextDim, Color.FromArgb(40, 50, 72))
            btnCancel.DialogResult = DialogResult.No
            btnCancel.Location = New Point(btnsX, 156)
            btnCancel.FlatAppearance.BorderSize = 1
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(50, 80, 120, 200)

            Dim btnLogout = FormStyles.CreateButton("Log Out", BtnW, BtnH,
                FormStyles.DangerRed, Color.White, Color.FromArgb(220, 60, 60))
            btnLogout.DialogResult = DialogResult.Yes
            btnLogout.Location = New Point(btnsX + BtnW + BtnGap, 156)

            dlg.Controls.AddRange({iconPanel, lblTitle, lblMsg, btnCancel, btnLogout})
            dlg.AcceptButton = btnCancel
            dlg.CancelButton = btnCancel

            FormStyles.MakeBorderless(dlg, "Confirm Logout", closable:=False)

            Return dlg.ShowDialog() = DialogResult.Yes
        End Function

        ' ── Membership UI update (called from heartbeat) ─────────────────────

        ''' <summary>
        ''' Only writes to _lblSub when text or fore-color actually changed.
        ''' Prevents AutoSize label reflows and parent-form repaints on every heartbeat.
        ''' </summary>
        Private Sub SetSubLabel(text As String, fore As Color)
            If _lblSub.Text      <> text Then _lblSub.Text      = text
            If _lblSub.ForeColor <> fore  Then _lblSub.ForeColor = fore
        End Sub

        Public Sub UpdateMembershipUI(enabled As Boolean, absorption As Boolean, username As String,
                                       balanceSeconds As Integer, canLogout As Boolean,
                                       zeroTimeLogoutSeconds As Integer, idleShutdownSeconds As Integer,
                                       minimumLogoutMinutes As Integer, serverLicensed As Boolean)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateMembershipUI(enabled, absorption, username, balanceSeconds,
                                                    canLogout, zeroTimeLogoutSeconds, idleShutdownSeconds,
                                                    minimumLogoutMinutes, serverLicensed))
                Return
            End If

            _membershipEnabled    = enabled
            _memberLoggedIn       = Not String.IsNullOrEmpty(username)
            _canLogout            = canLogout
            _minimumLogoutMinutes = minimumLogoutMinutes

            ' ── Server license expired — hide member panel entirely ───────────
            If Not serverLicensed Then
                If _pnlMember.Visible Then
                    _pnlMember.Visible  = False
                    _lastMemberFormMode = "off"
                End If
                Return
            End If

            ' ── Membership disabled ───────────────────────────────────────────
            If Not enabled Then
                If _pnlMember.Visible Then
                    _pnlMember.Visible  = False
                    _lastMemberFormMode = "off"
                End If
                Return
            End If

            If Not _pnlMember.Visible Then _pnlMember.Visible = True

            Const PW As Integer = 360
            Dim cx = PW \ 2
            Dim panelNeedsRepaint As Boolean = False

            If _memberLoggedIn Then
                ' ── Logged-in state ───────────────────────────────────────────
                ' Only rebuild the full control layout when switching INTO this mode.
                ' On every subsequent heartbeat only text / color values are checked.
                If _lastMemberFormMode <> "member" Then
                    _pnlMember.SuspendLayout()
                    HideMemberFormControls()
                    _btnRegister.Visible = False
                    _btnLogin.Visible    = False

                    _lblMemberTitle.AutoSize  = False
                    _lblMemberTitle.Size      = New Size(PW - 40, 20)
                    _lblMemberTitle.TextAlign = ContentAlignment.MiddleCenter
                    _lblMemberTitle.Location  = New Point(20, 14)
                    _lblMemberTitle.Visible   = True

                    _lblMemberInfo.Location = New Point(20, 38)
                    _lblMemberInfo.Size     = New Size(PW - 40, 24)
                    _lblMemberInfo.Visible  = True

                    _lblMemberTime.Location = New Point(20, 64)
                    _lblMemberTime.Size     = New Size(PW - 40, 22)
                    _lblMemberTime.Visible  = True

                    _btnLogout.Location = New Point(cx - _btnLogout.Width \ 2, 96)
                    _btnLogout.Visible  = True
                    _btnLogout.Enabled  = True

                    _pnlMember.Size = New Size(PW, 144)
                    _pnlMember.ResumeLayout(True)
                    _lastMemberFormMode = "member"
                    panelNeedsRepaint   = True
                End If

                ' Title — update only when changed
                If _lblMemberTitle.Text <> "Member" Then
                    _lblMemberTitle.Text = "Member"
                    panelNeedsRepaint    = True
                End If

                ' Username — update only when changed
                If _lblMemberInfo.Text <> username Then
                    _lblMemberInfo.Text      = username
                    _lblMemberInfo.ForeColor = Color.FromArgb(34, 197, 94)
                    panelNeedsRepaint        = True
                End If

                ' Time / status — compute desired text+color, write only when different
                Dim newTimeText As String
                Dim newTimeFore As Color
                If zeroTimeLogoutSeconds > 0 Then
                    newTimeText = $"No time — auto-logout in {zeroTimeLogoutSeconds}s"
                    newTimeFore = Color.FromArgb(245, 158, 11)
                    SetSubLabel("Insert coin to add time", Color.FromArgb(140, 160, 200))
                ElseIf balanceSeconds > 0 Then
                    Dim mins = balanceSeconds \ 60
                    Dim secs = balanceSeconds Mod 60
                    newTimeText = $"Balance: {mins}m {secs}s"
                    newTimeFore = Color.FromArgb(140, 160, 200)
                Else
                    newTimeText = "No time remaining"
                    newTimeFore = Color.FromArgb(160, 120, 140, 170)
                End If
                If _lblMemberTime.Text <> newTimeText OrElse _lblMemberTime.ForeColor <> newTimeFore Then
                    _lblMemberTime.Text      = newTimeText
                    _lblMemberTime.ForeColor = newTimeFore
                    panelNeedsRepaint        = True
                End If

            Else
                ' ── Not logged in — inline login / register form ──────────────
                ' Hide info controls (guard each to avoid redundant property sets)
                If _lblMemberInfo.Visible  Then _lblMemberInfo.Visible  = False
                If _lblMemberTime.Visible  Then _lblMemberTime.Visible  = False
                If _btnLogout.Visible      Then _btnLogout.Visible      = False
                If _btnRegister.Visible    Then _btnRegister.Visible    = False

                ' Only re-run LayoutMemberForm() when the mode actually transitions
                ' (login → register or first time). Static idle heartbeats skip it entirely.
                Dim newMode = If(_isRegisterMode, "register", "login")
                If _lastMemberFormMode <> newMode Then
                    _pnlMember.SuspendLayout()
                    LayoutMemberForm()
                    _pnlMember.ResumeLayout(True)
                    _lastMemberFormMode = newMode
                    panelNeedsRepaint   = True
                End If

                ' Sub-message: idle-shutdown countdown or default — only write when changed
                If idleShutdownSeconds > 0 Then
                    SetSubLabel($"PC will shut down in {idleShutdownSeconds}s",
                                Color.FromArgb(239, 68, 68))
                Else
                    SetSubLabel($"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}",
                                Color.FromArgb(140, 160, 200))
                End If
            End If

            ' Repaint panel only when something visual actually changed
            If panelNeedsRepaint Then _pnlMember.Invalidate()

            ' Full layout pass only when structural dimensions changed;
            ' otherwise re-center just the sub-message (its AutoSize width may shift).
            Dim lk = GetLayoutKey()
            If lk <> _lastLayoutKey Then
                _lastLayoutKey = lk
                CenterLabels()
            Else
                _lblSub.Location = New Point(
                    (Me.ClientSize.Width - _lblSub.Width) \ 2,
                    _lblSub.Location.Y)
            End If
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
            If Not _memberLoggedIn Then
                ' Show inline in the membership form panel
                _lblInlineError.Text    = message
                _lblInlineError.Visible = True
                _pnlMember.Invalidate()
            Else
                MessageBox.Show(message, "Membership", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If
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
