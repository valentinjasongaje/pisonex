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

        Public Sub New()
            _lockForm = New Forms.LockForm()
            AddHandler _lockForm.AdminPanelRequested, Sub() RaiseEvent LockFormAdminRequested()
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
