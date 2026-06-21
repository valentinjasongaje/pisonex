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
        Private _pnlPCBadge   As Panel        ' rounded-square badge wrapping the PC label
        Private _lblOffline   As Label
        Private _pnlStatus    As Panel        ' connection status pill (bottom-center)
        Private _lblStatusDot As Label        ' colored dot inside pill
        Private _lblStatusTxt As Label        ' "Connected" / "Disconnected"
        Private _bgImage      As Image
        Private _allowClose   As Boolean = False
        Private _isConnected  As Boolean = True

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
        ' Debounce counter: how many consecutive heartbeats have returned membership_enabled=False.
        ' The panel is only hidden when this reaches MEMBERSHIP_HIDE_THRESHOLD, preventing
        ' a single transient False from the server causing a visible blink.
        Private _membershipFalseCount As Integer = 0
        Private _membershipShownAt    As DateTime = DateTime.MinValue  ' when panel last became visible
        ' Only hide panel after this many consecutive False heartbeats AND after the panel
        ' has been shown for at least MEMBERSHIP_MIN_VISIBLE_MS milliseconds.
        ' This prevents overlapping async heartbeats (AutoReset timer) from racing the
        ' count to the threshold in under a second.
        Private Const MEMBERSHIP_HIDE_THRESHOLD     As Integer = 20   ' ~20 s of consecutive False
        Private Const MEMBERSHIP_MIN_VISIBLE_MS     As Integer = 15000 ' panel must show ≥ 15 s first
        Private _lblModeToggle        As Label       ' "Register" / "Back to Login" link
        Private _lblUsernameHint      As Label
        Private _txtMemberUser        As TextBox
        Private _lblPasswordHint      As Label
        Private _txtMemberPass        As TextBox
        Private _lblConfirmHint       As Label
        Private _txtMemberConf        As TextBox
        Private _lblInlineError       As Label       ' red inline error text

        ' Membership modal trigger (upper-right composite: badge + pill) + overlay
        Private _btnMemberBadge   As Button         ' circular person-icon button
        Private _pnlMemberTrigger As Panel          ' pill label beside the badge
        Private _memberModalOpen  As Boolean = False
        Private _memberUsername   As String = ""    ' shown in trigger when logged in
        Private _pnlModalBackdrop As Panel          ' full-screen dim overlay
        Private _btnModalClose    As Button         ' × close inside the card

        ' Receiving-coins indicator
        Private _pnlReceivingCoins As Panel
        Private _lblCoinIcon       As Label
        Private _lblCoinText       As Label
        Private _lblCoinProgress   As Label   ' live "₱15 inserted · +1h 30m"
        Private _coinPulseTimer    As System.Windows.Forms.Timer
        Private _coinPulseAlpha    As Integer = 255
        Private _coinPulseUp       As Boolean = False
        ' Last values written to the receiving-coins progress label.  Heartbeats fire
        ' UpdateCoinProgress every second; without these dedup guards each tick rewrites
        ' the transparent label's Text, which forces the parent transparent panel to
        ' repaint and causes a visible flicker.  We also use the pesos delta to decide
        ' whether a NEW coin actually arrived (only then should the countdown reset).
        Private _lastCoinProgressPesos   As Integer = -1
        Private _lastCoinProgressSeconds As Integer = -1

        ' Done-inserting-coins button (shown only while receiving coins)
        Private _btnDoneCoins     As Button

        ' Coin insertion countdown progress bar (shown while receiving coins)
        Private _pnlCoinCountdown   As Panel
        Private _lblCountdownRemain As Label
        Private _coinCountdownTimer As System.Windows.Forms.Timer
        Private _coinCountdownSecs  As Integer = COIN_COUNTDOWN_MAX
        ' Must match server's PC_IDLE_TIMEOUT so the bar drains in sync with when
        ' the server would auto-close the slot.  Each new coin resets to this value.
        Private Const COIN_COUNTDOWN_MAX As Integer = 30

        ' Insert Coin button
        Private _btnInsertCoin    As Button
        Private _coinSlotEnabled  As Boolean = True   ' tracked from heartbeat
        Private _isRequestingCoin As Boolean = False  ' true briefly after click, before receiving_coins=True

        ' Idle shutdown countdown UI
        Private _pnlIdleShutdown  As Panel
        Private _lblIdleTitle     As Label
        Private _lblIdleCount     As Label
        Private _idlePulseTimer   As System.Windows.Forms.Timer
        Private _idlePulseAlpha   As Integer = 50
        Private _idlePulseUp      As Boolean = True
        Private _lastIdleSeconds  As Integer = -1

        ' Membership colors
        Private Shared ReadOnly MemberAccent As Color = Color.FromArgb(79, 142, 247)
        Private Shared ReadOnly MemberBg     As Color = Color.FromArgb(220, 8, 12, 24)
        Private Shared ReadOnly MemberBorder As Color = Color.FromArgb(50, 80, 120, 200)

        Public Event AdminPanelRequested()
        Public Event MemberLoginRequested(username As String, password As String)
        Public Event MemberRegisterRequested(username As String, password As String)
        Public Event MemberLogoutRequested()
        Public Event InsertCoinRequested()
        Public Event DoneInsertingCoinsRequested()

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

        ''' <summary>
        ''' Enables the flicker-free transparent-panel style set on the given control.
        '''
        ''' A stock Panel with BackColor = Transparent forces a parent repaint on
        ''' every Invalidate (parent re-draws the wallpaper + dark overlay first,
        ''' then the panel paints on top — visible two-stage flicker).  Enabling
        ''' SupportsTransparentBackColor + AllPaintingInWmPaint + OptimizedDoubleBuffer
        ''' + UserPaint moves the parent-erase + child-paint into the panel's
        ''' off-screen buffer, so the screen sees a single blit per paint cycle.
        '''
        ''' ControlStyles.SetStyle is protected, so we reach for it via reflection.
        ''' </summary>
        Private Shared Sub EnableSmoothTransparency(c As Control)
            Dim mi = GetType(Control).GetMethod("SetStyle",
                System.Reflection.BindingFlags.Instance Or System.Reflection.BindingFlags.NonPublic)
            If mi Is Nothing Then Return
            mi.Invoke(c, New Object() {
                ControlStyles.SupportsTransparentBackColor Or
                ControlStyles.OptimizedDoubleBuffer Or
                ControlStyles.AllPaintingInWmPaint Or
                ControlStyles.UserPaint, True})
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

            ' PC number badge — rounded-square card, upper-left
            _lblPCNumber = New Label() With {
                .Text = $"PC {AppConfig.PCNumber:D2}",
                .Font = New Font("Segoe UI", 56, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .Location = New Point(22, 14)
            }

            ' The badge panel sizes itself around the label with padding
            Const BadgePadH As Integer = 22
            Const BadgePadV As Integer = 14
            _pnlPCBadge = New Panel() With {
                .BackColor = Color.Transparent
            }
            AddHandler _pnlPCBadge.Paint, AddressOf OnPCBadgePaint
            _pnlPCBadge.Controls.Add(_lblPCNumber)
            ' Size is fixed here; will be recalculated after font renders in CenterLabels()
            _pnlPCBadge.Size = New Size(
                _lblPCNumber.PreferredWidth + BadgePadH * 2,
                _lblPCNumber.PreferredHeight + BadgePadV * 2)
            _lblPCNumber.Location = New Point(BadgePadH, BadgePadV)

            ' Server-offline indicator — top-right, hidden by default
            _lblOffline = New Label() With {
                .Text = "Server Offline",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(245, 158, 11),
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .Visible = False
            }

            ' Main message — font/color/position driven by AppConfig (editable in admin panel)
            _lblMessage = New Label() With {
                .Text = AppConfig.LockMessage,
                .Font = New Font("Segoe UI", AppConfig.LockMsgSize, FontStyle.Bold),
                .ForeColor = Color.FromArgb(AppConfig.LockMsgForeArgb),
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' Sub-message — lighter, thinner
            _lblSub = New Label() With {
                .Text = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}",
                .Font = New Font("Segoe UI", 13),
                .ForeColor = Color.FromArgb(140, 160, 200),
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' ── Connection status pill (bottom-center) ───────────────────────────
            _pnlStatus = New Panel() With {
                .Size = New Size(200, 32),
                .BackColor = Color.FromArgb(100, 16, 20, 36)
            }
            AddHandler _pnlStatus.Paint, AddressOf OnStatusPillPaint

            _lblStatusDot = New Label() With {
                .Text = "●",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .Location = New Point(14, 7)
            }

            _lblStatusTxt = New Label() With {
                .Text = "Connected to server",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(140, 160, 200),
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .Location = New Point(32, 7)
            }

            _pnlStatus.Controls.AddRange({_lblStatusDot, _lblStatusTxt})

            ' ── Membership UI ────────────────────────────────────────────────────
            _pnlMember = New Panel() With {
                .Size = New Size(340, 160),
                .BackColor = Color.Transparent,
                .Visible = False
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
                .Text = "Member Access",
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.FromArgb(120, 150, 200),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(300, 20),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Location = New Point(20, 14)
            }

            _btnLogin = New Button() With {
                .Text = "  Login",
                .Size = New Size(140, 40),
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = MemberAccent,
                .FlatStyle = FlatStyle.Flat,
                .Cursor = Cursors.Hand,
                .TextAlign = ContentAlignment.MiddleCenter
            }
            _btnLogin.FlatAppearance.BorderSize = 0
            _btnLogin.FlatAppearance.MouseOverBackColor = Color.FromArgb(100, 160, 255)
            AddHandler _btnLogin.Click, AddressOf OnLoginClick

            _btnRegister = New Button() With {
                .Text = "  Register",
                .Size = New Size(140, 40),
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(180, 195, 220),
                .BackColor = Color.FromArgb(30, 38, 58),
                .FlatStyle = FlatStyle.Flat,
                .Cursor = Cursors.Hand,
                .TextAlign = ContentAlignment.MiddleCenter
            }
            _btnRegister.FlatAppearance.BorderSize = 1
            _btnRegister.FlatAppearance.BorderColor = Color.FromArgb(60, 80, 120, 200)
            _btnRegister.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 50, 72)
            AddHandler _btnRegister.Click, AddressOf OnRegisterClick

            _lblMemberInfo = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 11, FontStyle.Bold),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(300, 24),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible = False
            }

            _lblMemberTime = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 10),
                .ForeColor = Color.FromArgb(140, 160, 200),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(300, 22),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible = False
            }

            _btnLogout = New Button() With {
                .Text = "Logout",
                .Size = New Size(120, 34),
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = Color.FromArgb(180, 50, 50),
                .FlatStyle = FlatStyle.Flat,
                .Cursor = Cursors.Hand,
                .Visible = False
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
                .Text = "",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(239, 68, 68),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(fldW, 18),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible = False
            }

            _lblModeToggle = New Label() With {
                .Text = "Register",
                .Font = New Font("Segoe UI", 8.5F, FontStyle.Underline),
                .ForeColor = MemberAccent,
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .Cursor = Cursors.Hand,
                .Visible = False
            }
            AddHandler _lblModeToggle.Click, AddressOf OnModeToggleClick

            ' Close button — top-right corner of the card (440px card width → x = 404)
            _btnModalClose = New Button() With {
                .Text       = "×",
                .Size       = New Size(32, 32),
                .Font       = New Font("Segoe UI", 14, FontStyle.Bold),
                .ForeColor  = Color.FromArgb(200, 255, 255, 255),
                .BackColor  = Color.Transparent,
                .FlatStyle  = FlatStyle.Flat,
                .Cursor     = Cursors.Hand,
                .Location   = New Point(400, 6)
            }
            _btnModalClose.FlatAppearance.BorderSize = 0
            _btnModalClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 255, 255, 255)
            AddHandler _btnModalClose.Click, Sub(s, e) HideMemberModal()

            _pnlMember.Controls.AddRange({_lblUsernameHint, _txtMemberUser,
                                           _lblPasswordHint, _txtMemberPass,
                                           _lblConfirmHint, _txtMemberConf,
                                           _lblInlineError, _lblModeToggle,
                                           _btnModalClose})

            ' ── Receiving-coins indicator (shown when hardware controller is accepting coins for this PC) ──
            ' Wider, taller card so the title row and the live running-total line both
            ' breathe. The title (dot + "Receiving Coins…") is centered as a unit by
            ' LayoutReceivingPanel(); the progress line is a full-width centered label.
            _pnlReceivingCoins = New Panel() With {
                .Size = New Size(420, 132),
                .BackColor = Color.Transparent,
                .Visible = False
            }
            ' Flicker-free buffering: the pulse timer Invalidates every 150 ms.
            ' Without these styles each tick forces the parent form to repaint the
            ' wallpaper image + dark overlay under the panel before the gradient
            ' paints on top — a visible two-stage flicker.
            EnableSmoothTransparency(_pnlReceivingCoins)
            AddHandler _pnlReceivingCoins.Paint, AddressOf OnReceivingCoinsPaint

            _lblCoinIcon = New Label() With {
                .Text = "●",
                .Font = New Font("Segoe UI", 16, FontStyle.Bold),
                .ForeColor = Color.FromArgb(250, 204, 21),
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .Location = New Point(20, 22)
            }

            _lblCoinText = New Label() With {
                .Text = "Receiving Coins…",
                .Font = New Font("Segoe UI", 17, FontStyle.Bold),
                .ForeColor = Color.FromArgb(250, 204, 21),
                .BackColor = Color.Transparent,
                .AutoSize = True,
                .Location = New Point(48, 20)
            }

            ' Live "₱15 inserted · +1h 30m" — full-width centered so it never drifts
            ' out of its container regardless of how long the text grows.
            _lblCoinProgress = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 14, FontStyle.Bold),
                .ForeColor = Color.FromArgb(236, 238, 244),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(420, 34),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Location = New Point(0, 78)
            }

            _pnlReceivingCoins.Controls.AddRange({_lblCoinIcon, _lblCoinText, _lblCoinProgress})

            ' Pulse animation timer — coin icon gently fades in/out.
            ' 150 ms keeps the breathe effect visible while cutting transparent-panel
            ' Invalidate propagations from 16/s down to ~7/s, which eliminates the
            ' background-image repaint flicker that caused the receiving-coins UI to fluctuate.
            _coinPulseTimer = New System.Windows.Forms.Timer() With {.Interval = 150}
            AddHandler _coinPulseTimer.Tick, AddressOf OnCoinPulseTick

            ' ── Done inserting coins button (shown only while receiving coins) ──
            ' Secondary, gold-outline variant — larger and rounded to match the CTA.
            _btnDoneCoins = New Button() With {
                .Text = "Done inserting Coins",
                .Size = New Size(280, 54),
                .Font = New Font("Segoe UI", 13, FontStyle.Bold),
                .ForeColor = Color.FromArgb(250, 204, 21),
                .BackColor = Color.FromArgb(40, 34, 12),
                .FlatStyle = FlatStyle.Flat,
                .Cursor = Cursors.Hand,
                .Visible = False
            }
            _btnDoneCoins.FlatAppearance.BorderColor = Color.FromArgb(250, 204, 21)
            _btnDoneCoins.FlatAppearance.BorderSize = 2
            _btnDoneCoins.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 46, 16)
            _btnDoneCoins.FlatAppearance.MouseDownBackColor = Color.FromArgb(70, 58, 20)
            _btnDoneCoins.Region = RoundedRegion(_btnDoneCoins.Width, _btnDoneCoins.Height, 14)
            AddHandler _btnDoneCoins.Click, AddressOf OnDoneCoinsClick

            ' ── Coin insertion countdown bar ───────────────────────────────────────
            ' Replaces the "Closing in X seconds" concept: a backward progress bar
            ' (100% → 0%) is shown while the slot is open.  Reaching 0% auto-fires
            ' DoneInsertingCoinsRequested.  Each coin resets the bar to 100%.
            ' The manual Done button stays below for early close.
            _pnlCoinCountdown = New Panel() With {
                .Size      = New Size(420, 52),
                .BackColor = Color.Transparent,
                .Visible   = False
            }
            ' Flicker-free buffering: invalidated every second from the countdown
            ' tick — same parent-repaint flicker as _pnlReceivingCoins.
            EnableSmoothTransparency(_pnlCoinCountdown)
            AddHandler _pnlCoinCountdown.Paint, AddressOf OnCoinCountdownPaint

            _lblCountdownRemain = New Label() With {
                .Text      = $"{COIN_COUNTDOWN_MAX}s · insert more coins",
                .Font      = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(150, 165, 190),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(420, 18),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Location  = New Point(0, 32)
            }
            _pnlCoinCountdown.Controls.Add(_lblCountdownRemain)

            _coinCountdownTimer = New System.Windows.Forms.Timer() With {.Interval = 1000}
            AddHandler _coinCountdownTimer.Tick, AddressOf OnCoinCountdownTick

            ' ── Insert Coin button (shown when coin slot enabled and not yet receiving) ──
            ' Primary CTA — big, gold, rounded.
            _btnInsertCoin = New Button() With {
                .Text = "Insert Coin",
                .Size = New Size(300, 66),
                .Font = New Font("Segoe UI", 16, FontStyle.Bold),
                .ForeColor = Color.FromArgb(24, 16, 4),
                .BackColor = Color.FromArgb(250, 204, 21),
                .FlatStyle = FlatStyle.Flat,
                .Cursor = Cursors.Hand,
                .Visible = False
            }
            _btnInsertCoin.FlatAppearance.BorderSize = 0
            _btnInsertCoin.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 214, 41)
            _btnInsertCoin.FlatAppearance.MouseDownBackColor = Color.FromArgb(235, 175, 0)
            _btnInsertCoin.Region = RoundedRegion(_btnInsertCoin.Width, _btnInsertCoin.Height, 16)
            AddHandler _btnInsertCoin.Click, AddressOf OnInsertCoinClick

            ' ── Idle auto-shutdown countdown card (minimal, bottom-left) ─────────
            _pnlIdleShutdown = New Panel() With {
                .Size = New Size(200, 60),
                .BackColor = Color.Transparent,
                .Visible = False
            }
            ' Flicker-free buffering: pulse timer Invalidates every 60 ms.
            EnableSmoothTransparency(_pnlIdleShutdown)
            AddHandler _pnlIdleShutdown.Paint, AddressOf OnIdleShutdownPaint

            _lblIdleTitle = New Label() With {
                .Text = "Shutting down in",
                .Font = New Font("Segoe UI", 7, FontStyle.Regular),
                .ForeColor = Color.FromArgb(200, 110, 110),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Location = New Point(10, 4),
                .Size = New Size(180, 18)
            }

            _lblIdleCount = New Label() With {
                .Text = "00:00",
                .Font = New Font("Segoe UI", 13, FontStyle.Regular),
                .ForeColor = Color.FromArgb(245, 90, 90),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Location = New Point(8, 22),
                .Size = New Size(180, 32)
            }

            _pnlIdleShutdown.Controls.AddRange({_lblIdleTitle, _lblIdleCount})

            ' Border pulse animation — slow, subtle breathe
            _idlePulseTimer = New System.Windows.Forms.Timer() With {.Interval = 60}
            AddHandler _idlePulseTimer.Tick, AddressOf OnIdlePulseTick

            ' ── Membership modal backdrop + badge button ──────────────────────
            ' Panel.BackColor ignores alpha unless the control supports a transparent
            ' back colour, so a literal FromArgb(160, 0, 0, 0) BackColor would render
            ' as solid black — that's what produced the harsh "dark wave" flash when
            ' the modal opened.  We enable SupportsTransparentBackColor (via the same
            ' reflection trick used elsewhere for DoubleBuffered) and custom-paint a
            ' semi-transparent fill so the dim is a smooth blend over the lock bg.
            _pnlModalBackdrop = New Panel() With {
                .BackColor = Color.Transparent,
                .Visible   = False
            }
            EnableSmoothTransparency(_pnlModalBackdrop)
            AddHandler _pnlModalBackdrop.Paint, Sub(s, e)
                                                    Using br As New SolidBrush(Color.FromArgb(160, 0, 0, 0))
                                                        e.Graphics.FillRectangle(br, _pnlModalBackdrop.ClientRectangle)
                                                    End Using
                                                End Sub
            AddHandler _pnlModalBackdrop.Click, Sub(s, e) HideMemberModal()

            ' Pill trigger (sits to the right of the badge, overlapping its left edge)
            _pnlMemberTrigger = New Panel() With {
                .Size      = New Size(176, 40),
                .BackColor = Color.Transparent,
                .Visible   = False,
                .Cursor    = Cursors.Hand
            }
            AddHandler _pnlMemberTrigger.Paint, AddressOf OnMemberTriggerPaint
            AddHandler _pnlMemberTrigger.Click, Sub(s, e) ShowMemberModal()

            _btnMemberBadge = New Button() With {
                .Size      = New Size(52, 52),
                .BackColor = Color.Transparent,
                .FlatStyle = FlatStyle.Flat,
                .Cursor    = Cursors.Hand,
                .Visible   = False,
                .Text      = ""
            }
            _btnMemberBadge.FlatAppearance.BorderSize         = 0
            _btnMemberBadge.FlatAppearance.MouseOverBackColor = Color.Transparent
            AddHandler _btnMemberBadge.Paint, AddressOf OnBadgeButtonPaint
            AddHandler _btnMemberBadge.Click, Sub(s, e) ShowMemberModal()

            Me.Controls.AddRange({_pnlPCBadge, _lblOffline, _lblMessage, _lblSub, _pnlReceivingCoins, _pnlCoinCountdown, _btnDoneCoins, _btnInsertCoin, _pnlIdleShutdown, _pnlStatus, _pnlMemberTrigger, _btnMemberBadge, _pnlModalBackdrop, _pnlMember})
            _btnInsertCoin.BringToFront()
            _btnDoneCoins.BringToFront()
            ' Badge must sit above the pill trigger (so the circle overlaps the pill's left edge)
            _btnMemberBadge.BringToFront()
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

        Private Sub OnPCBadgePaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

            Const Radius As Integer = 16
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim d = Radius * 2

            Using path = New Drawing2D.GraphicsPath()
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()

                ' Dark semi-transparent fill
                Using br = New SolidBrush(Color.FromArgb(200, 8, 12, 26))
                    g.FillPath(br, path)
                End Using

                ' Subtle blue border
                Using pen = New Pen(Color.FromArgb(140, 59, 130, 246), 1.5F)
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
                ' Reset membership debounce each time the lock form becomes visible.
                ' Without this, false-counts accumulated from the previous lock session
                ' (or from multiple overlapping async heartbeats completing together)
                ' can carry over and cause the panel to hide almost immediately.
                _membershipFalseCount = 0
                _lastMemberFormMode   = ""   ' force a clean layout pass for the new session
                HideMemberModal()           ' close any modal left open from a previous lock session

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
            _altDown = False
            _winDown = False
        End Sub

        Private Function KeyboardHookProc(nCode As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
            If nCode >= 0 Then
                Dim kb = Marshal.PtrToStructure(Of KBDLLHOOKSTRUCT)(lParam)

                ' Never block synthetic (injected) key events — these come from our own
                ' ForceToFront() helper (keybd_event Alt-trick) and must not be swallowed.
                If (kb.flags And LLKHF_INJECTED) <> 0 Then
                    Return CallNextHookEx(_hookHandle, nCode, wParam, lParam)
                End If

                Dim vk = CType(kb.vkCode, Keys)
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
            Dim cw = Me.ClientSize.Width
            Dim ch = Me.ClientSize.Height

            Dim drewImage As Boolean = False
            If _bgImage IsNot Nothing Then
                Try
                    Dim imgW = _bgImage.Width
                    Dim imgH = _bgImage.Height
                    If imgW > 0 AndAlso imgH > 0 Then
                        Dim w As Integer, h As Integer
                        Select Case AppConfig.LockBgImageFit
                            Case "Cover"
                                Dim s = Math.Max(CSng(cw) / imgW, CSng(ch) / imgH)
                                w = CInt(imgW * s) : h = CInt(imgH * s)
                            Case "Stretch"
                                w = cw : h = ch
                            Case Else ' Contain (default)
                                Dim s = Math.Min(CSng(cw) / imgW, CSng(ch) / imgH)
                                w = CInt(imgW * s) : h = CInt(imgH * s)
                        End Select
                        g.DrawImage(_bgImage, (cw - w) \ 2, (ch - h) \ 2, w, h)
                        ' Dark overlay
                        Using br = New SolidBrush(Color.FromArgb(160, 0, 0, 0))
                            g.FillRectangle(br, Me.ClientRectangle)
                        End Using
                        drewImage = True
                    End If
                Catch
                    ' Image became invalid (disposed or corrupt) — clear and fall through
                    _bgImage = Nothing
                End Try
            End If

            If Not drewImage Then
                g.Clear(Me.BackColor)
            End If

            ' Bottom gradient fade (subtle vignette for status pill area)
            Dim fadeH = 120
            If ch > fadeH Then
                Dim fadeRect = New Rectangle(0, ch - fadeH, cw, fadeH)
                Using br = New Drawing2D.LinearGradientBrush(
                        fadeRect,
                        Color.FromArgb(0, 0, 0, 0),
                        Color.FromArgb(100, 0, 0, 0),
                        Drawing2D.LinearGradientMode.Vertical)
                    g.FillRectangle(br, fadeRect)
                End Using
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

        Private Function GetLayoutKey() As String
            Return $"{_btnMemberBadge.Visible}|{_pnlReceivingCoins.Visible}|{_pnlCoinCountdown.Visible}|{_btnInsertCoin.Visible}|{_btnDoneCoins.Visible}"
        End Function

        Private Sub CenterLabels()
            ' Main message: position derived from configured percentages.
            ' XPct = % of the slack space (Width - labelWidth), so 50 = centered.
            ' YPct = % of the slack space (Height - labelHeight), so 47 ≈ slightly above middle.
            Dim msgSlackX = Math.Max(0, Me.ClientSize.Width - _lblMessage.Width)
            Dim msgSlackY = Math.Max(0, Me.ClientSize.Height - _lblMessage.Height)
            Dim msgX = If(AppConfig.LockMsgCenterX,
                          msgSlackX \ 2,
                          CInt(msgSlackX * AppConfig.LockMsgXPct / 100.0))
            _lblMessage.Location = New Point(msgX, CInt(msgSlackY * AppConfig.LockMsgYPct / 100.0))

            ' Sub-message always sits directly below the main message, centered
            _lblSub.Location = New Point(
                (Me.ClientSize.Width - _lblSub.Width) \ 2,
                _lblMessage.Bottom + 16)

            ' PC badge: fixed upper-left position
            _pnlPCBadge.Location = New Point(24, 24)

            If _lblOffline.Visible Then
                _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
            End If

            ' Status pill — bottom center, 48px from bottom
            _pnlStatus.Location = New Point(
                (Me.ClientSize.Width - _pnlStatus.Width) \ 2,
                Me.ClientSize.Height - _pnlStatus.Height - 48)

            ' Insert Coin button — centered, below sub-message
            If _btnInsertCoin.Visible Then
                _btnInsertCoin.Location = New Point(
                    (Me.ClientSize.Width - _btnInsertCoin.Width) \ 2,
                    _lblSub.Bottom + 20)
            End If

            ' Receiving-coins indicator — centered, below sub-message (button is hidden when this shows)
            If _pnlReceivingCoins.Visible Then
                _pnlReceivingCoins.Location = New Point(
                    (Me.ClientSize.Width - _pnlReceivingCoins.Width) \ 2,
                    _lblSub.Bottom + 20)
            End If

            ' Countdown bar — centered, just below the receiving panel
            If _pnlCoinCountdown.Visible Then
                _pnlCoinCountdown.Location = New Point(
                    (Me.ClientSize.Width - _pnlCoinCountdown.Width) \ 2,
                    _pnlReceivingCoins.Bottom + 10)
            End If

            ' Done inserting coins button — centered, below countdown bar (or receiving panel)
            If _btnDoneCoins.Visible Then
                Dim doneBaseY = If(_pnlCoinCountdown.Visible,
                                   _pnlCoinCountdown.Bottom + 10,
                                   _pnlReceivingCoins.Bottom + 14)
                _btnDoneCoins.Location = New Point(
                    (Me.ClientSize.Width - _btnDoneCoins.Width) \ 2,
                    doneBaseY)
            End If

            ' Idle shutdown panel — bottom-left corner, minimal
            If _pnlIdleShutdown.Visible Then
                _pnlIdleShutdown.Location = New Point(
                    20,
                    Me.ClientSize.Height - _pnlIdleShutdown.Height - 20)
            End If

            ' Member badge + pill trigger — upper-right, badge overlaps pill left edge
            If _btnMemberBadge.Visible Then
                Const Overlap As Integer = 26
                Dim totalW = _btnMemberBadge.Width + _pnlMemberTrigger.Width - Overlap
                Dim startX = Me.ClientSize.Width - totalW - 24
                _btnMemberBadge.Location   = New Point(startX, 24)
                _pnlMemberTrigger.Location = New Point(
                    startX + Overlap,
                    24 + (_btnMemberBadge.Height - _pnlMemberTrigger.Height) \ 2)
            End If

            ' Modal backdrop — always covers full client area
            If _pnlModalBackdrop.Visible Then
                _pnlModalBackdrop.Size = Me.ClientSize
            End If

            ' Modal card — always centered when open
            If _pnlMember.Visible Then
                _pnlMember.Location = New Point(
                    (Me.ClientSize.Width - _pnlMember.Width) \ 2,
                    (Me.ClientSize.Height - _pnlMember.Height) \ 2)
                If _memberLoggedIn Then
                    Const LoggedInHeaderH As Integer = 88
                    _lblMemberTitle.Location = New Point(20, (LoggedInHeaderH - 28) \ 2)
                    _lblMemberTitle.Size     = New Size(_pnlMember.Width - 80, 28)
                End If
            End If
        End Sub

        ' ── Server-status API ─────────────────────────────────────────────────

        Public Sub ShowOfflineStatus()
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowOfflineStatus()) : Return
            _isConnected = False
            _lblOffline.Visible  = True
            _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
            UpdateStatusPill()
            UpdateInsertCoinVisibility()
        End Sub

        Public Sub HideOfflineStatus()
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            If Me.InvokeRequired Then Me.Invoke(Sub() HideOfflineStatus()) : Return
            _isConnected = True
            _lblOffline.Visible = False
            UpdateStatusPill()
            UpdateInsertCoinVisibility()
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

            ' PC badge — update text; resize panel to fit label + padding
            _lblPCNumber.Text = $"PC {AppConfig.PCNumber:D2}"
            Const BadgePadH2 As Integer = 22
            Const BadgePadV2 As Integer = 14
            _pnlPCBadge.Size = New Size(
                _lblPCNumber.PreferredWidth  + BadgePadH2 * 2,
                _lblPCNumber.PreferredHeight + BadgePadV2 * 2)
            _lblPCNumber.Location = New Point(BadgePadH2, BadgePadV2)

            _lblSub.Text = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
            CenterLabels()
            Me.Invalidate()
        End Sub

        ' ── Membership panel paint ──────────────────────────────────────────
        Private Sub OnMemberPanelPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g   = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

            Const R       As Integer = 20   ' corner radius
            Const HeaderH As Integer = 88   ' gradient header height
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim d    = R * 2

            ' Build rounded-rect path for the whole card
            Using path = New Drawing2D.GraphicsPath()
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()

                ' Dark card body
                Using br = New SolidBrush(Color.FromArgb(230, 8, 12, 26))
                    g.FillPath(br, path)
                End Using

                ' Clip to card shape, then paint gradient header
                Dim savedState = g.Save()
                g.SetClip(path, Drawing2D.CombineMode.Replace)
                Dim hdrRect = New Rectangle(0, 0, pnl.Width, HeaderH)
                Using br = New Drawing2D.LinearGradientBrush(hdrRect,
                        Color.FromArgb(255, 29, 78, 216),
                        Color.FromArgb(255, 109, 40, 217),
                        Drawing2D.LinearGradientMode.Horizontal)
                    g.FillRectangle(br, hdrRect)
                End Using

                ' Subtle separator line between header and content
                Using pen = New Pen(Color.FromArgb(40, 255, 255, 255), 1)
                    g.DrawLine(pen, 0, HeaderH, pnl.Width, HeaderH)
                End Using
                g.Restore(savedState)

                ' Card border
                Using pen = New Pen(Color.FromArgb(55, 100, 140, 255), 1)
                    g.DrawPath(pen, path)
                End Using
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
            Const PW      As Integer = 440   ' panel width
            Const HeaderH As Integer = 88    ' gradient header painted by OnMemberPanelPaint
            Const PadX    As Integer = 32    ' horizontal content padding
            Const FldW    As Integer = PW - PadX * 2   ' = 376
            Const LabelH  As Integer = 18
            Const Gap     As Integer = 6     ' label → input
            Const Block   As Integer = 18    ' between field groups

            ' ── Header ─────────────────────────────────────────────────────────
            _lblMemberTitle.Text      = If(_isRegisterMode, "Create Account", "Member Login")
            _lblMemberTitle.AutoSize  = False
            _lblMemberTitle.Size      = New Size(PW - 80, 28)
            _lblMemberTitle.Font      = New Font("Segoe UI", 14, FontStyle.Bold)
            _lblMemberTitle.ForeColor = Color.White
            _lblMemberTitle.TextAlign = ContentAlignment.MiddleCenter
            _lblMemberTitle.Location  = New Point(20, (HeaderH - 28) \ 2)
            _lblMemberTitle.Visible   = True

            ' ── Content starts below header ────────────────────────────────────
            Dim y = HeaderH + 22

            ' Larger input fonts for the modern look
            _txtMemberUser.Font = New Font("Segoe UI", 11)
            _txtMemberPass.Font = New Font("Segoe UI", 11)
            _txtMemberConf.Font = New Font("Segoe UI", 11)

            ' Field labels — slightly larger and brighter than default
            _lblUsernameHint.Font     = New Font("Segoe UI", 9, FontStyle.Regular)
            _lblUsernameHint.ForeColor = Color.FromArgb(160, 180, 220)
            _lblPasswordHint.Font     = New Font("Segoe UI", 9, FontStyle.Regular)
            _lblPasswordHint.ForeColor = Color.FromArgb(160, 180, 220)
            _lblConfirmHint.Font      = New Font("Segoe UI", 9, FontStyle.Regular)
            _lblConfirmHint.ForeColor = Color.FromArgb(160, 180, 220)

            ' Username
            _lblUsernameHint.Location = New Point(PadX, y)
            _lblUsernameHint.Visible  = True
            y += LabelH + Gap
            _txtMemberUser.Location = New Point(PadX, y)
            _txtMemberUser.Width    = FldW
            _txtMemberUser.Visible  = True
            y += _txtMemberUser.Height + Block

            ' Password
            _lblPasswordHint.Location = New Point(PadX, y)
            _lblPasswordHint.Visible  = True
            y += LabelH + Gap
            _txtMemberPass.Location = New Point(PadX, y)
            _txtMemberPass.Width    = FldW
            _txtMemberPass.Visible  = True
            y += _txtMemberPass.Height + Block

            ' Confirm (register mode only)
            If _isRegisterMode Then
                _lblConfirmHint.Location = New Point(PadX, y)
                _lblConfirmHint.Visible  = True
                y += LabelH + Gap
                _txtMemberConf.Location = New Point(PadX, y)
                _txtMemberConf.Width    = FldW
                _txtMemberConf.Visible  = True
                y += _txtMemberConf.Height + Block
            Else
                _lblConfirmHint.Visible = False
                _txtMemberConf.Visible  = False
            End If

            ' Inline error
            _lblInlineError.Location = New Point(PadX, y)
            _lblInlineError.Size     = New Size(FldW, 20)
            _lblInlineError.Font     = New Font("Segoe UI", 9.5F)
            y += 24

            ' Action button — full width, rounded via Region
            _btnLogin.Text      = If(_isRegisterMode, "Create Account", "Sign In")
            _btnLogin.Size      = New Size(FldW, 46)
            _btnLogin.Font      = New Font("Segoe UI", 11, FontStyle.Bold)
            _btnLogin.BackColor = Color.FromArgb(37, 99, 235)
            _btnLogin.FlatAppearance.MouseOverBackColor = Color.FromArgb(59, 130, 246)
            _btnLogin.Location  = New Point(PadX, y)
            _btnLogin.Visible   = True
            _btnLogin.Region    = RoundedRegion(_btnLogin.Width, _btnLogin.Height, 10)
            y += 46 + 14

            ' Mode toggle link — centered below button
            _lblModeToggle.Text    = If(_isRegisterMode, "Back to Login", "Don't have an account? Register")
            _lblModeToggle.Font    = New Font("Segoe UI", 9, FontStyle.Underline)
            _lblModeToggle.Visible = True
            Dim toggleW = _lblModeToggle.PreferredWidth
            _lblModeToggle.Location = New Point((PW - toggleW) \ 2, y)
            y += _lblModeToggle.PreferredHeight + 20

            ' Finalize panel size
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
                                       minimumLogoutMinutes As Integer)
            ' Guard against the form being disposed/torn down while a heartbeat callback
            ' is still in flight (app shutdown) — otherwise Invoke throws ObjectDisposedException.
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateMembershipUI(enabled, absorption, username, balanceSeconds,
                                                    canLogout, zeroTimeLogoutSeconds, idleShutdownSeconds,
                                                    minimumLogoutMinutes))
                Return
            End If

            ' ── Idle shutdown — always update, regardless of membership state ──
            UpdateIdleShutdown(idleShutdownSeconds)

            _membershipEnabled    = enabled
            _memberLoggedIn       = Not String.IsNullOrEmpty(username)
            _canLogout            = canLogout
            _minimumLogoutMinutes = minimumLogoutMinutes

            ' ── Membership enabled/disabled with debounce ─────────────────────
            ' Debounce now gates the badge button visibility (not the card panel).
            ' A single False from the server must not cause the button to vanish —
            ' only hide it after MEMBERSHIP_HIDE_THRESHOLD consecutive False readings.
            If Not enabled Then
                _membershipFalseCount += 1
                ' Guard 1 — count threshold not yet reached
                If _membershipFalseCount < MEMBERSHIP_HIDE_THRESHOLD Then
                    Return   ' transient — keep button in its current state
                End If
                ' Guard 2 — button must have been visible for at least MEMBERSHIP_MIN_VISIBLE_MS.
                If _btnMemberBadge.Visible AndAlso
                   (DateTime.Now - _membershipShownAt).TotalMilliseconds < MEMBERSHIP_MIN_VISIBLE_MS Then
                    Return   ' threshold reached but button shown too recently — wait longer
                End If
                ' Sustained False for long enough — hide the composite trigger and close any open modal
                If _btnMemberBadge.Visible Then
                    _btnMemberBadge.Visible   = False
                    _pnlMemberTrigger.Visible = False
                    _lastMemberFormMode       = "off"
                    HideMemberModal()
                End If
                Return
            End If

            ' enabled = True: reset debounce and ensure composite trigger is visible —
            ' but only when the coin slot is NOT currently open.  Opening the login
            ' modal mid-insertion would steal focus from the running-total card.
            _membershipFalseCount = 0
            If Not _pnlReceivingCoins.Visible Then
                If Not _btnMemberBadge.Visible Then
                    _btnMemberBadge.Visible   = True
                    _pnlMemberTrigger.Visible = True
                    _membershipShownAt        = DateTime.Now
                End If
                _btnMemberBadge.Invalidate()
                _pnlMemberTrigger.Invalidate()   ' repaint pill text / border color
            End If

            Const PW As Integer = 440
            Const HeaderH As Integer = 88   ' gradient header height painted in OnMemberPanelPaint
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

                    ' Title in gradient header area
                    _lblMemberTitle.AutoSize  = False
                    _lblMemberTitle.Size      = New Size(PW - 80, 28)
                    _lblMemberTitle.Font      = New Font("Segoe UI", 13, FontStyle.Bold)
                    _lblMemberTitle.ForeColor = Color.White
                    _lblMemberTitle.TextAlign = ContentAlignment.MiddleCenter
                    _lblMemberTitle.Location  = New Point(20, (HeaderH - 28) \ 2)
                    _lblMemberTitle.Visible   = True

                    ' Content below header
                    _lblMemberInfo.Location = New Point(32, HeaderH + 20)
                    _lblMemberInfo.Size     = New Size(PW - 64, 26)
                    _lblMemberInfo.Font     = New Font("Segoe UI", 13, FontStyle.Bold)
                    _lblMemberInfo.Visible  = True

                    _lblMemberTime.Location = New Point(32, HeaderH + 52)
                    _lblMemberTime.Size     = New Size(PW - 64, 22)
                    _lblMemberTime.Font     = New Font("Segoe UI", 10.5F)
                    _lblMemberTime.Visible  = True

                    _btnLogout.Size     = New Size(140, 38)
                    _btnLogout.Location = New Point(cx - 70, HeaderH + 88)
                    _btnLogout.Visible  = True
                    _btnLogout.Enabled  = True

                    _pnlMember.Size = New Size(PW, HeaderH + 144)
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
                    _memberUsername          = username
                    _pnlMemberTrigger.Invalidate()
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
                If _memberUsername <> "" Then
                    _memberUsername = ""
                    _pnlMemberTrigger.Invalidate()
                End If
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

                ' Sub-message: default instruction (idle countdown is shown by _pnlIdleShutdown)
                SetSubLabel($"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}",
                            Color.FromArgb(140, 160, 200))
            End If

            ' Repaint panel only when something visual actually changed
            If panelNeedsRepaint Then
                _pnlMember.Invalidate()
                ' If the modal is open and the card size changed (e.g. login → logged-in),
                ' re-center it immediately so it doesn't drift off-center.
                If _memberModalOpen Then
                    _pnlMember.Location = New Point(
                        (Me.ClientSize.Width - _pnlMember.Width) \ 2,
                        (Me.ClientSize.Height - _pnlMember.Height) \ 2)
                End If
            End If

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

        ' ── Membership modal show / hide ──────────────────────────────────────

        Private Sub OnMemberTriggerPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g   = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

            Dim r    = pnl.Height \ 2
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim d    = r * 2

            ' Pill background
            Using path = New Drawing2D.GraphicsPath()
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()
                Using br = New SolidBrush(Color.FromArgb(200, 6, 10, 22))
                    g.FillPath(br, path)
                End Using
                Dim borderColor = If(_memberLoggedIn,
                                     Color.FromArgb(110, 34, 197, 94),
                                     Color.FromArgb(100, 79, 142, 247))
                Using pen = New Pen(borderColor, 1.5F)
                    g.DrawPath(pen, path)
                End Using
            End Using

            ' Text — offset right of the overlap zone (26px) so the badge circle hides the left portion
            Const OverlapZone As Integer = 26
            Dim text As String
            Dim fore As Color
            If _memberLoggedIn AndAlso Not String.IsNullOrEmpty(_memberUsername) Then
                text = _memberUsername
                fore = Color.FromArgb(34, 197, 94)
            Else
                text = "Login / Register"
                fore = Color.FromArgb(210, 190, 210, 240)
            End If
            Using fnt = New Font("Segoe UI", 9.5F, FontStyle.Bold)
                Dim sf = New StringFormat() With {
                    .Alignment     = StringAlignment.Near,
                    .LineAlignment = StringAlignment.Center,
                    .Trimming      = StringTrimming.EllipsisCharacter
                }
                Dim textRect = New RectangleF(OverlapZone + 6, 0, pnl.Width - OverlapZone - 14, pnl.Height)
                Using br = New SolidBrush(fore)
                    g.DrawString(text, fnt, br, textRect, sf)
                End Using
            End Using
        End Sub

        Private Sub ShowMemberModal()
            If _memberModalOpen Then Return
            ' Ensure content is laid out before showing (first open, mode may be "off")
            If _lastMemberFormMode = "off" OrElse _lastMemberFormMode = "" Then
                _pnlMember.SuspendLayout()
                LayoutMemberForm()
                _pnlMember.ResumeLayout(True)
                _lastMemberFormMode = If(_isRegisterMode, "register", "login")
            End If
            _memberModalOpen = True
            _pnlModalBackdrop.Size    = Me.ClientSize
            _pnlModalBackdrop.Visible = True
            _pnlModalBackdrop.BringToFront()
            _pnlMember.Location = New Point(
                (Me.ClientSize.Width - _pnlMember.Width) \ 2,
                (Me.ClientSize.Height - _pnlMember.Height) \ 2)
            _pnlMember.BringToFront()
            _pnlMember.Visible = True
            If Not _memberLoggedIn AndAlso _txtMemberUser.Visible Then
                _txtMemberUser.Focus()
            End If
        End Sub

        Private Sub HideMemberModal()
            If Not _memberModalOpen Then Return
            _memberModalOpen          = False
            _pnlMember.Visible        = False
            _pnlModalBackdrop.Visible = False
            ' Clear any inline error when closing
            _lblInlineError.Visible = False
            _lblInlineError.Text    = ""
        End Sub

        ' ── Badge button custom paint (circular with person icon) ─────────────

        Private Sub OnBadgeButtonPaint(sender As Object, e As PaintEventArgs)
            Dim g   = e.Graphics
            Dim btn = CType(sender, Button)
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

            Dim sz  = btn.ClientSize
            Dim cx  = sz.Width \ 2
            Dim cy  = sz.Height \ 2
            Dim r   = Math.Min(cx, cy) - 2

            ' Circle background
            Dim fillAlpha = If(_memberLoggedIn, 180, 140)
            Dim fillColor = If(_memberLoggedIn,
                               Color.FromArgb(fillAlpha, 20, 60, 20),
                               Color.FromArgb(fillAlpha, 8, 28, 60))
            Using br = New SolidBrush(fillColor)
                g.FillEllipse(br, cx - r, cy - r, r * 2, r * 2)
            End Using

            ' Circle border
            Dim borderColor = If(_memberLoggedIn, MemberAccent, Color.FromArgb(180, 34, 197, 94))
            If _memberLoggedIn Then borderColor = Color.FromArgb(200, 34, 197, 94)
            Using pen = New Pen(borderColor, 2)
                g.DrawEllipse(pen, cx - r + 1, cy - r + 1, (r - 1) * 2, (r - 1) * 2)
            End Using

            ' Person silhouette — head circle + body arc
            Dim headR  = CInt(r * 0.28)
            Dim headCy = cy - CInt(r * 0.18)
            Using br = New SolidBrush(Color.FromArgb(220, 255, 255, 255))
                g.FillEllipse(br, cx - headR, headCy - headR, headR * 2, headR * 2)
                Dim bodyW = CInt(r * 0.72)
                Dim bodyY = headCy + headR + 2
                Dim bodyH = CInt(r * 0.48)
                Using bp = New Drawing2D.GraphicsPath()
                    bp.AddArc(cx - bodyW, bodyY, bodyW * 2, bodyH * 2, 180, 180)
                    bp.CloseFigure()
                    g.FillPath(br, bp)
                End Using
            End Using

            ' Green status dot when logged in
            If _memberLoggedIn Then
                Dim dotR  = 5
                Dim dotX  = cx + r - dotR - 1
                Dim dotY  = cy + r - dotR - 1
                Using br = New SolidBrush(Color.FromArgb(34, 197, 94))
                    g.FillEllipse(br, dotX, dotY, dotR * 2, dotR * 2)
                End Using
                Using pen = New Pen(Color.FromArgb(8, 12, 24), 1.5F)
                    g.DrawEllipse(pen, dotX, dotY, dotR * 2, dotR * 2)
                End Using
            End If
        End Sub

        ' ── Receiving-coins indicator ──────────────────────────────────────────

        ''' <summary>Builds a rounded-rectangle Region for clipping buttons/panels.</summary>
        Private Shared Function RoundedRegion(w As Integer, h As Integer, radius As Integer) As Region
            Using path = New Drawing2D.GraphicsPath()
                Dim d = radius * 2
                path.AddArc(0, 0, d, d, 180, 90)
                path.AddArc(w - d, 0, d, d, 270, 90)
                path.AddArc(w - d, h - d, d, d, 0, 90)
                path.AddArc(0, h - d, d, d, 90, 90)
                path.CloseFigure()
                Return New Region(path)
            End Using
        End Function

        ''' <summary>
        ''' Centers the (pulsing dot + "Receiving Coins…") title row as a single
        ''' unit within the panel, so the title is always visually aligned to its
        ''' container regardless of text length. The progress line below is a
        ''' full-width centered label and needs no per-text layout.
        ''' </summary>
        Private Sub LayoutReceivingPanel()
            Dim iconW = _lblCoinIcon.PreferredWidth
            Dim iconH = _lblCoinIcon.PreferredHeight
            Dim textW = _lblCoinText.PreferredWidth
            Dim textH = _lblCoinText.PreferredHeight
            Const gap As Integer = 12
            Dim totalW = iconW + gap + textW
            Dim startX = Math.Max(0, (_pnlReceivingCoins.Width - totalW) \ 2)
            Const rowY As Integer = 26
            _lblCoinText.Location = New Point(startX + iconW + gap, rowY)
            ' Vertically center the dot against the taller title text
            _lblCoinIcon.Location = New Point(startX, rowY + (textH - iconH) \ 2 + 1)
        End Sub

        Private Sub OnReceivingCoinsPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Const r As Integer = 22
            Using path = New Drawing2D.GraphicsPath()
                Dim d = r * 2
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()
                ' Soft vertical gradient — warm dark, gold-tinted top
                Using br As New Drawing2D.LinearGradientBrush(
                        New Rectangle(0, 0, pnl.Width, pnl.Height),
                        Color.FromArgb(220, 34, 28, 10),
                        Color.FromArgb(225, 16, 14, 6),
                        Drawing2D.LinearGradientMode.Vertical)
                    g.FillPath(br, path)
                End Using
                ' Gold border that gently tracks the icon pulse for a live feel
                Using pen As New Pen(Color.FromArgb(Math.Min(255, _coinPulseAlpha), 250, 204, 21), 2.0F)
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
            ' Do NOT update _lblCoinIcon.ForeColor here — changing a property on a
            ' Transparent-background label inside a Transparent-background panel
            ' propagates Invalidate all the way up to LockForm, triggering a full
            ' OnPaintBackground (background image + dark overlay) every tick.
            ' The gold border in OnReceivingCoinsPaint already uses _coinPulseAlpha,
            ' so one Invalidate on the panel is enough for the visual effect.
            If _pnlReceivingCoins.Visible Then _pnlReceivingCoins.Invalidate()
        End Sub

        ' ── Idle shutdown countdown ───────────────────────────────────────────

        Private Sub OnIdleShutdownPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g   = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

            Const R As Integer = 8
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)

            ' Rounded rect path
            Using path = New Drawing2D.GraphicsPath()
                Dim d = R * 2
                path.AddArc(rect.X, rect.Y, d, d, 180, 90)
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()

                ' Dark red-tinted background, slightly transparent
                Using br = New SolidBrush(Color.FromArgb(200, 28, 6, 6))
                    g.FillPath(br, path)
                End Using

                ' Subtle pulsing border
                Using pen = New Pen(Color.FromArgb(_idlePulseAlpha, 200, 50, 50), 1.0F)
                    g.DrawPath(pen, path)
                End Using
            End Using
        End Sub

        Private Sub OnIdlePulseTick(sender As Object, e As EventArgs)
            If _idlePulseUp Then
                _idlePulseAlpha += 3
                If _idlePulseAlpha >= 120 Then
                    _idlePulseAlpha = 120 : _idlePulseUp = False
                End If
            Else
                _idlePulseAlpha -= 3
                If _idlePulseAlpha <= 50 Then
                    _idlePulseAlpha = 50 : _idlePulseUp = True
                End If
            End If
            _pnlIdleShutdown.Invalidate()
        End Sub

        ''' <summary>
        ''' Shows or hides the idle-shutdown countdown card.
        ''' Called from UpdateMembershipUI before any early-return guards so it
        ''' works regardless of whether the membership feature is enabled.
        ''' </summary>
        Public Sub UpdateIdleShutdown(seconds As Integer)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateIdleShutdown(seconds))
                Return
            End If

            If seconds <= 0 Then
                If _pnlIdleShutdown.Visible Then
                    _idlePulseTimer.Stop()
                    _pnlIdleShutdown.Visible = False
                    _lastIdleSeconds = -1
                    CenterLabels()
                End If
                Return
            End If

            ' Update countdown text only when value actually changed (avoid needless repaints)
            If seconds <> _lastIdleSeconds Then
                _lastIdleSeconds = seconds
                Dim mins = seconds \ 60
                Dim secs = seconds Mod 60
                _lblIdleCount.Text = $"{mins:D2}:{secs:D2}"

                ' Urgency: escalate colour when under 60 seconds
                If seconds <= 60 Then
                    _lblIdleCount.ForeColor = Color.FromArgb(255, 50, 50)
                    _lblIdleTitle.ForeColor = Color.FromArgb(255, 120, 120)
                Else
                    _lblIdleCount.ForeColor = Color.FromArgb(255, 80, 80)
                    _lblIdleTitle.ForeColor = Color.FromArgb(240, 100, 100)
                End If
            End If

            If Not _pnlIdleShutdown.Visible Then
                _pnlIdleShutdown.Visible = True
                _idlePulseAlpha = 50
                _idlePulseUp    = True
                _idlePulseTimer.Start()
                CenterLabels()
            End If
        End Sub

        Public Sub ShowReceivingCoins(isReceiving As Boolean)
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowReceivingCoins(isReceiving)) : Return

            _pnlReceivingCoins.Visible = isReceiving
            If isReceiving Then
                ' Hide the Insert Coin button — the receiving-coins panel takes over.
                ' Reset its text/enabled state too: without this, a later transition
                ' back to the unlocked → relocked state would leave the button stuck
                ' on "Connecting…" / disabled when UpdateInsertCoinVisibility re-shows it.
                _btnInsertCoin.Visible = False
                _btnInsertCoin.Text = "Insert Coin"
                _btnInsertCoin.Enabled = True
                _isRequestingCoin = False
                _coinPulseAlpha = 255
                _coinPulseUp = False
                _coinPulseTimer.Start()
                ' Fresh slot session — clear the dedup memory so the first heartbeat
                ' payload always paints (otherwise leftover values from a previous
                ' session could suppress the very first update).
                _lastCoinProgressPesos   = -1
                _lastCoinProgressSeconds = -1
                ' Suppress the membership CTA while a coin slot is open — opening
                ' the login modal mid-insertion would steal focus from the running
                ' total / countdown card and confuse the user.  UpdateMembershipUI
                ' honours _pnlReceivingCoins.Visible so subsequent heartbeats won't
                ' re-show the badge until the slot closes.
                If _memberModalOpen Then HideMemberModal()
                If _btnMemberBadge.Visible   Then _btnMemberBadge.Visible   = False
                If _pnlMemberTrigger.Visible Then _pnlMemberTrigger.Visible = False
                LayoutReceivingPanel()
                _lblSub.Text = "Coins are being loaded to this PC…"
                _lblSub.ForeColor = Color.FromArgb(250, 204, 21)
                ' Seed the running-total line with a placeholder until the first coin
                ' drops; UpdateCoinProgress replaces it on the next heartbeat.
                If String.IsNullOrEmpty(_lblCoinProgress.Text) Then
                    _lblCoinProgress.Text = "Waiting for coins…"
                    _lblCoinProgress.ForeColor = Color.FromArgb(170, 176, 190)
                End If
                ' Show Done button (manual early close) + start the countdown bar
                _btnDoneCoins.Visible = True
                _btnDoneCoins.Enabled = True
                _btnDoneCoins.Text = "Done inserting Coins"
                ' Start countdown — resets to full on each new coin (UpdateCoinProgress)
                _coinCountdownSecs = COIN_COUNTDOWN_MAX
                _lblCountdownRemain.Text = $"{_coinCountdownSecs}s · insert more coins"
                _pnlCoinCountdown.Visible = True
                _pnlCoinCountdown.Invalidate()
                _coinCountdownTimer.Start()
            Else
                _coinPulseTimer.Stop()
                ' Stop countdown bar and hide Done button; clear the running-total line
                _coinCountdownTimer.Stop()
                _pnlCoinCountdown.Visible = False
                _btnDoneCoins.Visible = False
                _btnDoneCoins.Enabled = True
                _btnDoneCoins.Text = "Done inserting Coins"
                _lblCoinProgress.Text = ""
                ' Restore Insert Coin button visibility based on current slot state
                UpdateInsertCoinVisibility()
                If Not _memberLoggedIn Then
                    _lblSub.Text = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
                    _lblSub.ForeColor = Color.FromArgb(140, 160, 200)
                End If
                ' Slot closed — restore the membership CTA if the feature is enabled.
                ' UpdateMembershipUI on the next heartbeat will also handle this, but
                ' bringing it back immediately avoids a 1-second gap with no badge.
                If _membershipEnabled Then
                    If Not _btnMemberBadge.Visible Then
                        _btnMemberBadge.Visible   = True
                        _pnlMemberTrigger.Visible = True
                        _membershipShownAt        = DateTime.Now
                        _btnMemberBadge.Invalidate()
                        _pnlMemberTrigger.Invalidate()
                    End If
                End If
            End If
            CenterLabels()
        End Sub

        ''' <summary>Updates the live "₱X inserted · +Hh Mm" line while coins are
        ''' being inserted. Cleared (empty) when pesos &lt;= 0.</summary>
        Public Sub UpdateCoinProgress(pesos As Integer, seconds As Integer)
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            If Me.InvokeRequired Then Me.Invoke(Sub() UpdateCoinProgress(pesos, seconds)) : Return

            ' Dedup: heartbeats fire this every second.  Skip when nothing changed
            ' so we don't rewrite the transparent label (which would force the
            ' parent transparent panel to repaint and produce a visible flicker).
            If pesos = _lastCoinProgressPesos AndAlso seconds = _lastCoinProgressSeconds Then
                Return
            End If

            Dim prevPesos = _lastCoinProgressPesos
            _lastCoinProgressPesos   = pesos
            _lastCoinProgressSeconds = seconds

            If pesos <= 0 Then
                Dim placeholder = "Waiting for coins…"
                Dim placeholderFore = Color.FromArgb(170, 176, 190)
                If _lblCoinProgress.Text <> placeholder Then _lblCoinProgress.Text = placeholder
                If _lblCoinProgress.ForeColor <> placeholderFore Then _lblCoinProgress.ForeColor = placeholderFore
            Else
                Dim newText = $"₱{pesos} inserted  ·  +{FormatHm(seconds)}"
                Dim newFore = Color.FromArgb(236, 238, 244)
                If _lblCoinProgress.Text <> newText Then _lblCoinProgress.Text = newText
                If _lblCoinProgress.ForeColor <> newFore Then _lblCoinProgress.ForeColor = newFore
                ' Reset the countdown ONLY when a fresh coin actually arrived
                ' (pesos increased).  Resetting on every heartbeat made the bar
                ' permanently sit at 100% and flicker its label each second.
                If pesos > prevPesos AndAlso _pnlCoinCountdown.Visible Then
                    _coinCountdownSecs = COIN_COUNTDOWN_MAX
                    _lblCountdownRemain.Text = $"{_coinCountdownSecs}s · insert more coins"
                    _lblCountdownRemain.ForeColor = Color.FromArgb(150, 165, 190)
                    _pnlCoinCountdown.Invalidate()
                End If
            End If
        End Sub

        ''' <summary>Formats a duration as "1h 30m" / "30m" / "0m".</summary>
        Private Shared Function FormatHm(totalSeconds As Integer) As String
            Dim h = totalSeconds \ 3600
            Dim m = (totalSeconds Mod 3600) \ 60
            If h > 0 AndAlso m > 0 Then Return $"{h}h {m}m"
            If h > 0 Then Return $"{h}h"
            If m > 0 Then Return $"{m}m"
            Return "0m"
        End Function

        Public Sub ShowMemberError(message As String)
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowMemberError(message)) : Return
            If Not _memberLoggedIn Then
                ' Show inline in the membership form panel
                _lblInlineError.Text = message
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

        ' ── Coin countdown bar ────────────────────────────────────────────────

        ''' <summary>
        ''' Paints the backward progress bar: full gold at 100%, drains right-to-left,
        ''' shifts from gold → red as time runs low (&lt;30 %).
        ''' </summary>
        Private Sub OnCoinCountdownPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g   = e.Graphics
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias

            Const BarTop    As Integer = 6
            Const BarHeight As Integer = 20
            Const BarRadius As Integer = 10
            Dim barW = pnl.Width

            ' ── Track (background) ──────────────────────────────────────────────
            Dim trackRect = New Rectangle(0, BarTop, barW - 1, BarHeight)
            Using path = New Drawing2D.GraphicsPath()
                Dim d = BarRadius * 2
                path.AddArc(trackRect.X, trackRect.Y, d, d, 180, 90)
                path.AddArc(trackRect.Right - d, trackRect.Y, d, d, 270, 90)
                path.AddArc(trackRect.Right - d, trackRect.Bottom - d, d, d, 0, 90)
                path.AddArc(trackRect.X, trackRect.Bottom - d, d, d, 90, 90)
                path.CloseFigure()
                Using br = New SolidBrush(Color.FromArgb(35, 255, 255, 255))
                    g.FillPath(br, path)
                End Using
            End Using

            ' ── Fill (remaining time — gold fading to red) ───────────────────────
            Dim ratio = Math.Max(0.0F, Math.Min(1.0F, _coinCountdownSecs / CSng(COIN_COUNTDOWN_MAX)))
            If ratio > 0 Then
                Dim fillW = Math.Max(BarRadius * 2, CInt((barW - 1) * ratio))
                Dim fillRect = New Rectangle(0, BarTop, fillW, BarHeight)
                ' Gold (250,204,21) when full, shifts to red (239,68,68) below 30 %
                Dim t     = Math.Max(0.0F, (ratio - 0.3F) / 0.7F)   ' 1→0 over top 70 %
                Dim fillR = CInt(250 * t + 239 * (1.0F - t))
                Dim fillG = CInt(204 * t + 68  * (1.0F - t))
                Dim fillB = CInt(21  * t + 68  * (1.0F - t))
                Using path = New Drawing2D.GraphicsPath()
                    Dim d  = BarRadius * 2
                    Dim fr = fillRect
                    path.AddArc(fr.X, fr.Y, d, d, 180, 90)
                    path.AddArc(fr.Right - d, fr.Y, d, d, 270, 90)
                    path.AddArc(fr.Right - d, fr.Bottom - d, d, d, 0, 90)
                    path.AddArc(fr.X, fr.Bottom - d, d, d, 90, 90)
                    path.CloseFigure()
                    Using br = New SolidBrush(Color.FromArgb(fillR, fillG, fillB))
                        g.FillPath(br, path)
                    End Using
                End Using
            End If
        End Sub

        ''' <summary>
        ''' Fires every second while the coin slot is open. Decrements the countdown;
        ''' at zero auto-triggers "Done inserting Coins" exactly as if the user had
        ''' clicked the button.
        ''' </summary>
        Private Sub OnCoinCountdownTick(sender As Object, e As EventArgs)
            _coinCountdownSecs -= 1

            If _coinCountdownSecs <= 0 Then
                _coinCountdownSecs = 0
                _coinCountdownTimer.Stop()
                _lblCountdownRemain.Text = "Closing…"
                _pnlCoinCountdown.Invalidate()
                ' Disable the Done button so double-fire can't happen
                _btnDoneCoins.Enabled = False
                _btnDoneCoins.Text    = "Closing…"
                RaiseEvent DoneInsertingCoinsRequested()
                Return
            End If

            ' Urgency label: plain past 10 s, "!" prefix under 10 s
            _lblCountdownRemain.Text = If(_coinCountdownSecs <= 10,
                                          $"! {_coinCountdownSecs}s · insert more coins",
                                          $"{_coinCountdownSecs}s · insert more coins")
            _lblCountdownRemain.ForeColor = If(_coinCountdownSecs <= 10,
                                               Color.FromArgb(239, 100, 68),
                                               Color.FromArgb(150, 165, 190))
            _pnlCoinCountdown.Invalidate()
        End Sub

        ' ── Insert Coin button ────────────────────────────────────────────────

        Private Sub UpdateInsertCoinVisibility()
            ' Show only when: slot enabled, not currently requesting/receiving, server reachable
            _btnInsertCoin.Visible = _coinSlotEnabled AndAlso
                                     Not _isRequestingCoin AndAlso
                                     _isConnected AndAlso
                                     Not _pnlReceivingCoins.Visible
            ' Whenever the button becomes visible we are by definition ready for a
            ' fresh request — guarantee a clean "Insert Coin" / enabled state in
            ' case any previous path left it on "Connecting…" / disabled.
            If _btnInsertCoin.Visible Then
                _btnInsertCoin.Text = "Insert Coin"
                _btnInsertCoin.Enabled = True
            End If
            ' Reposition so the button lands centered below the sub-message rather
            ' than at its default (0,0) the first time it becomes visible.
            CenterLabels()
        End Sub

        Public Sub UpdateCoinSlot(enabled As Boolean)
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            If Me.InvokeRequired Then Me.Invoke(Sub() UpdateCoinSlot(enabled)) : Return
            _coinSlotEnabled = enabled
            UpdateInsertCoinVisibility()
            Dim lk = GetLayoutKey()
            If lk <> _lastLayoutKey Then
                _lastLayoutKey = lk
                CenterLabels()
            End If
        End Sub

        Public Sub SetInsertCoinResult(success As Boolean)
            If Me.IsDisposed OrElse Not Me.IsHandleCreated Then Return
            If Me.InvokeRequired Then Me.Invoke(Sub() SetInsertCoinResult(success)) : Return
            If Not success Then
                ' Request failed — restore button so user can try again
                _isRequestingCoin = False
                _btnInsertCoin.Text    = "Insert Coin"
                _btnInsertCoin.Enabled = True
                UpdateInsertCoinVisibility()
            End If
            ' On success the next heartbeat will set receiving_coins=True,
            ' which calls ShowReceivingCoins(True) and hides the button.
        End Sub

        Private Sub OnInsertCoinClick(sender As Object, e As EventArgs)
            _isRequestingCoin      = True
            _btnInsertCoin.Enabled = False
            _btnInsertCoin.Text    = "Connecting…"
            RaiseEvent InsertCoinRequested()
        End Sub

        Private Sub OnDoneCoinsClick(sender As Object, e As EventArgs)
            _btnDoneCoins.Enabled = False
            _btnDoneCoins.Text    = "Closing…"
            RaiseEvent DoneInsertingCoinsRequested()
            ' On success the next heartbeat reports receiving_coins=False,
            ' which calls ShowReceivingCoins(False) and restores the screen.
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
            _idlePulseTimer?.Stop()
            _idlePulseTimer?.Dispose()
        End Sub

    End Class

End Namespace
