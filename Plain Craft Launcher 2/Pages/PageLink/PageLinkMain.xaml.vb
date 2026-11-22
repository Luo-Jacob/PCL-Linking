Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Net.Sockets
Imports System.Reflection
Imports PCL

Public Class PageLinkMain
    'v3：邀请码末尾追加 4 位验证码校验位，房主与加入者都用它派生网络密钥
    Private Const INVITE_CODE_VERSION As Integer = 3

    '===============================
    '  状态机与前端页面
    '===============================

#Region "状态管理"

    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum
    Public Shared LinkState As LinkStates = LinkStates.Waiting
    Private IsUserInitiatedExit As Boolean = False
    Private IsAutoReconnecting As Boolean = False
    Private AutoReconnectAttempts As Integer = 0
    Private HasInitializedMemberTracker As Boolean = False
    Private ReadOnly MemberTrackerLock As New Object()
    Private ReadOnly KnownPeerDisplayNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly PingHistory As New Queue(Of Double)()

    ''' <summary>
    ''' 切换到指定的状态。
    ''' </summary>
    Private Sub ChangeState(NewState As LinkStates)
        If LinkState = NewState Then Return
        Dim OldState = LinkState
        LinkState = NewState
        Logger.Info($"主状态由 {OldState} 变更为 {NewState}")
        '触发状态切换
        RunInUi(
        Sub()
            Select Case NewState
                Case LinkStates.Waiting
                    SwitchToWaiting(OldState)
                Case LinkStates.Loading
                    SwitchToLoading(OldState)
                Case LinkStates.Failed
                    SwitchToFailed(OldState)
                Case LinkStates.Finished
                    SwitchToFinished(OldState)
            End Select
        End Sub)
    End Sub

    '让加载器的 成功/失败 事件触发状态切换
    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanContent, PanAlways, LinkLoader, AutoRun:=False)
        AddHandler LinkLoader.OnStateChangedUi,
        Sub(Loader As LoaderBase, NewState As LoadState, OldState As LoadState)
            Select Case NewState
                Case LoadState.Finished
                    ChangeState(LinkStates.Finished)
                Case LoadState.Failed
                    Telemetry("联机失败", "Exception", FilterUserName(Loader.Error.GetDisplay(True), "*"))
                    ChangeState(LinkStates.Failed)
            End Select
        End Sub
    End Sub

    '页面动画兼容
    '注意：PageEnter 是在页面切换动画的回调（AaCode）里触发的，这里面绝对不能弹模态框。
    'MyMsgBox 内部用的是 Dispatcher.PushFrame，在渲染回调里开嵌套消息循环会让界面彻底卡死，
    '而且弹窗本身也画不出来。所以这里只做可见性切换，提权提示改为页面里的常驻提示条。
    Private Sub UpdatePanelVisibility() Handles Me.PageEnter
        '这里一旦抛异常，PageOnEnter 会中断在触发进入动画之前，页面会一直保持不可见（看起来像卡死），
        '所以整个处理器包一层保护。
        Try
            FrmLinkMain.PanSelect.Visibility = If(LinkState = LinkStates.Waiting, Visibility.Visible, Visibility.Collapsed)
            FrmLinkMain.PanFinish.Visibility = If(LinkState = LinkStates.Finished, Visibility.Visible, Visibility.Collapsed)
            If LinkState = LinkStates.Waiting Then
                RefreshLinkBookmarksUi()
                RefreshLinkOverviewStatus()
            End If
            '自动联机：记住上次加入的房间，进入本页时自动再连一次。
            '这里在动画回调里，不能弹窗也不能阻塞，所以只把加入请求丢到 UI 队列里排队执行。
            If LinkState = LinkStates.Waiting AndAlso Not IsAutoReconnecting AndAlso Not HasPendingNavigationAction AndAlso AutoJoinCode Is Nothing AndAlso AutoCreatePort <= 0 AndAlso Settings.Get(Of Boolean)("LinkAutoJoin") Then
                Dim AutoCode As String = Settings.Get(Of String)("LinkLastAutoJoinInviteCode")
                If Not String.IsNullOrWhiteSpace(AutoCode) Then
                    Dispatcher.BeginInvoke(
                        New Action(
                        Sub()
                            If LinkState <> LinkStates.Waiting Then Return
                            Dim CodeError As String = ValidateCodeFormat(AutoCode)
                            If CodeError IsNot Nothing Then
                                Hint("自动联机失败：" & CodeError, HintType.Red)
                                Return
                            End If
                            Dim CodeForLog As String = If(AutoCode.Length > 23, AutoCode.Substring(0, 23), AutoCode)
                            Logger.Info($"自动联机：尝试加入上次的房间（{CodeForLog}）")
                            JoinInternal(AutoCode, Settings.Get(Of String)("LinkLastAutoJoinVerifyCode"))
                        End Sub), Windows.Threading.DispatcherPriority.Background)
                End If
            End If
        Catch ex As Exception
            Logger.Error(ex, "联机页 PageEnter 处理失败")
        End Try
    End Sub

    '（PCL Linking：改用 --no-tun 后联机不再需要管理员权限，原提权检查逻辑已整体移除）

#End Region

#Region "初始页面 (Waiting)"

    ''' <summary>
    ''' 邀请码里的原始网络密钥（不含密码）。邀请码只携带这个，真正的密钥由验证码派生。
    ''' </summary>
    Private BaseNetworkSecret As String = ""
    ''' <summary>
    ''' 本次房间的联机密码 / 验证码。空字符串表示旧版无密码房间。
    ''' </summary>
    Private RoomVerifyCode As String = ""

    ''' <summary>
    ''' 从验证码生成 4 位校验标记，写进邀请码供加入者当场核对（不泄露密码本身）。
    ''' </summary>
    Private Function VerifyChecksum(VerifyCode As String) As String
        Dim Bytes = Text.Encoding.UTF8.GetBytes($"LML-CHECK|{VerifyCode}")
        Using Sha = System.Security.Cryptography.SHA256.Create()
            Dim Hash = Sha.ComputeHash(Bytes)
            Return BitConverter.ToString(Hash).Replace("-", "").Substring(0, 4).ToUpperInvariant()
        End Using
    End Function

    ''' <summary>
    ''' 用验证码派生真正的 EasyTier 网络密钥。
    ''' 这样即使有人绕过了界面校验，密码不对也连不进房间。
    ''' </summary>
    Private Function DeriveNetworkSecret(BaseSecret As String, VerifyCode As String) As String
        If String.IsNullOrEmpty(VerifyCode) Then Return BaseSecret
        Dim Bytes = Text.Encoding.UTF8.GetBytes($"LML-SECRET|{BaseSecret}|{VerifyCode}")
        Using Sha = System.Security.Cryptography.SHA256.Create()
            Dim Hash = Sha.ComputeHash(Bytes)
            Return BitConverter.ToString(Hash).Replace("-", "").Substring(0, 24).ToLowerInvariant()
        End Using
    End Function

    ''' <summary>
    ''' 每次创建 / 加入房间前必须点击同意的联机协议。
    ''' 只能在普通 UI 事件里调用：MyMsgBox 用 Dispatcher.PushFrame，页面动画回调里会卡死界面。
    ''' </summary>
    Private Function ConfirmLinkAgreement() As Boolean
        Dim Text As String =
            "PCL Linking 仅提供点对点网络连接，不创建、不管理、不监管任何联机房间。" & vbCrLf & vbCrLf &
            "根据《中华人民共和国网络安全法》《中华人民共和国未成年人保护法》等规定，" &
            "你在房间内的言论、分享内容与游戏行为由你本人独立承担全部法律责任；" &
            "不得利用本功能制作、发布、传播违法信息，不得侵害他人合法权益，" &
            "不得组织或诱导未成年人接触不适宜内容。" & vbCrLf & vbCrLf &
            "点击「同意」即表示你已阅读并接受上述约定。"
        Return MyMsgBox(Text, "联机协议", "同意", "取消") = 1
    End Function

    '由任意状态切换到 Waiting
    Private Sub SwitchToWaiting(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        SyncLock LinkLoader.LockState
            LinkLoader.State = LoadState.Waiting
        End SyncLock
        ProcessStop()
        '页面切换
        If OldState <> LinkStates.Finished Then
            '从完成页面退出时，PageOnContentExit 结束会触发 PageEnter 事件，这会调用 UpdatePanelVisibility
            '如果在现在提前调用了，会丢失完成页面的退出动画
            UpdatePanelVisibility()
        End If
        PageOnContentExit()
    End Sub

    '===============================
    '  页面 UI
    '===============================

    '创建
    Private Sub Create_MouseLeftButtonUp() Handles PanSelectCreate.MouseLeftButtonUp
        '输入端口号
        Dim Port As String = MyMsgBoxInput("输入端口", $"在单人游戏的暂停菜单选择 {vbLQ}对局域网开放{vbRQ}，然后输入端口数字。{vbCrLf}甚至可以输入其他游戏的端口……嗯……",
            ValidateRules:=New Collection(Of Validate) From {New ValidateInteger(1024, 65535)},
            HintText:="端口号")
        If Port Is Nothing Then Return
        StartCreateRoomWithPort(Integer.Parse(Port))
    End Sub

    Private Shared AutoCreatePort As Integer = 0

    Public Sub StartCreateRoomWithPort(Port As Integer)
        If LinkState <> LinkStates.Waiting Then Return
        '每次开始联机都要重新确认一次协议
        If Not ConfirmLinkAgreement() Then Return
        IsUserInitiatedExit = False
        IsAutoReconnecting = False
        AutoReconnectAttempts = 0
        '基础信息
        IsServerSide = True
        ServerPort = Port
        Dim GenerateRandomCode =
        Function() As String
            Dim Result As String = ""
            For i = 1 To 5
                Result &= CryptoRandomInt(36).ConvertRadix(36)
            Next
            Return Result.Replace("O", "0").Replace("I", "1")
        End Function
        Dim UseSticky = Settings.Get(Of Boolean)("LinkStickyRoom")
        Dim StickySuffix As String = If(UseSticky, Settings.Get(Of String)("LinkStickySuffix"), "")
        Dim StickySecret As String = If(UseSticky, Settings.Get(Of String)("LinkStickySecret"), "")
        Dim StickyVerify As String = If(UseSticky, Settings.Get(Of String)("LinkStickyVerify"), "")
        Dim SuffixCode = If(StickySuffix IsNot Nothing AndAlso StickySuffix.Length = 5, StickySuffix, GenerateRandomCode())
        NetworkName = $"P{ServerPort.ConvertRadix(16).PadLeft(4, "0"c)}-{SuffixCode}"
        BaseNetworkSecret = If(StickySecret IsNot Nothing AndAlso StickySecret.Length = 5, StickySecret, GenerateRandomCode())
        '联机密码：留空则自动生成一个 6 位验证码，房主把它和邀请码分开发给朋友
        Dim Password As String = MyMsgBoxInput("设置联机密码",
            $"目标端口：{ServerPort}{If(UseSticky AndAlso StickySuffix IsNot Nothing AndAlso StickySuffix.Length = 5, "（已启用固定车队房间码）", "")}{vbCrLf}留空将自动生成一个 6 位验证码。{vbCrLf}房主需要把邀请码和验证码一起发给朋友；两者都对才能进入房间。",
            DefaultInput:=StickyVerify,
            HintText:="可留空（自动生成）")
        If Password Is Nothing Then Return
        Password = Password.Trim
        RoomVerifyCode = If(Password = "", (100000 + CryptoRandomInt(900000)).ToString("000000"), Password)
        If UseSticky Then
            Settings.Set("LinkStickySuffix", SuffixCode)
            Settings.Set("LinkStickySecret", BaseNetworkSecret)
            Settings.Set("LinkStickyVerify", RoomVerifyCode)
        End If
        NetworkSecret = DeriveNetworkSecret(BaseNetworkSecret, RoomVerifyCode)
        DiscoverNodeID = -1
        Logger.Info($"尝试创建房间，网络名 {NetworkName}，端口 {ServerPort}，验证码校验位 {VerifyChecksum(RoomVerifyCode)}")
        '启动
        ChangeState(LinkStates.Loading)
    End Sub

    Public Shared Sub CreateRoomFromLocalServer(Port As Integer)
        If LinkState <> LinkStates.Waiting Then
            If LinkState = LinkStates.Finished AndAlso FrmLinkMain IsNot Nothing AndAlso FrmLinkMain.IsServerSide AndAlso FrmLinkMain.ServerPort = Port Then
                FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
                Hint($"端口 {Port} 的联机房间已在运行，可点击「发给好友」或「复制邀请码」！", HintType.Green)
            Else
                Hint("当前已在联机中或正在创建房间，请先退出当前房间后再共享！", HintType.Red)
                FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
            End If
            Return
        End If
        If FrmMain.PageCurrent = FormMain.PageType.Link AndAlso FrmLinkMain IsNot Nothing Then
            FrmLinkMain.StartCreateRoomWithPort(Port)
        Else
            AutoCreatePort = Port
            FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
        End If
    End Sub

    '加入
    Private Shared LastCode As String = Nothing
    ''' <summary>
    ''' 输入邀请码，切换到联机页并立即加入房间。
    ''' </summary>
    Public Shared Sub Join() Handles PanSelectJoin.MouseLeftButtonUp
        Dim Code As String = MyMsgBoxInput("输入邀请码", "输入房主发给你的邀请码，或直接粘贴整段带验证码的邀请文案。",
            HintText:=If(String.IsNullOrEmpty(LastCode), "", "使用上一次的邀请码"))
        If Not String.IsNullOrEmpty(LastCode) AndAlso Code IsNot Nothing AndAlso Code = "" Then Code = LastCode
        If Code Is Nothing Then Return
        Dim ExCode As String = ""
        Dim ExVerify As String = ""
        If PageFriends.TryExtractInviteFromText(Code, ExCode, ExVerify) Then
            Join(ExCode, If(String.IsNullOrEmpty(ExVerify), Nothing, ExVerify))
        Else
            Join(Code)
        End If
    End Sub
    ''' <param name="KnownVerifyCode">自动联机时传入上次保存的验证码，避免重复询问。</param>
    Public Sub JoinInternal(Code As String, Optional KnownVerifyCode As String = Nothing)
        If LinkState <> LinkStates.Waiting Then Return
        If Code Is Nothing Then
            AutoLaunchGameAfterJoin = False
            Return
        End If
        IsUserInitiatedExit = False
        IsAutoReconnecting = False
        AutoReconnectAttempts = 0
        '每次开始联机都要重新确认一次协议
        If Not ConfirmLinkAgreement() Then
            AutoLaunchGameAfterJoin = False
            Return
        End If
        If LinkState <> LinkStates.Waiting Then Return
        '基础格式校验
        Dim ValidateResult = ValidateCodeFormat(Code)
        If ValidateResult IsNot Nothing Then
            AutoLaunchGameAfterJoin = False
            Hint(ValidateResult, HintType.Red)
            Return
        End If
        Code = FixCodeFormat(Code)
        Logger.Info($"实际使用的邀请码：{If(Code.Length > 11, Code.Substring(0, 11) & "-***", Code)}")
        '基础信息
        IsServerSide = False
        ServerPort = Code.Substring(1, 4).Upper.ConvertRadix(16, 10)
        NetworkName = Code.Substring(0, 11)
        BaseNetworkSecret = Code.Substring(12, 5)
        Dim HasPassword = Code.Length >= 28 AndAlso Val(Code.Substring(18, 2)) >= 3
        If HasPassword Then
            Dim VerifyCode As String = KnownVerifyCode
            If String.IsNullOrEmpty(VerifyCode) Then VerifyCode = Nothing '旧配置没有存过验证码，改为询问
            If VerifyCode Is Nothing Then
                VerifyCode = MyMsgBoxInput("输入联机验证码",
                    $"这个房间设有联机密码。{vbCrLf}请向房主索取与邀请码一起给出的验证码（或房主自定义的密码）。",
                    HintText:="验证码 / 联机密码")
                If LinkState <> LinkStates.Waiting Then Return
                If VerifyCode Is Nothing Then
                    AutoLaunchGameAfterJoin = False
                    Return
                End If
                VerifyCode = VerifyCode.Trim
            End If
            If VerifyCode = "" Then
                AutoLaunchGameAfterJoin = False
                Hint("验证码不能为空！", HintType.Red)
                Return
            End If
            If VerifyChecksum(VerifyCode) <> Code.Substring(24, 4) Then
                AutoLaunchGameAfterJoin = False
                Hint("验证码错误，请向房主确认后再试！", HintType.Red)
                Return
            End If
            RoomVerifyCode = VerifyCode
        Else
            '旧版邀请码，没有密码
            RoomVerifyCode = ""
        End If
        NetworkSecret = DeriveNetworkSecret(BaseNetworkSecret, RoomVerifyCode)
        If Code.Substring(20, 3) = "000" Then
            DiscoverNodeID = -2
        Else
            DiscoverNodeID = Code.Substring(20, 3).Upper.ConvertRadix(16, 10)
        End If
        Logger.Info($"尝试加入房间，网络名 {NetworkName}，端口 {ServerPort}，发现节点 {DiscoverNodeID}")
        '记住这次加入的房间，供「自动联机」与「历史/收藏夹」直接使用
        Settings.Set("LinkLastAutoJoinInviteCode", Code)
        Settings.Set("LinkLastAutoJoinVerifyCode", RoomVerifyCode)
        ModLink.RecordJoinedRoomBookmark(Code, RoomVerifyCode)
        RunInUi(Sub() RefreshLinkBookmarksUi())
        '启动
        LastCode = Code
        ChangeState(LinkStates.Loading)
    End Sub
    Public Shared Function ValidateCodeFormat(Code As String) As String
        If String.IsNullOrWhiteSpace(Code) Then Return "邀请码为空！"
        Code = FixCodeFormat(Code)
        '判断类型
        If Code.StartsWithF("U/") Then 'HMCL
            Return "请让房主使用 PCL Linking 创建房间！"
        ElseIf Code.Length = 10 Then 'PCL CE
            Return "请让房主使用非社区版的 PCL Linking 创建房间！"
        End If
        If Not (Code.Length >= 17 AndAlso Code(0) = "P"c AndAlso Code(5) = "-"c AndAlso Code(11) = "-"c) Then
            Return "邀请码有误，请让房主使用 PCL Linking 创建房间！"
        End If
        Dim IsHex = Function(s As String) s.All(Function(c) Char.IsDigit(c) OrElse (c >= "A"c AndAlso c <= "F"c) OrElse (c >= "a"c AndAlso c <= "f"c))
        If Not IsHex(Code.Substring(1, 4)) Then Return "邀请码端口段格式有误！"
        '校验长度与版本/发现节点段（v2 为 23 位，v3 含校验位为 28 位）
        If Not ((Code.Length = 23 OrElse Code.Length = 28) AndAlso Code(17) = "-"c) Then
            Return "邀请码长度或格式有误，请让房主重新复制邀请码！"
        End If
        If Not Code.Substring(18, 2).All(Function(c) Char.IsDigit(c)) Then Return "邀请码版本号格式有误！"
        If Not IsHex(Code.Substring(20, 3)) Then Return "邀请码节点段格式有误！"
        Dim CodeVer = Val(Code.Substring(18, 2))
        If CodeVer > INVITE_CODE_VERSION Then Return "你的 PCL Linking 版本太老了，请在更新 PCL Linking 之后再联机！"
        'v3 起，末尾必须有 4 位验证码校验位
        If CodeVer >= 3 Then
            If Code.Length <> 28 OrElse Code(23) <> "-"c OrElse Not IsHex(Code.Substring(24, 4)) Then
                Return "邀请码的验证码校验位缺失或有误，请让房主重新复制邀请码！"
            End If
        ElseIf Code.Length <> 23 Then
            Return "邀请码格式有误，请让房主重新复制邀请码！"
        End If
        Return Nothing
    End Function
    Private Shared Function FixCodeFormat(Code As String) As String
        Code = If(Code, "").Trim().Between("【", "】").Between("[", "]").Trim() '从完整消息中提取并去空白
        Code = Code.Upper.Replace("O", "0").Replace("I", "1") '输入修正
        '版本 1 兼容（固定 17 位旧码）
        If Code.Length = 17 Then
            Code &= "-0105E"
        End If
        Return Code
    End Function

    '自定义节点：公共节点随时可能失效，给用户留一个应急入口
    '（原 PageSetupLink 设置页已随导航精简删除，LinkCustomPeer 因此没有 UI，这里补上）
    Private Sub RefreshLinkOverviewStatus()
        If LabLobbyNetworkStatus Is Nothing Then Return
        Dim CustomRaw = If(Settings.Get(Of String)("LinkCustomPeer"), "").Trim()
        Dim CustomList = CustomRaw.Split({","c, ";"c, vbCr(0), vbLf(0)}, StringSplitOptions.RemoveEmptyEntries)
        Dim NodeText = If(CustomList.Any(), $"自定义节点 ({CustomList.Length} 个)", If(Settings.Get(Of Boolean)("LinkPeerPool"), "社区节点池（自动择优）", "内置节点"))
        Dim ProtoText = If(Settings.Get(Of Integer)("LinkProtocol") = 1, "UDP 优先", "TCP 优先")
        Dim ReconnectText = If(Settings.Get(Of Boolean)("LinkAutoReconnect"), "断线自动重连", "手动重连")
        LabLobbyNetworkStatus.Text = $"节点：{NodeText}  ·  协议：{ProtoText}  ·  {ReconnectText}"
    End Sub

    Private Sub BtnOpenLinkSetup_Click() Handles BtnOpenLinkSetup.Click
        FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLink)
    End Sub

    Private Sub BtnCustomPeer_Click() Handles BtnCustomPeer.Click
        Dim Current As String = Settings.Get(Of String)("LinkCustomPeer")
        Dim Input As String = MyMsgBoxInput("自定义节点",
            $"每行一个节点地址，例如：{vbLQ}tcp://1.2.3.4:11010{vbRQ}。{vbCrLf}留空则使用内置公共节点。房主与所有加入者必须填写相同的节点。",
            DefaultInput:=Current, HintText:="节点地址")
        If Input Is Nothing Then Return
        Settings.Set("LinkCustomPeer", Input.Trim)
        RefreshLinkOverviewStatus()
        Hint(If(Input.Trim = "", "已恢复使用内置公共节点。", "自定义节点已保存，下次联机时生效。"), HintType.Green)
    End Sub

    Private Sub BtnTestPeers_Click() Handles BtnTestPeers.Click
        Hint("正在并发测速内置与社区中继节点（约 1~2 秒）……", HintType.Blue)
        RunInNewThread(
        Sub()
            Try
                Dim Tested = ModLink.TestPeersLatency(True, 1500)
                Dim Sb As New Text.StringBuilder()
                Sb.AppendLine($"共测试 {Tested.Count} 个节点（TCP 握手延迟，单位 ms）：")
                Sb.AppendLine()
                For Each Item In Tested
                    Dim Badge = If(Item.Reachable, $"[{Item.LatencyMs} ms]", "[超时/不可达]")
                    Sb.AppendLine($"• {Badge} {Item.Peer}")
                Next
                Dim Fastest3 = Tested.Where(Function(r) r.Reachable).Take(3).Select(Function(r) r.Peer).ToList()
                RunInUi(
                Sub()
                    If Fastest3.Any() Then
                        Dim Choice = MyMsgBox(Sb.ToString().TrimEnd(), "EasyTier 中继节点测速报告",
                                              "应用最快前 3 节点", "恢复默认节点池", "关闭")
                            If Choice = 1 Then
                                Settings.Set("LinkCustomPeer", String.Join("; ", Fastest3))
                                RefreshLinkOverviewStatus()
                                Hint($"已将最快 {Fastest3.Count} 个节点写入自定义节点，注意房主与好友需使用相同中继节点！", HintType.Green)
                            ElseIf Choice = 2 Then
                                Settings.Set("LinkCustomPeer", "")
                                RefreshLinkOverviewStatus()
                                Hint("已恢复使用默认「社区节点池 + 内置兜底」策略。", HintType.Green)
                            End If
                        Else
                            Dim Choice = MyMsgBox(Sb.ToString().TrimEnd() & vbCrLf & vbCrLf & "未能探测到可达的公共节点，请检查网络或在「自定义节点」填入自建节点。",
                                                  "EasyTier 中继节点测速报告", "恢复默认节点池", "关闭", IsWarn:=True)
                            If Choice = 1 Then
                                Settings.Set("LinkCustomPeer", "")
                                RefreshLinkOverviewStatus()
                            End If
                        End If
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "节点测速失败")
                RunInUi(Sub() Hint($"节点测速失败：{ex.Message}", HintType.Red))
            End Try
        End Sub, "Link Peer Latency Test")
    End Sub

    Private Sub BtnScanLanWorlds_Click() Handles BtnScanLanWorlds.Click
        If LinkState <> LinkStates.Waiting Then Return
        Hint("正在扫描本机 Minecraft 局域网广播（4445 端口）与本地服务端……", HintType.Blue)
        RunInNewThread(
            Sub()
                Dim Worlds = ModLink.ScanLocalLanWorlds(2200)
                RunInUi(
                    Sub()
                        If LinkState <> LinkStates.Waiting Then Return
                        If Not Worlds.Any() Then
                            Hint("未扫描到开放中的局域网存档。请先在游戏中点击「对局域网开放」或开启本地开服后再试。", HintType.Red)
                            Return
                        End If
                        If Worlds.Count = 1 Then
                            Dim Only = Worlds(0)
                            If MyMsgBox($"检测到局域网世界「{Only.Motd}」（{Only.SourceLabel}，端口 {Only.Port}），是否立即以此端口创建联机房间？",
                                        "发现本机局域网世界", "立即创建房间", "取消") = 1 Then
                                StartCreateRoomWithPort(Only.Port)
                            End If
                        Else
                            Dim Radios As New List(Of IMyRadio)
                            For Each W In Worlds
                                Radios.Add(New MyRadioBox With {.Text = $"[{W.SourceLabel}] {W.Motd}（端口 {W.Port}）"})
                            Next
                            Dim SelectedIdx = MyMsgBoxSelect(Radios.ToArray(), "发现以下本机局域网 / 本地开服端口，请选择要共享的房间：", "创建联机房间", "取消")
                            If SelectedIdx IsNot Nothing Then
                                StartCreateRoomWithPort(Worlds(SelectedIdx.Value).Port)
                            End If
                        End If
                    End Sub)
            End Sub, "Scan Local LAN Worlds")
    End Sub

    Private Sub BtnQuickJoinClipboard_Click() Handles BtnQuickJoinClipboard.Click
        If LinkState <> LinkStates.Waiting Then
            Hint("当前已在联机状态中，请先退出当前房间后再试。", HintType.Red)
            Return
        End If
        Dim ClipText As String = ""
        Try
            If Clipboard.ContainsText() Then ClipText = Clipboard.GetText()
        Catch ex As Exception
            Hint($"读取剪贴板失败：{ex.Message}", HintType.Red)
            Return
        End Try
        Dim ExCode As String = ""
        Dim ExVerify As String = ""
        If Not PageFriends.TryExtractInviteFromText(ClipText, ExCode, ExVerify) Then
            Hint("剪贴板内容未检测到有效的 PCL Linking 联机邀请码。", HintType.Red)
            Return
        End If
        Hint($"已从剪贴板识别邀请码 {ExCode}{If(String.IsNullOrEmpty(ExVerify), "", "（含验证码）")}，正在准备加入……", HintType.Green)
        Join(ExCode, If(String.IsNullOrEmpty(ExVerify), Nothing, ExVerify))
    End Sub

    '自动加入

    ''' <summary>
    ''' 切换到联机页并立即加入指定房间。VerifyCode 为好友邀请里附带的联机验证码。
    ''' </summary>
    Public Shared Sub Join(Code As String, Optional VerifyCode As String = Nothing, Optional AutoLaunchGame As Boolean = False)
        If LinkState <> LinkStates.Waiting Then
            If AutoLaunchGame AndAlso LinkState = LinkStates.Finished AndAlso FrmLinkMain IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(CurrentClientAddress) Then
                LaunchConnectedRoomGame()
            Else
                Hint("你已经在联机房间中了！", HintType.Red)
            End If
        ElseIf FrmMain.PageCurrent = FormMain.PageType.Link Then
            AutoLaunchGameAfterJoin = AutoLaunchGame
            FrmLinkMain.JoinInternal(Code, VerifyCode)
        Else
            AutoJoinCode = Code
            AutoJoinVerifyCode = VerifyCode
            AutoLaunchGameAfterJoin = AutoLaunchGame
            FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
        End If
    End Sub

    Public Shared AutoLaunchGameAfterJoin As Boolean = False
    Public Shared ReadOnly Property CurrentClientAddress As String
        Get
            If FrmLinkMain Is Nothing Then Return ""
            Return If(FrmLinkMain.ClientAddress, "")
        End Get
    End Property

    Public Shared Sub WriteRoomToInstanceServersDat(Optional ShowToast As Boolean = True)
        If McInstanceSelected Is Nothing Then
            If ShowToast Then Hint("请先在启动页面选择一个游戏实例！", HintType.Red)
            Return
        End If
        Dim TargetAddr = CurrentClientAddress
        If String.IsNullOrWhiteSpace(TargetAddr) Then
            If ShowToast Then Hint("当前联机房间尚未就绪！", HintType.Red)
            Return
        End If
        Try
            Dim ServersDat = IO.Path.Combine(McInstanceSelected.PathIndie, "servers.dat")
            Dim RoomLabel = $"PCL Linking 联机房间 ({If(FrmLinkMain?.NetworkName, "EasyTier")})"
            Dim Added = ModNbt.EnsureServerInServersDat(ServersDat, RoomLabel, TargetAddr)
            If ShowToast Then
                Hint(If(Added, $"已将 {TargetAddr} 写入实例「{McInstanceSelected.Name}」服务器列表！", $"已更新实例「{McInstanceSelected.Name}」的联机房间条目！"), HintType.Green)
            End If
        Catch ex As Exception
            If ShowToast Then Hint($"写入 servers.dat 失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Public Shared Sub LaunchConnectedRoomGame()
        If McInstanceSelected Is Nothing Then
            Hint("请先在启动页面选择一个游戏实例！", HintType.Red)
            Return
        End If
        Dim TargetAddr = CurrentClientAddress
        If String.IsNullOrWhiteSpace(TargetAddr) Then
            Hint("当前联机房间尚未就绪！", HintType.Red)
            Return
        End If
        WriteRoomToInstanceServersDat(False)
        RunInUi(
        Sub()
            McLaunchStart(New McLaunchOptions With {
                .Instance = McInstanceSelected,
                .ServerIp = TargetAddr
            })
        End Sub)
    End Sub

    ''' <summary>
    ''' 获取当前已创建房间的邀请码与验证码，供好友页发送邀请。未建房时返回 Nothing。
    ''' </summary>
    Public Shared Function GetCurrentInvite() As String()
        If LinkState <> LinkStates.Finished OrElse FrmLinkMain Is Nothing Then Return Nothing
        If Not FrmLinkMain.IsServerSide Then Return Nothing
        Return {FrmLinkMain.GetInviteCode(), FrmLinkMain.RoomVerifyCode}
    End Function

    '在进入页面时自动尝试加入房间或一键共享本地服务端
    Private Shared AutoJoinCode As String = Nothing
    Private Shared AutoJoinVerifyCode As String = Nothing
    Private HasPendingNavigationAction As Boolean = False
    Private Sub PanSelectJoin_Loaded() Handles PanSelectJoin.Loaded
        If AutoCreatePort > 0 AndAlso LinkState = LinkStates.Waiting Then
            Dim TargetPort = AutoCreatePort
            AutoCreatePort = 0
            HasPendingNavigationAction = True
            Dispatcher.BeginInvoke(
                New Action(
                Sub()
                    HasPendingNavigationAction = False
                    If LinkState = LinkStates.Waiting Then StartCreateRoomWithPort(TargetPort)
                End Sub), Windows.Threading.DispatcherPriority.Background)
            Return
        End If
        If AutoJoinCode Is Nothing Then Return
        Dim Code = AutoJoinCode
        Dim Verify = AutoJoinVerifyCode
        AutoJoinCode = Nothing
        AutoJoinVerifyCode = Nothing
        If LinkState <> LinkStates.Waiting Then Return
        HasPendingNavigationAction = True
        'Loaded 可能发生在页面切换动画期间，弹模态框会卡死界面，丢到 UI 队列里再执行
        Dispatcher.BeginInvoke(
            New Action(
            Sub()
                HasPendingNavigationAction = False
                If LinkState = LinkStates.Waiting Then JoinInternal(Code, Verify)
            End Sub), Windows.Threading.DispatcherPriority.Background)
    End Sub

