Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Runtime.InteropServices
Imports System.Windows.Forms
Imports PisoNetClient.Config
Imports PisoNetClient.Resources

Namespace Forms

    ''' <summary>
    ''' Floating session timer overlay with rounded corners, gradient accent,
    ''' and semi-transparent background.
    ''' • Left-click-drag uses native Win32 caption drag (smooth, zero lag).
    ''' • Right-click shows a context menu to hide or reset position.
    ''' • Connection indicator: small filled circle (green = connected, amber = offline).
    ''' • PC label: "PC 01" shown above or beside the time, configurable.
    ''' • Call ApplyConfig() after changing AppConfig timer settings to re-layout.
    ''' • Add Time CTA row: shows "Add Time" button when coin slot is enabled
    '''   (but not currently receiving), and a receiving-coins mini card while
    '''   the slot is open. Height grows/shrinks dynamically.
    ''' </summary>
    Public Class TimerOverlay
        Inherits Form

        ' ── Colors ───────────────────────────────────────────────────
        Private Shared ReadOnly BgColor      As Color = Color.FromArgb(14, 17, 30)
        Private Shared ReadOnly BgSolid      As Color = Color.FromArgb(14, 17, 30)
        Private Shared ReadOnly BorderColor  As Color = Color.FromArgb(80, 80, 110, 180)
        Private Shared ReadOnly DimColor     As Color = Color.FromArgb(120, 140, 170)
        Private Shared ReadOnly AccentBlue   As Color = Color.FromArgb(14, 165, 233)
        Private Shared ReadOnly AccentPurple As Color = Color.FromArgb(124, 58, 237)
        Private Shared ReadOnly GreenColor   As Color = Color.FromArgb(34, 197, 94)
        Private Shared ReadOnly GoldColor    As Color = Color.FromArgb(250, 204, 21)

        ' ── Dimensions ───────────────────────────────────────────────
        Private Const FORM_W        As Integer = 240
        Private Const CORNER_R      As Integer = 14
        Private Const ACCENT_H      As Integer = 3
        Private Const PAD_X         As Integer = 14
        Private Const PAD_Y         As Integer = 10
        Private Const FORM_H_SLIM   As Integer = 66
        Private Const FORM_H_TALL   As Integer = 88
        Private Const DOT_SIZE      As Integer = 8
        Private Const DOT_MARGIN    As Integer = 10
        Private Const MEMBER_ROW_H  As Integer = 24

        ' Add Time / Receiving-coins row heights
        Private Const ADD_TIME_ROW_H  As Integer = 36
        Private Const COIN_RECV_ROW_H As Integer = 126  ' content is 116px tall + 10px bottom clearance for rounded corners

        ' Countdown max (must match server's PC_IDLE_TIMEOUT = 30 s)
        Private Const OVERLAY_COIN_MAX As Integer = 30

        ' ── Controls ─────────────────────────────────────────────────
        Private _lblTime  As Label
        Private _lblPC    As Label
        Private _pbLogo   As PictureBox
        Private _lblMember As Label
        Private _btnLogout As Button
        ' Tracks whether the member is currently allowed to log out (server-driven).
        ' We do NOT disable the button when False — disabling makes the button
        ' silently swallow clicks, which looks broken.  Instead, OnLogoutClick
        ' surfaces a "Cannot Log Out" dialog explaining the minimum-time rule.
        Private _canLogout             As Boolean = False
        Private _minimumLogoutMinutes  As Integer = 0

        ' Add Time row — idle state
        Private _btnAddTime As Button

        ' Receiving-coins mini card controls
        Private _lblRecvDot       As Label
        Private _lblRecvTitle     As Label
        Private _lblRecvProgress  As Label
        Private _pnlRecvBar       As Panel   ' custom-painted countdown bar
        Private _lblRecvCountdown As Label
        Private _btnDoneCoins     As Button

        ' ── Add Time state ────────────────────────────────────────────
        Private _coinSlotEnabled    As Boolean = False
        Private _isReceivingCoins   As Boolean = False
        Private _isRequestingCoin   As Boolean = False
        Private _addTimeSepY        As Integer = -1   ' separator Y painted in OnPaint

        ' ── Receiving-coins countdown ─────────────────────────────────
        Private _coinCountdownTimer  As System.Windows.Forms.Timer
        Private _coinCountdownSecs   As Integer = OVERLAY_COIN_MAX
        ' Dedup state for UpdateCoinProgress — heartbeats fire every second; without
        ' these guards each tick rewrites the progress label and resets the countdown
        ' bar to full, causing visible flicker and freezing the bar at 100%.
        Private _lastRecvPesos       As Integer = -1
        Private _lastRecvSeconds     As Integer = -1

        ' ── Pulse animation ───────────────────────────────────────────
        Private _pulseTimer      As System.Windows.Forms.Timer
        Private _recvPulseAlpha  As Integer = 255
        Private _recvPulseUp     As Boolean = False

        Private _isConnected    As Boolean = True
        Private _memberName     As String = Nothing
        Private _currentMinutes As Integer = Integer.MaxValue  ' tracks last-known minutes for color decisions
        Private _userMoved      As Boolean = False             ' True once user drags the overlay

        Public Event MemberLogoutRequested()
        Public Event TimerHiddenByUser()
        Public Event InsertCoinRequested()
        Public Event DoneInsertingCoinsRequested()

        ' ── Native drag ──────────────────────────────────────────────
        <DllImport("user32.dll", CharSet:=CharSet.Auto)>
        Private Shared Function ReleaseCapture() As Boolean
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Auto)>
        Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer,
                                            wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        Private Const WM_NCLBUTTONDOWN As Integer = &HA1
        Private Const HTCAPTION        As Integer = 2
        Private Const WS_EX_TOOLWINDOW As Integer = &H80
        Private Const WS_EX_APPWINDOW  As Integer = &H40000

        ' Exclude from Alt+Tab switcher (WS_EX_TOOLWINDOW hides it; removing WS_EX_APPWINDOW
        ' prevents Windows from forcing it back into the list).
        Protected Overrides ReadOnly Property CreateParams As CreateParams
            Get
                Dim cp = MyBase.CreateParams
                cp.ExStyle = (cp.ExStyle Or WS_EX_TOOLWINDOW) And (Not WS_EX_APPWINDOW)
                Return cp
            End Get
        End Property

        Public Sub New()
            InitializeComponent()
            ApplyConfig()
        End Sub

        Private Sub InitializeComponent()
            Me.DoubleBuffered  = True
            Me.FormBorderStyle = FormBorderStyle.None
            Me.ShowInTaskbar   = False
            Me.TopMost         = True
            Me.BackColor       = BgSolid
            Me.StartPosition   = FormStartPosition.Manual
            Me.Cursor          = Cursors.SizeAll
            Me.Opacity         = 0.95

            ' ── Time label ───────────────────────────────────────────
            _lblTime = New Label() With {
                .Font      = New Font("Segoe UI", 18, FontStyle.Bold),
                .ForeColor = GreenColor,
                .BackColor = Color.Transparent,
                .Text      = "--:--",
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' ── PC number label ──────────────────────────────────────
            _lblPC = New Label() With {
                .Text      = $"PC {AppConfig.PCNumber:D2}",
                .Font      = New Font("Segoe UI", 8, FontStyle.Bold),
                .ForeColor = DimColor,
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible   = False
            }

            ' ── Logo (top-left, 18x18) ───────────────────────────────
            _pbLogo = New PictureBox() With {
                .Image    = LogoHelper.GetLogo(18, 18),
                .Size     = New Size(18, 18),
                .SizeMode = PictureBoxSizeMode.Zoom,
                .BackColor = Color.Transparent
            }

            ' ── Member name label ───────────────────────────────────
            _lblMember = New Label() With {
                .Text      = "",
                .Font      = New Font("Segoe UI", 7.5F),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Visible   = False
            }

            ' ── Logout button ───────────────────────────────────────
            _btnLogout = New Button() With {
                .Text      = "Logout",
                .Font      = New Font("Segoe UI", 6.5F, FontStyle.Bold),
                .ForeColor = Color.FromArgb(239, 150, 150),
                .BackColor = Color.FromArgb(40, 239, 68, 68),
                .FlatStyle = FlatStyle.Flat,
                .Size      = New Size(46, 18),
                .Cursor    = Cursors.Hand,
                .Visible   = False
            }
            _btnLogout.FlatAppearance.BorderSize = 1
            _btnLogout.FlatAppearance.BorderColor = Color.FromArgb(80, 239, 68, 68)
            _btnLogout.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 239, 68, 68)
            AddHandler _btnLogout.Click, AddressOf OnLogoutClick

            ' ── Add Time button (shown when slot enabled, not receiving) ──────
            _btnAddTime = New Button() With {
                .Text      = ChrW(&HFF0B) & "  Add Time",
                .Font      = New Font("Segoe UI", 8.5F, FontStyle.Bold),
                .ForeColor = GoldColor,
                .BackColor = Color.Transparent,
                .FlatStyle = FlatStyle.Flat,
                .Cursor    = Cursors.Hand,
                .Visible   = False
            }
            _btnAddTime.FlatAppearance.BorderSize  = 1
            _btnAddTime.FlatAppearance.BorderColor = GoldColor
            _btnAddTime.FlatAppearance.MouseOverBackColor = Color.FromArgb(40, 34, 12)
            AddHandler _btnAddTime.Click, AddressOf OnAddTimeClick

            ' ── Receiving-coins card: dot + title ────────────────────
            _lblRecvDot = New Label() With {
                .Text      = ChrW(&H25CF),   ' "●"
                .Font      = New Font("Segoe UI", 10.0F, FontStyle.Bold),
                .ForeColor = GoldColor,
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Visible   = False
            }

            _lblRecvTitle = New Label() With {
                .Text      = "Receiving Coins" & ChrW(&H2026),
                .Font      = New Font("Segoe UI", 10.0F, FontStyle.Bold),
                .ForeColor = GoldColor,
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleLeft,
                .Visible   = False
            }

            ' ── Progress text ("₱10 inserted · +1h 30m") ─────────────
            _lblRecvProgress = New Label() With {
                .Text      = "Waiting for coins" & ChrW(&H2026),
                .Font      = New Font("Segoe UI", 11.0F, FontStyle.Bold),
                .ForeColor = Color.FromArgb(220, 228, 240),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible   = False
            }

            ' ── Countdown progress bar panel ──────────────────────────
            _pnlRecvBar = New Panel() With {
                .BackColor = Color.Transparent,
                .Visible   = False
            }
            AddHandler _pnlRecvBar.Paint, AddressOf OnRecvBarPaint

            ' ── Countdown seconds label ───────────────────────────────
            _lblRecvCountdown = New Label() With {
                .Text      = $"{OVERLAY_COIN_MAX}s",
                .Font      = New Font("Segoe UI", 8.0F),
                .ForeColor = Color.FromArgb(100, 120, 150),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleRight,
                .Visible   = False
            }

            ' ── Done inserting Coins button ───────────────────────────
            _btnDoneCoins = New Button() With {
                .Text      = "Done inserting Coins",
                .Font      = New Font("Segoe UI", 9.0F, FontStyle.Bold),
                .ForeColor = GoldColor,
                .BackColor = Color.Transparent,
                .FlatStyle = FlatStyle.Flat,
                .Cursor    = Cursors.Hand,
                .Visible   = False
            }
            _btnDoneCoins.FlatAppearance.BorderSize  = 1
            _btnDoneCoins.FlatAppearance.BorderColor = Color.FromArgb(180, 250, 204, 21)
            _btnDoneCoins.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 250, 204, 21)
            AddHandler _btnDoneCoins.Click, AddressOf OnDoneCoinsClick

            ' ── Timers ────────────────────────────────────────────────
            _coinCountdownTimer = New System.Windows.Forms.Timer() With {.Interval = 1000}
            AddHandler _coinCountdownTimer.Tick, AddressOf OnCoinCountdownTick

            _pulseTimer = New System.Windows.Forms.Timer() With {.Interval = 60}
            AddHandler _pulseTimer.Tick, AddressOf OnPulseTick

            Me.Controls.AddRange({
                _lblTime, _lblPC, _lblMember, _btnLogout, _pbLogo,
                _btnAddTime,
                _lblRecvDot, _lblRecvTitle, _lblRecvProgress,
                _pnlRecvBar, _lblRecvCountdown, _btnDoneCoins
            })

            ' Left-click drag on every visible surface
            Dim drag = New MouseEventHandler(AddressOf HandleMouseDown)
            AddHandler Me.MouseDown,              drag
            AddHandler _lblTime.MouseDown,        drag
            AddHandler _lblPC.MouseDown,          drag
            AddHandler _lblMember.MouseDown,      drag
            AddHandler _pbLogo.MouseDown,         drag
            ' _btnAddTime intentionally excluded — drag on a button steals its Click event
            AddHandler _lblRecvDot.MouseDown,     drag
            AddHandler _lblRecvTitle.MouseDown,   drag
            AddHandler _lblRecvProgress.MouseDown, drag
            AddHandler _pnlRecvBar.MouseDown,     drag
            AddHandler _lblRecvCountdown.MouseDown, drag
        End Sub

        ' ── Custom paint: rounded rect + gradient accent + border + dot ──
        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g  = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim sc = GetLayoutScale()
            Dim Sv = Function(n As Integer) CInt(Math.Round(n * sc))

            Dim cornerR   = Sv(CORNER_R)
            Dim accentH   = Math.Max(2, Sv(ACCENT_H))
            Dim padX      = Sv(PAD_X)
            Dim dotSize   = Sv(DOT_SIZE)
            Dim dotMargin = Sv(DOT_MARGIN)
            Dim memberRowH = Sv(MEMBER_ROW_H)

            Dim rect = New Rectangle(0, 0, Me.Width - 1, Me.Height - 1)
            g.Clear(BgSolid)

            Using path = RoundedRect(rect, cornerR)
                Using pen = New Pen(BorderColor, 1)
                    g.DrawPath(pen, path)
                End Using
            End Using

            Dim accentRect = New Rectangle(cornerR, 0, Me.Width - cornerR * 2, accentH)
            Using br = New LinearGradientBrush(accentRect, AccentBlue, AccentPurple, 0F)
                g.FillRectangle(br, accentRect)
            End Using

            If _lblMember.Visible Then
                Dim sepY = Me.Height - memberRowH - 4
                ' If the Add Time row is also visible, member separator is above it
                If _addTimeSepY > 0 Then
                    sepY = _addTimeSepY - memberRowH - 4
                End If
                Using pen = New Pen(Color.FromArgb(40, 100, 120, 180), 1)
                    g.DrawLine(pen, padX, sepY, Me.Width - padX, sepY)
                End Using
            End If

            If _addTimeSepY > 0 Then
                Using pen = New Pen(Color.FromArgb(40, 100, 120, 180), 1)
                    g.DrawLine(pen, padX, _addTimeSepY, Me.Width - padX, _addTimeSepY)
                End Using
            End If

            If AppConfig.TimerShowConnDot Then
                Dim dotX   = Me.Width - dotSize - dotMargin
                Dim dotY   = dotMargin
                Dim dotClr = If(_isConnected, GreenColor, Color.FromArgb(245, 158, 11))
                Using br = New SolidBrush(dotClr)
                    g.FillEllipse(br, dotX, dotY, dotSize, dotSize)
                End Using
                Using glowBr = New SolidBrush(Color.FromArgb(40, dotClr))
                    g.FillEllipse(glowBr, dotX - 2, dotY - 2, dotSize + 4, dotSize + 4)
                End Using
            End If

            MyBase.OnPaint(e)
        End Sub

        ' ── Countdown bar paint (backward: 100% → 0%, gold → red below 30%) ──
        Private Sub OnRecvBarPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g   = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias

            Const BarRadius As Integer = 5
            Dim barW = pnl.Width - 1
            Dim barH = pnl.Height - 1

            ' Track background
            Dim trackRect = New Rectangle(0, 0, barW, barH)
            Using path = New GraphicsPath()
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

            ' Fill portion (remaining time)
            Dim ratio = Math.Max(0.0F, Math.Min(1.0F, _coinCountdownSecs / CSng(OVERLAY_COIN_MAX)))
            If ratio > 0 Then
                Dim fillW = Math.Max(BarRadius * 2, CInt(barW * ratio))
                Dim fillRect = New Rectangle(0, 0, fillW, barH)
                ' Gold (250,204,21) when full → red (239,68,68) below 30%
                Dim t     = Math.Max(0.0F, (ratio - 0.3F) / 0.7F)
                Dim fillR = CInt(250 * t + 239 * (1.0F - t))
                Dim fillG = CInt(204 * t + 68  * (1.0F - t))
                Dim fillB = CInt(21  * t + 68  * (1.0F - t))
                Using path = New GraphicsPath()
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

        ' ── Rounded rectangle helper ─────────────────────────────────
        Private Shared Function RoundedRect(rect As Rectangle, r As Integer) As GraphicsPath
            Dim path = New GraphicsPath()
            Dim d = r * 2
            path.AddArc(rect.X, rect.Y, d, d, 180, 90)
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
            path.CloseFigure()
            Return path
        End Function

        Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
            ' Suppress default background — OnPaint handles everything
        End Sub

        ' ── Layout engine ────────────────────────────────────────────

        ''' <summary>Returns the screen the overlay currently lives on (or primary if not yet shown).</summary>
        Private Function GetCurrentScreen() As Screen
            If Me.IsHandleCreated Then Return Screen.FromHandle(Me.Handle)
            Return Screen.PrimaryScreen
        End Function

        ''' <summary>
        ''' Compute a combined DPI + screen-size scale factor so the overlay
        ''' stays proportional on small or high-DPI monitors.
        ''' </summary>
        Private Function GetLayoutScale() As Single
            Dim dpiScale As Single = 1.0F
            Using g = Me.CreateGraphics()
                dpiScale = g.DpiX / 96.0F
            End Using
            Dim wa = GetCurrentScreen().WorkingArea
            ' Shrink proportionally on small screens (reference = 1920 wide).
            Dim widthScale  = CSng(wa.Width  / 1920.0F)
            Dim heightScale = CSng(wa.Height / 1080.0F)
            Dim screenScale = Math.Min(widthScale, heightScale)
            screenScale = Math.Max(0.55F, Math.Min(1.10F, screenScale))
            ' Combine DPI + screen scale, then clamp to a sane range.
            Return Math.Max(0.65F, Math.Min(1.40F, dpiScale * screenScale))
        End Function

        ''' <summary>
        ''' Returns the Add Time row height to append: 0 if not shown,
        ''' COIN_RECV_ROW_H if receiving, ADD_TIME_ROW_H if slot enabled but idle.
        ''' </summary>
        Private Function GetAddTimeRowH(Sv As Func(Of Integer, Integer)) As Integer
            If _isReceivingCoins Then
                Return Sv(COIN_RECV_ROW_H)
            End If
            If _coinSlotEnabled Then
                Return Sv(ADD_TIME_ROW_H)
            End If
            Return 0
        End Function

        ''' <summary>
        ''' Positions all Add Time / receiving-coins card controls at the given base Y.
        ''' </summary>
        Private Sub LayoutAddTimeRow(baseH As Integer, formW As Integer, padX As Integer,
                                     Sv As Func(Of Integer, Integer))
            If _isReceivingCoins Then
                ' Hide the idle button
                _btnAddTime.Visible = False

                ' Row 1 — dot + title (y = baseH + 6), height 22px
                Dim y1 = baseH + Sv(6)
                _lblRecvDot.Location   = New Point(padX, y1)
                _lblRecvTitle.Location = New Point(padX + _lblRecvDot.Width + Sv(4), y1)
                _lblRecvTitle.Size     = New Size(formW - padX - _lblRecvDot.Width - Sv(4) - padX, Sv(22))
                _lblRecvDot.Visible    = True
                _lblRecvTitle.Visible  = True

                ' Row 2 — "₱X inserted · +Xh Xm" (y + 26), height 24px — biggest text
                Dim y2 = y1 + Sv(26)
                _lblRecvProgress.Location = New Point(padX, y2)
                _lblRecvProgress.Size     = New Size(formW - padX * 2, Sv(24))
                _lblRecvProgress.Visible  = True

                ' Row 3 — countdown bar (y + 54), height 10px
                Dim y3 = y1 + Sv(54)
                Dim barW = formW - padX * 2
                _pnlRecvBar.Location = New Point(padX, y3)
                _pnlRecvBar.Size     = New Size(barW, Sv(10))
                _pnlRecvBar.Visible  = True
                _pnlRecvBar.Invalidate()

                ' Row 4 — countdown seconds label (y + 68), height 16px, right-aligned
                Dim y4 = y1 + Sv(68)
                _lblRecvCountdown.Location = New Point(padX, y4)
                _lblRecvCountdown.Size     = New Size(barW, Sv(16))
                _lblRecvCountdown.Visible  = True

                ' Row 5 — Done button (y + 88), height 28px
                Dim y5 = y1 + Sv(88)
                _btnDoneCoins.Location = New Point(padX, y5)
                _btnDoneCoins.Size     = New Size(formW - padX * 2, Sv(28))
                _btnDoneCoins.Visible  = True

            Else
                ' Hide receiving card
                _lblRecvDot.Visible       = False
                _lblRecvTitle.Visible     = False
                _lblRecvProgress.Visible  = False
                _pnlRecvBar.Visible       = False
                _lblRecvCountdown.Visible = False
                _btnDoneCoins.Visible     = False

                ' Show Add Time button centered vertically in the row
                Dim btnH = Sv(28)
                Dim rowPadY = (Sv(ADD_TIME_ROW_H) - btnH) \ 2
                _btnAddTime.Location = New Point(padX, baseH + rowPadY)
                _btnAddTime.Size     = New Size(formW - padX * 2, btnH)
                _btnAddTime.Font     = New Font("Segoe UI", Math.Max(7.0F, 8.5F * GetLayoutScale()), FontStyle.Bold)
                _btnAddTime.Visible  = True
            End If
        End Sub

        ''' <summary>
        ''' Re-layouts the overlay according to current AppConfig timer settings.
        ''' Safe to call from any thread.
        ''' </summary>
        Public Sub ApplyConfig()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ApplyConfig())
                Return
            End If

            Dim sc As Single = GetLayoutScale()
            Dim Sv = Function(n As Integer) CInt(Math.Round(n * sc))

            ' Scaled dimension locals
            Dim formW     = Sv(FORM_W)
            Dim formHSlim = Sv(FORM_H_SLIM)
            Dim formHTall = Sv(FORM_H_TALL)
            Dim cornerR   = Sv(CORNER_R)
            Dim accentH   = Math.Max(2, Sv(ACCENT_H))
            Dim padX      = Sv(PAD_X)
            Dim padY      = Sv(PAD_Y)
            Dim dotSize   = Sv(DOT_SIZE)
            Dim dotMargin = Sv(DOT_MARGIN)
            Dim memberRowH = Sv(MEMBER_ROW_H)
            Dim logoSize  = Sv(18)

            ' Clamp formW to working area (leave a margin so it is never clipped)
            Dim wa = GetCurrentScreen().WorkingArea
            formW = Math.Min(formW, wa.Width - Sv(24))

            ' Scale fonts
            _lblTime.Font   = New Font("Segoe UI", Math.Max(9.0F,  18.0F * sc), FontStyle.Bold)
            _lblPC.Font     = New Font("Segoe UI", Math.Max(6.5F,   8.0F * sc), FontStyle.Bold)
            _lblMember.Font = New Font("Segoe UI", Math.Max(6.0F,   7.5F * sc))
            _btnLogout.Font = New Font("Segoe UI", Math.Max(5.5F,   6.5F * sc), FontStyle.Bold)
            _btnLogout.Size = New Size(Sv(46), Sv(18))

            ' Update logo size
            If _pbLogo.Image IsNot Nothing OrElse logoSize <> _pbLogo.Width Then
                _pbLogo.Size  = New Size(logoSize, logoSize)
                _pbLogo.Image = LogoHelper.GetLogo(logoSize, logoSize)
            End If

            Dim showPc  = AppConfig.TimerShowPcLabel
            Dim pcAbove = (AppConfig.TimerPcLabelPosition = "Above")

            _lblPC.Visible = showPc
            _lblPC.Text    = $"PC {AppConfig.PCNumber:D2}"

            ' Logo offset
            Dim hasLogo  = (_pbLogo.Image IsNot Nothing)
            Dim contentX = If(hasLogo, padX + _pbLogo.Width + Sv(6), padX)
            Dim dotReserved = If(AppConfig.TimerShowConnDot, dotSize + dotMargin + Sv(4), 0)
            Dim contentW = Math.Max(Sv(80), formW - contentX - padX - dotReserved)

            _pbLogo.Location = New Point(padX, padY + accentH)

            If showPc AndAlso pcAbove Then
                ' ── PC label above time ──────────────────────────────
                Me.Size = New Size(formW, formHTall)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, formW, formHTall), cornerR))

                Dim pcH = Sv(16)
                _lblPC.Location  = New Point(contentX, padY + accentH)
                _lblPC.Size      = New Size(contentW, pcH)
                _lblPC.TextAlign = ContentAlignment.MiddleCenter

                _lblTime.Location = New Point(contentX, padY + accentH + pcH + Sv(1))
                _lblTime.Size     = New Size(contentW, formHTall - padY - accentH - pcH - Sv(1) - padY)

            ElseIf showPc Then
                ' ── PC label side ────────────────────────────────────
                Me.Size = New Size(formW, formHSlim)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, formW, formHSlim), cornerR))

                Dim pcW   = Sv(56)
                Dim timeW = Math.Max(Sv(80), contentW - pcW - Sv(4))
                _lblTime.Location  = New Point(contentX, padY + accentH)
                _lblTime.Size      = New Size(timeW, formHSlim - padY * 2 - accentH)
                _lblTime.TextAlign = ContentAlignment.MiddleLeft

                _lblPC.Location  = New Point(contentX + timeW + Sv(4), (formHSlim - Sv(20)) \ 2)
                _lblPC.Size      = New Size(pcW, Sv(20))
                _lblPC.TextAlign = ContentAlignment.MiddleLeft

            Else
                ' ── No PC label ──────────────────────────────────────
                Me.Size = New Size(formW, formHSlim)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, formW, formHSlim), cornerR))

                _lblTime.Location  = New Point(contentX, padY + accentH)
                _lblTime.Size      = New Size(contentW, formHSlim - padY * 2 - accentH)
                _lblTime.TextAlign = ContentAlignment.MiddleCenter
            End If

            _lblTime.ForeColor = If(
                _currentMinutes < 5,
                Color.FromArgb(AppConfig.TimerLowTimeArgb),
                Color.FromArgb(AppConfig.TimerTimeArgb))

            ' Snapshot the timer-only height before any row expansion.
            ' Used below to compute Add Time Y explicitly, so the button never
            ' lands on top of the member row regardless of call order.
            Dim baseFormH = Me.Height

            ' Expand height for member row if visible
            If _lblMember.Visible Then
                Dim newH = baseFormH + memberRowH + Sv(10)
                Me.Size   = New Size(formW, newH)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, formW, newH), cornerR))
                LayoutMemberControls(baseFormH, memberRowH, padX, Sv(46))
            End If

            ' Add Time / Receiving-coins row.
            ' addTimeBaseY is computed from baseFormH + member expansion (if visible),
            ' NOT from Me.Height, so it is immune to any accumulated-height drift.
            _addTimeSepY = -1
            Dim addRowH    = GetAddTimeRowH(Sv)
            Dim addTimeBaseY = baseFormH + If(_lblMember.Visible, memberRowH + Sv(10), 0)
            If addRowH > 0 Then
                _addTimeSepY = addTimeBaseY
                Dim totalH = addTimeBaseY + addRowH
                Me.Size   = New Size(formW, totalH)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, formW, totalH), cornerR))
                LayoutAddTimeRow(addTimeBaseY, formW, padX, Sv)
            Else
                ' Hide all Add Time controls when row is not shown
                _btnAddTime.Visible       = False
                _lblRecvDot.Visible       = False
                _lblRecvTitle.Visible     = False
                _lblRecvProgress.Visible  = False
                _pnlRecvBar.Visible       = False
                _lblRecvCountdown.Visible = False
                _btnDoneCoins.Visible     = False
            End If

            Me.Invalidate()
            If Not _userMoved Then PositionToCorner()
        End Sub

        Private Sub PositionToCorner()
            Dim wa     = GetCurrentScreen().WorkingArea
            Dim margin = CInt(Math.Round(12 * GetLayoutScale()))
            Dim x      = wa.Right  - Me.Width  - margin
            Dim y      = wa.Top    + margin
            ' Clamp fully within the working area on all four sides
            x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right  - Me.Width  - 4))
            y = Math.Max(wa.Top  + 4, Math.Min(y, wa.Bottom - Me.Height - 4))
            Me.Location = New Point(x, y)
        End Sub

        ' ── Public API ────────────────────────────────────────────────

        Public Sub UpdateTime(minutes As Integer, seconds As Integer)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateTime(minutes, seconds))
                Return
            End If
            _currentMinutes = minutes
            If minutes >= 60 Then
                Dim hrs  = minutes \ 60
                Dim mins = minutes Mod 60
                _lblTime.Text = If(mins = 0, $"{hrs}h", $"{hrs}h {mins}m")
            Else
                _lblTime.Text = $"{minutes:D2}:{seconds:D2}"
            End If
            _lblTime.ForeColor = If(
                minutes < 5,
                Color.FromArgb(AppConfig.TimerLowTimeArgb),
                Color.FromArgb(AppConfig.TimerTimeArgb))
        End Sub

        Public Sub ShowConnected()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowConnected())
                Return
            End If
            _isConnected = True
            Me.Invalidate()
        End Sub

        Public Sub ShowOffline()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowOffline())
                Return
            End If
            _isConnected = False
            Me.Invalidate()
        End Sub

        Public Sub SetMemberInfo(username As String, canLogout As Boolean,
                                  Optional minimumLogoutMinutes As Integer = 0)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() SetMemberInfo(username, canLogout, minimumLogoutMinutes))
                Return
            End If
            ' Track whether the member row is actually appearing or disappearing.
            ' SetMemberInfo is called on every heartbeat (MembershipUpdated has no
            ' dedup), so we must not call ApplyConfig() unconditionally — it resizes
            ' Me.Size and Me.Region every second, which is the main source of flicker.
            Dim wasVisible = _lblMember.Visible
            Dim nowVisible = Not String.IsNullOrEmpty(username)
            _memberName            = username
            _canLogout             = canLogout
            _minimumLogoutMinutes  = minimumLogoutMinutes
            If nowVisible Then
                If _lblMember.Text <> $"  {username}" Then _lblMember.Text = $"  {username}"
                _lblMember.Visible = True
                _btnLogout.Visible = True
                ' The button stays Enabled regardless of canLogout — disabling it
                ' silently eats clicks, which the user perceives as "broken".
                ' OnLogoutClick handles the soft-disabled state explicitly by
                ' surfacing a dialog explaining the minimum-time rule.
                _btnLogout.Enabled = True
                ' Visual cue for the soft-disabled state: dim the button when
                ' logout isn't permitted yet so it looks intentionally gated.
                If canLogout Then
                    _btnLogout.ForeColor = Color.FromArgb(239, 150, 150)
                    _btnLogout.BackColor = Color.FromArgb(40, 239, 68, 68)
                    _btnLogout.FlatAppearance.BorderColor = Color.FromArgb(80, 239, 68, 68)
                Else
                    _btnLogout.ForeColor = Color.FromArgb(140, 150, 160)
                    _btnLogout.BackColor = Color.FromArgb(20, 100, 110, 130)
                    _btnLogout.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 110, 130)
                End If
            Else
                _lblMember.Visible = False
                _btnLogout.Visible = False
            End If
            ' Only do full re-layout when the row appears or disappears.
            ' Changing text / enabled state alone does not need a form resize.
            If wasVisible <> nowVisible Then ApplyConfig()
        End Sub

        ''' <summary>
        ''' Called when the heartbeat reports a coin_slot_enabled change.
        ''' Shows or hides the "Add Time" button accordingly.
        ''' Safe to call from any thread.
        ''' </summary>
        Public Sub ShowAddTimeButton(enabled As Boolean)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowAddTimeButton(enabled))
                Return
            End If
            _coinSlotEnabled = enabled
            ' If slot disabled, reset request state so the button is ready on re-enable
            If Not enabled Then
                _isRequestingCoin = False
                _btnAddTime.Text      = ChrW(&HFF0B) & "  Add Time"
                _btnAddTime.ForeColor = GoldColor
                _btnAddTime.Enabled   = True
            End If
            UpdateAddTimeState()
            ApplyConfig()
        End Sub

        ''' <summary>
        ''' Transitions the overlay between the idle Add Time button state and
        ''' the receiving-coins mini card state.
        ''' Safe to call from any thread.
        ''' </summary>
        Public Sub SetReceivingCoins(isReceiving As Boolean)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() SetReceivingCoins(isReceiving))
                Return
            End If

            _isReceivingCoins = isReceiving

            If isReceiving Then
                ' Reset requesting flag — slot is now open
                _isRequestingCoin = False
                ' Reset Done button — it may have been left as "Closing…"/disabled
                ' from the previous insertion cycle.  LayoutAddTimeRow sets Visible,
                ' but not Text or Enabled, so a stale state carries over to the next open.
                _btnDoneCoins.Text    = "Done inserting Coins"
                _btnDoneCoins.Enabled = True
                ' Seed progress text
                _lblRecvProgress.Text     = "Waiting for coins" & ChrW(&H2026)
                _lblRecvProgress.ForeColor = Color.FromArgb(140, 160, 200)
                ' Fresh slot session — clear dedup memory so the first heartbeat
                ' always paints (otherwise a leftover value from the previous
                ' session could suppress the very first update).
                _lastRecvPesos   = -1
                _lastRecvSeconds = -1
                ' Reset and start countdown
                _coinCountdownSecs = OVERLAY_COIN_MAX
                UpdateCountdownLabel()
                _coinCountdownTimer.Stop()
                _coinCountdownTimer.Start()
                ' Start pulse
                _recvPulseAlpha = 255
                _recvPulseUp    = False
                _pulseTimer.Start()
            Else
                ' Stop timers
                _coinCountdownTimer.Stop()
                _pulseTimer.Stop()
                ' Restore Add Time button state
                _btnAddTime.Text      = ChrW(&HFF0B) & "  Add Time"
                _btnAddTime.ForeColor = GoldColor
                _btnAddTime.Enabled   = True
                _isRequestingCoin     = False
            End If

            ApplyConfig()
        End Sub

        ''' <summary>
        ''' Updates the in-progress coin insertion display.
        ''' Resets the countdown to 30 s when pesos > 0 (a coin just landed).
        ''' Safe to call from any thread.
        ''' </summary>
        Public Sub UpdateCoinProgress(pesos As Integer, seconds As Integer)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateCoinProgress(pesos, seconds))
                Return
            End If

            If Not _isReceivingCoins Then Return

            ' Dedup — skip if nothing changed since the previous heartbeat.  Without
            ' this, every 1 s tick rewrites the label and resets the countdown bar,
            ' producing visible flicker and freezing the bar permanently at 100 %.
            If pesos = _lastRecvPesos AndAlso seconds = _lastRecvSeconds Then Return

            Dim prevPesos = _lastRecvPesos
            _lastRecvPesos   = pesos
            _lastRecvSeconds = seconds

            If pesos > 0 Then
                Dim hrs  = seconds \ 3600
                Dim mins = (seconds Mod 3600) \ 60
                Dim timeStr = If(hrs > 0, $"+{hrs}h {mins}m", $"+{mins}m")
                Dim newText = $"₱{pesos} inserted · {timeStr}"
                If _lblRecvProgress.Text <> newText Then _lblRecvProgress.Text = newText
                ' Only reset the countdown when a fresh coin actually arrived
                ' (pesos increased) — not on every heartbeat.
                If pesos > prevPesos Then
                    _coinCountdownSecs = OVERLAY_COIN_MAX
                    UpdateCountdownLabel()
                    _pnlRecvBar.Invalidate()
                End If
            Else
                Dim waiting = "Waiting for coins" & ChrW(&H2026)
                If _lblRecvProgress.Text <> waiting Then _lblRecvProgress.Text = waiting
            End If
        End Sub

        ''' <summary>
        ''' Called when a request-coins API call fails. Restores the Add Time button.
        ''' Safe to call from any thread.
        ''' </summary>
        Public Sub SetInsertCoinResult(success As Boolean)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() SetInsertCoinResult(success))
                Return
            End If

            If Not success Then
                _isRequestingCoin     = False
                _btnAddTime.Text      = ChrW(&HFF0B) & "  Add Time"
                _btnAddTime.ForeColor = GoldColor
                _btnAddTime.Enabled   = True
            End If
        End Sub

        ' ── Internal helpers ──────────────────────────────────────────

        ''' <summary>
        ''' Keeps Add Time visibility flags consistent without triggering a full layout.
        ''' Called before ApplyConfig() whenever _coinSlotEnabled changes.
        ''' </summary>
        Private Sub UpdateAddTimeState()
            ' Nothing extra needed — ApplyConfig drives visibility via GetAddTimeRowH
        End Sub

        Private Sub UpdateCountdownLabel()
            _lblRecvCountdown.Text = $"{_coinCountdownSecs}s"
            _lblRecvCountdown.ForeColor = If(
                _coinCountdownSecs <= 10,
                Color.FromArgb(239, 100, 68),
                Color.FromArgb(100, 120, 150))
        End Sub

        ' ── Timer handlers ────────────────────────────────────────────

        Private Sub OnCoinCountdownTick(sender As Object, e As EventArgs)
            _coinCountdownSecs -= 1

            If _coinCountdownSecs <= 0 Then
                _coinCountdownSecs = 0
                _coinCountdownTimer.Stop()
                _lblRecvCountdown.Text      = "0s"
                _lblRecvCountdown.ForeColor = Color.FromArgb(239, 100, 68)
                _pnlRecvBar.Invalidate()
                _btnDoneCoins.Text    = "Closing" & ChrW(&H2026)
                _btnDoneCoins.Enabled = False
                RaiseEvent DoneInsertingCoinsRequested()
                Return
            End If

            UpdateCountdownLabel()
            _pnlRecvBar.Invalidate()
        End Sub

        Private Sub OnPulseTick(sender As Object, e As EventArgs)
            If _recvPulseUp Then
                _recvPulseAlpha += 12
                If _recvPulseAlpha >= 255 Then
                    _recvPulseAlpha = 255
                    _recvPulseUp    = False
                End If
            Else
                _recvPulseAlpha -= 12
                If _recvPulseAlpha <= 80 Then
                    _recvPulseAlpha = 80
                    _recvPulseUp    = True
                End If
            End If
            _lblRecvDot.ForeColor = Color.FromArgb(_recvPulseAlpha, 250, 204, 21)
        End Sub

        ' ── Button click handlers ─────────────────────────────────────

        Private Sub OnAddTimeClick(sender As Object, e As EventArgs)
            If _isRequestingCoin Then Return
            _isRequestingCoin     = True
            _btnAddTime.Enabled   = False
            _btnAddTime.Text      = "Connecting" & ChrW(&H2026)
            _btnAddTime.ForeColor = Color.FromArgb(100, 120, 150)
            RaiseEvent InsertCoinRequested()
        End Sub

        Private Sub OnDoneCoinsClick(sender As Object, e As EventArgs)
            _btnDoneCoins.Enabled = False
            _btnDoneCoins.Text    = "Closing" & ChrW(&H2026)
            RaiseEvent DoneInsertingCoinsRequested()
        End Sub

        Private Sub LayoutMemberControls(baseH As Integer, memberRowH As Integer, padX As Integer, btnW As Integer)
            If Not _lblMember.Visible Then Return
            Dim y = baseH + 4
            _lblMember.Location = New Point(padX, y)
            _lblMember.Size     = New Size(Math.Max(40, Me.Width - padX - btnW - padX - 4), memberRowH)
            _btnLogout.Location = New Point(Me.Width - btnW - padX - 4, y + (memberRowH - _btnLogout.Height) \ 2)
        End Sub

        ' ── Logout confirmation ──────────────────────────────────────
        Private Sub OnLogoutClick(sender As Object, e As EventArgs)
            ' Soft-disabled state: server says the member hasn't met the
            ' minimum-time threshold yet.  Show an explanation dialog instead
            ' of silently swallowing the click (which makes the button look
            ' broken to the user).
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

        Private Function ConfirmLogout() As Boolean
            Dim dlg = New Form() With {
                .Size          = New Size(440, 226),
                .TopMost       = True,
                .StartPosition = FormStartPosition.CenterScreen
            }

            ' Icon circle
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

            Const BtnW As Integer = 160, BtnH As Integer = 38, BtnGap As Integer = 16
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

        ' ── Mouse handling ────────────────────────────────────────────

        Private Sub HandleMouseDown(sender As Object, e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then
                _userMoved = True
                ReleaseCapture()
                SendMessage(Me.Handle, WM_NCLBUTTONDOWN, New IntPtr(HTCAPTION), IntPtr.Zero)

            ElseIf e.Button = MouseButtons.Right Then
                ShowContextMenu()
            End If
        End Sub

        Private Sub ShowContextMenu()
            Dim menu = New ContextMenuStrip() With {
                .BackColor = Color.FromArgb(22, 26, 42),
                .ForeColor = Color.White,
                .Font      = New Font("Segoe UI", 9)
            }

            Dim itemHide = New ToolStripMenuItem("Hide Timer") With {
                .ForeColor = Color.FromArgb(220, 228, 240)
            }
            AddHandler itemHide.Click, Sub(s, ev)
                                                   Me.Hide()
                                                   RaiseEvent TimerHiddenByUser()
                                               End Sub

            Dim itemReset = New ToolStripMenuItem("Reset Position") With {
                .ForeColor = Color.FromArgb(220, 228, 240)
            }
            AddHandler itemReset.Click, Sub(s, ev)
                                           _userMoved = False
                                           PositionToCorner()
                                       End Sub

            menu.Items.Add(itemHide)
            menu.Items.Add(New ToolStripSeparator())
            menu.Items.Add(itemReset)

            menu.Show(Me, Me.PointToClient(Control.MousePosition))
        End Sub

    End Class

End Namespace
