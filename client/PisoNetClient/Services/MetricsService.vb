Imports System.Diagnostics
Imports System.IO
Imports System.Management
Imports System.Net.NetworkInformation
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.Json.Serialization
Imports System.Timers
Imports PisoNetClient.Config

Namespace Services

    ' ── Data models (serialised to snake_case by ApiService) ─────────────────

    Public Class ProcessInfo
        <JsonPropertyName("name")>   Public Property Name     As String
        <JsonPropertyName("pid")>    Public Property Pid      As Integer
        <JsonPropertyName("ram_mb")> Public Property RamMb    As Long
        <JsonPropertyName("cpu_pct")>Public Property CpuPct   As Double
    End Class

    Public Class DiskInfo
        <JsonPropertyName("drive")>    Public Property Drive    As String
        <JsonPropertyName("used_gb")>  Public Property UsedGb   As Double
        <JsonPropertyName("total_gb")> Public Property TotalGb  As Double
        <JsonPropertyName("percent")>  Public Property Percent  As Double
    End Class

    Public Class PcMetrics
        <JsonPropertyName("cpu_percent")>       Public Property CpuPercent      As Double
        <JsonPropertyName("cpu_cores")>         Public Property CpuCores        As Integer
        <JsonPropertyName("ram_used_mb")>       Public Property RamUsedMb       As Long
        <JsonPropertyName("ram_total_mb")>      Public Property RamTotalMb      As Long
        <JsonPropertyName("ram_percent")>       Public Property RamPercent      As Double
        <JsonPropertyName("disk_drives")>       Public Property DiskDrives      As List(Of DiskInfo)
        <JsonPropertyName("net_upload_kbps")>   Public Property NetUploadKbps   As Double
        <JsonPropertyName("net_download_kbps")> Public Property NetDownloadKbps As Double
        <JsonPropertyName("cpu_temp_c")>        Public Property CpuTempC        As Double?
        <JsonPropertyName("gpu_percent")>       Public Property GpuPercent      As Double?
        <JsonPropertyName("active_window")>     Public Property ActiveWindow    As String
        <JsonPropertyName("top_processes")>     Public Property TopProcesses    As List(Of ProcessInfo)
        <JsonPropertyName("uptime_seconds")>    Public Property UptimeSeconds   As Long
    End Class

    ' ── Service ──────────────────────────────────────────────────────────────

    ''' <summary>
    ''' Collects CPU, RAM, disk, network, temperature, and process metrics on a
    ''' background timer and sends them to the server as JSON.
    ''' Controlled by AppConfig.MetricsEnabled and AppConfig.MetricsIntervalSec.
    ''' Disable by setting MetricsEnabled = 0 in the registry to reduce overhead.
    ''' </summary>
    Public Class MetricsService
        Implements IDisposable

        Private ReadOnly _api     As ApiService
        Private _timer            As System.Timers.Timer
        Private _disposed         As Boolean = False

        ' ── PerformanceCounters ─────────────────────────────────────────
        Private _cpuCounter  As PerformanceCounter
        Private _ramAvailMb  As PerformanceCounter
        Private _ramTotalMb  As Long = 0

        ' ── Network delta tracking ──────────────────────────────────────
        Private _lastNetRx   As Long = 0
        Private _lastNetTx   As Long = 0
        Private _lastNetTick As DateTime = DateTime.UtcNow

        ' ── Process CPU delta tracking ──────────────────────────────────
        Private _prevProcTicks    As New Dictionary(Of Integer, Long)
        Private _prevProcSnapshot As DateTime = DateTime.UtcNow

        ' ── Win32 active-window API ─────────────────────────────────────
        <DllImport("user32.dll")>
        Private Shared Function GetForegroundWindow() As IntPtr
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Unicode)>
        Private Shared Function GetWindowText(hWnd As IntPtr, lpString As StringBuilder, nMaxCount As Integer) As Integer
        End Function

        Public Sub New(api As ApiService)
            _api = api

            ' CPU counter — first NextValue() always returns 0, so prime it now
            Try
                _cpuCounter = New PerformanceCounter("Processor", "% Processor Time", "_Total")
                _cpuCounter.NextValue()
            Catch
            End Try

            ' Available-memory counter
            Try
                _ramAvailMb = New PerformanceCounter("Memory", "Available MBytes")
            Catch
            End Try

            ' Total physical RAM via WMI (read once at startup)
            Try
                Using searcher = New ManagementObjectSearcher(
                        "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")
                    For Each obj In searcher.Get()
                        _ramTotalMb = CLng(obj("TotalPhysicalMemory")) \ (1024 * 1024)
                    Next
                End Using
            Catch
            End Try

            ' Prime the network baseline
            Try
                SampleNetworkBytes(_lastNetRx, _lastNetTx)
            Catch
            End Try
        End Sub

        Public Sub Start()
            If Not AppConfig.MetricsEnabled Then Return
            Dim intervalMs = AppConfig.MetricsIntervalSec * 1_000
            _timer = New System.Timers.Timer(intervalMs)
            AddHandler _timer.Elapsed, AddressOf OnTick
            _timer.AutoReset = True
            _timer.Start()
        End Sub

        Private Async Sub OnTick(sender As Object, e As ElapsedEventArgs)
            If Not AppConfig.MetricsEnabled Then Return
            Try
                Dim snapshot = CollectMetrics()
                Await _api.SendMetricsAsync(snapshot)
            Catch
                ' Silent — metrics are purely informational
            End Try
        End Sub

        ' ── Metric collection ─────────────────────────────────────────────

        Private Function CollectMetrics() As PcMetrics
            Dim m As New PcMetrics() With {
                .DiskDrives  = New List(Of DiskInfo),
                .TopProcesses = New List(Of ProcessInfo)
            }

            ' ── CPU % ──────────────────────────────────────────────────
            m.CpuCores = Environment.ProcessorCount
            Try
                m.CpuPercent = Math.Round(CDbl(_cpuCounter?.NextValue()), 1)
            Catch
                m.CpuPercent = 0
            End Try

            ' ── RAM ────────────────────────────────────────────────────
            Try
                If _ramTotalMb > 0 AndAlso _ramAvailMb IsNot Nothing Then
                    Dim availMb = CLng(_ramAvailMb.NextValue())
                    m.RamTotalMb = _ramTotalMb
                    m.RamUsedMb  = _ramTotalMb - availMb
                    m.RamPercent = Math.Round(CDbl(m.RamUsedMb) / m.RamTotalMb * 100.0, 1)
                End If
            Catch
            End Try

            ' ── Disk drives ────────────────────────────────────────────
            Try
                For Each drive In DriveInfo.GetDrives()
                    If drive.DriveType = DriveType.Fixed AndAlso drive.IsReady Then
                        Dim totalGb = Math.Round(CDbl(drive.TotalSize) / (1024.0 ^ 3), 1)
                        Dim usedGb  = Math.Round(CDbl(drive.TotalSize - drive.AvailableFreeSpace) / (1024.0 ^ 3), 1)
                        m.DiskDrives.Add(New DiskInfo() With {
                            .Drive   = drive.Name.TrimEnd("\"c),
                            .TotalGb = totalGb,
                            .UsedGb  = usedGb,
                            .Percent = If(totalGb > 0, Math.Round(usedGb / totalGb * 100.0, 1), 0)
                        })
                    End If
                Next
            Catch
            End Try

            ' ── Network (Δ bytes → KB/s) ────────────────────────────────
            Try
                Dim nowRx As Long, nowTx As Long
                SampleNetworkBytes(nowRx, nowTx)
                Dim elapsed = (DateTime.UtcNow - _lastNetTick).TotalSeconds
                If elapsed > 0 Then
                    m.NetDownloadKbps = Math.Round(Math.Max(0, CDbl(nowRx - _lastNetRx) / elapsed / 1024.0), 1)
                    m.NetUploadKbps   = Math.Round(Math.Max(0, CDbl(nowTx - _lastNetTx) / elapsed / 1024.0), 1)
                End If
                _lastNetRx   = nowRx
                _lastNetTx   = nowTx
                _lastNetTick = DateTime.UtcNow
            Catch
            End Try

            ' ── CPU temperature via ACPI WMI ────────────────────────────
            Try
                Using wmi = New ManagementObjectSearcher(
                        "root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature")
                    Dim maxC As Double = 0
                    For Each obj In wmi.Get()
                        Dim kelvinX10 = CDbl(obj("CurrentTemperature"))
                        Dim celsius   = (kelvinX10 - 2732.0) / 10.0
                        If celsius > maxC Then maxC = celsius
                    Next
                    If maxC > 0 Then m.CpuTempC = Math.Round(maxC, 1)
                End Using
            Catch
                m.CpuTempC = Nothing
            End Try

            ' ── GPU % via WDDM performance counters ─────────────────────
            ' Works on Windows 10/11 with WDDM 2.x drivers.
            ' Falls back to Nothing (hidden in dashboard) on older systems.
            Try
                Using wmi = New ManagementObjectSearcher(
                        "root\cimv2",
                        "SELECT PercentGPUTime FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine " &
                        "WHERE Name LIKE '%engtype_3D%'")
                    Dim total As Double = 0
                    Dim count As Integer = 0
                    For Each obj In wmi.Get()
                        total += CDbl(obj("PercentGPUTime"))
                        count += 1
                    Next
                    If count > 0 Then m.GpuPercent = Math.Round(total / count, 1)
                End Using
            Catch
                m.GpuPercent = Nothing
            End Try

            ' ── Active window title ─────────────────────────────────────
            Try
                Dim hwnd = GetForegroundWindow()
                Dim sb = New StringBuilder(256)
                GetWindowText(hwnd, sb, 256)
                m.ActiveWindow = sb.ToString()
            Catch
                m.ActiveWindow = ""
            End Try

            ' ── Top processes by RAM (with CPU % delta) ─────────────────
            Try
                Dim now      = DateTime.UtcNow
                Dim elapsedTicks = (now - _prevProcSnapshot).TotalMilliseconds * 10_000.0

                Dim procs = New List(Of ProcessInfo)
                For Each p In Process.GetProcesses()
                    Try
                        Dim cpuTicks As Long = p.TotalProcessorTime.Ticks
                        Dim prevTicks As Long = 0
                        _prevProcTicks.TryGetValue(p.Id, prevTicks)
                        _prevProcTicks(p.Id) = cpuTicks

                        Dim cpuPct As Double = 0
                        If elapsedTicks > 0 AndAlso prevTicks > 0 Then
                            cpuPct = Math.Max(0, Math.Round(
                                (cpuTicks - prevTicks) / elapsedTicks / Environment.ProcessorCount * 100.0, 1))
                        End If

                        Dim ramMb = p.WorkingSet64 \ (1024 * 1024)
                        If ramMb >= 5 Then
                            procs.Add(New ProcessInfo() With {
                                .Name   = p.ProcessName,
                                .Pid    = p.Id,
                                .RamMb  = ramMb,
                                .CpuPct = cpuPct
                            })
                        End If
                    Catch
                        ' Process may have exited between Get() and property access
                    End Try
                Next

                _prevProcSnapshot = now
                m.TopProcesses = procs.OrderByDescending(Function(x) x.RamMb).Take(8).ToList()
            Catch
            End Try

            ' ── System uptime ───────────────────────────────────────────
            m.UptimeSeconds = Environment.TickCount64 \ 1000

            Return m
        End Function

        ' ── Helpers ──────────────────────────────────────────────────────

        Private Shared Sub SampleNetworkBytes(ByRef rx As Long, ByRef tx As Long)
            rx = 0 : tx = 0
            For Each nic In NetworkInterface.GetAllNetworkInterfaces()
                If nic.OperationalStatus = OperationalStatus.Up AndAlso
                   nic.NetworkInterfaceType <> NetworkInterfaceType.Loopback Then
                    Dim stats = nic.GetIPStatistics()
                    rx += stats.BytesReceived
                    tx += stats.BytesSent
                End If
            Next
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Not _disposed Then
                _disposed = True
                _timer?.Stop()
                _timer?.Dispose()
                _cpuCounter?.Dispose()
                _ramAvailMb?.Dispose()
            End If
        End Sub

    End Class

End Namespace
