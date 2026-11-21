Public Module ModLaunch

#Region "开始"

    Public CurrentLaunchOptions As McLaunchOptions = Nothing
    Public Class McLaunchOptions
        ''' <summary>
        ''' 强制指定在启动后进入的服务器 IP。
        ''' 默认值：Nothing。使用版本设置的值。
        ''' </summary>
        Public ServerIp As String = Nothing
        ''' <summary>
        ''' 强制指定在启动后直接进入的单人存档文件夹名（需 Minecraft 1.20+ 支持 QuickPlay）。
        ''' 默认值：Nothing。不直连单人存档。
        ''' </summary>
        Public SingleplayerWorld As String = Nothing
        ''' <summary>
        ''' 将启动脚本保存到该地址，然后取消启动。这同时会改变启动时的提示等。
        ''' 默认值：Nothing。不保存。
        ''' </summary>
        Public SaveBatch As String = Nothing
        ''' <summary>
        ''' 强行指定启动的 MC 版本。
        ''' 默认值：Nothing。使用 McInstanceCurrent。
        ''' </summary>
        Public Instance As McInstance = Nothing
        ''' <summary>
        ''' 额外的启动参数。
        ''' </summary>
        Public ExtraGameArgs As New List(Of String)
    End Class
    ''' <summary>
    ''' 尝试启动 Minecraft。必须在 UI 线程调用。
    ''' 返回是否实际开始了启动（如果没有，则一定弹出了错误提示）。
    ''' </summary>
    Public Function McLaunchStart(Optional Options As McLaunchOptions = Nothing) As Boolean
        CurrentLaunchOptions = If(Options, New McLaunchOptions)
        '预检查
        If Not RunInUi() Then Throw New Exception("McLaunchStart 必须在 UI 线程调用！")
        If McLaunchLoader.State = LoadState.Loading Then
            Hint("已有游戏正在启动中！", HintType.Red)
            Return False
        End If
        '强制切换需要启动的版本
        If CurrentLaunchOptions.Instance IsNot Nothing AndAlso McInstanceSelected <> CurrentLaunchOptions.Instance Then
            McLaunchLog("在启动前切换到版本 " & CurrentLaunchOptions.Instance.Name)
            '检查版本是否存在
            Try
                CurrentLaunchOptions.Instance.GetJsonPath()
            Catch
                Hint($"无法启动 {CurrentLaunchOptions.Instance.Name}：当前 Minecraft 文件夹中尚未安装该版本！", HintType.Red)
                Return False
            End Try
            '检查版本
            CurrentLaunchOptions.Instance.Load()
            If CurrentLaunchOptions.Instance.State = McInstance.McInstanceState.Error Then
                Hint($"无法启动 {CurrentLaunchOptions.Instance.Name}：{CurrentLaunchOptions.Instance.Info}", HintType.Red)
                Return False
            End If
            '切换版本
            McInstanceSelected = CurrentLaunchOptions.Instance
            FrmLaunchLeft.RefreshButtonsUI()
            FrmLaunchLeft.RefreshPage(False, False)
        End If
        If McInstanceSelected IsNot Nothing AndAlso McInstanceLock.IsLocked(McInstanceSelected) Then
            Hint($"版本 {McInstanceSelected.Name} 正在被修改，请稍后再启动！", HintType.Red)
            Return False
        End If
        FrmMain.AprilGiveup()
        '禁止进入版本选择页面（否则就可以在启动中切换 McInstanceSelected 了）
        FrmMain.PageStack = FrmMain.PageStack.Where(Function(p) p.Page <> FormMain.PageType.InstanceSelect).ToList
        '实际启动加载器
        McLaunchLoader.Start(Options, IsForceRestart:=True)
        Return True
    End Function

    ''' <summary>
    ''' 记录启动日志。
    ''' </summary>
    Public Sub McLaunchLog(Text As String, Optional Level As LogLevel = LogLevel.Info)
        Text = FilterUserName(FilterAccessToken(Text, "*"), "*")
        If ModeDebug Then
            RunInUi(
            Sub()
                FrmLaunchRight.LabLog.AppendText($"[{Date.Now:HH':'mm':'ss'.'fff}] {Text}{vbCrLf}")
            End Sub)
        End If
        Logger.Log(Text, Level)
    End Sub

    '启动状态切换
    Public McLaunchLoader As New LoaderTask(Of McLaunchOptions, Object)("Loader Launch", AddressOf McLaunchStart) With {.OnStateChanged = AddressOf McLaunchState}
    Public McLaunchLoaderReal As LoaderCombo(Of Object)
    Public McLaunchProcess As Process
    Public McLaunchWatcher As Watcher
    Private Sub McLaunchState(Loader As LoaderTask(Of McLaunchOptions, Object))
        Select Case McLaunchLoader.State
            Case LoadState.Finished, LoadState.Failed, LoadState.Waiting, LoadState.Canceled
                FrmLaunchLeft.PageChangeToLogin()
            Case LoadState.Loading
                '在预检测结束后再触发动画
                FrmLaunchRight.LabLog.Text = ""
        End Select
    End Sub
    ''' <summary>
    ''' 指定启动中断时的提示文本。若不为 Nothing 则会显示为绿色。
    ''' </summary>
    Private CanceledHint As String = Nothing

    '实际的启动方法
    Private Sub McLaunchStart(Loader As LoaderTask(Of McLaunchOptions, Object))
        '开始动画
        RunInUiWait(AddressOf FrmLaunchLeft.PageChangeToLaunching)
        '预检测（预检测的错误将直接抛出）
        Try
            McLaunchPrecheck()
            McLaunchLog("预检测已通过")
        Catch ex As Exception
            If Not ex.IsCanceled Then Hint(ex.Message, HintType.Red)
            Throw
        End Try
        Dim IsSavingBatch As Boolean = CurrentLaunchOptions?.SaveBatch IsNot Nothing

        '正式加载
        Try
            '构造主加载器
            Dim Loaders As New List(Of LoaderBase) From {
                New LoaderTask(Of Integer, Integer)("获取 Java", AddressOf McLaunchJava) With {.ProgressWeight = 4, .Block = False},
                McLoginLoader, '.ProgressWeight = 15, .Block = False
                New LoaderCombo(Of String)("补全文件", DlClientFix(McInstanceSelected, False, True)) With {.ProgressWeight = 15, .Show = False},
                New LoaderTask(Of String, Integer)("获取启动参数", AddressOf McLaunchArgumentMain) With {.ProgressWeight = 2},
                New LoaderTask(Of Integer, Integer)("解压文件", AddressOf McLaunchNatives) With {.ProgressWeight = 2},
                New LoaderTask(Of Integer, Integer)("预启动处理", AddressOf McLaunchPrerun) With {.ProgressWeight = 1},
                New LoaderTask(Of Integer, Integer)("执行自定义命令", AddressOf McLaunchCustom) With {.ProgressWeight = 1},
                New LoaderTask(Of Integer, Process)("启动进程", AddressOf McLaunchRun) With {.ProgressWeight = 2},
                New LoaderTask(Of Process, Integer)("等待游戏窗口出现", AddressOf McLaunchWait) With {.ProgressWeight = 1},
                New LoaderTask(Of Integer, Integer)("结束处理", AddressOf McLaunchEnd) With {.ProgressWeight = 1}
            }
            '内存优化
            Select Case Settings.Get(Of Integer)("VersionRamOptimize", Instance:=McInstanceSelected)
                Case 0 '全局
                    If Settings.Get(Of Boolean)("LaunchArgumentRam") Then '使用全局设置
                        CType(Loaders(2), LoaderCombo(Of String)).Block = False
                        Loaders.Insert(3, New LoaderTask(Of Integer, Integer)("内存优化", AddressOf McLaunchMemoryOptimize) With {.ProgressWeight = 30})
                    End If
                Case 1 '开启
                    CType(Loaders(2), LoaderCombo(Of String)).Block = False
                    Loaders.Insert(3, New LoaderTask(Of Integer, Integer)("内存优化", AddressOf McLaunchMemoryOptimize) With {.ProgressWeight = 30})
                Case 2 '关闭
            End Select
            If Settings.Get(Of McLoginType)("LoginType") = McLoginType.Auth Then
                Dim ArgIndex = Loaders.FindIndex(Function(l) l.Name = "获取启动参数")
                If ArgIndex >= 0 Then Loaders.Insert(ArgIndex, New LoaderTask(Of Integer, Integer)("准备外置登录组件", AddressOf McLaunchAuthlibInjector) With {.ProgressWeight = 3})
            End If
            '启动前自动检查并更新 Mod（功能 6）
            If Not IsSavingBatch AndAlso Settings.Get(Of Boolean)("VersionAdvanceAutoModUpdate", Instance:=McInstanceSelected) AndAlso
                Not Settings.Get(Of Boolean)("VersionAdvanceDisableModUpdate", Instance:=McInstanceSelected) AndAlso
                McInstanceSelected.Version.ModLoaders <> ModLoaders.None Then
                Dim ArgIndex = Loaders.FindIndex(Function(l) l.Name = "获取启动参数")
                If ArgIndex >= 0 Then Loaders.Insert(ArgIndex, New LoaderTask(Of Integer, Integer)("自动检查并更新 Mod", AddressOf McLaunchAutoUpdateMods) With {.ProgressWeight = 4, .Block = True})
            End If
            '启动前静态 Mod 冲突与依赖预检
            If Not IsSavingBatch AndAlso McInstanceSelected.Version.ModLoaders <> ModLoaders.None AndAlso
                Settings.Get(Of Boolean)("VersionAdvancePrelaunchModCheck", Instance:=McInstanceSelected) Then
                Dim ArgIndex = Loaders.FindIndex(Function(l) l.Name = "获取启动参数")
                If ArgIndex >= 0 Then Loaders.Insert(ArgIndex, New LoaderTask(Of Integer, Integer)("检查 Mod 冲突与依赖", AddressOf McLaunchStaticModPrecheck) With {.ProgressWeight = 2, .Block = True})
            End If
            '启动前自动检查并更新已知资源包/光影
            If Not IsSavingBatch AndAlso Settings.Get(Of Boolean)("VersionAdvanceAutoAssetUpdate", Instance:=McInstanceSelected) Then
                Dim ArgIndex = Loaders.FindIndex(Function(l) l.Name = "获取启动参数")
                If ArgIndex >= 0 Then Loaders.Insert(ArgIndex, New LoaderTask(Of Integer, Integer)("自动检查并更新材质与光影", AddressOf McLaunchAutoUpdateAssets) With {.ProgressWeight = 4, .Block = True})
            End If
            '启动前自动备份实例快照（功能 5）
            If Not IsSavingBatch AndAlso Settings.Get(Of Boolean)("VersionAdvanceAutoBackup", Instance:=McInstanceSelected) Then
                Dim RunIndex = Loaders.FindIndex(Function(l) l.Name = "启动进程")
                If RunIndex >= 0 Then Loaders.Insert(RunIndex, New LoaderTask(Of Integer, Integer)("启动前自动备份", AddressOf McLaunchAutoBackup) With {.ProgressWeight = 5, .Block = True})
            End If
            Dim LaunchLoader As New LoaderCombo(Of Object)("Minecraft 启动", Loaders) With {.Show = False}
            If McLoginLoader.State = LoadState.Finished Then McLoginLoader.State = LoadState.Waiting '要求重启登录主加载器，它会自行决定是否启动副加载器
            '等待加载器执行并更新 UI
            McLaunchLoaderReal = LaunchLoader
            CanceledHint = Nothing
            LaunchLoader.Start()
            '任务栏进度条
            LoaderTaskbarAdd(LaunchLoader)
            Do While LaunchLoader.State = LoadState.Loading
                FrmLaunchLeft.Dispatcher.Invoke(AddressOf FrmLaunchLeft.LaunchingRefresh)
                Thread.Sleep(100)
            Loop
            FrmLaunchLeft.Dispatcher.Invoke(AddressOf FrmLaunchLeft.LaunchingRefresh)
            '成功与失败处理
            Select Case LaunchLoader.State
                Case LoadState.Finished
                    Hint(McInstanceSelected.Name & " 启动成功！", HintType.Green)
                    '上报
                    If Not IsSavingBatch Then
                        Telemetry("Minecraft 启动成功",
                              "Version", McInstanceSelected.VersionDisplayName,
                              "LoginType", Settings.Get(Of McLoginType)("LoginType").ToString)
                    End If
                Case LoadState.Canceled
                    If CanceledHint Is Nothing Then
                        Hint(If(IsSavingBatch, "已取消导出启动脚本！", "已取消启动！"), HintType.Blue)
                    Else
                        Hint(CanceledHint, HintType.Green)
                    End If
                Case LoadState.Failed
                    Throw LaunchLoader.Error
                Case Else
                    Throw New Exception("错误的状态改变：" & LaunchLoader.State.ToString())
            End Select
        Catch ex As Exception
            ex.ThrowIfCanceled()
            Dim CurrentEx = ex
NextInner:
            If CurrentEx.Message.StartsWithF("$") Then
                '若有以 $ 开头的错误信息，则以此为准显示提示
                MyMsgBox(CurrentEx.Message.TrimStart("$"), If(IsSavingBatch, "导出启动脚本失败", "启动失败"))
                Throw
            ElseIf CurrentEx.InnerException IsNot Nothing Then
                '检查下一级错误
                CurrentEx = CurrentEx.InnerException
                GoTo NextInner
            Else
                '没有特殊处理过的错误信息
                McLaunchLog("错误：" & ex.GetDisplay(True))
                Logger.Error(ex, If(IsSavingBatch, "导出启动脚本失败", "Minecraft 启动失败"), LogBehavior.Alert)
                '上报
                If Not IsSavingBatch Then
                    Telemetry("Minecraft 启动失败",
                              "Version", McInstanceSelected.VersionDisplayName,
                              "LoginType", Settings.Get(Of McLoginType)("LoginType").ToString,
                              "Exception", FilterUserName(FilterAccessToken(ex.GetDisplay(True), "*"), "*"))
                End If
                Throw
            End If
        End Try
    End Sub

#End Region

#Region "内存优化"

    Private Sub McLaunchMemoryOptimize(Loader As LoaderTask(Of Integer, Integer))
        McLaunchLog("内存优化开始")
        Dim Finished As Boolean = False
        RunInNewThread(
        Sub()
            MemoryOptimize(False)
            Finished = True
        End Sub, "Launch Memory Optimize")
        Do While Not Finished AndAlso Not Loader.IsCanceled
            If Loader.Progress < 0.7 Then
                Loader.Progress += 0.007 '10s
            Else
                Loader.Progress += (0.95 - Loader.Progress) * 0.02 '最快 += 0.005
            End If
            Thread.Sleep(100)
        Loop
    End Sub

#End Region

#Region "预检测"

    Private Sub McLaunchPrecheck()
        If Settings.Get(Of Boolean)("SystemDebugDelay") Then Thread.Sleep(RandomInteger(100, 2000))
        '检查版本（必须先判空，再访问属性）
        If McInstanceSelected Is Nothing Then Throw New Exception("未选择 Minecraft 版本！")
        '检查路径
        If McInstanceSelected.PathIndie.Contains("!") OrElse McInstanceSelected.PathIndie.Contains(";") Then Throw New Exception("游戏路径中不可包含 ! 或 ;（" & McInstanceSelected.PathIndie & "）")
        If McInstanceSelected.PathVersion.Contains("!") OrElse McInstanceSelected.PathVersion.Contains(";") Then Throw New Exception("游戏路径中不可包含 ! 或 ;（" & McInstanceSelected.PathVersion & "）")
        McInstanceSelected.Load()
        If McInstanceSelected.State = McInstance.McInstanceState.Error Then Throw New Exception("Minecraft 存在问题：" & McInstanceSelected.Info)
        '检查输入信息
        Dim CheckResult As String = ""
        RunInUiWait(Sub() CheckResult = McLoginAble(McLoginInput()))
        If CheckResult <> "" Then Throw New ArgumentException(CheckResult)
        '求赞助
        If BuildType = BuildTypes.Release AndAlso CurrentLaunchOptions?.SaveBatch Is Nothing Then '保存脚本时不提示
            RunInNewThread(
            Sub()
                Select Case Settings.Get(Of Integer)("SystemLaunchCount")
                    Case 10, 20, 40, 60, 80, 100, 120, 150, 200, 250, 300, 350, 400, 500, 600, 700, 800, 900, 1000, 1200, 1400, 1600, 1800, 2000
                        If MyMsgBox("PCL Linking修改版 基于龙腾猫跃开源的 PCL 二次开发，已为你启动了 " & Settings.Get(Of Integer)("SystemLaunchCount") & " 次游戏啦！" & vbCrLf &
                                    "如果觉得 PCL 很好用，欢迎考虑赞助支持 PCL 原作者龙腾猫跃！" & vbCrLf &
                                    "如果没有原作者长期维护开源上游，就没有这些持续更新的基础功能……！",
                                    Settings.Get(Of Integer)("SystemLaunchCount") & " 次启动！", "赞助 PCL 原作者", "但是我拒绝") = 1 Then
                            OpenWebsite("https://meloong.com/afd/a/LTCat")
                        End If
                End Select
            End Sub, "Donate")
        End If
        '正版购买提示
        If CurrentLaunchOptions?.SaveBatch Is Nothing AndAlso '保存脚本时不提示
           Not Settings.Get(Of Boolean)("HintBuy") AndAlso Settings.Get(Of McLoginType)("LoginType") <> McLoginType.Ms Then
            If Globalization.CultureInfo.CurrentCulture.Name.StartsWithF("zh") OrElse Globalization.CultureInfo.CurrentUICulture.Name.StartsWithF("zh") Then '中文？
                RunInNewThread(
                Sub()
                    Select Case Settings.Get(Of Integer)("SystemLaunchCount")
                        Case 3, 8, 15, 30, 50, 70, 90, 110, 130, 180, 220, 280, 330, 380, 450, 550, 660, 750, 880, 950, 1100, 1300, 1500, 1700, 1900
                            If MyMsgBox("你已经启动了 " & Settings.Get(Of Integer)("SystemLaunchCount") & " 次 Minecraft 啦！" & vbCrLf &
                                "如果觉得 Minecraft 还不错，可以购买正版支持一下，毕竟开发游戏也真的很不容易……不要一直白嫖啦。" & vbCrLf & vbCrLf &
                                "在登录一次正版账号后，就不会再出现这个提示了！",
                                "考虑一下正版？", "支持正版游戏！", "下次一定") = 1 Then
                                OpenWebsite("https://www.xbox.com/zh-cn/games/store/minecraft-java-bedrock-edition-for-pc/9nxp44l49shj")
                            End If
                    End Select
                End Sub, "Buy Minecraft")
            ElseIf Settings.Get(Of McLoginType)("LoginType") = McLoginType.Legacy Then
                Select Case MyMsgBox("你必须先登录正版账号，才能进行离线登录！", "正版验证", "购买正版", "试玩", "返回",
                    Button1Action:=Sub() OpenWebsite("https://www.xbox.com/zh-cn/games/store/minecraft-java-bedrock-edition-for-pc/9nxp44l49shj"))
                    Case 2
                        Hint("游戏将以试玩模式启动！", HintType.Red)
                        CurrentLaunchOptions.ExtraGameArgs.Add("--demo")
                    Case 3
                        Throw New OperationCanceledException
                End Select
            End If
        End If
    End Sub

#End Region

#Region "主登录模块"

    '登录方式
    Public Enum McLoginType
        Legacy = 0
        Nide = 2
        Auth = 3
        Ms = 5
    End Enum

    '各个登录方式的对应数据
    Public MustInherit Class McLoginData
        ''' <summary>
        ''' 登录方式。
        ''' </summary>
        Public Type As McLoginType
        Public Overrides Function Equals(obj As Object) As Boolean
            Return obj IsNot Nothing AndAlso obj.GetHashCode() = GetHashCode()
        End Function
    End Class
    Public Class McLoginLegacy
        Inherits McLoginData
        ''' <summary>
        ''' 登录用户名。
        ''' </summary>
        Public UserName As String
        ''' <summary>
        ''' 皮肤种类。
        ''' </summary>
        Public SkinType As Integer
        ''' <summary>
        ''' 若采用正版皮肤，则为该皮肤名。
        ''' </summary>
        Public SkinName As String

        Public Sub New()
            Type = McLoginType.Legacy
        End Sub
        Public Overrides Function GetHashCode() As Integer
            Return (UserName & SkinType & SkinName & Type).GetStableHashCode() Mod Integer.MaxValue
        End Function
    End Class
    Public Class McLoginServer
        Inherits McLoginData
        ''' <summary>
        ''' 用户名或邮箱。
        ''' </summary>
        Public UserName As String
        ''' <summary>
        ''' 登录密码。
        ''' </summary>
        Public Password As String
        ''' <summary>
        ''' Yggdrasil 验证服务器地址（以 /authserver 结尾）。
        ''' </summary>
        Public BaseUrl As String
        ''' <summary>
        ''' 登录方式标识。
        ''' </summary>
        Public Token As String
        ''' <summary>
        ''' 登录方式描述。
        ''' </summary>
        Public Description As String
        ''' <summary>
        ''' 是否强制弹出角色选择框。
        ''' </summary>
        Public ForceReselectProfile As Boolean = False

        Public Sub New(Type As McLoginType)
            Me.Type = Type
        End Sub
        Public Overrides Function GetHashCode() As Integer
            Return (If(UserName, "") & If(Password, "") & If(BaseUrl, "") & If(Token, "") & Type & ForceReselectProfile).GetStableHashCode() Mod Integer.MaxValue
        End Function
    End Class

    '登录返回结果
    Public Structure McLoginResult
        Public Name As String
        Public Uuid As String
        Public AccessToken As String
        Public Type As String
        Public ClientToken As String
        ''' <summary>
        ''' 进行微软登录时返回的 profile 信息。
        ''' </summary>
        Public ProfileJson As String
    End Structure

    ''' <summary>
    ''' 当前是否可以进行登录。若不可以则会返回错误原因。
    ''' </summary>
    Public Function McLoginAble() As String
        If Settings.Get(Of McLoginType)("LoginType") = McLoginType.Auth Then
            Return McLoginAble(McLoginInput())
        End If
        Return FrmLoginLegacy.IsVaild()
    End Function
    ''' <summary>
    ''' 登录输入是否可以进行登录。若不可以则会返回错误原因。
    ''' </summary>
    Public Function McLoginAble(LoginData As McLoginData) As String
        If LoginData Is Nothing Then Return "未知的登录方式"
        If LoginData.Type = McLoginType.Legacy Then Return PageLoginLegacy.IsVaild(LoginData)
        If LoginData.Type = McLoginType.Auth Then Return PageLoginAuth.IsVaild(CType(LoginData, McLoginServer))
        Return "未知的登录方式"
    End Function

    '登录主模块加载器
    Public McLoginLoader As New LoaderTask(Of McLoginData, McLoginResult)("登录", AddressOf McLoginStart, AddressOf McLoginInput, ThreadPriority.BelowNormal) With
        {.ReloadTimeout = 1, .ProgressWeight = 15, .Block = False}
    Public Function McLoginInput() As McLoginData
        Dim LoginData As McLoginData = Nothing
        Dim LoginType = Settings.Get(Of McLoginType)("LoginType")
        Try
            Select Case LoginType
                Case McLoginType.Legacy
                    LoginData = PageLoginLegacy.GetLoginData()
                Case McLoginType.Auth
                    If Settings.Get(Of String)("CacheAuthAccess") <> "" AndAlso Settings.Get(Of String)("CacheAuthUuid") <> "" AndAlso FrmLoginAuthSkin IsNot Nothing Then
                        LoginData = PageLoginAuthSkin.GetLoginData()
                    Else
                        LoginData = PageLoginAuth.GetLoginData()
                    End If
            End Select
            Return LoginData
        Catch ex As Exception
            Throw New Exception($"获取登录输入信息失败（{LoginType}）", ex)
        End Try
    End Function
    Private Sub McLoginStart(Data As LoaderTask(Of McLoginData, McLoginResult))
        McLaunchLog("登录加载已开始")
        '校验登录信息
        Dim CheckResult As String = McLoginAble(Data.Input)
        If Not CheckResult = "" Then Throw New ArgumentException(CheckResult)
        '获取对应加载器
        Dim Loader As LoaderBase
        Select Case Data.Input.Type
            Case McLoginType.Legacy
                Loader = McLoginLegacyLoader
            Case McLoginType.Auth
                Loader = McLoginAuthLoader
            Case Else
                Throw New ArgumentException($"不支持的登录方式：{Data.Input.Type}")
        End Select
        '尝试加载
        Loader.WaitForExit(Data.Input, McLoginLoader, Data.IsForceRestarting)
        Data.Output = CType(Loader, Object).Output
        RunInUi(Sub() FrmLaunchLeft.RefreshPage(True, False)) '刷新自动填充列表
        McLaunchLog("登录加载已结束")
    End Sub

