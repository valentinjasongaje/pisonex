Imports System.Windows.Forms

Namespace Services

    ''' <summary>
    ''' Controls the lock screen form.
    ''' LockForm is created once on the UI thread and reused — no SynchronizationContext needed.
    ''' </summary>
    Public Class LockManager

        Private ReadOnly _lockForm As Forms.LockForm

        ' When the coin slot is open we hold off hiding the lock form even if the
        ' server says is_locked=False (the first coin creates a session before the
        ' user is done inserting coins).  The form is hidden only once the slot
        ' actually closes (ShowReceivingCoins(False)).
        Private _receivingCoins   As Boolean = False
        Private _isUnlockPending  As Boolean = False

        ''' <summary>Forwarded from LockForm.AdminPanelRequested — wired in Program.vb.</summary>
        Public Event LockFormAdminRequested()
        Public Event LockFormLoginRequested(username As String, password As String)
        Public Event LockFormRegisterRequested(username As String, password As String)
        Public Event LockFormLogoutRequested()
        Public Event LockFormInsertCoinRequested()
        Public Event LockFormDoneInsertingCoinsRequested()

        Public Sub New()
            _lockForm = New Forms.LockForm()
            AddHandler _lockForm.AdminPanelRequested, Sub() RaiseEvent LockFormAdminRequested()
            AddHandler _lockForm.MemberLoginRequested, Sub(u, p) RaiseEvent LockFormLoginRequested(u, p)
            AddHandler _lockForm.MemberRegisterRequested, Sub(u, p) RaiseEvent LockFormRegisterRequested(u, p)
            AddHandler _lockForm.MemberLogoutRequested, Sub() RaiseEvent LockFormLogoutRequested()
            AddHandler _lockForm.InsertCoinRequested, Sub() RaiseEvent LockFormInsertCoinRequested()
            AddHandler _lockForm.DoneInsertingCoinsRequested, Sub() RaiseEvent LockFormDoneInsertingCoinsRequested()
        End Sub

        Public Sub LockPC()
            If _lockForm.InvokeRequired Then
                _lockForm.Invoke(Sub() LockPC())
                Return
            End If
            ' If we're re-locking while an unlock was deferred (e.g. admin reset the
            ' session mid-insertion), discard the pending unlock — the form is already shown.
            _isUnlockPending = False
            If Not _lockForm.Visible Then _lockForm.Show()
            _lockForm.BringToFront()
        End Sub

        Public Sub UnlockPC()
            If _lockForm.InvokeRequired Then
                _lockForm.Invoke(Sub() UnlockPC())
                Return
            End If
            ' While the coin slot is open keep the lock form visible so the user
            ' can insert additional coins.  Record that an unlock is waiting and
            ' hide the form once the slot actually closes (ShowReceivingCoins(False)).
            If _receivingCoins Then
                _isUnlockPending = True
                Return
            End If
            If _lockForm.Visible Then _lockForm.Hide()
        End Sub

        Public Sub ShowOfflineStatus()
            _lockForm.ShowOfflineStatus()
        End Sub

        Public Sub HideOfflineStatus()
            _lockForm.HideOfflineStatus()
        End Sub

        Public Sub RefreshLockAppearance()
            _lockForm.RefreshAppearance()
        End Sub

        Public Sub UpdateMembershipUI(enabled As Boolean, absorption As Boolean, username As String,
                                       balanceSeconds As Integer, canLogout As Boolean,
                                       zeroTimeLogoutSeconds As Integer, idleShutdownSeconds As Integer,
                                       minimumLogoutMinutes As Integer)
            _lockForm.UpdateMembershipUI(enabled, absorption, username, balanceSeconds,
                                          canLogout, zeroTimeLogoutSeconds, idleShutdownSeconds,
                                          minimumLogoutMinutes)
        End Sub

        Public Sub UpdateCoinProgress(pesos As Integer, seconds As Integer)
            _lockForm.UpdateCoinProgress(pesos, seconds)
        End Sub

        Public Sub ShowReceivingCoins(isReceiving As Boolean)
            _receivingCoins = isReceiving
            _lockForm.ShowReceivingCoins(isReceiving)
            ' If the PC was unlocked while the slot was open (first coin created a
            ' session before the user closed the slot), complete the deferred unlock now.
            ' IMPORTANT: we re-use UnlockPC() here rather than calling _lockForm.Hide()
            ' directly. ShowReceivingCoins is invoked from the heartbeat's background
            ' thread; _lockForm.ShowReceivingCoins internally Invokes to the UI thread
            ' and then RETURNS back to the background thread, so any _lockForm.Hide()
            ' call after it would also be on the background thread (cross-thread → silent
            ' no-op / exception). UnlockPC() already has its own InvokeRequired marshal
            ' so it is safe to call from any thread. Since _receivingCoins is now False,
            ' UnlockPC() will not defer again.
            If Not isReceiving AndAlso _isUnlockPending Then
                _isUnlockPending = False
                UnlockPC()
            End If
        End Sub

        Public Sub UpdateCoinSlot(enabled As Boolean)
            _lockForm.UpdateCoinSlot(enabled)
        End Sub

        Public Sub SetInsertCoinResult(success As Boolean)
            _lockForm.SetInsertCoinResult(success)
        End Sub

        Public Sub ShowMemberError(message As String)
            _lockForm.ShowMemberError(message)
        End Sub

        ''' <summary>
        ''' Clears login/register form fields after a successful authentication
        ''' so the previous user's credentials are not left visible on the form.
        ''' </summary>
        Public Sub ClearMemberForm()
            _lockForm.ClearMemberForm()
        End Sub

        Public Sub ShowMemberSuccess(message As String)
            _lockForm.ShowMemberSuccess(message)
        End Sub

        ''' <summary>True while the hardware coin slot is open for this PC.</summary>
        Public ReadOnly Property IsReceivingCoins As Boolean
            Get
                Return _receivingCoins
            End Get
        End Property

        ''' <summary>Call before Application.Exit() so WM_CLOSE is honoured.</summary>
        Public Sub AllowExit()
            If _lockForm.InvokeRequired Then
                _lockForm.Invoke(Sub() AllowExit())
                Return
            End If
            _lockForm.AllowExit()
            If _lockForm.Visible Then _lockForm.Close()
        End Sub

    End Class

End Namespace
