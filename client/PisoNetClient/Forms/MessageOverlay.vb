Imports System.Windows.Forms
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports PisoNetClient.Resources

Namespace Forms

    ''' <summary>
    ''' Fullscreen, always-on-top overlay used for:
    '''   1. Admin messages / Announcements — dismissed by the user (OK button).
    '''   2. Shutdown/Restart — non-dismissible countdown with large centered UI.
    ''' </summary>
    Public Class MessageOverlay
        Inherits Form

        ' ── Shared UI ─────────────────────────────────────────────────────────
        Private _lblTitle   As Label
        Private _lblMessage As Label
        Private _btnOk      As Button

        ' ── Countdown-specific UI ─────────────────────────────────────────────
        Private _lblCountdown    As Label      ' Big "30" number
        Private _lblCountdownSub As Label      ' "seconds remaining"
        Private _lblSaveWork     As Label      ' "Save your work now"
        Private _progressBar     As Panel      ' Shrinking bar
        Private _progressFill    As Panel

        ' ── Countdown state ───────────────────────────────────────────────────
        Private _countdownTimer  As System.Timers.Timer
        Private _secondsLeft     As Integer = 30
        Private ReadOnly _isCountdown As Boolean
        Private ReadOnly _shutdownArgs As String
        Private ReadOnly _isRestart As Boolean

        ' ── Colors ────────────────────────────────────────────────────────────
        Private Shared ReadOnly BgDark     As Color = Color.FromArgb(8, 10, 20)
        Private Shared ReadOnly AccentRed  As Color = Color.FromArgb(239, 68, 68)
        Private Shared ReadOnly AccentBlue As Color = Color.FromArgb(79, 142, 247)
        Private Shared ReadOnly TextDim    As Color = Color.FromArgb(148, 163, 184)
        Private Shared ReadOnly TextBright As Color = Color.FromArgb(241, 245, 249)

        ''' <summary>
        ''' Show a simple message or announcement.
        ''' </summary>
        Public Sub New(title As String, message As String)
            _isCountdown = False
            BuildMessageLayout(title, message)
        End Sub

        ''' <summary>
        ''' Show a countdown overlay for shutdown or restart.
        ''' shutdownType must be "shutdown" or "restart".
        ''' </summary>
        Public Sub New(shutdownType As String)
            _isCountdown = True
            _isRestart = (shutdownType = "restart")
            _shutdownArgs = If(_isRestart, "/r /f /t 0", "/s /f /t 0")
            BuildCountdownLayout()
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        '  Message / Announcement layout (simple centered card)
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub BuildMessageLayout(title As String, message As String)
            SetupForm()

            ' ── Background with subtle blue vignette ──────────────────────────
            Dim container As New Panel() With {.Dock = DockStyle.Fill, .BackColor = Color.Transparent}
            AddHandler container.Paint, Sub(s, e)
                Dim pnl = DirectCast(s, Panel)
                Dim cx = pnl.Width \ 2
                Dim cy2 = pnl.Height \ 2
                Dim radius = Math.Max(pnl.Width, pnl.Height)
                Using path = New GraphicsPath()
                    path.AddEllipse(cx - radius, cy2 - radius, radius * 2, radius * 2)
                    Using br = New PathGradientBrush(path)
                        br.CenterColor = Color.FromArgb(20, 30, 40, 80)
                        br.SurroundColors = {BgDark}
                        e.Graphics.FillRectangle(br, pnl.ClientRectangle)
                    End Using
                End Using
            End Sub
            Me.Controls.Add(container)

            ' ── Center card ───────────────────────────────────────────────────
            Dim cardWidth = 520
            Dim card As New Panel() With {
                .Size = New Size(cardWidth, 10),  ' height will auto-adjust
                .BackColor = Color.FromArgb(14, 17, 28)
            }
            container.Controls.Add(card)
            AddHandler card.Paint, AddressOf PaintCard

            Dim cy = 36
            Dim pad = 40

            ' ── Envelope icon circle ──────────────────────────────────────────
            Dim lblIcon = New Label() With {
                .Text = "✉",
                .Font = New Font("Segoe UI", 26),
                .ForeColor = AccentBlue,
                .TextAlign = ContentAlignment.MiddleCenter,
                .Size = New Size(72, 72),
                .Location = New Point((cardWidth - 72) \ 2, cy)
            }
            AddHandler lblIcon.Paint, Sub(s, e)
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
                Using pen = New Pen(Color.FromArgb(40, AccentBlue.R, AccentBlue.G, AccentBlue.B), 3)
                    e.Graphics.DrawEllipse(pen, 2, 2, 67, 67)
                End Using
            End Sub
            card.Controls.Add(lblIcon)
            cy += 84

            ' ── "FROM ADMIN" label ────────────────────────────────────────────
            Dim lblFrom = New Label() With {
                .Text = "MESSAGE FROM ADMIN",
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = TextDim,
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize = False,
                .Size = New Size(cardWidth, 18),
                .Location = New Point(0, cy)
            }
            card.Controls.Add(lblFrom)
            cy += 28

            ' ── Thin accent line ──────────────────────────────────────────────
            Dim line = New Panel() With {
                .Size = New Size(60, 2),
                .Location = New Point((cardWidth - 60) \ 2, cy),
                .BackColor = AccentBlue
            }
            card.Controls.Add(line)
            cy += 18

            ' ── Message text ──────────────────────────────────────────────────
            _lblMessage = New Label() With {
                .Text = message,
                .Font = New Font("Segoe UI", 14),
                .ForeColor = TextBright,
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize = True,
                .MaximumSize = New Size(cardWidth - (pad * 2), 0),
                .Location = New Point(pad, cy)
            }
            card.Controls.Add(_lblMessage)
            ' Defer final layout until message label is sized
            AddHandler _lblMessage.Resize, Sub(s, e)
                _lblMessage.Location = New Point((cardWidth - _lblMessage.Width) \ 2, _lblMessage.Top)
            End Sub
            cy += Math.Max(_lblMessage.PreferredHeight, 30) + 32

            ' ── Dismiss button ────────────────────────────────────────────────
            _btnOk = New Button() With {
                .Text = "Dismiss",
                .Size = New Size(160, 44),
                .Location = New Point((cardWidth - 160) \ 2, cy),
                .BackColor = AccentBlue,
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat,
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .Cursor = Cursors.Hand
            }
            _btnOk.FlatAppearance.BorderSize = 0
            AddHandler _btnOk.Click, Sub(s, e) Me.Close()
            card.Controls.Add(_btnOk)
            cy += 60

            ' ── Pisonex branding at bottom of card ────────────────────────────
            Dim lblBrand = New Label() With {
                .Text = "Pisonex",
                .Font = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(60, 70, 90),
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize = False,
                .Size = New Size(cardWidth, 16),
                .Location = New Point(0, cy)
            }
            card.Controls.Add(lblBrand)
            cy += 30

            ' ── Finalize card height and center ───────────────────────────────
            card.Size = New Size(cardWidth, cy)
            AddHandler container.Resize, Sub(s, e)
                card.Location = New Point(
                    (container.Width - card.Width) \ 2,
                    (container.Height - card.Height) \ 2)
            End Sub
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        '  Shutdown / Restart countdown layout
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub BuildCountdownLayout()
            SetupForm()

            Dim friendly = If(_isRestart, "RESTARTING", "SHUTTING DOWN")
            Dim icon     = If(_isRestart, "↻", "⏻")

            ' ── Compute DPI + screen-size scale factor ────────────────────────
            Dim dpiScale As Single = 1.0F
            Using g = Me.CreateGraphics()
                dpiScale = g.DpiX / 96.0F
            End Using
            Dim screenH = Screen.PrimaryScreen.WorkingArea.Height
            Dim screenW = Screen.PrimaryScreen.WorkingArea.Width
            ' Scale layout down on small screens (reference = 768p height)
            Dim sizeScale  = Math.Max(0.60F, Math.Min(1.0F, screenH / 768.0F))
            Dim layoutScale = Math.Max(0.60F, Math.Min(1.20F, dpiScale * sizeScale))
            Dim Sv = Function(n As Integer) CInt(Math.Round(n * layoutScale))

            ' Card width: at most 90 % of screen width, capped at 480 scaled
            Dim cardWidth = Math.Min(Sv(480), CInt(screenW * 0.90F))

            ' ── Main container ────────────────────────────────────────────────
            Dim container As New Panel() With {.Dock = DockStyle.Fill, .BackColor = Color.Transparent}
            AddHandler container.Paint, AddressOf PaintBackground
            Me.Controls.Add(container)

            ' ── Center card (dark glass panel) ────────────────────────────────
            Dim card As New Panel() With {.BackColor = Color.FromArgb(14, 17, 28)}
            container.Controls.Add(card)
            AddHandler card.Paint, AddressOf PaintCard

            ' We accumulate cy and set card height at the end so nothing overflows.
            Dim cy = Sv(32)

            ' ── Icon circle ───────────────────────────────────────────────────
            Dim iconColor = If(_isRestart, AccentBlue, AccentRed)
            Dim iconSize  = Sv(76)
            Dim lblIcon   = New Label() With {
                .Text      = icon,
                .Font      = New Font("Segoe UI", Math.Max(16.0F, 26.0F * layoutScale)),
                .ForeColor = iconColor,
                .TextAlign = ContentAlignment.MiddleCenter,
                .Size      = New Size(iconSize, iconSize),
                .Location  = New Point((cardWidth - iconSize) \ 2, cy)
            }
            AddHandler lblIcon.Paint, Sub(s, e)
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
                Using pen = New Pen(Color.FromArgb(40, iconColor.R, iconColor.G, iconColor.B), 3)
                    e.Graphics.DrawEllipse(pen, 2, 2, iconSize - 5, iconSize - 5)
                End Using
            End Sub
            card.Controls.Add(lblIcon)
            cy += iconSize + Sv(14)

            ' ── Title ─────────────────────────────────────────────────────────
            Dim titleH = Sv(26)
            _lblTitle = New Label() With {
                .Text      = friendly,
                .Font      = New Font("Segoe UI", Math.Max(9.0F, 13.0F * layoutScale), FontStyle.Bold),
                .ForeColor = TextDim,
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize  = False,
                .Size      = New Size(cardWidth, titleH),
                .Location  = New Point(0, cy)
            }
            card.Controls.Add(_lblTitle)
            cy += titleH + Sv(10)

            ' ── Big countdown number ──────────────────────────────────────────
            Dim countdownFontPt = Math.Max(28.0F, 64.0F * layoutScale)
            ' Allocate enough height for the rendered font (roughly 1.5× the pt size in pixels)
            Dim countdownH = CInt(Math.Ceiling(countdownFontPt * dpiScale * 1.55F))
            _lblCountdown = New Label() With {
                .Text      = "30",
                .Font      = New Font("Segoe UI", countdownFontPt, FontStyle.Bold),
                .ForeColor = TextBright,
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize  = False,
                .Size      = New Size(cardWidth, countdownH),
                .Location  = New Point(0, cy)
            }
            card.Controls.Add(_lblCountdown)
            cy += countdownH + Sv(6)

            ' ── "seconds remaining" ───────────────────────────────────────────
            Dim subH = Sv(24)
            _lblCountdownSub = New Label() With {
                .Text      = "seconds remaining",
                .Font      = New Font("Segoe UI", Math.Max(8.0F, 11.0F * layoutScale)),
                .ForeColor = TextDim,
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize  = False,
                .Size      = New Size(cardWidth, subH),
                .Location  = New Point(0, cy)
            }
            card.Controls.Add(_lblCountdownSub)
            cy += subH + Sv(16)

            ' ── Progress bar ──────────────────────────────────────────────────
            Dim barPad   = Sv(40)
            Dim barW     = Math.Max(Sv(60), cardWidth - barPad * 2)
            Dim barH     = Math.Max(4, Sv(6))
            _progressBar = New Panel() With {
                .Size      = New Size(barW, barH),
                .Location  = New Point(barPad, cy),
                .BackColor = Color.FromArgb(30, 34, 50)
            }
            _progressFill = New Panel() With {
                .Size      = New Size(barW, barH),
                .Location  = New Point(0, 0),
                .BackColor = If(_isRestart, AccentBlue, AccentRed)
            }
            AddHandler _progressFill.Paint, Sub(s, e)
                Dim pnl = DirectCast(s, Panel)
                If pnl.Width > 0 Then
                    Using br = New LinearGradientBrush(
                        New Point(0, 0), New Point(pnl.Width, 0),
                        If(_isRestart, AccentBlue, AccentRed),
                        If(_isRestart, Color.FromArgb(56, 189, 248), Color.FromArgb(251, 146, 60)))
                        e.Graphics.FillRectangle(br, 0, 0, pnl.Width, pnl.Height)
                    End Using
                End If
            End Sub
            _progressBar.Controls.Add(_progressFill)
            card.Controls.Add(_progressBar)
            cy += barH + Sv(14)

            ' ── "Save your work now" ──────────────────────────────────────────
            Dim saveH = Sv(22)
            _lblSaveWork = New Label() With {
                .Text      = "Preparing for shutdown...",
                .Font      = New Font("Segoe UI", Math.Max(7.5F, 10.0F * layoutScale)),
                .ForeColor = If(_isRestart, AccentBlue, AccentRed),
                .TextAlign = ContentAlignment.MiddleCenter,
                .AutoSize  = False,
                .Size      = New Size(cardWidth, saveH),
                .Location  = New Point(0, cy)
            }
            card.Controls.Add(_lblSaveWork)
            cy += saveH + Sv(28)   ' bottom padding

            ' ── Finalise card size and centre it ──────────────────────────────
            card.Size = New Size(cardWidth, cy)
            ' Clamp card to screen if it somehow still exceeds it
            Dim maxCardH = CInt(screenH * 0.92F)
            If card.Height > maxCardH Then card.Size = New Size(card.Width, maxCardH)

            AddHandler container.Resize, Sub(s, e)
                card.Location = New Point(
                    (container.Width  - card.Width)  \ 2,
                    (container.Height - card.Height) \ 2)
            End Sub

            StartCountdown()
        End Sub

        Private Sub SetupForm()
            Me.FormBorderStyle = FormBorderStyle.None
            Me.WindowState = FormWindowState.Maximized
            Me.TopMost = True
            Me.BackColor = BgDark
            Me.ForeColor = Color.White
            Me.StartPosition = FormStartPosition.CenterScreen
            Me.ShowInTaskbar = False
            Me.KeyPreview = True
            Me.DoubleBuffered = True
        End Sub

        Private Sub PaintBackground(sender As Object, e As PaintEventArgs)
            ' Subtle radial vignette
            Dim pnl = DirectCast(sender, Panel)
            Dim cx = pnl.Width \ 2
            Dim cy = pnl.Height \ 2
            Dim radius = Math.Max(pnl.Width, pnl.Height)
            Using path = New GraphicsPath()
                path.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2)
                Using br = New PathGradientBrush(path)
                    br.CenterColor = Color.FromArgb(20, If(_isRestart, 30, 40), If(_isRestart, 40, 20), If(_isRestart, 60, 30))
                    br.SurroundColors = {BgDark}
                    e.Graphics.FillRectangle(br, pnl.ClientRectangle)
                End Using
            End Using
        End Sub

        Private Sub PaintCard(sender As Object, e As PaintEventArgs)
            Dim pnl = DirectCast(sender, Panel)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)
            Dim borderColor = Color.FromArgb(40, 255, 255, 255)
            Using pen = New Pen(borderColor, 1)
                Dim r = 16
                Using gp = RoundRect(rect, r)
                    e.Graphics.DrawPath(pen, gp)
                End Using
            End Using
        End Sub

        Private Shared Function RoundRect(rect As Rectangle, r As Integer) As GraphicsPath
            Dim gp = New GraphicsPath()
            Dim d = r * 2
            gp.AddArc(rect.X, rect.Y, d, d, 180, 90)
            gp.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
            gp.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
            gp.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
            gp.CloseFigure()
            Return gp
        End Function

        ' ══════════════════════════════════════════════════════════════════════
        '  Countdown logic
        ' ══════════════════════════════════════════════════════════════════════

        Private Sub StartCountdown()
            _secondsLeft = 30
            _countdownTimer = New System.Timers.Timer(1_000) With {.AutoReset = True}
            AddHandler _countdownTimer.Elapsed, AddressOf CountdownTick
            _countdownTimer.Start()
        End Sub

        Private Sub CountdownTick(sender As Object, e As System.Timers.ElapsedEventArgs)
            _secondsLeft -= 1

            If _secondsLeft <= 0 Then
                _countdownTimer?.Stop()
                Try
                    ' Call shutdown.exe directly rather than via cmd.exe — the
                    ' "Command Prompt has been disabled by your administrator"
                    ' Group Policy (DisableCMD) blocks any invocation of cmd.exe,
                    ' including /c, silently killing this shutdown/restart call.
                    ' DisableCMD only restricts cmd.exe, not other executables.
                    System.Diagnostics.Process.Start(New System.Diagnostics.ProcessStartInfo() With {
                        .FileName = "shutdown.exe",
                        .Arguments = _shutdownArgs,
                        .CreateNoWindow = True,
                        .UseShellExecute = False
                    })
                Catch
                End Try
                If Me.InvokeRequired Then Me.Invoke(Sub() Me.Close()) Else Me.Close()
                Return
            End If

            If Me.InvokeRequired Then
                Me.Invoke(Sub() UpdateCountdownUI())
            Else
                UpdateCountdownUI()
            End If
        End Sub

        Private Sub UpdateCountdownUI()
            If _lblCountdown IsNot Nothing Then
                _lblCountdown.Text = _secondsLeft.ToString()
            End If

            ' Update progress bar
            If _progressFill IsNot Nothing AndAlso _progressBar IsNot Nothing Then
                Dim pct = _secondsLeft / 30.0
                _progressFill.Width = CInt(_progressBar.Width * pct)
                _progressFill.Invalidate()
            End If

            ' Urgency: change color in last 10 seconds
            If _secondsLeft <= 10 AndAlso Not _isRestart Then
                _lblCountdown.ForeColor = AccentRed
                _lblSaveWork.Text = "Shutting down soon!"
            ElseIf _secondsLeft <= 10 AndAlso _isRestart Then
                _lblCountdown.ForeColor = AccentBlue
                _lblSaveWork.Text = "Restarting soon!"
            End If
        End Sub

        ' ══════════════════════════════════════════════════════════════════════
        '  Overrides
        ' ══════════════════════════════════════════════════════════════════════

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            If e.KeyCode = Keys.Escape AndAlso Not _isCountdown Then Me.Close()
            MyBase.OnKeyDown(e)
        End Sub

        Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
            _countdownTimer?.Stop()
            _countdownTimer?.Dispose()
            MyBase.OnFormClosed(e)
        End Sub

    End Class

End Namespace