#End Region
#Region "分方式登录模块"

    '各个登录方式的主对象与输入构造
    Public McLoginLegacyLoader As New LoaderTask(Of McLoginLegacy, McLoginResult)("Loader Login Legacy", AddressOf McLoginLegacyStart)
    Public McLoginAuthLoader As New LoaderTask(Of McLoginServer, McLoginResult)("Loader Login Auth", AddressOf McLoginAuthStart)

    Private Sub McLoginAuthStart(Data As LoaderTask(Of McLoginServer, McLoginResult))
        Dim Input As McLoginServer = Data.Input
        Dim RawServer As String = If(Input.BaseUrl, "").BeforeLast("/authserver").Trim().TrimEnd("/"c)
        Dim BaseAuthUrl As String = RawServer & "/authserver"
        McLaunchLog($"登录方式：外置登录（{RawServer}，{Input.UserName}）")
        Data.Progress = 0.1

        Dim AccessToken As String = Settings.Get(Of String)("CacheAuthAccess").Trim()
        Dim ClientToken As String = Settings.Get(Of String)("CacheAuthClient").Trim()
        If String.IsNullOrEmpty(ClientToken) Then ClientToken = GetUuid()
        Dim CachedUuid As String = Settings.Get(Of String)("CacheAuthUuid").Trim()
        Dim CachedName As String = Settings.Get(Of String)("CacheAuthName").Trim()
        Dim CachedServer As String = Settings.Get(Of String)("CacheAuthServerServer").Trim().TrimEnd("/"c)
        Dim CachedUsername As String = Settings.Get(Of String)("CacheAuthUsername").Trim()
        Dim CachedPass As String = Settings.Get(Of String)("CacheAuthPass").Trim()

        Dim CanTryCachedSession As Boolean =
            Not Input.ForceReselectProfile AndAlso
            AccessToken <> "" AndAlso CachedUuid <> "" AndAlso CachedName <> "" AndAlso
            String.Equals(CachedServer, RawServer, StringComparison.OrdinalIgnoreCase) AndAlso
            String.Equals(CachedUsername, Input.UserName, StringComparison.OrdinalIgnoreCase) AndAlso
            (String.IsNullOrEmpty(Input.Password) OrElse CachedPass = Input.Password)

        If CanTryCachedSession Then
            Data.Progress = 0.35
            '优先验证现有 Token（/validate 返回 204 即有效）
            Try
                Dim ValidatePayload As String = (New JObject From {
                    {"accessToken", AccessToken},
                    {"clientToken", ClientToken}
                }).ToString(Newtonsoft.Json.Formatting.None)
                NetRequestByClient(BaseAuthUrl & "/validate", Method:=HttpMethod.Post, Content:=ValidatePayload, ContentType:="application/json", Timeout:=15000)
                Data.Output = New McLoginResult With {
                    .Name = CachedName,
                    .Uuid = CachedUuid,
                    .AccessToken = AccessToken,
                    .ClientToken = ClientToken,
                    .Type = "Auth"
                }
                McLaunchLog($"外置登录令牌验证成功：{CachedName}")
                Return
            Catch ex As Exception
                ex.ThrowIfCanceled()
                Logger.Info($"外置登录令牌验证未通过，尝试刷新（{ex.Message}）")
            End Try

            '验证失败时尝试刷新 Token（/refresh）
            Try
                Dim RefreshJson As New JObject From {
                    {"accessToken", AccessToken},
                    {"clientToken", ClientToken},
                    {"requestUser", False}
                }
                Dim RefreshResp As String = NetRequestByClient(BaseAuthUrl & "/refresh", Method:=HttpMethod.Post, Content:=RefreshJson.ToString(Newtonsoft.Json.Formatting.None), ContentType:="application/json", RequireJson:=True, Timeout:=15000)
                Dim RefreshObj As JObject = RefreshResp.DeserializeJson()
                Dim NewAccess As String = If(RefreshObj("accessToken")?.ToString(), "")
                Dim NewClient As String = If(RefreshObj("clientToken")?.ToString(), ClientToken)
                Dim SelectedObj As JObject = TryCast(RefreshObj("selectedProfile"), JObject)
                Dim FinalUuid As String = If(SelectedObj?("id")?.ToString(), CachedUuid)
                Dim FinalName As String = If(SelectedObj?("name")?.ToString(), CachedName)
                If NewAccess <> "" AndAlso FinalUuid <> "" AndAlso FinalName <> "" Then
                    Settings.Set("CacheAuthAccess", NewAccess)
                    Settings.Set("CacheAuthClient", NewClient)
                    Settings.Set("CacheAuthUuid", FinalUuid)
                    Settings.Set("CacheAuthName", FinalName)
                    Data.Output = New McLoginResult With {
                        .Name = FinalName,
                        .Uuid = FinalUuid,
                        .AccessToken = NewAccess,
                        .ClientToken = NewClient,
                        .Type = "Auth"
                    }
                    McLaunchLog($"外置登录令牌刷新成功：{FinalName}")
                    Return
                End If
            Catch ex As Exception
                ex.ThrowIfCanceled()
                Logger.Info($"外置登录刷新失败，将重新登录：{ex.Message}")
            End Try
        End If

        '执行完整用户名密码登录（/authenticate）
        If Input.Password = "" Then Throw New ArgumentException("密码不能为空，请重新登录！")
        Data.Progress = 0.6
        Dim AuthPayload As String = (New JObject From {
            {"agent", New JObject From {{"name", "Minecraft"}, {"version", 1}}},
            {"username", Input.UserName},
            {"password", Input.Password},
            {"clientToken", ClientToken},
            {"requestUser", False}
        }).ToString(Newtonsoft.Json.Formatting.None)

        Dim AuthResp As String
        Try
            AuthResp = NetRequestByClient(BaseAuthUrl & "/authenticate", Method:=HttpMethod.Post, Content:=AuthPayload, ContentType:="application/json", RequireJson:=True, Timeout:=20000)
        Catch ex As HttpRequestCodeException
            Dim ServerErr As String = ExtractYggdrasilError(ex.Response)
            Throw New Exception(If(ServerErr <> "", $"外置登录失败：{ServerErr}", $"外置登录失败（HTTP {CInt(ex.StatusCode)}）"), ex)
        End Try

        Dim AuthJson As JObject = AuthResp.DeserializeJson()
        Dim FinalAccess As String = If(AuthJson("accessToken")?.ToString(), "")
        Dim FinalClient As String = If(AuthJson("clientToken")?.ToString(), ClientToken)
        If FinalAccess = "" Then Throw New Exception("外置登录服务器未返回有效的 accessToken！")

        Dim AvailableProfiles As JArray = TryCast(AuthJson("availableProfiles"), JArray)
        Dim SelectedProfile As JObject = TryCast(AuthJson("selectedProfile"), JObject)
        If AvailableProfiles Is Nothing OrElse AvailableProfiles.Count = 0 Then
            If SelectedProfile Is Nothing Then Throw New Exception("该账号在皮肤站中还没有创建角色，请先前往皮肤站网页创建角色！")
            AvailableProfiles = New JArray From {SelectedProfile}
        End If

        Dim ChosenProfile As JObject = Nothing
        If AvailableProfiles.Count = 1 Then
            ChosenProfile = CType(AvailableProfiles(0), JObject)
        ElseIf Not Input.ForceReselectProfile AndAlso SelectedProfile IsNot Nothing AndAlso SelectedProfile("id") IsNot Nothing Then
            ChosenProfile = SelectedProfile
        Else
            Dim MatchingCached = If(Not Input.ForceReselectProfile AndAlso CachedUuid <> "",
                AvailableProfiles.OfType(Of JObject)().FirstOrDefault(Function(p) String.Equals(p("id")?.ToString(), CachedUuid, StringComparison.OrdinalIgnoreCase)),
                Nothing)
            If MatchingCached IsNot Nothing Then
                ChosenProfile = MatchingCached
            Else
                Dim SelectionIndex As Integer? = Nothing
                RunInUiWait(
                Sub()
                    Dim Choices As New List(Of IMyRadio)
                    For i = 0 To AvailableProfiles.Count - 1
                        Dim Prof = CType(AvailableProfiles(i), JObject)
                        Choices.Add(New MyRadioBox With {
                            .Text = If(Prof("name")?.ToString(), $"角色 {i + 1}"),
                            .Checked = (i = 0)
                        })
                    Next
                    SelectionIndex = MyMsgBoxSelect(Choices, "选择角色", "确定", "取消")
                End Sub)
                If Not SelectionIndex.HasValue OrElse SelectionIndex.Value < 0 OrElse SelectionIndex.Value >= AvailableProfiles.Count Then
                    Throw New OperationCanceledException("已取消选择角色")
                End If
                ChosenProfile = CType(AvailableProfiles(SelectionIndex.Value), JObject)
            End If
        End If

        Dim ProfileUuid As String = If(ChosenProfile("id")?.ToString(), "")
        Dim ProfileName As String = If(ChosenProfile("name")?.ToString(), "")
        If ProfileUuid = "" OrElse ProfileName = "" Then Throw New Exception("外置登录服务器返回的角色信息不完整！")

        '若返回的多角色在服务端尚未绑定 selectedProfile 且手动选了与 default 不同的角色，则刷新以绑定所选角色
        If SelectedProfile Is Nothing OrElse Not String.Equals(SelectedProfile("id")?.ToString(), ProfileUuid, StringComparison.OrdinalIgnoreCase) Then
            Try
                Dim BindPayload As String = (New JObject From {
                    {"accessToken", FinalAccess},
                    {"clientToken", FinalClient},
                    {"selectedProfile", New JObject From {{"id", ProfileUuid}, {"name", ProfileName}}},
                    {"requestUser", False}
                }).ToString(Newtonsoft.Json.Formatting.None)
                Dim BindResp = NetRequestByClient(BaseAuthUrl & "/refresh", Method:=HttpMethod.Post, Content:=BindPayload, ContentType:="application/json", RequireJson:=True, Timeout:=15000)
                Dim BindObj = BindResp.DeserializeJson()
                If BindObj("accessToken") IsNot Nothing Then FinalAccess = BindObj("accessToken").ToString()
                If BindObj("clientToken") IsNot Nothing Then FinalClient = BindObj("clientToken").ToString()
            Catch ex As Exception
                Logger.Warn(ex, "绑定外置登录所选角色时刷新失败，继续使用当前令牌")
            End Try
        End If

        '写入登录缓存与账号历史
        Settings.Set("CacheAuthAccess", FinalAccess)
        Settings.Set("CacheAuthClient", FinalClient)
        Settings.Set("CacheAuthUuid", ProfileUuid)
        Settings.Set("CacheAuthName", ProfileName)
        Settings.Set("CacheAuthUsername", Input.UserName)
        Settings.Set("CacheAuthPass", Input.Password)
        Settings.Set("CacheAuthServerServer", RawServer)
        If McInstanceSelected IsNot Nothing Then
            Settings.Set("CacheAuthServerName", Settings.Get(Of String)("VersionServerAuthName", Instance:=McInstanceSelected))
            Settings.Set("CacheAuthServerRegister", Settings.Get(Of String)("VersionServerAuthRegister", Instance:=McInstanceSelected))
        End If
        SaveAuthLoginCredentials(Input.UserName, Input.Password, Settings.Get(Of Boolean)("LoginRemember"))

        Data.Output = New McLoginResult With {
            .Name = ProfileName,
            .Uuid = ProfileUuid,
            .AccessToken = FinalAccess,
            .ClientToken = FinalClient,
            .Type = "Auth"
        }
        McLaunchLog($"外置登录成功：{ProfileName}（{ProfileUuid}）")
    End Sub

    Public Function ExtractYggdrasilError(ResponseBody As String) As String
        If String.IsNullOrWhiteSpace(ResponseBody) Then Return ""
        Try
            Dim Obj = ResponseBody.DeserializeJson()
            Dim Msg As String = If(Obj("errorMessage")?.ToString(), If(Obj("error")?.ToString(), ""))
            Return Msg.Trim()
        Catch
            Return ""
        End Try
    End Function

    Public Sub SaveAuthLoginCredentials(Email As String, Password As String, RememberPassword As Boolean)
        Dim Emails As New List(Of String)
        Dim Passes As New List(Of String)
        Dim RawEmails As String = Settings.Get(Of String)("LoginAuthEmail").ToString
        Dim RawPasses As String = Settings.Get(Of String)("LoginAuthPass").ToString
        If Not String.IsNullOrEmpty(RawEmails) Then Emails.AddRange(RawEmails.Split("¨"c))
        If Not String.IsNullOrEmpty(RawPasses) Then Passes.AddRange(RawPasses.Split("¨"c))
        Do While Passes.Count < Emails.Count
            Passes.Add("")
        Loop
        Dim ExistingIdx As Integer = Emails.IndexOf(Email)
        If ExistingIdx >= 0 Then
            Emails.RemoveAt(ExistingIdx)
            If ExistingIdx < Passes.Count Then Passes.RemoveAt(ExistingIdx)
        End If
        Emails.Insert(0, Email)
        Passes.Insert(0, If(RememberPassword, Password, ""))
        Settings.Set("LoginAuthEmail", Emails.Join("¨"c))
        Settings.Set("LoginAuthPass", Passes.Join("¨"c))
    End Sub

    Private Sub McLaunchAuthlibInjector(Loader As LoaderTask(Of Integer, Integer))
        Dim JarPath As String = PathPure.Value & "authlib-injector.jar"
        McLaunchLog("检查外置登录组件：" & JarPath)
        Try
            Dim OfficialMeta As String = "https://authlib-injector.yushi.moe/artifact/latest.json"
            Dim MirrorMeta As String = "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json"
            Dim MetaUrls = DlVersionListOrder({OfficialMeta}, {MirrorMeta})
            Dim MetaJson As JObject = Nothing
            Dim LastMetaEx As Exception = Nothing
            For Each MetaUrl In MetaUrls
                Try
                    Dim Raw = NetRequestByClient(MetaUrl, RequireJson:=True, Timeout:=15000)
                    MetaJson = Raw.DeserializeJson()
                    Exit For
                Catch ex As Exception
                    ex.ThrowIfCanceled()
                    LastMetaEx = ex
                    Logger.Warn(ex, $"获取 authlib-injector 元数据失败：{MetaUrl}")
                End Try
            Next
            If MetaJson Is Nothing Then Throw If(LastMetaEx, New Exception("无法获取 authlib-injector 版本信息"))

            Dim DownloadUrl As String = If(MetaJson("download_url")?.ToString(), "")
            Dim ExpectedSha256 As String = If(MetaJson("checksums")?("sha256")?.ToString(), "")
            If DownloadUrl = "" Then Throw New Exception("authlib-injector 元数据中缺少下载地址")

            Dim Checker As New FileChecker With {.MinSize = 1024, .Hash = ExpectedSha256}
            If FileUtils.Exists(JarPath) AndAlso String.IsNullOrEmpty(Checker.Check(JarPath)) Then
                McLaunchLog("authlib-injector.jar 校验通过，无需重复下载")
                Return
            End If

            Dim MirrorDownloadUrl As String = DownloadUrl.Replace("https://authlib-injector.yushi.moe/", "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/")
            Dim JarUrls = DlSourceOrder({DownloadUrl}, {MirrorDownloadUrl})
            McLaunchLog("正在下载 authlib-injector.jar：" & JarUrls.FirstOrDefault())
            NetDownloadByLoader(JarUrls, JarPath, Loader, Checker)
            McLaunchLog("authlib-injector.jar 下载并校验完成")
        Catch ex As Exception
            ex.ThrowIfCanceled()
            If FileUtils.Exists(JarPath) AndAlso (New FileInfo(JarPath)).Length > 1024 Then
                McLaunchLog("更新 authlib-injector.jar 失败，将继续使用本地已有组件：" & ex.Message, LogLevel.Warn)
            Else
                Throw New Exception("无法准备外置登录组件 authlib-injector.jar，请检查网络连接！", ex)
            End If
        End Try
    End Sub

    '主加载函数，返回所有需要的登录信息
    Private Sub McLoginLegacyStart(Data As LoaderTask(Of McLoginLegacy, McLoginResult))
        Dim Input As McLoginLegacy = Data.Input
        McLaunchLog("登录方式：离线（" & Input.UserName & "）")
        Data.Progress = 0.1
        With Data.Output
            .Name = Input.UserName
            .Uuid = McLoginLegacyUuidWithCustomSkin(Input.UserName, Input.SkinType, Input.SkinName)
            .Type = "Legacy"
        End With
        '将结果扩展到所有项目中
        Data.Output.AccessToken = Data.Output.Uuid
        Data.Output.ClientToken = Data.Output.Uuid
        '保存启动记录
        Dim Names As New List(Of String)
        If Not Settings.Get(Of String)("LoginLegacyName") = "" Then Names.AddRange(Settings.Get(Of String)("LoginLegacyName").ToString.Split("¨"))
        Names.Remove(Input.UserName)
        Names.Insert(0, Input.UserName)
        Settings.Set("LoginLegacyName", Names.Join("¨"c))
    End Sub

    '返回符合离线皮肤设置的 UUID
    Private Function McLoginLegacyUuidWithCustomSkin(UserName As String, SkinType As Integer, SkinName As String) As String
        Dim Uuid As String = McLoginLegacyUuid(UserName)
        '根据离线皮肤获取实际使用的 Uuid
        Select Case SkinType
            Case 0
                '默认，不需要处理
            Case 1
                'Steve
                Do Until McSkinSex(Uuid) = "Steve"
                    If Uuid.EndsWithF("FFFFF") Then Uuid = Uuid.Substring(0, 27) & "00000"
                    Uuid = Uuid.Substring(0, 27) & (Long.Parse(Uuid.Substring(27), Globalization.NumberStyles.AllowHexSpecifier) + 1).ToString("X").PadLeft(5, "0")
                Loop
            Case 2
                'Alex
                Do Until McSkinSex(Uuid) = "Alex"
                    If Uuid.EndsWithF("FFFFF") Then Uuid = Uuid.Substring(0, 27) & "00000"
                    Uuid = Uuid.Substring(0, 27) & (Long.Parse(Uuid.Substring(27), Globalization.NumberStyles.AllowHexSpecifier) + 1).ToString("X").PadLeft(5, "0")
                Loop
            Case 3
                '使用正版用户名
                Try
                    If SkinName <> "" AndAlso
                        McInstanceSelected IsNot Nothing AndAlso McInstanceSelected.Version.Vanilla.Major < 20 Then '1.20+ 或快照版不能使用该项（#3746）
                        Logger.Info($"由于离线皮肤设置，使用正版 UUID：{SkinName}")
                        Uuid = McLoginMojangUuid(SkinName, False)
                    End If
                Catch ex As Exception
                    Logger.Error(ex, "皮肤信息获取失败，游戏将以无皮肤的方式启动", LogBehavior.Toast)
                End Try
            Case 4
                '自定义
                Do Until McSkinSex(Uuid) = If(Settings.Get(Of Boolean)("LaunchSkinSlim"), "Alex", "Steve")
                    If Uuid.EndsWithF("FFFFF") Then Uuid = Uuid.Substring(0, 27) & "00000"
                    Uuid = Uuid.Substring(0, 27) & (Long.Parse(Uuid.Substring(27), Globalization.NumberStyles.AllowHexSpecifier) + 1).ToString("X").PadLeft(5, "0")
                Loop
        End Select
        Return Uuid
    End Function
    '根据用户名返回对应 UUID，需要多线程
    Public Function McLoginMojangUuid(Name As String, ThrowOnNotFound As Boolean)
        If Name.Trim.Length = 0 Then Return New String("0"c, 32)
        '从缓存获取
        Dim Uuid As String = ReadIni(PathTemp & "Cache\Uuid\Mojang.ini", Name, "")
        If Len(Uuid) = 32 Then Return Uuid
        '从官网获取
        Try
            Dim GotJson As JObject = NetRequestByClientRetry("https://api.mojang.com/users/profiles/minecraft/" & Name, RequireJson:=True).DeserializeJson()
            If GotJson Is Nothing Then Throw New FileNotFoundException("正版玩家档案不存在（" & Name & "）")
            Uuid = If(GotJson("id"), "")
        Catch ex As Exception
            Logger.Warn(ex, $"从官网获取正版 Uuid 失败（{Name}）")
            If Not ThrowOnNotFound AndAlso ex.GetType.Name = "FileNotFoundException" Then
                Uuid = McLoginLegacyUuid(Name) '玩家档案不存在
            Else
                Throw New Exception("从官网获取正版 Uuid 失败", ex)
            End If
        End Try
        '写入缓存
        If Not Len(Uuid) = 32 Then Throw New Exception("获取的正版 Uuid 长度不足（" & Uuid & "）")
        WriteIni(PathTemp & "Cache\Uuid\Mojang.ini", Name, Uuid)
        Return Uuid
    End Function
    Public Function McLoginLegacyUuid(Name As String)
        Dim FullUuid As String = Name.Length.ToString("X").EnsureLength("0", 16) & Name.GetStableHashCode().ToString("X").EnsureLength("0", 16)
        Return FullUuid.Substring(0, 12) & "3" & FullUuid.Substring(13, 3) & "9" & FullUuid.Substring(17, 15)
    End Function

