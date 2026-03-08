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
    ''' The caller is responsible for positioning via Show(yOffset).
    ''' </summary>
    Public Class NotificationToast
        Inherits Form

        Private _progress    As Panel
        Private _slideTimer  As System.Windows.Forms.Timer
        Private _lifeTimer   As System.Windows.Forms.Timer
        Private _ticks       As Integer = 0
        Private Const TOTAL_TICKS As Integer = 40   ' 40 × 100 ms = 4 seconds
        Private Const FORM_W      As Integer = 300
        Private Const FORM_H      As Integer = 80
        Private Const PROGRESS_W  As Integer = 292  ' max progress bar width
        Private _targetX     As Integer
        Private _slidingOut  As Boolean = False

        Public Sub New(title As String, message As String, type As ToastType,
                       Optional yOffset As Integer = 0)
            InitializeComponent(title, message, type, yOffset)
        End Sub

        Private Sub InitializeComponent(title As String, message As String,
                                        type As ToastType, yOffset As Integer)
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
                    iconText = "+"
            End Select

            Dim bg = Color.FromArgb(22, 26, 42)

            Me.FormBorderStyle = FormBorderStyle.None
            Me.ShowInTaskbar   = False
            Me.TopMost         = True
            Me.DoubleBuffered  = True
            Me.Size            = New Size(FORM_W, FORM_H)
            Me.BackColor       = bg
            Me.StartPosition   = FormStartPosition.Manual

            ' ── Initial position: off-screen to the right ──────────────────
            Dim wa = Screen.PrimaryScreen.WorkingArea
            _targetX       = wa.Right - FORM_W - 12
            Dim targetY    = wa.Top + 78 + yOffset   ' below timer overlay
            Me.Location    = New Point(wa.Right + 10, targetY)

            ' ── Colored left accent bar ─────────────────────────────────────
            Dim bar = New Panel() With {
                .Location  = New Point(0, 0),
                .Size      = New Size(4, FORM_H),
                .BackColor = accent
            }

            ' ── Icon ───────────────────────────────────────────────────────
            Dim lblIcon = New Label() With {
                .Text      = iconText,
                .Font      = New Font("Segoe UI", 14, FontStyle.Bold),
                .ForeColor = accent,
                .BackColor = bg,
                .Location  = New Point(14, 0),
                .Size      = New Size(32, FORM_H),
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize  = False
            }

            ' ── Title ──────────────────────────────────────────────────────
            Dim lblTitle = New Label() With {
                .Text      = title,
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = bg,
                .Location  = New Point(52, 14),
                .Size      = New Size(242, 18),
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleLeft
            }

            ' ── Message ────────────────────────────────────────────────────
            Dim lblMsg = New Label() With {
                .Text      = message,
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(160, 174, 200),
                .BackColor = bg,
                .Location  = New Point(52, 34),
                .Size      = New Size(242, 36),
                .AutoSize  = False,
                .TextAlign = ContentAlignment.TopLeft
            }

            ' ── Progress bar ───────────────────────────────────────────────
            _progress = New Panel() With {
                .Location  = New Point(4, FORM_H - 3),
                .Size      = New Size(PROGRESS_W, 3),
                .BackColor = accent
            }

            Me.Controls.AddRange({bar, lblIcon, lblTitle, lblMsg, _progress})

            ' Click anywhere to dismiss
            Dim dismissClick = New EventHandler(Sub(s, e) StartSlideOut())
            AddHandler Me.Click,        dismissClick
            AddHandler lblIcon.Click,   dismissClick
            AddHandler lblTitle.Click,  dismissClick
            AddHandler lblMsg.Click,    dismissClick
            AddHandler _progress.Click, dismissClick

            ' ── Slide-in timer (~60 fps) ────────────────────────────────────
            _slideTimer = New System.Windows.Forms.Timer() With {.Interval = 14}
            AddHandler _slideTimer.Tick, AddressOf OnSlideTick
            _slideTimer.Start()
        End Sub

        ' ── Animation ──────────────────────────────────────────────────────────

        Private Sub OnSlideTick(sender As Object, e As EventArgs)
            If _slidingOut Then
                ' Slide to the right (off-screen)
                Dim wa = Screen.PrimaryScreen.WorkingArea
                Dim delta = Math.Max(CInt((wa.Right - Me.Left) * 0.25), 8)
                Me.Left += delta
                If Me.Left >= wa.Right Then
                    _slideTimer.Stop()
                    Me.Close()
                End If
            Else
                ' Slide in from the right
                Dim dist = Me.Left - _targetX
                Dim delta = Math.Max(CInt(dist * 0.25), 4)
                Me.Left -= delta
                If Me.Left <= _targetX Then
                    Me.Left = _targetX
                    _slideTimer.Stop()
                    ' Start countdown
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