#End Region

#Region "加载页面 (Loading | Failed)"

    '由 Waiting 或 Failed 状态切换到 Loading
    Private Sub SwitchToLoading(OldState As LinkStates)
        '清理变量
        Peers = Nothing
        FailCount = 0
        '加载器与进程状态
        LinkLoader.Start(IsForceRestart:=True)
        '页面切换会由 Loader 调用 MyPageRight 来触发
        'UI 更新
        UpdateProgressBar(0)
        LabLoadTitle.Text = If(IsServerSide, "创建房间中", "加入房间中")
        UpdateLoadingPage("正在初始化……", "准备初始化")
    End Sub

    '由 Loading 状态切换到 Failed
    Private Sub SwitchToFailed(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        ProcessStop()
        'UI 更新
        UpdateProgressBar(1)
        '显示错误信息
        LabLoadTitle.Text = FailReason
        Dim Brief As String = LinkLoader.Error.GetDisplay(False)
        Dim InnerEx As Exception = LinkLoader.Error
        If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Do Until InnerEx.InnerException Is Nothing
            InnerEx = InnerEx.InnerException
            If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Loop
        Dim CleanErr = If(Brief.StartsWithF("$"), Brief.TrimStart("$"), LinkLoader.Error.GetDisplay(True))
        LabLoadDesc.Text = CleanErr
        Logger.Warn(LinkLoader.Error, LabLoadTitle.Text)

        If IsAutoReconnecting Then
            If IsUserInitiatedExit OrElse Not Settings.Get(Of Boolean)("LinkAutoReconnect") OrElse CleanErr.Contains("找不到") OrElse Not ScheduleAutoReconnect("重连失败") Then
                IsAutoReconnecting = False
            End If
        End If
    End Sub

    '===============================
    '  页面 UI
    '===============================

    Private Sub UpdateLoadingPage(Title As String, FailBrief As String)
        If FailReason = FailBrief & "失败" Then Return
        FailReason = FailBrief & "失败"
        Logger.Info($"开始步骤：{Title}")
        RunInUiWait(
        Sub()
            If FrmLinkMain Is Nothing OrElse Not FrmLinkMain.LabLoadDesc.IsLoaded Then Return
            FrmLinkMain.LabLoadDesc.Text = Title
        End Sub)
    End Sub
    Private FailReason As String = "准备初始化"

    '取消加载
    Private Sub BtnLoadCancel_Click() Handles BtnLoadCancel.Click
        IsUserInitiatedExit = True
        IsAutoReconnecting = False
        ChangeState(LinkStates.Waiting)
    End Sub

    '点击重试
    Private Sub CardLoad_MouseLeftButtonUp() Handles CardLoad.MouseLeftButtonUp
        ChangeState(LinkStates.Loading)
    End Sub

    '进度条
    Private Sub UpdateProgressBar(Optional Value As Double = -1)
        If Value = -1 Then Value = LinkLoader.Progress
        Dim DisplayingProgress As Double = ColumnProgressA.Width.Value
        If Math.Round(Value - DisplayingProgress, 3) = 0 Then Return
        If DisplayingProgress <= Value Then
            Dim NewProgress As Double = If(Value = 1, 1, DisplayingProgress +
                (Value - DisplayingProgress) ^ 2 * 0.5)
            AniStart({
                AaGridLengthWidth(ColumnProgressA, NewProgress - ColumnProgressA.Width.Value, 200),
                AaGridLengthWidth(ColumnProgressB, (1 - NewProgress) - ColumnProgressB.Width.Value, 200)
            }, "Link Progress")
        Else
            ColumnProgressA.Width = New GridLength(Value, GridUnitType.Star)
            ColumnProgressB.Width = New GridLength(1 - Value, GridUnitType.Star)
            AniStop("Link Progress")
        End If
    End Sub
    Private Sub CardLoad_SizeChanged() Handles CardLoad.SizeChanged
        RectProgressClip.Rect = New Rect(0, 0, CardLoad.ActualWidth, 12)
    End Sub

#End Region

#Region "完成页面 (Finished)"

    '由 Loading 状态切换到 Finished
    Private Sub SwitchToFinished(OldState As LinkStates)
        '由于只可能从加载器的完成事件触发，不需要管加载器
        IsAutoReconnecting = False
        AutoReconnectAttempts = 0
        SyncLock MemberTrackerLock
            HasInitializedMemberTracker = False
            KnownPeerDisplayNames.Clear()
        End SyncLock
        PingHistory.Clear()
        If LabPingStats IsNot Nothing Then LabPingStats.Text = "延迟采样中（近 30 秒趋势与抖动）…"
        If LinePingGraph IsNot Nothing Then LinePingGraph.Points.Clear()

        UpdatePanelVisibility() '页面的实际切换会由 Loader 调用 MyPageRight 来触发

        'UI 更新
        If IsServerSide Then
            LabFinishTitle.Text = "已创建房间"
            LabFinishDesc.Text = $"把邀请码和验证码发给朋友，缺一不可！{vbCrLf}邀请码：{GetInviteCode()}{vbCrLf}验证码：{RoomVerifyCode}"
            BtnFinishExit.Text = "关闭"
            BtnFinishPing.ToolTip = "网络延迟"
            BtnFinishCopy.Visibility = Visibility.Visible
            BtnFinishShareFriend.Visibility = Visibility.Visible
            BtnFinishBroadcastFriends.Visibility = Visibility.Visible
            BtnFinishLaunchJoin.Visibility = Visibility.Collapsed
            BtnFinishAddServersDat.Visibility = Visibility.Collapsed
            Copy() '立即复制邀请码
            '下边栏
            BtnFinishPort.Visibility = Visibility.Visible
            LabFinishPort.Visibility = Visibility.Visible
            BtnFinishIp.Visibility = Visibility.Collapsed
            LabFinishIp.Visibility = Visibility.Collapsed
            LabFinishPort.Text = ServerPort
        Else
            LabFinishTitle.Text = "已加入房间"
            LabFinishDesc.Text = $"在多人游戏页面的最下方就能找到联机房间，也可直接点击下方按钮启动并直连！{vbCrLf}注意：使用离线登录时不要手动输入 IP！"
            BtnFinishExit.Text = "离开"
            BtnFinishPing.ToolTip = "与房主的延迟"
            BtnFinishCopy.Visibility = Visibility.Collapsed
            BtnFinishShareFriend.Visibility = Visibility.Collapsed
            BtnFinishBroadcastFriends.Visibility = Visibility.Collapsed
            BtnFinishLaunchJoin.Visibility = Visibility.Visible
            BtnFinishAddServersDat.Visibility = Visibility.Visible
            '下边栏
            BtnFinishPort.Visibility = Visibility.Collapsed
            LabFinishPort.Visibility = Visibility.Collapsed
            LabFinishIp.Text = ClientAddress
            BtnFinishIp.Visibility = Visibility.Visible
            LabFinishIp.Visibility = Visibility.Visible
            If AutoLaunchGameAfterJoin Then
                AutoLaunchGameAfterJoin = False
                Dispatcher.BeginInvoke(Threading.DispatcherPriority.ApplicationIdle, New Action(AddressOf LaunchConnectedRoomGame))
            End If
        End If
        Update() '立即刷新
    End Sub

    '===============================
    '  页面 UI
    '===============================

    '退出
    Private Sub BtnFinishExit_Click() Handles BtnFinishExit.Click
        TryExit(False, False)
    End Sub
    ''' <summary>
    ''' 退出联机。
    ''' 返回是否弹出了警告窗口并且玩家选择了取消。
    ''' </summary>
    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If LinkState = LinkStates.Waiting OrElse LinkState = LinkStates.Failed Then Return False
        If Not Silent Then
            If IsServerSide AndAlso PeopleCount > 1 Then
                If MyMsgBox("你确定要关闭联机房间吗？" & vbCrLf & "所有玩家都需要重新输入邀请码才可加入游戏！", "退出联机", "确定", "取消", IsWarn:=True) = 2 Then Return True
            ElseIf Closing Then
                If MyMsgBox(If(IsServerSide, "你确定要关闭联机房间吗？", "你确定要离开联机房间吗？"), "退出联机", "确定", "取消") = 2 Then Return True
            End If
        End If
        IsUserInitiatedExit = True
        IsAutoReconnecting = False
        ChangeState(LinkStates.Waiting)
        Return False
    End Function

    '复制邀请码
    Private Sub BtnFinishShareFriend_Click() Handles BtnFinishShareFriend.Click
        FrmMain.PageChange(FormMain.PageType.Friends)
    End Sub
    Private Sub Copy() Handles BtnFinishCopy.Click
        Dim CodeText = $"在 PCL Linking 启动器中输入邀请码【{GetInviteCode()}】，再输入验证码【{RoomVerifyCode}】，即可加入联机房间！"
        ClipboardSet(CodeText, False)
        Hint("已复制邀请码！", HintType.Green)
    End Sub
    Private Function GetInviteCode() As String
        Dim Raw = $"{NetworkName}-{BaseNetworkSecret}-{INVITE_CODE_VERSION.ToString.PadLeft(2, "0"c)}{ _
            If(DiscoverNodeID = -1, 0, DiscoverNodeID).ConvertRadix(16).PadLeft(3, "0"c)}"
        If RoomVerifyCode <> "" Then Raw &= "-" & VerifyChecksum(RoomVerifyCode)
        Return Raw
    End Function

    '复制 IP
    Private Sub BtnFinishIp_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles BtnFinishIp.MouseLeftButtonUp
        ClipboardSet(ClientAddress, False)
        Hint("已复制服务器地址！", HintType.Green)
    End Sub

    Private Sub BtnFinishLaunchJoin_Click() Handles BtnFinishLaunchJoin.Click
        LaunchConnectedRoomGame()
    End Sub

    Private Sub BtnFinishAddServersDat_Click() Handles BtnFinishAddServersDat.Click
        WriteRoomToInstanceServersDat(True)
    End Sub

