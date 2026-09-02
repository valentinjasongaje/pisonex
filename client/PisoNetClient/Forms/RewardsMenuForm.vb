Imports System.Drawing
Imports System.Windows.Forms
Imports PisoNetClient.Services

Namespace Forms

    ''' <summary>
    ''' Rewards Menu — opened from the tray icon's "Redeem Points..." item
    ''' (only visible when points are enabled AND a member is logged in on
    ''' this PC — see SystemTray.UpdateMemberMenuState).
    '''
    ''' Fetches the admin-defined reward catalog on open (GET /api/member/rewards)
    ''' and renders it as a scrollable list — bonus time and food/drink items
    ''' side by side, each with its own fixed point cost. Redeeming an item is
    ''' immediate and the list stays open afterward so a member with enough
    ''' points can claim more than one thing in a single visit; the displayed
    ''' balance and item availability (grayed out once unaffordable) refresh
    ''' after each redemption. Freely cancelable — no forced mode like
    ''' ChangePasswordForm's first-login flow.
    '''
    ''' "time" items credit bonus minutes immediately (same as the old flat
    ''' points-per-minute redeem). "food" items can't be dispensed by the
    ''' client — redeeming one deducts points and queues a claim for staff to
    ''' hand over at the counter; the result message tells the member to show
    ''' this to staff instead of reporting added time.
    ''' </summary>
    Public Class RewardsMenuForm
        Inherits Form

        Private ReadOnly _memberSvc As MemberService
        Private ReadOnly _pcNumber As Integer

        Private _currentPoints As Integer

        Private _lblBalance   As Label
        Private _listPanel    As Panel
        Private _lblResult    As Label
        Private _lblEmpty     As Label

        Public Sub New(memberSvc As MemberService, pcNumber As Integer, currentPoints As Integer)
            _memberSvc = memberSvc
            _pcNumber = pcNumber
            _currentPoints = currentPoints
            InitializeComponent()
            AddHandler Me.Shown, AddressOf OnShown
        End Sub

        Private Sub InitializeComponent()
            Me.Size = New Size(460, 520)
            Me.BackColor = FormStyles.DarkBg
            Me.ForeColor = FormStyles.TextPrimary

            Dim lblTitle = New Label() With {
                .Text = "Rewards Menu",
                .Font = New Font("Segoe UI", 15, FontStyle.Bold),
                .ForeColor = FormStyles.TextPrimary,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(412, 30),
                .Location = New Point(24, 16),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            _lblBalance = New Label() With {
                .Text = $"You have {_currentPoints} points",
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = Color.FromArgb(34, 197, 94),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(412, 24),
                .Location = New Point(24, 50),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            _listPanel = New Panel() With {
                .Location = New Point(24, 84),
                .Size = New Size(412, 340),
                .BackColor = Color.Transparent,
                .AutoScroll = True
            }

            _lblEmpty = New Label() With {
                .Text = "Loading rewards…",
                .Font = New Font("Segoe UI", 9.5F),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(392, 40),
                .Location = New Point(10, 10),
                .TextAlign = ContentAlignment.MiddleCenter
            }
            _listPanel.Controls.Add(_lblEmpty)

            _lblResult = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 9),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(412, 40),
                .Location = New Point(24, 432),
                .TextAlign = ContentAlignment.MiddleCenter
            }

            Dim btnClose = FormStyles.CreateButton("Close", 412, 40)
            btnClose.Location = New Point(24, 472)
            AddHandler btnClose.Click, Sub(s, e) Me.Close()

            Me.Controls.AddRange({lblTitle, _lblBalance, _listPanel, _lblResult, btnClose})
            FormStyles.MakeBorderless(Me, "Rewards Menu", closable:=True)
        End Sub

        Private Async Sub OnShown(sender As Object, e As EventArgs)
            Await LoadRewardsAsync()
        End Sub

        Private Async Function LoadRewardsAsync() As Task
            Dim rewards = Await _memberSvc.GetRewardsAsync()
            RenderRewards(rewards)
        End Function

        Private Sub RenderRewards(rewards As List(Of RewardItem))
            _listPanel.Controls.Clear()

            If rewards Is Nothing OrElse rewards.Count = 0 Then
                _lblEmpty.Text = "No rewards available right now."
                _listPanel.Controls.Add(_lblEmpty)
                Return
            End If

            Dim y = 4
            For Each item In rewards
                Dim row = BuildRewardRow(item, y)
                _listPanel.Controls.Add(row)
                y += row.Height + 8
            Next
        End Sub

        Private Function BuildRewardRow(item As RewardItem, y As Integer) As Panel
            Dim canAfford = item.points_cost <= _currentPoints

            Dim row = New Panel() With {
                .Location = New Point(0, y),
                .Size = New Size(388, 64),
                .BackColor = FormStyles.SurfaceBg
            }

            Dim lblName = New Label() With {
                .Text = item.name,
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = If(canAfford, FormStyles.TextPrimary, FormStyles.TextDim),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(220, 22),
                .Location = New Point(12, 8)
            }

            Dim detail = $"{item.points_cost} pts"
            If item.kind = "time" AndAlso item.minutes.HasValue Then
                detail &= $"  •  +{item.minutes.Value} min"
            Else
                detail &= "  •  Food/Drink — collect at counter"
            End If

            Dim lblDetail = New Label() With {
                .Text = detail,
                .Font = New Font("Segoe UI", 8.5F),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(220, 20),
                .Location = New Point(12, 32)
            }

            Dim btnRedeem = New Button() With {
                .Text = "Redeem",
                .Size = New Size(90, 32),
                .Location = New Point(286, 16),
                .FlatStyle = FlatStyle.Flat,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .Cursor = If(canAfford, Cursors.Hand, Cursors.Default),
                .Enabled = canAfford,
                .BackColor = If(canAfford, FormStyles.AccentBlue, FormStyles.BorderClr),
                .ForeColor = If(canAfford, Color.White, FormStyles.TextDim)
            }
            btnRedeem.FlatAppearance.BorderSize = 0
            If canAfford Then btnRedeem.FlatAppearance.MouseOverBackColor = Color.FromArgb(100, 160, 255)
            AddHandler btnRedeem.Click, Sub(s, e) RedeemItem(item, btnRedeem)

            row.Controls.AddRange({lblName, lblDetail, btnRedeem})
            Return row
        End Function

        Private Async Sub RedeemItem(item As RewardItem, btn As Button)
            btn.Enabled = False
            btn.Text = "..."
            _lblResult.Text = ""

            Try
                Dim result = Await _memberSvc.RedeemRewardAsync(_pcNumber, item.id)
                If result.success Then
                    _currentPoints = result.remaining_points
                    _lblBalance.Text = $"You have {_currentPoints} points"

                    If result.status = "fulfilled" Then
                        _lblResult.Text = $"Redeemed! +{result.minutes_granted} minute(s) added."
                    Else
                        _lblResult.Text = $"Redeemed! Show this to staff to collect: {result.item_name}"
                    End If
                    _lblResult.ForeColor = Color.FromArgb(34, 197, 94)

                    ' Refresh the whole list so items that just became
                    ' unaffordable (or affordable again) update their state.
                    Await LoadRewardsAsync()
                Else
                    _lblResult.Text = If(result.[error], "Failed to redeem. Try again.")
                    _lblResult.ForeColor = Color.FromArgb(239, 68, 68)
                    btn.Enabled = True
                    btn.Text = "Redeem"
                End If
            Catch ex As Exception
                _lblResult.Text = $"Connection error: {ex.Message}"
                _lblResult.ForeColor = Color.FromArgb(239, 68, 68)
                btn.Enabled = True
                btn.Text = "Redeem"
            End Try
        End Sub

    End Class

End Namespace