#End Region

#Region "Java 处理"

    Public McLaunchJavaSelected As Java = Nothing
    Private Sub McLaunchJava(Task As LoaderTask(Of Integer, Integer))
        McLaunchJavaSelected = SelectOrDownloadJava(McInstanceSelected, True, Task.CreateCancellationToken, Task.CreateSyncProgressProvider)
        If Task.IsCanceled Then Return
        If McLaunchJavaSelected Is Nothing Then Throw New OperationCanceledException 'SelectOrDownloadJava 已经会给予适当的提示
        If ModeDebug Then
            McLaunchLog($"预期 Java 版本范围：{GetJavaRequirement(McInstanceSelected).Range}")
            McLaunchLog($"Java 版本设置类别：{Settings.Get(Of Integer)("VersionArgumentJavaV2", McInstanceSelected)}")
        End If
        McLaunchLog("选择的 Java：" & McLaunchJavaSelected.ToString)
    End Sub

#End Region

#Region "启动参数"
    Private McLaunchArgument As String
    Private McLaunchEncoding As Encoding

    '主方法
    Private Sub McLaunchArgumentMain(Loader As LoaderTask(Of String, Integer))
        '获取基础参数
        Dim Arguments As New List(Of String)
        McLaunchLog("参数获取开始")
        Arguments.AddRange(McLaunchArgumentsJVM()) 'JVM 参数
        Arguments.Add(McInstanceSelected.JsonObject("mainClass")) 'mainClass
        Arguments.AddRange(McLaunchArgumentsGame()) '游戏参数
        '替换
        McLaunchLog("=================== 启动参数开始 ===================")
        Dim Replacements = McLaunchArgumentsReplace(Arguments).ToList
        Dim Argument As String = ""
        For Each Arg In Arguments
            '应用替换标记
            For Each Replacement In Replacements
                Arg = Arg.Replace(Replacement.Key, Replacement.Value)
            Next
            '为带特殊字符的参数添加双引号
            If Arg.IndexOfAny({"&"c, "|"c, "<"c, ">"c, "^"c, " "c}) >= 0 AndAlso Not Arg.StartsWithF("""") AndAlso Not Arg.EndsWithF("""") Then
                Arg = $"""{Arg.Replace("""", "\""")}""" '转义在原本参数中间的双引号
            End If
            '输出
            McLaunchLog(Arg)
            Argument &= Arg & " "
        Next
        McLaunchLog("=================== 启动参数结束 ===================")
        '输出
        McLaunchArgument = Argument.TrimEnd
    End Sub

    'JVM 部分
    Private Function McLaunchArgumentsJVM() As List(Of String)
        Dim Args As New List(Of String)

        'Minecraft 参数
        If McInstanceSelected.JsonObject("arguments") IsNot Nothing AndAlso McInstanceSelected.JsonObject("arguments")("jvm") IsNot Nothing Then
            '新版 MC：从版本 JSON 获取参数
            Dim CurrentInstance As McInstance = McInstanceSelected
NextInstance:
            McLaunchLog($"新版 JVM 参数：从版本 JSON 获取（{CurrentInstance.Name}）")
            If CurrentInstance.JsonObject("arguments") IsNot Nothing AndAlso CurrentInstance.JsonObject("arguments")("jvm") IsNot Nothing Then
                For Each SubJson As JToken In CurrentInstance.JsonObject("arguments")("jvm")
                    Select Case SubJson.Type
                        Case JTokenType.String
                            Args.Add(SubJson.ToString.Trim)
                        Case JTokenType.Object
                            Dim Argument As JObject = CType(SubJson, JObject)
                            If Argument.ContainsKey("rules") AndAlso Not McJsonRuleCheck(Argument("rules")) Then Continue For '不满足准则
                            If Not Argument.ContainsKey("value") Then Continue For '没有 value 字段
                            If Argument("value").Type = JTokenType.String Then
                                Args.Add(Argument("value").ToString.Trim)
                            Else
                                For Each value As JToken In Argument("value")
                                    Args.Add(value.ToString.Trim)
                                Next
                            End If
                    End Select
                Next
            End If
            If CurrentInstance.InheritName <> "" Then
                CurrentInstance = New McInstance(CurrentInstance.InheritName)
                GoTo NextInstance
            End If
            '1.17.1 中有个 "-Dos.name=Windows 10"，不立即加双引号会导致 10 被隔开
            Args = Args.Select(Function(a) If(a.Contains(" ") AndAlso Not a.StartsWithF("""") AndAlso Not a.EndsWithF(""""), $"""{a.Replace("""", "\""")}""", a)).ToList
        Else
            '旧版 MC：直接添加参数
            McLaunchLog($"旧版 JVM 参数")
            Args.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump")
            Args.Add("-Djava.library.path=${natives_directory}")
            Args.Add("-cp") : Args.Add("${classpath}")
        End If

        '设置里自定义的参数
        Dim CustomArg As String = Settings.Get(Of String)("VersionAdvanceJvm", Instance:=McInstanceSelected)
        If CustomArg = "" Then CustomArg = Settings.Get(Of String)("LaunchAdvanceJvm")
        If CustomArg <> "" Then CustomArg = ArgumentReplace(CustomArg)

        '重新分割字符串并去重
        Args = DeduplicateJavaArguments(SplitJavaArguments(Args.Join(" "c).Replace("McEmu= ", "McEmu=") & " " & CustomArg).ToList, True)

        'Authlib-Injector
        If McLoginLoader.Output.Type = "Auth" Then
            If McLaunchJavaSelected.Version.Major >= 6 Then Args.Add("-Djavax.net.ssl.trustStoreType=WINDOWS-ROOT") '信任系统根证书（5252#）
            Dim Server As String = Settings.Get(Of String)("VersionServerAuthServer", Instance:=McInstanceSelected).Trim().TrimEnd("/"c)
            Try
                McLaunchLog($"开始 Authlib Injector Prefetch")
                Dim Response As String = NetRequestByClientRetry(Server, Encoding:=Encoding.UTF8)
                Args.Add("-javaagent:""${pure_directory}\authlib-injector.jar""=""" & Server & """")
                Args.Add("-Dauthlibinjector.side=client")
                Args.Add("-Dauthlibinjector.yggdrasil.prefetched=" & Convert.ToBase64String(Encoding.UTF8.GetBytes(Response)))
            Catch ex As Exception
                Throw New Exception("无法连接到第三方登录服务器（" & If(Server, Nothing) & "）", ex)
            End Try
        End If

        'JLW
        '非 GBK 编码下命令行参数乱码的问题（JDK-8272352）
        Dim UseJLW As Boolean =
            Not Settings.Get(Of Boolean)("LaunchAdvanceDisableJLW") AndAlso Not Settings.Get(Of Boolean)("VersionAdvanceDisableJLW", McInstanceSelected) AndAlso
            Not IsGBKEncoding '即使游戏文件夹名称不含非 ASCII 字符，但玩家名等也可能出现中文，因此依然需要 JLW（#8739）
        If UseJLW AndAlso CustomArg.Contains("-javaagent") Then
            UseJLW = False
            McLaunchLog("由于使用了自定义 javaagent，已禁用 JLW，这可能导致游戏崩溃！", LogLevel.Warn)
        End If
        If UseJLW Then
            If McLaunchJavaSelected.Version.Major >= 9 Then
                Args.Add("--add-exports") : Args.Add("cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED")
            End If
            Args.Add("-Doolloo.jlw.tmpdir=${pure_directory}") '这里需要不以 \ 结尾
        End If
        McLaunchLog("使用 JLW：" & UseJLW)

        'LUA
        Dim UseLUA As Boolean =
            Not Settings.Get(Of Boolean)("LaunchAdvanceDisableLUA") AndAlso Not Settings.Get(Of Boolean)("VersionAdvanceDisableLUA", McInstanceSelected) AndAlso
            McLibListGet(McInstanceSelected, False).Select(Function(e) e.OriginalName).Contains("org.lwjgl:lwjgl:3.4.1")
        If UseLUA Then Args.Add($"-javaagent:""{LaunchUtils.ExtractPatch("Launch\LwjglUnsafeAgent.jar", PathPure.Value)}""")
        McLaunchLog("使用 LUA：" & UseLUA)

#Region "内存管理"

        McLaunchLog("当前剩余内存：" & Math.Round(My.Computer.Info.AvailablePhysicalMemory / 1024 / 1024 / 1024 * 10) / 10 & "G")
        Dim TargetRam As Integer = Math.Floor(PageInstanceSetup.GetRam(McInstanceSelected) * 1024)
        Args.Add("-Xmx" & TargetRam & "m")

        '获取设置的管理方式
        '0：在 Java 21+ 使用分代 ZGC，20~15 使用非分代的 ZGC，14- 使用 G1GC
        '1：在 Java 21+ 使用分代 ZGC，20- 使用 G1GC
        '2：仅 G1GC
        '3：不指定，玩家自定义
        Dim SetupType As Integer = Settings.Get(Of Integer)("LaunchAdvanceGC")
        If McInstanceSelected IsNot Nothing AndAlso Settings.Get(Of Integer)("VersionAdvanceGC", McInstanceSelected) > 0 Then SetupType = Settings.Get(Of Integer)("VersionAdvanceGC", McInstanceSelected) - 1 '去掉默认
        If SetupType <> 3 Then
            '确定是使用 G1GC 还是 ZGC
            Dim UseG1GC As Boolean = False
            If (SetupType = 0 AndAlso McLaunchJavaSelected.Version.Major < 15) OrElse
               (SetupType = 1 AndAlso McLaunchJavaSelected.Version.Major < 21) OrElse
               (SetupType = 2 OrElse SetupType = 4) Then UseG1GC = True '检查 Java 版本，ZGC 在 Java 15+ 可用，分代 ZGC 在 Java 21+ 可用
            If Environment.OSVersion.Version.Major < 10 OrElse Environment.OSVersion.Version.Build < 17763 Then UseG1GC = True '检查系统兼容性，ZGC 需要 Windows 10 1809+
            McLaunchLog($"GC 设置：{SetupType}，选取 {If(UseG1GC, "G1GC", "ZGC")}，Java 版本：{McLaunchJavaSelected.Version.Major}")
            '移除已有的 GC 参数
            Args = Args.Where(Function(a) Not a.RegexCheck(
                "( )*-XX:[+-]?(Use\w+GC|ZGenerational|UseCompactObjectHeaders|G1\w+Percent|G1\w+Size|(Max|Min)(GCPauseMillis|HeapFreeRatio))")).ToList
            '添加 GC 参数
            Args.Add("-XX:+UnlockExperimentalVMOptions")
            If McLaunchJavaSelected.Version.Major >= 24 Then Args.Add("-XX:+UseCompactObjectHeaders")
            If UseG1GC AndAlso SetupType = 4 Then
                '优化的 G1GC
                Args.Add("-XX:+UseG1GC")
                Args.Add("-XX:G1NewSizePercent=20")
                Args.Add("-XX:G1ReservePercent=20")
                Args.Add("-XX:G1HeapRegionSize=32M")
                Args.Add("-XX:MaxGCPauseMillis=50")
                If Not ModeDebug Then Args.Add("-XX:+PerfDisableSharedMem")
                If McLaunchJavaSelected.Version.Major <= 7 Then Args.Add("-XX:MaxPermSize=512m") '#8286
                If McLaunchJavaSelected.Version.Major = 8 Then Args.Add("-XX:+ParallelRefProcEnabled")
                If McLaunchJavaSelected.Version.Major >= 12 Then Args.Add("-XX:MinHeapFreeRatio=25") '需要比 G1NewSizePercent 大一点
                If McLaunchJavaSelected.Version.Major >= 12 Then Args.Add("-XX:MaxHeapFreeRatio=40") '需要比 MinHeapFreeRatio 大一些
            ElseIf UseG1GC Then
                'Mojang G1GC
                Args.Add("-XX:+UseG1GC")
                Args.Add("-XX:G1NewSizePercent=20")
                Args.Add("-XX:G1ReservePercent=20")
                Args.Add("-XX:G1HeapRegionSize=32M")
                Args.Add("-XX:MaxGCPauseMillis=50")
                If McLaunchJavaSelected.Version.Major <= 7 Then Args.Add("-XX:MaxPermSize=512m") '#8286
            Else
                'ZGC
                Args.Add("-XX:+UseZGC")
                If McLaunchJavaSelected.Version.Major = 21 OrElse McLaunchJavaSelected.Version.Major = 22 Then Args.Add("-XX:+ZGenerational") 'Java 23 起默认启用分代 ZGC，不需要再添加参数
            End If
        End If

        Args.Remove("-XX:MaxDirectMemorySize=256M") '#3511 的清理

#End Region

        'Log4j 漏洞防御参数
        Args.Add("-Dlog4j2.formatMsgNoLookups=true")

        '编码（#4700、#5892、#5909、#6934、#8950、#9241）
        Dim UseCompatEncoding = McLaunchJavaSelected.Version.Major >= 18
        Dim EncodingName = If(UseCompatEncoding,
                              McLaunchJavaSelected.NativeEncodingName, 'Java 18+ 会添加 COMPAT，因此 Java 会回退到 native.encoding
                              McLaunchJavaSelected.FileEncodingName) 'Java 17- 直接使用 file.encoding
        Try
            McLaunchEncoding = Encoding.GetEncoding(If(EncodingName.Equals("GBK", StringComparison.OrdinalIgnoreCase), "GB18030", EncodingName))
        Catch ex As Exception
            Dim FallbackEncoding = Encoding.GetEncoding(Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage) '老版本 Java 的编码选择行为
            McLaunchLog($"无法识别 Java 编码 {If(EncodingName, "Nothing")}，已回退为 {FallbackEncoding.WebName}", LogLevel.Warn)
            McLaunchEncoding = FallbackEncoding
        End Try
        McLaunchLog($"使用编码：{McLaunchEncoding.WebName}")
        Dim LaunchEncodingName = If(McLaunchEncoding.CodePage = 54936, "GBK", McLaunchEncoding.WebName)
        Dim StdoutArgName = If(McLaunchJavaSelected.Version.Major < 19, "sun.stdout.encoding", "stdout.encoding") '老版本 Java 应该设置 sun.std
        Dim StderrArgName = If(McLaunchJavaSelected.Version.Major < 19, "sun.stderr.encoding", "stderr.encoding")
        If Not Args.Any(Function(a) a.StartsWithF($"-D{StdoutArgName}=")) Then Args.Add($"-D{StdoutArgName}={LaunchEncodingName}")
        If Not Args.Any(Function(a) a.StartsWithF($"-D{StderrArgName}=")) Then Args.Add($"-D{StderrArgName}={LaunchEncodingName}")
        If UseCompatEncoding AndAlso Not Args.Any(Function(a) a.StartsWithF("-Dfile.encoding=")) Then Args.Add("-Dfile.encoding=COMPAT") '防止老版本 MC 与部分 Mod 不兼容 UTF-8

        '为 JLW 添加 -jar，它必须放在最后
        If UseJLW Then
            Args.Add("-jar") : Args.Add(LaunchUtils.ExtractPatch("Launch\JavaWrapper.jar", PathPure.Value))
        End If

        '再次去重并输出
        Return DeduplicateJavaArguments(Args, True)
    End Function
    '游戏参数部分
    Private Function McLaunchArgumentsGame() As List(Of String)
        Dim Arg As String = ""

        'Minecraft 参数
        '旧版 MC：直接添加参数
        If McInstanceSelected.JsonObject("minecraftArguments")?.ToString.Any Then
            Dim BasicString As String = McInstanceSelected.JsonObject("minecraftArguments").ToString
            McLaunchLog($"旧版游戏参数：" & BasicString)
            BasicString += " --height ${resolution_height} --width ${resolution_width}" '总是添加宽高选项，之后去重的时候会覆盖 MC 自带的
            Arg &= " " & BasicString
        End If
        '新版 MC：从版本 JSON 获取参数
        If McInstanceSelected.JsonObject("arguments") IsNot Nothing AndAlso McInstanceSelected.JsonObject("arguments")("game") IsNot Nothing Then
            Dim CurrentInstance As McInstance = McInstanceSelected
            Dim AppendArg = Sub(a As String) Arg &= " " & If(a.Contains(" ") AndAlso Not a.StartsWithF("""") AndAlso Not a.EndsWithF(""""), $"""{a.Replace("""", "\""")}""", a)
NextInstance:
            McLaunchLog($"新版游戏参数：从版本 JSON 获取（{CurrentInstance.Name}）")
            If CurrentInstance.JsonObject("arguments") IsNot Nothing AndAlso CurrentInstance.JsonObject("arguments")("game") IsNot Nothing Then
                For Each SubJson As JToken In CurrentInstance.JsonObject("arguments")("game")
                    Select Case SubJson.Type
                        Case JTokenType.String
                            AppendArg(SubJson.ToString)
                        Case JTokenType.Object
                            Dim Argument As JObject = CType(SubJson, JObject)
                            If Argument.ContainsKey("rules") AndAlso Not McJsonRuleCheck(Argument("rules")) Then Continue For '不满足准则
                            If Not Argument.ContainsKey("value") Then Continue For '没有 value 字段
                            If Argument("value").Type = JTokenType.String Then
                                AppendArg(Argument("value").ToString)
                            Else
                                For Each value As JToken In Argument("value")
                                    AppendArg(value.ToString)
                                Next
                            End If
                    End Select
                Next
            End If
            If CurrentInstance.InheritName <> "" Then
                CurrentInstance = New McInstance(CurrentInstance.InheritName)
                GoTo NextInstance
            End If
        End If

        '设置里自定义的参数
        Dim CustomArg As String = Settings.Get(Of String)("VersionAdvanceGame", Instance:=McInstanceSelected)
        If CustomArg = "" Then CustomArg = Settings.Get(Of String)("LaunchAdvanceGame")
        If CustomArg <> "" Then CustomArg = ArgumentReplace(CustomArg)
        Arg &= " " & CustomArg

        '把 OptiFineForgeTweaker 放在参数的末尾
        If (McInstanceSelected.Version.ModLoaders.HasFlagF(ModLoaders.Forge) OrElse McInstanceSelected.Version.ModLoaders.HasFlagF(ModLoaders.LiteLoader)) AndAlso
                McInstanceSelected.Version.HasOptiFine AndAlso Not McInstanceSelected.Version.OptiFineAsMod Then
            If Arg.Contains("--tweakClass optifine.OptiFineForgeTweaker") Then
                McLaunchLog("发现正确的 OptiFineForge TweakClass，目前的游戏参数：" & Arg)
                Arg = Arg.Replace(" --tweakClass optifine.OptiFineForgeTweaker", "") & " --tweakClass optifine.OptiFineForgeTweaker"
            End If
            If Arg.Contains("--tweakClass optifine.OptiFineTweaker") Then
                McLaunchLog("发现错误的 OptiFineForge TweakClass，目前的游戏参数：" & Arg)
                Arg = Arg.Replace(" --tweakClass optifine.OptiFineTweaker", "") & " --tweakClass optifine.OptiFineForgeTweaker"
                Try
                    FileUtils.Write(
                        McInstanceSelected.GetJsonPath(),
                        FileUtils.ReadAsString(McInstanceSelected.GetJsonPath()).Replace("optifine.OptiFineTweaker", "optifine.OptiFineForgeTweaker"))
                Catch ex As Exception
                    Logger.Warn(ex, "替换 OptiFineForge TweakClass 失败")
                End Try
            End If
        End If

        '重新分割字符串并去重
        Dim Args As List(Of String) = DeduplicateJavaArguments(SplitJavaArguments(Arg).ToList, False)

        '额外传入的参数
        Args.AddRange(CurrentLaunchOptions.ExtraGameArgs)

        '全屏
        If Settings.Get(Of Integer)("LaunchArgumentWindowType") = 0 Then Args.Add("--fullscreen")

        '直连单人世界或自动进入服务器
        Dim TargetWorld As String = CurrentLaunchOptions.SingleplayerWorld
        If Not String.IsNullOrWhiteSpace(TargetWorld) Then
            If McInstanceSelected.ReleaseTime > New Date(2023, 4, 4) Then
                Args.Add("--quickPlaySingleplayer")
                Args.Add(TargetWorld)
            Else
                Hint("当前游戏版本低于 1.20，不支持直连进入单人存档，已为你正常启动游戏。", HintType.Blue)
            End If
        Else
            Dim Server As String = If(String.IsNullOrEmpty(CurrentLaunchOptions.ServerIp), Settings.Get(Of String)("VersionServerEnter", McInstanceSelected), CurrentLaunchOptions.ServerIp)
            If Server.Any Then
                If McInstanceSelected.ReleaseTime > New Date(2023, 4, 4) Then
                    'QuickPlay
                    Args.Add($"--quickPlayMultiplayer") : Args.Add(Server)
                Else
                    '老版本
                    If Server.Contains(":") Then
                        '包含端口号
                        Args.Add("--server") : Args.Add(Server.Split(":")(0))
                        Args.Add("--port") : Args.Add(Server.Split(":")(1))
                    Else
                        '不包含端口号
                        Args.Add("--server") : Args.Add(Server)
                        Args.Add("--port") : Args.Add("25565")
                    End If
                    If McInstanceSelected.Version.HasOptiFine Then Hint("OptiFine 与自动进入服务器可能不兼容，有概率导致材质丢失甚至游戏崩溃！", HintType.Red)
                End If
            End If
        End If

        '再次去重并输出
        Return DeduplicateJavaArguments(Args, False)
    End Function
    '获取替换标记
    Private Iterator Function McLaunchArgumentsReplace(Arguments As List(Of String)) As IEnumerable(Of (Key As String, Value As String))
        '基础参数
        Yield ("${classpath_separator}", ";")
        Yield ("${natives_directory}", PathUtils.RemoveSlashSuffix(GetNativesFolder()))
        Yield ("${library_directory}", PathUtils.ToShortPath(McFolderSelected & "libraries"))
        Yield ("${libraries_directory}", PathUtils.ToShortPath(McFolderSelected & "libraries"))
        Yield ("${pure_directory}", PathUtils.RemoveSlashSuffix(PathPure.Value)) '由 PCL Linking 添加，这会允许在分割参数并去重后再替换路径，防止路径中的特殊字符影响参数分割和去重
        Yield ("${launcher_name}", "PCL Linking")
        Yield ("${launcher_version}", VersionCode)
        Yield ("${version_name}", McInstanceSelected.Name)
        Yield ("${game_directory}", PathUtils.ToShortPath(Left(McInstanceSelected.PathIndie, McInstanceSelected.PathIndie.Count - 1)))
        Yield ("${assets_root}", PathUtils.ToShortPath(McFolderSelected & "assets"))
        Yield ("${user_properties}", "{}")
        Yield ("${auth_player_name}", McLoginLoader.Output.Name)
        Yield ("${auth_uuid}", McLoginLoader.Output.Uuid)
        Yield ("${auth_access_token}", McLoginLoader.Output.AccessToken)
        Yield ("${access_token}", McLoginLoader.Output.AccessToken)
        Yield ("${auth_session}", McLoginLoader.Output.AccessToken)
        Yield ("${user_type}", "msa") '#1221
        Yield ("${primary_jar}", PathUtils.ToShortPath(McInstanceSelected.PathVersion & McInstanceSelected.Name & ".jar")) '#6942
        Yield ("${game_assets}", PathUtils.ToShortPath(McFolderSelected & "assets\virtual\legacy")) '1.5.2 的 pre-1.6 资源索引应与 legacy 合并
        Yield ("${assets_index_name}", McAssetsGetIndexName(McInstanceSelected))

        '自定义信息
        Dim ArgumentInfo As String
        ArgumentInfo = Settings.Get(Of String)("VersionArgumentInfo", Instance:=McInstanceSelected)
        If ArgumentInfo = "" Then ArgumentInfo = Settings.Get(Of String)("LaunchArgumentInfo")
        If ArgumentInfo = "PCL" OrElse ArgumentInfo = "LML" Then ArgumentInfo = "PCL Linking" 'LML：兼容旧配置里遗留的版本类型名
        If ArgumentInfo = "" Then '若自定义信息为空，则必须从参数中去掉该部分，不然 MC 会显示 ${version_type} 或者一个空字符串
            If Arguments.Contains("--versionType") Then
                Dim Index As Integer = Arguments.IndexOf("--versionType")
                Arguments.RemoveAt(Index)
                If Index < Arguments.Count Then Arguments.RemoveAt(Index)
            End If
            Yield ("${version_type}", """""")
        Else
            Yield ("${version_type}", ArgumentReplace(ArgumentInfo))
        End If

        '窗口尺寸
        Dim TargetSize As Size
        Select Case Settings.Get(Of Integer)("LaunchArgumentWindowType")
            Case 2 '与启动器尺寸一致
                Dim Result As Size
                RunInUiWait(Sub() Result = New Size(GetPixelSize(FrmMain.PanForm.ActualWidth), GetPixelSize(FrmMain.PanForm.ActualHeight)))
                TargetSize = Result
                TargetSize.Height -= 29.5 * DPI / 96 '标题栏高度
            Case 3 '自定义
                TargetSize = New Size(Math.Max(100, Settings.Get(Of Integer)("LaunchArgumentWindowWidth")), Math.Max(100, Settings.Get(Of Integer)("LaunchArgumentWindowHeight")))
            Case Else
                TargetSize = New Size(854, 480)
        End Select
        If McInstanceSelected.Version.Vanilla.Major <= 12 AndAlso McLaunchJavaSelected.Version >= New Version(8, 0, 200) AndAlso McLaunchJavaSelected.Version <= New Version(8, 0, 321) AndAlso
                Not McInstanceSelected.Version.HasOptiFine AndAlso Not McInstanceSelected.Version.ModLoaders.HasFlagF(ModLoaders.Forge) Then '修复 1.12.2- 下 JRE 8u200~321 下窗口大小为设置大小的 DPI% 倍（#3463）
            McLaunchLog($"已应用窗口大小过大修复（Java 版本：{McLaunchJavaSelected.Version}）")
            TargetSize.Width /= DPI / 96
            TargetSize.Height /= DPI / 96
        End If
        Yield ("${resolution_width}", Math.Round(TargetSize.Width))
        Yield ("${resolution_height}", Math.Round(TargetSize.Height))

        '支持库
        Dim Cps As New List(Of String)
        Dim OptiFineCp As String = Nothing
        For Each Library As McLibEntry In McLibListGet(McInstanceSelected, True)
            If Library.IsNatives Then Continue For
            If Library.Name IsNot Nothing AndAlso Library.Name = "optifine:OptiFine" Then
                OptiFineCp = Library.LocalPath
            Else
                Cps.Add(Library.LocalPath)
            End If
        Next
        If OptiFineCp IsNot Nothing Then Cps.Insert(Cps.Count - 2, OptiFineCp) 'OptiFine 的总是需要放到倒数第二位
        Yield ("${classpath}", Cps.Select(Function(c) PathUtils.ToShortPath(c)).Join(";"c))
    End Function

    ''' <summary>
    ''' 分割 Java 参数字符串。
    ''' </summary>
    Private Iterator Function SplitJavaArguments(Arg As String) As IEnumerable(Of String)
        Arg = Arg.ReplaceLineEndings(" ", mergeMultiple:=True).Trim '预处理换行符
        If String.IsNullOrWhiteSpace(Arg) Then Return
        Dim Current As New StringBuilder
        Dim InQuotes As Boolean = False
        Dim i As Integer = 0
        While i < Arg.Length
            Dim c As Char = Arg(i)
            If c = "\"c AndAlso i + 1 < Arg.Length AndAlso Arg(i + 1) = """"c Then '检测 \"
                '转义的双引号：保留反斜杠和引号，不切换 InQuotes
                Current.Append("\""")
                i += 1 '额外跳过下一个字符（双引号）
            ElseIf c = """"c Then
                '未转义的双引号：切换 InQuotes
                InQuotes = Not InQuotes
                Current.Append(c)
            ElseIf c = " "c AndAlso Not InQuotes Then
                '不在双引号内的空格：一个参数结束
                If Current.Length > 0 Then
                    Yield Current.ToString()
                    Current.Clear()
                End If
            Else
                '普通字符，直接添加
                Current.Append(c)
            End If
            i += 1
        End While
        If Current.Length > 0 Then Yield Current.ToString '添加剩余的
    End Function
    ''' <summary>
    ''' 对 Java 参数进行去重。
    ''' </summary>
    Private Function DeduplicateJavaArguments(Args As List(Of String), IsJVMArgs As Boolean) As List(Of String)
        Dim Result As New List(Of String)
        Dim i As Integer = 0
        While i < Args.Count
            Dim Key As String = Args(i)
            If Not Key.StartsWithF("-") OrElse
               (i + 1 >= Args.Count OrElse (Args(i + 1).StartsWithF("-") AndAlso Val(Args(i + 1).AfterFirst("-")) = 0)) Then '避免 -xPos 23 -xPos -50 中后面的 -50 被识别为参数
                '单参数
                i += 1
                If Result.Contains(Key) Then
                    McLaunchLog($"已去重单个{If(IsJVMArgs, " JVM ", "游戏")}参数：{Key}")
                    Continue While
                End If
                Result.Add(Key)
            Else
                '以空格间隔的键值对（例如 --width 233）
                Dim Value As String = Args(i + 1)
                i += 2
                For j = 0 To Result.Count - 2 '向前寻找相同的键
                    If Result(j) <> Key Then Continue For
                    '仅当是游戏参数且不是 tweakClass 时，用新的值覆盖旧的值
                    '据测试，如果包含多个相同 --width 或 --uuid 等参数，两个都会直接失效，如果这发生在关键参数上会导致崩溃
                    If Not IsJVMArgs AndAlso Key <> "--tweakClass" Then
                        McLaunchLog($"已覆盖重复的{If(IsJVMArgs, " JVM ", "游戏")}参数：{Key} {Result(j + 1)} → {Key} {Value}")
                        Result(j + 1) = Value
                        Continue While
                    End If
                    '键和值完全相同，直接抛弃
                    If Result(j + 1) = Value Then
                        McLaunchLog($"已去重{If(IsJVMArgs, " JVM ", "游戏")}参数对：{Key} {Value}")
                        Continue While
                    End If
                Next
                Result.Add(Key)
                Result.Add(Value)
            End If
        End While
        Return Result
    End Function

#End Region

#Region "解压 Natives"

    Private Sub McLaunchNatives(Loader As LoaderTask(Of Integer, Integer))

        '创建文件夹
        Dim Target As String = GetNativesFolder()
        DirectoryUtils.Create(Target)

        '解压文件
        McLaunchLog("正在解压 Natives 文件")
        Dim ExistFiles As New List(Of String)
        For Each Native As McLibEntry In McLibListGet(McInstanceSelected, True)
            If Not Native.IsNatives Then Continue For
            Try
                Using Zip = FileUtils.OpenZip(Native.LocalPath)
                    For Each Entry In Zip.Entries
                        Dim FileName As String = Entry.FullName
                        If PathUtils.GetExtension(FileName) = "dll" Then
                            '实际解压文件的步骤
                            Dim FilePath As String = Target & FileName
                            ExistFiles.Add(FilePath)
                            Dim OriginalFile = FileUtils.GetInfo(FilePath)
                            If OriginalFile.Exists Then
                                If OriginalFile.Length = Entry.Length Then
                                    If ModeDebug Then McLaunchLog("无需解压：" & FilePath)
                                    Continue For
                                End If
                                '删除原文件
                                Try
                                    FileUtils.Delete(FilePath)
                                Catch ex As UnauthorizedAccessException
                                    McLaunchLog("删除原 dll 访问被拒绝，这通常代表有一个 MC 正在运行，跳过解压：" & FilePath)
                                    McLaunchLog("实际的错误信息：" & ex.GetDisplay(True))
                                    Exit For
                                End Try
                            End If
                            '解压新文件
                            Using EntryStream = Entry.Open()
                                FileUtils.Write(FilePath, EntryStream)
                            End Using
                            McLaunchLog("已解压：" & FilePath)
                        End If
                    Next
                End Using
            Catch ex As InvalidDataException
                Logger.Warn(ex, $"打开 Natives 文件失败（{Native.LocalPath}）")
                FileUtils.Delete(Native.LocalPath)
                Throw New Exception("无法打开 Natives 文件（" & Native.LocalPath & "），该文件可能已损坏，请重新尝试启动游戏")
            End Try
        Next

        '删除多余文件
        For Each FileName As String In DirectoryUtils.EnumerateFiles(Target)
            If ExistFiles.Contains(FileName) Then Continue For
            Try
                McLaunchLog("删除：" & FileName)
                FileUtils.Delete(FileName)
            Catch ex As UnauthorizedAccessException
                McLaunchLog("删除多余文件访问被拒绝，跳过删除步骤")
                McLaunchLog("实际的错误信息：" & ex.GetDisplay(True))
                Return
            End Try
        Next

    End Sub
    ''' <summary>
    ''' 获取 Natives 文件夹路径，以 \ 结尾。
    ''' </summary>
    Public Function GetNativesFolder(Optional Instance As McInstance = Nothing) As String
        If Instance Is Nothing Then Instance = McInstanceSelected
        Dim Result As String = PathUtils.AddSlashSuffix(PathUtils.ToShortPath(Instance.PathVersion)) & Instance.Name & "-natives\"
        If Not (IsGBKEncoding OrElse Result.IsAsciiOnly()) Then
            Result = Paths.AppData & ".minecraft\bin\natives\"
            If Not Result.IsAsciiOnly() Then
                Result = OsDrive & "ProgramData\PCL Linking\natives\"
            End If
        End If
        DirectoryUtils.Create(Result) '提前创建，这样 DirectoryUtils.Shorten 才有结果（否则在长路径下首次启动 Forge 会崩溃）
        Return PathUtils.AddSlashSuffix(PathUtils.ToShortPath(Result))
    End Function

#End Region

#Region "启动与前后处理"

    Private Sub McLaunchPrerun()

        '使用高性能显卡
        If Settings.Get(Of Boolean)("LaunchAdvanceGraphicCard") Then
            Try
                SetGPUPreference(McLaunchJavaSelected.JavaExePath)
                SetGPUPreference(PathExe)
            Catch ex As Exception
                Logger.Warn(ex, "直接调整显卡设置失败")
            End Try
        End If

        '更新 launcher_profiles.json
        Try
            '确保可用
            If Not McLoginLoader.Output.Type = "Microsoft" Then Exit Try
            McFolderLauncherProfilesJsonCreate(McFolderSelected)
            '构建需要替换的 Json 对象
            Dim ReplaceJsonString As String = "
            {
              ""authenticationDatabase"": {
                ""00000111112222233333444445555566"": {
                  ""username"": """ & McLoginLoader.Output.Name.Replace("""", "-") & """,
                  ""profiles"": {
                    ""66666555554444433333222221111100"": {
                        ""displayName"": """ & McLoginLoader.Output.Name & """
                    }
                  }
                }
              },
              ""clientToken"": """ & McLoginLoader.Output.ClientToken & """,
              ""selectedUser"": {
                ""account"": ""00000111112222233333444445555566"", 
                ""profile"": ""66666555554444433333222221111100""
              }
            }"
            Dim ReplaceJson As JObject = (ReplaceJsonString).DeserializeJson()
            '更新文件
            Dim Profiles As JObject = FileUtils.ReadAsJson(McFolderSelected & "launcher_profiles.json")
            Profiles.Merge(ReplaceJson)
            FileUtils.Write(McFolderSelected & "launcher_profiles.json", Profiles.ToString, encoding:=Encoding.GetEncoding("GB18030"))
            McLaunchLog("已更新 launcher_profiles.json")
        Catch ex As Exception
            Logger.Warn(ex, "更新 launcher_profiles.json 失败，将在删除文件后重试")
            Try
                FileUtils.Delete(McFolderSelected & "launcher_profiles.json")
                McFolderLauncherProfilesJsonCreate(McFolderSelected)
                '构建需要替换的 Json 对象
                Dim ReplaceJsonString As String = "
                    {
                      ""authenticationDatabase"": {
                        ""00000111112222233333444445555566"": {
                          ""username"": """ & McLoginLoader.Output.Name.Replace("""", "-") & """,
                          ""profiles"": {
                            ""66666555554444433333222221111100"": {
                                ""displayName"": """ & McLoginLoader.Output.Name & """
                            }
                          }
                        }
                      },
                      ""clientToken"": """ & McLoginLoader.Output.ClientToken & """,
                      ""selectedUser"": {
                        ""account"": ""00000111112222233333444445555566"", 
                        ""profile"": ""66666555554444433333222221111100""
                      }
                    }"
                Dim ReplaceJson As JObject = ReplaceJsonString.DeserializeJson()
                '更新文件
                Dim Profiles As JObject = FileUtils.ReadAsJson(McFolderSelected & "launcher_profiles.json")
                Profiles.Merge(ReplaceJson)
                FileUtils.Write(McFolderSelected & "launcher_profiles.json", Profiles.ToString, encoding:=Encoding.GetEncoding("GB18030"))
                McLaunchLog("已在删除后更新 launcher_profiles.json")
            Catch exx As Exception
                Logger.Error(exx, "更新 launcher_profiles.json 失败")
            End Try
        End Try

        '更新 options.txt
        Dim SetupFileAddress As String = McInstanceSelected.PathIndie & "options.txt"
        If Not FileUtils.Exists(SetupFileAddress) Then
            'Yosbr Mod 兼容（#2385）：https://www.curseforge.com/minecraft/mc-mods/yosbr
            Dim YosbrFileAddress As String = McInstanceSelected.PathIndie & "config\yosbr\options.txt"
            If FileUtils.Exists(YosbrFileAddress) Then
                McLaunchLog("将修改 Yosbr Mod 中的 options.txt")
                SetupFileAddress = YosbrFileAddress
                WriteIni(SetupFileAddress, "lang", "none") '忽略默认语言
            End If
        End If
        Try
            '语言
            '1.0-     ：没有语言选项
            '1.1 ~ 5  ：zh_CN 时正常，zh_cn 时崩溃（最后两位字母必须大写，否则将会 NPE 崩溃）
            '1.6 ~ 10 ：zh_CN 时正常，zh_cn 时自动切换为英文
            '1.11 ~ 12：zh_cn 时正常，zh_CN 时虽然显示了中文但语言设置会错误地显示选择英文
            '1.13+    ：zh_cn 时正常，zh_CN 时自动切换为英文
            Dim CurrentLang As String = ReadIni(SetupFileAddress, "lang", "none")
            Dim RequiredLang As String = If(CurrentLang = "none" OrElse Not DirectoryUtils.Exists(McInstanceSelected.PathIndie & "saves"), '#3844，整合包可能已经自带了 options.txt
                If(Settings.Get(Of Boolean)("ToolHelpChinese"), "zh_cn", "en_us"), CurrentLang.Lower)
            If Not McInstanceSelected.Version.Valid OrElse McInstanceSelected.Version.Vanilla.Major <= 10 Then
                '将最后两位改为大写，前面的部分保留
                RequiredLang = RequiredLang.Substring(0, RequiredLang.Length - 2) & RequiredLang.Substring(RequiredLang.Length - 2).Upper
            End If
            If CurrentLang = RequiredLang Then
                McLaunchLog($"需要的语言为 {RequiredLang}，当前语言为 {CurrentLang}，无需修改")
            Else
                WriteIni(SetupFileAddress, "lang", "-") '触发缓存更改，避免删除后重新下载残留缓存
                WriteIni(SetupFileAddress, "lang", RequiredLang)
                McLaunchLog($"已将语言从 {CurrentLang} 修改为 {RequiredLang}")
            End If
            ''如果是初次设置，一并修改 forceUnicodeFont
            'If Settings.Get(Of Boolean)("ToolHelpChinese") AndAlso (CurrentLang = "none" OrElse Not DirectoryUtils.Exists(McInstanceSelected.PathIndie & "saves")) Then
            '    WriteIni(SetupFileAddress, "forceUnicodeFont", "true")
            '    McLaunchLog("已开启 forceUnicodeFont")
            'End If
            '窗口
            Select Case Settings.Get(Of Integer)("LaunchArgumentWindowType")
                Case 0 '全屏
                    WriteIni(SetupFileAddress, "fullscreen", "true")
                Case 1 '默认
                Case Else '其他
                    WriteIni(SetupFileAddress, "fullscreen", "false")
            End Select
        Catch ex As Exception
            Logger.Error(ex, "更新 options.txt 失败", LogBehavior.Toast)
        End Try

        '离线皮肤 Alex 警告
        If McInstanceSelected.Version.Vanilla.Major <= 7 AndAlso McInstanceSelected.Version.Vanilla.Major >= 2 AndAlso '1.2 ~ 1.7
               McLoginLoader.Input.Type = McLoginType.Legacy AndAlso '离线登录
               (Settings.Get(Of Integer)("LaunchSkinType") = 2 OrElse '强制 Alex
               (Settings.Get(Of Integer)("LaunchSkinType") = 4 AndAlso Settings.Get(Of Boolean)("LaunchSkinSlim"))) Then '或选用 Alex 皮肤
            Hint("此 Minecraft 版本尚不支持 Alex 皮肤，你的皮肤可能会显示为 Steve！", HintType.Red)
        End If

        '离线皮肤资源包
        Try
            Dim ZipFileAddress As String = McInstanceSelected.PathIndie & "resourcepacks\PCL2 Skin.zip"
            Dim NewTypeSetup As Boolean = McInstanceSelected.Version.Vanilla.Major >= 13 OrElse McInstanceSelected.Version.Vanilla.Major < 6
            If McLoginLoader.Input.Type = McLoginType.Legacy AndAlso Settings.Get(Of Integer)("LaunchSkinType") = 4 AndAlso FileUtils.Exists(GetPortableDataPath("CustomSkin.png")) Then
                Dim MetaFileAddress As String = PathTemp & "pack.mcmeta"
                Dim PackPicAddress As String = PathTemp & "pack.png"
                If McInstanceSelected.Version.Vanilla.Major < 6 Then
                    McLaunchLog("Minecraft 版本过老，尚不支持自定义离线皮肤")
                    GoTo IgnoreCustomSkin
                End If
                Dim PackVersionJToken As JToken = McInstanceSelected.JsonVersion?("pack_version")
                If TypeOf PackVersionJToken Is JObject Then PackVersionJToken = DirectCast(PackVersionJToken, JObject)("resource") '从 1.17 开始，可以从 pack_version.resource 获取资源包版本
                Dim PackVersion As Integer = Val(PackVersionJToken?.ToString)
                If PackVersion <= 0 Then '没有对应字段的老版本 MC，或兜底
                    Select Case McInstanceSelected.Version.Vanilla.Major
                        Case 6, 7, 8
                            PackVersion = 1
                        Case 9, 10
                            PackVersion = 2
                        Case 11, 12
                            PackVersion = 3
                        Case 13, 14
                            PackVersion = 4
                        Case 15
                            PackVersion = 5
                        Case 16
                            PackVersion = If(McInstanceSelected.Version.Vanilla.Build <= 1, 5, 6)
                        Case 17
                            PackVersion = 7
                        Case 18
                            PackVersion = 8
                        Case Else '旧版快照可能没有可用的资源包格式声明
                            PackVersion = 17
                    End Select
                End If
                McLaunchLog($"正在构建自定义皮肤资源包，版本号 {PackVersion}，MC 版本 {McInstanceSelected.Version.Vanilla}")
                '准备文件
                Dim Bit As New MyBitmap(PathImage & "Heads/Logo.png")
                Bit.Save(PackPicAddress)
                FileUtils.Write(MetaFileAddress, "{""pack"":{""pack_format"":" & PackVersion & ",""description"":""PCL Linking 自定义离线皮肤资源包""}}")
                Dim Skin As New MyBitmap(GetPortableDataPath("CustomSkin.png"))
                If (McInstanceSelected.Version.Vanilla.Major = 6 OrElse McInstanceSelected.Version.Vanilla.Major = 7) AndAlso Skin.Pic.Height = 64 Then
                    McLaunchLog("该 Minecraft 版本不支持双层皮肤，已进行裁剪")
                    Skin = Skin.Clip(0, 0, 64, 32)
                End If
                Skin.Save(Paths.Base & "PCL Linking\CustomSkin_Cliped.png")
                '构建压缩文件
                Dim FilesToCompress As New Dictionary(Of String, String) From {
                    {"pack.mcmeta", MetaFileAddress}, {"pack.png", PackPicAddress}}
                If McInstanceSelected.Version.Vanilla >= New Version(19, 0, 3) Then
                    '1.19.3+ 使用复杂版本的替换
                    For Each SkinName In {"alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri"}
                        FilesToCompress.Add(
                                $"assets/minecraft/textures/entity/player/{If(Settings.Get(Of Boolean)("LaunchSkinSlim"), "slim", "wide")}/{SkinName}.png",
                                Paths.Base & "PCL Linking\CustomSkin_Cliped.png")
                    Next
                Else
                    FilesToCompress.Add(
                            $"assets/minecraft/textures/entity/{If(Settings.Get(Of Boolean)("LaunchSkinSlim"), "alex.png", "steve.png")}",
                            Paths.Base & "PCL Linking\CustomSkin_Cliped.png")
                End If
                FileUtils.CreateZipFromFiles(ZipFileAddress, FilesToCompress)
                FileUtils.Delete(Paths.Base & "PCL Linking\CustomSkin_Cliped.png")
                '更改设置文件
                IniClearCache(SetupFileAddress)
                Dim EnabledResourcePack As String = ReadIni(SetupFileAddress, "resourcePacks", "[]").TrimStart("[").TrimEnd("]")
                If NewTypeSetup Then
                    If EnabledResourcePack = "" Then EnabledResourcePack = """vanilla"""
                    Dim EnabledResourcePacks As New List(Of String)(EnabledResourcePack.Split(","))
                    Dim NewResourcePacks As New List(Of String)
                    For Each Res In EnabledResourcePacks
                        If Res <> """file/PCL2 Skin.zip""" AndAlso Res <> "" Then NewResourcePacks.Add(Res)
                    Next
                    NewResourcePacks.Add("""file/PCL2 Skin.zip""")
                    Dim Result As String = "[" & NewResourcePacks.Join(","c) & "]"
                    WriteIni(SetupFileAddress, "resourcePacks", Result)
                Else
                    Dim EnabledResourcePacks As New List(Of String)(EnabledResourcePack.Split(","))
                    Dim NewResourcePacks As New List(Of String)
                    For Each Res In EnabledResourcePacks
                        If Res <> """PCL2 Skin.zip""" AndAlso Res <> "" Then NewResourcePacks.Add(Res)
                    Next
                    NewResourcePacks.Add("""PCL2 Skin.zip""")
                    Dim Result As String = "[" & NewResourcePacks.Join(","c) & "]"
                    WriteIni(SetupFileAddress, "resourcePacks", Result)
                End If
IgnoreCustomSkin:
            ElseIf FileUtils.Exists(ZipFileAddress) Then
                McLaunchLog("正在清空自定义皮肤资源包")
                '删除压缩文件
                FileUtils.Delete(ZipFileAddress)
                '更改设置文件
                IniClearCache(SetupFileAddress)
                Dim EnabledResourcePack As String = ReadIni(SetupFileAddress, "resourcePacks", "[]").TrimStart("[").TrimEnd("]")
                If NewTypeSetup Then
                    If EnabledResourcePack = "" Then EnabledResourcePack = """vanilla"""
                    Dim EnabledResourcePacks As New List(Of String)(EnabledResourcePack.Split(","))
                    EnabledResourcePacks.Remove("""file/PCL2 Skin.zip""")
                    Dim Result As String = "[" & EnabledResourcePacks.Join(","c) & "]"
                    WriteIni(SetupFileAddress, "resourcePacks", Result)
                Else
                    Dim EnabledResourcePacks As New List(Of String)(EnabledResourcePack.Split(","))
                    EnabledResourcePacks.Remove("""PCL2 Skin.zip""")
                    Dim Result As String = "[" & EnabledResourcePacks.Join(","c) & "]"
                    WriteIni(SetupFileAddress, "resourcePacks", Result)
                End If
            End If
        Catch ex As Exception
            Logger.Error(ex, "离线皮肤资源包设置失败", LogBehavior.Toast)
        End Try

    End Sub
    Private Sub McLaunchCustom(Loader As LoaderTask(Of Integer, Integer))

        '获取自定义命令
        Dim CustomCommandGlobal As String = Settings.Get(Of String)("LaunchAdvanceRun")
        If CustomCommandGlobal <> "" Then CustomCommandGlobal = ArgumentReplace(CustomCommandGlobal)
        Dim CustomCommandVersion As String = Settings.Get(Of String)("VersionAdvanceRun", Instance:=McInstanceSelected)
        If CustomCommandVersion <> "" Then CustomCommandVersion = ArgumentReplace(CustomCommandVersion)

        '输出 bat
        Try
            Dim CmdString As String =
                $"chcp {If(McLaunchEncoding.CodePage = 54936 AndAlso IsGBKEncoding, 936, McLaunchEncoding.CodePage)}>nul" & vbCrLf &
                "@echo off" & vbCrLf &
                $"title 启动 - {McInstanceSelected.Name}" & vbCrLf &
                "echo 游戏正在启动，请稍候。" & vbCrLf &
                $"cd /D ""{PathUtils.ToShortPath(McInstanceSelected.PathIndie)}""" & vbCrLf &
                CustomCommandGlobal & vbCrLf &
                CustomCommandVersion & vbCrLf &
                $"""{McLaunchJavaSelected.JavaExePath}"" {McLaunchArgument}" & vbCrLf &
                "echo 游戏已退出。" & vbCrLf &
                "pause"
            FileUtils.Write(
                filePath:=If(CurrentLaunchOptions.SaveBatch, Paths.Base & "PCL Linking\LatestLaunch.bat"),
                text:=FilterAccessToken(CmdString, "F").Replace("%", "%%"),
                encoding:=McLaunchEncoding)
            If CurrentLaunchOptions.SaveBatch IsNot Nothing Then
                McLaunchLog("导出启动脚本完成，强制结束启动过程")
                CanceledHint = "导出启动脚本成功！"
                OpenExplorer(CurrentLaunchOptions.SaveBatch)
                Loader.Parent.Cancel()
                Return '导出脚本完成
            End If
        Catch ex As Exception
            Logger.Warn(ex, "输出启动脚本失败")
            If CurrentLaunchOptions.SaveBatch IsNot Nothing Then Throw ex '直接触发启动失败
        End Try

        '执行自定义命令
        Dim RunCustomCommand As Action(Of String, String, Boolean) =
            Sub(Kind As String, Command As String, WaitForExit As Boolean)
                If Command = "" Then Return
                McLaunchLog($"正在执行{Kind}自定义命令：" & Command)
                Dim CustomProcess As Process = Nothing
                Try
                    CustomProcess = StartProcess(New ProcessStartInfo With {
                        .FileName = "cmd.exe",
                        .Arguments = $"/c ""{Command}""",
                        .WorkingDirectory = McFolderSelected,
                        .UseShellExecute = False,
                        .CreateNoWindow = True
                    })
                    If WaitForExit Then
                        Do Until CustomProcess.HasExited OrElse Loader.IsCanceled
                            Thread.Sleep(10)
                        Loop
                    End If
                Catch ex As Exception
                    Logger.Error(ex, $"执行{Kind}自定义命令失败", LogBehavior.Toast)
                Finally
                    If CustomProcess IsNot Nothing Then
                        Try
                            If Not CustomProcess.HasExited AndAlso Loader.IsCanceled Then
                                McLaunchLog("由于取消启动，已强制结束自定义命令 CMD 进程") '#1183
                                CustomProcess.Kill()
                            End If
                        Finally
                            CustomProcess.Dispose()
                        End Try
                    End If
                End Try
            End Sub
        RunCustomCommand("全局", CustomCommandGlobal, Settings.Get(Of Boolean)("LaunchAdvanceRunWait"))
        RunCustomCommand("版本", CustomCommandVersion, Settings.Get(Of Boolean)("VersionAdvanceRunWait", Instance:=McInstanceSelected))

    End Sub
    Private Sub McLaunchRun(Loader As LoaderTask(Of Integer, Process))

        '启动信息
        Dim StartInfo As New ProcessStartInfo(McLaunchJavaSelected.JavaExePath) '使用 javaw.exe 会导致 #6263

        '设置环境变量
        Dim PathEnv As String = StartInfo.EnvironmentVariables("Path")
        Dim Paths As New List(Of String)
        If Not String.IsNullOrEmpty(PathEnv) Then Paths.AddRange(PathEnv.Split(";"))
        Paths.Add(PathUtils.ToShortPath(McLaunchJavaSelected.Folder))
        StartInfo.EnvironmentVariables("Path") = Paths.Distinct.Join(";"c)
        StartInfo.EnvironmentVariables("appdata") = PathUtils.ToShortPath(McFolderSelected)

        '设置其他参数
        StartInfo.WorkingDirectory = McInstanceSelected.PathIndie
        StartInfo.UseShellExecute = False
        StartInfo.StandardOutputEncoding = McLaunchEncoding
        StartInfo.StandardErrorEncoding = McLaunchEncoding
        StartInfo.RedirectStandardOutput = True
        StartInfo.RedirectStandardError = True
        StartInfo.CreateNoWindow = True
        StartInfo.Arguments = McLaunchArgument

        '开始进程
        Dim GameProcess = StartProcess(StartInfo)
        McLaunchLog("已启动游戏进程：" & McLaunchJavaSelected.JavaExePath)
        If Loader.IsCanceled Then
            McLaunchLog("由于取消启动，已强制结束游戏进程") '#1631
            GameProcess.Kill()
            GameProcess.Dispose()
            Return
        End If
        Loader.Output = GameProcess
        McLaunchProcess = GameProcess

        '进程优先级处理
        Try
            GameProcess.PriorityBoostEnabled = True
            Select Case Settings.Get(Of Integer)("LaunchArgumentPriority")
                Case 0 '高
                    GameProcess.PriorityClass = ProcessPriorityClass.AboveNormal
                Case 2 '低
                    GameProcess.PriorityClass = ProcessPriorityClass.BelowNormal
                Case Else '中
            End Select
        Catch ex As Exception
            Logger.Error(ex, "设置进程优先级失败")
        End Try

    End Sub
    Private Sub McLaunchWait(Loader As LoaderTask(Of Process, Integer))

        '输出信息
        McLaunchLog("")
        McLaunchLog("~ 基础参数 ~")
        McLaunchLog("PCL Linking 版本：" & VersionDisplay & " (" & VersionCode & ")")
        McLaunchLog($"游戏版本：{McInstanceSelected.VersionDisplayName}（{McInstanceSelected.Version.Vanilla}，Drop {McInstanceSelected.Version.Drop}{If(McInstanceSelected.Version.Reliable, "", "，无法完全确定")}）")
        McLaunchLog("资源版本：" & McAssetsGetIndexName(McInstanceSelected))
        McLaunchLog("版本继承：" & If(McInstanceSelected.InheritName = "", "无", McInstanceSelected.InheritName))
        McLaunchLog("分配的内存：" & PageInstanceSetup.GetRam(McInstanceSelected) & " GB（" & Math.Round(PageInstanceSetup.GetRam(McInstanceSelected) * 1024) & " MB）")
        McLaunchLog("MC 文件夹：" & McFolderSelected)
        McLaunchLog("版本文件夹：" & McInstanceSelected.PathVersion)
        McLaunchLog("版本隔离：" & (McInstanceSelected.PathIndie = McInstanceSelected.PathVersion))
        McLaunchLog("HMCL 格式：" & McInstanceSelected.IsHmclFormatJson)
        McLaunchLog("Java 信息：" & If(McLaunchJavaSelected IsNot Nothing, McLaunchJavaSelected.ToString, "无可用 Java"))
        McLaunchLog("Natives 文件夹：" & GetNativesFolder())
        McLaunchLog("")
        McLaunchLog("~ 登录参数 ~")
        McLaunchLog("玩家用户名：" & McLoginLoader.Output.Name)
        McLaunchLog("AccessToken：" & McLoginLoader.Output.AccessToken)
        McLaunchLog("ClientToken：" & McLoginLoader.Output.ClientToken)
        McLaunchLog("UUID：" & McLoginLoader.Output.Uuid)
        McLaunchLog("登录方式：" & McLoginLoader.Output.Type)
        McLaunchLog("")

        '获取窗口标题
        Dim WindowTitle As String = Settings.Get(Of String)("VersionArgumentTitle", Instance:=McInstanceSelected)
        If WindowTitle = "" Then WindowTitle = Settings.Get(Of String)("LaunchArgumentTitle")
        WindowTitle = ArgumentReplace(WindowTitle, ReplaceTime:=False)

        '初始化等待
        Dim Watcher As New Watcher(Loader, McInstanceSelected, WindowTitle)
        McLaunchWatcher = Watcher

        '等待
        Do While Watcher.State = Watcher.MinecraftState.Loading
            Thread.Sleep(100)
        Loop
        If Watcher.State = Watcher.MinecraftState.Crashed Then
            Throw New OperationCanceledException
        End If

    End Sub
    Private Sub McLaunchEnd()
        McLaunchLog("开始启动结束处理")

        '暂停或开始音乐播放
        If Settings.Get(Of Boolean)("UiMusicStop") Then
            RunInUi(Sub() If MusicPause() Then Logger.Info("已根据设置，在启动后暂停音乐播放"))
        ElseIf Settings.Get(Of Boolean)("UiMusicStart") Then
            RunInUi(Sub() If MusicResume() Then Logger.Info("已根据设置，在启动后开始音乐播放"))
        End If

        '启动器可见性
        McLaunchLog("启动器可见性：" & Settings.Get(Of Integer)("LaunchArgumentVisible"))
        Select Case Settings.Get(Of Integer)("LaunchArgumentVisible")
            Case 0
                '直接关闭
                McLaunchLog("已根据设置，在启动后关闭启动器")
                RunInUi(Sub() FrmMain.EndProgram(False))
            Case 2, 3
                '隐藏
                McLaunchLog("已根据设置，在启动后隐藏启动器")
                RunInUi(Sub() FrmMain.Hidden = True)
            Case 4
                '最小化
                McLaunchLog("已根据设置，在启动后最小化启动器")
                RunInUi(Sub() FrmMain.WindowState = WindowState.Minimized)
            Case 5
                '啥都不干
        End Select

        '启动计数
        Settings.Set("SystemLaunchCount", Settings.Get(Of Integer)("SystemLaunchCount") + 1)

    End Sub

    Private Sub McLaunchAutoUpdateMods(Loader As LoaderTask(Of Integer, Integer))
        Try
            If Loader.IsCanceled Then Return
            Dim TargetInstance = McInstanceSelected
            If TargetInstance Is Nothing Then Return
            McLaunchLog("开始启动前自动检查并更新 Mod...")
            Dim Scan = ScanInstanceModsWithCache(TargetInstance)
            If Not Scan.ModList.Any() Then
                McLaunchLog("当前实例未安装 Mod，跳过自动更新")
                Return
            End If
            Dim SyncLoader As New LoaderTask(Of (ModList As List(Of LocalResourceFile), Cache As JObject, Target As McVersion), Integer)(
                "启动前检测 Mod 更新", AddressOf LocalResourceOnlineLoad)
            Using Cts As New CancellationTokenSource(30000)
                SyncLoader.WaitForExit(Input:=(Scan.ModList, Scan.Cache, TargetInstance.Version), c:=Cts.Token)
            End Using
            If Loader.IsCanceled Then Return

            Dim ToUpdate = Scan.ModList.Where(Function(m) m.IsEnabled AndAlso m.UpdateFile IsNot Nothing AndAlso m.UpdateFile.DownloadAvailable AndAlso m.ProjectVersion IsNot Nothing).ToList()
            If Not ToUpdate.Any() Then
                McLaunchLog("启动前检查完成：所有已启用 Mod 均为最新版")
                Return
            End If

            McLaunchLog($"检测到 {ToUpdate.Count} 个 Mod 有新版本，开始自动下载更新...")
            Dim FileCopyList As Dictionary(Of String, String) = Nothing
            Dim FileList = PageInstanceMod.BuildModUpdateFiles(ToUpdate, TargetInstance, FileCopyList)
            If FileList.Any() Then
                Dim DlLoader As New LoaderDownload("下载新版 Mod", FileList)
                DlLoader.WaitForExit()
                If DlLoader.State = LoadState.Finished AndAlso Not Loader.IsCanceled Then
                    Dim UpdatedNames As New List(Of String)
                    PageInstanceMod.ApplyDownloadedModUpdates(ToUpdate, FileCopyList, UpdatedNames)
                    McLaunchLog($"启动前已自动更新 {UpdatedNames.Count} 个 Mod：{UpdatedNames.Join("、")}")
                    If UpdatedNames.Any() Then Hint($"启动前已自动更新 {UpdatedNames.Count} 个 Mod", HintType.Green)
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "启动前自动更新 Mod 失败（已跳过以避免阻塞启动）")
            McLaunchLog($"启动前自动更新 Mod 异常（已跳过）：{ex.Message}")
        End Try
    End Sub

    Private Sub McLaunchAutoUpdateAssets(Loader As LoaderTask(Of Integer, Integer))
        Try
            If Loader.IsCanceled Then Return
            Dim TargetInstance = McInstanceSelected
            If TargetInstance Is Nothing Then Return
            McLaunchLog("开始启动前自动检查已知资源包与光影更新...")
            Dim ToUpdate = PageInstanceAssets.ScanAndCheckAssetUpdates(TargetInstance, OnlyEnabled:=True)
            If Loader.IsCanceled Then Return
            If Not ToUpdate.Any() Then
                McLaunchLog("启动前检查完成：所有已识别材质与光影均为最新版")
                Return
            End If
            McLaunchLog($"检测到 {ToUpdate.Count} 个已知材质/光影有新版本，开始自动下载更新...")
            Dim UpdatedNames = PageInstanceAssets.ApplyAssetUpdates(TargetInstance, ToUpdate)
            McLaunchLog($"启动前已自动更新 {UpdatedNames.Count} 个材质/光影：{UpdatedNames.Join("、")}")
            If UpdatedNames.Any() Then Hint($"启动前已自动更新 {UpdatedNames.Count} 个材质/光影", HintType.Green)
        Catch ex As Exception
            Logger.Warn(ex, "启动前自动更新材质/光影失败（已跳过以避免阻塞启动）")
            McLaunchLog($"启动前自动更新材质/光影异常（已跳过）：{ex.Message}")
        End Try
    End Sub

    Private Sub McLaunchAutoBackup(Loader As LoaderTask(Of Integer, Integer))
        Try
            If Loader.IsCanceled Then Return
            Dim TargetInstance = McInstanceSelected
            If TargetInstance Is Nothing Then Return
            Dim IncludeSaves = Settings.Get(Of Boolean)("VersionAdvanceAutoBackupSaves", Instance:=TargetInstance)
            Dim KeepCount = Math.Max(1, Settings.Get(Of Integer)("VersionAdvanceAutoBackupKeep", Instance:=TargetInstance))
            Dim Scopes As New List(Of String) From {"mods", "config", "options"}
            If IncludeSaves Then Scopes.Add("saves")

            McLaunchLog($"正在创建启动前自动备份快照（包含存档：{IncludeSaves}，保留 {KeepCount} 份）...")
            Dim Snap = ModSnapshot.CreateSnapshotCore(
                TargetInstance,
                $"[自动备份] {DateTime.Now:MM-dd HH:mm}",
                $"启动前自动备份 ({String.Join("+", Scopes)})",
                Scopes,
                IsAutoBackup:=True)
            Dim Pruned = ModSnapshot.PruneAutoBackups(TargetInstance, KeepCount)
            McLaunchLog($"启动前自动备份完成：{Snap.Name}（{Snap.FileCount} 个文件），已清理旧自动备份 {Pruned} 份")
        Catch ex As Exception
            Logger.Warn(ex, "启动前自动备份快照失败（已跳过以避免阻塞启动）")
            McLaunchLog($"启动前自动备份异常（已跳过）：{ex.Message}")
        End Try
    End Sub

    Private Sub McLaunchStaticModPrecheck(Loader As LoaderTask(Of Integer, Integer))
        If Loader.IsCanceled Then Return
        Dim TargetInstance = McInstanceSelected
        If TargetInstance Is Nothing Then Return
        McLaunchLog("开始启动前 Mod 冲突与前置依赖静态预检...")
        Dim Report = ModPrecheck.RunInstanceModPrecheck(TargetInstance)
        If Loader.IsCanceled Then Return
        If Not Report.HasIssues Then
            McLaunchLog($"Mod 静态预检通过（已扫描 {Report.ScannedModCount} 个启用 Mod）")
            Return
        End If

        Dim Summary = Report.FormatReport(TargetInstance)
        McLaunchLog($"启动前 Mod 静态预检发现潜在问题：{vbCrLf}{Summary}", LogLevel.Warn)
        Dim ActionTaken As Integer = 0
        RunInUiWait(
        Sub()
            If Report.AutoDisableCandidates.Any() Then
                Dim Choice = MyMsgBox(Summary & vbCrLf & vbCrLf &
                                      $"发现 {Report.AutoDisableCandidates.Count} 个可自动禁用的问题 Mod，建议先处理以免游戏崩溃：",
                                      "启动前 Mod 冲突预检预警",
                                      "一键禁用问题 Mod 并继续启动", "忽略并强制启动", "取消启动去排查", IsWarn:=True)
                If Choice = 1 Then
                    Dim DisabledCnt As Integer = 0
                    For Each TargetPath In Report.AutoDisableCandidates
                        Try
                            Dim NewPath = PathUtils.RemoveExtendedPrefix(TargetPath) & ".disabled"
                            If Not IO.File.Exists(NewPath) AndAlso IO.File.Exists(TargetPath) Then
                                FileUtils.Move(TargetPath, NewPath)
                                DisabledCnt += 1
                            End If
                        Catch ex As Exception
                            Logger.Warn(ex, $"禁用问题 Mod 失败：{TargetPath}")
                        End Try
                    Next
                    If DisabledCnt > 0 Then Hint($"已自动禁用 {DisabledCnt} 个问题 Mod，继续启动游戏…", HintType.Green)
                    ActionTaken = 1
                ElseIf Choice = 2 Then
                    ActionTaken = 2
                Else
                    ActionTaken = 3
                End If
            Else
                Dim Choice = MyMsgBox(Summary, "启动前 Mod 冲突预检预警", "忽略并强制启动", "取消启动去排查", IsWarn:=True)
                ActionTaken = If(Choice = 1, 2, 3)
            End If
        End Sub)
        If ActionTaken = 3 OrElse ActionTaken = 0 Then
            Throw New OperationCanceledException("已取消启动以排查 Mod 冲突")
        End If
    End Sub

#End Region

End Module
