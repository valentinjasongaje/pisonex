Imports System.Drawing
Imports System.Windows.Forms

Namespace Forms

    ''' <summary>
    ''' Small floating timer shown in the corner when a session is active.
    ''' Shows remaining time and connection status.
    ''' </summary>
    Public Class TimerOverlay
        Inherits Form

        Private _lblTime As Label
        Private _lblStatus As Label
        Private _panelBg As Panel

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.FormBorderStyle = FormBorderStyle.None
            Me.ShowInTaskbar = False
            Me.TopMost = True
            Me.Size = New Size(160, 56)
            Me.BackColor = Color.FromArgb(15, 20, 35)
            Me.Opacity = 0.92
            Me.StartPosition = FormStartPosition.Manual

            PositionToCorner()

            _lblTime = New Label() With {
                .Font = New Font("Segoe UI", 22, FontStyle.Bold),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .Text = "--:--",
                .AutoSize = False,
                .Size = New Size(160, 38),
                .Location = New Point(0, 2),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            _lblStatus = New Label() With {
                .Font = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(100, 116, 139),
                .Text = "Connected",
                .AutoSize = False,
                .Size = New Size(160, 16),
                .Location = New Point(0, 38),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Me.Controls.AddRange({_lblTime, _lblStatus})

            ' Allow dragging the overlay
            AddHandler _lblTime.MouseDown, AddressOf StartDrag
            AddHandler Me.MouseDown, AddressOf StartDrag
        End Sub

        Private Sub PositionToCorner()
            Dim workArea = Screen.PrimaryScreen.WorkingArea
            Me.Location = New Point(workArea.Right - Me.Width - 12, workArea.Top + 12)
        End Sub

        Public Sub UpdateTime(minutes As Integer, seconds As Integer)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateTime(minutes, seconds))
                Return
            End If
            _lblTime.Text = $"{minutes:D2}:{seconds:D2}"
            _lblTime.ForeColor = If(
                minutes < 5,
                Color.FromArgb(239, 68, 68),    ' Red when < 5 min
                Color.FromArgb(34, 197, 94)     ' Green otherwise
            )
        End Sub

        Public Sub ShowConnected()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowConnected())
                Return
            End If
            _lblStatus.Text = "Connected"
            _lblStatus.ForeColor = Color.FromArgb(100, 116, 139)
        End Sub

        Public Sub ShowOffline()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ShowOffline())
                Return
            End If
            _lblStatus.Text = "Offline - timer running"
            _lblStatus.ForeColor = Color.FromArgb(245, 158, 11)
        End Sub

        ' ── Drag support ──────────────────────────────────────────────

        Private _dragging As Boolean = False
        Private _dragStart As Point

        Private Sub StartDrag(sender As Object, e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then
                _dragging = True
                _dragStart = e.Location
                AddHandler Me.MouseMove, AddressOf OnDrag
                AddHandler Me.MouseUp, AddressOf StopDrag
            End If
        End Sub

        Private Sub OnDrag(sender As Object, e As MouseEventArgs)
            If _dragging Then
                Me.Location = New Point(
                    Me.Left + e.X - _dragStart.X,
                    Me.Top + e.Y - _dragStart.Y
                )
            End If
        End Sub

        Private Sub StopDrag(sender As Object, e As MouseEventArgs)
            _dragging = False
            RemoveHandler Me.MouseMove, AddressOf OnDrag
            RemoveHandler Me.MouseUp, AddressOf StopDrag
        End Sub
    End Class

End Namespace
