Imports System.Drawing
Imports System.Windows.Forms

Namespace Forms

    ''' <summary>
    ''' Full-screen lock overlay. Blocks access to the desktop when shown.
    ''' Resists Alt+F4, Win key, and other escape attempts.
    ''' </summary>
    Public Class LockForm
        Inherits Form

        Private _lblMessage As Label
        Private _lblSub As Label
        Private _lblPCNumber As Label

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.FormBorderStyle = FormBorderStyle.None
            Me.WindowState = FormWindowState.Maximized
            Me.TopMost = True
            Me.ShowInTaskbar = False
            Me.BackColor = Color.FromArgb(10, 14, 23)
            Me.ForeColor = Color.White
            Me.KeyPreview = True
            Me.Cursor = Cursors.Default

            ' Prevent the form from being moved or resized
            Me.SetStyle(ControlStyles.Selectable, True)

            ' PC Number label (top left)
            _lblPCNumber = New Label() With {
                .Text = $"PC {Config.AppConfig.PCNumber:D2}",
                .Font = New Font("Segoe UI", 11, FontStyle.Regular),
                .ForeColor = Color.FromArgb(100, 120, 160),
                .AutoSize = True,
                .Location = New Point(24, 24)
            }

            ' Main message
            _lblMessage = New Label() With {
                .Text = "Insert Coins to Start",
                .Font = New Font("Segoe UI", 36, FontStyle.Bold),
                .ForeColor = Color.White,
                .AutoSize = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' Sub-message
            _lblSub = New Label() With {
                .Text = $"Go to the Raspberry Pi unit and select PC {Config.AppConfig.PCNumber:D2}",
                .Font = New Font("Segoe UI", 14, FontStyle.Regular),
                .ForeColor = Color.FromArgb(120, 140, 180),
                .AutoSize = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Me.Controls.AddRange({_lblPCNumber, _lblMessage, _lblSub})
            AddHandler Me.Load, AddressOf OnFormLoad
            AddHandler Me.Resize, AddressOf OnFormResize
        End Sub

        Private Sub OnFormLoad(sender As Object, e As EventArgs)
            CenterLabels()
        End Sub

        Private Sub OnFormResize(sender As Object, e As EventArgs)
            CenterLabels()
        End Sub

        Private Sub CenterLabels()
            _lblMessage.Location = New Point(
                (Me.ClientSize.Width - _lblMessage.Width) \ 2,
                (Me.ClientSize.Height - _lblMessage.Height) \ 2 - 40
            )
            _lblSub.Location = New Point(
                (Me.ClientSize.Width - _lblSub.Width) \ 2,
                _lblMessage.Bottom + 16
            )
        End Sub

        ' ── Block escape keys ─────────────────────────────────────────

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            Select Case True
                Case e.Alt AndAlso e.KeyCode = Keys.F4
                    e.Handled = True : e.SuppressKeyPress = True
                Case e.KeyCode = Keys.LWin OrElse e.KeyCode = Keys.RWin
                    e.Handled = True : e.SuppressKeyPress = True
                Case e.Control AndAlso e.Alt AndAlso e.KeyCode = Keys.Delete
                    e.Handled = True : e.SuppressKeyPress = True
                Case e.Alt AndAlso e.KeyCode = Keys.Tab
                    e.Handled = True : e.SuppressKeyPress = True
                Case Else
                    MyBase.OnKeyDown(e)
            End Select
        End Sub

        ' Block WM_CLOSE from external sources
        Protected Overrides Sub WndProc(ByRef m As Message)
            Const WM_CLOSE As Integer = &H10
            If m.Msg = WM_CLOSE Then Return
            MyBase.WndProc(m)
        End Sub

        ' Keep this form on top even when other windows try to steal focus
        Protected Overrides Sub OnDeactivate(e As EventArgs)
            MyBase.OnDeactivate(e)
            Me.TopMost = True
            Me.BringToFront()
            Me.Activate()
        End Sub
    End Class

End Namespace
