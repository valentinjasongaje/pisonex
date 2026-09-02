Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms
Imports PisoNetClient.Services

Namespace Forms

    ''' <summary>
    ''' Rewards Menu — opened from the tray icon's "Redeem Points..." item
    ''' (only visible when points are enabled AND a member is logged in on
    ''' this PC — see SystemTray.UpdateMemberMenuState).
    '''
    ''' Fetches the admin-defined reward catalog on open (GET /api/member/rewards)
    ''' and renders it as a scrollable list of cards, grouped into "Bonus Time"
    ''' and "Food &amp; Drinks" sections. Redeeming an item is immediate and the
    ''' list stays open afterward so a member with enough points can claim more
    ''' than one thing in a single visit; the balance and each card's
    ''' affordable/locked state refresh after every redemption. Freely
    ''' cancelable — no forced mode like ChangePasswordForm's first-login flow.
    '''
    ''' "time" items credit bonus minutes immediately (same as the old flat
    ''' points-per-minute redeem). "food" items can't be dispensed by the
    ''' client — redeeming one deducts points and queues a claim for staff to
    ''' hand over at the counter; the result banner tells the member to show
    ''' this to staff instead of reporting added time.
    '''
    ''' Redeeming is two-step (click Redeem → "Confirm?" → click again).
    ''' Points are non-refundable once spent, so a single stray click must
    ''' never cost the member a reward — the armed state self-cancels after
    ''' CONFIRM_MS if they do not follow through.
    '''
    ''' Styling follows the shared Pisonex language used by LockForm's member
    ''' panel and TimerOverlay: rounded cards, a blue→purple gradient header,
    ''' and per-kind accent colors — see FormStyles for the palette.
    ''' </summary>
    Public Class RewardsMenuForm
        Inherits Form

        ' ── Layout ───────────────────────────────────────────────────────
        Private Const FORM_W    As Integer = 460
        Private Const PAD       As Integer = 24
        Private Const CONTENT_W As Integer = FORM_W - PAD * 2   ' 412
        Private Const HERO_H    As Integer = 76
        Private Const LIST_H    As Integer = 322
        Private Const CARD_H    As Integer = 72
        Private Const CARD_GAP  As Integer = 10
        Private Const SECTION_H As Integer = 28
        Private Const CARD_R    As Integer = 12
        Private Const HERO_R    As Integer = 14

        ''' <summary>How long an armed "Confirm?" button waits before self-cancelling.</summary>
        Private Const CONFIRM_MS As Integer = 4000

        ' ── Palette (kind accents + status, on top of FormStyles) ────────
        Private Shared ReadOnly TimeClr    As Color = Color.FromArgb(79, 142, 247)   ' blue  — bonus time
        Private Shared ReadOnly FoodClr    As Color = Color.FromArgb(245, 158, 11)   ' amber — food/drink
        Private Shared ReadOnly GoldClr    As Color = Color.FromArgb(250, 204, 21)   ' point costs
        Private Shared ReadOnly SuccessClr As Color = Color.FromArgb(34, 197, 94)
        Private Shared ReadOnly ErrorClr   As Color = Color.FromArgb(239, 68, 68)
        Private Shared ReadOnly HeroFrom   As Color = Color.FromArgb(29, 78, 216)
        Private Shared ReadOnly HeroTo     As Color = Color.FromArgb(109, 40, 217)

        Private ReadOnly _memberSvc As MemberService
        Private ReadOnly _pcNumber As Integer

        Private _currentPoints As Integer

        Private _lblPoints    As Label
        Private _listPanel    As BufferedPanel
        Private _bannerPanel  As BufferedPanel
        Private _lblBanner    As Label
        Private _bannerClr    As Color = SuccessClr
        Private _tip          As ToolTip

        ' Two-step confirm state — only one item can be armed at a time.
        Private _armTimer     As Timer
        Private _armedId      As Integer = -1
        Private _armedBtn     As Button
        Private _armedText    As String
        Private _armedBg      As Color
        Private _armedFg      As Color

        Public Sub New(memberSvc As MemberService, pcNumber As Integer, currentPoints As Integer)
            _memberSvc = memberSvc
            _pcNumber = pcNumber
            _currentPoints = currentPoints
            InitializeComponent()
            AddHandler Me.Shown, AddressOf OnShown
            AddHandler Me.FormClosed, AddressOf OnClosedCleanup
        End Sub

        ''' <summary>
        ''' Panel that paints into an off-screen buffer. The reward cards are
        ''' custom-painted, so without this the list flickers on every scroll
        ''' and on every hover repaint.
        ''' </summary>
        Private Class BufferedPanel
            Inherits Panel
            Public Sub New()
                Me.DoubleBuffered = True
                Me.ResizeRedraw = True
            End Sub
        End Class

        ' ── Shared rounded-rect helper (same construction as TimerOverlay) ──
        Private Shared Function RoundedRect(rect As Rectangle, r As Integer) As GraphicsPath
            Dim path = New GraphicsPath()
            Dim d = r * 2
            path.AddArc(rect.X, rect.Y, d, d, 180, 90)
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
            path.CloseFigure()
            Return path
        End Function

        Private Sub InitializeComponent()
            Me.Size = New Size(FORM_W, 556)
            Me.BackColor = FormStyles.DarkBg
            Me.ForeColor = FormStyles.TextPrimary
            Me.DoubleBuffered = True

            _tip = New ToolTip() With {.InitialDelay = 350, .ReshowDelay = 120}

            _armTimer = New Timer() With {.Interval = CONFIRM_MS}
            AddHandler _armTimer.Tick, AddressOf OnArmTimeout

            ' ── Points hero card ─────────────────────────────────────────
            Dim hero = New BufferedPanel() With {
                .Location = New Point(PAD, 16),
                .Size = New Size(CONTENT_W, HERO_H),
                .BackColor = FormStyles.DarkBg
            }
            AddHandler hero.Paint, AddressOf OnHeroPaint

            Dim lblHeroCap = New Label() With {
                .Text = "YOUR POINTS",
                .Font = New Font("Segoe UI", 8, FontStyle.Bold),
                .ForeColor = Color.FromArgb(190, 255, 255, 255),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(240, 16),
                .Location = New Point(20, 14)
            }

            _lblPoints = New Label() With {
                .Text = FormatPoints(_currentPoints),
                .Font = New Font("Segoe UI", 19, FontStyle.Bold),
                .ForeColor = Color.White,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(280, 34),
                .Location = New Point(18, 31)
            }

            hero.Controls.AddRange({lblHeroCap, _lblPoints})

            ' ── Scrollable catalog ───────────────────────────────────────
            _listPanel = New BufferedPanel() With {
                .Location = New Point(PAD, 16 + HERO_H + 12),
                .Size = New Size(CONTENT_W, LIST_H),
                .BackColor = FormStyles.DarkBg,
                .AutoScroll = True
            }

            ' ── Result banner (reserved space so the list never jumps) ───
            _bannerPanel = New BufferedPanel() With {
                .Location = New Point(PAD, _listPanel.Bottom + 10),
                .Size = New Size(CONTENT_W, 44),
                .BackColor = FormStyles.DarkBg,
                .Visible = False
            }
            AddHandler _bannerPanel.Paint, AddressOf OnBannerPaint

            _lblBanner = New Label() With {
                .Text = "",
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = SuccessClr,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(CONTENT_W - 28, 44),
                .Location = New Point(14, 0),
                .TextAlign = ContentAlignment.MiddleCenter
            }
            _bannerPanel.Controls.Add(_lblBanner)

            ' Secondary styling — redeeming is the primary action on this
            ' screen, so Close must not out-shout the reward cards.
            Dim btnClose = FormStyles.CreateButton("Close", CONTENT_W, 42,
                                                   bg:=FormStyles.SurfaceBg,
                                                   fg:=FormStyles.TextDim,
                                                   hoverBg:=FormStyles.BorderClr)
            btnClose.Location = New Point(PAD, _bannerPanel.Bottom + 12)
            AddHandler btnClose.Click, Sub(s, e) Me.Close()

            Me.Controls.AddRange({hero, _listPanel, _bannerPanel, btnClose})
            ShowListMessage("Loading rewards…", FormStyles.TextDim)
            FormStyles.MakeBorderless(Me, "Rewards Menu", closable:=True)
        End Sub

        Private Shared Function FormatPoints(pts As Integer) As String
            Return $"{pts:N0} pts"
        End Function

        ' ── Hero: blue→purple gradient card with a star watermark ────────
        Private Sub OnHeroPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)

            Using path = RoundedRect(rect, HERO_R)
                Using br = New LinearGradientBrush(New Rectangle(0, 0, pnl.Width, pnl.Height),
                                                   HeroFrom, HeroTo, LinearGradientMode.Horizontal)
                    g.FillPath(br, path)
                End Using

                ' Decorative star, clipped to the card so it bleeds off the edge.
                Dim saved = g.Save()
                g.SetClip(path, CombineMode.Replace)
                Using f = New Font("Segoe UI", 44, FontStyle.Bold)
                    Using br = New SolidBrush(Color.FromArgb(38, 255, 255, 255))
                        g.DrawString(ChrW(&H2605), f, br, pnl.Width - 84, -6)
                    End Using
                End Using
                g.Restore(saved)

                Using pen = New Pen(Color.FromArgb(70, 160, 190, 255), 1)
                    g.DrawPath(pen, path)
                End Using
            End Using
        End Sub

        ' ── Result banner: tinted rounded pill, green on success / red on error ──
        Private Sub OnBannerPaint(sender As Object, e As PaintEventArgs)
            Dim pnl = CType(sender, Panel)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            Dim rect = New Rectangle(0, 0, pnl.Width - 1, pnl.Height - 1)

            Using path = RoundedRect(rect, 10)
                Using br = New SolidBrush(Color.FromArgb(28, _bannerClr))
                    g.FillPath(br, path)
                End Using
                Using pen = New Pen(Color.FromArgb(110, _bannerClr), 1)
                    g.DrawPath(pen, path)
                End Using
            End Using
        End Sub

        Private Async Sub OnShown(sender As Object, e As EventArgs)
            Await LoadRewardsAsync()
        End Sub

        Private Sub OnClosedCleanup(sender As Object, e As FormClosedEventArgs)
            _armTimer.Stop()
            _armTimer.Dispose()
            _tip.Dispose()
        End Sub

        Private Async Function LoadRewardsAsync() As Task
            Dim rewards = Await _memberSvc.GetRewardsAsync()
            RenderRewards(rewards)
        End Function

        ' ── List rendering ───────────────────────────────────────────────

        ''' <summary>
        ''' Replaces the list contents with a single centered status line
        ''' (loading / empty / offline). Built fresh each time rather than
        ''' kept as a field, so ClearList can dispose everything it removes.
        ''' </summary>
        Private Sub ShowListMessage(text As String, clr As Color)
            ClearList()
            Dim lbl = New Label() With {
                .Text = text,
                .Font = New Font("Segoe UI", 9.5F),
                .ForeColor = clr,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(_listPanel.Width - 20, 60),
                .Location = New Point(10, 40),
                .TextAlign = ContentAlignment.MiddleCenter
            }
            _listPanel.Controls.Add(lbl)
        End Sub

        ''' <summary>
        ''' Controls.Clear() alone leaks every card we throw away, and
        ''' disposing while enumerating Controls mutates the collection —
        ''' so snapshot first, detach, then dispose.
        ''' </summary>
        Private Sub ClearList()
            Dim old(_listPanel.Controls.Count - 1) As Control
            _listPanel.Controls.CopyTo(old, 0)
            _listPanel.Controls.Clear()
            For Each c In old
                c.Dispose()
            Next
        End Sub

        Private Sub RenderRewards(rewards As List(Of RewardItem))
            DisarmConfirm()

            If rewards Is Nothing OrElse rewards.Count = 0 Then
                ShowListMessage("No rewards available right now." & Environment.NewLine &
                                "Check back later — staff set these up.", FormStyles.TextDim)
                Return
            End If

            ' Bonus time first, then food; cheapest first within each group so
            ' whatever the member can actually afford floats to the top.
            Dim timeItems = New List(Of RewardItem)()
            Dim foodItems = New List(Of RewardItem)()
            For Each item In rewards
                If item.kind = "time" Then timeItems.Add(item) Else foodItems.Add(item)
            Next
            Dim byCost = New Comparison(Of RewardItem)(
                Function(a, b) a.points_cost.CompareTo(b.points_cost))
            timeItems.Sort(byCost)
            foodItems.Sort(byCost)

            ' Predict the scrollbar so cards can be sized to the final client
            ' width up front — sizing them after the fact would trip a
            ' horizontal scrollbar the moment the vertical one appears.
            Dim needed = 0
            If timeItems.Count > 0 Then needed += SECTION_H + timeItems.Count * (CARD_H + CARD_GAP)
            If foodItems.Count > 0 Then needed += SECTION_H + foodItems.Count * (CARD_H + CARD_GAP)
            Dim cardW = _listPanel.Width - 4
            If needed > _listPanel.Height Then cardW -= SystemInformation.VerticalScrollBarWidth

            ClearList()
            Dim y = 0
            y = RenderGroup(timeItems, "BONUS TIME", cardW, y)
            y = RenderGroup(foodItems, "FOOD & DRINKS", cardW, y)
        End Sub

        Private Function RenderGroup(items As List(Of RewardItem), heading As String,
                                     cardW As Integer, y As Integer) As Integer
            If items.Count = 0 Then Return y

            Dim lbl = New Label() With {
                .Text = heading,
                .Font = New Font("Segoe UI", 8, FontStyle.Bold),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(cardW, SECTION_H),
                .Location = New Point(0, y),
                .TextAlign = ContentAlignment.BottomLeft,
                .Padding = New Padding(2, 0, 0, 6)
            }
            _listPanel.Controls.Add(lbl)
            y += SECTION_H

            For Each item In items
                _listPanel.Controls.Add(BuildRewardCard(item, cardW, y))
                y += CARD_H + CARD_GAP
            Next
            Return y
        End Function

        ''' <summary>
        ''' One reward card: kind-colored left edge + badge, name, detail,
        ''' point cost, and the Redeem button. Unaffordable items stay visible
        ''' (they are the reason to keep earning) but read as locked — dimmed
        ''' throughout, with a tooltip saying how many points are missing.
        ''' </summary>
        Private Function BuildRewardCard(item As RewardItem, cardW As Integer, y As Integer) As Panel
            Dim isTime    = (item.kind = "time")
            Dim kindClr   = If(isTime, TimeClr, FoodClr)
            Dim kindText  = If(isTime, "TIME", "FOOD")
            Dim canAfford = item.points_cost <= _currentPoints
            Dim shortBy   = item.points_cost - _currentPoints

            Dim card = New BufferedPanel() With {
                .Location = New Point(0, y),
                .Size = New Size(cardW, CARD_H),
                .BackColor = FormStyles.DarkBg
            }
            Dim hovered = False

            AddHandler card.Paint, Sub(s, e)
                Dim g = e.Graphics
                g.SmoothingMode = SmoothingMode.AntiAlias
                Dim rect = New Rectangle(0, 0, card.Width - 1, card.Height - 1)

                Dim edgeClr = If(canAfford, kindClr, Color.FromArgb(90, kindClr))
                Dim bodyClr As Color
                Dim brdrClr As Color
                If Not canAfford Then
                    bodyClr = Color.FromArgb(17, 20, 33)
                    brdrClr = Color.FromArgb(28, 33, 50)
                ElseIf hovered Then
                    bodyClr = Color.FromArgb(30, 36, 56)
                    brdrClr = Color.FromArgb(120, 100, 140, 255)
                Else
                    bodyClr = FormStyles.SurfaceBg
                    brdrClr = FormStyles.BorderClr
                End If

                Using path = RoundedRect(rect, CARD_R)
                    ' Fill with the accent, then lay the body over everything
                    ' but the leftmost 4px — leaves a rounded accent edge.
                    Using br = New SolidBrush(edgeClr)
                        g.FillPath(br, path)
                    End Using
                    Dim saved = g.Save()
                    g.SetClip(path, CombineMode.Replace)
                    Using br = New SolidBrush(bodyClr)
                        g.FillRectangle(br, New Rectangle(4, 0, card.Width, card.Height))
                    End Using
                    g.Restore(saved)
                    Using pen = New Pen(brdrClr, 1)
                        g.DrawPath(pen, path)
                    End Using
                End Using

                ' Kind badge
                Dim pill = New Rectangle(18, 40, 46, 18)
                Using path = RoundedRect(pill, 9)
                    Using br = New SolidBrush(Color.FromArgb(If(canAfford, 45, 26), kindClr))
                        g.FillPath(br, path)
                    End Using
                End Using
                Using f = New Font("Segoe UI", 7.5F, FontStyle.Bold)
                    Using br = New SolidBrush(If(canAfford, kindClr, Color.FromArgb(150, kindClr)))
                        Using sf = New StringFormat() With {
                            .Alignment = StringAlignment.Center,
                            .LineAlignment = StringAlignment.Center
                        }
                            Dim pillF = New RectangleF(pill.X, pill.Y, pill.Width, pill.Height)
                            g.DrawString(kindText, f, br, pillF, sf)
                        End Using
                    End Using
                End Using
            End Sub

            Dim lblName = New Label() With {
                .Text = item.name,
                .Font = New Font("Segoe UI", 10, FontStyle.Bold),
                .ForeColor = If(canAfford, FormStyles.TextPrimary, FormStyles.TextDim),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(Math.Max(80, cardW - 190), 21),
                .Location = New Point(18, 11),
                .TextAlign = ContentAlignment.MiddleLeft
            }

            Dim detail As String
            If isTime AndAlso item.minutes.HasValue Then
                Dim minPlural = If(item.minutes.Value = 1, "minute", "minutes")
                detail = $"+{item.minutes.Value} {minPlural} of play time"
            ElseIf isTime Then
                detail = "Bonus play time"
            Else
                detail = "Collect at the counter"
            End If

            Dim lblDetail = New Label() With {
                .Text = detail,
                .Font = New Font("Segoe UI", 8.5F),
                .ForeColor = FormStyles.TextDim,
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(Math.Max(60, cardW - 244), 18),
                .Location = New Point(72, 40),
                .TextAlign = ContentAlignment.MiddleLeft
            }

            Dim lblCost = New Label() With {
                .Text = FormatPoints(item.points_cost),
                .Font = New Font("Segoe UI", 10.5F, FontStyle.Bold),
                .ForeColor = If(canAfford, GoldClr, Color.FromArgb(120, 130, 160)),
                .BackColor = Color.Transparent,
                .AutoSize = False,
                .Size = New Size(64, 20),
                .Location = New Point(cardW - 162, 26),
                .TextAlign = ContentAlignment.MiddleRight
            }

            Dim btnRedeem = New Button() With {
                .Text = "Redeem",
                .Size = New Size(78, 34),
                .Location = New Point(cardW - 90, 19),
                .FlatStyle = FlatStyle.Flat,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .Cursor = If(canAfford, Cursors.Hand, Cursors.Default),
                .Enabled = canAfford,
                .BackColor = If(canAfford, FormStyles.AccentBlue, Color.FromArgb(28, 33, 50)),
                .ForeColor = If(canAfford, Color.White, Color.FromArgb(110, 122, 150)),
                .TabStop = canAfford
            }
            btnRedeem.FlatAppearance.BorderSize = 0
            If canAfford Then
                btnRedeem.FlatAppearance.MouseOverBackColor = Color.FromArgb(100, 160, 255)
                AddHandler btnRedeem.Click, Sub(s, e) OnRedeemClick(item, btnRedeem)
            Else
                Dim plural = If(shortBy = 1, "point", "points")
                Dim hint = $"You need {shortBy:N0} more {plural} for this."
                _tip.SetToolTip(card, hint)
                _tip.SetToolTip(lblName, hint)
                _tip.SetToolTip(lblDetail, hint)
                _tip.SetToolTip(lblCost, hint)
            End If

            card.Controls.AddRange({lblName, lblDetail, lblCost, btnRedeem})

            ' Hover lift. Children swallow mouse events, so every child has to
            ' drive the same flag; on leave, re-test the cursor against the
            ' card so card→child moves don't flash the highlight off.
            If canAfford Then
                Dim onEnter As EventHandler =
                    Sub(s, e)
                        If Not hovered Then
                            hovered = True
                            card.Invalidate()
                        End If
                    End Sub
                Dim onLeave As EventHandler =
                    Sub(s, e)
                        If hovered AndAlso
                           Not card.ClientRectangle.Contains(card.PointToClient(Cursor.Position)) Then
                            hovered = False
                            card.Invalidate()
                        End If
                    End Sub
                AddHandler card.MouseEnter, onEnter
                AddHandler card.MouseLeave, onLeave
                For Each child As Control In card.Controls
                    AddHandler child.MouseEnter, onEnter
                    AddHandler child.MouseLeave, onLeave
                Next
            End If

            Return card
        End Function

        ' ── Two-step confirm ─────────────────────────────────────────────

        ''' <summary>
        ''' First click arms the button ("Confirm?"), second click within
        ''' CONFIRM_MS spends the points. Arming a different item disarms the
        ''' previous one, so only one card is ever primed.
        ''' </summary>
        Private Sub OnRedeemClick(item As RewardItem, btn As Button)
            If _armedId = item.id Then
                DisarmConfirm()
                RedeemItem(item, btn)
                Return
            End If

            DisarmConfirm()
            _armedId = item.id
            _armedBtn = btn
            _armedText = btn.Text
            _armedBg = btn.BackColor
            _armedFg = btn.ForeColor
            btn.Text = "Confirm?"
            btn.BackColor = FoodClr
            btn.ForeColor = Color.FromArgb(28, 20, 6)
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(252, 180, 50)
            ShowBanner($"Click again to spend {item.points_cost:N0} points on {item.name}.", GoldClr)
            _armTimer.Stop()
            _armTimer.Start()
        End Sub

        ''' <summary>They armed a redemption then walked away — drop the
        ''' primed state and the "click again" prompt rather than leaving a
        ''' live one-click spend sitting on screen.</summary>
        Private Sub OnArmTimeout(sender As Object, e As EventArgs)
            DisarmConfirm()
            HideBanner()
        End Sub

        Private Sub DisarmConfirm()
            _armTimer.Stop()
            If _armedBtn IsNot Nothing AndAlso Not _armedBtn.IsDisposed Then
                _armedBtn.Text = _armedText
                _armedBtn.BackColor = _armedBg
                _armedBtn.ForeColor = _armedFg
                _armedBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(100, 160, 255)
            End If
            _armedBtn = Nothing
            _armedId = -1
        End Sub

        ' ── Redemption ───────────────────────────────────────────────────

        Private Async Sub RedeemItem(item As RewardItem, btn As Button)
            btn.Enabled = False
            btn.Text = "…"
            HideBanner()

            Try
                Dim result = Await _memberSvc.RedeemRewardAsync(_pcNumber, item.id)
                If result.success Then
                    _currentPoints = result.remaining_points
                    _lblPoints.Text = FormatPoints(_currentPoints)

                    If result.status = "fulfilled" Then
                        Dim mins = If(result.minutes_granted.HasValue, result.minutes_granted.Value, 0)
                        Dim minPlural = If(mins = 1, "minute", "minutes")
                        ShowBanner($"{ChrW(&H2713)}  {mins} {minPlural} added to your session!", SuccessClr)
                    Else
                        ShowBanner($"{ChrW(&H2713)}  Claimed! Show staff at the counter: {result.item_name}",
                                   SuccessClr)
                    End If

                    ' Rebuild so cards that just became unaffordable lock, and
                    ' the fresh catalog reflects anything staff changed since.
                    Await LoadRewardsAsync()
                Else
                    ShowBanner(If(result.[error], "Could not redeem that. Please try again."), ErrorClr)
                    RestoreButton(btn)
                End If
            Catch ex As Exception
                ShowBanner($"Connection error: {ex.Message}", ErrorClr)
                RestoreButton(btn)
            End Try
        End Sub

        ''' <summary>
        ''' Puts a redeem button back to its resting state. Guarded because a
        ''' throw from the post-redemption reload lands in the same Catch, and
        ''' that reload has already disposed every card — including this button.
        ''' </summary>
        Private Shared Sub RestoreButton(btn As Button)
            If btn Is Nothing OrElse btn.IsDisposed Then Return
            btn.Enabled = True
            btn.Text = "Redeem"
        End Sub

        Private Sub ShowBanner(text As String, clr As Color)
            _bannerClr = clr
            _lblBanner.Text = text
            _lblBanner.ForeColor = clr
            _bannerPanel.Visible = True
            _bannerPanel.Invalidate()
        End Sub

        Private Sub HideBanner()
            _lblBanner.Text = ""
            _bannerPanel.Visible = False
        End Sub

    End Class

End Namespace
