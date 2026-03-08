Imports System.Windows.Forms

Namespace Services

    ''' <summary>
    ''' Controls the lock screen.
    ''' The LockForm is created once on the UI thread and reused —
    ''' no SynchronizationContext needed.
    ''' </summary>
    Public Class LockManager

        Private ReadOnly _lockForm As Forms.LockForm

        Public Sub New()
            ' Must be called from the UI thread (STAThread in Program.vb)
            _lockForm = New Forms.LockForm()
        End Sub

        ''' <summary>Returns the underlying form so Program.vb can pass it to Application.Run.</summary>
        Public ReadOnly Property LockForm As Forms.LockForm
            Get
                Return _lockForm
            End Get
        End Property

        Public Sub LockPC()
            If _lockForm.InvokeRequired Then
                _lockForm.Invoke(Sub() LockPC())
                Return
            End If
            If Not _lockForm.Visible Then
                _lockForm.Show()
            End If
            _lockForm.BringToFront()
        End Sub

        Public Sub UnlockPC()
            If _lockForm.InvokeRequired Then
                _lockForm.Invoke(Sub() UnlockPC())
                Return
            End If
            If _lockForm.Visible Then
                _lockForm.Hide()
            End If
        End Sub

    End Class

End Namespace
