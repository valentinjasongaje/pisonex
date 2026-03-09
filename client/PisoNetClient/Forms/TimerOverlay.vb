Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Runtime.InteropServices
Imports System.Windows.Forms
Imports PisoNetClient.Config

Namespace Forms

    ''' <summary>
    ''' Small floating timer shown in the top-right corner when a session is active.
    ''' • Left-click-drag uses native Win32 caption drag (smooth, zero lag).
    ''' • Right-click shows a context menu to hide or reset position.
    ''' • Connection indicator: small filled circle (green = connected, amber = offline)
    '''   drawn in the upper-right corner of the form.
    ''' • PC label: "PC 01" shown above or beside the time, configurable.
    ''' • Call ApplyConfig() after changing AppConfig timer settings to re-layout.
    ''' </summary>
    Public Class TimerOverlay
        Inherits Form

        Private Shared ReadOnly BgColor  As Color = Color.FromArgb(18, 22, 38)
        Private Shared ReadOnly DimColor As Color = Color.FromArgb(100, 116, 139)

        ' Fixed dimensions
        Private Const FORM_W      As Integer = 260   ' width stays constant
        Private Const PAD_X       As Integer = 10   ' horizontal inner padding
        Private Const PAD_Y       As Integer = 8    ' vertical inner padding
        Private Const FORM_H_SLIM As Integer = 48    ' no PC label / Side label
        Private Const FORM_H_TALL As Integer = 68    ' PC label Above

        Private Const DOT_SIZE As Integer = 10   ' connection indicator circle diameter
        Private Const DOT_MARGIN As Integer = 6  ' gap from right / top edge

        Private _lblTime As Label
        Private _lblPC   As Label    ' PC number badge — shown/hidden per config
        Private _dotPanel As Panel   ' connection indicator dot (transparent, Paint-driven)

        Private _isConnected As Boolean = True

        ' ── Native drag (handled by Windows DWM — zero lag) ──────────────────
        <DllImport("user32.dll", CharSet:=CharSet.Auto)>
        Private Shared Function ReleaseCapture() As Boolean
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Auto)>
        Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer,
                                            wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        Private Const WM_NCLBUTTONDOWN As Integer = &HA1
        Private Const HTCAPTION        As Integer = 2

        Public Sub New()
            InitializeComponent()
            ApplyConfig()
        End Sub

        Private Sub InitializeComponent()
            Me.DoubleBuffered  = True
            Me.FormBorderStyle = FormBorderStyle.None
            Me.ShowInTaskbar   = False
            Me.TopMost         = True
            Me.BackColor       = BgColor
            Me.StartPosition   = FormStartPosition.Manual
            Me.Cursor          = Cursors.SizeAll

            ' ── Time label ───────────────────────────────────────────────────
            _lblTime = New Label() With {
                .Font      = New Font("Segoe UI", 16, FontStyle.Bold),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = BgColor,
                .Text      = "--:--",
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' ── PC number label ───────────────────────────────────────────────
            _lblPC = New Label() With {
                .Text      = $"PC {AppConfig.PCNumber:D2}",
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = DimColor,
                .BackColor = BgColor,
                .AutoSize  = False,
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible   = False
            }

            ' ── Connection dot (transparent panel — drawn in Paint event) ─────
            _dotPanel = New Panel() With {
                .BackColor = Color.Transparent,
                .Size      = New Size(DOT_SIZE, DOT_SIZE),
                .Visible   = False
            }
            AddHandler _dotPanel.Paint, AddressOf OnDotPaint

            Me.Controls.AddRange({_lblTime, _lblPC, _dotPanel})

            ' Left-click drag on every visible surface
            Dim drag = New MouseEventHandler(AddressOf OnMouseDown)
            AddHandler Me.MouseDown,       drag
            AddHandler _lblTime.MouseDown, drag
            AddHandler _lblPC.MouseDown,   drag
        End Sub

        ' ── Connection dot paint ──────────────────────────────────────────────

        Private Sub OnDotPaint(sender As Object, e As PaintEventArgs)
            Dim clr = If(_isConnected,
                         Color.FromArgb(34, 197, 94),    ' green
                         Color.FromArgb(245, 158, 11))   ' amber
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            Using br = New SolidBrush(clr)
                e.Graphics.FillEllipse(br, 0, 0, DOT_SIZE - 1, DOT_SIZE - 1)
            End Using
        End Sub

        ' ── Layout engine ─────────────────────────────────────────────────────

        ''' <summary>
        ''' Re-layouts the overlay according to current AppConfig timer settings.
        ''' Safe to call from any thread.
        ''' </summary>
        Public Sub ApplyConfig()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ApplyConfig())
                Return
            End If

            Dim showDot  = AppConfig.TimerShowConnDot
            Dim showPc   = AppConfig.TimerShowPcLabel
            Dim pcAbove  = (AppConfig.TimerPcLabelPosition = "Above")

            _dotPanel.Visible = showDot
            _lblPC.Visible    = showPc
            _lblPC.Text       = $"PC {AppConfig.PCNumber:D2}"

            If showPc AndAlso pcAbove Then
                ' ── PC label above time ────────────────────────────────────
                Me.Size = New Size(FORM_W, FORM_H_TALL)

                _dotPanel.Location = New Point(FORM_W - DOT_SIZE - DOT_MARGIN, DOT_MARGIN)

                Dim pcW = If(showDot, FORM_W - DOT_SIZE - DOT_MARGIN - 2, FORM_W) - PAD_X * 2
                _lblPC.Location  = New Point(PAD_X, PAD_Y)
                _lblPC.Size      = New Size(pcW, 16)
                _lblPC.Font      = New Font("Segoe UI", 9)
                _lblPC.TextAlign = ContentAlignment.MiddleCenter

                _lblTime.Location = New Point(PAD_X, PAD_Y + 18)
                _lblTime.Size     = New Size(FORM_W - PAD_X * 2, FORM_H_TALL - PAD_Y - 18 - PAD_Y)

            ElseIf showPc Then
                ' ── PC label side (right of time, vertically centered) ────
                Me.Size = New Size(FORM_W, FORM_H_SLIM)

                _dotPanel.Location = New Point(FORM_W - DOT_SIZE - DOT_MARGIN, DOT_MARGIN)

                ' Time gets padded left, PC label sits right of it
                _lblTime.Location = New Point(PAD_X, PAD_Y)
                _lblTime.Size     = New Size(170, FORM_H_SLIM - PAD_Y * 2)

                _lblPC.Location  = New Point(PAD_X + 170 + 4, (FORM_H_SLIM - 24) \ 2)
                _lblPC.Size      = New Size(64, 24)
                _lblPC.Font      = New Font("Segoe UI", 9, FontStyle.Bold)
                _lblPC.TextAlign = ContentAlignment.MiddleLeft

            Else
                ' ── No PC label ───────────────────────────────────────────
                Me.Size = New Size(FORM_W, FORM_H_SLIM)
                _dotPanel.Location = New Point(FORM_W - DOT_SIZE - DOT_MARGIN, DOT_MARGIN)
                _lblTime.Location  = New Point(PAD_X, 6)
                _lblTime.Size      = New Size(FORM_W - PAD_X * 2, FORM_H_SLIM - 10)
            End If

            ' Refresh dot and time colours
            _dotPanel.Invalidate()
            ' Re-apply time colour in case it changed (next UpdateTime will refine for low-time)
            _lblTime.ForeColor = Color.FromArgb(AppConfig.TimerTimeArgb)
            PositionToCorner()
        End Sub

        Private Sub PositionToCorner()
            Dim wa = Screen.PrimaryScreen.WorkingArea
            Me.Location = New Point(wa.Right - Me.Width - 12, wa.Top + 12)
        End Sub

        ' ── Public API ─────────────────────────────────────────────────────────

        Public Sub UpdateTime(minutes As Integer, seconds As Integer)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateTime(minutes, seconds))
                Return
            End If
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
            _dotPanel.Invalidate()
        End Sub

        Public Sub ShowOffline()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowOffline())
                Return
            End If
            _isConnected = False
            _dotPanel.Invalidate()
        End Sub

        ' ── Mouse handling ─────────────────────────────────────────────────────

        Private Sub OnMouseDown(sender As Object, e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then
                ReleaseCapture()
                SendMessage(Me.Handle, WM_NCLBUTTONDOWN, New IntPtr(HTCAPTION), IntPtr.Zero)

            ElseIf e.Button = MouseButtons.Right Then
                ShowContextMenu()
            End If
        End Sub

        Private Sub ShowContextMenu()
            Dim menu = New ContextMenuStrip() With {
                .BackColor = Color.FromArgb(26, 30, 46),
                .ForeColor = Color.White,
                .Font      = New Font("Segoe UI", 9)
            }

            Dim itemHide = New ToolStripMenuItem("Hide Timer") With {
                .ForeColor = Color.FromArgb(220, 228, 240)
            }
            AddHandler itemHide.Click, Sub(s, e) Me.Hide()

            Dim itemReset = New ToolStripMenuItem("Reset Position") With {
                .ForeColor = Color.FromArgb(220, 228, 240)
            }
            AddHandler itemReset.Click, Sub(s, e) PositionToCorner()

            menu.Items.Add(itemHide)
            menu.Items.Add(New ToolStripSeparator())
            menu.Items.Add(itemReset)

            menu.Show(Me, Me.PointToClient(Control.MousePosition))
        End Sub

    End Class

End Namespace
