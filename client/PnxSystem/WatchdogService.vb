Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
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
'''
''' When running as a Windows Service (Session 0), the client is launched into the
''' active user's interactive session via WTSQueryUserToken + CreateProcessAsUser,
''' bypassing Session 0 isolation so the window actually appears on the user's desktop.
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
        ServiceName         = "pnxsystem"
        CanStop             = True
        CanPauseAndContinue = False
        AutoLog             = True
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
        EnsureClientRunning()

        AddHandler Console.CancelKeyPress, Sub(s, e)
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
        If Process.GetProcessesByName(CLIENT_PROC_NAME).Length > 0 Then Return

        If InGracePeriod() Then
            Log("Client not running but grace period active — skipping restart.")
            Return
        End If

        Dim exePath = GetClientExePath()
        If String.IsNullOrEmpty(exePath) OrElse Not File.Exists(exePath) Then
            Log($"Client exe not found at: {exePath}")
            Return
        End If

        Log($"Client not running — restarting: {exePath}")

        ' Services run in Session 0 (isolated). Use CreateProcessAsUser to launch
        ' the client into the active interactive user session so it appears on screen.
        If Not Environment.UserInteractive Then
            LaunchInUserSession(exePath)
        Else
            ' Console/backup mode — already in the user session, use normal launch
            Try
                Process.Start(New ProcessStartInfo(exePath) With {
                    .UseShellExecute  = True,
                    .WorkingDirectory = Path.GetDirectoryName(exePath)
                })
            Catch ex As Exception
                Log($"Failed to start client: {ex.Message}")
            End Try
        End If
    End Sub

    ' ── Session-aware launch (fixes Session 0 isolation) ─────────────────────

    ''' <summary>
    ''' Launches the client exe inside the active console user's session so the
    ''' window appears on the interactive desktop — not hidden in Session 0.
    ''' </summary>
    Private Sub LaunchInUserSession(exePath As String)
        Dim sessionId As UInteger = WTSGetActiveConsoleSessionId()
        Dim userToken As IntPtr = IntPtr.Zero

        If Not WTSQueryUserToken(sessionId, userToken) Then
            Dim err = Marshal.GetLastWin32Error()
            ' Deliberately NOT falling back to a plain Process.Start here. This
            ' service runs as LocalSystem, so an unqualified Process.Start would
            ' launch the client under the SYSTEM identity instead of the
            ' interactive user's. That client instance's registry reads/writes
            ' (ServerUrl, PCNumber, IsConfigured, ...) then go to SYSTEM's own
            ' hidden profile hive, not the real user's -- so an Admin Panel save
            ' during one of these launches "succeeds" but is invisible to every
            ' normal launch afterward, which looks like the settings randomly
            ' reverting. Skipping this cycle and retrying on the next 5 s tick
            ' is safe: WTSQueryUserToken failures are typically transient (e.g.
            ' the session isn't fully ready yet right after boot).
            Log($"WTSQueryUserToken failed (session={sessionId}, error={err}). " &
                "Skipping this cycle rather than launching under the wrong identity -- will retry.")
            Return
        End If

        Dim envBlock As IntPtr = IntPtr.Zero
        CreateEnvironmentBlock(envBlock, userToken, False)

        Dim si As New STARTUPINFO()
        si.cb        = CUInt(Marshal.SizeOf(si))
        si.lpDesktop = "winsta0\default"   ' interactive desktop

        Dim pi      As New PROCESS_INFORMATION()
        Dim cmdLine As New System.Text.StringBuilder($"""{exePath}""")
        Dim workDir As String = Path.GetDirectoryName(exePath)

        Const CREATE_UNICODE_ENVIRONMENT As UInteger = &H400UI
        Const NORMAL_PRIORITY_CLASS      As UInteger = &H20UI

        Dim ok = CreateProcessAsUser(
            userToken,
            Nothing,
            cmdLine,
            IntPtr.Zero, IntPtr.Zero,
            False,
            CREATE_UNICODE_ENVIRONMENT Or NORMAL_PRIORITY_CLASS,
            envBlock,
            workDir,
            si, pi
        )

        If ok Then
            Log($"Client relaunched in user session {sessionId}.")
            CloseHandle(pi.hProcess)
            CloseHandle(pi.hThread)
        Else
            Log($"CreateProcessAsUser failed (error={Marshal.GetLastWin32Error()}).")
        End If

        If envBlock <> IntPtr.Zero Then DestroyEnvironmentBlock(envBlock)
        CloseHandle(userToken)
    End Sub

    ' ── Graceful-shutdown grace period ───────────────────────────────────────
    ' Read from the same %ProgramData% file PisoNetClient.AppConfig.SaveGracefulShutdown
    ' writes to — NOT the registry. This service normally runs as SYSTEM (see
    ' install-watchdog.bat), whose HKEY_CURRENT_USER is a different hive from
    ' the logged-in café user's, so a registry flag written by the client was
    ' never actually visible here. %ProgramData% is machine-wide and readable
    ' by both regardless of which account each process runs as.

    Private ReadOnly _shutdownFlagPath As String = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PisoNet", "shutdown.flag")

    Private Function InGracePeriod() As Boolean
        Dim raw As String
        Try
            If Not File.Exists(_shutdownFlagPath) Then Return False
            raw = File.ReadAllText(_shutdownFlagPath).Trim()
        Catch
            Return False
        End Try
        If String.IsNullOrEmpty(raw) Then Return False

        Dim ts As Long
        If Not Long.TryParse(raw, ts) Then Return False

        Dim elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts
        If elapsed < GRACE_SECONDS Then Return True

        Try
            File.Delete(_shutdownFlagPath)
        Catch
        End Try
        Return False
    End Function

    ' ── Client exe path discovery ─────────────────────────────────────────────

    Private Function GetClientExePath() As String
        Dim regPath = ReadReg("ClientExePath")
        If Not String.IsNullOrEmpty(regPath) Then Return regPath

        Dim thisDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
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
            EventLog.WriteEntry("pnxsystem", msg, EventLogEntryType.Information)
        Catch
        End Try
    End Sub

    ' ── P/Invoke declarations ─────────────────────────────────────────────────

    <DllImport("kernel32.dll")>
    Private Shared Function WTSGetActiveConsoleSessionId() As UInteger
    End Function

    <DllImport("wtsapi32.dll", SetLastError:=True)>
    Private Shared Function WTSQueryUserToken(sessionId As UInteger, ByRef phToken As IntPtr) As Boolean
    End Function

    <DllImport("userenv.dll", SetLastError:=True)>
    Private Shared Function CreateEnvironmentBlock(ByRef lpEnvironment As IntPtr, hToken As IntPtr, bInherit As Boolean) As Boolean
    End Function

    <DllImport("userenv.dll", SetLastError:=True)>
    Private Shared Function DestroyEnvironmentBlock(lpEnvironment As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Private Shared Function CreateProcessAsUser(
        hToken As IntPtr,
        lpApplicationName As String,
        lpCommandLine As System.Text.StringBuilder,
        lpProcessAttributes As IntPtr,
        lpThreadAttributes As IntPtr,
        bInheritHandles As Boolean,
        dwCreationFlags As UInteger,
        lpEnvironment As IntPtr,
        lpCurrentDirectory As String,
        ByRef lpStartupInfo As STARTUPINFO,
        ByRef lpProcessInformation As PROCESS_INFORMATION
    ) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure STARTUPINFO
        Public cb             As UInteger
        Public lpReserved     As String
        Public lpDesktop      As String
        Public lpTitle        As String
        Public dwX            As UInteger
        Public dwY            As UInteger
        Public dwXSize        As UInteger
        Public dwYSize        As UInteger
        Public dwXCountChars  As UInteger
        Public dwYCountChars  As UInteger
        Public dwFillAttribute As UInteger
        Public dwFlags        As UInteger
        Public wShowWindow    As UShort
        Public cbReserved2    As UShort
        Public lpReserved2    As IntPtr
        Public hStdInput      As IntPtr
        Public hStdOutput     As IntPtr
        Public hStdError      As IntPtr
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure PROCESS_INFORMATION
        Public hProcess   As IntPtr
        Public hThread    As IntPtr
        Public dwProcessId As UInteger
        Public dwThreadId  As UInteger
    End Structure

End Class
