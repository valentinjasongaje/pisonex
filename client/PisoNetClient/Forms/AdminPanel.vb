Imports System.Windows.Forms
Imports System.Drawing
Imports System.IO
Imports PisoNetClient.Config
Imports PisoNetClient.Services

Namespace Forms

    ''' <summary>
    ''' Admin configuration panel — accessible only after entering the correct PIN.
    ''' Provides: connection settings, lock-screen appearance, Windows restrictions,
    ''' warning preferences, PIN change, and application exit.
    ''' </summary>
    Public Class AdminPanel
        Inherits Form

        ' Raised when the admin chooses to exit the application
        Public Event ExitRequested()

        ' ── Controls referenced across methods ────────────────────────────
        Private _txtUrl      As TextBox
        Private _nudPcNum    As NumericUpDown
        Private _picColor    As PictureBox
        Private _txtImgPath  As TextBox
        Private _txtMsg      As TextBox
        Private _chkTasks    As CheckBox
        Private _chkCmd      As CheckBox
        Private _chkReg      As CheckBox
        Private _chkRun      As CheckBox
        Private _chkWarn5    As CheckBox
        Private _chkWarn1    As CheckBox
        Private _txtPin      As TextBox
        Private _txtPin2     As TextBox
        Private _currentBgColor As Color

        Public Sub New()
            _currentBgColor = Color.FromArgb(AppConfig.LockBgArgb)
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.Text            = "PisoNet Admin Panel"
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.Size            = New Size(500, 520)
            Me.StartPosition   = FormStartPosition.CenterScreen
            Me.MaximizeBox     = False
            Me.MinimizeBox     = False
            Me.BackColor       = Color.FromArgb(15, 20, 35)
            Me.ForeColor       = Color.White

            ' ── Tab control ───────────────────────────────────────────────
            Dim tabs = New TabControl() With {
                .Location  = New Point(12, 12),
                .Size      = New Size(462, 400),
                .Appearance = TabAppearance.Normal
            }
            Me.Controls.Add(tabs)

            tabs.TabPages.Add(BuildConnectionTab())
            tabs.TabPages.Add(BuildLockScreenTab())
            tabs.TabPages.Add(BuildRestrictionsTab())
            tabs.TabPages.Add(BuildSecurityTab())

            ' ── Bottom buttons ────────────────────────────────────────────
            Dim btnSave = MakeBtn("Save & Apply", New Point(12, 424), Color.FromArgb(79, 142, 247))
            AddHandler btnSave.Click, AddressOf OnSave
            Me.Controls.Add(btnSave)

            Dim btnExit = MakeBtn("Exit Application", New Point(166, 424), Color.FromArgb(239, 68, 68))
            AddHandler btnExit.Click, AddressOf OnExitApp
            Me.Controls.Add(btnExit)

            Dim btnClose = MakeBtn("Close", New Point(380, 424), Color.FromArgb(42, 45, 62))
            AddHandler btnClose.Click, Sub(s, e) Me.Close()
            Me.Controls.Add(btnClose)
        End Sub

        ' ── Tab builders ──────────────────────────────────────────────────

        Private Function BuildConnectionTab() As TabPage
            Dim tab = DarkTab("Connection")
            Dim y = 16

            tab.Controls.Add(SectionLabel("Server URL", New Point(12, y))) : y += 20
            _txtUrl = DarkTextBox(New Point(12, y), 420, AppConfig.ServerUrl) : tab.Controls.Add(_txtUrl) : y += 36

            tab.Controls.Add(SectionLabel("PC Number", New Point(12, y))) : y += 20
            _nudPcNum = New NumericUpDown() With {
                .Minimum = 1, .Maximum = 99,
                .Value = AppConfig.PCNumber,
                .Location = New Point(12, y), .Width = 80,
                .BackColor = Color.FromArgb(26, 30, 45), .ForeColor = Color.White
            }
            tab.Controls.Add(_nudPcNum) : y += 40

            tab.Controls.Add(DimLabel("Changing these settings requires restarting the client to take effect.", New Point(12, y)))
            Return tab
        End Function

        Private Function BuildLockScreenTab() As TabPage
            Dim tab = DarkTab("Lock Screen")
            Dim y = 16

            ' Background color
            tab.Controls.Add(SectionLabel("Background Color", New Point(12, y))) : y += 20
            _picColor = New PictureBox() With {
                .Location = New Point(12, y),
                .Size = New Size(48, 28),
                .BackColor = _currentBgColor,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor = Cursors.Hand
            }
            AddHandler _picColor.Click, AddressOf OnPickColor
            tab.Controls.Add(_picColor)
            tab.Controls.Add(DimLabel("← Click to change", New Point(68, y + 6))) : y += 44

            ' Background image
            tab.Controls.Add(SectionLabel("Background Image  (overrides color when set)", New Point(12, y))) : y += 20
            _txtImgPath = DarkTextBox(New Point(12, y), 330, AppConfig.LockBgImagePath)
            tab.Controls.Add(_txtImgPath)

            Dim btnBrowse = MakeBtn("Browse...", New Point(350, y - 2), Color.FromArgb(42, 45, 62))
            btnBrowse.Size = New Size(82, 28)
            AddHandler btnBrowse.Click, AddressOf OnBrowseImage
            tab.Controls.Add(btnBrowse) : y += 42

            Dim btnClearImg = MakeBtn("Clear Image", New Point(12, y), Color.FromArgb(42, 45, 62))
            btnClearImg.Size = New Size(100, 26)
            AddHandler btnClearImg.Click, Sub(s, e) _txtImgPath.Text = ""
            tab.Controls.Add(btnClearImg) : y += 44

            ' Lock message
            tab.Controls.Add(SectionLabel("Lock Screen Message", New Point(12, y))) : y += 20
            _txtMsg = DarkTextBox(New Point(12, y), 420, AppConfig.LockMessage)
            tab.Controls.Add(_txtMsg)

            Return tab
        End Function

        Private Function BuildRestrictionsTab() As TabPage
            Dim tab = DarkTab("Restrictions")
            Dim y = 16

            tab.Controls.Add(DimLabel(
                "These restrictions apply to the current Windows user while the client is running." &
                " They are removed automatically when the client exits.",
                New Point(12, y))) : y += 46

            _chkTasks = DarkCheck("Disable Task Manager",     New Point(12, y), AppConfig.DisableTaskManager) : tab.Controls.Add(_chkTasks) : y += 30
            _chkCmd   = DarkCheck("Disable Command Prompt",   New Point(12, y), AppConfig.DisableCmdPrompt)   : tab.Controls.Add(_chkCmd)   : y += 30
            _chkReg   = DarkCheck("Disable Registry Editor",  New Point(12, y), AppConfig.DisableRegistryTools) : tab.Controls.Add(_chkReg)  : y += 30
            _chkRun   = DarkCheck("Disable Run Dialog (Win+R)", New Point(12, y), AppConfig.DisableRunDialog)  : tab.Controls.Add(_chkRun)   : y += 40

            tab.Controls.Add(DimLabel("Note: Task Manager and Registry Editor changes take effect immediately." &
                                      " CMD and Run require reopening.", New Point(12, y)))
            Return tab
        End Function

        Private Function BuildSecurityTab() As TabPage
            Dim tab = DarkTab("Security & Warnings")
            Dim y = 16

            tab.Controls.Add(SectionLabel("Change Admin PIN", New Point(12, y))) : y += 20
            tab.Controls.Add(DimLabel("Leave blank to keep current PIN.", New Point(12, y))) : y += 20
            _txtPin  = DarkTextBox(New Point(12, y), 130, "", pwChar:="●"c) : tab.Controls.Add(_txtPin)
            tab.Controls.Add(DimLabel("New PIN", New Point(12, y + 28))) : y += 58

            _txtPin2 = DarkTextBox(New Point(12, y), 130, "", pwChar:="●"c) : tab.Controls.Add(_txtPin2)
            tab.Controls.Add(DimLabel("Confirm PIN", New Point(12, y + 28))) : y += 64

            tab.Controls.Add(SectionLabel("Low-Time Warnings  (balloon notification)", New Point(12, y))) : y += 24
            _chkWarn5 = DarkCheck("Warn at 5 minutes remaining", New Point(12, y), AppConfig.WarnAt5Min) : tab.Controls.Add(_chkWarn5) : y += 28
            _chkWarn1 = DarkCheck("Warn at 1 minute remaining",  New Point(12, y), AppConfig.WarnAt1Min) : tab.Controls.Add(_chkWarn1)

            Return tab
        End Function

        ' ── Event handlers ────────────────────────────────────────────────

        Private Sub OnPickColor(sender As Object, e As EventArgs)
            Dim dlg = New ColorDialog() With {
                .Color = _currentBgColor,
                .FullOpen = True
            }
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _currentBgColor = dlg.Color
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
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _txtImgPath.Text = dlg.FileName
            End If
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            ' Validate PIN change
            Dim newPin = _txtPin.Text.Trim()
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

            ' Save all settings
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

            ' Re-apply restrictions with new settings
            WindowsPolicy.Apply()

            MessageBox.Show("Settings saved. Some changes (Server URL, PC Number) take effect after restart.",
                            "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

        Private Sub OnExitApp(sender As Object, e As EventArgs)
            Dim result = MessageBox.Show(
                "Exit PisoNet client? Windows restrictions will be removed and the lock screen will close.",
                "Exit Application", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If result = DialogResult.Yes Then
                RaiseEvent ExitRequested()
            End If
        End Sub

        ' ── UI factory helpers ─────────────────────────────────────────────

        Private Function DarkTab(title As String) As TabPage
            Return New TabPage(title) With {
                .BackColor = Color.FromArgb(20, 24, 38),
                .ForeColor = Color.White
            }
        End Function

        Private Function SectionLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text = text, .AutoSize = True, .Location = loc,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = Color.White
            }
        End Function

        Private Function DimLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text = text, .AutoSize = False,
                .Size = New Size(430, 36),
                .Location = loc,
                .Font = New Font("Segoe UI", 8),
                .ForeColor = Color.FromArgb(100, 116, 139)
            }
        End Function

        Private Function DarkTextBox(loc As Point, width As Integer, text As String,
                                     Optional pwChar As Char = Nothing) As TextBox
            Dim tb = New TextBox() With {
                .Text = text, .Location = loc, .Width = width,
                .BackColor = Color.FromArgb(26, 30, 45),
                .ForeColor = Color.White,
                .BorderStyle = BorderStyle.FixedSingle
            }
            If pwChar <> Nothing Then tb.PasswordChar = pwChar
            Return tb
        End Function

        Private Function DarkCheck(text As String, loc As Point, checked As Boolean) As CheckBox
            Return New CheckBox() With {
                .Text = text, .Checked = checked, .Location = loc,
                .AutoSize = True,
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat
            }
        End Function

        Private Function MakeBtn(text As String, loc As Point, bgColor As Color) As Button
            Dim b = New Button() With {
                .Text = text, .Location = loc, .Size = New Size(140, 36),
                .BackColor = bgColor, .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat
            }
            b.FlatAppearance.BorderSize = 0
            Return b
        End Function

    End Class

End Namespace
