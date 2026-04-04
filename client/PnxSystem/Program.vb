Imports System.ServiceProcess

''' <summary>
''' Entry point for PisoNetWatchdog.
'''
''' When launched by Windows Service Control Manager (non-interactive session):
'''   → runs as a proper Windows Service via ServiceBase.Run()
'''
''' When launched directly from PisoNetClient as a backup guardian (interactive session):
'''   → runs in console mode, blocking until Ctrl+C or process termination
''' </summary>
Module Program

    Sub Main()
        If Environment.UserInteractive Then
            ' Direct launch (backup / console mode)
            Dim svc = New WatchdogService()
            svc.RunConsole()
        Else
            ' Windows Service Control Manager launch
            ServiceBase.Run(New WatchdogService())
        End If
    End Sub

End Module
