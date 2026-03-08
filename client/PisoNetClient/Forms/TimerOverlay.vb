Imports System.Drawing
Imports System.Windows.Forms

Namespace Forms

    ''' <summary>
    ''' Small floating timer shown in the top-right corner when a session is active.
    ''' Shows remaining time and server connection status.
    ''' </summary>
    Public Class TimerOverlay
        Inherits Form

        ' Background colour used for the form AND all child controls so
        ' no "white flash" appears.  Do NOT use Opacity — layered windows
        ' (WS_EX_LAYERED) skip the normal WM_PAINT cycle and render white.
        Private Shared ReadOnly BgColor As Color = Color.FromArgb(18, 22, 38)

        Private _lblTime   As Label
        Private _lblStatus As Label

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.DoubleBuffered    = True
            Me.FormBorderStyle   = FormBorderStyle.None
            Me.ShowInTaskbar     = False
            Me.TopMost           = True
            Me.Size              = New Size(164, 58)
            Me.BackColor         = BgColor
            ' ──────────────────────────────────────────────────────────────
            ' NOTE: Me.Opacity is intentionally NOT set.
            ' Setting Opacity causes WinForms to add WS_EX_LAYERED, which
            ' routes all painting through UpdateLayeredWindow instead of
            ' WM_PAINT.  Child label BackColor = Transparent then resolves
            ' to white (system default) rather than the form background.
            ' ──────────────────────────────────────────────────────────────
            Me.StartPosition     = FormStartPosition.Manual
            Me.Cursor            = Cursors.SizeAll   ' Shows a move cursor; no spinner

            PositionToCorner()

            _lblTime = New Label() With {
                .Font      = New Font("Segoe UI", 22, FontStyle.Bold),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = BgColor,               ' Must match form — NOT Transparent
                .Text      = "--:--",
                .AutoSize  = False,
                .Size      = New Size(164, 40),
                .Location  = New Point(0, 2),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            _lblStatus = New Label() With {
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(100, 116, 139),
                .BackColor = BgColor,               ' Must match form — NOT Transparent
                .Text      = "Connected",
                .AutoSize  = False,
                .Size      = New Size(164, 16),
                .Location  = New Point(0, 40),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Me.Controls.AddRange({_lblTime, _lblStatus})

            ' Allow clicking anywhere on the overlay to drag it
            AddHandler Me.MouseDown,         AddressOf StartDrag
            AddHandler _lblTime.MouseDown,   AddressOf StartDrag
            AddHandler _lblStatus.MouseDown, AddressOf StartDrag
        End Sub

        Private Sub PositionToCorner()
            Dim wa = Screen.PrimaryScreen.WorkingArea
            Me.Location = New Point(wa.Right - Me.Width - 12, wa.Top + 12)
        End Sub

        ' ── Public API ────────────────────────────────────────────────────

        Public Sub UpdateTime(minutes As Integer, seconds As Integer)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateTime(minutes, seconds))
                Return
            End If
            _lblTime.Text = $"{minutes:D2}:{seconds:D2}"
            _lblTime.ForeColor = If(
                minutes < 5,
                Color.FromArgb(239, 68, 68),    ' Red when < 5 min left
                Color.FromArgb(34, 197, 94))    ' Green otherwise
        End Sub

        Public Sub ShowConnected()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowConnected())
                Return
            End If
            _lblStatus.Text      = "Connected"
            _lblStatus.ForeColor = Color.FromArgb(100, 116, 139)
        End Sub

        Public Sub ShowOffline()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowOffline())
                Return
            End If
            _lblStatus.Text      = "Offline — timer running"
            _lblStatus.ForeColor = Color.FromArgb(245, 158, 11)
        End Sub

        ' ── Drag support ──────────────────────────────────────────────────

        Private _dragging  As Boolean = False
        Private _dragStart As Point

        Private Sub StartDrag(sender As Object, e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then
                _dragging  = True
                _dragStart = e.Location
                AddHandler Me.MouseMove, AddressOf OnDrag
                AddHandler Me.MouseUp,   AddressOf StopDrag
            End If
        End Sub

        Private Sub OnDrag(sender As Object, e As MouseEventArgs)
            If _dragging Then
                Me.Location = New Point(
                    Me.Left + e.X - _dragStart.X,
                    Me.Top  + e.Y - _dragStart.Y)
            End If
        End Sub

        Private Sub StopDrag(sender As Object, e As MouseEventArgs)
            _dragging = False
            RemoveHandler Me.MouseMove, AddressOf OnDrag
            RemoveHandler Me.MouseUp,   AddressOf StopDrag
        End Sub

    End Class

End Namespace