#End Region

    '===============================
    '  后端
    '===============================

    ''' <summary>
    ''' EasyTier 所在的文件夹路径，以 \ 结尾。
    ''' </summary>
    Private PathEasyTier As String = Paths.BaseThenName & "EasyTier\"
    ''' <summary>
    ''' 当前是服务端还是客户端。
    ''' </summary>
    Private IsServerSide As Boolean
    ''' <summary>
    ''' 服务端映射前的端口号。
    ''' </summary>
    Private ServerPort As Integer
    ''' <summary>
    ''' 客户端映射后的端口号。
    ''' </summary>
    Private ClientPort As Integer
    ''' <summary>
    ''' RPC 端口号。
    ''' </summary>
    Private RPCPort As Integer
    ''' <summary>
    ''' 在客户端映射后的地址。
    ''' 除非已作为客户端启动 EasyTier，否则一直为 Nothing。
    ''' </summary>
    Private ClientAddress As String = Nothing
    ''' <summary>
    ''' 发现节点的 ID。
    ''' 若必须设定自定义节点则为 -2；若等待选择则为 -1；选择回退节点则为 0；否则为对应节点的 ID。
    ''' </summary>
    Private DiscoverNodeID As Integer = -1
    ''' <summary>
    ''' 网络信息。
    ''' </summary>
    Private NetworkName As String, NetworkSecret As String

