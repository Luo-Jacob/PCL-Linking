Public Class PageSetupLeft

    Private IsLoad As Boolean = False
    Private IsPageSwitched As Boolean = False '如果在 Loaded 前切换到其他页面，会导致触发 Loaded 时再次切换一次
    Private Sub PageSetupLeft_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        '是否处于隐藏的子页面
        Dim IsHiddenPage As Boolean = False
        If ItemLaunch.Checked AndAlso Settings.Get(Of Boolean)("UiHiddenSetupLaunch") Then IsHiddenPage = True
        If ItemUI.Checked AndAlso Settings.Get(Of Boolean)("UiHiddenSetupUi") Then IsHiddenPage = True
        If ItemLink.Checked AndAlso Settings.Get(Of Boolean)("UiHiddenSetupLink") Then IsHiddenPage = True
        If PageSetupUI.HiddenForceShow Then IsHiddenPage = False
        '若页面错误，或尚未加载，则继续
        If IsLoad AndAlso Not IsHiddenPage Then Return
        IsLoad = True
        '刷新子页面隐藏情况
        PageSetupUI.HiddenRefresh()
        '选择第一个未被禁用的子页面
        If IsPageSwitched Then Return
        If Not Settings.Get(Of Boolean)("UiHiddenSetupLaunch") Then
            ItemLaunch.SetChecked(True, False, False)
        ElseIf Not Settings.Get(Of Boolean)("UiHiddenSetupUi") Then
            ItemUI.SetChecked(True, False, False)
        ElseIf Not Settings.Get(Of Boolean)("UiHiddenSetupLink") Then
            ItemLink.SetChecked(True, False, False)
        Else
            ItemLaunch.SetChecked(True, False, False)
        End If
    End Sub
    Private Sub PageSetupLeft_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        IsPageSwitched = False
    End Sub

#Region "页面切换"

    ''' <summary>
    ''' 当前页面的编号。从左往右从 0 开始计算。
    ''' </summary>
    Public PageID As FormMain.PageSubType
    Public Sub New()
        InitializeComponent()
        '选择第一个未被禁用的子页面
        If Not Settings.Get(Of Boolean)("UiHiddenSetupLaunch") Then
            PageID = FormMain.PageSubType.SetupLaunch
        ElseIf Not Settings.Get(Of Boolean)("UiHiddenSetupUi") Then
            PageID = FormMain.PageSubType.SetupUI
        ElseIf Not Settings.Get(Of Boolean)("UiHiddenSetupLink") Then
            PageID = FormMain.PageSubType.SetupLink
        Else
            PageID = FormMain.PageSubType.SetupLaunch
        End If
    End Sub

    ''' <summary>
    ''' 勾选事件改变页面。
    ''' </summary>
    Private Sub PageCheck(sender As MyListItem, e As EventArgs) Handles ItemLaunch.Check, ItemUI.Check, ItemLink.Check, ItemAbout.Check
        '尚未初始化控件属性时，sender.Tag 为 Nothing，会跳过切换，且由于 PageID 默认为 0 而切换到第一个页面
        '若使用 IsLoaded，则会导致模拟点击不被执行（模拟点击切换页面时，控件的 IsLoaded 为 False）
        If sender.Tag IsNot Nothing Then PageChange(Val(sender.Tag))
    End Sub

    ''' <summary>
    ''' 获取当前导航指定的右页面。
    ''' </summary>
    Public Function PageGet(Optional ID As FormMain.PageSubType = -1)
        If ID = -1 Then ID = PageID
        Select Case ID
            Case FormMain.PageSubType.SetupLaunch
                If FrmSetupLaunch Is Nothing Then FrmSetupLaunch = New PageSetupLaunch
                Return FrmSetupLaunch
            Case FormMain.PageSubType.SetupUI
                If FrmSetupUI Is Nothing Then FrmSetupUI = New PageSetupUI
                Return FrmSetupUI
            Case FormMain.PageSubType.SetupLink
                If FrmSetupLink Is Nothing Then FrmSetupLink = New PageSetupLink
                Return FrmSetupLink
            Case FormMain.PageSubType.SetupAbout
                If FrmSetupAbout Is Nothing Then FrmSetupAbout = New PageSetupAbout
                Return FrmSetupAbout
            Case Else
                Throw New Exception("未知的设置子页面种类：" & ID)
        End Select
    End Function

    ''' <summary>
    ''' 切换现有页面。
    ''' </summary>
    Public Sub PageChange(ID As FormMain.PageSubType)
        If PageID = ID Then Return
        AniControlEnabled += 1
        IsPageSwitched = True
        Try

            Select Case ID
                Case FormMain.PageSubType.SetupLaunch
                    If IsNothing(FrmSetupLaunch) Then FrmSetupLaunch = New PageSetupLaunch
                    PageChangeRun(FrmSetupLaunch)
                Case FormMain.PageSubType.SetupUI
                    If IsNothing(FrmSetupUI) Then FrmSetupUI = New PageSetupUI
                    PageChangeRun(FrmSetupUI)
                Case FormMain.PageSubType.SetupLink
                    If IsNothing(FrmSetupLink) Then FrmSetupLink = New PageSetupLink
                    PageChangeRun(FrmSetupLink)
                Case FormMain.PageSubType.SetupAbout
                    If IsNothing(FrmSetupAbout) Then FrmSetupAbout = New PageSetupAbout
                    PageChangeRun(FrmSetupAbout)
                Case Else
                    Throw New Exception("未知的设置子页面种类：" & ID)
            End Select

            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换分页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub
    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight") '停止主页面的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
        If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
        FrmMain.PageRight = Target
        CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnExit()
        AniStart({
            AaCode(
            Sub()
                CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnForceExit()
                FrmMain.PanMainRight.Child = FrmMain.PageRight
                FrmMain.PageRight.Opacity = 0
            End Sub, 130),
            AaCode(
            Sub()
                '延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                FrmMain.PageRight.Opacity = 1
                FrmMain.PageRight.PageOnEnter()
            End Sub, 30, True)
        }, "PageLeft PageChange")
    End Sub

