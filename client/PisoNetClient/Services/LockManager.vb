Imports System.Windows.Forms

Namespace Services

    ''' <summary>
    ''' Controls the lock screen form.
    ''' LockForm is created once on the UI thread and reused — no SynchronizationContext needed.
    ''' </summary>
    Public Class LockManager

        Private ReadOnly _lockForm As Forms.LockForm

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
            If Not _lockForm.Visible Then _lockForm.Show()
            _lockForm.BringToFront()
        End Sub

        Public Sub UnlockPC()
            If _lockForm.InvokeRequired Then
                _lockForm.Invoke(Sub() UnlockPC())
                Return
            End If
            ' Defense-in-depth: never unlock if license is not active
            If Not LicenseService.IsActive() Then Return
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

        Public Sub ShowLicenseWarning(message As String)
            _lockForm.ShowLicenseWarning(message)
        End Sub

        Public Sub HideLicenseWarning()
            _lockForm.HideLicenseWarning()
        End Sub

        Public Sub ShowServerLicenseWarning(dashboardUrl As String)
            _lockForm.ShowServerLicenseWarning(dashboardUrl)
        End Sub

        Public Sub HideServerLicenseWarning()
            _lockForm.HideServerLicenseWarning()
        End Sub

        Public Sub UpdateMembershipUI(enabled As Boolean, absorption As Boolean, username As String,
                                       balanceSeconds As Integer, canLogout As Boolean,
                                       zeroTimeLogoutSeconds As Integer, idleShutdownSeconds As Integer,
                                       minimumLogoutMinutes As Integer, serverLicensed As Boolean)
            _lockForm.UpdateMembershipUI(enabled, absorption, username, balanceSeconds,
                                          canLogout, zeroTimeLogoutSeconds, idleShutdownSeconds,
                                          minimumLogoutMinutes, serverLicensed)
        End Sub

        Public Sub UpdateCoinProgress(pesos As Integer, seconds As Integer)
            _lockForm.UpdateCoinProgress(pesos, seconds)
        End Sub

        Public Sub ShowReceivingCoins(isReceiving As Boolean)
            _lockForm.ShowReceivingCoins(isReceiving)
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

        Public ReadOnly Property IsLicenseActive As Boolean
            Get
                Return _lockForm.IsLicenseActive
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
