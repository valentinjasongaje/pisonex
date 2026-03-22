Imports System.Drawing
Imports System.Reflection

Namespace Resources

    ''' <summary>
    ''' Loads the embedded logo image from assembly resources.
    ''' Returns Nothing if the resource cannot be found (e.g. during development before
    ''' logo.png has been added to Resources\).
    ''' </summary>
    Friend Module LogoHelper

        Private _cached As Image = Nothing
        Private _loaded  As Boolean = False

        Public Function GetLogo() As Image
            If _loaded Then Return _cached
            _loaded = True
            Try
                Dim asm    = Assembly.GetExecutingAssembly()
                Dim stream = asm.GetManifestResourceStream("PisoNetClient.Resources.logo.png")
                If stream IsNot Nothing Then
                    _cached = Image.FromStream(stream)
                End If
            Catch
            End Try
            Return _cached
        End Function

        ''' <summary>
        ''' Returns the logo scaled to the requested size, or Nothing on failure.
        ''' </summary>
        Public Function GetLogo(width As Integer, height As Integer) As Image
            Dim src = GetLogo()
            If src Is Nothing Then Return Nothing
            Dim bmp As New Bitmap(width, height)
            Using g = Graphics.FromImage(bmp)
                g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic
                g.DrawImage(src, 0, 0, width, height)
            End Using
            Return bmp
        End Function

        Private _iconCached As Drawing.Icon = Nothing
        Private _iconLoaded As Boolean = False

        ''' <summary>
        ''' Returns the embedded .ico file as an Icon, or SystemIcons.Application as fallback.
        ''' </summary>
        Public Function GetIcon() As Drawing.Icon
            If _iconLoaded Then Return _iconCached
            _iconLoaded = True
            Try
                Dim asm    = Assembly.GetExecutingAssembly()
                Dim stream = asm.GetManifestResourceStream("PisoNetClient.Resources.logo.ico")
                If stream IsNot Nothing Then
                    _iconCached = New Drawing.Icon(stream)
                End If
            Catch
            End Try
            If _iconCached Is Nothing Then _iconCached = SystemIcons.Application
            Return _iconCached
        End Function

    End Module

End Namespace
