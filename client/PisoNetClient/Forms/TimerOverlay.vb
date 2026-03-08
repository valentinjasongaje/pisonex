Imports System.Drawing
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

Namespace Forms

    ''' <summary>
    ''' Small floating timer shown in the top-right corner when a session is active.
    ''' • Left-click-drag uses native Win32 caption drag (smooth, zero lag).
    ''' • Right-click shows a context menu to hide or reset position.
    ''' </summary>
    Public Class TimerOverlay
        Inherits Form

        Private Shared ReadOnly BgColor As Color = Color.FromArgb(18, 22, 38)

        Private _lblTime   As Label
        Private _lblStatus As Label

        ' ── Native drag (zero-lag, handled by Windows DWM) ───────────────────
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
        End Sub

        Private Sub InitializeComponent()
            Me.DoubleBuffered    = True
            Me.FormBorderStyle   = FormBorderStyle.None
            Me.ShowInTaskbar     = False
            Me.TopMost           = True
            Me.Size              = New Size(164, 58)
            Me.BackColor         = BgColor
            Me.StartPosition     = FormStartPosition.Manual
            Me.Cursor            = Cursors.SizeAll

            PositionToCorner()

            _lblTime = New Label() With {
                .Font      = New Font("Segoe UI", 22, FontStyle.Bold),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = BgColor,
                .Text      = "--:--",
                .AutoSize  = False,
                .Size      = New Size(164, 40),
                .Location  = New Point(0, 2),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            _lblStatus = New Label() With {
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(100, 116, 139),
                .BackColor = BgColor,
                .Text      = "Connected",
                .AutoSize  = False,
                .Size      = New Size(164, 16),
                .Location  = New Point(0, 40),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Me.Controls.AddRange({_lblTime, _lblStatus})

            ' Left-click drag on every surface
            AddHandler Me.MouseDown,         AddressOf OnMouseDown
            AddHandler _lblTime.MouseDown,   AddressOf OnMouseDown
            AddHandler _lblStatus.MouseDown, AddressOf OnMouseDown
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
            _lblTime.Text = $"{minutes:D2}:{seconds:D2}"
            _lblTime.ForeColor = If(
                minutes < 5,
                Color.FromArgb(239, 68, 68),
                Color.FromArgb(34, 197, 94))
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

        ' ── Mouse handling ─────────────────────────────────────────────────────

        Private Sub OnMouseDown(sender As Object, e As MouseEventArgs)
            If e.Button = MouseButtons.Left Then
                ' Let Windows handle the drag natively — no jitter, perfect tracking
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
