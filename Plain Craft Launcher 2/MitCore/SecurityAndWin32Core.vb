' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone Windows DPAPI credential helper and Win32 borderless fullscreen API wrapper (no PCL UI/core dependencies).

Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text

Public Module DpapiCredentialCore

    Public Const DpapiPrefix As String = "dpapi:"

    Public Function ProtectString(PlainText As String, Entropy As Byte()) As String
        Dim Raw As Byte() = Encoding.UTF8.GetBytes(If(PlainText, ""))
        Dim ProtectedBytes As Byte() = ProtectedData.Protect(Raw, Entropy, DataProtectionScope.CurrentUser)
        Return DpapiPrefix & Convert.ToBase64String(ProtectedBytes)
    End Function

    Public Function TryUnprotectString(StoredValue As String, Entropy As Byte(), ByRef OutPlain As String) As Boolean
        OutPlain = ""
        If StoredValue Is Nothing OrElse Not StoredValue.StartsWith(DpapiPrefix, StringComparison.Ordinal) Then Return False
        Dim EncBytes As Byte() = Convert.FromBase64String(StoredValue.Substring(DpapiPrefix.Length))
        Dim PlainBytes As Byte() = ProtectedData.Unprotect(EncBytes, Entropy, DataProtectionScope.CurrentUser)
        OutPlain = Encoding.UTF8.GetString(PlainBytes)
        Return True
    End Function

End Module

Public Module Win32BorderlessCore

    <StructLayout(LayoutKind.Sequential)>
    Private Structure NativeRect
        Public Left As Integer
        Public Top As Integer
        Public Right As Integer
        Public Bottom As Integer
    End Structure

    Private Class SavedBorderlessState
        Public OriginalStyle As Integer
        Public OriginalRect As NativeRect
    End Class

    Private Declare Function ShowWindow Lib "user32" (hWnd As IntPtr, cmdWindow As UInteger) As Boolean
    Private Declare Function IsIconic Lib "user32" (hWnd As IntPtr) As Boolean
    Private Declare Function IsWindow Lib "user32" (hWnd As IntPtr) As Boolean
    Private Declare Function GetWindowLongW Lib "user32" (hWnd As IntPtr, nIndex As Integer) As Integer
    Private Declare Function SetWindowLongW Lib "user32" (hWnd As IntPtr, nIndex As Integer, dwNewLong As Integer) As Integer
    Private Declare Function GetWindowRect Lib "user32" (hWnd As IntPtr, ByRef lpRect As NativeRect) As Boolean
    Private Declare Function SetWindowPos Lib "user32" (hWnd As IntPtr, hWndInsertAfter As IntPtr, X As Integer, Y As Integer, cx As Integer, cy As Integer, uFlags As UInteger) As Boolean

    Private ReadOnly BorderlessLock As New Object()
    Private ReadOnly BorderlessSavedStates As New Dictionary(Of IntPtr, SavedBorderlessState)

    Public Function IsValidWindowHandle(Hwnd As IntPtr) As Boolean
        Return Hwnd <> IntPtr.Zero AndAlso IsWindow(Hwnd)
    End Function

    Public Function IsWindowBorderless(Hwnd As IntPtr) As Boolean
        If Hwnd = IntPtr.Zero Then Return False
        SyncLock BorderlessLock
            Return BorderlessSavedStates.ContainsKey(Hwnd)
        End SyncLock
    End Function

    Public Sub ForgetWindow(Hwnd As IntPtr)
        If Hwnd = IntPtr.Zero Then Return
        SyncLock BorderlessLock
            BorderlessSavedStates.Remove(Hwnd)
        End SyncLock
    End Sub

    Public Function ToggleWindowBorderless(Hwnd As IntPtr, DisplayLabel As String) As String
        Const GWL_STYLE As Integer = -16
        Const WS_CAPTION As Integer = &HC00000I
        Const WS_THICKFRAME As Integer = &H40000I
        Const WS_MINIMIZEBOX As Integer = &H20000I
        Const WS_MAXIMIZEBOX As Integer = &H10000I
        Const WS_SYSMENU As Integer = &H80000I
        Const WS_MAXIMIZE As Integer = &H1000000I
        Const WS_POPUP As Integer = &H80000000I
        Const SWP_NOZORDER As UInteger = &H4UI
        Const SWP_NOACTIVATE As UInteger = &H10UI
        Const SWP_FRAMECHANGED As UInteger = &H20UI
        Const SWP_SHOWWINDOW As UInteger = &H40UI
        Const SW_RESTORE As UInteger = 9UI

        If Not IsValidWindowHandle(Hwnd) Then Return "未找到有效窗口句柄，无法切换无边框。"
        Dim SafeLabel = If(String.IsNullOrWhiteSpace(DisplayLabel), "Minecraft", DisplayLabel)

        SyncLock BorderlessLock
            Dim Saved As SavedBorderlessState = Nothing
            If BorderlessSavedStates.TryGetValue(Hwnd, Saved) Then
                SetWindowLongW(Hwnd, GWL_STYLE, Saved.OriginalStyle)
                Dim Width = Math.Max(640, Saved.OriginalRect.Right - Saved.OriginalRect.Left)
                Dim Height = Math.Max(480, Saved.OriginalRect.Bottom - Saved.OriginalRect.Top)
                SetWindowPos(Hwnd, IntPtr.Zero, Saved.OriginalRect.Left, Saved.OriginalRect.Top, Width, Height, SWP_FRAMECHANGED Or SWP_NOZORDER Or SWP_NOACTIVATE Or SWP_SHOWWINDOW)
                BorderlessSavedStates.Remove(Hwnd)
                Return $"已将「{SafeLabel}」恢复为窗口模式"
            Else
                If IsIconic(Hwnd) Then ShowWindow(Hwnd, SW_RESTORE)
                Dim OrigStyle = GetWindowLongW(Hwnd, GWL_STYLE)
                Dim OrigRect As New NativeRect
                If Not GetWindowRect(Hwnd, OrigRect) Then Return "获取窗口位置失败，无法切换无边框。"
                Dim ScreenObj = System.Windows.Forms.Screen.FromHandle(Hwnd)
                Dim Bounds = ScreenObj.Bounds
                Dim Work = ScreenObj.WorkingArea
                If OrigRect.Left < -10000 OrElse OrigRect.Top < -10000 Then
                    OrigRect = New NativeRect With {
                        .Left = Work.X + 80,
                        .Top = Work.Y + 60,
                        .Right = Work.X + 80 + Math.Min(1280, Math.Max(640, Work.Width - 160)),
                        .Bottom = Work.Y + 60 + Math.Min(720, Math.Max(480, Work.Height - 120))
                    }
                End If
                BorderlessSavedStates(Hwnd) = New SavedBorderlessState With {.OriginalStyle = OrigStyle, .OriginalRect = OrigRect}
                ShowWindow(Hwnd, SW_RESTORE)
                Dim NewStyle = (OrigStyle And Not (WS_CAPTION Or WS_THICKFRAME Or WS_MINIMIZEBOX Or WS_MAXIMIZEBOX Or WS_SYSMENU Or WS_MAXIMIZE)) Or WS_POPUP
                SetWindowLongW(Hwnd, GWL_STYLE, NewStyle)
                SetWindowPos(Hwnd, IntPtr.Zero, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, SWP_FRAMECHANGED Or SWP_NOZORDER Or SWP_NOACTIVATE Or SWP_SHOWWINDOW)
                Return $"已将「{SafeLabel}」切换为无边框全屏（{Bounds.Width}×{Bounds.Height}）"
            End If
        End SyncLock
    End Function

End Module