#Region "加载"

    Private WithEvents LinkLoader As New LoaderCombo(Of Integer)("联机", {
        New LoaderTask(Of Integer, Integer)("获取配置", AddressOf InitConfig) With {.ProgressWeight = 8},
        New LoaderTask(Of Integer, List(Of NetFile))("准备联机模块", AddressOf InitPrepareDownload) With {.ProgressWeight = 2},
        New LoaderDownload("检查联机模块", New List(Of NetFile)) With {.ProgressWeight = 40},
        New LoaderTask(Of Integer, Integer)("启动联机模块", AddressOf InitLaunch) With {.ProgressWeight = 50}
    })

    '1. 检查随包预置的联机模块
    Private Sub InitConfig(Task As LoaderTask(Of Integer, Integer))
        MigrateLegacyEasyTier()
        UpdateLoadingPage("正在检查联机模块……", "检查联机模块")
        If Not IsModuleReady() AndAlso LinkZipPath() = "" Then
            Logger.Info("联机模块与压缩包都不在本地，将在下一步自动下载")
        End If
    End Sub

    Private Sub MigrateLegacyEasyTier()
        Try
            Dim LegacyDir As String = Paths.AppDataThenName & "EasyTier\"
            If Not IO.Directory.Exists(LegacyDir) Then Return
            If FileUtils.Exists(PathEasyTier & LinkCoreName) AndAlso FileUtils.Exists(PathEasyTier & LinkCliName) Then Return
            IO.Directory.CreateDirectory(PathEasyTier)
            For Each FilePath In IO.Directory.GetFiles(LegacyDir)
                Dim Dest = IO.Path.Combine(PathEasyTier, IO.Path.GetFileName(FilePath))
                If Not IO.File.Exists(Dest) Then
                    IO.File.Copy(FilePath, Dest, False)
                End If
            Next
        Catch ex As Exception
            Logger.Warn(ex, "迁移旧版 EasyTier 模块目录失败")
        End Try
    End Sub

    ''' <summary>
    ''' 核心程序与 CLI 是否都在（不看版本标记）。杀毒软件误删时这里会变成 False。
    ''' </summary>
    Private Function ModuleFilesExist() As Boolean
        Return FileUtils.Exists(PathEasyTier & LinkCoreName) AndAlso FileUtils.Exists(PathEasyTier & LinkCliName)
    End Function

    ''' <summary>
    ''' 联机模块是否已经解压到位，且版本与当前要求一致。
    ''' </summary>
    Private Function IsModuleReady() As Boolean
        If Settings.Get(Of Integer)("LinkEasyTierVersion") < LinkVersion Then Return False
        Return ModuleFilesExist()
    End Function

    ''' <summary>
    ''' 优先用随包 zip，其次用自动下载到数据目录的 zip。都没有则返回空字符串。
    ''' </summary>
    Private Function LinkZipPath() As String
        If FileUtils.Exists(Paths.Base & LinkPackageName) Then Return Paths.Base & LinkPackageName
        If FileUtils.Exists(PathEasyTier & LinkPackageName) Then Return PathEasyTier & LinkPackageName
        Return ""
    End Function

    '2. 准备联机模块：随包优先，缺失时自动下载
    Private ServerVersion As Integer
    Private Sub InitPrepareDownload(Task As LoaderTask(Of Integer, List(Of NetFile)))
        UpdateLoadingPage("正在准备联机模块……", "准备联机模块")
        Telemetry("联机开始")
        ServerVersion = LinkVersion
        Dim Files As New List(Of NetFile)
        If Not IsModuleReady() AndAlso LinkZipPath() = "" Then
            IO.Directory.CreateDirectory(PathEasyTier)
            Files.Add(New NetFile(ModLink.GetEasyTierDownloadUrls(), PathEasyTier & LinkPackageName,
                                  New FileChecker With {.MinSize = 2 * 1024 * 1024}))
            Logger.Info("联机模块缺失，已加入自动下载任务")
        End If
        Task.Output = Files
    End Sub

    ''' <summary>
    ''' 保证 core/cli 在位；缺失时从可用 zip 重新解压（也能应对杀毒软件误删）。
    ''' 压缩包里可能带一层目录，统一摊平到 EasyTier 目录。
    ''' </summary>
    Private Sub EnsureModuleFiles()
        If IsModuleReady() Then Return
        Dim Zip = LinkZipPath()
        If Zip = "" Then
            Throw New Exception($"找不到 {LinkPackageName}，自动下载也没成功。{vbCrLf}请检查网络后重试，或手动把 {LinkPackageName} 放到启动器所在目录。")
        End If
        '若使用的是自动下载到 EasyTier 缓存目录的官方压缩包，强制校验官方 v2.6.4 SHA-256 摘要
        If String.Equals(IO.Path.GetFullPath(Zip), IO.Path.GetFullPath(PathEasyTier & LinkPackageName), StringComparison.OrdinalIgnoreCase) Then
            Dim ExpectedSha = ModLink.GetEasyTierExpectedSha256()
            If Not EasyTierNetworkCore.VerifyFileSha256(Zip, ExpectedSha) Then
                Dim ActualSha = EasyTierNetworkCore.ComputeFileSha256Hex(Zip)
                Try
                    IO.File.Delete(Zip)
                Catch
                End Try
                Throw New Exception($"自动下载的联机模块 SHA-256 校验失败（预期 {ExpectedSha}，实际 {ActualSha}），已清除疑似损坏文件，请重新尝试下载。")
            End If
        End If
        Try
            IO.Directory.CreateDirectory(PathEasyTier)
            Dim Temp As String = PathTemp & "EasyTierExtract\" & GetUuid() & "\"
            IO.Directory.CreateDirectory(Temp)
            Try
                Dim TempFull As String = IO.Path.GetFullPath(Temp).TrimEnd(IO.Path.DirectorySeparatorChar) & IO.Path.DirectorySeparatorChar
                Dim TotalExtractedBytes As Long = 0
                Dim EntryCount As Integer = 0
                Dim Buf(16383) As Byte
                Using Archive As ZipArchive = ZipFile.OpenRead(Zip)
                    If Archive.Entries.Count > 100 Then
                        Throw New IO.InvalidDataException($"联机模块压缩包条目数（{Archive.Entries.Count}）异常，已拒绝解压！")
                    End If
                    For Each Entry In Archive.Entries
                        If String.IsNullOrEmpty(Entry.Name) Then Continue For
                        EntryCount += 1
                        If EntryCount > 100 Then Throw New IO.InvalidDataException("联机模块压缩包条目数超出安全上限！")
                        Dim FileName As String = IO.Path.GetFileName(Entry.Name)
                        If String.IsNullOrWhiteSpace(FileName) Then Continue For
                        Dim DestPath As String = IO.Path.GetFullPath(IO.Path.Combine(TempFull, FileName))
                        If Not DestPath.StartsWith(TempFull, StringComparison.OrdinalIgnoreCase) Then
                            Throw New IO.InvalidDataException($"压缩包内包含非法路径：{Entry.FullName}")
                        End If
                        Using SrcStream = Entry.Open(), DestFs As New IO.FileStream(DestPath, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                            Do
                                Dim n = SrcStream.Read(Buf, 0, Buf.Length)
                                If n <= 0 Then Exit Do
                                TotalExtractedBytes += n
                                If TotalExtractedBytes > 128L * 1024L * 1024L Then
                                    Throw New IO.InvalidDataException("联机模块解压体积超过 128 MB 安全上限！")
                                End If
                                DestFs.Write(Buf, 0, n)
                            Loop
                        End Using
                        IO.File.Copy(DestPath, PathEasyTier & FileName, True)
                    Next
                End Using
            Finally
                Try
                    IO.Directory.Delete(Temp, True)
                Catch
                End Try
            End Try
            Logger.Info($"联机模块已解压到 {PathEasyTier}")
        Catch ex As Exception
            Throw New Exception($"解压联机模块失败：{ex.Message}", ex)
        End Try
        If Not ModuleFilesExist() Then
            Throw New Exception($"解压后仍找不到 {LinkCoreName}。{vbCrLf}很可能是 Windows Defender 或其他杀毒软件把联机模块删掉了，请把启动器目录加入白名单后再试。")
        End If
        Settings.Set("LinkEasyTierVersion", ServerVersion)
    End Sub

    '3. 启动联机模块
    Private Sub InitLaunch(Task As LoaderTask(Of Integer, Integer))
        '解压（首次运行、版本变化，或核心文件被杀毒软件删掉）
        If Not IsModuleReady() Then
            UpdateLoadingPage("正在解压联机模块……", "解压联机模块")
            EnsureModuleFiles()
        End If
        Task.Progress = 0.07
        '获取节点列表
        UpdateLoadingPage("正在获取节点列表……", "获取节点列表")
        Dim CustomPeers As String = Settings.Get(Of String)("LinkCustomPeer")
        Dim HasCustomPeer As Boolean = Not String.IsNullOrWhiteSpace(CustomPeers)
        If Not HasCustomPeer AndAlso DiscoverNodeID = -2 AndAlso Not IsServerSide Then
            Panic("未填写自定义节点设置", $"$你必须在 {vbLQ}自定义节点{vbRQ} 设置中填写与房主相同的内容，{vbCrLf}才能进入该房间！")
            Return
        End If
        If HasCustomPeer AndAlso DiscoverNodeID <> -2 AndAlso Not IsServerSide Then
            Hint("房主可能没有使用自定义节点设置，请确认你们的自定义节点设置是否一致！")
        End If
        Dim RawPeers As List(Of String) = BuildPeerList(CustomPeers)
        DiscoverNodeID = If(HasCustomPeer, -1, LinkDiscoverID)
        If RawPeers.Count = 0 Then
            Panic("找不到联机节点", $"请在 {vbLQ}设置 → 联机设置{vbRQ} 中填写自定义节点，或稍后重试。")
            Return
        End If
        Task.Progress = 0.13
        '获取空闲端口
        UpdateLoadingPage("正在启动联机模块……", "启动联机模块")
        Dim FreePorts = FindFreePorts(5, ServerPort)
        ClientPort = FreePorts(0)
        RPCPort = FreePorts(1)
        Dim ListenersPort As Integer = FreePorts(2) '它会占用【当前、当前 +1、当前 +2】共三个端口
        '获取启动参数
        Dim Arguments As String = BuildArguments(RawPeers, ListenersPort)
        '启动进程
        ProcessStart(Arguments)
        Task.Progress = 0.15
        '等待连接
        Dim TimeoutCounter As Integer = 0
        Do
            Thread.Sleep(500) '为避免还没启动好先跑 CLI 了，先等半秒
            '刷新信息
            CheckCrash()
            RefreshPeerLoader.WaitForExit(IsForceRestart:=True)
            '查找目标节点
            Dim Ping = GetPeerPing()
            If Ping > 0 Then
                Logger.Info($"已与目标建立连接，当前 Ping 为 {Ping:0.0}ms")
                Telemetry("联机成功", "Server", IsServerSide, "NAT", NATType)
                Exit Do '退出循环
            End If
            '更新进度
            Dim LastProgress = Task.Progress
            Dim CurPeersSnap = Peers
            Dim PeerCount As Integer = If(CurPeersSnap Is Nothing, -1, CurPeersSnap.Where(Function(p) p.Ping > 0).Count)
            Select Case PeerCount
                Case -1 'CLI 无返回
                    Task.Progress = (Task.Progress + 0.02).Clamp(0.15, 0.25)
                Case 0 'CLI 有返回，但未连接到任何节点
                    UpdateLoadingPage("正在连接到节点……", "连接节点")
                    Task.Progress = (Task.Progress + 0.02).Clamp(If(IsServerSide, 0.5, 0.3), If(IsServerSide, 0.95, 0.5))
                Case Else '已连接到节点，但未连接到房主
                    UpdateLoadingPage("正在连接到房主……", "连接房主")
                    Task.Progress = (Task.Progress + 0.02).Clamp(Math.Min(0.45 + PeerCount * 0.05, 0.65), 0.95)
            End Select
            '超时判定
            If LastProgress <> Task.Progress Then
                TimeoutCounter = 0
            ElseIf TimeoutCounter < 30 * 2 Then
                TimeoutCounter += 1
            Else '进度停滞超过 30s
                Select Case PeerCount
                    Case -1 'CLI 无返回
                        Dim Snapshot As String
                        SyncLock LogHistoryLock
                            Snapshot = LogHistory.Join(vbCrLf)
                        End SyncLock
                        Panic("无法启动联机模块", "近期日志：" & vbCrLf & Snapshot)
                    Case 0 'CLI 有返回，但未连接到任何节点
                        Panic("无法连接到节点", $"可能的原因：{vbCrLf}- 网络环境不佳，或被防火墙/代理拦截{vbCrLf}- 公共节点临时故障，可点击联机页下方的{vbLQ}自定义节点{vbRQ}更换后重试")
                    Case Else '已连接到节点，但未连接到房主
                        Panic("无法连接到房主", $"可能的原因：{vbCrLf}- 你或者房主的网络环境不佳{vbCrLf}- 房主已关闭房间{vbCrLf}- 邀请码输错了")
                End Select
            End If
        Loop Until Task.IsCanceled
        If Task.IsCanceled Then Throw New OperationCanceledException
        '等待连接稳定，最多 5s
        If IsServerSide Then Return
        UpdateLoadingPage("连接优化中……", "优化连接")
        Task.Progress = 0.999
        For i = 1 To 50
            Dim Server = GetTargetPeer()
            If Server IsNot Nothing AndAlso Not Server.Relay AndAlso Server.Ping < 100 Then Return '结束
            If Task.IsCanceled Then Throw New OperationCanceledException
            Thread.Sleep(100)
        Next
    End Sub
    ''' <summary>
    ''' 生成 EasyTier 的启动参数。
    ''' 参数集参考 PCL 社区版（PCL.Core/Link/EasyTier/ETController.Launch），
    ''' 只保留与 PCL Linking「免登录、免管理员、纯 P2P」定位相容的部分：
    '''   - 不加 --encryption-algorithm / --compression：这两个是网络级配置，
    '''     一旦房主与加入者的取值不同就无法互通，而 PCL Linking 是朋友之间互相联机，
    '''     强制统一反而会让旧版本的加入者连不上，所以保持 EasyTier 默认值。
    ''' </summary>
    Private Function BuildArguments(RawPeers As List(Of String), ListenersPort As Integer) As String
        Dim Builder As New Text.StringBuilder()
        '基础网络
        '--no-tun：不创建虚拟网卡，配合 --port-forward 使用，因此不需要管理员权限
        Builder.Append(" --no-tun")
        Builder.Append($" --network-name={NetworkName} --network-secret={NetworkSecret}")
        '只为本网络提供中继，避免其他网络的流量占用本机带宽（社区版同款）
        Builder.Append($" --relay-network-whitelist {NetworkName}")
        Builder.Append(" --private-mode true")
        Builder.Append($" --listeners {ListenersPort} --rpc-portal {RPCPort}")
        '传输层
        Builder.Append(" --multi-thread")
        Builder.Append($" --default-protocol {If(Settings.Get(Of Integer)("LinkProtocol") = 1, "udp", "tcp")}")
        If Not Settings.Get(Of Boolean)("LinkEnableIPv6") Then Builder.Append(" --disable-ipv6")
        If Not Settings.Get(Of Boolean)("LinkPunchSym") Then Builder.Append(" --disable-sym-hole-punching")
        If Settings.Get(Of Boolean)("LinkForceRelay") Then Builder.Append(" --disable-p2p")
        If Settings.Get(Of Integer)("LinkLatencyMode") = 1 Then Builder.Append(" --latency-first")
        '身份：H| 房主、J| 加入者，竖线后面是给玩家看的名字
        Dim HostName As String = If(IsServerSide, "H|", "J|") & GetLinkUserName()
        Dim ExtraPorts = ModLink.ParseExtraPorts(Settings.Get(Of String)("LinkExtraPorts"), ServerPort)
        If IsServerSide Then
            Dim AllPorts = New List(Of Integer) From {ServerPort}
            AllPorts.AddRange(ExtraPorts)
            Dim PortSpec = String.Join(",", AllPorts)
            Builder.Append($" -i {LinkServerAddress} --hostname={HostName}")
            Builder.Append($" --tcp-whitelist={PortSpec} --udp-whitelist={PortSpec}")
        Else
            Builder.Append($" -d --hostname={HostName} --tcp-whitelist=0 --udp-whitelist=0")
            '端口转发：把房主的游戏端口映射到本机，玩家连 localhost 即可
            ClientAddress = $"localhost:{ClientPort}"
            Dim UseIPv6Forward = Settings.Get(Of Boolean)("LinkEnableIPv6")
            If UseIPv6Forward Then
                Builder.Append($" --port-forward tcp://[{IPAddress.IPv6Loopback}]:{ClientPort}/{LinkServerAddress}:{ServerPort}")
                Builder.Append($" --port-forward udp://[{IPAddress.IPv6Loopback}]:{ClientPort}/{LinkServerAddress}:{ServerPort}")
            End If
            Builder.Append($" --port-forward tcp://{IPAddress.Loopback}:{ClientPort}/{LinkServerAddress}:{ServerPort}")
            Builder.Append($" --port-forward udp://{IPAddress.Loopback}:{ClientPort}/{LinkServerAddress}:{ServerPort}")
            '扩展端口转发（如 Simple Voice Chat 24454 等）
            For Each ExtraP In ExtraPorts
                If ExtraP = ClientPort Then Continue For
                If UseIPv6Forward Then
                    Builder.Append($" --port-forward tcp://[{IPAddress.IPv6Loopback}]:{ExtraP}/{LinkServerAddress}:{ExtraP}")
                    Builder.Append($" --port-forward udp://[{IPAddress.IPv6Loopback}]:{ExtraP}/{LinkServerAddress}:{ExtraP}")
                End If
                Builder.Append($" --port-forward tcp://{IPAddress.Loopback}:{ExtraP}/{LinkServerAddress}:{ExtraP}")
                Builder.Append($" --port-forward udp://{IPAddress.Loopback}:{ExtraP}/{LinkServerAddress}:{ExtraP}")
            Next
        End If
        '中继节点
        For Each Peer As String In RawPeers
            Builder.Append($" -p=""{Peer}""")
        Next
        Return Builder.ToString()
    End Function

    ''' <summary>
    ''' 联机时展示给其他人的名字，优先用离线登录的玩家名。
    ''' 竖线是 EasyTier hostname 的分隔符，必须过滤掉。
    ''' </summary>
    Private Function GetLinkUserName() As String
        Dim Name As String = Settings.Get(Of String)("LoginLegacyName")
        If Not String.IsNullOrWhiteSpace(Name) Then Name = Name.BeforeFirst("¨")
        Name = If(String.IsNullOrWhiteSpace(Name), "", Name.Trim())
        Dim SafeChars As New Text.StringBuilder(20)
        For Each Ch In Name
            If Char.IsControl(Ch) OrElse Ch = """"c OrElse Ch = "'"c OrElse Ch = "`"c OrElse Ch = "\"c OrElse Ch = "/"c Then Continue For
            SafeChars.Append(If(Ch = "|"c OrElse Char.IsWhiteSpace(Ch), "_"c, Ch))
            If SafeChars.Length >= 20 Then Exit For
        Next
        Dim Cleaned As String = SafeChars.ToString().TrimStart("-"c)
        Return If(Cleaned = "", "Player", Cleaned)
    End Function

#End Region

#Region "进程与日志"

    ''' <summary>
    ''' EasyTier Core 进程。
    ''' </summary>
    Private Shared ReadOnly ProcessCoreLock As New Object
    Private Shared ProcessCore As Process
    Private Shared ReadOnly LogHistoryLock As New Object

    Private Shared ReadOnly EasyTierDirPath As String = IO.Path.GetFullPath(Paths.BaseThenName & "EasyTier\")

    ''' <summary>
    ''' 若程序正在运行，则结束由 PCL Linking 启动的联机进程。
    ''' 即使失败也不会抛出异常。
    ''' </summary>
    Public Shared Sub ProcessStop()
        Try
            Dim CurrentCoreId As Integer = -1
            SyncLock ProcessCoreLock
                If ProcessCore IsNot Nothing Then
                    Try
                        CurrentCoreId = ProcessCore.Id
                    Catch
                    End Try
                End If
            End SyncLock
            '只停止从 PCL Linking 自带 EasyTier 目录启动的残留进程，避免误杀用户本机其他 EasyTier 服务
            For Each ProcessObject In Process.GetProcesses
                Using ProcessObject
                    Try
                        If ProcessObject.Id = CurrentCoreId Then Continue For
                        If ProcessObject.ProcessName <> LinkCoreName.Replace(".exe", "") AndAlso
                           ProcessObject.ProcessName <> LinkCliName.Replace(".exe", "") Then Continue For
                        Dim ExePath As String = ProcessObject.MainModule?.FileName
                        If String.IsNullOrEmpty(ExePath) OrElse
                           Not IO.Path.GetFullPath(ExePath).StartsWith(EasyTierDirPath, StringComparison.OrdinalIgnoreCase) Then
                            Continue For
                        End If
                        Logger.Info($"停止残留的联机模块，PID：{ProcessObject.Id}，进程名：{ProcessObject.ProcessName}")
                        ProcessObject.Kill()
                        ProcessObject.Close()
                    Catch exx As Exception
                        '跳过无权读取 MainModule 的系统/他用户进程
                    End Try
                End Using
            Next
            '关闭并释放由自身启动的进程
            SyncLock ProcessCoreLock
                If ProcessCore IsNot Nothing Then
                    Try
                        If Not ProcessCore.HasExited Then
                            Logger.Info($"停止所启动的联机模块，PID：{ProcessCore.Id}")
                            ProcessCore.Kill()
                        End If
                    Catch
                    Finally
                        ProcessCore.Dispose()
                        ProcessCore = Nothing
                    End Try
                End If
            End SyncLock
        Catch ex As Exception
            Logger.Warn(ex, "停止联机模块失败")
        End Try
    End Sub
    Private Function ScheduleAutoReconnect(ReasonBrief As String) As Boolean
        If IsUserInitiatedExit OrElse Not Settings.Get(Of Boolean)("LinkAutoReconnect") OrElse AutoReconnectAttempts >= 3 Then
            IsAutoReconnecting = False
            Return False
        End If
        AutoReconnectAttempts += 1
        Dim AttemptNo = AutoReconnectAttempts
        IsAutoReconnecting = True
        RunInUi(
        Sub()
            Hint($"[联机] {ReasonBrief}，3 秒后尝试第 {AttemptNo}/3 次自动重连…", HintType.Blue)
        End Sub)
        RunInNewThread(
        Sub()
            Thread.Sleep(3000)
            If IsUserInitiatedExit OrElse Not IsAutoReconnecting Then Return
            RunInUi(
            Sub()
                If IsUserInitiatedExit OrElse Not IsAutoReconnecting Then Return
                If LinkState = LinkStates.Waiting OrElse LinkState = LinkStates.Failed Then
                    ChangeState(LinkStates.Loading)
                End If
            End Sub)
        End Sub, "Link Auto Reconnect")
        Return True
    End Function

    ''' <summary>
    ''' 出现意外错误，给出错误信息并结束联机。
    ''' </summary>
    Private Sub Panic(Brief As String, Detail As String)
        '常见原因分析
        If Detail.Contains("failed to listen on ") Then
            Detail = $"监听端口失败。{vbCrLf}请点击重试，如果还是出现此错误，可以重启电脑解决。{vbCrLf}{vbCrLf}{Detail}"
        End If
        '显示信息
        If LinkState = LinkStates.Loading Then
            FailReason = Brief
            LinkLoader.Failed(New Exception(Detail))
        Else
            If LinkState = LinkStates.Finished AndAlso Not Detail.Contains("找不到") AndAlso ScheduleAutoReconnect(Brief) Then
                ChangeState(LinkStates.Waiting)
                Return
            End If
            'PCL Linking：Panic 会由定时器线程调用，须先切出 Finished/Loading 状态再弹窗，防止定时轮询重入弹出多个框
            IsAutoReconnecting = False
            RunInUi(
            Sub()
                If LinkState = LinkStates.Waiting Then Return
                ChangeState(LinkStates.Waiting)
                MyMsgBox(Detail, "联机出错：" & Brief, IsWarn:=True)
            End Sub)
            Telemetry("联机失败", "Exception", FilterUserName(Brief & "：" & Detail, "*"))
        End If
    End Sub

    ''' <summary>
    ''' 启动进程。
    ''' </summary>
    Private Sub ProcessStart(Arguments As String)
        '杀毒软件可能在解压之后才把主程序删掉，这里再兜一次底
        If Not ModuleFilesExist() Then EnsureModuleFiles()
        If Not FileUtils.Exists(PathEasyTier & LinkCoreName) Then
            Throw New Exception($"找不到 {LinkCoreName}。{vbCrLf}多半是 Windows Defender 等杀毒软件删掉了联机模块，请把启动器所在目录加入白名单后重新联机。")
        End If
        Dim Info = New ProcessStartInfo With {
            .FileName = PathEasyTier & LinkCoreName,
            .Arguments = Arguments,
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardError = True,
            .RedirectStandardOutput = True,
            .StandardOutputEncoding = Encoding.UTF8,
            .StandardErrorEncoding = Encoding.UTF8
        }
        Dim SafeArgsForLog As String = System.Text.RegularExpressions.Regex.Replace(Arguments, "--network-secret=\S+", "--network-secret=***")
        Logger.Info($"正在启动 EasyTier：{SafeArgsForLog}")
        Dim NewProcess As New Process With {.StartInfo = Info}
        Dim LogLineHandler =
        Sub(sender As Object, e As DataReceivedEventArgs)
            Try
                If e.Data Is Nothing Then Return
                ProcessLogLine(e.Data)
            Catch unused As ObjectDisposedException
            Catch ex As Exception
                Logger.Warn(ex, "读取 EasyTier 信息失败")
            End Try
        End Sub
        AddHandler NewProcess.OutputDataReceived, Sub(sender, e) LogLineHandler(sender, e)
        AddHandler NewProcess.ErrorDataReceived, Sub(sender, e) LogLineHandler(sender, e)
        Try
            NewProcess.Start()
        Catch ex As Exception
            NewProcess.Dispose()
            Throw New Exception($"启动 {LinkCoreName} 失败：{ex.Message}。{vbCrLf}若提示「系统找不到指定的文件」，通常是 Windows Defender 或其他杀毒软件删掉了联机模块：请把启动器所在目录与 {PathEasyTier} 加入白名单后重试。", ex)
        End Try
        SyncLock ProcessCoreLock
            ProcessCore = NewProcess
        End SyncLock
        NewProcess.BeginOutputReadLine()
        NewProcess.BeginErrorReadLine()
    End Sub
    Private Sub ProcessLogLine(Line As String)
        '记录日志
        Logger.Info(Line)
        SyncLock LogHistoryLock
            LogHistory.Enqueue(Line)
            If LogHistory.Count > 10 Then LogHistory.Dequeue()
        End SyncLock
        '检查日志内容
        If Line.ContainsIgnoreCase("new peer connection added") Then
            Logger.Warn($"已建立连接：{If(Line.RegexSeek("(?<=remote_addr.+?"")[^""}]{3,}"), Line)}")
            Update()
        ElseIf Line.ContainsIgnoreCase("peer connection removed") Then
            Logger.Warn($"已断开连接：{If(Line.RegexSeek("(?<=remote_addr.+?"")[^""}]{3,}"), Line)}")
            Update()
        End If
    End Sub
    Private LogHistory As New Queue(Of String)(10)

