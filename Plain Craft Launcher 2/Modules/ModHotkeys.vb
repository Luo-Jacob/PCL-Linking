Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Windows.Input
Imports System.Windows.Interop
Imports Newtonsoft.Json

''' <summary>
''' 全局 / 窗口快捷键管理器（使用 Win32 RegisterHotKey / WM_HOTKEY，不注入全局键盘钩子）。
''' 默认没有任何绑定，由用户在「百宝箱 → 全局快捷键」按需添加。
''' </summary>
Public Module ModHotkeys

    Private Const WM_HOTKEY As Integer = &H312
    Private Const MOD_ALT As UInteger = &H1UI
    Private Const MOD_CONTROL As UInteger = &H2UI
    Private Const MOD_SHIFT As UInteger = &H4UI
    Private Const MOD_WIN As UInteger = &H8UI
    Private Const MOD_NOREPEAT As UInteger = &H4000UI

    <DllImport("user32.dll", SetLastError:=True)>
    Private Function RegisterHotKey(hWnd As IntPtr, id As Integer, fsModifiers As UInteger, vk As UInteger) As Boolean
    End Function

    <DllImport("user32.dll", SetLastError:=True)>
    Private Function UnregisterHotKey(hWnd As IntPtr, id As Integer) As Boolean
    End Function

    Public Enum HotkeyActionId
        LaunchSelected = 0
        OpenLatestLog = 1
        OpenGameDir = 2
        SwitchAccount = 3
        ToggleWindow = 4
        NavLaunch = 5
        NavDownload = 6
        NavPresets = 7
        NavTools = 8
        NavSaves = 9
        NavSnapshot = 10
        NavSetup = 11
        NavFriends = 12
        ToggleOverlay = 13
        NavHelp = 14
        ToggleBorderless = 15
        NavServerPing = 16
    End Enum

    Public Class HotkeyBinding
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Modifiers As Integer
        Public Property KeyCode As Integer
        Public Property Action As HotkeyActionId = HotkeyActionId.LaunchSelected
        Public Property Enabled As Boolean = True
        Public Property OnlyWhenFocused As Boolean = False
        <JsonIgnore>
        Public Property RuntimeId As Integer
        <JsonIgnore>
        Public Property RegisterOk As Boolean = True
    End Class

    Private HostHwnd As IntPtr = IntPtr.Zero
    Private HostSource As HwndSource = Nothing
    Private IsHookInstalled As Boolean = False
    Private NextRuntimeId As Integer = &H4B00
    Private ReadOnly ActiveMap As New Dictionary(Of Integer, HotkeyBinding)
    Private ReadOnly FocusedBindings As New List(Of HotkeyBinding)
    Public Property IsCaptureSuspended As Boolean = False

    Public Function GetHotkeyConfigPath() As String
        Return GetPortableDataPath("Hotkeys.json")
    End Function

    Public Function LoadHotkeyBindings() As List(Of HotkeyBinding)
        Try
            Dim PathStr = GetHotkeyConfigPath()
            If Not File.Exists(PathStr) Then Return New List(Of HotkeyBinding)
            Dim Loaded = JsonConvert.DeserializeObject(Of List(Of HotkeyBinding))(File.ReadAllText(PathStr, Encoding.UTF8))
            If Loaded Is Nothing Then Return New List(Of HotkeyBinding)
            Return Loaded
        Catch ex As Exception
            Logger.Warn(ex, "读取全局快捷键配置失败")
            Return New List(Of HotkeyBinding)
        End Try
    End Function

    Public Sub SaveHotkeyBindings(Bindings As List(Of HotkeyBinding))
        Try
            Dim PathStr = GetHotkeyConfigPath()
            Directory.CreateDirectory(Path.GetDirectoryName(PathStr))
            Dim TmpPath = PathStr & ".tmp"
            File.WriteAllText(TmpPath, JsonConvert.SerializeObject(If(Bindings, New List(Of HotkeyBinding)), Formatting.Indented), New UTF8Encoding(False))
            If File.Exists(PathStr) Then
                File.Replace(TmpPath, PathStr, Nothing, True)
            Else
                File.Move(TmpPath, PathStr)
            End If
        Catch ex As Exception
            Logger.Error(ex, "保存全局快捷键配置失败")
        End Try
    End Sub

    Public Function FormatKeyDisplay(Modifiers As ModifierKeys, KeyVal As Key) As String
        Dim Parts As New List(Of String)
        If (Modifiers And ModifierKeys.Control) <> 0 Then Parts.Add("Ctrl")
        If (Modifiers And ModifierKeys.Alt) <> 0 Then Parts.Add("Alt")
        If (Modifiers And ModifierKeys.Shift) <> 0 Then Parts.Add("Shift")
        If (Modifiers And ModifierKeys.Windows) <> 0 Then Parts.Add("Win")
        Parts.Add(KeyVal.ToString())
        Return String.Join(" + ", Parts)
    End Function

    Public Function FormatActionDisplay(Action As HotkeyActionId) As String
        Select Case Action
            Case HotkeyActionId.LaunchSelected : Return "启动当前选中的游戏实例"
            Case HotkeyActionId.OpenLatestLog : Return "打开当前实例最新游戏日志"
            Case HotkeyActionId.OpenGameDir : Return "打开当前实例游戏目录"
            Case HotkeyActionId.SwitchAccount : Return "轮换切换下一个离线账号"
            Case HotkeyActionId.ToggleWindow : Return "显示 / 最小化 PCL Linking 窗口"
            Case HotkeyActionId.NavLaunch : Return "跳转到「启动」页面"
            Case HotkeyActionId.NavDownload : Return "跳转到「下载」页面"
            Case HotkeyActionId.NavPresets : Return "跳转到「下载 → 常用模组清单」"
            Case HotkeyActionId.NavTools : Return "跳转到「百宝箱」页面"
            Case HotkeyActionId.NavSaves : Return "跳转到当前实例「世界存档管理」"
            Case HotkeyActionId.NavSnapshot : Return "跳转到当前实例「实例快照回滚」"
            Case HotkeyActionId.NavSetup : Return "跳转到「设置」页面"
            Case HotkeyActionId.NavFriends : Return "跳转到「好友」页面"
            Case HotkeyActionId.ToggleOverlay : Return "显示 / 隐藏游戏内好友消息浮窗"
            Case HotkeyActionId.NavHelp : Return "跳转到「百宝箱 → 使用帮助」"
            Case HotkeyActionId.ToggleBorderless : Return "切换运行中 Minecraft 无边框全屏"
            Case HotkeyActionId.NavServerPing : Return "跳转到「百宝箱 → 服务器状态探测」"
            Case Else : Return $"未知动作 ({CInt(Action)})"
        End Select
    End Function

    Public Sub HotkeysInit(Window As Window)
        If Window Is Nothing Then Return
        HostHwnd = New WindowInteropHelper(Window).Handle
        If HostHwnd = IntPtr.Zero Then Return
        If Not IsHookInstalled Then
            HostSource = HwndSource.FromHwnd(HostHwnd)
            If HostSource IsNot Nothing Then
                HostSource.AddHook(AddressOf WndProcHook)
                IsHookInstalled = True
            End If
        End If
        ReloadAndApplyHotkeys()
    End Sub

    Public Sub HotkeysDispose()
        UnregisterAllActive()
        If IsHookInstalled AndAlso HostSource IsNot Nothing Then
            Try
                HostSource.RemoveHook(AddressOf WndProcHook)
            Catch
            End Try
            IsHookInstalled = False
        End If
    End Sub

    Private Sub UnregisterAllActive()
        If HostHwnd <> IntPtr.Zero Then
            For Each Id In ActiveMap.Keys.ToList()
                Try
                    UnregisterHotKey(HostHwnd, Id)
                Catch
                End Try
            Next
        End If
        ActiveMap.Clear()
        FocusedBindings.Clear()
        NextRuntimeId = &H4B00
    End Sub

    Public Function ReloadAndApplyHotkeys() As List(Of HotkeyBinding)
        Dim Bindings = LoadHotkeyBindings()
        ApplyHotkeys(Bindings)
        Return Bindings
    End Function

    Public Sub ApplyHotkeys(Bindings As List(Of HotkeyBinding))
        UnregisterAllActive()
        If Bindings Is Nothing Then Return
        For Each B In Bindings
            If Not B.Enabled OrElse B.KeyCode <= 0 Then
                B.RegisterOk = True
                Continue For
            End If
            If B.OnlyWhenFocused Then
                B.RegisterOk = True
                FocusedBindings.Add(B)
                Continue For
            End If
            If HostHwnd = IntPtr.Zero Then
                B.RegisterOk = False
                Continue For
            End If
            Dim ModFlags As UInteger = MOD_NOREPEAT
            Dim Mods = CType(B.Modifiers, ModifierKeys)
            If (Mods And ModifierKeys.Alt) <> 0 Then ModFlags = ModFlags Or MOD_ALT
            If (Mods And ModifierKeys.Control) <> 0 Then ModFlags = ModFlags Or MOD_CONTROL
            If (Mods And ModifierKeys.Shift) <> 0 Then ModFlags = ModFlags Or MOD_SHIFT
            If (Mods And ModifierKeys.Windows) <> 0 Then ModFlags = ModFlags Or MOD_WIN

            Dim Vk As UInteger = CUInt(KeyInterop.VirtualKeyFromKey(CType(B.KeyCode, Key)))
            If Vk = 0UI Then
                B.RegisterOk = False
                Continue For
            End If
            NextRuntimeId += 1
            Dim Id As Integer = NextRuntimeId
            Dim Ok As Boolean = RegisterHotKey(HostHwnd, Id, ModFlags, Vk)
            B.RegisterOk = Ok
            If Ok Then
                B.RuntimeId = Id
                ActiveMap(Id) = B
            Else
                Logger.Warn($"注册快捷键失败（可能被其他程序占用）：{FormatKeyDisplay(Mods, CType(B.KeyCode, Key))}")
            End If
        Next
    End Sub

    Private Function WndProcHook(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr, ByRef handled As Boolean) As IntPtr
        If msg = WM_HOTKEY Then
            If IsCaptureSuspended Then Return IntPtr.Zero
            Dim Id As Integer = wParam.ToInt32()
            Dim Binding As HotkeyBinding = Nothing
            If ActiveMap.TryGetValue(Id, Binding) Then
                If Binding.OnlyWhenFocused AndAlso (FrmMain Is Nothing OrElse Not FrmMain.IsActive) Then
                    Return IntPtr.Zero
                End If
                handled = True
                RunInUi(Sub() ExecuteHotkeyAction(Binding.Action))
            End If
        End If
        Return IntPtr.Zero
    End Function

    Public Function TryDispatchFocusedHotkey(e As KeyEventArgs) As Boolean
        If IsCaptureSuspended OrElse e Is Nothing OrElse e.IsRepeat Then Return False
        If FrmMain Is Nothing OrElse FrmMain.PanMsg.Children.Count > 0 Then Return False
        If Not FocusedBindings.Any() Then Return False
        Dim ActualKey As Key = If(e.Key = Key.System, e.SystemKey, If(e.Key = Key.ImeProcessed, e.ImeProcessedKey, e.Key))
        If ActualKey = Key.None Then Return False
        Dim Mods = Keyboard.Modifiers
        If Mods = ModifierKeys.None Then
            Dim Focused = TryCast(Keyboard.FocusedElement, DependencyObject)
            If TypeOf Focused Is System.Windows.Controls.Primitives.TextBoxBase OrElse TypeOf Focused Is System.Windows.Controls.PasswordBox Then
                Return False
            End If
        End If
        Dim ModInt = CInt(Mods)
        Dim KeyInt = CInt(ActualKey)
        Dim Matched = FocusedBindings.FirstOrDefault(Function(b) b.Enabled AndAlso b.Modifiers = ModInt AndAlso b.KeyCode = KeyInt)
        If Matched Is Nothing Then Return False
        e.Handled = True
        RunInUi(Sub() ExecuteHotkeyAction(Matched.Action))
        Return True
    End Function

    Public Sub ExecuteHotkeyAction(Action As HotkeyActionId)
        If FrmMain Is Nothing Then Return
        Try
            Select Case Action
                Case HotkeyActionId.LaunchSelected
                    If McInstanceSelected Is Nothing Then
                        Hint("当前未选中任何 Minecraft 实例，请先选择版本！", HintType.Red)
                        Return
                    End If
                    If FrmLaunchLeft IsNot Nothing Then
                        FrmLaunchLeft.LaunchButtonClick()
                    Else
                        FrmMain.PageChange(FormMain.PageType.Launch)
                    End If

                Case HotkeyActionId.OpenLatestLog
                    Dim Opened As Boolean = False
                    If McInstanceSelected IsNot Nothing Then
                        Dim LogDir = Path.Combine(PathUtils.RemoveExtendedPrefix(McInstanceSelected.PathIndie), "logs")
                        For Each Candidate In {"latest.log", "debug.log"}
                            Dim Full = Path.Combine(LogDir, Candidate)
                            If File.Exists(Full) Then
                                OpenWebsite(Full)
                                Opened = True
                                Exit For
                            End If
                        Next
                        If Not Opened AndAlso Directory.Exists(LogDir) Then
                            OpenExplorer(LogDir)
                            Opened = True
                        End If
                    End If
                    If Not Opened Then
                        Dim BaseLogDir = PathUtils.RemoveExtendedPrefix(Paths.BaseThenName)
                        Dim LauncherLog = Path.Combine(BaseLogDir, "Log1.txt")
                        If File.Exists(LauncherLog) Then
                            OpenWebsite(LauncherLog)
                        Else
                            OpenExplorer(BaseLogDir)
                        End If
                    End If

                Case HotkeyActionId.OpenGameDir
                    If McInstanceSelected Is Nothing Then
                        Hint("当前未选中任何游戏实例！", HintType.Red)
                        Return
                    End If
                    Dim CleanIndie = PathUtils.RemoveExtendedPrefix(McInstanceSelected.PathIndie)
                    Directory.CreateDirectory(CleanIndie)
                    OpenExplorer(CleanIndie)

                Case HotkeyActionId.SwitchAccount
                    If Settings.Get(Of Integer)("LoginType") <> CInt(McLoginType.Legacy) Then
                        RestoreWindow()
                        FrmMain.PageChange(FormMain.PageType.Launch)
                        Hint("当前不是离线登录模式，无法使用快捷键轮换离线账号。", HintType.Blue)
                        Return
                    End If
                    Dim RawNames = Settings.Get(Of String)("LoginLegacyName")
                    Dim Names = If(String.IsNullOrEmpty(RawNames), New List(Of String), RawNames.Split("¨"c).Where(Function(s) Not String.IsNullOrWhiteSpace(s)).ToList())
                    If Names.Count >= 2 Then
                        Dim First = Names(0)
                        Names.RemoveAt(0)
                        Names.Add(First)
                        Settings.Set("LoginLegacyName", String.Join("¨", Names))
                        FrmLaunchLeft?.RefreshPage(False, True)
                        Hint($"已切换账号至：{Names(0)}", HintType.Green)
                    Else
                        FrmMain.PageChange(FormMain.PageType.Launch)
                        RestoreWindow()
                        Hint("当前仅配置了单个离线账号，请先在启动页添加多个账号名或登录。", HintType.Blue)
                    End If

                Case HotkeyActionId.ToggleWindow
                    If FrmMain.Hidden OrElse FrmMain.WindowState = WindowState.Minimized OrElse FrmMain.Visibility <> Visibility.Visible Then
                        RestoreWindow()
                    Else
                        FrmMain.WindowState = WindowState.Minimized
                    End If

                Case HotkeyActionId.NavLaunch
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Launch)
                Case HotkeyActionId.NavDownload
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall)
                Case HotkeyActionId.NavPresets
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadPresets)
                Case HotkeyActionId.NavTools
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsHotkeys)
                Case HotkeyActionId.NavSaves
                    If McInstanceSelected Is Nothing Then
                        Hint("请先选择一个游戏实例再打开世界存档管理！", HintType.Red)
                        Return
                    End If
                    RestoreWindow()
                    PageInstanceLeft.Instance = McInstanceSelected
                    FrmMain.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceSaves)
                Case HotkeyActionId.NavSnapshot
                    If McInstanceSelected Is Nothing Then
                        Hint("请先选择一个游戏实例再打开实例快照！", HintType.Red)
                        Return
                    End If
                    RestoreWindow()
                    PageInstanceLeft.Instance = McInstanceSelected
                    FrmMain.PageChange(FormMain.PageType.InstanceSetup, FormMain.PageSubType.InstanceSnapshot)
                Case HotkeyActionId.NavSetup
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch)
                Case HotkeyActionId.NavFriends
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Friends)
                Case HotkeyActionId.ToggleOverlay
                    WinInGameOverlay.ToggleOverlay()
                Case HotkeyActionId.NavHelp
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsHelp)
                Case HotkeyActionId.ToggleBorderless
                    Hint(ModWatcher.Watcher.ToggleActiveMinecraftBorderlessFullscreen(), HintType.Blue)
                Case HotkeyActionId.NavServerPing
                    RestoreWindow()
                    FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsServerPing)
            End Select
        Catch ex As Exception
            Logger.Warn(ex, $"执行全局快捷键动作失败：{Action}")
        End Try
    End Sub

    Private Sub RestoreWindow()
        If FrmMain Is Nothing Then Return
        If FrmMain.Hidden Then FrmMain.Hidden = False
        If FrmMain.Visibility <> Visibility.Visible Then FrmMain.Show()
        If FrmMain.WindowState = WindowState.Minimized Then FrmMain.WindowState = WindowState.Normal
        FrmMain.Activate()
    End Sub

End Module
