Imports System.Windows.Forms
Imports System.Drawing
Imports PisoNetClient.Config

Namespace Forms

    ''' <summary>
    ''' Shown on first run to configure the server URL, PC number, and admin PIN.
    ''' Cannot be dismissed without saving — required to proceed.
    ''' </summary>
    Public Class SetupDialog
        Inherits Form

        Private _txtUrl   As TextBox
        Private _nudPcNum As NumericUpDown
        Private _txtPin   As TextBox
        Private _txtPin2  As TextBox
        Private _txtApiKey As TextBox

        Public Sub New()
            InitializeComponent()
        End Sub

        Private Sub InitializeComponent()
            Me.Text            = "PisoNet — First Time Setup"
            Me.FormBorderStyle = FormBorderStyle.FixedDialog
            Me.Size            = New Size(440, 420)
            Me.StartPosition   = FormStartPosition.CenterScreen
            Me.MaximizeBox     = False
            Me.MinimizeBox     = False
            Me.BackColor       = Color.FromArgb(15, 20, 35)
            Me.ForeColor       = Color.White

            Dim y = 20

            ' ── Title ─────────────────────────────────────────────────────
            AddLabel("PisoNet Client Setup", New Font("Segoe UI", 16, FontStyle.Bold),
                     Color.White, New Point(24, y))
            y += 44

            AddLabel("Configure this PC before it can connect to the server.",
                     New Font("Segoe UI", 9), Color.FromArgb(120, 140, 180), New Point(24, y))
            y += 30

            ' ── Server URL ─────────────────────────────────────────────────
            AddLabel("Server URL", New Font("Segoe UI", 9, FontStyle.Bold),
                     Color.FromArgb(148, 163, 184), New Point(24, y))
            y += 18
            _txtUrl = AddTextBox(New Point(24, y), 384, AppConfig.ServerUrl)
            y += 36

            ' ── PC Number ──────────────────────────────────────────────────
            AddLabel("PC Number  (unique per machine)", New Font("Segoe UI", 9, FontStyle.Bold),
                     Color.FromArgb(148, 163, 184), New Point(24, y))
            y += 18
            _nudPcNum = New NumericUpDown() With {
                .Minimum  = 1, .Maximum = 99,
                .Value    = AppConfig.PCNumber,
                .Location = New Point(24, y),
                .Width    = 80,
                .BackColor = Color.FromArgb(26, 30, 45),
                .ForeColor = Color.White
            }
            Me.Controls.Add(_nudPcNum)
            y += 36

            ' ── Admin PIN ──────────────────────────────────────────────────
            AddLabel("Admin PIN  (4-8 digits, used to access settings)",
                     New Font("Segoe UI", 9, FontStyle.Bold),
                     Color.FromArgb(148, 163, 184), New Point(24, y))
            y += 18
            _txtPin  = AddTextBox(New Point(24, y), 120, AppConfig.AdminPin, pwChar:="●"c)
            _txtPin2 = AddTextBox(New Point(160, y), 120, AppConfig.AdminPin, pwChar:="●"c)
            AddLabel("↑ PIN", New Font("Segoe UI", 8), Color.FromArgb(100, 116, 139), New Point(24, y + 28))
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
            Dim btnSave = New Button() With {
                .Text      = "Save & Start",
                .Location  = New Point(24, y),
                .Size      = New Size(140, 38),
                .BackColor = Color.FromArgb(79, 142, 247),
                .ForeColor = Color.White,
                .FlatStyle = FlatStyle.Flat
            }
            btnSave.FlatAppearance.BorderSize = 0
            AddHandler btnSave.Click, AddressOf OnSave
            Me.Controls.Add(btnSave)

            Me.AcceptButton = btnSave
        End Sub

        Private Sub OnSave(sender As Object, e As EventArgs)
            Dim url = _txtUrl.Text.Trim()
            Dim pin = _txtPin.Text.Trim()
            Dim pin2 = _txtPin2.Text.Trim()

            If String.IsNullOrWhiteSpace(url) Then
                Warn("Server URL cannot be empty.") : Return
            End If
            If pin.Length < 4 Then
                Warn("Admin PIN must be at least 4 digits.") : Return
            End If
            If Not pin.All(Function(c) Char.IsDigit(c)) Then
                Warn("Admin PIN must contain digits only.") : Return
            End If
            If pin <> pin2 Then
                Warn("PINs do not match.") : Return
            End If

            AppConfig.SaveServerUrl(url)
            AppConfig.SavePCNumber(CInt(_nudPcNum.Value))
            AppConfig.SaveAdminPin(pin)
            AppConfig.SaveApiKey(_txtApiKey.Text.Trim())
            AppConfig.SaveIsConfigured(True)

            Me.DialogResult = DialogResult.OK
            Me.Close()
        End Sub

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