#End Region

#Region "CLI 节点信息获取"

    Private Class Peer
        ''' <summary>
        ''' 该节点的类型。
        ''' </summary>
        Public ReadOnly Type As Types
        Public Enum Types
            ''' <summary>
            ''' 客户端。
            ''' </summary>
            Client
            ''' <summary>
            ''' 服务器。
            ''' </summary>
            Server
            ''' <summary>
            ''' 公共节点。
            ''' </summary>
            Misc
            ''' <summary>
            ''' 自己，或者自己的重复（它可能返回多个自己）。
            ''' </summary>
            Self
        End Enum
        ''' <summary>
        ''' 与该节点的延迟 (ms)。
        ''' 若是自己则为 0。
        ''' </summary>
        Public ReadOnly Ping As Double
        ''' <summary>
        ''' 名称（EasyTier 的原始 hostname 字符串）。
        ''' </summary>
        Public ReadOnly Name As String
        ''' <summary>
        ''' 展示给玩家看的名字。新版 hostname 形如 H|小明 / J|小红，取竖线后的部分；
        ''' 旧版形如 Client-xxxxxx，直接显示原名。
        ''' </summary>
        Public ReadOnly DisplayName As String
        ''' <summary>
        ''' 是否通过中继连接。
        ''' </summary>
        Public ReadOnly Relay As Boolean
        ''' <summary>
        ''' NAT 类型。
        ''' </summary>
        Public ReadOnly NATType As NATTypes
        Public ReadOnly Ipv4 As String
        Public ReadOnly LossRate As String
        Public ReadOnly TunnelProto As String

        ''' <summary>
        ''' 从 CLI 给出的信息分析对应的数据。
        ''' </summary>
        Public Sub New(Info As JObject)
            '类别
            Dim PeerName As String = If(Info("hostname") Is Nothing, "", Info("hostname").ToString)
            Dim PeerIp As String = If(Info("ipv4") Is Nothing, "", Info("ipv4").ToString)
            Ipv4 = If(String.IsNullOrWhiteSpace(PeerIp), "-", PeerIp)
            LossRate = If(Info("loss_rate") Is Nothing, "0%", Info("loss_rate").ToString)
            TunnelProto = If(Info("tunnel_proto") Is Nothing OrElse String.IsNullOrWhiteSpace(Info("tunnel_proto").ToString), "tcp", Info("tunnel_proto").ToString)
            Dim PeerCost As String = If(Info("cost") Is Nothing, "", Info("cost").ToString)
            If PeerCost = "Local" Then
                Type = Types.Self
            ElseIf PeerName.StartsWithF("H|") OrElse PeerIp = LinkServerAddress Then
                Type = Types.Server '只允许这个 IP 作为房主，避免网络被多个房主搞炸的情况
            ElseIf PeerName.StartsWithF("J|") OrElse PeerName.StartsWithF("Client") Then
                Type = Types.Client
            ElseIf PeerName.StartsWithF("Server") Then
                Type = Types.Server '旧版房主的 hostname 是 Server-xxxxxx
            Else
                Type = Types.Misc
            End If
            '基础信息
            Double.TryParse(Info("lat_ms"), NumberStyles.Any, CultureInfo.InvariantCulture, Ping)
            Name = PeerName
            Dim NameParts = PeerName.Split("|"c)
            DisplayName = If(NameParts.Length >= 2, NameParts(1), PeerName)
            Relay = PeerCost.ContainsIgnoreCase("relay")
            Try
                NATType = EnumUtils.FromString(Of NATTypes)(
                    If(Info("nat_type") Is Nothing, "Unknown", Info("nat_type").ToString))
            Catch ex As Exception
                NATType = NATTypes.Unknown
            End Try
        End Sub
        Public Overrides Function ToString() As String
            Return $"{Type} - {Name} - Ping {Ping:0.0}ms [中继? {Relay}] - NAT {NATType}"
        End Function
    End Class
    Private Enum NATTypes
        'https://github.com/EasyTier/EasyTier/blob/6bb2fd9a15ab2499bdeabdcc3a925e9bd9aebf50/easytier/src/proto/common.proto#L129
        Unknown = 0
        OpenInternet = 1
        NoPAT = 2
        FullCone = 3
        Restricted = 4
        PortRestricted = 5
        Symmetric = 6
        SymUdpFirewall = 7
        SymmetricEasyInc = 8
        SymmetricEasyDec = 9
        ''' <summary>
        ''' 尚未获取。
        ''' </summary>
        Pending = 10
    End Enum

    ''' <summary>
    ''' 当前的节点列表，使用 RefreshPeerLoader 来刷新。
    ''' 若尚未成功获取过则为 Nothing，但保证在加载完成后至少是一个列表。
    ''' </summary>
    Private Peers As List(Of Peer) = Nothing
    ''' <summary>
    ''' 自己的 NAT 类型。
    ''' </summary>
    Private NATType As NATTypes = NATTypes.Pending

    ''' <summary>
    ''' 调用 EasyTier CLI 获取已连接节点信息。
    ''' 这会过滤掉尚未完成连接的其他节点以及自己。
    ''' 若在加载完成后连续获取失败 3 次，则会强制断开连接。
    ''' </summary>
    Private RefreshPeerLoader As New LoaderTask(Of Integer, List(Of Peer))("EasyTier CLI", AddressOf RefreshPeer)
    Private Sub RefreshPeer()
        Try
            Dim CliResult = StartProcessAndGetOutput(PathEasyTier & LinkCliName, $"-o json -p 127.0.0.1:{RPCPort} peer", 2000, Encoding:=Text.Encoding.UTF8)
            '解析
            If Not CliResult.Contains("lat_ms") Then Throw New Exception("CLI 调用失败：" & vbCrLf & CliResult)
            If GetUuid() Mod If(ModeDebug, 23, 103) = 0 Then Logger.Info($"CLI 输出抽样：{vbCrLf}{CliResult}")
            Dim NewPeers As New List(Of Peer)
            For Each Line As JObject In CType(CliResult.DeserializeJson(), JArray)
                Try
                    Dim Peer = New Peer(Line)
                    If Peer.Type = Peer.Types.Self Then
                        NATType = Peer.NATType '记录自己的 NAT
                    Else
                        NewPeers.Add(Peer)
                    End If
                Catch exx As Exception
                    Logger.Warn(exx, $"错误的信息（{Line}）")
                End Try
            Next
            '完成
            Peers = NewPeers
            FailCount = 0
        Catch ex As Exception
            Logger.Warn(ex, "获取节点信息失败")
            If LinkState = LinkStates.Finished Then
                FailCount += 1
                If FailCount >= 4 Then Panic("获取节点信息失败", ex.Message)
            End If
        End Try
    End Sub
    ''' <summary>
    ''' CLI 调用连续失败的次数。
    ''' </summary>
    Private FailCount As Integer = 0

    ''' <summary>
    ''' 根据当前的节点列表获取 Ping 值。
    ''' 服务端会返回所有节点中最低的 Ping，客户端会返回服务端的 Ping。
    ''' 0 代表尚不可用。
    ''' </summary>
    Private Function GetPeerPing() As Double
        Dim Peer = GetTargetPeer()
        If Peer Is Nothing Then Return 0
        Return Peer.Ping
    End Function
    ''' <summary>
    ''' 服务端会返回所有节点中 Ping 大于 0 最低的那一个，客户端会返回服务端。
    ''' 若没有则为 Nothing。
    ''' </summary>
    Private Function GetTargetPeer() As Peer
        Dim CurPeers = Peers
        If CurPeers Is Nothing Then Return Nothing
        Dim Targets As IEnumerable(Of Peer)
        If IsServerSide Then
            Targets = CurPeers.Where(Function(p) p.Ping > 0)
        Else
            Targets = CurPeers.Where(Function(p) p.Type = Peer.Types.Server AndAlso p.Ping > 0)
        End If
        If Not Targets.Any Then Return Nothing
        '返回 Ping 且最低的那个
        Dim MinPing = Targets.Min(Function(p) p.Ping)
        Return Targets.First(Function(p) p.Ping = MinPing)
    End Function

    ''' <summary>
    ''' 获取当前房间中的人数，包括自己。
    ''' 0 代表不可用。
    ''' </summary>
    Private ReadOnly Property PeopleCount As Integer
        Get
            Dim CurPeers = Peers
            If CurPeers Is Nothing Then Return 0
            Return CurPeers.Where(Function(p) p.Type <> Peer.Types.Misc).Count() + 1 '加上自己
        End Get
    End Property

