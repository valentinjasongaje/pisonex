Imports System.Windows.Forms

Namespace Services

    ''' <summary>
    ''' Controls the lock screen. LockPC shows a full-screen blocking overlay.
    ''' UnlockPC closes it and restores normal desktop access.
    ''' </summary>
    Public Class LockManager
        Private _lockForm As Forms.LockForm
        Private ReadOnly _syncContext As Threading.SynchronizationContext

        Public Sub New()
            ' Capture the UI thread's sync context so we can marshal calls safely
            _syncContext = Threading.SynchronizationContext.Current
        End Sub

        Public Sub LockPC()
            _syncContext.Post(Sub(state)
                If _lockForm Is Nothing OrElse _lockForm.IsDisposed Then
                    _lockForm = New Forms.LockForm()
                End If
                If Not _lockForm.Visible Then
                    _lockForm.Show()
                End If
            End Sub, Nothing)
        End Sub

        Public Sub UnlockPC()
            _syncContext.Post(Sub(state)
                If _lockForm IsNot Nothing AndAlso Not _lockForm.IsDisposed Then
                    _lockForm.Hide()
                End If
            End Sub, Nothing)
        End Sub
    End Class

End Namespace