#End Region

    Public Sub Reset(sender As Object, e As EventArgs)
        Select Case Val(sender.Tag)
            Case FormMain.PageSubType.SetupLaunch
                If MyMsgBox("是否要初始化 全局设置 页面的所有设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupLaunch) Then FrmSetupLaunch = New PageSetupLaunch
                    FrmSetupLaunch.Reset()
                    ItemLaunch.Checked = True
                End If
            Case FormMain.PageSubType.SetupUI
                If MyMsgBox("是否要初始化 个性化 页面的所有设置？该操作不可撤销。" & vbCrLf & "（背景图片与音乐、主页等外部文件不会被删除）", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupUI) Then FrmSetupUI = New PageSetupUI
                    FrmSetupUI.Reset()
                    ItemUI.Checked = True
                End If
            Case FormMain.PageSubType.SetupLink
                If MyMsgBox("是否要初始化 联机设置 页面的所有设置？该操作不可撤销。", "初始化确认",, "取消", IsWarn:=True) = 1 Then
                    If IsNothing(FrmSetupLink) Then FrmSetupLink = New PageSetupLink
                    FrmSetupLink.Reset()
                    ItemLink.Checked = True
                End If
        End Select
    End Sub

#Region "设置快速检索与定位"

    Private Class SetupJumpEntry
        Public Property Title As String
        Public Property SubPage As FormMain.PageSubType
        Public Property CardTitle As String
        Public Property Keywords As String
    End Class

    Private Shared ReadOnly JumpEntries As SetupJumpEntry() = {
        New SetupJumpEntry With {.Title = "启动选项（隔离/标题/窗口）", .SubPage = FormMain.PageSubType.SetupLaunch, .CardTitle = "启动选项", .Keywords = "隔离 窗口 标题 优先级 可见性 全屏"},
        New SetupJumpEntry With {.Title = "内存分配", .SubPage = FormMain.PageSubType.SetupLaunch, .CardTitle = "内存分配", .Keywords = "内存 ram xmx xms 自动配置"},
        New SetupJumpEntry With {.Title = "离线皮肤", .SubPage = FormMain.PageSubType.SetupLaunch, .CardTitle = "离线皮肤", .Keywords = "离线 皮肤 steve alex"},
        New SetupJumpEntry With {.Title = "下载选项（镜像/CF Key/线程）", .SubPage = FormMain.PageSubType.SetupLaunch, .CardTitle = "下载选项", .Keywords = "下载 modrinth curseforge 镜像 bmclapi mcim 线程 限速 ssl key 译名"},
        New SetupJumpEntry With {.Title = "高级选项（JVM/Java 列表/GC）", .SubPage = FormMain.PageSubType.SetupLaunch, .CardTitle = "高级选项", .Keywords = "jvm java 列表 参数 zgc g1 显卡 日志"},
        New SetupJumpEntry With {.Title = "连接选项（直连/协议/节点池）", .SubPage = FormMain.PageSubType.SetupLink, .CardTitle = "连接选项", .Keywords = "直连 延迟 tcp udp 社区节点池 easytier"},
        New SetupJumpEntry With {.Title = "网络兼容（IPv6/NAT/中继）", .SubPage = FormMain.PageSubType.SetupLink, .CardTitle = "网络兼容", .Keywords = "ipv6 nat 对称 中继 打洞"},
        New SetupJumpEntry With {.Title = "自动联机与车队固定码", .SubPage = FormMain.PageSubType.SetupLink, .CardTitle = "自动联机与车队固定码", .Keywords = "自动加入 自动重连 固定 房间码 车队"},
        New SetupJumpEntry With {.Title = "自定义节点与穿透端口", .SubPage = FormMain.PageSubType.SetupLink, .CardTitle = "自定义节点与穿透端口", .Keywords = "自定义节点 额外穿透端口 24454 语音 voice 8100"},
        New SetupJumpEntry With {.Title = "基础主题（透明度/浅色深色）", .SubPage = FormMain.PageSubType.SetupUI, .CardTitle = "基础", .Keywords = "透明度 深色 浅色 跟随系统 主题 图标"},
        New SetupJumpEntry With {.Title = "背景图片", .SubPage = FormMain.PageSubType.SetupUI, .CardTitle = "背景图片", .Keywords = "背景 图片 模糊 自适应"},
        New SetupJumpEntry With {.Title = "背景音乐", .SubPage = FormMain.PageSubType.SetupUI, .CardTitle = "背景音乐", .Keywords = "背景 音乐 音量 随机播放"},
        New SetupJumpEntry With {.Title = "标题栏", .SubPage = FormMain.PageSubType.SetupUI, .CardTitle = "标题栏", .Keywords = "标题栏 文本 图片 logo"},
        New SetupJumpEntry With {.Title = "主页预设与自定义", .SubPage = FormMain.PageSubType.SetupUI, .CardTitle = "主页", .Keywords = "主页 预设 custom xaml"},
        New SetupJumpEntry With {.Title = "上游署名与版本信息", .SubPage = FormMain.PageSubType.SetupAbout, .CardTitle = "关于 PCL Linking修改版", .Keywords = "关于 版本 龙腾猫跃 赞助 qq 隐私 开源 协议"}
    }

    Private Sub SearchSetupBox_TextChanged() Handles SearchSetupBox.TextChanged
        If PanSetupSearch Is Nothing Then Return
        Dim Kw = If(SearchSetupBox.Text, "").Trim()
        PanSetupSearch.Children.Clear()
        If Kw = "" Then
            PanSetupSearch.Visibility = Visibility.Collapsed
            Return
        End If
        Dim Matches = JumpEntries.Where(
            Function(e) e.Title.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                        e.Keywords.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) >= 0).Take(6).ToList()
        If Not Matches.Any() Then
            PanSetupSearch.Children.Add(New TextBlock With {
                .Text = "未找到匹配设置",
                .FontSize = 11.5,
                .Margin = New Thickness(10, 4, 8, 4),
                .Foreground = FindResource("ColorBrushGray3")
            })
        Else
            For Each Entry In Matches
                Dim Captured = Entry
                Dim Item As New MyListItem With {
                    .Type = MyListItem.CheckType.Clickable,
                    .Height = 30,
                    .PaddingLeft = 8,
                    .MinPaddingRight = 8,
                    .Title = Captured.Title
                }
                Item.LabTitle.FontSize = 12
                AddHandler Item.Click, Sub() JumpToSetupCard(Captured)
                PanSetupSearch.Children.Add(Item)
            Next
        End If
        PanSetupSearch.Visibility = Visibility.Visible
    End Sub

    Private Sub JumpToSetupCard(Entry As SetupJumpEntry)
        FrmMain.PageChange(FormMain.PageType.Setup, Entry.SubPage)
        RunInUi(
        Sub()
            Try
                Dim TargetPage As MyPageRight = CType(PageGet(Entry.SubPage), MyPageRight)
                Dim PanMainObj = TryCast(TargetPage.FindName("PanMain"), Panel)
                If PanMainObj Is Nothing Then Return
                For Each ChildCard In PanMainObj.Children.OfType(Of MyCard)()
                    If String.Equals(If(ChildCard.Title, "").Trim(), Entry.CardTitle, StringComparison.OrdinalIgnoreCase) Then
                        If ChildCard.CanSwap AndAlso ChildCard.IsSwapped Then
                            ChildCard.IsSwapped = False
                        End If
                        ChildCard.BringIntoView()
                        Exit For
                    End If
                Next
            Catch
            End Try
        End Sub)
    End Sub

#End Region

End Class
