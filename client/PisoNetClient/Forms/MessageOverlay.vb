Imports System.Windows.Forms
Imports System.Drawing
Imports PisoNetClient.Resources

Namespace Forms

    ''' <summary>
    ''' Fullscreen, always-on-top overlay used for three purposes:
    '''   1. Admin messages   — dismissed by the user (OK button).
    '''   2. Announcements    — dismissed by the user (OK button).
    '''   3. Shutdown/Restart — non-dismissible countdown (30 s), shows Cancel button
    '''                         that runs "shutdown /a" to abort.
    ''' </summary>
    Public Class MessageOverlay
        Inherits Form

        ' ── Shared UI ─────────────────────────────────────────────────────────
        Private _lblTitle   As Label
        Private _lblMessage As Label
        Private _btnOk      As Button
        Private _btnCancel  As Button   ' Only visible for shutdown/restart

        ' ── Countdown (shutdown / restart only) ───────────────────────────────
        Private _countdownTimer  As System.Timers.Timer
        Private _secondsLeft     As Integer = 30
        Private ReadOnly _isCountdown As Boolean
        Private ReadOnly _shutdownCmd As String  ' "shutdown /s /t 0" or "shutdown /r /t 0"

        ''' <summary>
        ''' Show a simple message or announcement.
        ''' </summary>
        Public Sub New(title As String, message As String)
            _isCountdown = False
            BuildLayout(title, message)
        End Sub

        ''' <summary>
        ''' Show a countdown overlay for shutdown or restart.
        ''' shutdownType must be "shutdown" or "restart".
        ''' </summary>
        Public Sub New(shutdownType As String)
            _isCountdown = True
            Dim friendly = If(shutdownType = "restart", "Restarting", "Shutting down")
            _shutdownCmd = If(shutdownType = "restart", "shutdown /r /t 0", "shutdown /s /t 0")
            BuildLayout($"PC {friendly}", $"PC {friendly} in 30 seconds…{vbCrLf}Save your work now.")
        End Sub

        Private Sub BuildLayout(title As String, message As String)
            Me.FormBorderStyle = FormBorderStyle.None
            Me.WindowState     = FormWindowState.Maximized
            Me.TopMost         = True
            Me.BackColor       = Color.FromArgb(10, 14, 26)
            Me.ForeColor       = Color.White
            Me.StartPosition   = FormStartPosition.CenterScreen
            Me.ShowInTaskbar   = False
            Me.KeyPreview      = True

            Dim panel As New TableLayoutPanel() With {
                .Dock        = DockStyle.Fill,
                .RowCount    = 1,
                .ColumnCount = 1,
                .Padding     = New Padding(40)
            }
            panel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))

            Dim inner As New FlowLayoutPanel() With {
                .FlowDirection = FlowDirection.TopDown,
                .Dock          = DockStyle.Fill,
                .Anchor        = AnchorStyles.None,
                .AutoSize      = True,
                .AutoSizeMode  = AutoSizeMode.GrowAndShrink,
                .WrapContents  = False,
                .Padding       = New Padding(0)
            }

            ' ── Logo ──────────────────────────────────────────────────────────
            Dim logoImg = LogoHelper.GetLogo(72, 72)
            If logoImg IsNot Nothing Then
                Dim pb As New PictureBox() With {
                    .Image    = logoImg,
                    .Size     = New Size(72, 72),
                    .SizeMode = PictureBoxSizeMode.Zoom,
                    .Margin   = New Padding(0, 0, 0, 12)
                }
                inner.Controls.Add(pb)
            End If

            ' ── Title ─────────────────────────────────────────────────────────
            _lblTitle = New Label() With {
                .Text      = title,
                .Font      = New Font("Segoe UI", 22, FontStyle.Bold),
                .ForeColor = Color.FromArgb(79, 142, 247),
                .AutoSize  = True,
                .Margin    = New Padding(0, 0, 0, 16)
            }

            ' ── Message ───────────────────────────────────────────────────────
            _lblMessage = New Label() With {
                .Text        = message,
                .Font        = New Font("Segoe UI", 14),
                .ForeColor   = Color.FromArgb(226, 232, 240),
                .AutoSize    = True,
                .MaximumSize = New Size(800, 0),
                .Margin      = New Padding(0, 0, 0, 32)
            }

            ' ── OK button (messages / announcements) ──────────────────────────
            _btnOk = New Button() With {
                .Text      = "OK",
                .Size      = New Size(120, 42),
                .BackColor = Color.FromArgb(79, 142, 247),
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat,
                .Visible   = Not _isCountdown
            }
            _btnOk.FlatAppearance.BorderSize = 0
            AddHandler _btnOk.Click, Sub(s, e) Me.Close()

            ' ── Cancel button (shutdown/restart countdown only) ────────────────
            _btnCancel = New Button() With {
                .Text      = "Cancel",
                .Size      = New Size(120, 42),
                .BackColor = Color.FromArgb(239, 68, 68),
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat,
                .Visible   = _isCountdown
            }
            _btnCancel.FlatAppearance.BorderSize = 0
            AddHandler _btnCancel.Click, AddressOf CancelShutdown

            inner.Controls.AddRange({_lblTitle, _lblMessage, _btnOk, _btnCancel})
            ' (logo PictureBox was already added above, before the title)
            panel.Controls.Add(inner, 0, 0)
            inner.Anchor = AnchorStyles.None
            Me.Controls.Add(panel)

            If _isCountdown Then StartCountdown()
        End Sub

        Private Sub StartCountdown()
            _secondsLeft = 30
            _countdownTimer = New System.Timers.Timer(1_000) With {.AutoReset = True}
            AddHandler _countdownTimer.Elapsed, AddressOf CountdownTick
            _countdownTimer.Start()
        End Sub

        Private Sub CountdownTick(sender As Object, e As System.Timers.ElapsedEventArgs)
            _secondsLeft -= 1
            Dim friendly = If(_shutdownCmd.Contains("/r"), "Restarting", "Shutting down")
            Dim newText = $"PC {friendly} in {_secondsLeft} seconds…{vbCrLf}Save your work now."

            If _secondsLeft <= 0 Then
                _countdownTimer?.Stop()
                Try
                    System.Diagnostics.Process.Start(New System.Diagnostics.ProcessStartInfo() With {
                        .FileName        = "cmd.exe",
                        .Arguments       = $"/c {_shutdownCmd}",
                        .CreateNoWindow  = True,
                        .UseShellExecute = False
                    })
                Catch
                End Try
                If Me.InvokeRequired Then Me.Invoke(Sub() Me.Close()) Else Me.Close()
                Return
            End If

            If Me.InvokeRequired Then
                Me.Invoke(Sub() _lblMessage.Text = newText)
            Else
                _lblMessage.Text = newText
            End If
        End Sub

        Private Sub CancelShutdown(sender As Object, e As EventArgs)
            _countdownTimer?.Stop()
            Try
                System.Diagnostics.Process.Start(New System.Diagnostics.ProcessStartInfo() With {
                    .FileName        = "cmd.exe",
                    .Arguments       = "/c shutdown /a",
                    .CreateNoWindow  = True,
                    .UseShellExecute = False
                })
            Catch
            End Try
            Me.Close()
        End Sub

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
