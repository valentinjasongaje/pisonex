Imports System.Diagnostics
Imports System.IO
Imports System.ServiceProcess
Imports System.Threading
Imports System.Timers
Imports Microsoft.Win32

''' <summary>
''' Core watchdog logic — shared by both Windows Service mode and console/standalone mode.
'''
''' Windows Service mode  : started by SCM, runs as SYSTEM, survives user log-off.
''' Console/standalone mode: spawned directly by PisoNetClient as a backup guardian.
'''
''' Every 5 seconds it checks whether PisoNetClient.exe is running.
''' If the process is gone AND there is no recent graceful-shutdown flag, it starts it.
''' </summary>
Public Class WatchdogService
    Inherits ServiceBase

    Private _timer   As System.Timers.Timer
    Private _stopped As New ManualResetEventSlim(False)

    Private Const CHECK_INTERVAL_MS As Integer = 5_000
    Private Const GRACE_SECONDS     As Integer = 300   ' 5-minute admin shutdown grace
    Private Const REG_KEY           As String  = "SOFTWARE\PisoNet\Client"
    Private Const CLIENT_PROC_NAME  As String  = "PisoNetClient"

    Public Sub New()
        ServiceName          = "PisoNetWatchdog"
        CanStop              = True
        CanPauseAndContinue  = False
        AutoLog              = True
    End Sub

    ' ── Windows Service entry points ─────────────────────────────────────────

    Protected Overrides Sub OnStart(args() As String)
        StartTimer()
    End Sub

    Protected Overrides Sub OnStop()
        _stopped.Set()
        StopTimer()
    End Sub

    ' ── Console / standalone mode ────────────────────────────────────────────

    ''' <summary>
    ''' Call this when running as a plain console process (spawned by PisoNetClient).
    ''' Blocks until Ctrl+C is pressed.
    ''' </summary>
    Public Sub RunConsole()
        Log("Starting in console/backup mode.")
        StartTimer()

        ' Immediate first check (don't wait 5 s)
        EnsureClientRunning()

        Console.CancelKeyPress += Sub(s, e)
            e.Cancel = True
            _stopped.Set()
        End Sub

        _stopped.Wait()
        StopTimer()
        Log("Stopped.")
    End Sub

    ' ── Timer ────────────────────────────────────────────────────────────────

    Private Sub StartTimer()
        _timer = New System.Timers.Timer(CHECK_INTERVAL_MS)
        AddHandler _timer.Elapsed, AddressOf OnTick
        _timer.AutoReset = True
        _timer.Start()
    End Sub

    Private Sub StopTimer()
        _timer?.Stop()
        _timer?.Dispose()
    End Sub

    Private Sub OnTick(sender As Object, e As ElapsedEventArgs)
        EnsureClientRunning()
    End Sub

    ' ── Core check ───────────────────────────────────────────────────────────

    Private Sub EnsureClientRunning()
        ' Skip if the client is already running
        Dim procs = Process.GetProcessesByName(CLIENT_PROC_NAME)
        If procs.Length > 0 Then Return

        ' Skip if within the admin graceful-shutdown grace period
        If InGracePeriod() Then
            Log("Client not running but grace period active — skipping restart.")
            Return
        End If

        ' Find the client exe
        Dim exePath = GetClientExePath()
        If String.IsNullOrEmpty(exePath) OrElse Not File.Exists(exePath) Then
            Log($"Client exe not found at: {exePath}")
            Return
        End If

        ' Launch it
        Try
            Log($"Client not running — starting: {exePath}")
            Dim psi = New ProcessStartInfo(exePath) With {
                .UseShellExecute   = True,
                .WorkingDirectory  = Path.GetDirectoryName(exePath)
            }
            Process.Start(psi)
        Catch ex As Exception
            Log($"Failed to start client: {ex.Message}")
        End Try
    End Sub

    ' ── Graceful-shutdown grace period ───────────────────────────────────────

    Private Function InGracePeriod() As Boolean
        Dim raw = ReadReg("ShutdownAt")
        If String.IsNullOrEmpty(raw) Then Return False

        Dim ts As Long
        If Not Long.TryParse(raw, ts) Then Return False

        Dim elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts
        If elapsed < GRACE_SECONDS Then Return True

        ' Grace period expired — clear the flag so next cycle restarts normally
        WriteReg("ShutdownAt", "")
        Return False
    End Function

    ' ── Client exe path discovery ─────────────────────────────────────────────

    Private Function GetClientExePath() As String
        ' 1. Registry (written by client on each startup)
        Dim regPath = ReadReg("ClientExePath")
        If Not String.IsNullOrEmpty(regPath) Then Return regPath

        ' 2. Same directory as this watchdog exe
        Dim thisDir = Path.GetDirectoryName(
            Process.GetCurrentProcess().MainModule?.FileName)
        Return Path.Combine(thisDir, CLIENT_PROC_NAME & ".exe")
    End Function

    ' ── Registry helpers ─────────────────────────────────────────────────────

    Private Shared Function ReadReg(key As String) As String
        Try
            Dim rk = Registry.LocalMachine.OpenSubKey(REG_KEY)
            Dim v  = rk?.GetValue(key)?.ToString()
            If v IsNot Nothing Then Return v
        Catch
        End Try
        Try
            Dim rk = Registry.CurrentUser.OpenSubKey(REG_KEY)
            Return If(rk?.GetValue(key)?.ToString(), "")
        Catch
            Return ""
        End Try
    End Function

    Private Shared Sub WriteReg(key As String, value As String)
        Try
            Dim rk = Registry.LocalMachine.CreateSubKey(REG_KEY, True)
            rk?.SetValue(key, value)
        Catch
            Try
                Dim rk = Registry.CurrentUser.CreateSubKey(REG_KEY, True)
                rk?.SetValue(key, value)
            Catch
            End Try
        End Try
    End Sub

    ' ── Logging ──────────────────────────────────────────────────────────────

    Private Shared Sub Log(msg As String)
        Console.WriteLine($"[Watchdog {DateTime.Now:HH:mm:ss}] {msg}")
        Try
            EventLog.WriteEntry("PisoNetWatchdog", msg, EventLogEntryType.Information)
        Catch
        End Try
    End Sub

End Class
