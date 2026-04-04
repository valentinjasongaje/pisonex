Imports System.Windows.Forms
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Text
Imports System.IO
Imports PisoNetClient.Config
Imports PisoNetClient.Services
Imports PisoNetClient.Resources

Namespace Forms

    ''' <summary>
    ''' Admin configuration panel with sidebar navigation and card-based layout.
    ''' Accessible only after entering the correct PASSWORD.
    ''' </summary>
    Public Class AdminPanel
        Inherits Form

        Public Event ExitRequested()
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
        Private _txtCurrentPin  As TextBox
        Private _txtPin         As TextBox
        Private _txtPin2        As TextBox
        Private _chkNotifs      As CheckBox
        Private _chkVoice       As CheckBox
        Private _nudVolume      As NumericUpDown
        Private _cmbVoice       As ComboBox
        Private _currentBgColor As Color

        ' Appearance tab controls
        Private _chkTimerDot     As CheckBox
        Private _chkTimerPcLabel As CheckBox
        Private _cmbTimerPcPos   As ComboBox
        Private _timerPreview    As Panel
        Private _picMsgColor     As PictureBox
        Private _nudMsgSize      As NumericUpDown
        Private _chkMsgCenterX   As CheckBox
        Private _nudMsgY         As NumericUpDown
        Private _currentMsgColor As Color
        Private _picPcLblColor   As PictureBox
        Private _nudPcLblSize    As NumericUpDown
        Private _chkPcLblCenterX As CheckBox
        Private _nudPcLblY       As NumericUpDown
        Private _currentPcLblColor As Color
        Private _lockPreview      As Panel
        Private _lockTabPreview   As Panel
        Private _picTimerTimeColor  As PictureBox
        Private _picTimerLowColor   As PictureBox
        Private _currentTimerTimeColor As Color
        Private _currentTimerLowColor  As Color

        ' Wallpaper controls
        Private _wpPreview     As Panel
        Private _chkUseLocalWp As CheckBox
        Private _lblServerWp   As Label

        ' Sidebar navigation
        Private _sidebar       As Panel
        Private _contentArea   As Panel
        Private _pages         As New List(Of Panel)
        Private _navItems      As New List(Of Panel)
        Private _activeIndex   As Integer = 0

        ' Layout constants
        Private Const SIDEBAR_W As Integer = 200
        Private Const FORM_W    As Integer = 920
        Private Const FORM_H    As Integer = 720   ' ClientSize height (excludes title bar)
        Private Const CONTENT_W As Integer = FORM_W - SIDEBAR_W - 14
        Private Const IW        As Integer = CONTENT_W - 48
        Private Const LM        As Integer = 20

        ' Color palette
        Private Shared ReadOnly ColFormBg     As Color = Color.FromArgb(10, 14, 25)
        Private Shared ReadOnly ColSidebarBg  As Color = Color.FromArgb(8, 12, 22)
        Private Shared ReadOnly ColContentBg  As Color = Color.FromArgb(16, 20, 34)
        Private Shared ReadOnly ColCardBg     As Color = Color.FromArgb(22, 28, 46)
        Private Shared ReadOnly ColCardBorder As Color = Color.FromArgb(36, 42, 64)
        Private Shared ReadOnly ColAccent     As Color = Color.FromArgb(59, 130, 246)
        Private Shared ReadOnly ColAccentHov  As Color = Color.FromArgb(79, 150, 255)
        Private Shared ReadOnly ColNavHover   As Color = Color.FromArgb(28, 34, 56)
        Private Shared ReadOnly ColNavActive  As Color = Color.FromArgb(20, 26, 44)
        Private Shared ReadOnly ColInputBg    As Color = Color.FromArgb(24, 28, 44)
        Private Shared ReadOnly ColRule       As Color = Color.FromArgb(38, 42, 60)
        Private Shared ReadOnly ColSection    As Color = Color.FromArgb(99, 162, 255)
        Private Shared ReadOnly ColInfo       As Color = Color.FromArgb(94, 110, 140)
        Private Shared ReadOnly ColSmall      As Color = Color.FromArgb(180, 190, 210)
        Private Shared ReadOnly ColText       As Color = Color.FromArgb(220, 228, 240)
        Private Shared ReadOnly ColDanger     As Color = Color.FromArgb(239, 68, 68)

        ' License tab controls
        Private _txtLicenseKey  As TextBox
        Private _lblLicStatus   As Label
        Private _lblLicDeviceId As Label
        Private _lblLicExpiry   As Label
        Private _lblLicVerified As Label
        Private _lblLicResult   As Label

        ' Nav item definitions
        Private Shared ReadOnly NavLabels() As String = {
            "Connection", "Lock Screen", "Restrictions",
            "Security", "Notifications", "License"
        }
        Private Shared ReadOnly NavIcons() As String = {
            ChrW(&H26A1), Char.ConvertFromUtf32(&H1F512), Char.ConvertFromUtf32(&H1F6E1),
            Char.ConvertFromUtf32(&H1F511), Char.ConvertFromUtf32(&H1F514), ChrW(&H26BF)
        }

        Public Sub New()
            _currentBgColor        = Color.FromArgb(AppConfig.LockBgArgb)
            _currentMsgColor       = Color.FromArgb(AppConfig.LockMsgForeArgb)
            _currentPcLblColor     = Color.FromArgb(AppConfig.LockPcLabelForeArgb)
            _currentTimerTimeColor = Color.FromArgb(AppConfig.TimerTimeArgb)
            _currentTimerLowColor  = Color.FromArgb(AppConfig.TimerLowTimeArgb)
            InitializeComponent()
        End Sub

        Private Const TITLE_H As Integer = 32

        Private Sub InitializeComponent()
            Me.FormBorderStyle = FormBorderStyle.None
            Me.ClientSize      = New Size(FORM_W, FORM_H + TITLE_H)
            Me.StartPosition   = FormStartPosition.CenterScreen
            Me.BackColor       = ColFormBg
            Me.ForeColor       = Color.White
            Me.Icon            = Resources.LogoHelper.GetIcon()
            Me.Font            = New Font("Segoe UI", 9)
            Me.DoubleBuffered  = True

            BuildTitleBar()
            BuildSidebar()
            BuildContentArea()
            BuildFooterButtons()

            ' Form border
            AddHandler Me.Paint, Sub(s, e)
                Using pen = New Pen(Color.FromArgb(30, 80, 120, 200), 1)
                    e.Graphics.DrawRectangle(pen, 0, 0, Me.Width - 1, Me.Height - 1)
                End Using
            End Sub
        End Sub

        Private Sub BuildTitleBar()
            Dim bar = New Panel() With {
                .Location  = New Point(0, 0),
                .Size      = New Size(FORM_W, TITLE_H),
                .BackColor = Color.FromArgb(6, 10, 18),
                .Cursor    = Cursors.SizeAll
            }

            ' Gradient accent line at top
            AddHandler bar.Paint, Sub(s, e)
                Dim g = e.Graphics
                Using br = New LinearGradientBrush(New Rectangle(0, 0, bar.Width, 2),
                        FormStyles.AccentBlue, Color.FromArgb(124, 58, 237), 0F)
                    g.FillRectangle(br, New Rectangle(0, 0, bar.Width, 2))
                End Using
                Using pen = New Pen(Color.FromArgb(30, 80, 120, 200), 1)
                    g.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1)
                End Using
            End Sub

            ' Title text
            Dim lbl = New Label() With {
                .Text      = "Pisonex — Admin Panel",
                .Font      = New Font("Segoe UI", 8.5F, FontStyle.Bold),
                .ForeColor = Color.FromArgb(140, 160, 200),
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(FORM_W - 40, TITLE_H),
                .Location  = New Point(14, 0),
                .TextAlign = ContentAlignment.MiddleLeft,
                .Cursor    = Cursors.SizeAll
            }

            ' Close button
            Dim btnClose = New Label() With {
                .Text      = ChrW(&H2715),
                .Font      = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(100, 120, 150),
                .BackColor = Color.Transparent,
                .Size      = New Size(TITLE_H, TITLE_H),
                .Location  = New Point(FORM_W - TITLE_H, 0),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Cursor    = Cursors.Hand
            }
            AddHandler btnClose.MouseEnter, Sub(s, e)
                btnClose.BackColor = Color.FromArgb(60, 239, 68, 68)
                btnClose.ForeColor = Color.FromArgb(239, 150, 150)
            End Sub
            AddHandler btnClose.MouseLeave, Sub(s, e)
                btnClose.BackColor = Color.Transparent
                btnClose.ForeColor = Color.FromArgb(100, 120, 150)
            End Sub
            AddHandler btnClose.Click, Sub(s, e) Me.Close()

            ' Drag support
            Dim dragHandler = Sub(sender As Object, ev As MouseEventArgs)
                If ev.Button = MouseButtons.Left Then
                    ReleaseCapture()
                    SendMessage(Me.Handle, WM_NCLBUTTONDOWN, New IntPtr(HTCAPTION), IntPtr.Zero)
                End If
            End Sub
            AddHandler bar.MouseDown, dragHandler
            AddHandler lbl.MouseDown, dragHandler

            bar.Controls.Add(lbl)
            bar.Controls.Add(btnClose)
            Me.Controls.Add(bar)
        End Sub

        ' ── Sidebar ──────────────────────────────────────────────────────────

        Private Sub BuildSidebar()
            _sidebar = New Panel() With {
                .Location  = New Point(0, TITLE_H),
                .Size      = New Size(SIDEBAR_W, FORM_H),
                .BackColor = ColSidebarBg
            }
            AddHandler _sidebar.Paint, AddressOf OnPaintSidebar
            Me.Controls.Add(_sidebar)

            ' Logo header
            Dim header = New Panel() With {
                .Location  = New Point(0, 0),
                .Size      = New Size(SIDEBAR_W, 64),
                .BackColor = Color.Transparent
            }
            AddHandler header.Paint, AddressOf OnPaintHeader
            _sidebar.Controls.Add(header)

            ' Nav items
            Dim navY = 72
            For i = 0 To NavLabels.Length - 1
                Dim navItem = CreateNavItem(NavIcons(i), NavLabels(i), i, New Point(0, navY))
                _sidebar.Controls.Add(navItem)
                _navItems.Add(navItem)
                navY += 42
            Next
            UpdateNavSelection()
        End Sub

        Private Function CreateNavItem(icon As String, label As String, index As Integer, loc As Point) As Panel
            Dim pnl = New Panel() With {
                .Location  = loc,
                .Size      = New Size(SIDEBAR_W, 40),
                .BackColor = Color.Transparent,
                .Cursor    = Cursors.Hand,
                .Tag       = index
            }
            AddHandler pnl.Paint, AddressOf OnPaintNavItem
            AddHandler pnl.Click, AddressOf OnNavItemClick
            AddHandler pnl.MouseEnter, Sub(s, e) If CInt(pnl.Tag) <> _activeIndex Then pnl.BackColor = ColNavHover
            AddHandler pnl.MouseLeave, Sub(s, e) If CInt(pnl.Tag) <> _activeIndex Then pnl.BackColor = Color.Transparent
            Return pnl
        End Function

        Private Sub OnPaintHeader(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit

            ' Gradient background
            Using br = New LinearGradientBrush(pnl.ClientRectangle,
                    Color.FromArgb(15, 20, 40), Color.FromArgb(25, 35, 60),
                    LinearGradientMode.Horizontal)
                g.FillRectangle(br, pnl.ClientRectangle)
            End Using

            ' Logo
            Try
                Dim logo = LogoHelper.GetLogo()
                If logo IsNot Nothing Then
                    g.DrawImage(logo, New Rectangle(14, 14, 36, 36))
                End If
            Catch
            End Try

            ' Title
            Using f = New Font("Segoe UI", 13, FontStyle.Bold)
                Using br = New SolidBrush(Color.White)
                    g.DrawString("Admin Panel", f, br, 56, 20)
                End Using
            End Using
        End Sub

        Private Sub OnPaintSidebar(sender As Object, e As PaintEventArgs)
            ' Bottom separator line
            Dim g = e.Graphics
            Using p = New Pen(ColCardBorder)
                g.DrawLine(p, SIDEBAR_W - 1, 0, SIDEBAR_W - 1, FORM_H)
            End Using
        End Sub

        Private Sub OnPaintNavItem(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit
            Dim idx = CInt(pnl.Tag)
            Dim isActive = (idx = _activeIndex)

            ' Active indicator bar
            If isActive Then
                pnl.BackColor = ColNavActive
                Using br = New SolidBrush(ColAccent)
                    g.FillRectangle(br, 0, 0, 3, pnl.Height)
                End Using
            End If

            ' Icon
            Dim iconColor = If(isActive, ColAccent, ColInfo)
            Using f = New Font("Segoe UI", 11)
                Using br = New SolidBrush(iconColor)
                    g.DrawString(NavIcons(idx), f, br, 14, 9)
                End Using
            End Using

            ' Label
            Dim labelColor = If(isActive, Color.White, ColSmall)
            Using f = New Font("Segoe UI", 9.5F, If(isActive, FontStyle.Bold, FontStyle.Regular))
                Using br = New SolidBrush(labelColor)
                    g.DrawString(NavLabels(idx), f, br, 44, 11)
                End Using
            End Using
        End Sub

        Private Sub OnNavItemClick(sender As Object, e As EventArgs)
            Dim pnl = CType(sender, Panel)
            Dim idx = CInt(pnl.Tag)
            If idx = _activeIndex Then Return
            _activeIndex = idx
            UpdateNavSelection()
            ShowPage(idx)
        End Sub

        Private Sub UpdateNavSelection()
            For i = 0 To _navItems.Count - 1
                _navItems(i).BackColor = If(i = _activeIndex, ColNavActive, Color.Transparent)
                _navItems(i).Invalidate()
            Next
        End Sub

        Private Sub ShowPage(index As Integer)
            For i = 0 To _pages.Count - 1
                _pages(i).Visible = (i = index)
            Next
        End Sub

        ' ── Content area ──────────────────────────────────────────────────────

        Private Sub BuildContentArea()
            _contentArea = New Panel() With {
                .Location  = New Point(SIDEBAR_W, TITLE_H),
                .Size      = New Size(FORM_W - SIDEBAR_W, FORM_H - 60),
                .BackColor = ColContentBg,
                .Padding   = New Padding(0)
            }
            Me.Controls.Add(_contentArea)

            ' Build all pages
            Dim builders = New Func(Of Panel)() {
                AddressOf BuildConnectionPage,
                AddressOf BuildLockScreenPage,
                AddressOf BuildRestrictionsPage,
                AddressOf BuildSecurityPage,
                AddressOf BuildNotificationsPage,
                AddressOf BuildLicensePage
            }
            For i = 0 To builders.Length - 1
                Dim page = builders(i)()
                page.Location = New Point(0, 0)
                page.Size = _contentArea.Size
                page.Visible = (i = 0)
                _contentArea.Controls.Add(page)
                _pages.Add(page)
            Next
        End Sub

        ' ── Footer ────────────────────────────────────────────────────────────

        Private Sub BuildFooterButtons()
            Dim footer = New Panel() With {
                .Location  = New Point(SIDEBAR_W, FORM_H - 60 + TITLE_H),
                .Size      = New Size(FORM_W - SIDEBAR_W, 60),
                .BackColor = Color.FromArgb(12, 16, 28)
            }
            ' Top border
            AddHandler footer.Paint, Sub(s, e)
                Using p = New Pen(ColCardBorder)
                    e.Graphics.DrawLine(p, 0, 0, footer.Width, 0)
                End Using
            End Sub
            Me.Controls.Add(footer)

            Dim btnSave  = MakeBtn("Save & Apply",     New Point(16,  12), ColAccent)
            Dim btnExit  = MakeBtn("Exit Application", New Point(178, 12), ColDanger)
            Dim btnClose = MakeBtn("Close",            New Point(footer.Width - 168, 12), Color.FromArgb(42, 46, 64))

            AddHandler btnSave.Click,  AddressOf OnSave
            AddHandler btnExit.Click,  AddressOf OnExitApp
            AddHandler btnClose.Click, Sub(s, e) Me.Close()

            footer.Controls.AddRange({btnSave, btnExit, btnClose})
        End Sub

        ' ── Page builders ─────────────────────────────────────────────────────

        Private Function BuildConnectionPage() As Panel
            Dim page = ScrollPage()
            Dim y = 16

            page.Controls.Add(PageTitle("Connection Settings", New Point(LM, y))) : y += 34

            Dim card = CardPanel(New Point(LM, y), New Size(IW, 160))
            Dim cy = 14

            card.Controls.Add(SectionLabel("Server URL", New Point(14, cy))) : cy += 22
            _txtUrl = DarkTextBox(New Point(14, cy), IW - 32, AppConfig.ServerUrl)
            card.Controls.Add(_txtUrl) : cy += 38

            card.Controls.Add(SectionLabel("PC Number", New Point(14, cy))) : cy += 22
            _nudPcNum = DarkNud(New Point(14, cy), 80, AppConfig.PCNumber, 1, 99)
            card.Controls.Add(_nudPcNum)

            page.Controls.Add(card) : y += 170

            Dim infoCard = CardPanel(New Point(LM, y), New Size(IW, 48))
            infoCard.Controls.Add(InfoLabel(
                "Server URL and PC Number take effect after restarting the client.",
                New Point(14, 10)))
            page.Controls.Add(infoCard)

            Return page
        End Function

        Private Function BuildLockScreenPage() As Panel
            Dim page = ScrollPage()
            Dim y = 16

            page.Controls.Add(PageTitle("Lock Screen", New Point(LM, y))) : y += 34

            ' ── Background card ──────────────────────────────────────────────
            Dim bgCard = CardPanel(New Point(LM, y), New Size(IW, 180))
            Dim cy = 14

            bgCard.Controls.Add(SectionLabel("Background Color", New Point(14, cy)))
            _picColor = New PictureBox() With {
                .Location    = New Point(140, cy - 2),
                .Size        = New Size(42, 22),
                .BackColor   = _currentBgColor,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor      = Cursors.Hand
            }
            AddHandler _picColor.Click, AddressOf OnPickColor
            bgCard.Controls.Add(_picColor) : cy += 32

            bgCard.Controls.Add(SectionLabel("Background Image", New Point(14, cy))) : cy += 22
            _txtImgPath = DarkTextBox(New Point(14, cy), IW - 140, AppConfig.LockBgImagePath)
            bgCard.Controls.Add(_txtImgPath)
            Dim btnBrowse = MakeSmallBtn("Browse...", New Point(IW - 118, cy - 1))
            AddHandler btnBrowse.Click, AddressOf OnBrowseImage
            bgCard.Controls.Add(btnBrowse) : cy += 34

            Dim btnClear = MakeSmallBtn("Clear", New Point(14, cy))
            AddHandler btnClear.Click, Sub(s, e) _txtImgPath.Text = ""
            bgCard.Controls.Add(btnClear)

            bgCard.Controls.Add(SmallLabel("Fit:", New Point(100, cy + 5)))
            _cmbBgFit = DarkCombo(New Point(124, cy), 100, {"Contain", "Cover", "Stretch"}, AppConfig.LockBgImageFit)
            bgCard.Controls.Add(_cmbBgFit) : cy += 32

            bgCard.Controls.Add(SmallLabel("Message:", New Point(14, cy + 4)))
            _txtMsg = DarkTextBox(New Point(80, cy), IW - 98, AppConfig.LockMessage)
            bgCard.Controls.Add(_txtMsg)

            page.Controls.Add(bgCard) : y += 190

            ' ── Wallpaper preview with drag-and-drop ─────────────────────────
            Dim wpCard = CardPanel(New Point(LM, y), New Size(IW, 240))
            cy = 14

            wpCard.Controls.Add(SectionLabel("Wallpaper Preview", New Point(14, cy)))
            wpCard.Controls.Add(SmallLabel("(drag & drop images here)", New Point(150, cy + 3))) : cy += 28

            _wpPreview = New Panel() With {
                .Location    = New Point(14, cy),
                .Size        = New Size(320, 180),
                .BackColor   = Color.FromArgb(10, 14, 23),
                .BorderStyle = BorderStyle.FixedSingle,
                .AllowDrop   = True
            }
            AddHandler _wpPreview.Paint, AddressOf OnPaintWpPreview
            AddHandler _wpPreview.DragEnter, AddressOf OnWpDragEnter
            AddHandler _wpPreview.DragDrop, AddressOf OnWpDragDrop
            AddHandler _wpPreview.DragLeave, Sub(s, e) _wpPreview.BorderStyle = BorderStyle.FixedSingle
            AddHandler _txtImgPath.TextChanged, Sub(s, e)
                                                     _wpPreview.Invalidate()
                                                     _lockTabPreview?.Invalidate()
                                                 End Sub
            AddHandler _cmbBgFit.SelectedIndexChanged, Sub(s, e) _wpPreview.Invalidate()
            wpCard.Controls.Add(_wpPreview)

            ' Preset gallery on right side of preview
            Dim presetsX = 350
            wpCard.Controls.Add(SmallLabel("Presets:", New Point(presetsX, cy)))
            cy += 18
            BuildPresetGallery(wpCard, presetsX, cy)

            page.Controls.Add(wpCard) : y += 250

            ' ── Server wallpaper status ──────────────────────────────────────
            Dim srvCard = CardPanel(New Point(LM, y), New Size(IW, 80))
            cy = 14

            srvCard.Controls.Add(SectionLabel("Server Wallpaper", New Point(14, cy)))
            Dim srvStatus = If(String.IsNullOrEmpty(AppConfig.ServerWallpaperPath), "None", "Active")
            _lblServerWp = SmallLabel($"Status: {srvStatus}", New Point(140, cy + 3))
            _lblServerWp.ForeColor = If(srvStatus = "Active", Color.FromArgb(34, 197, 94), ColInfo)
            srvCard.Controls.Add(_lblServerWp) : cy += 28

            _chkUseLocalWp = DarkCheck("Override server wallpaper with local selection",
                                       New Point(14, cy), AppConfig.UseLocalWallpaper)
            srvCard.Controls.Add(_chkUseLocalWp)

            page.Controls.Add(srvCard) : y += 90

            ' ── Lock screen preview ──────────────────────────────────────────
            Dim prevCard = CardPanel(New Point(LM, y), New Size(IW, 200))
            cy = 14
            prevCard.Controls.Add(SectionLabel("Live Preview", New Point(14, cy))) : cy += 24

            _lockTabPreview = New Panel() With {
                .Location    = New Point(14, cy),
                .Size        = New Size(IW - 32, 156),
                .BorderStyle = BorderStyle.FixedSingle
            }
            AddHandler _lockTabPreview.Paint, AddressOf OnPaintLockPreview
            AddHandler _txtMsg.TextChanged, Sub(s, e) _lockTabPreview.Invalidate()
            prevCard.Controls.Add(_lockTabPreview)

            page.Controls.Add(prevCard) : y += 210

            ' ── Timer overlay card ───────────────────────────────────────────
            Dim timerCard = CardPanel(New Point(LM, y), New Size(IW, 220))
            cy = 14
            timerCard.Controls.Add(SectionLabel("Timer Overlay", New Point(14, cy))) : cy += 26

            _chkTimerDot = DarkCheck("Show connection dot", New Point(14, cy), AppConfig.TimerShowConnDot)
            timerCard.Controls.Add(_chkTimerDot) : cy += 28

            _chkTimerPcLabel = DarkCheck("Show PC number label", New Point(14, cy), AppConfig.TimerShowPcLabel)
            timerCard.Controls.Add(_chkTimerPcLabel) : cy += 28

            timerCard.Controls.Add(SmallLabel("PC Label Position:", New Point(14, cy + 2)))
            _cmbTimerPcPos = DarkCombo(New Point(140, cy), 80, {"Above", "Side"},
                                       If(AppConfig.TimerPcLabelPosition = "Side", "Side", "Above"))
            timerCard.Controls.Add(_cmbTimerPcPos) : cy += 32

            ' Timer colors
            timerCard.Controls.Add(SmallLabel("Normal Color:", New Point(14, cy + 4)))
            _picTimerTimeColor = ColorSwatch(New Point(110, cy), _currentTimerTimeColor)
            AddHandler _picTimerTimeColor.Click, AddressOf OnPickTimerTimeColor
            timerCard.Controls.Add(_picTimerTimeColor)

            timerCard.Controls.Add(SmallLabel("Low Time:", New Point(180, cy + 4)))
            _picTimerLowColor = ColorSwatch(New Point(250, cy), _currentTimerLowColor)
            AddHandler _picTimerLowColor.Click, AddressOf OnPickTimerLowColor
            timerCard.Controls.Add(_picTimerLowColor) : cy += 32

            ' Timer preview
            _timerPreview = New Panel() With {
                .Location    = New Point(14, cy),
                .Size        = New Size(160, 56),
                .BackColor   = Color.FromArgb(18, 22, 38),
                .BorderStyle = BorderStyle.FixedSingle
            }
            AddHandler _timerPreview.Paint, AddressOf OnPaintTimerPreview
            timerCard.Controls.Add(_timerPreview)

            page.Controls.Add(timerCard) : y += 230

            ' ── Lock screen text card ────────────────────────────────────────
            Dim textCard = CardPanel(New Point(LM, y), New Size(IW, 170))
            cy = 14
            textCard.Controls.Add(SectionLabel("Lock Screen Text", New Point(14, cy))) : cy += 24

            ' Column headers
            textCard.Controls.Add(SmallLabel("Element",   New Point(14,  cy)))
            textCard.Controls.Add(SmallLabel("Color",     New Point(130, cy)))
            textCard.Controls.Add(SmallLabel("Size",      New Point(200, cy)))
            textCard.Controls.Add(SmallLabel("Center X",  New Point(270, cy)))
            textCard.Controls.Add(SmallLabel("Y Pos %",   New Point(350, cy))) : cy += 18

            ' Main message row
            textCard.Controls.Add(SmallLabel("Main Message", New Point(14, cy + 4)))
            _picMsgColor = ColorSwatch(New Point(130, cy), _currentMsgColor)
            AddHandler _picMsgColor.Click, AddressOf OnPickMsgColor
            textCard.Controls.Add(_picMsgColor)
            _nudMsgSize = DarkNud(New Point(200, cy), 60, AppConfig.LockMsgSize, 18, 72)
            textCard.Controls.Add(_nudMsgSize)
            _chkMsgCenterX = DarkCheck("", New Point(280, cy + 2), AppConfig.LockMsgCenterX)
            textCard.Controls.Add(_chkMsgCenterX)
            _nudMsgY = DarkNud(New Point(350, cy), 60, AppConfig.LockMsgYPct, 0, 100)
            textCard.Controls.Add(_nudMsgY) : cy += 30

            ' PC label row
            textCard.Controls.Add(SmallLabel("PC Label", New Point(14, cy + 4)))
            _picPcLblColor = ColorSwatch(New Point(130, cy), _currentPcLblColor)
            AddHandler _picPcLblColor.Click, AddressOf OnPickPcLblColor
            textCard.Controls.Add(_picPcLblColor)
            _nudPcLblSize = DarkNud(New Point(200, cy), 60, AppConfig.LockPcLabelSize, 8, 72)
            textCard.Controls.Add(_nudPcLblSize)
            _chkPcLblCenterX = DarkCheck("", New Point(280, cy + 2), AppConfig.LockPcLabelCenterX)
            textCard.Controls.Add(_chkPcLblCenterX)
            _nudPcLblY = DarkNud(New Point(350, cy), 60, AppConfig.LockPcLabelYPct, 0, 100)
            textCard.Controls.Add(_nudPcLblY) : cy += 36

            textCard.Controls.Add(InfoLabel(
                "Y: 0=top, 50=center, 100=bottom. Center X auto-centers based on text width.",
                New Point(14, cy)))

            page.Controls.Add(textCard) : y += 180

            ' Wire live-preview change handlers
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
            If _txtMsg IsNot Nothing Then AddHandler _txtMsg.TextChanged, lockRefresh

            page.AutoScrollMinSize = New Size(0, y + 10)
            Return page
        End Function

        Private Sub BuildPresetGallery(parent As Panel, startX As Integer, startY As Integer)
            Dim presets = {
                ("Dark Navy", Color.FromArgb(10, 14, 23), Color.FromArgb(25, 35, 60)),
                ("Blue Ocean", Color.FromArgb(10, 30, 80), Color.FromArgb(30, 100, 200)),
                ("Gaming Green", Color.FromArgb(5, 30, 20), Color.FromArgb(0, 120, 60)),
                ("Sunset", Color.FromArgb(80, 20, 50), Color.FromArgb(200, 100, 50)),
                ("City Night", Color.FromArgb(15, 15, 30), Color.FromArgb(60, 40, 80))
            }

            Dim px = startX
            Dim py = startY
            For i = 0 To presets.Length - 1
                Dim preset = presets(i)
                Dim name = preset.Item1
                Dim col1 = preset.Item2
                Dim col2 = preset.Item3
                Dim thumb = New Panel() With {
                    .Location = New Point(px, py),
                    .Size = New Size(110, 60),
                    .Cursor = Cursors.Hand,
                    .Tag = i
                }
                AddHandler thumb.Paint, Sub(s, e)
                    Dim p = CType(s, Panel)
                    Using br = New LinearGradientBrush(p.ClientRectangle, col1, col2, 45.0F)
                        e.Graphics.FillRectangle(br, p.ClientRectangle)
                    End Using
                    Using f = New Font("Segoe UI", 7)
                        Using tb = New SolidBrush(Color.FromArgb(200, 255, 255, 255))
                            e.Graphics.DrawString(name, f, tb, 4, p.Height - 16)
                        End Using
                    End Using
                    Using pen = New Pen(ColCardBorder)
                        e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1)
                    End Using
                End Sub
                AddHandler thumb.Click, Sub(s, e)
                    ' Generate a gradient wallpaper and save it
                    Dim cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Pisonex", "wallpapers")
                    Directory.CreateDirectory(cacheDir)
                    Dim savePath = Path.Combine(cacheDir, $"preset-{name.ToLower().Replace(" ", "-")}.png")
                    Using bmp = New Bitmap(1920, 1080)
                        Using g = Graphics.FromImage(bmp)
                            Using br = New LinearGradientBrush(New Rectangle(0, 0, 1920, 1080), col1, col2, 45.0F)
                                g.FillRectangle(br, 0, 0, 1920, 1080)
                            End Using
                        End Using
                        bmp.Save(savePath, Imaging.ImageFormat.Png)
                    End Using
                    _txtImgPath.Text = savePath
                End Sub
                AddHandler thumb.MouseEnter, Sub(s, e)
                    CType(s, Panel).BackColor = ColNavHover
                End Sub
                AddHandler thumb.MouseLeave, Sub(s, e)
                    CType(s, Panel).BackColor = Color.Transparent
                End Sub
                parent.Controls.Add(thumb)

                ' Stack vertically in pairs
                If i Mod 2 = 0 Then
                    py += 66
                Else
                    px += 116
                    py -= 66
                End If
                If i Mod 2 = 1 Then
                    px -= 116
                    py += 66
                End If
            Next
        End Sub

        Private Function BuildRestrictionsPage() As Panel
            Dim page = ScrollPage()
            Dim y = 16

            page.Controls.Add(PageTitle("Restrictions", New Point(LM, y))) : y += 34

            Dim infoCard = CardPanel(New Point(LM, y), New Size(IW, 44))
            infoCard.Controls.Add(InfoLabel(
                "These restrictions apply while the client is running and are removed on exit.",
                New Point(14, 10)))
            page.Controls.Add(infoCard) : y += 54

            Dim card = CardPanel(New Point(LM, y), New Size(IW, 180))
            Dim cy = 14
            card.Controls.Add(SectionLabel("Windows Restrictions", New Point(14, cy))) : cy += 28

            _chkTasks = DarkCheck("Disable Task Manager",       New Point(14, cy), AppConfig.DisableTaskManager)   : card.Controls.Add(_chkTasks) : cy += 30
            _chkCmd   = DarkCheck("Disable Command Prompt",     New Point(14, cy), AppConfig.DisableCmdPrompt)     : card.Controls.Add(_chkCmd)   : cy += 30
            _chkReg   = DarkCheck("Disable Registry Editor",    New Point(14, cy), AppConfig.DisableRegistryTools) : card.Controls.Add(_chkReg)   : cy += 30
            _chkRun   = DarkCheck("Disable Run Dialog (Win+R)", New Point(14, cy), AppConfig.DisableRunDialog)     : card.Controls.Add(_chkRun)

            page.Controls.Add(card) : y += 190

            Dim noteCard = CardPanel(New Point(LM, y), New Size(IW, 44))
            noteCard.Controls.Add(InfoLabel(
                "Task Manager and Registry changes take effect immediately. CMD requires reopening.",
                New Point(14, 10)))
            page.Controls.Add(noteCard)

            Return page
        End Function

        Private Function BuildSecurityPage() As Panel
            Dim page = ScrollPage()
            Dim y = 16

            page.Controls.Add(PageTitle("Security", New Point(LM, y))) : y += 34

            ' PASSWORD card
            Dim pinCard = CardPanel(New Point(LM, y), New Size(IW, 176))
            Dim cy = 14
            pinCard.Controls.Add(SectionLabel("Change Admin PASSWORD", New Point(14, cy))) : cy += 26

            pinCard.Controls.Add(SmallLabel("Current PASSWORD", New Point(14, cy))) : cy += 20
            _txtCurrentPin = DarkTextBox(New Point(14, cy), 190, "", pwChar:="●"c) : pinCard.Controls.Add(_txtCurrentPin)
            cy += 36

            pinCard.Controls.Add(SmallLabel("New PASSWORD", New Point(14, cy)))
            pinCard.Controls.Add(SmallLabel("Confirm PASSWORD", New Point(224, cy))) : cy += 20
            _txtPin  = DarkTextBox(New Point(14,  cy), 190, "", pwChar:="●"c) : pinCard.Controls.Add(_txtPin)
            _txtPin2 = DarkTextBox(New Point(224, cy), 190, "", pwChar:="●"c) : pinCard.Controls.Add(_txtPin2)

            page.Controls.Add(pinCard) : y += 186

            ' Warnings card
            Dim warnCard = CardPanel(New Point(LM, y), New Size(IW, 100))
            cy = 14
            warnCard.Controls.Add(SectionLabel("Low-Time Warnings", New Point(14, cy))) : cy += 26
            _chkWarn5 = DarkCheck("Warn at 5 minutes remaining", New Point(14, cy), AppConfig.WarnAt5Min) : warnCard.Controls.Add(_chkWarn5) : cy += 28
            _chkWarn1 = DarkCheck("Warn at 1 minute remaining",  New Point(14, cy), AppConfig.WarnAt1Min) : warnCard.Controls.Add(_chkWarn1)

            page.Controls.Add(warnCard) : y += 110

            ' Screen monitoring card
            Dim capCard = CardPanel(New Point(LM, y), New Size(IW, 140))
            cy = 14
            capCard.Controls.Add(SectionLabel("Screen Monitoring", New Point(14, cy))) : cy += 26
            _chkCapture = DarkCheck(
                "Enable remote screen capture",
                New Point(14, cy), AppConfig.ScreenCaptureEnabled)
            capCard.Controls.Add(_chkCapture) : cy += 30

            capCard.Controls.Add(SmallLabel("Interval (sec)", New Point(14, cy)))
            capCard.Controls.Add(SmallLabel("JPEG Quality", New Point(160, cy))) : cy += 18
            _nudInterval = DarkNud(New Point(14,  cy), 80, AppConfig.ScreenCaptureIntervalSec, 3,  60)
            _nudQuality  = DarkNud(New Point(160, cy), 80, AppConfig.ScreenCaptureQuality,     30, 100)
            capCard.Controls.Add(_nudInterval)
            capCard.Controls.Add(_nudQuality)

            page.Controls.Add(capCard)

            Return page
        End Function

        Private Function BuildNotificationsPage() As Panel
            Dim page = ScrollPage()
            Dim y = 16

            page.Controls.Add(PageTitle("Notifications", New Point(LM, y))) : y += 34

            ' Toast card
            Dim toastCard = CardPanel(New Point(LM, y), New Size(IW, 100))
            Dim cy = 14
            toastCard.Controls.Add(SectionLabel("On-Screen Notifications", New Point(14, cy))) : cy += 26
            _chkNotifs = DarkCheck(
                "Show animated toast notifications",
                New Point(14, cy), AppConfig.NotificationsEnabled)
            toastCard.Controls.Add(_chkNotifs) : cy += 28
            toastCard.Controls.Add(InfoLabel(
                "Toasts slide in from the right edge and auto-dismiss after 4 seconds.",
                New Point(14, cy)))

            page.Controls.Add(toastCard) : y += 110

            ' Voice card
            Dim voiceCard = CardPanel(New Point(LM, y), New Size(IW, 170))
            cy = 14
            voiceCard.Controls.Add(SectionLabel("Voice Announcements", New Point(14, cy))) : cy += 26
            _chkVoice = DarkCheck(
                "Read notifications aloud using Windows TTS",
                New Point(14, cy), AppConfig.VoiceEnabled)
            voiceCard.Controls.Add(_chkVoice) : cy += 30

            voiceCard.Controls.Add(SmallLabel("Volume (10-100)", New Point(14, cy))) : cy += 18
            _nudVolume = DarkNud(New Point(14, cy), 80, AppConfig.VoiceVolume, 10, 100)
            voiceCard.Controls.Add(_nudVolume) : cy += 34

            voiceCard.Controls.Add(SmallLabel("Voice", New Point(14, cy))) : cy += 18
            _cmbVoice = DarkCombo(New Point(14, cy), IW - 32, Nothing, "")
            _cmbVoice.Items.Add("(Auto -- prefer female voice)")
            Dim voiceNames = NotificationService.GetInstalledVoiceNames()
            For Each vn In voiceNames
                _cmbVoice.Items.Add(vn)
            Next
            Dim saved = AppConfig.VoiceName.Trim()
            If String.IsNullOrEmpty(saved) OrElse _cmbVoice.Items.Count = 1 Then
                _cmbVoice.SelectedIndex = 0
            Else
                Dim idx = _cmbVoice.FindStringExact(saved)
                _cmbVoice.SelectedIndex = If(idx >= 0, idx, 0)
            End If
            voiceCard.Controls.Add(_cmbVoice)

            page.Controls.Add(voiceCard) : y += 180

            Dim noteCard = CardPanel(New Point(LM, y), New Size(IW, 44))
            noteCard.Controls.Add(InfoLabel(
                "Voice works even when toasts are disabled -- ideal for full-screen games.",
                New Point(14, 10)))
            page.Controls.Add(noteCard)

            Return page
        End Function

        ' ── Preview paint handlers ─────────────────────────────────────────────

        Private Sub InvalidateTimerPreview(sender As Object, e As EventArgs)
            _timerPreview?.Invalidate()
        End Sub

        Private Sub InvalidateLockPreview(sender As Object, e As EventArgs)
            _lockPreview?.Invalidate()
            _lockTabPreview?.Invalidate()
        End Sub

        Private Sub OnPaintWpPreview(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            e.Graphics.Clear(Color.FromArgb(10, 14, 23))
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit

            Dim path = _txtImgPath?.Text
            If Not String.IsNullOrEmpty(path) AndAlso File.Exists(path) Then
                Try
                    Using img = Image.FromFile(path)
                        Dim fit = If(_cmbBgFit?.SelectedItem?.ToString(), "Contain")
                        Dim w, h As Integer
                        Select Case fit
                            Case "Cover"
                                Dim s = Math.Max(CSng(pnl.Width) / img.Width, CSng(pnl.Height) / img.Height)
                                w = CInt(img.Width * s) : h = CInt(img.Height * s)
                            Case "Stretch"
                                w = pnl.Width : h = pnl.Height
                            Case Else
                                Dim s = Math.Min(CSng(pnl.Width) / img.Width, CSng(pnl.Height) / img.Height)
                                w = CInt(img.Width * s) : h = CInt(img.Height * s)
                        End Select
                        e.Graphics.DrawImage(img, (pnl.Width - w) \ 2, (pnl.Height - h) \ 2, w, h)
                    End Using
                Catch
                End Try
            Else
                ' Show placeholder
                Using f = New Font("Segoe UI", 9)
                    Using br = New SolidBrush(ColInfo)
                        Dim sf = New StringFormat() With {
                            .Alignment = StringAlignment.Center,
                            .LineAlignment = StringAlignment.Center
                        }
                        e.Graphics.DrawString("No image selected" & vbCrLf & "Drop an image here",
                            f, br, New RectangleF(0, 0, pnl.Width, pnl.Height), sf)
                    End Using
                End Using
            End If
        End Sub

        Private Sub OnWpDragEnter(sender As Object, e As DragEventArgs)
            If e.Data.GetDataPresent(DataFormats.FileDrop) Then
                Dim files = CType(e.Data.GetData(DataFormats.FileDrop), String())
                If files IsNot Nothing AndAlso files.Length > 0 Then
                    Dim ext = Path.GetExtension(files(0)).ToLower()
                    If ext = ".jpg" OrElse ext = ".jpeg" OrElse ext = ".png" OrElse ext = ".bmp" OrElse ext = ".webp" Then
                        e.Effect = DragDropEffects.Copy
                        _wpPreview.BorderStyle = BorderStyle.Fixed3D
                        Return
                    End If
                End If
            End If
            e.Effect = DragDropEffects.None
        End Sub

        Private Sub OnWpDragDrop(sender As Object, e As DragEventArgs)
            _wpPreview.BorderStyle = BorderStyle.FixedSingle
            If e.Data.GetDataPresent(DataFormats.FileDrop) Then
                Dim files = CType(e.Data.GetData(DataFormats.FileDrop), String())
                If files IsNot Nothing AndAlso files.Length > 0 Then
                    _txtImgPath.Text = files(0)
                End If
            End If
        End Sub

        Private Sub OnPaintTimerPreview(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim greenClr = _currentTimerTimeColor
            Dim dimClr = Color.FromArgb(100, 116, 139)

            e.Graphics.Clear(Color.FromArgb(18, 22, 38))
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit

            Dim showDot = _chkTimerDot.Checked
            Dim showPc = _chkTimerPcLabel.Checked
            Dim pcAbove = (If(_cmbTimerPcPos.SelectedItem?.ToString(), "Above") = "Above")

            Dim pw = pnl.Width
            Dim ph = pnl.Height

            If showDot Then
                Using br = New SolidBrush(greenClr)
                    e.Graphics.FillEllipse(br, pw - 13, 5, 9, 9)
                End Using
            End If

            Dim sfC = New StringFormat() With {
                .Alignment = StringAlignment.Center,
                .LineAlignment = StringAlignment.Center
            }

            Dim pcText = $"PC {AppConfig.PCNumber:D2}"

            If showPc AndAlso pcAbove Then
                Using pcBr = New SolidBrush(dimClr)
                    e.Graphics.DrawString(pcText, New Font("Segoe UI", 7), pcBr,
                        New RectangleF(2, 4, pw - 16, 14), sfC)
                End Using
                Using timeBr = New SolidBrush(greenClr)
                    e.Graphics.DrawString("12:34", New Font("Segoe UI", 15, FontStyle.Bold), timeBr,
                        New RectangleF(0, 16, pw, ph - 16), sfC)
                End Using
            ElseIf showPc Then
                Using timeBr = New SolidBrush(greenClr)
                    e.Graphics.DrawString("12:34", New Font("Segoe UI", 15, FontStyle.Bold), timeBr,
                        New RectangleF(0, 4, pw - 44, ph - 8), sfC)
                End Using
                Using pcBr = New SolidBrush(dimClr)
                    e.Graphics.DrawString(pcText, New Font("Segoe UI", 6.5F), pcBr,
                        New RectangleF(pw - 42, (ph - 14) \ 2, 38, 14), New StringFormat() With {
                            .Alignment = StringAlignment.Near, .LineAlignment = StringAlignment.Center
                        })
                End Using
            Else
                Using timeBr = New SolidBrush(greenClr)
                    e.Graphics.DrawString("12:34", New Font("Segoe UI", 15, FontStyle.Bold), timeBr,
                        New RectangleF(0, 4, pw, ph - 8), sfC)
                End Using
            End If
        End Sub

        Private Sub OnPaintLockPreview(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim pw = pnl.Width
            Dim ph = pnl.Height

            e.Graphics.Clear(_currentBgColor)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit

            Const REF_H As Single = 1080.0F
            Dim scale = ph / REF_H

            Dim msgText = If(String.IsNullOrWhiteSpace(_txtMsg?.Text), "Insert Coins to Start", _txtMsg.Text)
            Dim msgPt = Math.Max(4.0F, CInt(_nudMsgSize.Value) * scale)
            Dim msgYPct = CInt(_nudMsgY.Value) / 100.0F

            Using msgFont = New Font("Segoe UI", msgPt, FontStyle.Bold)
                Dim msgSz = e.Graphics.MeasureString(msgText, msgFont)
                Dim msgX = If(_chkMsgCenterX.Checked,
                               (pw - msgSz.Width) / 2.0F,
                               (pw - msgSz.Width) * CSng(AppConfig.LockMsgXPct) / 100.0F)
                Dim msgY = (ph - msgSz.Height) * msgYPct
                Using msgBr = New SolidBrush(_currentMsgColor)
                    e.Graphics.DrawString(msgText, msgFont, msgBr, msgX, msgY)
                End Using

                Dim subText = $"Go to the PisoNet unit and select PC {AppConfig.PCNumber:D2}"
                Dim subPt = Math.Max(3.0F, 14 * scale)
                Using subFont = New Font("Segoe UI", subPt)
                    Dim subSz = e.Graphics.MeasureString(subText, subFont)
                    Dim subX = (pw - subSz.Width) / 2.0F
                    Dim subY = msgY + msgSz.Height + 2 * scale
                    Using subBr = New SolidBrush(Color.FromArgb(120, 140, 180))
                        e.Graphics.DrawString(subText, subFont, subBr, subX, subY)
                    End Using
                End Using
            End Using

            Dim pcText = $"PC {AppConfig.PCNumber:D2}"
            Dim pcPt = Math.Max(3.0F, CInt(_nudPcLblSize.Value) * scale)
            Dim pcYPct = CInt(_nudPcLblY.Value) / 100.0F

            Using pcFont = New Font("Segoe UI", pcPt)
                Dim pcSz = e.Graphics.MeasureString(pcText, pcFont)
                Dim pcX = If(_chkPcLblCenterX.Checked,
                              (pw - pcSz.Width) / 2.0F,
                              pw * CSng(AppConfig.LockPcLabelXPct) / 100.0F)
                Dim pcY = ph * pcYPct
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

        ' ── License page ─────────────────────────────────────────────────────

        Private Function BuildLicensePage() As Panel
            Dim page = ScrollPage()
            Dim y = 16

            page.Controls.Add(PageTitle("License", New Point(LM, y))) : y += 34

            ' Status card
            Dim statusCard = CardPanel(New Point(LM, y), New Size(IW, 200))
            Dim cy = 14
            statusCard.Controls.Add(SectionLabel("Activation Status", New Point(14, cy))) : cy += 26

            _lblLicStatus = New Label() With {
                .AutoSize = True,
                .Location = New Point(14, cy),
                .Font = New Font("Segoe UI", 11, FontStyle.Bold)
            }
            statusCard.Controls.Add(_lblLicStatus) : cy += 30

            statusCard.Controls.Add(SmallLabel("Device ID", New Point(14, cy))) : cy += 18
            _lblLicDeviceId = New Label() With {
                .Text = LicenseService.GetDeviceId(),
                .AutoSize = False,
                .Size = New Size(IW - 60, 18),
                .Location = New Point(14, cy),
                .ForeColor = ColInfo,
                .Font = New Font("Consolas", 8)
            }
            statusCard.Controls.Add(_lblLicDeviceId) : cy += 24

            statusCard.Controls.Add(SmallLabel("Expires At", New Point(14, cy)))
            statusCard.Controls.Add(SmallLabel("Last Verified", New Point(224, cy))) : cy += 18
            _lblLicExpiry = New Label() With {
                .Text = If(String.IsNullOrEmpty(AppConfig.LicenseExpiresAt), "Lifetime / N/A", AppConfig.LicenseExpiresAt),
                .AutoSize = True, .Location = New Point(14, cy), .ForeColor = ColSmall
            }
            _lblLicVerified = New Label() With {
                .Text = If(String.IsNullOrEmpty(AppConfig.LicenseLastVerified), "—", AppConfig.LicenseLastVerified),
                .AutoSize = True, .Location = New Point(224, cy), .ForeColor = ColSmall
            }
            statusCard.Controls.Add(_lblLicExpiry)
            statusCard.Controls.Add(_lblLicVerified)

            page.Controls.Add(statusCard) : y += 210

            ' Activate card
            Dim actCard = CardPanel(New Point(LM, y), New Size(IW, 160))
            cy = 14
            actCard.Controls.Add(SectionLabel("Enter License Key", New Point(14, cy))) : cy += 26

            actCard.Controls.Add(SmallLabel("License Key", New Point(14, cy))) : cy += 18
            _txtLicenseKey = DarkTextBox(New Point(14, cy), IW - 32, LicenseService.GetMaskedKey())
            _txtLicenseKey.MaxLength = 24
            _txtLicenseKey.Font = New Font("Consolas", 10)
            _txtLicenseKey.CharacterCasing = CharacterCasing.Upper
            actCard.Controls.Add(_txtLicenseKey) : cy += 34

            Dim btnActivate = New Button() With {
                .Text = "Activate",
                .Location = New Point(14, cy),
                .Size = New Size(120, 32),
                .BackColor = ColAccent,
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat,
                .Cursor = Cursors.Hand,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold)
            }
            btnActivate.FlatAppearance.BorderSize = 0
            AddHandler btnActivate.Click, AddressOf OnActivateLicense
            actCard.Controls.Add(btnActivate)

            Dim btnDeactivate = New Button() With {
                .Text = "Deactivate",
                .Location = New Point(146, cy),
                .Size = New Size(120, 32),
                .BackColor = ColDanger,
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat,
                .Cursor = Cursors.Hand,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .Enabled = LicenseService.IsActivated()
            }
            btnDeactivate.FlatAppearance.BorderSize = 0
            AddHandler btnDeactivate.Click, AddressOf OnDeactivateLicense
            actCard.Controls.Add(btnDeactivate) : cy += 38

            _lblLicResult = New Label() With {
                .AutoSize = True, .Location = New Point(14, cy),
                .ForeColor = ColInfo, .Text = ""
            }
            actCard.Controls.Add(_lblLicResult)

            page.Controls.Add(actCard)

            ' Update status display
            UpdateLicenseStatusDisplay()

            Return page
        End Function

        Private Sub UpdateLicenseStatusDisplay()
            If _lblLicStatus Is Nothing Then Return

            If LicenseService.BetaMode Then
                _lblLicStatus.Text = "Beta — All features unlocked"
                _lblLicStatus.ForeColor = Color.FromArgb(34, 197, 94)
                Return
            End If

            Dim status = LicenseService.GetStatus()
            Select Case status
                Case LicenseStatus.Activated
                    _lblLicStatus.Text = "Activated"
                    _lblLicStatus.ForeColor = Color.FromArgb(34, 197, 94)
                Case LicenseStatus.Trial
                    _lblLicStatus.Text = $"Trial — {LicenseService.TrialDaysRemaining()} day(s) remaining"
                    _lblLicStatus.ForeColor = Color.FromArgb(245, 158, 11)
                Case LicenseStatus.OfflineLocked
                    _lblLicStatus.Text = "Offline Locked — Cannot verify activation"
                    _lblLicStatus.ForeColor = Color.FromArgb(239, 68, 68)
                Case LicenseStatus.Expired
                    _lblLicStatus.Text = "Expired"
                    _lblLicStatus.ForeColor = Color.FromArgb(239, 68, 68)
            End Select
        End Sub

        Private Async Sub OnActivateLicense(sender As Object, e As EventArgs)
            Dim key = _txtLicenseKey.Text.Trim().ToUpper()
            If String.IsNullOrEmpty(key) Then
                _lblLicResult.Text = "Please enter a license key."
                _lblLicResult.ForeColor = Color.FromArgb(245, 158, 11)
                Return
            End If

            _lblLicResult.Text = "Activating..."
            _lblLicResult.ForeColor = ColInfo
            CType(sender, Button).Enabled = False

            Dim result = Await LicenseService.ActivateAsync(key)

            If result.Success Then
                _lblLicResult.Text = "License activated successfully!"
                _lblLicResult.ForeColor = Color.FromArgb(34, 197, 94)
                _lblLicExpiry.Text = If(String.IsNullOrEmpty(result.ExpiresAt), "Lifetime", result.ExpiresAt)
                _lblLicVerified.Text = DateTime.UtcNow.ToString("o")
            Else
                _lblLicResult.Text = result.ErrorMessage
                _lblLicResult.ForeColor = Color.FromArgb(239, 68, 68)
            End If

            CType(sender, Button).Enabled = True
            UpdateLicenseStatusDisplay()
        End Sub

        Private Async Sub OnDeactivateLicense(sender As Object, e As EventArgs)
            Dim answer = MessageBox.Show(
                "Deactivate this license? The software will revert to trial mode.",
                "Deactivate", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If answer <> DialogResult.Yes Then Return

            _lblLicResult.Text = "Deactivating..."
            _lblLicResult.ForeColor = ColInfo

            Await LicenseService.DeactivateAsync()

            _lblLicResult.Text = "License deactivated."
            _lblLicResult.ForeColor = Color.FromArgb(245, 158, 11)
            _txtLicenseKey.Text = ""
            _lblLicExpiry.Text = "N/A"
            _lblLicVerified.Text = "—"
            CType(sender, Button).Enabled = False
            UpdateLicenseStatusDisplay()
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            Dim newPin  = _txtPin.Text.Trim()
            Dim newPin2 = _txtPin2.Text.Trim()
            Dim currentPin = _txtCurrentPin.Text.Trim()
            If newPin.Length > 0 Then
                If currentPin <> AppConfig.AdminPin Then
                    MessageBox.Show("Current PASSWORD is incorrect.", "Validation",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning) : Return
                End If
                If newPin.Length < 4 Then
                    MessageBox.Show("New password must be at least 4 characters.", "Validation",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning) : Return
                End If
                If newPin <> newPin2 Then
                    MessageBox.Show("Passwords do not match.", "Validation",
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
            Dim selectedVoice = If(_cmbVoice.SelectedIndex <= 0, "",
                                   _cmbVoice.SelectedItem?.ToString())
            AppConfig.SaveVoiceName(If(selectedVoice, ""))

            ' Lock screen: timer overlay
            AppConfig.SaveTimerTimeArgb(_currentTimerTimeColor.ToArgb())
            AppConfig.SaveTimerLowTimeArgb(_currentTimerLowColor.ToArgb())
            AppConfig.SaveTimerShowConnDot(_chkTimerDot.Checked)
            AppConfig.SaveTimerShowPcLabel(_chkTimerPcLabel.Checked)
            AppConfig.SaveTimerPcLabelPosition(If(_cmbTimerPcPos.SelectedItem?.ToString(), "Above"))

            ' Lock screen: text styling
            AppConfig.SaveLockMsgForeArgb(_currentMsgColor.ToArgb())
            AppConfig.SaveLockMsgSize(CInt(_nudMsgSize.Value))
            AppConfig.SaveLockMsgCenterX(_chkMsgCenterX.Checked)
            AppConfig.SaveLockMsgYPct(CInt(_nudMsgY.Value))
            AppConfig.SaveLockPcLabelForeArgb(_currentPcLblColor.ToArgb())
            AppConfig.SaveLockPcLabelSize(CInt(_nudPcLblSize.Value))
            AppConfig.SaveLockPcLabelCenterX(_chkPcLblCenterX.Checked)
            AppConfig.SaveLockPcLabelYPct(CInt(_nudPcLblY.Value))

            ' Wallpaper
            AppConfig.SaveUseLocalWallpaper(_chkUseLocalWp.Checked)

            WindowsPolicy.Apply()
            RaiseEvent SettingsSaved()

            MessageBox.Show("Settings saved." & vbCrLf &
                            "Note: Server URL and PC Number require a client restart.",
                            "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End Sub

        Private Sub OnExitApp(sender As Object, e As EventArgs)
            Dim result = MessageBox.Show(
                "Exit Pisonex Client?" & vbCrLf &
                "Windows restrictions will be removed and the lock screen will close.",
                "Exit Application", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            If result = DialogResult.Yes Then RaiseEvent ExitRequested()
        End Sub

        ' ── UI factory helpers ────────────────────────────────────────────────

        Private Shared Function ScrollPage() As Panel
            Return New Panel() With {
                .AutoScroll = True,
                .BackColor  = ColContentBg,
                .Padding    = New Padding(0)
            }
        End Function

        Private Shared Function PageTitle(text As String, loc As Point) As Label
            Return New Label() With {
                .Text      = text,
                .AutoSize  = True,
                .Location  = loc,
                .Font      = New Font("Segoe UI", 14, FontStyle.Bold),
                .ForeColor = Color.White
            }
        End Function

        Private Shared Function CardPanel(loc As Point, sz As Size) As Panel
            Dim pnl = New Panel() With {
                .Location  = loc,
                .Size      = sz,
                .BackColor = ColCardBg
            }
            AddHandler pnl.Paint, Sub(s, e)
                Dim p = CType(s, Panel)
                Using gp = New GraphicsPath()
                    Dim r = 8
                    gp.AddArc(0, 0, r, r, 180, 90)
                    gp.AddArc(p.Width - r - 1, 0, r, r, 270, 90)
                    gp.AddArc(p.Width - r - 1, p.Height - r - 1, r, r, 0, 90)
                    gp.AddArc(0, p.Height - r - 1, r, r, 90, 90)
                    gp.CloseFigure()
                    Using pen = New Pen(ColCardBorder)
                        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
                        e.Graphics.DrawPath(pen, gp)
                    End Using
                End Using
            End Sub
            Return pnl
        End Function

        Private Shared Function SectionLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text      = text,
                .AutoSize  = True,
                .Location  = loc,
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = ColSection
            }
        End Function

        Private Shared Function InfoLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text      = text,
                .AutoSize  = False,
                .Size      = New Size(IW - 32, 30),
                .Location  = loc,
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = ColInfo
            }
        End Function

        Private Shared Function SmallLabel(text As String, loc As Point) As Label
            Return New Label() With {
                .Text      = text,
                .AutoSize  = True,
                .Location  = loc,
                .Font      = New Font("Segoe UI", 8),
                .ForeColor = ColSmall
            }
        End Function

        Private Shared Function DarkTextBox(loc As Point, width As Integer, text As String,
                                            Optional pwChar As Char = Nothing) As TextBox
            Dim tb = New TextBox() With {
                .Text        = text,
                .Location    = loc,
                .Width       = width,
                .BackColor   = ColInputBg,
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
                .BackColor = ColInputBg,
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
                .ForeColor = ColText,
                .FlatStyle = FlatStyle.Flat,
                .Font      = New Font("Segoe UI", 9)
            }
        End Function

        Private Shared Function DarkCombo(loc As Point, width As Integer,
                                          items() As String, selected As String) As ComboBox
            Dim cmb = New ComboBox() With {
                .Location      = loc,
                .Width         = width,
                .DropDownStyle = ComboBoxStyle.DropDownList,
                .BackColor     = ColInputBg,
                .ForeColor     = Color.White,
                .FlatStyle     = FlatStyle.Flat,
                .Font          = New Font("Segoe UI", 9)
            }
            If items IsNot Nothing Then
                cmb.Items.AddRange(items)
                If Not String.IsNullOrEmpty(selected) Then cmb.SelectedItem = selected
            End If
            Return cmb
        End Function

        Private Shared Function ColorSwatch(loc As Point, color As Color) As PictureBox
            Return New PictureBox() With {
                .Location    = loc,
                .Size        = New Size(42, 22),
                .BackColor   = color,
                .BorderStyle = BorderStyle.FixedSingle,
                .Cursor      = Cursors.Hand
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

            ' Hover effect
            Dim origColor = bgColor
            AddHandler b.MouseEnter, Sub(s, e)
                b.BackColor = Color.FromArgb(
                    Math.Min(255, origColor.R + 20),
                    Math.Min(255, origColor.G + 20),
                    Math.Min(255, origColor.B + 20))
            End Sub
            AddHandler b.MouseLeave, Sub(s, e) b.BackColor = origColor

            ' Rounded corners
            Try
                Dim gp = New GraphicsPath()
                Dim r = 6
                gp.AddArc(0, 0, r, r, 180, 90)
                gp.AddArc(b.Width - r - 1, 0, r, r, 270, 90)
                gp.AddArc(b.Width - r - 1, b.Height - r - 1, r, r, 0, 90)
                gp.AddArc(0, b.Height - r - 1, r, r, 90, 90)
                gp.CloseFigure()
                b.Region = New Region(gp)
            Catch
            End Try

            Return b
        End Function

        Private Shared Function MakeSmallBtn(text As String, loc As Point) As Button
            Dim b = New Button() With {
                .Text      = text,
                .Location  = loc,
                .Size      = New Size(88, 26),
                .BackColor = Color.FromArgb(42, 46, 64),
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat,
                .Font      = New Font("Segoe UI", 8),
                .Cursor    = Cursors.Hand
            }
            b.FlatAppearance.BorderSize = 0

            Dim origColor = b.BackColor
            AddHandler b.MouseEnter, Sub(s, e)
                b.BackColor = Color.FromArgb(62, 66, 84)
            End Sub
            AddHandler b.MouseLeave, Sub(s, e) b.BackColor = origColor

            Return b
        End Function

    End Class

End Namespace
