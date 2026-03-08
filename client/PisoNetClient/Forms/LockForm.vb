Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms
Imports PisoNetClient.Config

Namespace Forms

    ''' <summary>
    ''' Full-screen lock overlay shown when no session is active.
    ''' Blocks Alt+F4, Win key, and other escape keys.
    ''' Admin shortcut: Ctrl+Shift+F12 → PIN prompt → AdminPanel.
    ''' </summary>
    Public Class LockForm
        Inherits Form

        Private _lblMessage  As Label
        Private _lblSub      As Label
        Private _lblPCNumber As Label
        Private _lblOffline  As Label
        Private _bgImage     As Image
        Private _allowClose  As Boolean = False

        Public Event AdminPanelRequested()

        Public Sub New()
            LoadBackground()
            InitializeComponent()
        End Sub

        Private Sub LoadBackground()
            Dim path = AppConfig.LockBgImagePath
            If Not String.IsNullOrEmpty(path) AndAlso File.Exists(path) Then
                Try
                    _bgImage = Image.FromFile(path)
                Catch
                    _bgImage = Nothing
                End Try
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

            ' PC number badge — top-left
            _lblPCNumber = New Label() With {
                .Text      = $"PC {AppConfig.PCNumber:D2}",
                .Font      = New Font("Segoe UI", 11),
                .ForeColor = Color.FromArgb(100, 120, 160),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Location  = New Point(24, 24)
            }

            ' Server-offline indicator — top-right, hidden by default
            _lblOffline = New Label() With {
                .Text      = "⚠  Server Offline",
                .Font      = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(245, 158, 11),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Visible   = False
            }

            ' Main message
            _lblMessage = New Label() With {
                .Text      = AppConfig.LockMessage,
                .Font      = New Font("Segoe UI", 36, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            ' Sub-message
            _lblSub = New Label() With {
                .Text      = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}",
                .Font      = New Font("Segoe UI", 14),
                .ForeColor = Color.FromArgb(120, 140, 180),
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Me.Controls.AddRange({_lblPCNumber, _lblOffline, _lblMessage, _lblSub})
        End Sub

        ' ── Background painting ───────────────────────────────────────────

        Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
            If _bgImage IsNot Nothing Then
                ' Cover-fit the image
                Dim scale = Math.Max(CSng(Me.Width) / _bgImage.Width,
                                     CSng(Me.Height) / _bgImage.Height)
                Dim w = CInt(_bgImage.Width * scale)
                Dim h = CInt(_bgImage.Height * scale)
                e.Graphics.DrawImage(_bgImage, (Me.Width - w) \ 2, (Me.Height - h) \ 2, w, h)
                ' Semi-transparent dim so text stays readable
                Using br = New SolidBrush(Color.FromArgb(160, 0, 0, 0))
                    e.Graphics.FillRectangle(br, Me.ClientRectangle)
                End Using
            Else
                e.Graphics.Clear(Me.BackColor)
            End If
        End Sub

        ' ── Layout helpers ────────────────────────────────────────────────

        Protected Overrides Sub OnLoad(e As EventArgs)
            MyBase.OnLoad(e)
            CenterLabels()
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)
            CenterLabels()
        End Sub

        Private Sub CenterLabels()
            _lblMessage.Location = New Point(
                (Me.ClientSize.Width  - _lblMessage.Width)  \ 2,
                (Me.ClientSize.Height - _lblMessage.Height) \ 2 - 40)
            _lblSub.Location = New Point(
                (Me.ClientSize.Width - _lblSub.Width) \ 2,
                _lblMessage.Bottom + 16)
            If _lblOffline.Visible Then
                _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
            End If
        End Sub

        ' ── Server-status API ─────────────────────────────────────────────

        Public Sub ShowOfflineStatus()
            If Me.InvokeRequired Then Me.Invoke(Sub() ShowOfflineStatus()) : Return
            _lblOffline.Visible  = True
            _lblOffline.Location = New Point(Me.ClientSize.Width - _lblOffline.Width - 24, 24)
        End Sub

        Public Sub HideOfflineStatus()
            If Me.InvokeRequired Then Me.Invoke(Sub() HideOfflineStatus()) : Return
            _lblOffline.Visible = False
        End Sub

        ''' <summary>Reload appearance from config after admin saves settings.</summary>
        Public Sub RefreshAppearance()
            If Me.InvokeRequired Then Me.Invoke(Sub() RefreshAppearance()) : Return
            _bgImage?.Dispose() : _bgImage = Nothing
            LoadBackground()
            Me.BackColor      = Color.FromArgb(AppConfig.LockBgArgb)
            _lblMessage.Text  = AppConfig.LockMessage
            _lblPCNumber.Text = $"PC {AppConfig.PCNumber:D2}"
            _lblSub.Text      = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
            CenterLabels()
            Me.Invalidate()
        End Sub

        ' ── Keyboard handling ─────────────────────────────────────────────

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            Select Case True
                Case e.Alt    AndAlso e.KeyCode = Keys.F4               : e.Handled = True : e.SuppressKeyPress = True
                Case e.KeyCode = Keys.LWin OrElse e.KeyCode = Keys.RWin : e.Handled = True : e.SuppressKeyPress = True
                Case e.Control AndAlso e.Alt AndAlso e.KeyCode = Keys.Delete : e.Handled = True : e.SuppressKeyPress = True
                Case e.Alt    AndAlso e.KeyCode = Keys.Tab              : e.Handled = True : e.SuppressKeyPress = True
                ' Admin shortcut: Ctrl+Shift+F12
                Case e.Control AndAlso e.Shift AndAlso e.KeyCode = Keys.F12
                    e.Handled = True : e.SuppressKeyPress = True
                    RaiseEvent AdminPanelRequested()
                Case Else
                    MyBase.OnKeyDown(e)
            End Select
        End Sub

        Protected Overrides Sub WndProc(ByRef m As Message)
            Const WM_CLOSE As Integer = &H10
            If m.Msg = WM_CLOSE AndAlso Not _allowClose Then Return
            MyBase.WndProc(m)
        End Sub

        ''' <summary>Call before Application.Exit() to allow the form to close.</summary>
        Public Sub AllowExit()
            _allowClose = True
        End Sub

        Protected Overrides Sub OnDeactivate(e As EventArgs)
            MyBase.OnDeactivate(e)
            Me.TopMost = True
            Me.BringToFront()
            Me.Activate()
        End Sub

    End Class

End Namespace
