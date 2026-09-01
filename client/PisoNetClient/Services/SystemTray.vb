Imports System.Windows.Forms
Imports System.Drawing
Imports PisoNetClient.Resources

Namespace Services

    ''' <summary>
    ''' System tray icon showing PisoNet status.
    ''' Right-click menu provides access to the admin panel.
    ''' </summary>
    Public Class SystemTray
        Implements IDisposable

        Private ReadOnly _notify    As NotifyIcon
        Private _timerItem          As ToolStripMenuItem
        Private _memberLoginItem    As ToolStripMenuItem
        Private _memberLoginSep     As ToolStripSeparator
        Private _redeemPointsItem   As ToolStripMenuItem
        Private _memberLoggedInUsername As String = ""   ' "" = nobody logged in on this PC
        Private _disposed           As Boolean = False

        ''' <summary>Raised when the user clicks "Admin Panel..." in the tray menu.</summary>
        Public Event AdminPanelRequested()
        ''' <summary>Raised when the user clicks the Show/Hide Timer menu item.</summary>
        Public Event TimerToggleRequested()
        ''' <summary>Raised when the member menu item is clicked while nobody is logged in.</summary>
        Public Event MemberLoginRequested()
        ''' <summary>Raised when the member menu item is clicked while a member IS logged in.</summary>
        Public Event MemberChangePasswordRequested(username As String)
        ''' <summary>Raised when "Redeem Points..." is clicked (only visible while a member is logged in and points are enabled).</summary>
        Public Event RedeemPointsRequested(username As String)

        Public Sub New()
            _notify = New NotifyIcon()
            _notify.Icon  = LogoHelper.GetIcon()
            _notify.Text  = "Pisonex — Waiting"
            _notify.Visible = True

            Dim menu = New ContextMenuStrip()
            menu.BackColor = Color.FromArgb(26, 30, 45)
            menu.ForeColor = Color.White
            menu.Renderer  = New DarkMenuRenderer()

            Dim title = CType(menu.Items.Add("Pisonex Client v1.0"), ToolStripMenuItem)
            title.Enabled  = False
            title.ForeColor = Color.FromArgb(100, 116, 139)

            menu.Items.Add(New ToolStripSeparator())

            _timerItem = New ToolStripMenuItem("Show Timer") With {
                .ForeColor = Color.White
            }
            AddHandler _timerItem.Click, Sub(s, e) RaiseEvent TimerToggleRequested()
            menu.Items.Add(_timerItem)

            ' Hidden until the server reports membership is enabled (see SetMembershipEnabled) —
            ' no point offering a login option for a feature the café isn't using.
            _memberLoginSep = New ToolStripSeparator() With {.Visible = False}
            menu.Items.Add(_memberLoginSep)

            _memberLoginItem = New ToolStripMenuItem("Member Login...") With {
                .ForeColor = Color.White,
                .Visible = False
            }
            AddHandler _memberLoginItem.Click, Sub(s, e)
                                                    If String.IsNullOrEmpty(_memberLoggedInUsername) Then
                                                        RaiseEvent MemberLoginRequested()
                                                    Else
                                                        RaiseEvent MemberChangePasswordRequested(_memberLoggedInUsername)
                                                    End If
                                                End Sub
            menu.Items.Add(_memberLoginItem)

            ' Hidden until a member is logged in AND the café has points enabled —
            ' see UpdateMemberMenuState.
            _redeemPointsItem = New ToolStripMenuItem("Redeem Points...") With {
                .ForeColor = Color.White,
                .Visible = False
            }
            AddHandler _redeemPointsItem.Click, Sub(s, e) RaiseEvent RedeemPointsRequested(_memberLoggedInUsername)
            menu.Items.Add(_redeemPointsItem)

            menu.Items.Add(New ToolStripSeparator())

            Dim adminItem = CType(menu.Items.Add("Admin Panel..."), ToolStripMenuItem)
            adminItem.ForeColor = Color.White
            AddHandler adminItem.Click, AddressOf OnAdminPanelClick

            _notify.ContextMenuStrip = menu
        End Sub

        ''' <summary>Sync the Show/Hide Timer label with the overlay's current visibility.</summary>
        Public Sub SetTimerVisible(visible As Boolean)
            If _disposed Then Return
            _timerItem.Text = If(visible, "Hide Timer", "Show Timer")
        End Sub

        ''' <summary>
        ''' Syncs the member menu item with the server's current membership_enabled
        ''' setting and login state (both from the heartbeat, via MembershipUpdated).
        ''' Hidden entirely when membership is off. When on, shows "Member Login..."
        ''' if nobody is logged in on this PC, or "Change Password" if a member is —
        ''' logging in again wouldn't make sense once already logged in.
        ''' "Redeem Points..." is a separate item, shown only when a member IS
        ''' logged in AND the café has points enabled (pointsEnabled, from the
        ''' same heartbeat as everything else here).
        ''' </summary>
        Public Sub UpdateMemberMenuState(enabled As Boolean, loggedInUsername As String, pointsEnabled As Boolean)
            If _disposed Then Return
            _memberLoggedInUsername = If(loggedInUsername, "")
            _memberLoginItem.Visible = enabled
            _memberLoginSep.Visible = enabled
            _memberLoginItem.Text = If(String.IsNullOrEmpty(_memberLoggedInUsername), "Member Login...", "Change Password")
            _redeemPointsItem.Visible = enabled AndAlso pointsEnabled AndAlso Not String.IsNullOrEmpty(_memberLoggedInUsername)
        End Sub

        ''' <summary>Update the tooltip shown when hovering the tray icon.</summary>
        Public Sub UpdateStatus(text As String)
            If _disposed Then Return
            ' NotifyIcon.Text max = 63 chars
            _notify.Text = If(text?.Length > 63, text.Substring(0, 63), text)
        End Sub

        ''' <summary>Show a balloon notification (e.g. low-time warning).</summary>
        Public Sub ShowBalloon(title As String, message As String,
                               Optional icon As ToolTipIcon = ToolTipIcon.Warning)
            If _disposed Then Return
            _notify.ShowBalloonTip(6000, title, message, icon)
        End Sub

        Private Sub OnAdminPanelClick(sender As Object, e As EventArgs)
            RaiseEvent AdminPanelRequested()
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                _notify.Visible = False
                _notify.Dispose()
            End If
        End Sub

        ' ── Minimal dark-theme renderer so the menu matches the app style ──
        Private Class DarkMenuRenderer
            Inherits ToolStripProfessionalRenderer

            Public Sub New()
                MyBase.New(New DarkColorTable())
            End Sub

            Protected Overrides Sub OnRenderItemText(e As ToolStripItemTextRenderEventArgs)
                If Not e.Item.Enabled Then
                    e.TextColor = Color.FromArgb(100, 116, 139)
                Else
                    e.TextColor = Color.White
                End If
                MyBase.OnRenderItemText(e)
            End Sub
        End Class

        Private Class DarkColorTable
            Inherits ProfessionalColorTable

            Public Overrides ReadOnly Property MenuItemSelected As Color
                Get
                    Return Color.FromArgb(50, 79, 142, 247)
                End Get
            End Property
            Public Overrides ReadOnly Property MenuBorder As Color
                Get
                    Return Color.FromArgb(42, 45, 62)
                End Get
            End Property
            Public Overrides ReadOnly Property ToolStripDropDownBackground As Color
                Get
                    Return Color.FromArgb(26, 30, 45)
                End Get
            End Property
            Public Overrides ReadOnly Property ImageMarginGradientBegin As Color
                Get
                    Return Color.FromArgb(26, 30, 45)
                End Get
            End Property
            Public Overrides ReadOnly Property ImageMarginGradientMiddle As Color
                Get
                    Return Color.FromArgb(26, 30, 45)
                End Get
            End Property
            Public Overrides ReadOnly Property ImageMarginGradientEnd As Color
                Get
                    Return Color.FromArgb(26, 30, 45)
                End Get
            End Property
        End Class

    End Class

End Namespace
