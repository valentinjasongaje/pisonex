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

        ' ── Dimensions ───────────────────────────────────────────────
        Private Const FORM_W        As Integer = 240
        Private Const CORNER_R      As Integer = 14
        Private Const ACCENT_H      As Integer = 3
        Private Const PAD_X         As Integer = 14
        Private Const PAD_Y         As Integer = 10
        Private Const FORM_H_SLIM   As Integer = 52
        Private Const FORM_H_TALL   As Integer = 72
        Private Const DOT_SIZE      As Integer = 8
        Private Const DOT_MARGIN    As Integer = 10
        Private Const MEMBER_ROW_H  As Integer = 24

        ' ── Controls ─────────────────────────────────────────────────
        Private _lblTime  As Label
        Private _lblPC    As Label
        Private _pbLogo   As PictureBox
        Private _lblMember As Label
        Private _btnLogout As Button

        Private _isConnected As Boolean = True
        Private _memberName As String = Nothing

        Public Event MemberLogoutRequested()
        Public Event TimerHiddenByUser()

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

            Me.Controls.AddRange({_lblTime, _lblPC, _lblMember, _btnLogout, _pbLogo})

            ' Left-click drag on every visible surface
            Dim drag = New MouseEventHandler(AddressOf HandleMouseDown)
            AddHandler Me.MouseDown,        drag
            AddHandler _lblTime.MouseDown,  drag
            AddHandler _lblPC.MouseDown,    drag
            AddHandler _lblMember.MouseDown, drag
            AddHandler _pbLogo.MouseDown,   drag
        End Sub

        ' ── Custom paint: rounded rect + gradient accent + border + dot ──
        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim rect = New Rectangle(0, 0, Me.Width - 1, Me.Height - 1)

            ' Fill background (Region clips to rounded shape)
            g.Clear(BgSolid)

            ' Border drawn along rounded path
            Using path = RoundedRect(rect, CORNER_R)
                Using pen = New Pen(BorderColor, 1)
                    g.DrawPath(pen, path)
                End Using
            End Using

            ' Gradient accent bar at top
            Dim accentRect = New Rectangle(CORNER_R, 0, Me.Width - CORNER_R * 2, ACCENT_H)
            Using br = New LinearGradientBrush(accentRect, AccentBlue, AccentPurple, 0F)
                g.FillRectangle(br, accentRect)
            End Using

            ' Separator line above member row (when visible)
            If _lblMember.Visible Then
                Dim sepY = Me.Height - MEMBER_ROW_H - 4
                Using pen = New Pen(Color.FromArgb(40, 100, 120, 180), 1)
                    g.DrawLine(pen, PAD_X, sepY, Me.Width - PAD_X, sepY)
                End Using
            End If

            ' Connection dot
            If AppConfig.TimerShowConnDot Then
                Dim dotX = Me.Width - DOT_SIZE - DOT_MARGIN
                Dim dotY = DOT_MARGIN
                Dim dotClr = If(_isConnected, GreenColor, Color.FromArgb(245, 158, 11))
                Using br = New SolidBrush(dotClr)
                    g.FillEllipse(br, dotX, dotY, DOT_SIZE, DOT_SIZE)
                End Using
                ' Glow effect
                Using glowBr = New SolidBrush(Color.FromArgb(40, dotClr))
                    g.FillEllipse(glowBr, dotX - 2, dotY - 2, DOT_SIZE + 4, DOT_SIZE + 4)
                End Using
            End If

            MyBase.OnPaint(e)
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

        ''' <summary>
        ''' Re-layouts the overlay according to current AppConfig timer settings.
        ''' Safe to call from any thread.
        ''' </summary>
        Public Sub ApplyConfig()
            If Me.InvokeRequired Then
                Me.Invoke(Sub() ApplyConfig())
                Return
            End If

            Dim showPc  = AppConfig.TimerShowPcLabel
            Dim pcAbove = (AppConfig.TimerPcLabelPosition = "Above")

            _lblPC.Visible = showPc
            _lblPC.Text    = $"PC {AppConfig.PCNumber:D2}"

            ' Logo offset
            Dim hasLogo  = (_pbLogo.Image IsNot Nothing)
            Dim contentX = If(hasLogo, PAD_X + _pbLogo.Width + 6, PAD_X)
            Dim contentW = FORM_W - contentX - PAD_X

            _pbLogo.Location = New Point(PAD_X, PAD_Y + ACCENT_H)

            If showPc AndAlso pcAbove Then
                ' ── PC label above time ──────────────────────────────
                Me.Size = New Size(FORM_W, FORM_H_TALL)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, FORM_W, FORM_H_TALL), CORNER_R))

                _lblPC.Location  = New Point(contentX, PAD_Y + ACCENT_H)
                _lblPC.Size      = New Size(contentW - DOT_SIZE - DOT_MARGIN, 16)
                _lblPC.Font      = New Font("Segoe UI", 8, FontStyle.Bold)
                _lblPC.TextAlign = ContentAlignment.MiddleCenter

                _lblTime.Location = New Point(contentX, PAD_Y + ACCENT_H + 17)
                _lblTime.Size     = New Size(contentW, FORM_H_TALL - PAD_Y - ACCENT_H - 17 - PAD_Y)

            ElseIf showPc Then
                ' ── PC label side ────────────────────────────────────
                Me.Size = New Size(FORM_W, FORM_H_SLIM)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, FORM_W, FORM_H_SLIM), CORNER_R))

                Dim timeW = Math.Max(100, contentW - 60)
                _lblTime.Location = New Point(contentX, PAD_Y + ACCENT_H)
                _lblTime.Size     = New Size(timeW, FORM_H_SLIM - PAD_Y * 2 - ACCENT_H)

                _lblPC.Location  = New Point(contentX + timeW + 4, (FORM_H_SLIM - 20) \ 2)
                _lblPC.Size      = New Size(56, 20)
                _lblPC.Font      = New Font("Segoe UI", 8, FontStyle.Bold)
                _lblPC.TextAlign = ContentAlignment.MiddleLeft

            Else
                ' ── No PC label ──────────────────────────────────────
                Me.Size = New Size(FORM_W, FORM_H_SLIM)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, FORM_W, FORM_H_SLIM), CORNER_R))

                _lblTime.Location = New Point(contentX, PAD_Y + ACCENT_H)
                _lblTime.Size     = New Size(contentW, FORM_H_SLIM - PAD_Y * 2 - ACCENT_H)
            End If

            _lblTime.ForeColor = Color.FromArgb(AppConfig.TimerTimeArgb)

            ' Expand height for member row if visible
            If _lblMember.Visible Then
                Dim baseH = Me.Height
                Dim newH = baseH + MEMBER_ROW_H + 10  ' 4px separator + 6px bottom padding for rounded corner
                Me.Size = New Size(FORM_W, newH)
                Me.Region = New Region(RoundedRect(New Rectangle(0, 0, FORM_W, newH), CORNER_R))
                LayoutMemberControls(baseH)
            End If

            Me.Invalidate()
            PositionToCorner()
        End Sub

        Private Sub PositionToCorner()
            Dim wa = Screen.PrimaryScreen.WorkingArea
            Me.Location = New Point(wa.Right - Me.Width - 12, wa.Top + 12)
        End Sub

        ' ── Public API ────────────────────────────────────────────────

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

        Public Sub SetMemberInfo(username As String, canLogout As Boolean)
            If Me.InvokeRequired Then
                Me.Invoke(Sub() SetMemberInfo(username, canLogout))
                Return
            End If
            Dim changed = (_memberName IsNot Nothing) <> (username IsNot Nothing)
            _memberName = username
            If Not String.IsNullOrEmpty(username) Then
                _lblMember.Text = $"  {username}"  ' small indent after separator
                _lblMember.Visible = True
                _btnLogout.Visible = True
                _btnLogout.Enabled = canLogout
            Else
                _lblMember.Visible = False
                _btnLogout.Visible = False
            End If
            ' Full re-layout so the form resizes/shrinks correctly
            If changed Then
                ApplyConfig()
            Else
                ' Just reposition controls without full resize
                If _lblMember.Visible Then
                    LayoutMemberControls(Me.Height - MEMBER_ROW_H - 4)
                End If
            End If
        End Sub

        Private Sub LayoutMemberControls(baseH As Integer)
            If Not _lblMember.Visible Then Return
            ' Member row sits below a subtle separator line
            Dim y = baseH + 4  ' 4px gap after separator
            _lblMember.Location = New Point(PAD_X, y)
            _lblMember.Size = New Size(Me.Width - PAD_X - 60 - PAD_X, MEMBER_ROW_H)
            ' Keep logout button inside the rounded rect (CORNER_R inset from edge)
            _btnLogout.Location = New Point(Me.Width - 46 - PAD_X - 4, y + (MEMBER_ROW_H - 18) \ 2)
        End Sub

        ' ── Logout confirmation ──────────────────────────────────────
        Private Sub OnLogoutClick(sender As Object, e As EventArgs)
            If ConfirmLogout() Then
                RaiseEvent MemberLogoutRequested()
            End If
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
            AddHandler itemReset.Click, Sub(s, ev) PositionToCorner()

            menu.Items.Add(itemHide)
            menu.Items.Add(New ToolStripSeparator())
            menu.Items.Add(itemReset)

            menu.Show(Me, Me.PointToClient(Control.MousePosition))
        End Sub

    End Class

End Namespace
