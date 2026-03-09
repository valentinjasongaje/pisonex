Imports System.Drawing
Imports System.Windows.Forms

Namespace Forms

    Public Enum ToastType
        Info    ' Blue
        Success ' Green
        Warning ' Orange
    End Enum

    ''' <summary>
    ''' Custom floating notification that slides in from the right edge of the screen.
    ''' Must be created and shown on the UI (STA) thread.
    ''' The caller is responsible for vertical stacking via the yOffset parameter.
    ''' </summary>
    Public Class NotificationToast
        Inherits Form

        Private _progress    As Panel
        Private _slideTimer  As System.Windows.Forms.Timer
        Private _lifeTimer   As System.Windows.Forms.Timer
        Private _ticks       As Integer = 0

        Private Const TOTAL_TICKS As Integer = 40   ' 40 × 100 ms = 4 seconds
        Private Const FORM_W      As Integer = 320
        Private Const FORM_H      As Integer = 84
        Private Const BAR_W       As Integer = 5    ' left accent strip
        Private Const ICON_SIZE   As Integer = 30   ' icon circle diameter
        Private Const ICON_X      As Integer = 14   ' left edge of icon (9 px gap after bar)
        Private Const TEXT_X      As Integer = 54   ' left edge of title / message text
        Private Const PROGRESS_W  As Integer = FORM_W - BAR_W   ' progress bar max width

        Private _targetX    As Integer
        Private _slidingOut As Boolean = False

        Public Sub New(title As String, message As String, type As ToastType,
                       Optional yOffset As Integer = 0)
            InitializeComponent(title, message, type, yOffset)
        End Sub

        Private Sub InitializeComponent(title As String, message As String,
                                        type As ToastType, yOffset As Integer)
            ' ── Colour palette ───────────────────────────────────────────────
            Dim accent As Color
            Dim iconText As String
            Select Case type
                Case ToastType.Warning
                    accent   = Color.FromArgb(245, 158, 11)
                    iconText = "!"
                Case ToastType.Success
                    accent   = Color.FromArgb(34, 197, 94)
                    iconText = "✓"
                Case Else
                    accent   = Color.FromArgb(99, 162, 255)
                    iconText = "i"
            End Select

            ' Icon background: 25% accent blended into the dark bg so it's clearly
            ' distinct from both the bar and the form background.
            Dim bg = Color.FromArgb(22, 26, 42)
            Dim iconBg = Color.FromArgb(
                CInt(accent.R * 0.25 + bg.R * 0.75),
                CInt(accent.G * 0.25 + bg.G * 0.75),
                CInt(accent.B * 0.25 + bg.B * 0.75))

            ' ── Form shell ───────────────────────────────────────────────────
            Me.FormBorderStyle = FormBorderStyle.None
            Me.ShowInTaskbar   = False
            Me.TopMost         = True
            Me.DoubleBuffered  = True
            Me.Size            = New Size(FORM_W, FORM_H)
            Me.BackColor       = bg
            Me.StartPosition   = FormStartPosition.Manual

            ' Initial position: just off the right edge
            Dim wa = Screen.PrimaryScreen.WorkingArea
            _targetX    = wa.Right - FORM_W - 12
            Dim targetY = wa.Top + 78 + yOffset
            Me.Location = New Point(wa.Right + 10, targetY)

            ' ── Left accent bar ───────────────────────────────────────────────
            ' Placed first so everything else renders on top of it.
            Dim bar = New Panel() With {
                .Location  = New Point(0, 0),
                .Size      = New Size(BAR_W, FORM_H),
                .BackColor = accent
            }

            ' ── Icon panel (tinted accent background) ─────────────────────────
            ' Sits 9 px to the right of the bar — clearly separated from it.
            Dim iconY = (FORM_H - ICON_SIZE) \ 2   ' vertically centred
            Dim iconPanel = New Panel() With {
                .Location  = New Point(ICON_X, iconY),
                .Size      = New Size(ICON_SIZE, ICON_SIZE),
                .BackColor = iconBg
            }

            Dim lblIcon = New Label() With {
                .Text      = iconText,
                .Font      = New Font("Segoe UI", 12, FontStyle.Bold),
                .ForeColor = accent,
                .BackColor = iconBg,   ' same as panel — no transparent quirks
                .Location  = New Point(0, 0),
                .Size      = New Size(ICON_SIZE, ICON_SIZE),
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize  = False
            }
            iconPanel.Controls.Add(lblIcon)

            ' ── Title ─────────────────────────────────────────────────────────
            Dim lblTitle = New Label() With {
                .Text      = title,
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = bg,
                .Location  = New Point(TEXT_X, 14),
                .Size      = New Size(FORM_W - TEXT_X - 8, 20),
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleLeft
            }

            ' ── Message ───────────────────────────────────────────────────────
            Dim lblMsg = New Label() With {
                .Text      = message,
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(160, 174, 200),
                .BackColor = bg,
                .Location  = New Point(TEXT_X, 36),
                .Size      = New Size(FORM_W - TEXT_X - 8, 36),
                .AutoSize  = False,
                .TextAlign = ContentAlignment.TopLeft
            }

            ' ── Progress bar ──────────────────────────────────────────────────
            _progress = New Panel() With {
                .Location  = New Point(BAR_W, FORM_H - 3),
                .Size      = New Size(PROGRESS_W, 3),
                .BackColor = accent
            }

            ' Controls added in Z-order: bar first (back), icon panel on top of bar, rest in front
            Me.Controls.AddRange({bar, iconPanel, lblTitle, lblMsg, _progress})

            ' Click anywhere to dismiss early
            Dim dismissClick = New EventHandler(Sub(s, e) StartSlideOut())
            AddHandler Me.Click,          dismissClick
            AddHandler iconPanel.Click,   dismissClick
            AddHandler lblIcon.Click,     dismissClick
            AddHandler lblTitle.Click,    dismissClick
            AddHandler lblMsg.Click,      dismissClick
            AddHandler _progress.Click,   dismissClick

            ' ── Slide-in timer ────────────────────────────────────────────────
            _slideTimer = New System.Windows.Forms.Timer() With {.Interval = 14}
            AddHandler _slideTimer.Tick, AddressOf OnSlideTick
            _slideTimer.Start()
        End Sub

        ' ── Animation ─────────────────────────────────────────────────────────

        Private Sub OnSlideTick(sender As Object, e As EventArgs)
            If _slidingOut Then
                ' Slide out to the right (off-screen)
                Dim wa    = Screen.PrimaryScreen.WorkingArea
                Dim delta = Math.Max(CInt((wa.Right - Me.Left) * 0.25), 8)
                Me.Left += delta
                If Me.Left >= wa.Right Then
                    _slideTimer.Stop()
                    Me.Close()
                End If
            Else
                ' Slide in from the right
                Dim dist  = Me.Left - _targetX
                Dim delta = Math.Max(CInt(dist * 0.25), 4)
                Me.Left -= delta
                If Me.Left <= _targetX Then
                    Me.Left = _targetX
                    _slideTimer.Stop()
                    ' Begin countdown timer
                    _lifeTimer = New System.Windows.Forms.Timer() With {.Interval = 100}
                    AddHandler _lifeTimer.Tick, AddressOf OnLifeTick
                    _lifeTimer.Start()
                End If
            End If
        End Sub

        Private Sub OnLifeTick(sender As Object, e As EventArgs)
            _ticks += 1
            _progress.Width = CInt(PROGRESS_W * (1.0 - _ticks / CDbl(TOTAL_TICKS)))
            If _ticks >= TOTAL_TICKS Then
                _lifeTimer.Stop()
                StartSlideOut()
            End If
        End Sub

        Private Sub StartSlideOut()
            If _slidingOut Then Return
            _slidingOut = True
            _lifeTimer?.Stop()
            _slideTimer = New System.Windows.Forms.Timer() With {.Interval = 14}
            AddHandler _slideTimer.Tick, AddressOf OnSlideTick
            _slideTimer.Start()
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                _slideTimer?.Stop() : _slideTimer?.Dispose()
                _lifeTimer?.Stop()  : _lifeTimer?.Dispose()
            End If
            MyBase.Dispose(disposing)
        End Sub

    End Class

End Namespace
