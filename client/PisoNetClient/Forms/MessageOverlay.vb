Imports System.Windows.Forms
Imports System.Drawing
Imports System.Timers

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
        Private ReadOnly _lblTitle   As Label
        Private ReadOnly _lblMessage As Label
        Private ReadOnly _btnOk      As Button
        Private ReadOnly _btnCancel  As Button   ' Only visible for shutdown/restart

        ' ── Countdown (shutdown / restart only) ───────────────────────────────
        Private _countdownTimer  As Timer
        Private _secondsLeft     As Integer = 30
        Private ReadOnly _isCountdown As Boolean
        Private ReadOnly _shutdownCmd As String  ' "shutdown /s /t 0" or "shutdown /r /t 0"

        ''' <summary>
        ''' Show a simple message or announcement.
        ''' </summary>
        Public Sub New(title As String, message As String)
            _isCountdown = False
            InitLayout(title, message)
        End Sub

        ''' <summary>
        ''' Show a countdown overlay for shutdown or restart.
        ''' shutdownType must be "shutdown" or "restart".
        ''' </summary>
        Public Sub New(shutdownType As String)
            _isCountdown = True
            Dim friendly = If(shutdownType = "restart", "Restarting", "Shutting down")
            _shutdownCmd = If(shutdownType = "restart", "shutdown /r /t 0", "shutdown /s /t 0")
            InitLayout($"PC {friendly}", $"PC {friendly} in 30 seconds…{vbCrLf}Save your work now.")
        End Sub

        Private Sub InitLayout(title As String, message As String)
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
                .Text      = message,
                .Font      = New Font("Segoe UI", 14),
                .ForeColor = Color.FromArgb(226, 232, 240),
                .AutoSize  = True,
                .MaximumSize = New Size(800, 0),
                .Margin    = New Padding(0, 0, 0, 32)
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
            AddHandler _btnCancel.Click, AddressOf OnCancelShutdown

            inner.Controls.AddRange({_lblTitle, _lblMessage, _btnOk, _btnCancel})
            panel.Controls.Add(inner, 0, 0)
            panel.SetChildIndex(inner, 0)
            inner.Anchor = AnchorStyles.None
            Me.Controls.Add(panel)

            ' Start countdown for shutdown/restart
            If _isCountdown Then StartCountdown()

            AddHandler Me.KeyDown, AddressOf OnKeyDown
        End Sub

        Private Sub StartCountdown()
            _secondsLeft = 30
            _countdownTimer = New Timer(1_000) With {.AutoReset = True}
            AddHandler _countdownTimer.Elapsed, AddressOf OnCountdownTick
            _countdownTimer.Start()
        End Sub

        Private Sub OnCountdownTick(sender As Object, e As ElapsedEventArgs)
            _secondsLeft -= 1
            Dim friendly = If(_shutdownCmd.Contains("/r"), "Restarting", "Shutting down")
            Dim newText = $"PC {friendly} in {_secondsLeft} seconds…{vbCrLf}Save your work now."

            If _secondsLeft <= 0 Then
                _countdownTimer?.Stop()
                ' Execute the actual system command
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

        Private Sub OnCancelShutdown(sender As Object, e As EventArgs)
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

        Private Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            ' Allow Escape to dismiss message/announcement overlays only
            If e.KeyCode = Keys.Escape AndAlso Not _isCountdown Then Me.Close()
        End Sub

        Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
            _countdownTimer?.Stop()
            _countdownTimer?.Dispose()
            MyBase.OnFormClosed(e)
        End Sub

    End Class

End Namespace
