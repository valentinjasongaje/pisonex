Imports System.Runtime.InteropServices
Imports PisoNetClient.Config

Namespace Services

    ''' <summary>
    ''' Mutes the PC while it is locked (no time) and restores sound when a
    ''' session starts, so the "time added" voice announcement can be heard.
    '''
    ''' Talks to the default playback device through the Windows Core Audio API
    ''' (IAudioEndpointVolume) — no NuGet package needed. Every call is
    ''' best-effort: a PC with no sound device, or a driver that refuses, must
    ''' never break locking or unlocking.
    ''' </summary>
    Public Module AudioControl

        ' Remembers that WE muted the PC, so unlocking only unmutes (and resets the
        ' volume) when the mute was ours — and the app restores sound on exit.
        Private _mutedByUs As Boolean = False

        ''' <summary>Called when the lock screen appears.</summary>
        Public Sub OnLocked()
            If Not AppConfig.MuteWhenLocked Then Return
            If SetMute(True) Then
                _mutedByUs = True
                DiagnosticLog.Write("Audio: muted (PC locked)")
            End If
        End Sub

        ''' <summary>
        ''' Called when the lock screen is dismissed because time was added / a
        ''' session started. Unmutes and sets the volume to the configured level
        ''' (default 50 %) — before the time-added voice plays.
        ''' </summary>
        Public Sub OnUnlocked()
            If Not _mutedByUs Then Return
            _mutedByUs = False
            Dim pct = AppConfig.UnlockVolumePercent
            SetVolume(pct / 100.0F)
            SetMute(False)
            DiagnosticLog.Write($"Audio: unmuted at {pct}% (session started)")
        End Sub

        ''' <summary>Called on app exit — never leave the PC muted behind us.</summary>
        Public Sub RestoreOnExit()
            If _mutedByUs Then
                SetMute(False)
                _mutedByUs = False
            End If
        End Sub

        ' ── Core Audio plumbing ──────────────────────────────────────────────

        Private Function SetMute(mute As Boolean) As Boolean
            Return WithEndpoint(Function(ep)
                                    Dim ctx = Guid.Empty
                                    Return ep.SetMute(mute, ctx) = 0
                                End Function)
        End Function

        Private Function SetVolume(scalar As Single) As Boolean
            Dim level = Math.Max(0.0F, Math.Min(1.0F, scalar))
            Return WithEndpoint(Function(ep)
                                    Dim ctx = Guid.Empty
                                    Return ep.SetMasterVolumeLevelScalar(level, ctx) = 0
                                End Function)
        End Function

        Private Function WithEndpoint(action As Func(Of IAudioEndpointVolume, Boolean)) As Boolean
            Dim enumerator As IMMDeviceEnumerator = Nothing
            Dim device As IMMDevice = Nothing
            Dim endpoint As IAudioEndpointVolume = Nothing
            Try
                ' Created generically from its CLSID rather than through a ComImport
                ' class: Windows hands out ONE shared enumerator per process, and a
                ' typed wrapper breaks ("Unable to cast") if anything else in the
                ' process wrapped it first.
                enumerator = CType(Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)),
                                   IMMDeviceEnumerator)
                If enumerator.GetDefaultAudioEndpoint(E_RENDER, E_MULTIMEDIA, device) <> 0 OrElse device Is Nothing Then
                    Return False
                End If
                Dim iid = IID_IAudioEndpointVolume
                Dim obj As Object = Nothing
                If device.Activate(iid, CLSCTX_ALL, IntPtr.Zero, obj) <> 0 OrElse obj Is Nothing Then
                    Return False
                End If
                endpoint = CType(obj, IAudioEndpointVolume)
                Return action(endpoint)
            Catch ex As Exception
                DiagnosticLog.Write($"Audio: control failed — {ex.Message}")
                Return False
            Finally
                If endpoint IsNot Nothing Then Marshal.ReleaseComObject(endpoint)
                If device IsNot Nothing Then Marshal.ReleaseComObject(device)
                If enumerator IsNot Nothing Then Marshal.ReleaseComObject(enumerator)
            End Try
        End Function

        Private Const E_RENDER As Integer = 0        ' EDataFlow.eRender (playback)
        Private Const E_MULTIMEDIA As Integer = 1    ' ERole.eMultimedia
        Private Const CLSCTX_ALL As Integer = 23
        Private ReadOnly IID_IAudioEndpointVolume As New Guid("5CDF2C82-841E-4546-9722-0CF74078229A")
        Private ReadOnly CLSID_MMDeviceEnumerator As New Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")

        <ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
        Private Interface IMMDeviceEnumerator
            <PreserveSig> Function EnumAudioEndpoints(dataFlow As Integer, stateMask As Integer,
                                                      ByRef devices As IntPtr) As Integer
            <PreserveSig> Function GetDefaultAudioEndpoint(dataFlow As Integer, role As Integer,
                                                           ByRef endpoint As IMMDevice) As Integer
        End Interface

        <ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
        Private Interface IMMDevice
            <PreserveSig> Function Activate(ByRef iid As Guid, clsCtx As Integer, activationParams As IntPtr,
                                            <MarshalAs(UnmanagedType.IUnknown)> ByRef iface As Object) As Integer
        End Interface

        ' Methods MUST stay in vtable order, up to the last one we call (GetMute).
        <ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>
        Private Interface IAudioEndpointVolume
            <PreserveSig> Function RegisterControlChangeNotify(notify As IntPtr) As Integer
            <PreserveSig> Function UnregisterControlChangeNotify(notify As IntPtr) As Integer
            <PreserveSig> Function GetChannelCount(ByRef count As UInteger) As Integer
            <PreserveSig> Function SetMasterVolumeLevel(levelDb As Single, ByRef eventContext As Guid) As Integer
            <PreserveSig> Function SetMasterVolumeLevelScalar(level As Single, ByRef eventContext As Guid) As Integer
            <PreserveSig> Function GetMasterVolumeLevel(ByRef levelDb As Single) As Integer
            <PreserveSig> Function GetMasterVolumeLevelScalar(ByRef level As Single) As Integer
            <PreserveSig> Function SetChannelVolumeLevel(channel As UInteger, levelDb As Single, ByRef eventContext As Guid) As Integer
            <PreserveSig> Function SetChannelVolumeLevelScalar(channel As UInteger, level As Single, ByRef eventContext As Guid) As Integer
            <PreserveSig> Function GetChannelVolumeLevel(channel As UInteger, ByRef levelDb As Single) As Integer
            <PreserveSig> Function GetChannelVolumeLevelScalar(channel As UInteger, ByRef level As Single) As Integer
            <PreserveSig> Function SetMute(<MarshalAs(UnmanagedType.Bool)> mute As Boolean, ByRef eventContext As Guid) As Integer
            <PreserveSig> Function GetMute(<MarshalAs(UnmanagedType.Bool)> ByRef mute As Boolean) As Integer
        End Interface

    End Module

End Namespace
