Imports System.Drawing
Imports System.Windows.Forms
Imports PisoNetClient.Services

Namespace Forms

    ''' <summary>
    ''' Password-change modal, used in two modes:
    '''
    ''' Forced (forced:=True, the default) — shown immediately after a member logs
    ''' in with an admin-issued temp password (MemberLoginResponse.must_change_password
    ''' = True). No cancel/close option, no "current password" field (the login that
    ''' just happened already proved identity). Blocks until
    ''' POST /api/member/change-password succeeds. LockManager defers any pending PC
    ''' unlock while this is open (see LockManager.ShowChangePasswordDialog).
    '''
    ''' Voluntary (forced:=False) — opened from the tray icon's "Change Password" item
    ''' when a member is already logged in on this PC. Freely cancelable, and requires
    ''' the member's CURRENT password as a third field — unlike the forced flow, there's
    ''' no just-completed login proving whoever is at the keyboard is actually the
    ''' account owner (the session may have been sitting logged-in for a while), so the
    ''' server re-verifies the current password before accepting the change.
    ''' </summary>
    Public Class ChangePasswordForm
        Inherits Form

        Private ReadOnly _memberSvc As MemberService
        Private ReadOnly _pcNumber As Integer
        Private ReadOnly _forced As Boolean

        Private _txtOld     As TextBox   ' only used when Not _forced
        Private _txtNew     As TextBox
        Private _txtConfirm As TextBox
        Private _lblError   As Label
        Private _btnSubmit  As Button

        Public Sub New(memberSvc As MemberService, pcNumber As Integer, username As String,
                       Optional forced As Boolean = True)
            _memberSvc = memberSvc
            _pcNumber = pcNumber
            _forced = forced
            InitializeComponent(username)
        End Sub

        Private Sub InitializeComponent(username As String)
            Me.Size = New Size(440, If(_forced, 316, 372))
            Me.BackColor = FormStyles.DarkBg
            Me.ForeColor = FormStyles.TextPrimary

            Dim lblTitle = New Label() With {
                .Text = If(_forced, "Set a New Password", "Change Your Password"),
                .Font = New Font("Segoe UI", 15, FontStyle.Bold),
                .ForeColor = FormStyles.TextPrimary,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 30),
                .Location = New Point(24, 16),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Dim lblSub = New Label() With {
                .Text = If(_forced,
                    $"Your account ""{username}"" was created with a temporary password. Choose a new password to continue.",
                    "Enter your current password and choose a new one."),
                .Font = New Font("Segoe UI", 9.5F),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 44),
                .Location = New Point(24, 50),
                .TextAlign = ContentAlignment.TopCenter
            }

            Dim y = 102
            Dim lblOld As Label = Nothing

            If Not _forced Then
                lblOld = FormStyles.CreateLabel("Current Password")
                lblOld.Location = New Point(24, y)
                _txtOld = FormStyles.CreateInput(New Point(24, y + 20), 392, maxLen:=128, pwChar:="●"c)
                AddHandler _txtOld.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then _txtNew.Focus()
                y += 56
            End If

            Dim lblNew = FormStyles.CreateLabel("New Password (min. 6 characters)")
            lblNew.Location = New Point(24, y)
            _txtNew = FormStyles.CreateInput(New Point(24, y + 20), 392, maxLen:=128, pwChar:="●"c)
            AddHandler _txtNew.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then _txtConfirm.Focus()
            y += 56

            Dim lblConfirm = FormStyles.CreateLabel("Confirm New Password")
            lblConfirm.Location = New Point(24, y)
            _txtConfirm = FormStyles.CreateInput(New Point(24, y + 20), 392, maxLen:=128, pwChar:="●"c)
            AddHandler _txtConfirm.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then OnSubmitClick(s, e)
            y += 56

            _lblError = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(239, 68, 68),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 36),
                .Location = New Point(24, y),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible = False
            }
            y += 42

            _btnSubmit = FormStyles.CreateButton(If(_forced, "Set Password", "Change Password"), 392, 42)
            _btnSubmit.Location = New Point(24, y)
            AddHandler _btnSubmit.Click, AddressOf OnSubmitClick

            Dim controls As New List(Of Control) From {lblTitle, lblSub, lblNew, _txtNew, lblConfirm, _txtConfirm, _lblError, _btnSubmit}
            If Not _forced Then controls.AddRange({lblOld, _txtOld})
            Me.Controls.AddRange(controls.ToArray())
            Me.AcceptButton = _btnSubmit

            ' Forced mode: no close button, dialog can't be dismissed except via success.
            ' Voluntary mode: normal closable dialog — the member can back out any time.
            FormStyles.MakeBorderless(Me, If(_forced, "Password Change Required", "Change Password"), closable:=Not _forced)
        End Sub

        Private Async Sub OnSubmitClick(sender As Object, e As EventArgs)
            _lblError.Visible = False
            Dim oldPass = If(_forced, "", _txtOld.Text)
            Dim pass1 = _txtNew.Text
            Dim pass2 = _txtConfirm.Text

            If Not _forced AndAlso oldPass.Length = 0 Then
                ShowError("Enter your current password.")
                Return
            End If
            If pass1.Length < 6 Then
                ShowError("Password must be at least 6 characters.")
                Return
            End If
            If pass1 <> pass2 Then
                ShowError("Passwords do not match.")
                Return
            End If

            _btnSubmit.Enabled = False
            _btnSubmit.Text = "Saving..."
            Try
                Dim result = Await _memberSvc.ChangePasswordAsync(_pcNumber, pass1, oldPassword:=oldPass)
                If result.success Then
                    Me.DialogResult = DialogResult.OK
                    Me.Close()
                Else
                    ShowError(If(result.[error], "Failed to change password. Try again."))
                    _btnSubmit.Enabled = True
                    _btnSubmit.Text = If(_forced, "Set Password", "Change Password")
                End If
            Catch ex As Exception
                ShowError($"Connection error: {ex.Message}")
                _btnSubmit.Enabled = True
                _btnSubmit.Text = If(_forced, "Set Password", "Change Password")
            End Try
        End Sub

        Private Sub ShowError(msg As String)
            _lblError.Text = msg
            _lblError.Visible = True
        End Sub

        ''' <summary>
        ''' In forced mode, blocks any attempt to dismiss the dialog without a
        ''' successful password change (Alt+F4, taskbar close, etc. — FormBorderStyle.None
        ''' already removes the system close button via MakeBorderless(closable:=False)).
        ''' In voluntary mode, closing normally is allowed.
        ''' </summary>
        Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
            If _forced AndAlso Me.DialogResult <> DialogResult.OK Then
                e.Cancel = True
                Return
            End If
            MyBase.OnFormClosing(e)
        End Sub

    End Class

End Namespace