#End Region

#Region "定时任务"

    '启动
    Private Shared IsTimerStarted As Integer = 0
    Private Sub StartTimerThread() Handles Me.Loaded
        If Threading.Interlocked.Exchange(IsTimerStarted, 1) = 1 Then Return
        RunInNewThread(
        Sub()
            Dim Counter As Integer = 0
            Do While True
                Try
                    Thread.Sleep(200)
                    Counter += 200
                    '每秒执行一次
                    If Counter >= 1000 Then
                        Counter = 0
                        Update()
                    End If
                    '每 200ms 更新进度条
                    If LinkState = LinkStates.Loading Then RunInUi(AddressOf UpdateProgressBar)
                Catch ex As Exception
                    Logger.Error(ex, "联机模块主时钟出错")
                    Thread.Sleep(10000)
                End Try
            Loop
        End Sub, "Link Timer")
    End Sub

    '每秒或进入页面时触发
    Private Sub Update() Handles Me.Loaded
        If LinkState <> LinkStates.Finished Then Return
        '重新获取信息
        SyncLock RefreshPeerLoader.LockState
            If RefreshPeerLoader.State <> LoadState.Loading Then RefreshPeerLoader.Start(IsForceRestart:=True)
        End SyncLock
        '更新 UI
        If FrmMain.PageCurrent = FormMain.PageType.Link Then
            RunInUi(
            Sub()
                'Logo 旋转动画
                AniStart(AaRotateTransform(ImgFinishLogo, 500, 5000), "Link Logo Rotation")
                'Ping
                Dim Ping As Double = GetPeerPing()
                Dim Connecting As Boolean = Ping Mod 500 = 0 OrElse $"{Ping:0.0}" = "1.0" OrElse FailCount > 0
                If Connecting Then
                    LabFinishPing.Text = "连接优化中"
                Else
                    LabFinishPing.Text = If(Ping >= 10, $"{Ping:0} ms", $"{Ping:0.0} ms")
                End If
                '人数显示
                LabFinishPlayer.Text = PeopleCount & "  人"
                '成员名单：鼠标悬停在人数上就能看到房间里都有谁
                Dim CurrentPeers = Peers
                Dim MemberNames = If(CurrentPeers Is Nothing, New List(Of String),
                    CurrentPeers.Where(Function(p) p.Type <> Peer.Types.Misc).Select(Function(p) p.DisplayName).Where(Function(n) Not String.IsNullOrWhiteSpace(n)).ToList())
                LabFinishPlayer.ToolTip = If(MemberNames.Any, "房间成员：" & vbCrLf & MemberNames.Join(vbCrLf), Nothing)
                '更新联机网络与成员监控卡片
                UpdatePeerMonitorPanel(CurrentPeers)
                '------------------------
                '  提示条
                '------------------------
                HintFinish.Visibility = Visibility.Collapsed
                If IsServerSide Then Return '服务端没有需要显示的提示
                If Connecting Then Return '连接稳定过程中不显示提示
                '中继提示
                Dim Server As Peer = GetTargetPeer()
                If Server Is Nothing Then Return
                If Server.Relay Then
                    If NATType >= NATTypes.Symmetric AndAlso Server.NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "你和房主的网络环境都不太好，"
                    ElseIf NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "你的网络环境不太好，"
                    ElseIf Server.NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "房主的网络环境不太好，"
                    Else
                        HintFinish.Text = "你或者房主的网络环境不太好，"
                    End If
                    If String.IsNullOrWhiteSpace(Settings.Get(Of String)("LinkCustomPeer")) Then
                        HintFinish.Text &= "正使用社区节点进行中继。"
                    Else
                        HintFinish.Text &= "正通过自定义节点进行中继。"
                    End If
                    HintFinish.Visibility = Visibility.Visible
                    HintFinish.Theme = MyHint.Themes.Yellow
                    Return
                End If
                '环境比房主更好的提示
                If NATType >= NATTypes.PortRestricted Then Return '自身的 NAT 为 2 或更好
                If Server.NATType < NATTypes.PortRestricted Then Return '房主的 NAT 为 3 或更差
                Dim OtherPlayers = If(CurrentPeers, New List(Of Peer)).Where(Function(p) p.Type = Peer.Types.Client).ToList()
                If Not OtherPlayers.Any() Then Return '房间里还有其他玩家
                If OtherPlayers.Any(Function(p) p.Relay) Then Return '自己可以与所有玩家打洞
                If OtherPlayers.All(Function(p) p.NATType < NATTypes.PortRestricted) Then Return '任意其他玩家的 NAT 为 3 或更差
                HintFinish.Visibility = Visibility.Visible
                HintFinish.Theme = MyHint.Themes.Blue
                HintFinish.Text = "你的网络环境比房主更好！如果你来当房主，其他玩家或许能更加流畅！"
            End Sub)
        End If
        '检查核心状态
        CheckCrash()
        '检查节点状态
        Dim SnapshotPeers = Peers
        If SnapshotPeers Is Nothing OrElse Not SnapshotPeers.Any Then
            If SnapshotPeers Is Nothing Then Return
            Panic("网络连接已断开", "请检查你的网络环境是否正常。")
            Return
        End If
        If Not IsServerSide AndAlso Not SnapshotPeers.Any(Function(p) p.Type = Peer.Types.Server) Then
            'PCL Linking：Update 由定时器线程调用，须先切回 Waiting 再弹窗，防止 1s 定时器重复弹窗
            RunInUi(
            Sub()
                If LinkState <> LinkStates.Finished Then Return
                ChangeState(LinkStates.Waiting)
                MyMsgBox("房主已离开房间！", "联机结束")
            End Sub)
            Return
        End If
        '成员加入 / 离开静默提醒
        Dim SnapshotPeersForNotice = Peers
        If SnapshotPeersForNotice IsNot Nothing Then
            Dim NowMemberNames = New HashSet(Of String)(
                SnapshotPeersForNotice.Where(Function(p) (p.Type = Peer.Types.Server OrElse p.Type = Peer.Types.Client) AndAlso Not String.IsNullOrWhiteSpace(p.DisplayName)) _
                                      .Select(Function(p) p.DisplayName),
                StringComparer.OrdinalIgnoreCase)
            Dim JoinedList As New List(Of String)
            Dim LeftList As New List(Of String)
            SyncLock MemberTrackerLock
                If Not HasInitializedMemberTracker Then
                    HasInitializedMemberTracker = True
                    KnownPeerDisplayNames.Clear()
                    For Each N In NowMemberNames : KnownPeerDisplayNames.Add(N) : Next
                Else
                    For Each JoinedName In NowMemberNames
                        If Not KnownPeerDisplayNames.Contains(JoinedName) Then JoinedList.Add(JoinedName)
                    Next
                    For Each LeftName In KnownPeerDisplayNames
                        If Not NowMemberNames.Contains(LeftName) Then LeftList.Add(LeftName)
                    Next
                    KnownPeerDisplayNames.Clear()
                    For Each N In NowMemberNames : KnownPeerDisplayNames.Add(N) : Next
                End If
            End SyncLock
            For Each JoinedName In JoinedList
                Dim CapturedName = JoinedName
                RunInUi(Sub() Hint($"[联机] {CapturedName} 加入了房间", HintType.Blue))
            Next
            For Each LeftName In LeftList
                Dim CapturedName = LeftName
                RunInUi(Sub() Hint($"[联机] {CapturedName} 离开了房间", HintType.Blue))
            Next
        End If

        '广播联机房间端口
        If Not IsServerSide Then
            Try
                Dim ServerPeer = GetTargetPeer()
                Dim HostTag = If(Not String.IsNullOrWhiteSpace(ServerPeer?.DisplayName), ServerPeer.DisplayName, "好友房间")
                BroadcastClientRoomPort(ClientPort, HostTag)
            Catch ex As Exception
                Logger.Warn(ex, "广播联机房间端口失败")
            End Try
        End If
    End Sub
    Private Shared ReadOnly BroadcastSocketLock As New Object
    Private Shared BroadcastSocket As Socket
    Private Shared Sub BroadcastClientRoomPort(Port As Integer, Optional HostName As String = "好友房间")
        Dim SafeHost = If(String.IsNullOrWhiteSpace(HostName), "好友房间", HostName.Replace("[", "").Replace("]", ""))
        Dim Payload = Encoding.UTF8.GetBytes($"[MOTD]PCL Linking 联机 · {SafeHost}[/MOTD][AD]{Port}[/AD]")
        Dim LoopbackTarget As New IPEndPoint(IPAddress.Loopback, 4445)
        Dim MulticastTarget As New IPEndPoint(IPAddress.Parse("224.0.2.60"), 4445)
        SyncLock BroadcastSocketLock
            If BroadcastSocket Is Nothing Then
                BroadcastSocket = New Socket(SocketType.Dgram, ProtocolType.Udp)
                Try
                    BroadcastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, True)
                Catch
                End Try
            End If
            BroadcastSocket.SendTo(Payload, SocketFlags.None, MulticastTarget)
            BroadcastSocket.SendTo(Payload, SocketFlags.None, LoopbackTarget)
        End SyncLock
    End Sub
    ''' <summary>
    ''' 检查联机模块是否崩溃。
    ''' </summary>
    Private Sub CheckCrash()
        SyncLock ProcessCoreLock
            If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then Return
        End SyncLock
        If LinkState <> LinkStates.Finished AndAlso LinkState <> LinkStates.Loading Then Return
        Dim Snapshot As String
        SyncLock LogHistoryLock
            Snapshot = LogHistory.Join(vbCrLf)
        End SyncLock
        Panic("联机模块已崩溃", "近期日志：" & vbCrLf & Snapshot)
    End Sub

    Public Shared Function GetLiveLinkSummary() As String
        If LinkState <> LinkStates.Finished OrElse FrmLinkMain Is Nothing Then Return ""
        Dim Ping As Double = FrmLinkMain.GetPeerPing()
        Dim PingText As String = If(Ping <= 0 OrElse Ping Mod 500 = 0, "连接中", If(Ping >= 10, $"{Ping:0}ms", $"{Ping:0.0}ms"))
        Return $"联机中 · {PingText} · {FrmLinkMain.PeopleCount}人"
    End Function

    Private Sub UpdatePeerMonitorPanel(CurrentPeers As List(Of Peer))
        If LabSelfNatInfo Is Nothing OrElse PanPeerMonitorList Is Nothing Then Return
        Dim ProtoText = If(Settings.Get(Of Integer)("LinkProtocol") = 1, "UDP", "TCP")
        Dim ExtraList = ModLink.ParseExtraPorts(Settings.Get(Of String)("LinkExtraPorts"), ServerPort)
        Dim ExtraTag = If(ExtraList.Any(), $" · 穿透端口：{String.Join(",", ExtraList)}", "")
        LabSelfNatInfo.Text = $"本机 NAT：{NATType} · 协议：{ProtoText} · 成员：{PeopleCount} 人{ExtraTag}"

        '延迟采样折线图与抖动指标
        Dim LivePing = GetPeerPing()
        If LivePing > 0 AndAlso LivePing Mod 500 <> 0 AndAlso $"{LivePing:0.0}" <> "1.0" Then
            PingHistory.Enqueue(LivePing)
            While PingHistory.Count > 30
                PingHistory.Dequeue()
            End While
            Dim Samples = PingHistory.ToArray()
            Dim Stats = EasyTierNetworkCore.ComputeLatencySeriesStats(Samples)
            If LabPingStats IsNot Nothing Then
                LabPingStats.Text = $"当前 {LivePing:0.0} ms · 均值 {Stats.AvgMs:0.0} ms · 抖动 {Stats.JitterMs:0.0} ms（{Stats.SampleCount} 样本）"
            End If
            If LinePingGraph IsNot Nothing AndAlso CanvasPingGraph IsNot Nothing Then
                Dim CanvasW = If(CanvasPingGraph.ActualWidth > 60, CanvasPingGraph.ActualWidth, 420.0)
                Dim CanvasH = If(CanvasPingGraph.ActualHeight > 20, CanvasPingGraph.ActualHeight, 36.0)
                Dim MaxY = Math.Max(30.0, Samples.Max() * 1.15)
                Dim Pts As New PointCollection()
                If Samples.Length = 1 Then
                    Dim Y0 = CanvasH - 4 - (Samples(0) / MaxY) * (CanvasH - 8)
                    Pts.Add(New Point(2, Y0))
                    Pts.Add(New Point(CanvasW - 2, Y0))
                Else
                    For I = 0 To Samples.Length - 1
                        Dim X = 2 + (I / CDbl(Samples.Length - 1)) * (CanvasW - 4)
                        Dim Y = CanvasH - 4 - (Samples(I) / MaxY) * (CanvasH - 8)
                        Pts.Add(New Point(X, Math.Max(2.0, Math.Min(CanvasH - 2.0, Y))))
                    Next
                End If
                LinePingGraph.Points = Pts
            End If
        End If

        PanPeerMonitorList.Children.Clear()

        Dim RoomPeers = If(CurrentPeers, New List(Of Peer)).Where(Function(p) p.Type = Peer.Types.Server OrElse p.Type = Peer.Types.Client).ToList()
        If Not RoomPeers.Any() Then
            Dim EmptyTb As New TextBlock With {
                .Text = If(IsServerSide, "房间已就绪，正在等待好友加入…", "正在同步房间其他成员状态…"),
                .FontSize = 12
            }
            EmptyTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
            PanPeerMonitorList.Children.Add(EmptyTb)
            Return
        End If

        For Each P In RoomPeers
            Dim RoleTag = If(P.Type = Peer.Types.Server, "[房主]", "[成员]")
            Dim ModeTag = If(P.Relay, $"中继 ({P.TunnelProto})", $"P2P 直连 ({P.TunnelProto})")
            Dim PingStr = If(P.Ping > 0 AndAlso P.Ping Mod 500 <> 0, $"{P.Ping:0.0} ms", "--")
            Dim LineText = $"{RoleTag} {P.DisplayName}  |  {P.Ipv4}  |  {ModeTag}  |  延迟 {PingStr}  |  丢包 {P.LossRate}  |  NAT {P.NATType}"
            Dim Tb As New TextBlock With {
                .Text = LineText,
                .FontSize = 12,
                .Margin = New Thickness(0, 2, 0, 2),
                .TextTrimming = TextTrimming.CharacterEllipsis
            }
            Tb.SetResourceReference(TextBlock.ForegroundProperty, If(P.Relay, "ColorBrushGray2", "ColorBrush1"))
            PanPeerMonitorList.Children.Add(Tb)
        Next
    End Sub

    Private Sub BtnFinishBroadcastFriends_Click() Handles BtnFinishBroadcastFriends.Click
        If Not BtnFinishBroadcastFriends.IsEnabled Then Return
        Dim Invite = GetCurrentInvite()
        If Invite Is Nothing Then
            Hint("请先等待联机房间创建完成！", HintType.Red)
            Return
        End If
        Dim Targets = ModFriends.GetFriendsSnapshot()
        If Not Targets.Any() Then
            Hint("好友列表为空，请先在「好友」页添加好友！", HintType.Red)
            Return
        End If
        Dim IncludeMods As Boolean = False
        If McInstanceSelected IsNot Nothing Then
            Dim Choice = MyMsgBox($"即将向全部 {Targets.Count} 位好友发送联机邀请（当前实例：「{McInstanceSelected.Name}」），是否附带 Mod 清单？",
                                  "一键群发联机邀请", "附带 Mod 清单群发", "仅群发邀请", "取消")
            If Choice = 3 OrElse Choice = 0 Then Return
            IncludeMods = (Choice = 1)
        Else
            If MyMsgBox($"确定向全部 {Targets.Count} 位好友群发当前房间邀请吗？", "一键群发联机邀请", "确认群发", "取消") <> 1 Then Return
        End If

        BtnFinishBroadcastFriends.IsEnabled = False
        Dim Code = Invite(0), Verify = Invite(1), InstSnap = McInstanceSelected
        Hint($"正在向 {Targets.Count} 位好友群发联机邀请…", HintType.Blue)
        RunInNewThread(
            Sub()
                Dim OkCount As Integer = 0, FailCount As Integer = 0, LastErr As String = ""
                Try
                    Dim SyncData As String = ""
                    If IncludeMods AndAlso InstSnap IsNot Nothing Then
                        SyncData = ModFriends.CompressModSyncManifest(ModFriends.BuildInstanceModSyncManifest(InstSnap))
                    End If
                    Dim BodyMsg = If(IncludeMods, "一起来联机吧（群发邀请，含 Mod 同步清单）", "一起来联机吧（群发邀请）")
                    For Each F In Targets
                        Try
                            Dim Err = ModFriends.Send(F.Id, "invite", BodyMsg, Code, Verify, SyncData)
                            If Err Is Nothing Then
                                OkCount += 1
                            Else
                                FailCount += 1
                                LastErr = Err
                            End If
                        Catch exEach As Exception
                            FailCount += 1
                            LastErr = exEach.Message
                        End Try
                    Next
                Catch ex As Exception
                    FailCount = Math.Max(1, FailCount)
                    LastErr = ex.Message
                Finally
                    RunInUi(
                        Sub()
                            BtnFinishBroadcastFriends.IsEnabled = True
                            If FailCount = 0 Then
                                Hint($"已成功向全部 {OkCount} 位好友群发联机邀请！", HintType.Green)
                            ElseIf OkCount > 0 Then
                                Hint($"群发完成：成功 {OkCount} 位，失败 {FailCount} 位（{LastErr}）", HintType.Blue)
                            Else
                                Hint($"群发失败：{LastErr}", HintType.Red)
                            End If
                        End Sub)
                End Try
            End Sub, "Link Finish Broadcast Friends")
    End Sub

    Private Sub BtnLinkDiagnose_Click() Handles BtnLinkDiagnose.Click
        If LinkState <> LinkStates.Finished Then Return
        BtnLinkDiagnose.IsEnabled = False
        Hint("正在执行联机隧道与 Minecraft 端口连通性自检…", HintType.Blue)
        Dim TargetAddr = If(IsServerSide, $"127.0.0.1:{ServerPort}", CurrentClientAddress)
        Dim PeerSnapshot = Peers
        Dim SelfNat = NATType
        Dim CurIsServer = IsServerSide
        Dim CurPort = ServerPort
        Dim ExtraPorts = ModLink.ParseExtraPorts(Settings.Get(Of String)("LinkExtraPorts"), ServerPort)

        RunInNewThread(
            Sub()
                Dim Sb As New Text.StringBuilder()
                Sb.AppendLine($"【角色】{If(CurIsServer, $"房主（映射本地端口 {CurPort}）", $"加入者（本地映射 {TargetAddr}）")}")
                Sb.AppendLine($"【本机 NAT 类型】{SelfNat}  ｜  【断线自动重连】{If(Settings.Get(Of Boolean)("LinkAutoReconnect"), "已开启", "已关闭")}")
                Sb.AppendLine($"【扩展穿透端口】{If(ExtraPorts.Any(), String.Join(", ", ExtraPorts), "未配置")}")
                Sb.AppendLine()

                Dim RoomPeers = If(PeerSnapshot, New List(Of Peer)).Where(Function(p) p.Type = Peer.Types.Server OrElse p.Type = Peer.Types.Client).ToList()
                Sb.AppendLine($"【隧道成员连通情况】已发现其他成员 {RoomPeers.Count} 位：")
                If Not RoomPeers.Any() Then
                    Sb.AppendLine(If(CurIsServer, "• 暂无其他成员连接（可先邀请好友进房）。", "• 尚未同步到房主节点信息，请等待 2~3 秒后重测。"))
                Else
                    For Each P In RoomPeers
                        Sb.AppendLine($"• {P.DisplayName} ({P.Ipv4})：{If(P.Relay, "中继模式", "P2P 直连")} · 延迟 {P.Ping:0.0} ms · 丢包 {P.LossRate} · NAT {P.NATType}")
                    Next
                End If
                Sb.AppendLine()

                Sb.AppendLine($"【游戏端口可达性探测 ({TargetAddr})】")
                Dim Probe = PageToolsServerPing.ProbeMinecraftServer(TargetAddr, 2500)
                If Probe.Online Then
                    Sb.AppendLine($"✓ SLP 状态探测通过！延迟：{Probe.LatencyMs} ms · 版本：{Probe.VersionName} · 在线人数：{Probe.OnlinePlayers}/{Probe.MaxPlayers}")
                    Sb.AppendLine("结论：联机隧道与 Minecraft 端口均已打通，可直接在游戏内加入服务器。")
                Else
                    Sb.AppendLine($"✗ 暂未收到 Minecraft 服务端回包（{Probe.ErrorMessage}）")
                    If CurIsServer Then
                        Sb.AppendLine("排查建议：请确认游戏内已点击「对局域网开放」（且端口号与建房所填一致）或本地开服器已启动，并放行防火墙。")
                    Else
                        Sb.AppendLine("排查建议：① 若房主仍在主界面未开局，请让房主在单人世界点「对局域网开放」；② 若上方显示中继丢包高，建议双方改用「节点测速与择优」的同一节点。")
                    End If
                End If

                Dim Report = Sb.ToString().TrimEnd()
                RunInUi(
                    Sub()
                        BtnLinkDiagnose.IsEnabled = True
                        If MyMsgBox(Report, "EasyTier 联机连通性诊断报告", "关闭", "复制诊断结果") = 2 Then
                            ClipboardSet(Report, False)
                            Hint("已复制联机诊断报告！", HintType.Green)
                        End If
                    End Sub)
            End Sub, "Link Connectivity Self Diagnose")
    End Sub

#End Region

#Region "房间历史与收藏夹"

    Private Sub RefreshLinkBookmarksUi()
        If PanLinkBookmarks Is Nothing OrElse LabBookmarksEmpty Is Nothing Then Return
        PanLinkBookmarks.Children.Clear()
        Dim Bookmarks = ModLink.LoadRoomBookmarks()
        LabBookmarksEmpty.Visibility = If(Bookmarks.Any(), Visibility.Collapsed, Visibility.Visible)
        If Not Bookmarks.Any() Then Return

        For Each Bm In Bookmarks
            Dim LocalBm = Bm
            Dim Row As New Border With {
                .CornerRadius = New CornerRadius(6),
                .Padding = New Thickness(10, 7, 10, 7),
                .Margin = New Thickness(0, 0, 0, 6),
                .BorderThickness = New Thickness(1)
            }
            Row.SetResourceReference(Border.BackgroundProperty, "ColorBrush8")
            Row.SetResourceReference(Border.BorderBrushProperty, If(LocalBm.IsFavorite, "ColorBrush3", "ColorBrush6"))

            Dim GridRow As New Grid()
            GridRow.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            GridRow.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim InfoStack As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(0, 0, 8, 0)}
            Dim Prefix = If(LocalBm.IsFavorite, "★ ", "⏱ ")
            Dim TitleTb As New TextBlock With {
                .Text = $"{Prefix}{LocalBm.Name}",
                .FontSize = 12.5,
                .FontWeight = If(LocalBm.IsFavorite, FontWeights.SemiBold, FontWeights.Normal),
                .TextTrimming = TextTrimming.CharacterEllipsis
            }
            TitleTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
            InfoStack.Children.Add(TitleTb)

            Dim MaskedCode = If(LocalBm.InviteCode.Length > 13, LocalBm.InviteCode.Substring(0, 13) & "…", LocalBm.InviteCode)
            Dim PwTag = If(String.IsNullOrEmpty(LocalBm.VerifyCode), "无密码", "已保存密码")
            Dim SubTb As New TextBlock With {
                .Text = $"{MaskedCode} · {PwTag} · {LocalBm.LastUsedUtc.ToLocalTime():MM-dd HH:mm} ({LocalBm.UseCount}次)",
                .FontSize = 11,
                .TextTrimming = TextTrimming.CharacterEllipsis
            }
            SubTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
            InfoStack.Children.Add(SubTb)
            Grid.SetColumn(InfoStack, 0)
            GridRow.Children.Add(InfoStack)

            Dim ActPanel As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
            Dim BtnJoin As New MyButton With {
                .Text = "加入",
                .Height = 24,
                .Padding = New Thickness(9, 0, 9, 0),
                .ColorType = MyButton.ColorState.Highlight,
                .Margin = New Thickness(0, 0, 5, 0)
            }
            AddHandler BtnJoin.Click,
                Sub()
                    If LinkState <> LinkStates.Waiting Then Return
                    Dim CodeErr = ValidateCodeFormat(LocalBm.InviteCode)
                    If CodeErr IsNot Nothing Then
                        Hint("邀请码格式已失效：" & CodeErr, HintType.Red)
                        Return
                    End If
                    JoinInternal(LocalBm.InviteCode, LocalBm.VerifyCode)
                End Sub
            ActPanel.Children.Add(BtnJoin)

            Dim BtnFav As New MyButton With {
                .Text = If(LocalBm.IsFavorite, "取消收藏", "收藏"),
                .Height = 24,
                .Padding = New Thickness(8, 0, 8, 0),
                .Margin = New Thickness(0, 0, 5, 0)
            }
            AddHandler BtnFav.Click,
                Sub()
                    ModLink.ToggleFavoriteBookmark(LocalBm.Id)
                    RefreshLinkBookmarksUi()
                End Sub
            ActPanel.Children.Add(BtnFav)

            Dim BtnRename As New MyButton With {
                .Text = "改名",
                .Height = 24,
                .Padding = New Thickness(8, 0, 8, 0),
                .Margin = New Thickness(0, 0, 5, 0)
            }
            AddHandler BtnRename.Click,
                Sub()
                    Dim NewName = MyMsgBoxInput("重命名房间书签", "输入容易辨识的备注名（如「周末生存服」「小张的整合包」）：", LocalBm.Name)
                    If String.IsNullOrWhiteSpace(NewName) Then Return
                    ModLink.RenameBookmark(LocalBm.Id, NewName)
                    RefreshLinkBookmarksUi()
                End Sub
            ActPanel.Children.Add(BtnRename)

            Dim BtnDel As New MyButton With {
                .Text = "删除",
                .Height = 24,
                .Padding = New Thickness(8, 0, 8, 0),
                .ColorType = MyButton.ColorState.Red
            }
            AddHandler BtnDel.Click,
                Sub()
                    ModLink.DeleteBookmark(LocalBm.Id)
                    RefreshLinkBookmarksUi()
                End Sub
            ActPanel.Children.Add(BtnDel)

            Grid.SetColumn(ActPanel, 1)
            GridRow.Children.Add(ActPanel)
            Row.Child = GridRow
            PanLinkBookmarks.Children.Add(Row)
        Next
    End Sub

#End Region

End Class
