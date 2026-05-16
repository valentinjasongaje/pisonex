Imports System.Windows.Forms
Imports System.Drawing
Imports System.Net.Http
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Threading
Imports PisoNetClient.Config

Namespace Forms

    ''' <summary>
    ''' Shown on first run to configure the server URL, PC number, and admin PASSWORD.
    ''' Cannot be dismissed without saving — required to proceed.
    ''' </summary>
    Public Class SetupDialog
        Inherits Form

        Private _txtUrl   As TextBox
        Private _nudPcNum As NumericUpDown
        Private _txtPin   As TextBox
        Private _txtPin2  As TextBox
        Private _txtApiKey As TextBox
        Private _btnScan  As Button
        Private _btnTest  As Button

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.Size            = New Size(440, 480)
            Me.BackColor       = FormStyles.DarkBg
            Me.ForeColor       = Color.White

            Dim y = 20

            ' ── Title ─────────────────────────────────────────────────────
            AddLabel("Pisonex Client Setup", New Font("Segoe UI", 16, FontStyle.Bold),
                     Color.White, New Point(24, y))
            y += 44

            AddLabel("Configure this PC before it can connect to the server.",
                     New Font("Segoe UI", 9), Color.FromArgb(120, 140, 180), New Point(24, y))
            y += 30

            ' ── Server IP ──────────────────────────────────────────────────
            AddLabel("Server IP Address", New Font("Segoe UI", 9, FontStyle.Bold),
                     Color.FromArgb(148, 163, 184), New Point(24, y))
            y += 18
            _txtUrl = AddTextBox(New Point(24, y), 280, AppConfig.ServerIp)
            _txtUrl.PlaceholderText = "e.g. 192.168.1.100"

            ' Test button beside the IP textbox
            _btnTest = New Button() With {
                .Text = "Test",
                .Location = New Point(312, y),
                .Size = New Size(96, 26),
                .FlatStyle = FlatStyle.Flat,
                .BackColor = Color.FromArgb(30, 36, 56),
                .ForeColor = Color.FromArgb(148, 163, 184),
                .Font = New Font("Segoe UI", 8),
                .Cursor = Cursors.Hand
            }
            _btnTest.FlatAppearance.BorderColor = FormStyles.BorderClr
            AddHandler _btnTest.Click, AddressOf OnTestConnection
            Me.Controls.Add(_btnTest)
            y += 28

            ' Scan for Server button
            _btnScan = New Button() With {
                .Text = "Scan for Server",
                .Location = New Point(24, y),
                .Size = New Size(384, 32),
                .FlatStyle = FlatStyle.Flat,
                .BackColor = Color.FromArgb(30, 36, 56),
                .ForeColor = FormStyles.AccentBlue,
                .Font = New Font("Segoe UI", 9),
                .Cursor = Cursors.Hand
            }
            _btnScan.FlatAppearance.BorderColor = FormStyles.BorderClr
            AddHandler _btnScan.Click, AddressOf OnScanForServer
            Me.Controls.Add(_btnScan)
            y += 40

            ' ── PC Number ──────────────────────────────────────────────────
            AddLabel("PC Number  (unique per machine)", New Font("Segoe UI", 9, FontStyle.Bold),
                     Color.FromArgb(148, 163, 184), New Point(24, y))
            y += 18
            _nudPcNum = New NumericUpDown() With {
                .Minimum  = 1, .Maximum = 99,
                .Value    = AppConfig.PCNumber,
                .Location = New Point(24, y),
                .Width    = 80,
                .BackColor = Color.FromArgb(22, 26, 42),
                .ForeColor = Color.White
            }
            Me.Controls.Add(_nudPcNum)
            y += 36

            ' ── Admin Password ─────────────────────────────────────────────
            AddLabel("Admin Password  (min 4 characters, used to access settings)",
                     New Font("Segoe UI", 9, FontStyle.Bold),
                     Color.FromArgb(148, 163, 184), New Point(24, y))
            y += 18
            Dim legacyPin = Config.LicenseStore.GetLegacyPinForSetup()
            _txtPin  = AddTextBox(New Point(24, y), 120, legacyPin, pwChar:="●"c)
            _txtPin2 = AddTextBox(New Point(160, y), 120, legacyPin, pwChar:="●"c)
            AddLabel("↑ Password", New Font("Segoe UI", 8), Color.FromArgb(100, 116, 139), New Point(24, y + 28))
            AddLabel("↑ Confirm", New Font("Segoe UI", 8), Color.FromArgb(100, 116, 139), New Point(160, y + 28))
            y += 56

            ' ── API Key ────────────────────────────────────────────────────
            AddLabel("Server API Key  (leave blank if not set)",
                     New Font("Segoe UI", 9, FontStyle.Bold),
                     Color.FromArgb(148, 163, 184), New Point(24, y))
            y += 18
            _txtApiKey = AddTextBox(New Point(24, y), 384, AppConfig.ApiKey)
            _txtApiKey.PlaceholderText = "Optional — must match CLIENT_API_KEY in server .env"
            y += 36

            ' ── Save button ────────────────────────────────────────────────
            Dim btnSave = FormStyles.CreateButton("Save && Start", 384, 38)
            btnSave.Location = New Point(24, y)
            AddHandler btnSave.Click, AddressOf OnSave
            Me.Controls.Add(btnSave)

            Me.AcceptButton = btnSave

            ' Apply borderless styling (this shifts all controls down and adds title bar)
            FormStyles.MakeBorderless(Me, "Pisonex — First Time Setup", closable:=False)
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            Dim ip = _txtUrl.Text.Trim()
            Dim pin = _txtPin.Text.Trim()
            Dim pin2 = _txtPin2.Text.Trim()

            If String.IsNullOrWhiteSpace(ip) Then
                Warn("Server IP Address cannot be empty.") : Return
            End If
            If pin.Length < 4 Then
                Warn("Admin password must be at least 4 characters.") : Return
            End If
            If pin <> pin2 Then
                Warn("Passwords do not match.") : Return
            End If

            AppConfig.SaveServerUrl("http://" & ip)
            AppConfig.SavePCNumber(CInt(_nudPcNum.Value))
            Config.LicenseStore.SaveAdminPinHash(pin)
            AppConfig.SaveApiKey(_txtApiKey.Text.Trim())
            AppConfig.SaveIsConfigured(True)

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

        ' ── Scan / Test ───────────────────────────────────────────────────

        Private Async Sub OnTestConnection(sender As Object, e As EventArgs)
            Dim ip = _txtUrl.Text.Trim()
            If String.IsNullOrWhiteSpace(ip) Then
                Warn("Enter an IP address first.") : Return
            End If

            _btnTest.Enabled = False
            _btnTest.Text = "..."
            Try
                Using client As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(3)}
                    Dim response = Await client.GetAsync($"http://{ip}/health")
                    If response.IsSuccessStatusCode Then
                        _btnTest.Text = "OK ✓"
                        _btnTest.ForeColor = Color.FromArgb(34, 197, 94)
                    Else
                        _btnTest.Text = "Fail ✗"
                        _btnTest.ForeColor = Color.FromArgb(239, 68, 68)
                    End If
                End Using
            Catch
                _btnTest.Text = "Fail ✗"
                _btnTest.ForeColor = Color.FromArgb(239, 68, 68)
            End Try

            _btnTest.Enabled = True
            ' Reset label after 3 seconds
            Dim resetTimer = New System.Windows.Forms.Timer() With {.Interval = 3000}
            AddHandler resetTimer.Tick, Sub(s2, e2)
                resetTimer.Stop()
                resetTimer.Dispose()
                _btnTest.Text = "Test"
                _btnTest.ForeColor = Color.FromArgb(148, 163, 184)
            End Sub
            resetTimer.Start()
        End Sub

        Private Async Sub OnScanForServer(sender As Object, e As EventArgs)
            _btnScan.Enabled = False
            Dim originalText = _btnScan.Text
            _btnScan.Text = "Scanning..."

            Try
                Dim subnet = GetLocalSubnet()
                If subnet Is Nothing Then
                    Warn("Could not determine local network subnet.")
                    Return
                End If

                Dim foundIp = Await ScanSubnetAsync(subnet)
                If foundIp IsNot Nothing Then
                    _txtUrl.Text = foundIp
                    _btnScan.Text = $"Found: {foundIp}"
                    _btnScan.ForeColor = Color.FromArgb(34, 197, 94)
                Else
                    Warn("No Pisonex server found on the local network." & vbCrLf & vbCrLf &
                         "Make sure the server is running and port 80 is allowed through Windows Firewall.")
                End If
            Catch
                Warn("Scan failed. Check your network connection.")
            Finally
                _btnScan.Enabled = True
                ' Reset after 4 seconds if it showed a found IP
                Dim resetTimer = New System.Windows.Forms.Timer() With {.Interval = 4000}
                AddHandler resetTimer.Tick, Sub(s2, e2)
                    resetTimer.Stop()
                    resetTimer.Dispose()
                    _btnScan.Text = originalText
                    _btnScan.ForeColor = FormStyles.AccentBlue
                End Sub
                resetTimer.Start()
            End Try
        End Sub

        Private Shared Function GetLocalSubnet() As String
            For Each nic In NetworkInterface.GetAllNetworkInterfaces()
                If nic.OperationalStatus <> OperationalStatus.Up Then Continue For
                If nic.NetworkInterfaceType = NetworkInterfaceType.Loopback Then Continue For
                For Each addr In nic.GetIPProperties().UnicastAddresses
                    If addr.Address.AddressFamily = AddressFamily.InterNetwork Then
                        Dim ip = addr.Address.ToString()
                        ' Return the first 3 octets, e.g. "192.168.1"
                        Dim parts = ip.Split("."c)
                        If parts.Length = 4 Then Return $"{parts(0)}.{parts(1)}.{parts(2)}"
                    End If
                Next
            Next
            Return Nothing
        End Function

        Private Shared Async Function ScanSubnetAsync(subnet As String) As Task(Of String)
            Dim cts As New CancellationTokenSource()
            Dim semaphore As New SemaphoreSlim(50)
            Dim foundIp As String = Nothing

            Dim tasks As New List(Of Task)()
            For i = 1 To 254
                Dim ip = $"{subnet}.{i}"
                tasks.Add(Task.Run(Async Function()
                    If cts.IsCancellationRequested Then Return
                    Await semaphore.WaitAsync(cts.Token)
                    Try
                        If cts.IsCancellationRequested Then Return
                        Using client As New HttpClient() With {.Timeout = TimeSpan.FromMilliseconds(800)}
                            Dim response = Await client.GetAsync($"http://{ip}/health", cts.Token)
                            If response.IsSuccessStatusCode Then
                                Dim body = Await response.Content.ReadAsStringAsync()
                                If body.Contains("""status""") Then
                                    Interlocked.CompareExchange(foundIp, ip, Nothing)
                                    cts.Cancel()
                                End If
                            End If
                        End Using
                    Catch
                        ' Timeout or cancelled — expected for most IPs
                    Finally
                        semaphore.Release()
                    End Try
                End Function))
            Next

            Try
                Await Task.WhenAll(tasks)
            Catch ex As OperationCanceledException
                ' Expected when a server is found
            End Try

            Return foundIp
        End Function

        ' ── UI helpers ────────────────────────────────────────────────────

        Private Sub AddLabel(text As String, font As Font, color As Color, loc As Point)
            Me.Controls.Add(New Label() With {
                .Text      = text,
                .Font      = font,
                .ForeColor = color,
                .AutoSize  = True,
                .Location  = loc
            })
        End Sub

        Private Function AddTextBox(loc As Point, width As Integer,
                                    text As String,
                                    Optional pwChar As Char = Nothing) As TextBox
            Dim tb = New TextBox() With {
                .Text        = text,
                .Location    = loc,
                .Width       = width,
                .BackColor   = Color.FromArgb(26, 30, 45),
                .ForeColor   = Color.White,
                .BorderStyle = BorderStyle.FixedSingle,
                .MaxLength   = 256
            }
            If pwChar <> Nothing Then tb.PasswordChar = pwChar
            Me.Controls.Add(tb)
            Return tb
        End Function

        Private Sub Warn(msg As String)
            MessageBox.Show(msg, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Sub

    End Class

End Namespace
