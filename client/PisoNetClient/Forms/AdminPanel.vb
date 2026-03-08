Imports System.Windows.Forms
Imports System.Drawing
Imports System.IO
Imports PisoNetClient.Config
Imports PisoNetClient.Services

Namespace Forms

    ''' <summary>
    ''' Admin configuration panel — accessible only after entering the correct PIN.
    ''' </summary>
    Public Class AdminPanel
        Inherits Form

        Public Event ExitRequested()

        ' ── Controls ──────────────────────────────────────────────────────────
        Private _txtUrl         As TextBox
        Private _nudPcNum       As NumericUpDown
        Private _picColor       As PictureBox
        Private _txtImgPath     As TextBox
        Private _txtMsg         As TextBox
        Private _chkTasks       As CheckBox
        Private _chkCmd         As CheckBox
        Private _chkReg         As CheckBox
        Private _chkRun         As CheckBox
        Private _chkWarn5       As CheckBox
        Private _chkWarn1       As CheckBox
        Private _chkCapture     As CheckBox
        Private _nudInterval    As NumericUpDown
        Private _nudQuality     As NumericUpDown
        Private _txtPin         As TextBox
        Private _txtPin2        As TextBox
        Private _chkNotifs      As CheckBox
        Private _chkVoice       As CheckBox
        Private _nudVolume      As NumericUpDown
        Private _currentBgColor As Color

        ' Layout — wider so all labels have room
        Private Const W   As Integer = 720
        Private Const TW  As Integer = 680
        Private Const IW  As Integer = 644
        Private Const LM  As Integer = 14

        Public Sub New()
            _currentBgColor = Color.FromArgb(AppConfig.LockBgArgb)
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.Text            = "PisoNet Admin Panel"
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.Size            = New Size(W, 600)
            Me.StartPosition   = FormStartPosition.CenterScreen
            Me.MaximizeBox     = False
            Me.MinimizeBox     = False
            Me.BackColor       = Color.FromArgb(12, 16, 28)
            Me.ForeColor       = Color.White
            Me.Font            = New Font("Segoe UI", 9)

            Dim tabs = New TabControl() With {
                .Location   = New Point(14, 14),
                .Size       = New Size(TW, 480),
                .Appearance = TabAppearance.Normal,
                .Font       = New Font("Segoe UI", 9)
            }
            Me.Controls.Add(tabs)

            tabs.TabPages.Add(BuildConnectionTab())
            tabs.TabPages.Add(BuildLockScreenTab())
            tabs.TabPages.Add(BuildRestrictionsTab())
            tabs.TabPages.Add(BuildSecurityTab())
            tabs.TabPages.Add(BuildNotificationsTab())

            Dim btnSave  = MakeBtn("Save & Apply",     New Point(14,  510), Color.FromArgb(59, 130, 246))
            Dim btnExit  = MakeBtn("Exit Application", New Point(176, 510), Color.FromArgb(220, 38, 38))
            Dim btnClose = MakeBtn("Close",            New Point(560, 510), Color.FromArgb(42, 46, 64))

            AddHandler btnSave.Click,  AddressOf OnSave
            AddHandler btnExit.Click,  AddressOf OnExitApp
            AddHandler btnClose.Click, Sub(s, e) Me.Close()

            Me.Controls.AddRange({btnSave, btnExit, btnClose})
        End Sub

        ' ── Tab builders ──────────────────────────────────────────────────────

        Private Function BuildConnectionTab() As TabPage
            Dim tab = DarkTab("  Connection")
            Dim y = 18

            tab.Controls.Add(SectionLabel("Server URL", New Point(LM, y))) : y += 22
            _txtUrl = DarkTextBox(New Point(LM, y), IW, AppConfig.ServerUrl)
            tab.Controls.Add(_txtUrl) : y += 38

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            tab.Controls.Add(SectionLabel("PC Number", New Point(LM, y))) : y += 22
            _nudPcNum = DarkNud(New Point(LM, y), 80, AppConfig.PCNumber, 1, 99)
            tab.Controls.Add(_nudPcNum) : y += 46

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14
            tab.Controls.Add(InfoLabel(
                "Server URL and PC Number take effect after restarting the client.",
                New Point(LM, y)))

            Return tab
        End Function

        Private Function BuildLockScreenTab() As TabPage
            Dim tab = DarkTab("  Lock Screen")
            Dim y = 18

            tab.Controls.Add(SectionLabel("Background Color", New Point(LM, y))) : y += 22
            _picColor = New PictureBox() With {
                .Location    = New Point(LM, y),
                .Size        = New Size(52, 30),
                .BackColor   = _currentBgColor,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor      = Cursors.Hand
            }
            AddHandler _picColor.Click, AddressOf OnPickColor
            tab.Controls.Add(_picColor)
            tab.Controls.Add(InfoLabel("← Click swatch to open color picker", New Point(LM + 62, y + 7)))
            y += 46

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            tab.Controls.Add(SectionLabel("Background Image", New Point(LM, y)))
            tab.Controls.Add(InfoLabel("(overrides color when set)", New Point(LM + 148, y + 3)))
            y += 22
            _txtImgPath = DarkTextBox(New Point(LM, y), IW - 102, AppConfig.LockBgImagePath)
            tab.Controls.Add(_txtImgPath)

            Dim btnBrowse = MakeBtn("Browse...", New Point(LM + IW - 96, y - 1), Color.FromArgb(42, 46, 64))
            btnBrowse.Size = New Size(92, 28)
            AddHandler btnBrowse.Click, AddressOf OnBrowseImage
            tab.Controls.Add(btnBrowse) : y += 36

            Dim btnClear = MakeBtn("Clear Image", New Point(LM, y), Color.FromArgb(42, 46, 64))
            btnClear.Size = New Size(106, 26)
            AddHandler btnClear.Click, Sub(s, e) _txtImgPath.Text = ""
            tab.Controls.Add(btnClear) : y += 44

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            tab.Controls.Add(SectionLabel("Lock Screen Message", New Point(LM, y))) : y += 22
            _txtMsg = DarkTextBox(New Point(LM, y), IW, AppConfig.LockMessage)
            tab.Controls.Add(_txtMsg)

            Return tab
        End Function

        Private Function BuildRestrictionsTab() As TabPage
            Dim tab = DarkTab("  Restrictions")
            Dim y = 18

            tab.Controls.Add(InfoLabel(
                "The following restrictions apply to all users on this PC while the client is running." &
                " They are automatically removed when the client exits.",
                New Point(LM, y))) : y += 44

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14
            tab.Controls.Add(SectionLabel("Windows Restrictions", New Point(LM, y))) : y += 26

            _chkTasks = DarkCheck("Disable Task Manager",       New Point(LM, y), AppConfig.DisableTaskManager)   : tab.Controls.Add(_chkTasks) : y += 30
            _chkCmd   = DarkCheck("Disable Command Prompt",     New Point(LM, y), AppConfig.DisableCmdPrompt)     : tab.Controls.Add(_chkCmd)   : y += 30
            _chkReg   = DarkCheck("Disable Registry Editor",    New Point(LM, y), AppConfig.DisableRegistryTools) : tab.Controls.Add(_chkReg)   : y += 30
            _chkRun   = DarkCheck("Disable Run Dialog (Win+R)", New Point(LM, y), AppConfig.DisableRunDialog)     : tab.Controls.Add(_chkRun)   : y += 40

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14
            tab.Controls.Add(InfoLabel(
                "Task Manager and Registry Editor changes take effect immediately." &
                " CMD and Run Dialog require reopening a new shell.",
                New Point(LM, y)))

            Return tab
        End Function

        Private Function BuildSecurityTab() As TabPage
            Dim tab = DarkTab("  Security")
            Dim y = 18

            ' ── Admin PIN ────────────────────────────────────────────────────
            tab.Controls.Add(SectionLabel("Change Admin PIN", New Point(LM, y))) : y += 24
            tab.Controls.Add(InfoLabel("Leave blank to keep the current PIN.", New Point(LM, y))) : y += 26

            Dim col2 = LM + 224
            tab.Controls.Add(SmallLabel("New PIN",     New Point(LM,   y)))
            tab.Controls.Add(SmallLabel("Confirm PIN", New Point(col2, y))) : y += 18
            _txtPin  = DarkTextBox(New Point(LM,   y), 200, "", pwChar:="●"c) : tab.Controls.Add(_txtPin)
            _txtPin2 = DarkTextBox(New Point(col2, y), 200, "", pwChar:="●"c) : tab.Controls.Add(_txtPin2)
            y += 42

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            ' ── Low-time warnings ────────────────────────────────────────────
            tab.Controls.Add(SectionLabel("Low-Time Warnings", New Point(LM, y)))
            tab.Controls.Add(InfoLabel("(tray balloon notification)", New Point(LM + 160, y + 3))) : y += 26
            _chkWarn5 = DarkCheck("Warn at 5 minutes remaining", New Point(LM, y), AppConfig.WarnAt5Min) : tab.Controls.Add(_chkWarn5) : y += 28
            _chkWarn1 = DarkCheck("Warn at 1 minute remaining",  New Point(LM, y), AppConfig.WarnAt1Min) : tab.Controls.Add(_chkWarn1) : y += 38

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            ' ── Screen monitoring ────────────────────────────────────────────
            tab.Controls.Add(SectionLabel("Screen Monitoring", New Point(LM, y))) : y += 26
            _chkCapture = DarkCheck(
                "Enable remote screen capture (uploads screenshots to admin dashboard)",
                New Point(LM, y), AppConfig.ScreenCaptureEnabled)
            tab.Controls.Add(_chkCapture) : y += 32

            Dim col3 = LM + 160
            tab.Controls.Add(SmallLabel("Interval (sec)",       New Point(LM,   y)))
            tab.Controls.Add(SmallLabel("JPEG Quality (30–95)", New Point(col3, y))) : y += 18

            _nudInterval = DarkNud(New Point(LM,   y), 80, AppConfig.ScreenCaptureIntervalSec, 3, 60)
            _nudQuality  = DarkNud(New Point(col3, y), 80, AppConfig.ScreenCaptureQuality,     30, 95)
            tab.Controls.Add(_nudInterval)
            tab.Controls.Add(_nudQuality) : y += 30

            tab.Controls.Add(InfoLabel(
                "Interval takes effect after restart.  Quality applies immediately." &
                " Higher quality = clearer image but larger upload size.",
                New Point(LM, y)))

            Return tab
        End Function

        Private Function BuildNotificationsTab() As TabPage
            Dim tab = DarkTab("  Notifications")
            Dim y = 18

            ' ── On-screen toasts ─────────────────────────────────────────────
            tab.Controls.Add(SectionLabel("On-Screen Notifications", New Point(LM, y))) : y += 26
            _chkNotifs = DarkCheck(
                "Show animated toast notifications for time added and low-time warnings",
                New Point(LM, y), AppConfig.NotificationsEnabled)
            tab.Controls.Add(_chkNotifs) : y += 32
            tab.Controls.Add(InfoLabel(
                "Toasts slide in from the right edge of the screen and auto-dismiss after 4 seconds." &
                " Click a toast to dismiss it early.",
                New Point(LM, y))) : y += 44

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            ' ── Voice announcements ───────────────────────────────────────────
            tab.Controls.Add(SectionLabel("Voice Announcements", New Point(LM, y))) : y += 26
            _chkVoice = DarkCheck(
                "Read notifications aloud using Windows text-to-speech (SAPI)",
                New Point(LM, y), AppConfig.VoiceEnabled)
            tab.Controls.Add(_chkVoice) : y += 32

            tab.Controls.Add(SmallLabel("Volume (10–100)", New Point(LM, y))) : y += 18
            _nudVolume = DarkNud(New Point(LM, y), 80, AppConfig.VoiceVolume, 10, 100)
            tab.Controls.Add(_nudVolume) : y += 36

            tab.Controls.Add(InfoLabel(
                "Voice works even when visual toasts are disabled — useful for players" &
                " focused on full-screen games who cannot see on-screen popups.",
                New Point(LM, y))) : y += 44

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            ' ── Timer overlay hint ────────────────────────────────────────────
            tab.Controls.Add(SectionLabel("Timer Overlay", New Point(LM, y))) : y += 26
            tab.Controls.Add(InfoLabel(
                "Right-click the timer to hide it or reset its position." &
                " It reappears automatically when a new session starts.",
                New Point(LM, y)))

            Return tab
        End Function

        ' ── Handlers ──────────────────────────────────────────────────────────

        Private Sub OnPickColor(sender As Object, e As EventArgs)
            Dim dlg = New ColorDialog() With {.Color = _currentBgColor, .FullOpen = True}
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _currentBgColor     = dlg.Color
                _picColor.BackColor = dlg.Color
            End If
        End Sub

        Private Sub OnBrowseImage(sender As Object, e As EventArgs)
            Dim dlg = New OpenFileDialog() With {
                .Title  = "Select Lock Screen Background",
                .Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files|*.*"
            }
            If Not String.IsNullOrEmpty(_txtImgPath.Text) AndAlso File.Exists(_txtImgPath.Text) Then
                dlg.FileName = _txtImgPath.Text
            End If
            If dlg.ShowDialog(Me) = DialogResult.OK Then _txtImgPath.Text = dlg.FileName
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            Dim newPin  = _txtPin.Text.Trim()
            Dim newPin2 = _txtPin2.Text.Trim()
            If newPin.Length > 0 Then
                If newPin.Length < 4 Then
                    MessageBox.Show("New PIN must be at least 4 digits.", "Validation",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning) : Return
                End If
                If Not newPin.All(Function(c) Char.IsDigit(c)) Then
                    MessageBox.Show("PIN must contain digits only.", "Validation",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning) : Return
                End If
                If newPin <> newPin2 Then
                    MessageBox.Show("PINs do not match.", "Validation",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning) : Return
                End If
                AppConfig.SaveAdminPin(newPin)
            End If

            AppConfig.SaveServerUrl(_txtUrl.Text.Trim())
            AppConfig.SavePCNumber(CInt(_nudPcNum.Value))
            AppConfig.SaveLockBgArgb(_currentBgColor.ToArgb())
            AppConfig.SaveLockBgImagePath(_txtImgPath.Text.Trim())
            AppConfig.SaveLockMessage(_txtMsg.Text.Trim())
            AppConfig.SaveDisableTaskManager(_chkTasks.Checked)
            AppConfig.SaveDisableCmdPrompt(_chkCmd.Checked)
            AppConfig.SaveDisableRegistryTools(_chkReg.Checked)
            AppConfig.SaveDisableRunDialog(_chkRun.Checked)
            AppConfig.SaveWarnAt5Min(_chkWarn5.Checked)
            AppConfig.SaveWarnAt1Min(_chkWarn1.Checked)
            AppConfig.SaveScreenCaptureEnabled(_chkCapture.Checked)
            AppConfig.SaveScreenCaptureIntervalSec(CInt(_nudInterval.Value))
            AppConfig.SaveScreenCaptureQuality(CInt(_nudQuality.Value))
            AppConfig.SaveNotificationsEnabled(_chkNotifs.Checked)
            AppConfig.SaveVoiceEnabled(_chkVoice.Checked)
            AppConfig.SaveVoiceVolume(CInt(_nudVolume.Value))

            WindowsPolicy.Apply()

            MessageBox.Show("Settings saved." & vbCrLf &
                            "Note: Server URL and PC Number require a client restart.",
                            "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

        Private Sub OnExitApp(sender As Object, e As EventArgs)
            Dim result = MessageBox.Show(
                "Exit PisoNet Client?" & vbCrLf &
                "Windows restrictions will be removed and the lock screen will close.",
                "Exit Application", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If result = DialogResult.Yes Then RaiseEvent ExitRequested()
        End Sub

        ' ── UI factory helpers ────────────────────────────────────────────────

        Private Shared Function DarkTab(title As String) As TabPage
            Return New TabPage(title) With {
                .BackColor = Color.FromArgb(18, 22, 36),
                .ForeColor = Color.White
            }
        End Function

        Private Shared Function SectionLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text      = text,
                .AutoSize  = True,
                .Location  = loc,
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.FromArgb(99, 162, 255)
            }
        End Function

        Private Shared Function InfoLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text      = text,
                .AutoSize  = False,
                .Size      = New Size(IW, 40),
                .Location  = loc,
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(94, 110, 140)
            }
        End Function

        Private Shared Function SmallLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text      = text,
                .AutoSize  = True,
                .Location  = loc,
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(180, 190, 210)
            }
        End Function

        Private Shared Function Rule(loc As Point, width As Integer) As Panel
            Return New Panel() With {
                .Location  = loc,
                .Size      = New Size(width, 1),
                .BackColor = Color.FromArgb(38, 42, 60)
            }
        End Function

        Private Shared Function DarkTextBox(loc As Point, width As Integer, text As String,
                                            Optional pwChar As Char = Nothing) As TextBox
            Dim tb = New TextBox() With {
                .Text        = text,
                .Location    = loc,
                .Width       = width,
                .BackColor   = Color.FromArgb(24, 28, 44),
                .ForeColor   = Color.White,
                .BorderStyle = BorderStyle.FixedSingle,
                .Font        = New Font("Segoe UI", 9)
            }
            If pwChar <> Nothing Then tb.PasswordChar = pwChar
            Return tb
        End Function

        Private Shared Function DarkNud(loc As Point, width As Integer,
                                        value As Integer, min As Integer, max As Integer) As NumericUpDown
            Return New NumericUpDown() With {
                .Minimum   = min,
                .Maximum   = max,
                .Value     = Math.Max(min, Math.Min(max, value)),
                .Location  = loc,
                .Width     = width,
                .BackColor = Color.FromArgb(24, 28, 44),
                .ForeColor = Color.White,
                .Font      = New Font("Segoe UI", 9)
            }
        End Function

        Private Shared Function DarkCheck(text As String, loc As Point, isChecked As Boolean) As CheckBox
            Return New CheckBox() With {
                .Text      = text,
                .Checked   = isChecked,
                .Location  = loc,
                .AutoSize  = True,
                .ForeColor = Color.FromArgb(220, 228, 240),
                .FlatStyle = FlatStyle.Flat,
                .Font      = New Font("Segoe UI", 9)
            }
        End Function

        Private Shared Function MakeBtn(text As String, loc As Point, bgColor As Color) As Button
            Dim b = New Button() With {
                .Text      = text,
                .Location  = loc,
                .Size      = New Size(152, 36),
                .BackColor = bgColor,
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat,
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .Cursor    = Cursors.Hand
            }
            b.FlatAppearance.BorderSize = 0
            Return b
        End Function

    End Class

End Namespace
