Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

Namespace Forms

    ''' <summary>
    ''' Shared styling utilities for Pisonex borderless forms.
    ''' Call MakeBorderless() on any dialog to replace OS chrome with a custom title bar.
    ''' </summary>
    Public Module FormStyles

        ' ── Pisonex color palette (shared) ──────────────────────────────
        Public ReadOnly DarkBg      As Color = Color.FromArgb(12, 16, 28)
        Public ReadOnly SurfaceBg   As Color = Color.FromArgb(22, 26, 42)
        Public ReadOnly BorderClr   As Color = Color.FromArgb(36, 42, 64)
        Public ReadOnly AccentBlue  As Color = Color.FromArgb(79, 142, 247)
        Public ReadOnly TextPrimary As Color = Color.FromArgb(220, 228, 240)
        Public ReadOnly TextDim     As Color = Color.FromArgb(140, 160, 200)
        Public ReadOnly DangerRed   As Color = Color.FromArgb(180, 50, 50)

        Public Const TITLE_BAR_H As Integer = 38

        ' Win32 for drag (Friend so other forms can reuse)
        <DllImport("user32.dll", CharSet:=CharSet.Auto)>
        Friend Function ReleaseCapture() As Boolean
        End Function

        <DllImport("user32.dll", CharSet:=CharSet.Auto)>
        Friend Function SendMessage(hWnd As IntPtr, msg As Integer,
                                     wParam As IntPtr, lParam As IntPtr) As IntPtr
        End Function

        Friend Const WM_NCLBUTTONDOWN As Integer = &HA1
        Friend Const HTCAPTION        As Integer = 2

        ''' <summary>
        ''' Converts a standard dialog form into a borderless Pisonex-styled form.
        ''' Adds a custom title bar with drag support and a close button.
        ''' All existing controls are shifted down by TITLE_BAR_H.
        ''' </summary>
        Public Sub MakeBorderless(frm As Form, title As String,
                                  Optional closable As Boolean = True,
                                  Optional accentBar As Boolean = True)
            frm.FormBorderStyle = FormBorderStyle.None
            frm.BackColor = DarkBg
            frm.ForeColor = TextPrimary
            frm.StartPosition = FormStartPosition.CenterScreen
            frm.Icon = Resources.LogoHelper.GetIcon()

            ' Shift all existing controls down
            For Each ctl As Control In frm.Controls
                ctl.Location = New Point(ctl.Left, ctl.Top + TITLE_BAR_H)
            Next

            ' Expand form height to accommodate title bar
            frm.Height += TITLE_BAR_H

            ' ── Title bar panel ─────────────────────────────────────────
            Dim titleBar = New Panel() With {
                .Location  = New Point(0, 0),
                .Size      = New Size(frm.Width, TITLE_BAR_H),
                .BackColor = Color.FromArgb(8, 12, 22),
                .Cursor    = Cursors.SizeAll
            }

            ' Gradient accent line at top
            If accentBar Then
                AddHandler titleBar.Paint, Sub(s, e)
                    Dim g = e.Graphics
                    Dim r = New Rectangle(0, 0, titleBar.Width, 2)
                    Using br = New LinearGradientBrush(r, AccentBlue,
                            Color.FromArgb(124, 58, 237), 0F)
                        g.FillRectangle(br, r)
                    End Using
                    ' Bottom border line
                    Using pen = New Pen(Color.FromArgb(30, 80, 120, 200), 1)
                        g.DrawLine(pen, 0, titleBar.Height - 1, titleBar.Width, titleBar.Height - 1)
                    End Using
                End Sub
            End If

            ' Title text
            Dim lblTitle = New Label() With {
                .Text      = title,
                .Font      = New Font("Segoe UI", 9, FontStyle.Bold),
                .ForeColor = TextDim,
                .BackColor = Color.Transparent,
                .AutoSize  = False,
                .Size      = New Size(frm.Width - 50, TITLE_BAR_H),
                .Location  = New Point(14, 0),
                .TextAlign = ContentAlignment.MiddleLeft,
                .Cursor    = Cursors.SizeAll
            }

            ' Drag on title bar and label
            Dim dragHandler = Sub(sender As Object, ev As MouseEventArgs)
                If ev.Button = MouseButtons.Left Then
                    ReleaseCapture()
                    SendMessage(frm.Handle, WM_NCLBUTTONDOWN, New IntPtr(HTCAPTION), IntPtr.Zero)
                End If
            End Sub
            AddHandler titleBar.MouseDown, dragHandler
            AddHandler lblTitle.MouseDown, dragHandler

            titleBar.Controls.Add(lblTitle)

            ' Close button
            If closable Then
                Dim btnClose = New Label() With {
                    .Text      = ChrW(&H2715),
                    .Font      = New Font("Segoe UI", 10),
                    .ForeColor = Color.FromArgb(100, 120, 150),
                    .BackColor = Color.Transparent,
                    .Size      = New Size(TITLE_BAR_H, TITLE_BAR_H),
                    .Location  = New Point(frm.Width - TITLE_BAR_H, 0),
                    .TextAlign = ContentAlignment.MiddleCenter,
                    .Cursor    = Cursors.Hand
                }
                AddHandler btnClose.MouseEnter, Sub(s, e)
                    btnClose.BackColor = Color.FromArgb(60, 239, 68, 68)
                    btnClose.ForeColor = Color.FromArgb(239, 150, 150)
                End Sub
                AddHandler btnClose.MouseLeave, Sub(s, e)
                    btnClose.BackColor = Color.Transparent
                    btnClose.ForeColor = Color.FromArgb(100, 120, 150)
                End Sub
                AddHandler btnClose.Click, Sub(s, e) frm.Close()
                titleBar.Controls.Add(btnClose)
            End If

            ' Form border (painted on the form itself)
            AddHandler frm.Paint, Sub(s, e)
                Using pen = New Pen(Color.FromArgb(30, 80, 120, 200), 1)
                    e.Graphics.DrawRectangle(pen, 0, 0, frm.Width - 1, frm.Height - 1)
                End Using
            End Sub

            frm.Controls.Add(titleBar)
            titleBar.BringToFront()
        End Sub

        ''' <summary>
        ''' Creates a standard Pisonex-styled text input field.
        ''' </summary>
        Public Function CreateInput(loc As Point, width As Integer,
                                    Optional text As String = "",
                                    Optional maxLen As Integer = 256,
                                    Optional pwChar As Char = Nothing) As TextBox
            Dim tb = New TextBox() With {
                .Text        = text,
                .Location    = loc,
                .Width       = width,
                .BackColor   = Color.FromArgb(22, 26, 42),
                .ForeColor   = TextPrimary,
                .BorderStyle = BorderStyle.FixedSingle,
                .Font        = New Font("Segoe UI", 10),
                .MaxLength   = maxLen
            }
            If pwChar <> Nothing Then tb.PasswordChar = pwChar
            Return tb
        End Function

        ''' <summary>
        ''' Creates a standard Pisonex-styled label.
        ''' </summary>
        Public Function CreateLabel(text As String,
                                    Optional size As Single = 8.5F,
                                    Optional bold As Boolean = False,
                                    Optional clr As Color = Nothing) As Label
            If clr = Nothing Then clr = TextDim
            Return New Label() With {
                .Text      = text,
                .Font      = New Font("Segoe UI", size, If(bold, FontStyle.Bold, FontStyle.Regular)),
                .ForeColor = clr,
                .BackColor = Color.Transparent,
                .AutoSize  = True
            }
        End Function

        ''' <summary>
        ''' Creates a standard Pisonex-styled button.
        ''' </summary>
        Public Function CreateButton(text As String, width As Integer, height As Integer,
                                     Optional bg As Color = Nothing,
                                     Optional fg As Color = Nothing,
                                     Optional hoverBg As Color = Nothing) As Button
            If bg = Nothing Then bg = AccentBlue
            If fg = Nothing Then fg = Color.White
            If hoverBg = Nothing Then hoverBg = Color.FromArgb(100, 160, 255)
            Dim btn = New Button() With {
                .Text      = text,
                .Size      = New Size(width, height),
                .BackColor = bg,
                .ForeColor = fg,
                .FlatStyle = FlatStyle.Flat,
                .Font      = New Font("Segoe UI", 9.5F, FontStyle.Bold),
                .Cursor    = Cursors.Hand
            }
            btn.FlatAppearance.BorderSize = 0
            btn.FlatAppearance.MouseOverBackColor = hoverBg
            Return btn
        End Function

    End Module

End Namespace
