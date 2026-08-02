Imports System.Drawing
Imports System.Windows.Forms
Imports PisoNetClient.Services

Namespace Forms

    ''' <summary>
    ''' Standalone member login dialog, opened from the system tray icon's
    ''' "Member Login..." menu item (only shown when membership is enabled
    ''' server-side). Independent of LockForm's own inline member panel —
    ''' this works whether the PC is currently locked or unlocked, and can be
    ''' cancelled freely (unlike the forced ChangePasswordForm).
    ''' </summary>
    Public Class MemberLoginForm
        Inherits Form

        Private ReadOnly _memberSvc As MemberService
        Private ReadOnly _pcNumber As Integer

        Private _txtUsername As TextBox
        Private _txtPassword As TextBox
        Private _lblError As Label
        Private _btnLogin As Button

        ''' <summary>True once LoginAsync has succeeded — check after ShowDialog() returns.</summary>
        Public Property LoginSucceeded As Boolean = False
        Public Property LoggedInUsername As String = ""
        Public Property MustChangePassword As Boolean = False
        Public Property AbsorbedSeconds As Integer = 0

        Public Sub New(memberSvc As MemberService, pcNumber As Integer)
            _memberSvc = memberSvc
            _pcNumber = pcNumber
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.Size = New Size(440, 296)
            Me.BackColor = FormStyles.DarkBg
            Me.ForeColor = FormStyles.TextPrimary

            Dim lblTitle = New Label() With {
                .Text = "Member Login",
                .Font = New Font("Segoe UI", 15, FontStyle.Bold),
                .ForeColor = FormStyles.TextPrimary,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 30),
                .Location = New Point(24, 16),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Dim lblUser = FormStyles.CreateLabel("Username")
            lblUser.Location = New Point(24, 62)

            _txtUsername = FormStyles.CreateInput(New Point(24, 82), 392, maxLen:=20)
            AddHandler _txtUsername.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then _txtPassword.Focus()

            Dim lblPass = FormStyles.CreateLabel("Password")
            lblPass.Location = New Point(24, 118)

            _txtPassword = FormStyles.CreateInput(New Point(24, 138), 392, maxLen:=128, pwChar:="●"c)
            AddHandler _txtPassword.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then OnSubmitClick(s, e)

            _lblError = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(239, 68, 68),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 36),
                .Location = New Point(24, 168),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible = False
            }

            _btnLogin = FormStyles.CreateButton("Log In", 392, 42)
            _btnLogin.Location = New Point(24, 210)
            AddHandler _btnLogin.Click, AddressOf OnSubmitClick

            Me.Controls.AddRange({lblTitle, lblUser, _txtUsername, lblPass, _txtPassword, _lblError, _btnLogin})
            Me.AcceptButton = _btnLogin

            ' Closable — unlike ChangePasswordForm, this is a voluntary login the
            ' member can back out of at any time.
            FormStyles.MakeBorderless(Me, "Member Login")
        End Sub

        Private Async Sub OnSubmitClick(sender As Object, e As EventArgs)
            _lblError.Visible = False
            Dim username = _txtUsername.Text.Trim()
            Dim password = _txtPassword.Text

            If username.Length = 0 Then
                ShowError("Enter your username.")
                Return
            End If
            If password.Length = 0 Then
                ShowError("Enter your password.")
                Return
            End If

            _btnLogin.Enabled = False
            _btnLogin.Text = "Logging in…"
            Try
                Dim result = Await _memberSvc.LoginAsync(_pcNumber, username, password)
                If result.success Then
                    LoginSucceeded = True
                    LoggedInUsername = username
                    MustChangePassword = result.must_change_password
                    AbsorbedSeconds = result.absorbed_seconds
                    Me.DialogResult = DialogResult.OK
                    Me.Close()
                Else
                    ShowError(If(result.[error], "Login failed"))
                    _btnLogin.Enabled = True
                    _btnLogin.Text = "Log In"
                End If
            Catch ex As Exception
                ShowError($"Connection error: {ex.Message}")
                _btnLogin.Enabled = True
                _btnLogin.Text = "Log In"
            End Try
        End Sub

        Private Sub ShowError(msg As String)
            _lblError.Text = msg
            _lblError.Visible = True
        End Sub

    End Class

End Namespace
