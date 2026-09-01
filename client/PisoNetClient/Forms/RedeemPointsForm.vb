Imports System.Drawing
Imports System.Windows.Forms
Imports PisoNetClient.Services

Namespace Forms

    ''' <summary>
    ''' Self-service points redemption, opened from the tray icon's "Redeem
    ''' Points..." item (only visible when points are enabled AND a member is
    ''' logged in on this PC — see SystemTray.UpdateMemberMenuState).
    '''
    ''' Freely cancelable, unlike the forced ChangePasswordForm — there's no
    ''' account-safety reason to block dismissal here. currentPoints and
    ''' pointsPerMinute come from the last heartbeat (SessionManager.MembershipUpdated)
    ''' so opening this form needs no extra round trip; the actual redeem call
    ''' still re-validates server-side (rate/balance may have changed).
    ''' </summary>
    Public Class RedeemPointsForm
        Inherits Form

        Private ReadOnly _memberSvc As MemberService
        Private ReadOnly _pcNumber As Integer
        Private ReadOnly _pointsPerMinute As Integer

        Public Property PointsRedeemed As Integer = 0
        Public Property MinutesAdded As Integer = 0

        Private _lblBalance  As Label
        Private _txtPoints   As TextBox
        Private _lblPreview  As Label
        Private _lblError    As Label
        Private _btnRedeem   As Button

        Public Sub New(memberSvc As MemberService, pcNumber As Integer,
                       currentPoints As Integer, pointsPerMinute As Integer)
            _memberSvc = memberSvc
            _pcNumber = pcNumber
            _pointsPerMinute = pointsPerMinute
            InitializeComponent(currentPoints)
        End Sub

        Private Sub InitializeComponent(currentPoints As Integer)
            Me.Size = New Size(440, 316)
            Me.BackColor = FormStyles.DarkBg
            Me.ForeColor = FormStyles.TextPrimary

            Dim lblTitle = New Label() With {
                .Text = "Redeem Points",
                .Font = New Font("Segoe UI", 15, FontStyle.Bold),
                .ForeColor = FormStyles.TextPrimary,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 30),
                .Location = New Point(24, 16),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            _lblBalance = New Label() With {
                .Text = $"You have {currentPoints} points ({_pointsPerMinute} pts = 1 minute).",
                .Font = New Font("Segoe UI", 9.5F),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 40),
                .Location = New Point(24, 50),
                .TextAlign = ContentAlignment.TopCenter
            }

            Dim y = 100
            Dim lblPoints = FormStyles.CreateLabel("Points to Redeem")
            lblPoints.Location = New Point(24, y)
            _txtPoints = FormStyles.CreateInput(New Point(24, y + 20), 392, text:=currentPoints.ToString())
            AddHandler _txtPoints.TextChanged, AddressOf OnPointsChanged
            AddHandler _txtPoints.KeyDown, Sub(s, e) If e.KeyCode = Keys.Enter Then OnRedeemClick(s, e)
            y += 56

            _lblPreview = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 20),
                .Location = New Point(24, y),
                .TextAlign = ContentAlignment.MiddleCenter
            }
            y += 30

            _lblError = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = Color.FromArgb(239, 68, 68),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 24),
                .Location = New Point(24, y),
                .TextAlign = ContentAlignment.MiddleCenter,
                .Visible = False
            }
            y += 34

            _btnRedeem = FormStyles.CreateButton("Redeem", 392, 42)
            _btnRedeem.Location = New Point(24, y)
            AddHandler _btnRedeem.Click, AddressOf OnRedeemClick

            Me.Controls.AddRange({lblTitle, _lblBalance, lblPoints, _txtPoints, _lblPreview, _lblError, _btnRedeem})
            Me.AcceptButton = _btnRedeem

            FormStyles.MakeBorderless(Me, "Redeem Points", closable:=True)
            UpdatePreview()
        End Sub

        Private Sub OnPointsChanged(sender As Object, e As EventArgs)
            _lblError.Visible = False
            UpdatePreview()
        End Sub

        Private Sub UpdatePreview()
            Dim points As Integer
            If Not Integer.TryParse(_txtPoints.Text, points) OrElse points <= 0 OrElse _pointsPerMinute <= 0 Then
                _lblPreview.Text = ""
                Return
            End If
            Dim minutes = points \ _pointsPerMinute
            _lblPreview.Text = If(minutes > 0,
                $"= {minutes} minute(s) of bonus time",
                $"Redeem at least {_pointsPerMinute} points (1 minute)")
        End Sub

        Private Async Sub OnRedeemClick(sender As Object, e As EventArgs)
            _lblError.Visible = False
            Dim points As Integer
            If Not Integer.TryParse(_txtPoints.Text, points) OrElse points <= 0 Then
                ShowError("Enter a valid number of points.")
                Return
            End If

            _btnRedeem.Enabled = False
            _btnRedeem.Text = "Redeeming..."
            Try
                Dim result = Await _memberSvc.RedeemPointsAsync(_pcNumber, points)
                If result.success Then
                    PointsRedeemed = result.points_redeemed
                    MinutesAdded = result.seconds_added \ 60
                    Me.DialogResult = DialogResult.OK
                    Me.Close()
                Else
                    ShowError(If(result.[error], "Failed to redeem points. Try again."))
                    _btnRedeem.Enabled = True
                    _btnRedeem.Text = "Redeem"
                End If
            Catch ex As Exception
                ShowError($"Connection error: {ex.Message}")
                _btnRedeem.Enabled = True
                _btnRedeem.Text = "Redeem"
            End Try
        End Sub

        Private Sub ShowError(msg As String)
            _lblError.Text = msg
            _lblError.Visible = True
        End Sub

    End Class

End Namespace
