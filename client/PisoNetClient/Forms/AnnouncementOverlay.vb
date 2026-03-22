Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms
Imports PisoNetClient.Resources

Namespace Forms

    ''' <summary>
    ''' Modern dark-card announcement overlay. Fullscreen dimmed background with a
    ''' centered rounded card containing an accent badge, title, body, and action button.
    ''' Used exclusively for shop-wide announcements.
    ''' </summary>
    Public Class AnnouncementOverlay
        Inherits Form

        Private _pnlCard      As Panel
        Private _lblBadge     As Label
        Private _lblTitle     As Label
        Private _lblBody      As Label
        Private _btnDismiss   As Button
        Private _btnClose     As Label

        ' ── Card dimensions ──────────────────────────────────────────────────
        Private Const CardW As Integer = 520
        Private Const CardRadius As Integer = 18
        Private Const CardPadX As Integer = 36
        Private Const CardPadTop As Integer = 32

        ' ── Colors ───────────────────────────────────────────────────────────
        Private Shared ReadOnly BgDim       As Color = Color.FromArgb(200, 4, 6, 14)
        Private Shared ReadOnly CardBg      As Color = Color.FromArgb(18, 22, 38)
        Private Shared ReadOnly CardBorder  As Color = Color.FromArgb(50, 80, 130, 220)
        Private Shared ReadOnly AccentGold  As Color = Color.FromArgb(250, 204, 21)
        Private Shared ReadOnly AccentBlue  As Color = Color.FromArgb(79, 142, 247)
        Private Shared ReadOnly TextTitle   As Color = Color.FromArgb(240, 244, 255)
        Private Shared ReadOnly TextBody    As Color = Color.FromArgb(190, 200, 220)
        Private Shared ReadOnly TextDim     As Color = Color.FromArgb(120, 140, 170)
        Private Shared ReadOnly BtnBg       As Color = Color.FromArgb(79, 142, 247)
        Private Shared ReadOnly BtnHover    As Color = Color.FromArgb(100, 160, 255)

        Public Sub New(message As String)
            Me.FormBorderStyle = FormBorderStyle.None
            Me.WindowState     = FormWindowState.Maximized
            Me.TopMost         = True
            Me.BackColor       = Color.Black
            Me.ShowInTaskbar   = False
            Me.KeyPreview      = True
            Me.DoubleBuffered  = True

            BuildLayout(message)
        End Sub

        Private Sub BuildLayout(message As String)
            ' ── Card panel (custom-painted with rounded corners) ────────────
            _pnlCard = New Panel() With {
                .Size      = New Size(CardW, 100),  ' height computed below
                .BackColor = Color.Transparent
            }
            AddHandler _pnlCard.Paint, AddressOf OnCardPaint

            Dim y = CardPadTop

            ' ── Accent badge icon ───────────────────────────────────────────
            _lblBadge = New Label() With {
                .Text      = "📢",
                .Font      = New Font("Segoe UI Emoji", 22),
                .BackColor = Color.Transparent,
                .AutoSize  = True
            }
            _lblBadge.Location = New Point((CardW - _lblBadge.PreferredWidth) \ 2, y)
            _pnlCard.Controls.Add(_lblBadge)
            y += _lblBadge.PreferredHeight + 12

            ' ── Title ───────────────────────────────────────────────────────
            _lblTitle = New Label() With {
                .Text      = "Shop Announcement",
                .Font      = New Font("Segoe UI", 18, FontStyle.Bold),
                .ForeColor = TextTitle,
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(CardW - CardPadX * 2, 32),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Location  = New Point(CardPadX, y)
            }
            _pnlCard.Controls.Add(_lblTitle)
            y += 32 + 8

            ' ── Divider line ────────────────────────────────────────────────
            Dim divider As New Panel() With {
                .Size      = New Size(CardW - CardPadX * 2, 1),
                .BackColor = Color.FromArgb(40, 120, 160, 220),
                .Location  = New Point(CardPadX, y)
            }
            _pnlCard.Controls.Add(divider)
            y += 1 + 20

            ' ── Body text ───────────────────────────────────────────────────
            _lblBody = New Label() With {
                .Text        = message,
                .Font        = New Font("Segoe UI", 13),
                .ForeColor   = TextBody,
                .BackColor   = Color.Transparent,
                .AutoSize    = True,
                .MaximumSize = New Size(CardW - CardPadX * 2, 0),
                .Location    = New Point(CardPadX, y)
            }
            _pnlCard.Controls.Add(_lblBody)
            y += Math.Max(_lblBody.PreferredHeight, 30) + 28

            ' ── Dismiss button ──────────────────────────────────────────────
            Dim btnW = 160
            Dim btnH = 42
            _btnDismiss = New Button() With {
                .Text      = "Got it",
                .Size      = New Size(btnW, btnH),
                .Font      = New Font("Segoe UI", 11, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = BtnBg,
                .FlatStyle = FlatStyle.Flat,
                .Cursor    = Cursors.Hand,
                .Location  = New Point((CardW - btnW) \ 2, y)
            }
            _btnDismiss.FlatAppearance.BorderSize = 0
            _btnDismiss.FlatAppearance.MouseOverBackColor = BtnHover
            AddHandler _btnDismiss.Click, Sub(s, e) Me.Close()
            _pnlCard.Controls.Add(_btnDismiss)
            y += btnH + CardPadTop

            ' ── Close icon (top-right of card) ──────────────────────────────
            _btnClose = New Label() With {
                .Text      = "✕",
                .Font      = New Font("Segoe UI", 12, FontStyle.Bold),
                .ForeColor = TextDim,
                .BackColor = Color.Transparent,
                .AutoSize  = True,
                .Cursor    = Cursors.Hand,
                .Location  = New Point(CardW - 36, 12)
            }
            AddHandler _btnClose.Click, Sub(s, e) Me.Close()
            AddHandler _btnClose.MouseEnter, Sub(s, e) _btnClose.ForeColor = Color.White
            AddHandler _btnClose.MouseLeave, Sub(s, e) _btnClose.ForeColor = TextDim
            _pnlCard.Controls.Add(_btnClose)

            ' Finalize card size
            _pnlCard.Size = New Size(CardW, y)

            Me.Controls.Add(_pnlCard)
        End Sub

        ' ── Custom card painting (rounded rect with accent top bar) ─────────

        Private Sub OnCardPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias

            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim r = CardRadius
            Using path = RoundedRectPath(rect, r)
                ' Card background
                Using br = New SolidBrush(CardBg)
                    g.FillPath(br, path)
                End Using
                ' Card border
                Using pen = New Pen(CardBorder, 1.2F)
                    g.DrawPath(pen, path)
                End Using
            End Using

            ' Top accent gradient bar
            Dim barRect = New Rectangle(r, 0, pnl.Width - r * 2, 3)
            Using br = New LinearGradientBrush(barRect, AccentBlue, AccentGold, 0F)
                g.FillRectangle(br, barRect)
            End Using
        End Sub

        Private Shared Function RoundedRectPath(rect As Rectangle, radius As Integer) As GraphicsPath
            Dim path = New GraphicsPath()
            Dim d = radius * 2
            path.AddArc(rect.X, rect.Y, d, d, 180, 90)
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
            path.CloseFigure()
            Return path
        End Function

        ' ── Background painting (dimmed overlay) ────────────────────────────

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            MyBase.OnPaint(e)
            Using br = New SolidBrush(BgDim)
                e.Graphics.FillRectangle(br, Me.ClientRectangle)
            End Using
        End Sub

        ' ── Layout: center card on screen ───────────────────────────────────

        Protected Overrides Sub OnLoad(e As EventArgs)
            MyBase.OnLoad(e)
            CenterCard()
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)
            CenterCard()
        End Sub

        Private Sub CenterCard()
            If _pnlCard Is Nothing Then Return
            _pnlCard.Location = New Point(
                (Me.ClientSize.Width - _pnlCard.Width) \ 2,
                (Me.ClientSize.Height - _pnlCard.Height) \ 2)
        End Sub

        ' ── Keyboard: Escape or Enter to dismiss ───────────────────────────

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            If e.KeyCode = Keys.Escape OrElse e.KeyCode = Keys.Enter Then Me.Close()
            MyBase.OnKeyDown(e)
        End Sub

    End Class

End Namespace
