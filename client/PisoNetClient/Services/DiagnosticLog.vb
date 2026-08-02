Imports System.IO

Namespace Services

    ''' <summary>
    ''' Always-on diagnostic logger for the lock/unlock loop investigation.
    ''' Unlike the crash logger in Program.vb (which only fires on an
    ''' unhandled exception), a UI-thread HANG throws nothing — the process
    ''' just stops responding. This logs heartbeat send/receive timing and
    ''' every lock-state transition continuously, so if it happens again the
    ''' log simply stops, and the last lines show exactly what was
    ''' happening (including whether a heartbeat was in flight when it
    ''' froze — a heartbeat "send" with no matching "recv" pinpoints the
    ''' hang inside the HTTP call itself).
    ''' Writes to %ProgramData%\PisoNet\client.log, same folder as
    ''' crash.log and license.dat. Rotates to client.log.old past 5 MB so a
    ''' 24/7 shop PC doesn't grow this file forever.
    ''' </summary>
    Public Module DiagnosticLog

        Private ReadOnly _sync As New Object()
        Private Const MaxSizeBytes As Long = 5 * 1024 * 1024

        Private ReadOnly _dir As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PisoNet")
        Private ReadOnly _logPath As String = Path.Combine(_dir, "client.log")
        Private ReadOnly _oldLogPath As String = Path.Combine(_dir, "client.log.old")

        Public Sub Write(message As String)
            Try
                SyncLock _sync
                    Directory.CreateDirectory(_dir)
                    Dim info = New FileInfo(_logPath)
                    If info.Exists AndAlso info.Length > MaxSizeBytes Then
                        File.Copy(_logPath, _oldLogPath, overwrite:=True)
                        File.Delete(_logPath)
                    End If
                    Dim line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}"
                    File.AppendAllText(_logPath, line)
                End SyncLock
            Catch
                ' Logging must never throw out of a caller's code path.
            End Try
        End Sub

    End Module

End Namespace
