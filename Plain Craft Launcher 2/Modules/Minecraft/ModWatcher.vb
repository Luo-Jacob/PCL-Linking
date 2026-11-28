Public Module ModWatcher

    '对全体的监视
    Public McWatcherList As New List(Of Watcher)
    Private ReadOnly WatcherListLock As New Object
    Private IsWatcherRunning As Boolean = False
    Public HasRunningMinecraft As Boolean = False
    Public Event LiveLogAppended(Pid As Integer, InstanceName As String, Line As String)
    Public Function GetWatchersSnapshot() As Watcher()
        SyncLock WatcherListLock
            Return McWatcherList.ToArray()
        End SyncLock
    End Function
    Private Sub WatcherStateChanged()
        Dim IsRunning As Boolean = False
        Dim TriggerLauncherShutdown As Boolean = True
        Dim BecomeRunning As Boolean? = Nothing
        SyncLock WatcherListLock
            For Each Watcher In McWatcherList
                If Watcher.State = Watcher.MinecraftState.Loading OrElse Watcher.State = Watcher.MinecraftState.Running OrElse
                   (Watcher.State = Watcher.MinecraftState.Crashed AndAlso Watcher.GameProcess IsNot Nothing AndAlso Not Watcher.GameProcess.HasExited) Then
                    IsRunning = True
                    Exit For
                ElseIf Watcher.State = Watcher.MinecraftState.Crashed OrElse Watcher.State = Watcher.MinecraftState.Canceled Then
                    TriggerLauncherShutdown = False
                End If
            Next
            If IsWatcherRunning = IsRunning Then Return
            IsWatcherRunning = IsRunning
            BecomeRunning = IsWatcherRunning
        End SyncLock
        If BecomeRunning.Value Then
            MinecraftStart()
        Else
            MinecraftStop(TriggerLauncherShutdown)
        End If
    End Sub
    Private Sub MinecraftStart()
        McLaunchLog("[全局] 出现运行中的 Minecraft")
        HasRunningMinecraft = True
        FrmMain.BtnExtraShutdown.ShowRefresh()
        FrmMain.BtnExtraGameLog?.ShowRefresh()
    End Sub
    Private Sub MinecraftStop(TriggerLauncherShutdown As Boolean)
        McLaunchLog("[全局] 已无运行中的 Minecraft")
        HasRunningMinecraft = False
        FrmMain.BtnExtraShutdown.ShowRefresh()
        FrmMain.BtnExtraGameLog?.ShowRefresh()
        '音乐播放
        If Settings.Get(Of Boolean)("UiMusicStop") Then
            RunInUi(Sub() If MusicResume() Then Logger.Info("已根据设置，在结束后开始音乐播放"))
        ElseIf Settings.Get(Of Boolean)("UiMusicStart") Then
            RunInUi(Sub() If MusicPause() Then Logger.Info("已根据设置，在结束后暂停音乐播放"))
        End If
        '启动器可见性
        Select Case Settings.Get(Of Integer)("LaunchArgumentVisible")
            Case 2
                '直接关闭
                If TriggerLauncherShutdown Then
                    RunInUi(Sub() FrmMain.EndProgram(False))
                Else
                    RunInUi(Sub() FrmMain.Hidden = False)
                End If
            Case 3
                '恢复
                RunInUi(Sub() FrmMain.Hidden = False)
        End Select
    End Sub

    Public Sub FlushActivePlaytimeSessions()
        Dim Snapshot As Watcher()
        SyncLock WatcherListLock
            Snapshot = McWatcherList.ToArray()
        End SyncLock
        For Each W In Snapshot
            Try
                W?.FlushPlaytimeOnce()
            Catch
            End Try
        Next
    End Sub

    '对单个进程的监视
    Public Class Watcher

        '初始化
        Public GameProcess As Process
        Public Instance As McInstance
        Private WindowTitle As String = ""
        Private PID As Integer
        Public Loader As LoaderTask(Of Process, Integer)
        Public Sub New(Loader As LoaderTask(Of Process, Integer), Instance As McInstance, WindowTitle As String)
            Me.Loader = Loader
            Me.Instance = Instance
            Me.WindowTitle = WindowTitle
            Me.PID = Loader.Input.Id
            WatcherLog("开始 Minecraft 日志监控")
            If Me.WindowTitle <> "" Then WatcherLog("要求窗口标题：" & WindowTitle)

            '先初始化进程与日志读取（先赋值 GameProcess 再加入全局列表，避免外部枚举空引用）
            GameProcess = Loader.Input
            AddHandler GameProcess.OutputDataReceived, AddressOf LogReceived
            AddHandler GameProcess.ErrorDataReceived, AddressOf LogReceived
            GameProcess.BeginOutputReadLine()
            GameProcess.BeginErrorReadLine()

            '更改列表
            SyncLock WatcherListLock
                Dim NewWatcherList As New List(Of Watcher)
                For Each Watch In McWatcherList
                    If Watch.State = MinecraftState.Crashed OrElse Watch.State = MinecraftState.Ended OrElse Watch.State = MinecraftState.Canceled Then Continue For
                    NewWatcherList.Add(Watch)
                Next
                NewWatcherList.Add(Me)
                McWatcherList = NewWatcherList
            End SyncLock
            WatcherStateChanged()

            If Settings.Get(Of Boolean)("LaunchGameLogWindow") Then
                WinGameLog.ShowOrActivate()
            End If

            '初始化时钟
            RunInNewThread(
            Sub()
                Try
                    Do Until State = MinecraftState.Ended OrElse State = MinecraftState.Canceled OrElse
                             (Loader.State = LoadState.Canceled AndAlso State <> MinecraftState.Crashed) OrElse
                             (State = MinecraftState.Crashed AndAlso GameProcess.HasExited)
                        TimerWindow()
                        TimerLog()
                        '设置窗口标题
                        For i = 1 To 3
                            If IsWindowFinished AndAlso IsWindowAppeared AndAlso WindowTitle <> "" AndAlso State = MinecraftState.Running AndAlso Not GameProcess.HasExited Then
                                Dim RealTitle As String = WindowTitle.Replace("{date}", Date.Now.ToString("yyyy'/'M'/'d")).Replace("{time}", Date.Now.ToString("HH':'mm':'ss"))
                                SetWindowText(WindowHandle, RealTitle)
                            End If
                            Thread.Sleep(64)
                        Next
                    Loop
                    If Loader.State = LoadState.Canceled AndAlso State = MinecraftState.Loading Then
                        State = MinecraftState.Canceled
                    End If
                    If State = MinecraftState.Crashed Then WatcherStateChanged()
                    WatcherLog("Minecraft 日志监控已退出")
                Catch ex As Exception
                    Logger.Error(ex, "Minecraft 日志监控主循环出错")
                    State = MinecraftState.Ended
                End Try
            End Sub, "Minecraft Watcher PID " & PID)
        End Sub

        '会话统计与退出收尾
        Public ReadOnly SessionStartUtc As DateTime = DateTime.UtcNow
        Public ReadOnly SessionStopwatch As Diagnostics.Stopwatch = Diagnostics.Stopwatch.StartNew()
        Private _SessionFinalized As Boolean = False
        Private _PlaytimeFlushed As Integer = 0

        '状态
        Private _State As MinecraftState = MinecraftState.Loading
        Public Property State As MinecraftState
            Get
                Return _State
            End Get
            Set(value As MinecraftState)
                If _State = value Then Return
                _State = value
                If value = MinecraftState.Ended OrElse value = MinecraftState.Crashed OrElse value = MinecraftState.Canceled Then
                    FinalizeSession(value)
                End If
                WatcherStateChanged()
            End Set
        End Property
        Public Enum MinecraftState
            Loading
            Running
            Crashed
            Ended
            Canceled
        End Enum

        '日志
        Public WaitingLog As New List(Of String)(1000)
        Private ReadOnly WaitingLogLock As New Object
        Private Sub LogReceived(sender As Object, e As DataReceivedEventArgs)
            SyncLock WaitingLogLock
                WaitingLog.Add(e.Data)
            End SyncLock
        End Sub
        Private Sub TimerLog()
            Try
                '输出文本
                Dim Copyed As New List(Of String)
                SyncLock WaitingLogLock
                    If Not WaitingLog.Any() Then Return
                    Copyed = WaitingLog
                    WaitingLog = New List(Of String)(1000)
                End SyncLock
                For Each Str As String In Copyed
                    GameLog(Str)
                Next
                If State = MinecraftState.Loading Then ProgressUpdate()
                '游戏退出检查
                If GameProcess.HasExited Then
                    WatcherLog("Minecraft 已退出，返回值：" & GameProcess.ExitCode)
                    'If Process.ExitCode = 1 Then
                    '    '返回值为 1，考虑是任务管理器结束
                    '    WatcherLog("Minecraft 返回值为 1，考虑为任务管理器结束") '并不，崩了照样是 1
                    '    State = MinecraftState.Ended
                    'Else
                    If State = MinecraftState.Loading Then
                        '窗口未出现
                        WatcherLog("Minecraft 尚未加载完成，可能已崩溃", LogLevel.Error)
                        Crashed()
                    ElseIf GameProcess.ExitCode <> 0 AndAlso State = MinecraftState.Running AndAlso Instance.ReleaseTime.Year >= 2012 Then
                        '返回值不为 0 且未结束
                        WatcherLog("Minecraft 返回值异常，可能已崩溃", LogLevel.Error)
                        Crashed()
                    ElseIf State <> MinecraftState.Crashed Then
                        '正常关闭
                        State = MinecraftState.Ended
                    End If
                End If
            Catch ex As Exception
                Logger.Error(ex, "输出 Minecraft 日志失败")
            End Try
        End Sub
        Public LatestLog As New ConcurrentQueue(Of String)
        Private Sub GameLog(Text As String)
            '预处理
            If Text Is Nothing Then Return
            Text = Text.ReplaceLineEndings(vbCrLf)
            'If Text.Contains("�����") Then Hint("检测到错误的日志编码：" & Text)
            '加入预存储
            LatestLog.Enqueue(Text)
            If LatestLog.Count >= 2001 Then LatestLog.TryDequeue(Nothing)
            RaiseEvent LiveLogAppended(PID, If(Instance?.Name, ""), Text)
            Logger.Trace(Text)
            '进度处理
            If LogProgress < 1 Then
                WatcherLog("日志 1/5：已出现日志输出")
                LogProgress = 1
            End If '可能第一句就是后面需要判断的 Log（重现：启动 1.15.2 原版）
            If LogProgress < 2 AndAlso Text.Contains("Setting user:") Then
                WatcherLog("日志 2/5：游戏用户已设置") '仅确保支持 Minecraft 1.7+
                LogProgress = 2
            ElseIf LogProgress < 3 AndAlso Text.ContainsIgnoreCase("lwjgl version") Then
                WatcherLog("日志 3/5：LWJGL 版本已确认")
                LogProgress = 3
            ElseIf LogProgress < 4 AndAlso (Text.Contains("OpenAL initialized") OrElse Text.Contains("Starting up SoundSystem")) Then
                WatcherLog("日志 4/5：OpenAL 已加载") '仅确保支持 Minecraft 1.7+
                LogProgress = 4
            ElseIf LogProgress < 5 AndAlso ((Text.Contains("Created") AndAlso Text.Contains("textures") AndAlso Text.Contains("-atlas")) OrElse Text.Contains("Found animation info")) Then
                WatcherLog("日志 5/5：材质已加载") '仅确保支持 Minecraft 1.7+
                LogProgress = 5
            End If
            '输出日志
            'Log(Text)
            '关闭与崩溃检测
            If Not Text.Contains("[CHAT]") Then
                If Text.Contains("Someone is closing me!") OrElse Text.Contains("Restarting Minecraft with command") Then '#1258
                    WatcherLog("识别为关闭的 Log：" & Text)
                    State = MinecraftState.Ended
                ElseIf Text.Contains("Crash report saved to") OrElse Text.Contains("This crash report has been saved to:") Then
                    ' Text.Contains("Minecraft ran into a problem! Report saved to:") Then
                    'Minecraft 崩溃，忽略 VanillaFix
                    WatcherLog("识别为崩溃的 Log：" & Text, LogLevel.Error)
                    Crashed()
                ElseIf Text.Contains("Could not save crash report to") Then
                    'Minecraft 崩溃，无法保存崩溃日志
                    WatcherLog("识别为崩溃的 Log：" & Text, LogLevel.Error)
                    Crashed()
                ElseIf Text.Contains("/ERROR]: Unable to launch") OrElse Text.Contains("An exception was thrown, the game will display an error screen and halt.") Then
                    'Forge 崩溃
                    WatcherLog("识别为崩溃的 Log：" & Text, LogLevel.Error)
                    Crashed()
                    'ElseIf Text.Contains("Shutdown failure!") Then
                    '    'Minecraft 强行崩溃，由于点 X 强行关闭也会触发这句话，所以不可用
                    '    Crashed(Nothing)
                End If
            End If
        End Sub
        Private Sub WatcherLog(Text As String, Optional Level As LogLevel = LogLevel.Info)
            McLaunchLog("[" & PID & "] " & Text, Level)
        End Sub

        '进度更新
        Private LogProgress As Integer = 0
        Private Sub ProgressUpdate()
            Dim CurrentProgress As Double
            If IsWindowAppeared OrElse LogProgress = 5 Then
                CurrentProgress = 0.95
                WatcherLog("Minecraft 加载已完成")
                State = MinecraftState.Running
            Else
                CurrentProgress = Math.Min(LogProgress, 3) / 3 * 0.9
            End If
            Loader.Progress = CurrentProgress
        End Sub

        '窗口检查
        Private IsWindowAppeared As Boolean = False
        ''' <summary>
        ''' 窗口检查是否已经完成。这不一定代表着找到了窗口（如果没有找到，IsWindowAppeared 仍为 False）。
        ''' </summary>
        Private IsWindowFinished As Boolean = False
        Public WindowHandle As IntPtr
        Private Sub TimerWindow()
            Try
                If GameProcess.HasExited Then Return
                If IsWindowFinished Then Return
                '获取全部窗口，检查是否有新增的
                Dim MinecraftWindow As KeyValuePair(Of IntPtr, String)? = Nothing
                Try
                    MinecraftWindow = TryGetMinecraftWindow()
                Catch ex As ComponentModel.Win32Exception
                    '拒绝访问（#1062）
                    Logger.Error(ex, "由于反作弊或安全软件拦截，PCL Linking 无法操作游戏窗口", LogBehavior.Toast)
                    IsWindowFinished = True
                End Try
                If MinecraftWindow Is Nothing Then Return
                Dim MinecraftWindowName = MinecraftWindow.Value.Value, MinecraftWindowHandle = MinecraftWindow.Value.Key
                '已找到窗口
                If Not MinecraftWindowName.StartsWithF("FML") AndAlso Not MinecraftWindowName.StartsWithF("Quilt Loader") Then
                    '已找到 Minecraft 窗口
                    WindowHandle = MinecraftWindowHandle
                    WatcherLog($"Minecraft 窗口已加载：{MinecraftWindowName}（{MinecraftWindowHandle.ToInt64}）")
                    IsWindowFinished = True
                    '最大化
                    If Settings.Get(Of Integer)("LaunchArgumentWindowType") = 4 Then
                        RunInNewThread(
                        Sub()
                            Try
                                '如果最大化导致屏幕渲染大小不对，那是 MC 的 Bug，不是我的 Bug
                                '……虽然我很想这样说，但总有人反馈，算了
                                Thread.Sleep(2000)
                                ShowWindow(WindowHandle, 3)
                                WatcherLog($"已最大化 Minecraft 窗口：{MinecraftWindowHandle.ToInt64}")
                            Catch ex As Exception
                                Logger.Warn(ex, "最大化 Minecraft 窗口时出现错误")
                            End Try
                        End Sub, "MinecraftWindowMaximize")
                    End If
                ElseIf Not IsWindowAppeared Then
                    '已找到 Mod Loader 窗口
                    WatcherLog("Mod Loader 窗口已加载：" & MinecraftWindowName & "（" & MinecraftWindowHandle.ToInt64 & "）")
                End If
                IsWindowAppeared = True
            Catch ex As Exception
                Logger.Error(ex, "检查 Minecraft 窗口失败")
            End Try
        End Sub
        Private Declare Function IsWindowVisible Lib "user32" (hWnd As IntPtr) As Boolean

        ''' <summary>
        ''' 获取可能是当前进程对应的 Minecraft 窗口的句柄和标题。
        ''' Nothing 代表未找到。
        ''' </summary>
        Private Function TryGetMinecraftWindow() As KeyValuePair(Of IntPtr, String)?
            Dim Result = Nothing
            EnumWindows(
                Sub(hwnd As IntPtr, lParam As Integer)
                    If Result IsNot Nothing Then Return
                    '检查类名
                    Dim str As New StringBuilder(512)
                    GetClassName(hwnd, str, str.Capacity)
                    Dim ClassName As String = str.ToString
                    If Not (ClassName = "GLFW30" OrElse ClassName = "SDL_app" OrElse ClassName = "LWJGL" OrElse ClassName = "SunAwtFrame") Then Return
                    'SDL 会先创建隐藏窗口，待初始化完成后才显示（#9073）
                    If ClassName = "SDL_app" AndAlso Not IsWindowVisible(hwnd) Then Return
                    '获取窗口标题名
                    str = New StringBuilder(512)
                    GetWindowText(hwnd, str, str.Capacity)
                    Dim WindowText As String = str.ToString
                    '有的 Mod 可以修改窗口标题，所以不能检测是否为 Minecraft 打头，这并不准确
                    '部分版本会搞个 GLFW message window，高版本还有个 Hidden Utility Window，都要反选
                    If String.IsNullOrWhiteSpace(WindowText) OrElse WindowText = "SDL_app" OrElse WindowText = "PopupMessageWindow" OrElse
                       WindowText.StartsWithF("GLFW") OrElse WindowText.EndsWithF("Hidden Utility Window") Then Return
                    '获取窗口关联的进程
                    Dim ProcessId As Integer
                    GetWindowThreadProcessId(hwnd, ProcessId)
                    Try
                        Using TargetProc = Process.GetProcessById(ProcessId)
                            If TargetProc.StartTime < GameProcess.StartTime Then Return '需要是此后启动的进程
                        End Using
                    Catch ex As Exception
                        Logger.Warn(ex, "枚举 Minecraft 窗口进程失败")
                        Return
                    End Try
                    '返回
                    Result = New KeyValuePair(Of IntPtr, String)(hwnd, WindowText)
                End Sub, 0)
            Return Result
        End Function
        Private Delegate Sub EnumWindowsSub(hwnd As IntPtr, lParam As Integer)
        Private Declare Function EnumWindows Lib "user32" (hWnd As EnumWindowsSub, lParam As Integer) As Boolean
        Private Declare Function GetClassName Lib "user32" Alias "GetClassNameA" (hWnd As IntPtr, str As StringBuilder, maxCount As Integer) As Integer
        Private Declare Function GetWindowText Lib "user32" Alias "GetWindowTextA" (hWnd As IntPtr, str As StringBuilder, maxCount As Integer) As Integer
        Private Declare Function SetWindowText Lib "user32" Alias "SetWindowTextA" (hWnd As IntPtr, str As String) As Boolean
        Private Declare Function ShowWindow Lib "user32" (hWnd As IntPtr, cmdWindow As UInteger) As Boolean
        Private Declare Function IsIconic Lib "user32" (hWnd As IntPtr) As Boolean
        Private Declare Function IsWindow Lib "user32" (hWnd As IntPtr) As Boolean
        Private Declare Function GetWindowThreadProcessId Lib "user32" (hWnd As IntPtr, ByRef lpdwProcessId As Integer) As Integer

        Public Sub FlushPlaytimeOnce()
            If Threading.Interlocked.Exchange(_PlaytimeFlushed, 1) = 1 Then Return
            Dim TargetInst = Instance
            If TargetInst Is Nothing Then Return
            Try
                Dim ElapsedSec = CLng(Math.Max(0, SessionStopwatch.Elapsed.TotalSeconds))
                ModPlaytime.RecordSession(TargetInst, ElapsedSec)
            Catch ex As Exception
                Logger.Warn(ex, "记录实例游玩时长失败")
            End Try
        End Sub

        Private Sub FinalizeSession(EndState As MinecraftState)
            If _SessionFinalized Then Return
            _SessionFinalized = True

            Win32BorderlessCore.ForgetWindow(WindowHandle)

            Dim TargetInst = Instance
            If TargetInst Is Nothing Then Return

            FlushPlaytimeOnce()

            AppEventBus.Publish(
                If(EndState = MinecraftState.Crashed, "game.crashed", "game.session.ended"),
                "ModWatcher",
                New Newtonsoft.Json.Linq.JObject From {
                    {"instance", TargetInst.Name},
                    {"state", EndState.ToString()}
                })

            Try
                If EndState = MinecraftState.Ended AndAlso Settings.Get(Of Boolean)("VersionAdvanceAutoBackupOnExit", Instance:=TargetInst) Then
                    Dim StartUtcSnapshot = SessionStartUtc
                    Dim BackupAction = Sub()
                                           Try
                                               If Not HasSavesChangedSinceStart(TargetInst, StartUtcSnapshot) Then
                                                   Logger.Info($"实例「{TargetInst.Name}」退出时未检测到 saves 目录变更，已跳过退出自动存档。")
                                                   Return
                                               End If
                                               Dim KeepCount = Math.Max(1, Settings.Get(Of Integer)("VersionAdvanceAutoBackupKeep", Instance:=TargetInst))
                                               Dim Snap = ModSnapshot.CreateSnapshotCore(
                                                   TargetInst,
                                                   $"[自动备份-退出] {DateTime.Now:MM-dd HH:mm}",
                                                   "游戏正常退出时检测到存档变更，自动备份 saves 目录",
                                                   {"saves"},
                                                   IsAutoBackup:=True)
                                               ModSnapshot.PruneAutoBackups(TargetInst, KeepCount)
                                               Hint($"已为「{TargetInst.Name}」创建退出自动存档：{Snap.Name}（{Snap.FileCount} 个文件）", HintType.Green)
                                           Catch ex2 As Exception
                                               Logger.Warn(ex2, "退出时自动存档快照失败")
                                           End Try
                                       End Sub
                    If Settings.Get(Of Integer)("LaunchArgumentVisible") = 2 Then
                        BackupAction()
                    Else
                        RunInNewThread(BackupAction, "ExitAutoBackup")
                    End If
                End If
                If EndState = MinecraftState.Ended AndAlso
                   Settings.Get(Of Integer)("LaunchArgumentVisible") <> 2 AndAlso
                   PageLinkMain.ConsumeRecentHostActivity() AndAlso
                   HasSavesChangedSinceStart(TargetInst, SessionStartUtc) Then
                    Dim FriendDataSnap = ModFriends.GetData()
                    If FriendDataSnap.Friends.Any() OrElse FriendDataSnap.Groups.Any() Then
                        Dim InstForRelay = TargetInst
                        RunInUi(
                            Sub()
                                If MyMsgBox($"检测到本次联机后实例「{InstForRelay.Name}」的存档有更新。{vbCrLf}是否立即选择变动存档加密上传给好友/群聊（轮值主机接力），方便队友下次随时接手开房？",
                                            "联机存档接力提示", "打包上传接力", "暂不上传") = 1 Then
                                    PageFriends.PromptAndSendWorldSaveRelay(InstForRelay)
                                End If
                            End Sub)
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, "触发退出自动存档检查失败")
            End Try
        End Sub

        Private Shared Function HasSavesChangedSinceStart(TargetInst As McInstance, StartUtc As DateTime) As Boolean
            Try
                Dim SavesDir = IO.Path.Combine(PathUtils.RemoveExtendedPrefix(TargetInst.PathIndie), "saves")
                If Not IO.Directory.Exists(SavesDir) Then Return False
                Dim Threshold = StartUtc.AddSeconds(-3)
                For Each FilePath In IO.Directory.EnumerateFiles(SavesDir, "*", IO.SearchOption.AllDirectories)
                    Try
                        If IO.File.GetLastWriteTimeUtc(FilePath) >= Threshold Then Return True
                    Catch
                    End Try
                Next
            Catch ex As Exception
                Logger.Warn(ex, "检查 saves 目录变更状态失败")
            End Try
            Return False
        End Function

        Private Shared Function FindActiveWatcher(ByRef OutHwnd As IntPtr) As Watcher
            OutHwnd = IntPtr.Zero
            For Each W In McWatcherList.ToArray()
                If W Is Nothing OrElse W.GameProcess Is Nothing Then Continue For
                Try
                    If W.GameProcess.HasExited Then Continue For
                Catch
                    Continue For
                End Try
                If W.State <> MinecraftState.Loading AndAlso W.State <> MinecraftState.Running Then Continue For
                Dim Candidate = W.WindowHandle
                If Candidate = IntPtr.Zero OrElse Not IsWindow(Candidate) Then
                    Try
                        W.GameProcess.Refresh()
                        Candidate = W.GameProcess.MainWindowHandle
                    Catch
                        Candidate = IntPtr.Zero
                    End Try
                End If
                If Candidate <> IntPtr.Zero AndAlso IsWindow(Candidate) Then W.WindowHandle = Candidate
                OutHwnd = Candidate
                Return W
            Next
            Return Nothing
        End Function

        Public Shared Function ToggleActiveMinecraftBorderlessFullscreen() As String
            Dim Hwnd As IntPtr = IntPtr.Zero
            Dim Active = FindActiveWatcher(Hwnd)
            If Active Is Nothing Then Return "当前没有正在运行中的游戏实例。"
            If Not Win32BorderlessCore.IsValidWindowHandle(Hwnd) Then Return "未找到游戏窗口句柄（请等待游戏窗口出现后再试）。"
            Dim InstName = If(Active.Instance?.Name, "Minecraft")
            Return Win32BorderlessCore.ToggleWindowBorderless(Hwnd, InstName)
        End Function

        Public Shared Function GetActiveMinecraftPerfSummary() As String
            Dim Hwnd As IntPtr = IntPtr.Zero
            Dim Active = FindActiveWatcher(Hwnd)
            If Active Is Nothing Then Return "当前无运行中的游戏实例"
            Try
                Active.GameProcess.Refresh()
                Dim RamBytes = Active.GameProcess.WorkingSet64
                Dim RamText As String = If(RamBytes >= 1073741824L,
                    $"{Math.Round(RamBytes / 1073741824.0, 2):0.00} GB",
                    $"{Math.Round(RamBytes / 1048576.0, 0):0} MB")
                Dim Elapsed = Active.SessionStopwatch.Elapsed
                If Elapsed < TimeSpan.Zero Then Elapsed = TimeSpan.Zero
                Dim TimeText = If(Elapsed.TotalHours >= 1, Elapsed.ToString("hh\:mm\:ss"), Elapsed.ToString("mm\:ss"))
                Dim IsBorderless As Boolean = Win32BorderlessCore.IsWindowBorderless(Hwnd)
                Dim InstName = If(Active.Instance?.Name, "Minecraft")
                Return $"实例：{InstName}  ｜  内存：{RamText}  ｜  本次：{TimeText}{If(IsBorderless, "  ｜  [无边框全屏]", "")}"
            Catch
                Return "正在运行中（读取进程状态中…）"
            End Try
        End Function

        '崩溃处理
        Private Sub Crashed()
            If State = MinecraftState.Crashed OrElse State = MinecraftState.Ended Then Return
            State = MinecraftState.Crashed
            '崩溃分析
            WatcherLog("Minecraft 已崩溃，将在 2 秒后开始崩溃分析", LogLevel.Error)
            Hint("检测到 Minecraft 出现错误，错误分析已开始……", HintType.Red)
            FeedbackInfo()
            RunInNewThread(
            Sub()
                Try
                    Thread.Sleep(2000)
                    WatcherLog("崩溃分析开始")
                    Dim Analyzer As New CrashAnalyzer(Instance)
                    Analyzer.Collect(Instance.PathIndie, LatestLog.ToList)
                    Analyzer.Prepare()
                    Analyzer.Analyze()
                    Analyzer.Output(False, New List(Of String) From
                        {Instance.PathVersion & Instance.Name & ".json", Paths.Base & "PCL Linking\Log1.txt", Paths.Base & "PCL Linking\LatestLaunch.bat"})
                Catch ex As Exception
                    Logger.Error(ex, "崩溃分析失败")
                End Try
            End Sub, "Crash Analyzer")
        End Sub

        '强制关闭
        Public Sub Kill()
            State = MinecraftState.Canceled
            WatcherLog("尝试强制结束 Minecraft 进程")
            Try
                If Not GameProcess.HasExited Then GameProcess.Kill()
                WatcherLog("已强制结束 Minecraft 进程")
            Catch ex As Exception
                Logger.Error(ex, "强制结束 Minecraft 进程失败", LogBehavior.Toast)
            End Try
        End Sub

    End Class

End Module
