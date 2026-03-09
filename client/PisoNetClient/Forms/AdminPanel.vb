Imports System.Windows.Forms
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Text
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
        ''' <summary>Raised after a successful Save so callers can refresh live UI (timer overlay, lock screen).</summary>
        Public Event SettingsSaved()

        ' ── Controls ──────────────────────────────────────────────────────────
        Private _txtUrl         As TextBox
        Private _nudPcNum       As NumericUpDown
        Private _picColor       As PictureBox
        Private _txtImgPath     As TextBox
        Private _cmbBgFit       As ComboBox
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
        Private _cmbVoice       As ComboBox
        Private _currentBgColor As Color

        ' ── Appearance tab controls ────────────────────────────────────────────
        ' Timer overlay
        Private _chkTimerDot     As CheckBox
        Private _chkTimerPcLabel As CheckBox
        Private _cmbTimerPcPos   As ComboBox
        Private _timerPreview    As Panel
        ' Lock screen text — main message
        Private _picMsgColor     As PictureBox
        Private _nudMsgSize      As NumericUpDown
        Private _chkMsgCenterX   As CheckBox
        Private _nudMsgY         As NumericUpDown
        Private _currentMsgColor As Color
        ' Lock screen text — PC number label
        Private _picPcLblColor   As PictureBox
        Private _nudPcLblSize    As NumericUpDown
        Private _chkPcLblCenterX As CheckBox
        Private _nudPcLblY       As NumericUpDown
        Private _currentPcLblColor As Color
        ' Lock screen preview (Appearance tab)
        Private _lockPreview      As Panel
        ' Lock screen preview (Lock Screen tab — shows bg + message live)
        Private _lockTabPreview   As Panel
        ' Timer text colours
        Private _picTimerTimeColor  As PictureBox
        Private _picTimerLowColor   As PictureBox
        Private _currentTimerTimeColor As Color
        Private _currentTimerLowColor  As Color

        ' Layout — wider so all labels have room
        Private Const W   As Integer = 720
        Private Const TW  As Integer = 680
        Private Const IW  As Integer = 644
        Private Const LM  As Integer = 14

        Public Sub New()
            _currentBgColor        = Color.FromArgb(AppConfig.LockBgArgb)
            _currentMsgColor       = Color.FromArgb(AppConfig.LockMsgForeArgb)
            _currentPcLblColor     = Color.FromArgb(AppConfig.LockPcLabelForeArgb)
            _currentTimerTimeColor = Color.FromArgb(AppConfig.TimerTimeArgb)
            _currentTimerLowColor  = Color.FromArgb(AppConfig.TimerLowTimeArgb)
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.Text            = "PisoNet Admin Panel"
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.Size            = New Size(W, 660)
            Me.StartPosition   = FormStartPosition.CenterScreen
            Me.MaximizeBox     = False
            Me.MinimizeBox     = False
            Me.BackColor       = Color.FromArgb(12, 16, 28)
            Me.ForeColor       = Color.White
            Me.Font            = New Font("Segoe UI", 9)

            Dim tabs = New TabControl() With {
                .Location   = New Point(14, 14),
                .Size       = New Size(TW, 540),
                .Appearance = TabAppearance.Normal,
                .Font       = New Font("Segoe UI", 9)
            }
            Me.Controls.Add(tabs)

            tabs.TabPages.Add(BuildConnectionTab())
            tabs.TabPages.Add(BuildLockScreenTab())
            tabs.TabPages.Add(BuildRestrictionsTab())
            tabs.TabPages.Add(BuildSecurityTab())
            tabs.TabPages.Add(BuildNotificationsTab())
            tabs.TabPages.Add(BuildAppearanceTab())

            Dim btnSave  = MakeBtn("Save & Apply",     New Point(14,  570), Color.FromArgb(59, 130, 246))
            Dim btnExit  = MakeBtn("Exit Application", New Point(176, 570), Color.FromArgb(220, 38, 38))
            Dim btnClose = MakeBtn("Close",            New Point(560, 570), Color.FromArgb(42, 46, 64))

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
            tab.Controls.Add(btnClear)

            tab.Controls.Add(SmallLabel("Fit:", New Point(LM + 116, y + 5)))
            _cmbBgFit = New ComboBox() With {
                .Location      = New Point(LM + 138, y),
                .Width         = 110,
                .DropDownStyle = ComboBoxStyle.DropDownList,
                .BackColor     = Color.FromArgb(24, 28, 44),
                .ForeColor     = Color.White,
                .FlatStyle     = FlatStyle.Flat,
                .Font          = New Font("Segoe UI", 9)
            }
            _cmbBgFit.Items.AddRange({"Contain", "Cover", "Stretch"})
            _cmbBgFit.SelectedItem = AppConfig.LockBgImageFit
            tab.Controls.Add(_cmbBgFit) : y += 44

            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            tab.Controls.Add(SectionLabel("Lock Screen Message", New Point(LM, y))) : y += 22
            _txtMsg = DarkTextBox(New Point(LM, y), IW, AppConfig.LockMessage)
            tab.Controls.Add(_txtMsg) : y += 36

            ' ── Live lock screen preview ──────────────────────────────────────
            tab.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14
            tab.Controls.Add(SectionLabel("Preview", New Point(LM, y))) : y += 22

            _lockTabPreview = New Panel() With {
                .Location    = New Point(LM, y),
                .Size        = New Size(IW, 220),
                .BorderStyle = BorderStyle.FixedSingle
            }
            AddHandler _lockTabPreview.Paint, AddressOf OnPaintLockPreview
            tab.Controls.Add(_lockTabPreview)

            ' Wire bg-color and message changes so preview updates live
            AddHandler _txtMsg.TextChanged, Sub(s, e) _lockTabPreview.Invalidate()

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
            tab.Controls.Add(SmallLabel("JPEG Quality (30–100)", New Point(col3, y))) : y += 18

            _nudInterval = DarkNud(New Point(LM,   y), 80, AppConfig.ScreenCaptureIntervalSec, 3,  60)
            _nudQuality  = DarkNud(New Point(col3, y), 80, AppConfig.ScreenCaptureQuality,     30, 100)
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

            ' Volume
            tab.Controls.Add(SmallLabel("Volume (10–100)", New Point(LM, y))) : y += 18
            _nudVolume = DarkNud(New Point(LM, y), 80, AppConfig.VoiceVolume, 10, 100)
            tab.Controls.Add(_nudVolume) : y += 36

            ' Voice selection dropdown
            tab.Controls.Add(SmallLabel("Voice", New Point(LM, y))) : y += 18
            _cmbVoice = New ComboBox() With {
                .Location        = New Point(LM, y),
                .Width           = IW,
                .DropDownStyle   = ComboBoxStyle.DropDownList,
                .BackColor       = Color.FromArgb(26, 30, 45),
                .ForeColor       = Color.White,
                .FlatStyle       = FlatStyle.Flat,
                .Font            = New Font("Segoe UI", 9)
            }
            ' Populate with installed SAPI voices; "(Auto — prefer female)" = empty name
            _cmbVoice.Items.Add("(Auto — prefer female voice)")
            Dim voiceNames = NotificationService.GetInstalledVoiceNames()
            For Each vn In voiceNames
                _cmbVoice.Items.Add(vn)
            Next
            ' Select currently saved voice
            Dim saved = AppConfig.VoiceName.Trim()
            If String.IsNullOrEmpty(saved) OrElse _cmbVoice.Items.Count = 1 Then
                _cmbVoice.SelectedIndex = 0
            Else
                Dim idx = _cmbVoice.FindStringExact(saved)
                _cmbVoice.SelectedIndex = If(idx >= 0, idx, 0)
            End If
            tab.Controls.Add(_cmbVoice) : y += 36

            tab.Controls.Add(InfoLabel(
                "Voice works even when visual toasts are disabled — ideal for players in" &
                " full-screen games. Install more voices via Settings → Time & Language → Speech.",
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

        Private Function BuildAppearanceTab() As TabPage
            Dim tab = DarkTab("  Appearance")

            ' ── Scrollable inner panel — content can exceed the tab's visible height ──
            Dim inner = New Panel() With {
                .AutoScroll = True,
                .Dock       = DockStyle.Fill,
                .BackColor  = Color.FromArgb(18, 22, 36),
                .Padding    = New Padding(0)
            }
            tab.Controls.Add(inner)

            Dim y = 18

            ' ═══════════════════════════════════════════════════════════════════
            ' SECTION 1 — Timer Overlay
            ' ═══════════════════════════════════════════════════════════════════
            inner.Controls.Add(SectionLabel("Timer Overlay", New Point(LM, y))) : y += 26

            _chkTimerDot = DarkCheck(
                "Show connection dot  (green = connected, amber = offline) in upper-right corner",
                New Point(LM, y), AppConfig.TimerShowConnDot)
            inner.Controls.Add(_chkTimerDot) : y += 28

            _chkTimerPcLabel = DarkCheck(
                "Show PC number label on timer",
                New Point(LM, y), AppConfig.TimerShowPcLabel)
            inner.Controls.Add(_chkTimerPcLabel) : y += 32

            inner.Controls.Add(SmallLabel("PC Label Position:", New Point(LM, y)))
            _cmbTimerPcPos = New ComboBox() With {
                .Location      = New Point(LM + 124, y - 2),
                .Width         = 90,
                .DropDownStyle = ComboBoxStyle.DropDownList,
                .BackColor     = Color.FromArgb(26, 30, 45),
                .ForeColor     = Color.White,
                .FlatStyle     = FlatStyle.Flat,
                .Font          = New Font("Segoe UI", 9)
            }
            _cmbTimerPcPos.Items.AddRange({"Above", "Side"})
            _cmbTimerPcPos.SelectedItem = If(AppConfig.TimerPcLabelPosition = "Side", "Side", "Above")
            inner.Controls.Add(_cmbTimerPcPos) : y += 36

            ' ── Timer text colours ────────────────────────────────────────────
            inner.Controls.Add(SmallLabel("Time Color (normal):", New Point(LM, y + 4)))
            _picTimerTimeColor = New PictureBox() With {
                .Location    = New Point(LM + 150, y),
                .Size        = New Size(50, 22),
                .BackColor   = _currentTimerTimeColor,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor      = Cursors.Hand
            }
            AddHandler _picTimerTimeColor.Click, AddressOf OnPickTimerTimeColor
            inner.Controls.Add(_picTimerTimeColor)

            inner.Controls.Add(SmallLabel("Low Time (<5 min):", New Point(LM + 220, y + 4)))
            _picTimerLowColor = New PictureBox() With {
                .Location    = New Point(LM + 360, y),
                .Size        = New Size(50, 22),
                .BackColor   = _currentTimerLowColor,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor      = Cursors.Hand
            }
            AddHandler _picTimerLowColor.Click, AddressOf OnPickTimerLowColor
            inner.Controls.Add(_picTimerLowColor) : y += 34

            inner.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14
            inner.Controls.Add(SectionLabel("Timer Preview", New Point(LM, y))) : y += 22

            _timerPreview = New Panel() With {
                .Location    = New Point(LM, y),
                .Size        = New Size(180, 70),
                .BackColor   = Color.FromArgb(18, 22, 38),
                .BorderStyle = BorderStyle.FixedSingle
            }
            AddHandler _timerPreview.Paint, AddressOf OnPaintTimerPreview
            inner.Controls.Add(_timerPreview) : y += 82   ' panel (70) + gap (12)

            inner.Controls.Add(InfoLabel(
                "Drag the timer to move it. Right-click to hide or reset position.",
                New Point(LM, y))) : y += 32

            inner.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            ' ═══════════════════════════════════════════════════════════════════
            ' SECTION 2 — Lock Screen Text
            ' ═══════════════════════════════════════════════════════════════════
            inner.Controls.Add(SectionLabel("Lock Screen Text", New Point(LM, y))) : y += 24

            ' ── Column header row ─────────────────────────────────────────────
            inner.Controls.Add(SmallLabel("Element",      New Point(LM,       y)))
            inner.Controls.Add(SmallLabel("Color",        New Point(LM + 130, y)))
            inner.Controls.Add(SmallLabel("Font Size",    New Point(LM + 200, y)))
            inner.Controls.Add(SmallLabel("Center X",     New Point(LM + 278, y)))
            inner.Controls.Add(SmallLabel("Y Position %", New Point(LM + 358, y))) : y += 18

            ' ── Main Message row ─────────────────────────────────────────────
            inner.Controls.Add(SmallLabel("Main Message", New Point(LM, y + 4)))

            _picMsgColor = New PictureBox() With {
                .Location    = New Point(LM + 130, y),
                .Size        = New Size(50, 22),
                .BackColor   = _currentMsgColor,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor      = Cursors.Hand
            }
            AddHandler _picMsgColor.Click, AddressOf OnPickMsgColor
            inner.Controls.Add(_picMsgColor)

            _nudMsgSize = DarkNud(New Point(LM + 200, y), 70, AppConfig.LockMsgSize, 18, 72)
            inner.Controls.Add(_nudMsgSize)

            _chkMsgCenterX = DarkCheck("Center", New Point(LM + 278, y + 2), AppConfig.LockMsgCenterX)
            inner.Controls.Add(_chkMsgCenterX)

            _nudMsgY = DarkNud(New Point(LM + 358, y), 70, AppConfig.LockMsgYPct, 0, 100)
            inner.Controls.Add(_nudMsgY) : y += 30

            ' ── PC Number Label row ───────────────────────────────────────────
            inner.Controls.Add(SmallLabel("PC Number Label", New Point(LM, y + 4)))

            _picPcLblColor = New PictureBox() With {
                .Location    = New Point(LM + 130, y),
                .Size        = New Size(50, 22),
                .BackColor   = _currentPcLblColor,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor      = Cursors.Hand
            }
            AddHandler _picPcLblColor.Click, AddressOf OnPickPcLblColor
            inner.Controls.Add(_picPcLblColor)

            _nudPcLblSize = DarkNud(New Point(LM + 200, y), 70, AppConfig.LockPcLabelSize, 8, 72)
            inner.Controls.Add(_nudPcLblSize)

            _chkPcLblCenterX = DarkCheck("Center", New Point(LM + 278, y + 2), AppConfig.LockPcLabelCenterX)
            inner.Controls.Add(_chkPcLblCenterX)

            _nudPcLblY = DarkNud(New Point(LM + 358, y), 70, AppConfig.LockPcLabelYPct, 0, 100)
            inner.Controls.Add(_nudPcLblY) : y += 30

            inner.Controls.Add(InfoLabel(
                "Y: 0 = top edge, 50 = centered, 100 = bottom edge." &
                " Center X auto-centers the label based on its rendered pixel width.",
                New Point(LM, y))) : y += 38

            inner.Controls.Add(Rule(New Point(LM, y), IW)) : y += 14

            ' ── Lock screen preview ────────────────────────────────────────────
            inner.Controls.Add(SectionLabel("Lock Screen Preview", New Point(LM, y))) : y += 22

            _lockPreview = New Panel() With {
                .Location    = New Point(LM, y),
                .Size        = New Size(IW, 180),   ' taller now that tab scrolls
                .BorderStyle = BorderStyle.FixedSingle
            }
            AddHandler _lockPreview.Paint, AddressOf OnPaintLockPreview
            inner.Controls.Add(_lockPreview) : y += 192   ' panel (180) + gap (12)

            ' Explicit AutoScrollMinSize so the scrollbar appears at the right moment
            inner.AutoScrollMinSize = New Size(0, y)

            ' ── Wire live-preview change handlers ─────────────────────────────
            Dim timerRefresh = New EventHandler(AddressOf InvalidateTimerPreview)
            AddHandler _chkTimerDot.CheckedChanged,         timerRefresh
            AddHandler _chkTimerPcLabel.CheckedChanged,     timerRefresh
            AddHandler _cmbTimerPcPos.SelectedIndexChanged, timerRefresh

            Dim lockRefresh = New EventHandler(AddressOf InvalidateLockPreview)
            AddHandler _nudMsgSize.ValueChanged,        lockRefresh
            AddHandler _chkMsgCenterX.CheckedChanged,   lockRefresh
            AddHandler _nudMsgY.ValueChanged,           lockRefresh
            AddHandler _nudPcLblSize.ValueChanged,      lockRefresh
            AddHandler _chkPcLblCenterX.CheckedChanged, lockRefresh
            AddHandler _nudPcLblY.ValueChanged,         lockRefresh

            ' Sync lock preview when the message text is edited on the Lock Screen tab
            If _txtMsg IsNot Nothing Then
                AddHandler _txtMsg.TextChanged, lockRefresh
            End If

            Return tab
        End Function

        ' ── Preview paint handlers ─────────────────────────────────────────────

        Private Sub InvalidateTimerPreview(sender As Object, e As EventArgs)
            _timerPreview?.Invalidate()
        End Sub

        Private Sub InvalidateLockPreview(sender As Object, e As EventArgs)
            _lockPreview?.Invalidate()
            _lockTabPreview?.Invalidate()
        End Sub

        ''' <summary>Paints a scaled-down visual of the timer overlay based on current form settings.</summary>
        Private Sub OnPaintTimerPreview(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim bg       = Color.FromArgb(18, 22, 38)
            Dim greenClr = _currentTimerTimeColor   ' respects the configured normal time colour
            Dim dimClr   = Color.FromArgb(100, 116, 139)

            e.Graphics.Clear(bg)
            e.Graphics.SmoothingMode      = SmoothingMode.AntiAlias
            e.Graphics.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit

            Dim showDot = _chkTimerDot.Checked
            Dim showPc  = _chkTimerPcLabel.Checked
            Dim pcAbove = (If(_cmbTimerPcPos.SelectedItem?.ToString(), "Above") = "Above")

            Dim pw = pnl.Width
            Dim ph = pnl.Height

            ' Connection dot
            If showDot Then
                Dim dotX = pw - 13
                Using br = New SolidBrush(greenClr)
                    e.Graphics.FillEllipse(br, dotX, 5, 9, 9)
                End Using
            End If

            Dim dotReserve = If(showDot, 16, 0)

            Dim sfC = New StringFormat() With {
                .Alignment     = StringAlignment.Center,
                .LineAlignment = StringAlignment.Center
            }
            Dim sfL = New StringFormat() With {
                .Alignment     = StringAlignment.Near,
                .LineAlignment = StringAlignment.Center
            }

            Dim pcText = $"PC {AppConfig.PCNumber:D2}"

            If showPc AndAlso pcAbove Then
                ' PC label above time
                Using pcBr = New SolidBrush(dimClr)
                    e.Graphics.DrawString(pcText, New Font("Segoe UI", 7),
                        pcBr, New RectangleF(2, 4, pw - dotReserve - 2, 14), sfC)
                End Using
                Using timeBr = New SolidBrush(greenClr)
                    e.Graphics.DrawString("12:34", New Font("Segoe UI", 17, FontStyle.Bold),
                        timeBr, New RectangleF(0, 18, pw, ph - 18), sfC)
                End Using

            ElseIf showPc Then
                ' PC label side
                Dim timeW = pw - 44
                Using timeBr = New SolidBrush(greenClr)
                    e.Graphics.DrawString("12:34", New Font("Segoe UI", 17, FontStyle.Bold),
                        timeBr, New RectangleF(0, 4, timeW, ph - 8), sfC)
                End Using
                Using pcBr = New SolidBrush(dimClr)
                    e.Graphics.DrawString(pcText, New Font("Segoe UI", 6.5F),
                        pcBr, New RectangleF(timeW + 2, (ph - 14) \ 2, 38, 14), sfL)
                End Using

            Else
                ' Time only
                Using timeBr = New SolidBrush(greenClr)
                    e.Graphics.DrawString("12:34", New Font("Segoe UI", 17, FontStyle.Bold),
                        timeBr, New RectangleF(0, 4, pw, ph - 8), sfC)
                End Using
            End If
        End Sub

        ''' <summary>Paints a proportional miniature of the lock screen based on current form settings.</summary>
        Private Sub OnPaintLockPreview(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim pw = pnl.Width
            Dim ph = pnl.Height

            e.Graphics.Clear(_currentBgColor)
            e.Graphics.SmoothingMode     = SmoothingMode.AntiAlias
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit

            ' Scale fonts relative to a 1080p reference height so the preview is proportional
            Const REF_H As Single = 1080.0F
            Dim scale = ph / REF_H

            ' ── Main message ─────────────────────────────────────────────────
            Dim msgText = If(String.IsNullOrWhiteSpace(_txtMsg?.Text), "Insert Coins to Start", _txtMsg.Text)
            Dim msgPt   = Math.Max(4.0F, CInt(_nudMsgSize.Value) * scale)
            Dim msgYPct = CInt(_nudMsgY.Value) / 100.0F

            Using msgFont = New Font("Segoe UI", msgPt, FontStyle.Bold)
                Dim msgSz = e.Graphics.MeasureString(msgText, msgFont)
                Dim msgX  = If(_chkMsgCenterX.Checked,
                               (pw - msgSz.Width) / 2.0F,
                               (pw - msgSz.Width) * CSng(AppConfig.LockMsgXPct) / 100.0F)
                Dim msgY  = (ph - msgSz.Height) * msgYPct
                Using msgBr = New SolidBrush(_currentMsgColor)
                    e.Graphics.DrawString(msgText, msgFont, msgBr, msgX, msgY)
                End Using

                ' Sub-text sits just below, always centered, dimmed
                Dim subText = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
                Dim subPt   = Math.Max(3.0F, 14 * scale)
                Using subFont = New Font("Segoe UI", subPt)
                    Dim subSz = e.Graphics.MeasureString(subText, subFont)
                    Dim subX  = (pw - subSz.Width) / 2.0F
                    Dim subY  = msgY + msgSz.Height + 2 * scale
                    Using subBr = New SolidBrush(Color.FromArgb(120, 140, 180))
                        e.Graphics.DrawString(subText, subFont, subBr, subX, subY)
                    End Using
                End Using
            End Using

            ' ── PC number label ───────────────────────────────────────────────
            Dim pcText = $"PC {AppConfig.PCNumber:D2}"
            Dim pcPt   = Math.Max(3.0F, CInt(_nudPcLblSize.Value) * scale)
            Dim pcYPct = CInt(_nudPcLblY.Value) / 100.0F

            Using pcFont = New Font("Segoe UI", pcPt)
                Dim pcSz = e.Graphics.MeasureString(pcText, pcFont)
                Dim pcX  = If(_chkPcLblCenterX.Checked,
                              (pw - pcSz.Width) / 2.0F,
                              pw * CSng(AppConfig.LockPcLabelXPct) / 100.0F)
                Dim pcY  = ph * pcYPct
                Using pcBr = New SolidBrush(_currentPcLblColor)
                    e.Graphics.DrawString(pcText, pcFont, pcBr, pcX, pcY)
                End Using
            End Using
        End Sub

        ' ── Handlers ──────────────────────────────────────────────────────────

        Private Sub OnPickColor(sender As Object, e As EventArgs)
            Dim dlg = New ColorDialog() With {.Color = _currentBgColor, .FullOpen = True}
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _currentBgColor     = dlg.Color
                _picColor.BackColor = dlg.Color
                _lockPreview?.Invalidate()
                _lockTabPreview?.Invalidate()
            End If
        End Sub

        Private Sub OnPickMsgColor(sender As Object, e As EventArgs)
            Dim dlg = New ColorDialog() With {.Color = _currentMsgColor, .FullOpen = True}
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _currentMsgColor       = dlg.Color
                _picMsgColor.BackColor = dlg.Color
                _lockPreview?.Invalidate()
                _lockTabPreview?.Invalidate()
            End If
        End Sub

        Private Sub OnPickPcLblColor(sender As Object, e As EventArgs)
            Dim dlg = New ColorDialog() With {.Color = _currentPcLblColor, .FullOpen = True}
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _currentPcLblColor       = dlg.Color
                _picPcLblColor.BackColor = dlg.Color
                _lockPreview?.Invalidate()
                _lockTabPreview?.Invalidate()
            End If
        End Sub

        Private Sub OnPickTimerTimeColor(sender As Object, e As EventArgs)
            Dim dlg = New ColorDialog() With {.Color = _currentTimerTimeColor, .FullOpen = True}
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _currentTimerTimeColor     = dlg.Color
                _picTimerTimeColor.BackColor = dlg.Color
                _timerPreview?.Invalidate()
            End If
        End Sub

        Private Sub OnPickTimerLowColor(sender As Object, e As EventArgs)
            Dim dlg = New ColorDialog() With {.Color = _currentTimerLowColor, .FullOpen = True}
            If dlg.ShowDialog(Me) = DialogResult.OK Then
                _currentTimerLowColor     = dlg.Color
                _picTimerLowColor.BackColor = dlg.Color
                _timerPreview?.Invalidate()
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
            AppConfig.SaveLockBgImageFit(If(_cmbBgFit.SelectedItem?.ToString(), "Contain"))
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
            ' Index 0 = Auto; any other = specific voice name
            Dim selectedVoice = If(_cmbVoice.SelectedIndex <= 0, "",
                                   _cmbVoice.SelectedItem?.ToString())
            AppConfig.SaveVoiceName(If(selectedVoice, ""))

            ' ── Appearance: timer overlay ──────────────────────────────────────
            AppConfig.SaveTimerTimeArgb(_currentTimerTimeColor.ToArgb())
            AppConfig.SaveTimerLowTimeArgb(_currentTimerLowColor.ToArgb())
            AppConfig.SaveTimerShowConnDot(_chkTimerDot.Checked)
            AppConfig.SaveTimerShowPcLabel(_chkTimerPcLabel.Checked)
            AppConfig.SaveTimerPcLabelPosition(If(_cmbTimerPcPos.SelectedItem?.ToString(), "Above"))

            ' ── Appearance: lock screen text ───────────────────────────────────
            AppConfig.SaveLockMsgForeArgb(_currentMsgColor.ToArgb())
            AppConfig.SaveLockMsgSize(CInt(_nudMsgSize.Value))
            AppConfig.SaveLockMsgCenterX(_chkMsgCenterX.Checked)
            AppConfig.SaveLockMsgYPct(CInt(_nudMsgY.Value))
            AppConfig.SaveLockPcLabelForeArgb(_currentPcLblColor.ToArgb())
            AppConfig.SaveLockPcLabelSize(CInt(_nudPcLblSize.Value))
            AppConfig.SaveLockPcLabelCenterX(_chkPcLblCenterX.Checked)
            AppConfig.SaveLockPcLabelYPct(CInt(_nudPcLblY.Value))

            WindowsPolicy.Apply()

            RaiseEvent SettingsSaved()

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
