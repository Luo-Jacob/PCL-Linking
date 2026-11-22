Imports System.Collections.ObjectModel

Public Class PageDownloadResourceDetail
    Private ResourceItem As MyResourceItem = Nothing

    ''' <summary>
    ''' 当前页面应展示的内容类别。
    ''' 若当前页面是在查看前置时进入，则为 Any；这也能用于指示当前是否在查看前置。
    ''' </summary>
    Private TargetResourceType As ResourceTypes
    Private Project As ResourceProject
    Private TargetVersion As String
    Private TargetLoaders As ModLoaders
    Public Sub LoadPageArguments() Handles Me.Loaded
        Project = FrmMain.PageCurrent.Additional(0)
        TargetVersion = FrmMain.PageCurrent.Additional(2)
        TargetLoaders = CType(FrmMain.PageCurrent.Additional(3), ModLoaders)
        If TargetLoaders <> ModLoaders.None AndAlso Not TargetLoaders.Flags.IsSingle Then TargetLoaders = TargetLoaders.Flags.First() '强制取第一个
        TargetResourceType = FrmMain.PageCurrent.Additional(4)
    End Sub

#Region "加载器"

    Private ResourceFileLoader As New LoaderTask(Of Integer, List(Of ResourceVersion))(
        "Resource File",
        Sub(Task)
            LoadPageArguments()
            Dim Result = ResourceVersion.FromProjectId(Project.Id, Project.Platform)
            If Task.IsCanceled Then Return
            Task.Output = Result
        End Sub)

    '初始化加载器信息
    Private Sub PageDownloadResourceDetail_Inited(sender As Object, e As EventArgs) Handles Me.Initialized
        LoadPageArguments()
        PageLoaderInit(Load, PanLoad, PanMain, CardIntro, ResourceFileLoader, AddressOf Load_OnFinish)
    End Sub
    '自动重试
    Private Sub Load_State(sender As Object, state As MyLoading.MyLoadingState, oldState As MyLoading.MyLoadingState) Handles Load.StateChanged
        Select Case ResourceFileLoader.State
            Case LoadState.Failed
                Dim ErrorMessage As String = ""
                If ResourceFileLoader.Error IsNot Nothing Then ErrorMessage = ResourceFileLoader.Error.Message
                If ErrorMessage.Contains("不是有效的 json 文件") Then
                    Logger.Warn("下载的文件 json 列表损坏，已自动重试")
                    PageLoaderRestart()
                End If
        End Select
    End Sub
    '结果 UI 化
    Private Class CardSorter
        Implements IComparer(Of String)
        Public Topmost As String = ""
        Public Function Compare(x As String, y As String) As Integer Implements IComparer(Of String).Compare
            '相同
            If x = y Then Return 0
            '置顶
            If x = Topmost Then Return -1
            If y = Topmost Then Return 1
            '特殊版本
            Dim IsXSpecial As Boolean = Not x.Contains(".")
            Dim IsYSpecial As Boolean = Not y.Contains(".")
            If IsXSpecial AndAlso IsYSpecial Then Return x.CompareTo(y)
            If IsXSpecial Then Return 1
            If IsYSpecial Then Return -1
            '比较版本号
            Dim VersionCodeSort = -McUtils.CompareVersion(x.Replace(x.BeforeFirst(" ") & " ", ""), y.Replace(y.BeforeFirst(" ") & " ", ""))
            If VersionCodeSort <> 0 Then Return VersionCodeSort
            '比较全部
            Return -McUtils.CompareVersion(x, y)
        End Function
        Public Sub New(Optional Topmost As String = "")
            Me.Topmost = If(Topmost, "")
        End Sub
    End Class

    Private VersionFilter As String
    Private GroupedDrop As Boolean '是否按 Drop 筛选（1.21 / 1.20 / 1.19 / ...）而非小版本号（1.21.1 / 1.21 / 1.20.4 / ...）
    Private GroupedOld As Boolean '是否折叠远古版本为一个选项
    '筛选类型相同的结果（Modrinth 会返回 Mod、服务端插件、数据包混合的列表）
    Private Function GetResults() As List(Of ResourceVersion)
        Dim Results As List(Of ResourceVersion) = ResourceFileLoader.Output
        Select Case TargetResourceType
            Case ResourceTypes.Any
                Results = Results.Where(Function(r) r.ResourceType <> ResourceTypes.Plugin).ToList
            Case ResourceTypes.Shader, ResourceTypes.ResourcePack
                '不筛选光影和资源包，否则原版光影会因为是资源包格式而被过滤（#6473）
            Case Else
                Results = Results.Where(Function(r) r.ResourceType.HasFlagF(TargetResourceType)).ToList
        End Select
        Return Results
    End Function
    Private Sub Load_OnFinish()
        Dim Results = GetResults()

        '初始化筛选器
        Dim Filters As List(Of String) = Nothing
        Dim UpdateFilters =
        Sub()
            Filters = Results.SelectMany(Function(v) v.GameVersions).Select(Function(v) GetGroupedVersionName(v, GroupedDrop, GroupedOld)).
                Distinct.OrderByDescending(Function(s) s, Comparer(Of String).Create(Function(x, y) McUtils.CompareVersion(x, y))).ToList
        End Sub

        '确定分组方式
        GroupedDrop = False : GroupedOld = False
        UpdateFilters()
        If Filters.Count < 9 Then GoTo GroupDone
        GroupedDrop = True : GroupedOld = False
        UpdateFilters()
        If Filters.Count < 9 Then GoTo GroupDone
        GroupedDrop = False : GroupedOld = True
        UpdateFilters()
        If Filters.Count < 9 Then GoTo GroupDone
        GroupedDrop = True : GroupedOld = True
        UpdateFilters()
GroupDone:

        'UI 化筛选器
        PanFilter.Children.Clear()
        If Filters.Count < 2 Then
            CardFilter.Visibility = Visibility.Collapsed
            VersionFilter = Nothing
        Else
            CardFilter.Visibility = Visibility.Visible
            Filters.Insert(0, "全部")
            '转化为按钮
            For Each Version As String In Filters
                Dim RawFilterVal = Version
                Dim NewButton As New MyRadioButton With {
                    .Text = LocalizationCore.TranslateText(Version), .Tag = If(Version = "全部", "ALL", Version), .Margin = New Thickness(2, 0, 2, 0), .ColorType = MyRadioButton.ColorState.Highlight}
                NewButton.LabText.Margin = New Thickness(-2, 0, 10, 0)
                AddHandler NewButton.Check,
                Sub(sender As MyRadioButton, raiseByMouse As Boolean)
                    VersionFilter = If(sender.Tag?.ToString() = "ALL" OrElse sender.Text = "全部", Nothing, RawFilterVal)
                    UpdateFilterResult()
                End Sub
                PanFilter.Children.Add(NewButton)
            Next
            '自动选择
            Dim ToCheck As MyRadioButton = Nothing
            If TargetVersion <> "" Then
                Dim TargetFile = Results.FirstOrDefault(Function(v) v.GameVersions.Contains(TargetVersion))
                If TargetFile IsNot Nothing Then
                    Dim TargetGroup = GetGroupedVersionName(TargetVersion, GroupedDrop, GroupedOld)
                    For Each Button As MyRadioButton In PanFilter.Children
                        If Button.Text <> TargetGroup Then Continue For
                        ToCheck = Button
                        Exit For
                    Next
                End If
            End If
            If ToCheck Is Nothing Then ToCheck = PanFilter.Children(0)
            ToCheck.Checked = True
        End If

        '更新筛选结果（文件列表 UI 化）
        UpdateFilterResult()
    End Sub
    Private Sub UpdateFilterResult()
        Dim Results = GetResults()

        Dim TargetCardName As String = If(TargetVersion <> "" OrElse TargetLoaders <> ModLoaders.None,
            $"{If(TargetLoaders <> ModLoaders.None, TargetLoaders.ToString + " ", "")}{TargetVersion}（所选版本）", "")
        '归类到卡片下
        Dim Dict As New SortedDictionary(Of String, List(Of ResourceVersion))(New CardSorter(TargetCardName))
        Dict.Add("其他", New List(Of ResourceVersion))
        For Each Version As ResourceVersion In Results
            For Each GameVersion In Version.GameVersions
                '检查是否符合版本筛选器
                If VersionFilter IsNot Nothing AndAlso
                    GetGroupedVersionName(GameVersion, GroupedDrop, GroupedOld) <> VersionFilter Then Continue For
                '决定添加到哪个卡片
                Dim VerName As String = GetGroupedVersionName(GameVersion, False, False)
                '遍历加入的加载器列表
                Dim Loaders As New List(Of String)
                If Project.ModLoaders.Flags.Count > 1 AndAlso '工程至少有两个加载器
                    Version.ResourceType = ResourceTypes.Mod AndAlso '是 Mod
                    McVersion.IsStandardFormat(VerName) Then '不是 “快照版本” 之类的
                    Loaders.AddRange(Version.ModLoaders.Flags.Select(Function(f) f.ToString & " "))
                End If
                If Not Loaders.Any() Then Loaders.Add("") '保底加一个空的，确保它在一张卡片里
                '实际添加
                For Each Loader In Loaders
                    Dim TargetCard As String = Loader & VerName
                    If Not Dict.ContainsKey(TargetCard) Then Dict.Add(TargetCard, New List(Of ResourceVersion))
                    If Not Dict(TargetCard).Contains(Version) Then Dict(TargetCard).Add(Version)
                Next
            Next
        Next
        '添加筛选的版本的卡片
        If TargetCardName <> "" AndAlso (VersionFilter Is Nothing OrElse GetGroupedVersionName(TargetVersion, GroupedDrop, GroupedOld).StartsWithF(VersionFilter)) Then
            Dict.Add(TargetCardName, New List(Of ResourceVersion))
            For Each Version As ResourceVersion In Results
                If Version.GameVersions.Contains(TargetVersion) AndAlso
                   (TargetLoaders = ModLoaders.None OrElse TargetLoaders.Flags.Intersect(Version.ModLoaders.Flags).Any) Then
                    '检查是否符合版本筛选器
                    If VersionFilter IsNot Nothing AndAlso
                        Not Version.GameVersions.Any(Function(v) GetGroupedVersionName(v, GroupedDrop, GroupedOld) = VersionFilter) Then Continue For
                    If Not Dict(TargetCardName).Contains(Version) Then Dict(TargetCardName).Add(Version)
                End If
            Next
        End If
        '转化为 UI
        Try
            PanResults.Children.Clear()
            For Each Pair As KeyValuePair(Of String, List(Of ResourceVersion)) In Dict
                If Not Pair.Value.Any() Then Continue For
                If Pair.Key = TargetCardName.Replace("（所选版本）", "") Then Continue For
                '增加卡片
                Dim NewCard As New MyCard With {.Title = LocalizationCore.TranslateText(Pair.Key), .Margin = New Thickness(0, 0, 0, 15)}
                Dim NewStack As New StackPanel With {.Margin = New Thickness(20, MyCard.SwapedHeight, 18, 0), .VerticalAlignment = VerticalAlignment.Top, .RenderTransform = New TranslateTransform(0, 0), .Tag = Pair.Value}
                NewCard.Children.Add(NewStack)
                NewCard.SwapControl = NewStack
                NewCard.SetContentBuilder(AddressOf InitializeStackResourceVersions)
                PanResults.Children.Add(NewCard)
                '确定卡片是否默认展开
                If Pair.Key = TargetCardName OrElse
                   (FrmMain.PageCurrent.Additional IsNot Nothing AndAlso '#2761
                   (CType(FrmMain.PageCurrent.Additional(1), List(Of String)).Contains(NewCard.Title) OrElse CType(FrmMain.PageCurrent.Additional(1), List(Of String)).Contains(Pair.Key))) Then
                    NewCard.BuildContent()
                Else
                    NewCard.IsSwapped = True
                End If
                '增加提示
                If Pair.Key = "其他" Then
                    NewStack.Children.Add(New MyHint With {.Text = LocalizationCore.TranslateText("由于版本信息更新缓慢，可能无法识别刚更新的 MC 版本。几天后即可正常识别。"), .Theme = MyHint.Themes.Yellow, .Margin = New Thickness(5, 0, 0, 8)})
                End If
            Next
            '如果只有一张卡片，展开第一张卡片
            If PanResults.Children.Count = 1 Then
                CType(PanResults.Children(0), MyCard).IsSwapped = False
            End If
            '替代提示
            If Project.Types = ResourceTypes.ModOrDataPack AndAlso (TargetResourceType = ResourceTypes.Mod OrElse TargetResourceType = ResourceTypes.DataPack) Then
                HintAlternative.Visibility = Visibility.Visible
                HintAlternative.Text = LocalizationCore.TranslateText(If(TargetResourceType = ResourceTypes.Mod,
                    "以下是该项目的 Mod 版本。点击这里查看其数据包版本。", "以下是该项目的数据包版本。点击这里查看其 Mod 版本。"))
            Else
                HintAlternative.Visibility = Visibility.Collapsed
            End If
        Catch ex As Exception
            Logger.Error(ex, "可视化工程下载列表出错")
        End Try
    End Sub
    ''' <summary>
    ''' 根据卡片中的 StackPanel.Tag 将资源版本列表数据实例化为 UI。
    ''' </summary>
    Private Sub InitializeStackResourceVersions(Stack As StackPanel)
        Dim Versions = CType(Stack.Tag, List(Of ResourceVersion)).SortByComparison(Function(a, b) a.ReleaseDate > b.ReleaseDate)
        Dim HasDuplicates = Versions.DistinctBy(Function(a) a.Display).Count <> Versions.Count
        Dim Dependences As List(Of String)
        '获取并添加前置信息
        If TargetResourceType = ResourceTypes.ModPack Then
            Dependences = New List(Of String) '整合包的前置信息会显示其中包含的一大堆 Mod，不是真正意义的前置
        Else
            Dependences = Versions.SelectMany(Function(f) f.Dependencies).Distinct.Ordered.ToList()
        End If
        Dependences = Dependences.Where(
        Function(d)
            If Not ResourceProject.Cache.ContainsKey(d) Then Logger.Warn($"未找到 ID {d} 的前置信息")
            Return ResourceProject.Cache.ContainsKey(d)
        End Function).ToList
        If Dependences.Any() Then
            '添加开头间隔
            Stack.Children.Add(New TextBlock With {.Text = LocalizationCore.TranslateText("前置资源"), .FontSize = 14, .HorizontalAlignment = HorizontalAlignment.Left, .Margin = New Thickness(6, 2, 0, 5)})
            '添加前置列表
            For Each Dep In Dependences
                Dim Item = ResourceProject.Cache(Dep).ToResourceItem(False, False)
                Stack.Children.Add(Item)
            Next
            '添加结尾间隔
            Stack.Children.Add(New TextBlock With {.Text = LocalizationCore.TranslateText("版本列表"), .FontSize = 14, .HorizontalAlignment = HorizontalAlignment.Left, .Margin = New Thickness(6, 12, 0, 5)})
        End If
        '实例化每个元素
        Dim HasServerDir = IO.Directory.Exists(PageToolsServer.LoadSavedServerDir())
        For Each Version In Versions
            If TargetResourceType = ResourceTypes.ModPack Then
                Stack.Children.Add(Version.ToListItem(AddressOf Install_Click, AddressOf Save_Click, IsBadDisplay:=HasDuplicates))
            ElseIf TargetResourceType = ResourceTypes.Map Then
                Stack.Children.Add(Version.ToListItem(AddressOf InstallMap_Click, AddressOf Save_Click, IsBadDisplay:=HasDuplicates, ActionButtonToolTip:="另存为 .zip"))
            ElseIf TargetResourceType = ResourceTypes.DataPack Then
                Stack.Children.Add(Version.ToListItem(AddressOf InstallDataPack_Click, AddressOf Save_Click, IsBadDisplay:=HasDuplicates, ActionButtonToolTip:="另存为 .zip"))
            ElseIf HasServerDir AndAlso (Version.ResourceType.HasFlagF(ResourceTypes.Plugin) OrElse Version.ResourceType.HasFlagF(ResourceTypes.Mod)) Then
                Stack.Children.Add(Version.ToListItem(AddressOf Save_Click, AddressOf InstallToServer_Click, IsBadDisplay:=HasDuplicates, ActionButtonToolTip:="装入已配置服务端"))
            Else
                Stack.Children.Add(Version.ToListItem(AddressOf Save_Click, IsBadDisplay:=HasDuplicates))
            End If
        Next
        Stack.Children.Add(New FrameworkElement With {.Height = 18})
        Stack.Tag = Nothing
    End Sub
    Private Function GetGroupedVersionName(Name As String, GroupedByDrop As Boolean, FoldOld As Boolean) As String
        Dim Drop = McVersion.VersionToDrop(Name)
        If Name Is Nothing Then
            Return "其他"
        ElseIf Name.Contains("w") OrElse Drop = 209 Then
            Return "快照版"
        ElseIf Not McVersion.IsStandardFormat(Name) OrElse (FoldOld AndAlso Drop < 120) Then
            Return "远古版"
        ElseIf GroupedByDrop Then
            Return McVersion.DropToVersion(Drop)
        Else
            Return Name
        End If
    End Function

#End Region

    Private IsFirstInit As Boolean = True
    Public Sub Init() Handles Me.PageEnter
        AniControlEnabled += 1
        Project = FrmMain.PageCurrent.Additional(0)
        PanBack.ScrollToHome()

        '重启加载器
        If IsFirstInit Then
            '在 Me.Initialized 已经初始化了加载器，不再重复初始化
            IsFirstInit = False
        Else
            PageLoaderRestart(IsForceRestart:=True)
        End If

        '放置当前工程
        If ResourceItem IsNot Nothing Then PanIntro.Children.Remove(ResourceItem)
        ResourceItem = Project.ToResourceItem(True, True).Init()
        ResourceItem.CanInteraction = False
        ResourceItem.Margin = New Thickness(-7, -7, 0, 8)
        PanIntro.Children.Insert(0, ResourceItem)

        '决定按钮显示
        BtnIntroWeb.Text = LocalizationCore.TranslateText($"转到 {If(Project.Platform = ResourcePlatforms.SpigotMC, "SpigotMC", Project.Platform.ToString)}")
        BtnIntroWiki.Visibility = If(Project.WikiId = 0, Visibility.Collapsed, Visibility.Visible)

        AniControlEnabled -= 1
    End Sub

    '整合包安装
    Public Sub Install_Click(sender As MyListItem, e As EventArgs)
        Try

            '获取基本信息
            Dim File As ResourceVersion = sender.Tag
            Dim LoaderName As String = $"{Project.Platform} 整合包下载：{Project.TranslatedName} "

            '获取版本名
            Dim PackName As String = Project.TranslatedName.Replace(".zip", "").Replace(".rar", "").Replace(".mrpack", "").Replace("\", "＼").Replace("/", "／").Replace("|", "｜").Replace(":", "：").Replace("<", "＜").Replace(">", "＞").Replace("*", "＊").Replace("?", "？").Replace("""", "").Replace("： ", "：")
            Dim Validate As New ValidateFolderName(McFolderSelected & "versions")
            If Validate.Validate(PackName) <> "" Then PackName = ""
            Dim InstanceName As String = MyMsgBoxInput("输入版本名称", "", PackName, New Collection(Of Validate) From {Validate})
            If String.IsNullOrEmpty(InstanceName) Then Return

            '构造步骤加载器
            Dim Loaders As New List(Of LoaderBase)
            Dim Target As String = $"{McFolderSelected}versions\{InstanceName}\原始整合包.{If(Project.Platform = ResourcePlatforms.CurseForge, "zip", "mrpack")}"
            Dim LogoFileAddress As String = ResourceItem.PathLogo.ActualSource
            Loaders.Add(New LoaderDownload("下载整合包文件", {
                File.ToNetFile(Target, ResourceVersion.DownloadReason.Standalone, File.GameVersions.FirstOrDefault, File.ModLoaders)
            }) With {.ProgressWeight = 10, .Block = True})
            Loaders.Add(New LoaderTask(Of Integer, Integer)("准备安装整合包",
            Sub() ModpackInstall(Target, InstanceName, If(FileUtils.Exists(LogoFileAddress), LogoFileAddress, Nothing))) With {.ProgressWeight = 0.1})

            '启动
            Dim Loader As New LoaderCombo(Of String)(LoaderName, Loaders) With {.OnStateChanged =
            Sub(MyLoader)
                Select Case MyLoader.State
                    Case LoadState.Failed
                        Hint(MyLoader.Name & "失败：" & MyLoader.Error.GetDisplay(False), HintType.Red)
                    Case LoadState.Canceled
                        Hint(MyLoader.Name & "已取消！", HintType.Blue)
                    Case LoadState.Loading
                        Return '不重新加载版本列表
                End Select
                McInstallFailedClearFolder(MyLoader)
            End Sub}
            Loader.Start(McFolderSelected & "versions\" & InstanceName & "\")
            LoaderTaskbarAdd(Loader)
            FrmMain.BtnExtraDownload.ShowRefresh()
            FrmMain.PageChange(FormMain.PageType.TaskManager)

        Catch ex As Exception
            Logger.Error(ex, "下载资源整合包失败")
        End Try
    End Sub
    Private Function BuildFormattedFileName(File As ResourceVersion) As String
        Dim FileName As String
        If Project.TranslatedName = Project.RawName Then
            FileName = File.FileName
        Else
            Dim ChineseName As String = Project.TranslatedName.BeforeFirst(" (").BeforeFirst(" - ").
                Replace("\", "＼").Replace("/", "／").Replace("|", "｜").Replace(":", "：").Replace("<", "＜").Replace(">", "＞").Replace("*", "＊").Replace("?", "？").Replace("""", "").Replace("： ", "：")
            Select Case Settings.Get(Of Integer)("ToolDownloadTranslateV2")
                Case 0
                    FileName = $"【{ChineseName}】{File.FileName}"
                Case 1
                    FileName = $"[{ChineseName}] {File.FileName}"
                Case 2
                    FileName = $"{ChineseName}-{File.FileName}"
                Case 3
                    FileName = $"{File.FileName}-{ChineseName}"
                Case Else
                    FileName = File.FileName
            End Select
        End If
        If File.ResourceType = ResourceTypes.Mod Then FileName = FileName.Replace("~", "-") '~ 会导致 Mixin 加载失败
        Return FileName
    End Function

    ''' <summary>
    ''' 检查压缩包/JAR 下载结果的有效性；若仅有外链下载则直接跳转浏览器。
    ''' </summary>
    Private Function TryRedirectExternalOnly(File As ResourceVersion) As Boolean
        If String.IsNullOrEmpty(File?.ExternalOnlyUrl) Then Return False
        RunInUi(
        Sub()
            OpenWebsite(File.ExternalOnlyUrl)
            Hint("该资源需前往官方网页下载，已为你打开下载页面！", HintType.Blue)
        End Sub)
        Return True
    End Function

    Private Shared Sub TryDeleteFileQuietly(FilePath As String)
        Try
            If Not String.IsNullOrEmpty(FilePath) AndAlso IO.File.Exists(FilePath) Then IO.File.Delete(FilePath)
        Catch
        End Try
    End Sub

    Private Shared Sub ValidateDownloadedArchiveHeader(TargetPath As String)
        If Not IO.File.Exists(TargetPath) OrElse New IO.FileInfo(TargetPath).Length < 512 Then
            TryDeleteFileQuietly(TargetPath)
            Throw New Exception("下载的文件不完整，可能是服务端限制或触发了站点校验。")
        End If
        Using Fs As New IO.FileStream(TargetPath, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
            Dim H0 = Fs.ReadByte()
            Dim H1 = Fs.ReadByte()
            If H0 <> &H50 OrElse H1 <> &H4B Then
                Fs.Close()
                TryDeleteFileQuietly(TargetPath)
                Throw New Exception("下载的文件并非有效的 .jar / .zip 压缩包（可能触发了防机器人限制）。")
            End If
        End Using
    End Sub

    Private Shared Sub PromptExternalFallbackOnFailure(MyLoader As LoaderBase, FallbackUrl As String)
        If MyLoader.State <> LoadState.Failed OrElse String.IsNullOrEmpty(FallbackUrl) Then Return
        RunInUi(
        Sub()
            Dim Choice = MyMsgBox("无法直接自动下载该插件文件（可能受到防机器人校验或外链限制）。" & vbCrLf &
                                  "你可以直接前往插件官方页面手动下载。",
                                  "需前往网页下载", "打开下载网页", "取消", IsWarn:=True)
            If Choice = 1 Then OpenWebsite(FallbackUrl)
        End Sub)
    End Sub

    ''' <summary>
    ''' 从压缩包安全解压地图存档到目标 saves\SaveName 目录（含 ZipSlip 与解压总体积限制）。
    ''' </summary>
    Private Shared Sub ExtractMapZipSafely(ZipPath As String, DestSaveDir As String)
        Const MAX_ENTRIES As Integer = 50000
        Const MAX_TOTAL_BYTES As Long = 2L * 1024 * 1024 * 1024
        Dim StagingDir As String = PathTemp & "Cache\MapExtract_" & GetUuid() & "\"
        DirectoryUtils.Create(StagingDir)
        Try
            Dim StagingRoot As String = IO.Path.GetFullPath(StagingDir).TrimEnd("\"c) & "\"
            Dim TotalBytes As Long = 0
            Dim EntryCount As Integer = 0
            Using Archive = IO.Compression.ZipFile.OpenRead(ZipPath)
                Dim Buf(65535) As Byte
                For Each Entry In Archive.Entries
                    EntryCount += 1
                    If EntryCount > MAX_ENTRIES Then Throw New Exception("地图压缩包内文件数量过多（超过 50,000），已中止解压")
                    Dim RelPath = Entry.FullName.Replace("/"c, "\"c).TrimStart("\"c)
                    If String.IsNullOrEmpty(RelPath) Then Continue For
                    Dim FullDest = IO.Path.GetFullPath(IO.Path.Combine(StagingRoot, RelPath))
                    If Not FullDest.StartsWith(StagingRoot, StringComparison.OrdinalIgnoreCase) Then
                        Throw New Exception($"检测到不安全的压缩包路径（ZipSlip 防护拦截）：{Entry.FullName}")
                    End If
                    If Entry.FullName.EndsWith("/") OrElse Entry.FullName.EndsWith("\") Then
                        IO.Directory.CreateDirectory(FullDest)
                        Continue For
                    End If
                    Dim ParentDir = IO.Path.GetDirectoryName(FullDest)
                    If Not String.IsNullOrEmpty(ParentDir) Then IO.Directory.CreateDirectory(ParentDir)
                    Using Src = Entry.Open(), Dst As New IO.FileStream(FullDest, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                        Do
                            Dim ReadLen = Src.Read(Buf, 0, Buf.Length)
                            If ReadLen <= 0 Then Exit Do
                            TotalBytes += ReadLen
                            If TotalBytes > MAX_TOTAL_BYTES Then
                                Throw New Exception("地图解压总体积超过 2 GB 上限，已中止解压")
                            End If
                            Dst.Write(Buf, 0, ReadLen)
                        Loop
                    End Using
                Next
            End Using

            '定位包含 level.dat 的存档根目录
            Dim SourceRoot As String = StagingRoot.TrimEnd("\"c)
            If Not IO.File.Exists(IO.Path.Combine(SourceRoot, "level.dat")) Then
                Dim LevelFiles = IO.Directory.GetFiles(SourceRoot, "level.dat", IO.SearchOption.AllDirectories)
                If LevelFiles.Any() Then
                    Dim Shallowest = LevelFiles.OrderBy(Function(p) p.Count(Function(c) c = "\"c)).First()
                    SourceRoot = IO.Path.GetDirectoryName(Shallowest)
                Else
                    Dim TopFiles = IO.Directory.GetFiles(SourceRoot)
                    Dim TopDirs = IO.Directory.GetDirectories(SourceRoot)
                    If TopFiles.Length = 0 AndAlso TopDirs.Length = 1 Then SourceRoot = TopDirs(0)
                End If
            End If

            If IO.Directory.Exists(DestSaveDir) Then Throw New Exception($"目标存档目录已存在：{DestSaveDir}")
            MoveOrCopyDirectory(SourceRoot, DestSaveDir)
        Finally
            TryDeleteFileQuietly(ZipPath)
            Try
                If IO.Directory.Exists(StagingDir) Then IO.Directory.Delete(StagingDir, True)
            Catch
            End Try
        End Try
    End Sub

    Private Shared Sub MoveOrCopyDirectory(SourceDir As String, DestDir As String)
        Dim SrcRoot = IO.Path.GetPathRoot(IO.Path.GetFullPath(SourceDir))
        Dim DstRoot = IO.Path.GetPathRoot(IO.Path.GetFullPath(DestDir))
        Dim ParentDest = IO.Path.GetDirectoryName(IO.Path.GetFullPath(DestDir))
        If Not String.IsNullOrEmpty(ParentDest) Then IO.Directory.CreateDirectory(ParentDest)
        If String.Equals(SrcRoot, DstRoot, StringComparison.OrdinalIgnoreCase) Then
            IO.Directory.Move(SourceDir, DestDir)
            Return
        End If
        IO.Directory.CreateDirectory(DestDir)
        For Each FilePath In IO.Directory.GetFiles(SourceDir, "*", IO.SearchOption.AllDirectories)
            Dim Rel = FilePath.Substring(SourceDir.TrimEnd("\"c).Length).TrimStart("\"c)
            Dim TargetFile = IO.Path.Combine(DestDir, Rel)
            Dim TargetParent = IO.Path.GetDirectoryName(TargetFile)
            If Not String.IsNullOrEmpty(TargetParent) Then IO.Directory.CreateDirectory(TargetParent)
            IO.File.Copy(FilePath, TargetFile, True)
        Next
    End Sub

    '地图安装（下载压缩包并安全解压到当前游戏存档目录 saves\）
    Public Sub InstallMap_Click(sender As MyListItem, e As EventArgs)
        Try
            Dim File As ResourceVersion = sender.Tag
            If TryRedirectExternalOnly(File) Then Return

            Dim SavesDir As String = If(McInstanceSelected IsNot Nothing,
                $"{McInstanceSelected.PathIndie}saves\",
                $"{McFolderSelected}saves\")
            DirectoryUtils.Create(SavesDir)

            Dim DefaultName As String = Project.TranslatedName.Replace(".zip", "").Replace(".rar", "").
                Replace("\", "＼").Replace("/", "／").Replace("|", "｜").Replace(":", "：").
                Replace("<", "＜").Replace(">", "＞").Replace("*", "＊").Replace("?", "？").
                Replace("""", "").Replace("： ", "：").Trim()
            Dim Validate As New ValidateFolderName(SavesDir.TrimEnd("\"c))
            If Validate.Validate(DefaultName) <> "" Then DefaultName = ""
            Dim SaveName As String = MyMsgBoxInput("输入地图存档名称", "将自动下载并解压到游戏存档目录 (saves)", DefaultName, New Collection(Of Validate) From {Validate})
            If String.IsNullOrEmpty(SaveName) Then Return

            Dim TempZipPath As String = PathTemp & "Cache\MapDownload_" & GetUuid() & ".zip"
            DirectoryUtils.Create(PathTemp & "Cache\")
            Dim DestSaveDir As String = IO.Path.Combine(SavesDir, SaveName)
            Dim LoaderName As String = $"地图安装：{SaveName} "
            Dim Loaders As New List(Of LoaderBase) From {
                New LoaderDownload("下载地图压缩包", {
                    File.ToNetFile(TempZipPath, ResourceVersion.DownloadReason.Standalone)
                }) With {.ProgressWeight = 8, .Block = True},
                New LoaderTask(Of Integer, Integer)("安全校验并解压地图",
                    Sub() ExtractMapZipSafely(TempZipPath, DestSaveDir)) With {.ProgressWeight = 2}
            }
            Dim Loader As New LoaderCombo(Of Integer)(LoaderName, Loaders) With {.OnStateChanged =
            Sub(MyLoader)
                Select Case MyLoader.State
                    Case LoadState.Finished
                        Hint($"地图「{SaveName}」已安装到 saves 目录！", HintType.Green)
                    Case LoadState.Failed
                        Hint(MyLoader.Name & "失败：" & MyLoader.Error.GetDisplay(False), HintType.Red)
                    Case LoadState.Canceled
                        Hint(MyLoader.Name & "已取消！", HintType.Blue)
                End Select
            End Sub}
            Loader.Start(1)
            LoaderTaskbarAdd(Loader)
            FrmMain.BtnExtraDownload.ShowRefresh()
            FrmMain.BtnExtraDownload.Ribble()
            Hint($"已开始下载并安装地图「{SaveName}」到 saves 目录", HintType.Blue)
        Catch ex As Exception
            Logger.Error(ex, "安装地图存档失败")
        End Try
    End Sub

    '数据包一键装入指定世界存档的 datapacks\ 目录
    Public Sub InstallDataPack_Click(sender As MyListItem, e As EventArgs)
        Try
            Dim File As ResourceVersion = sender.Tag
            If TryRedirectExternalOnly(File) Then Return

            Dim SavesDir As String = If(McInstanceSelected IsNot Nothing,
                $"{McInstanceSelected.PathIndie}saves\",
                $"{McFolderSelected}saves\")
            Dim Worlds As New List(Of IO.DirectoryInfo)
            If IO.Directory.Exists(SavesDir) Then
                Dim AllDirs = New IO.DirectoryInfo(SavesDir).GetDirectories().OrderByDescending(Function(d) d.LastWriteTime).ToList()
                Dim WithLevelDat = AllDirs.Where(Function(d) IO.File.Exists(IO.Path.Combine(d.FullName, "level.dat"))).ToList()
                Worlds = If(WithLevelDat.Any(), WithLevelDat, AllDirs)
            End If

            If Not Worlds.Any() Then
                If MyMsgBox("当前游戏实例的 saves 目录中尚无可用世界存档。" & vbCrLf & "是否改用「另存为」保存到其他位置？",
                            "未找到存档", "另存为", "取消") = 1 Then
                    Save_Click(sender, e)
                End If
                Return
            End If

            Dim Radios As New List(Of IMyRadio)
            For Each W In Worlds
                Radios.Add(New MyRadioBox With {.Text = $"{W.Name}（更新于 {W.LastWriteTime:MM-dd HH:mm}）"})
            Next
            Radios.Add(New MyRadioBox With {.Text = "另存为其他位置..."})

            Dim SelectedIdx = MyMsgBoxSelect(Radios.ToArray(), "选择要装入数据包的世界存档：", "安装到该存档", "取消")
            If SelectedIdx Is Nothing Then Return
            If SelectedIdx.Value = Worlds.Count Then
                Save_Click(sender, e)
                Return
            End If

            Dim ChosenWorld = Worlds(SelectedIdx.Value)
            Dim DatapacksDir = IO.Path.Combine(ChosenWorld.FullName, "datapacks")
            IO.Directory.CreateDirectory(DatapacksDir)
            Dim FileName = BuildFormattedFileName(File)
            If Not FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
                FileName &= ".zip"
            End If
            Dim Target = IO.Path.Combine(DatapacksDir, FileName)
            Dim LoaderName As String = $"数据包安装：{ChosenWorld.Name}\{FileName} "
            Dim Loaders As New List(Of LoaderBase) From {
                New LoaderDownload("下载数据包到存档", {
                    File.ToNetFile(Target, ResourceVersion.DownloadReason.Standalone)
                }) With {.ProgressWeight = 6, .Block = True}
            }
            Dim Loader As New LoaderCombo(Of Integer)(LoaderName, Loaders) With {.OnStateChanged =
            Sub(MyLoader)
                Select Case MyLoader.State
                    Case LoadState.Finished
                        Hint($"数据包已安装到存档「{ChosenWorld.Name}」的 datapacks 目录！", HintType.Green)
                    Case LoadState.Failed
                        Hint(MyLoader.Name & "失败：" & MyLoader.Error.GetDisplay(False), HintType.Red)
                    Case LoadState.Canceled
                        Hint(MyLoader.Name & "已取消！", HintType.Blue)
                End Select
            End Sub}
            Loader.Start(1)
            LoaderTaskbarAdd(Loader)
            FrmMain.BtnExtraDownload.ShowRefresh()
            FrmMain.BtnExtraDownload.Ribble()
            Hint($"已开始将 {FileName} 安装至存档「{ChosenWorld.Name}」", HintType.Blue)
        Catch ex As Exception
            Logger.Error(ex, "安装数据包到存档失败")
        End Try
    End Sub

    Public Sub InstallToServer_Click(sender As Object, e As EventArgs)
        Dim File As ResourceVersion = If(TypeOf sender Is MyListItem, sender, sender.Parent).Tag
        If TryRedirectExternalOnly(File) Then Return
        Dim SavedServerDir = PageToolsServer.LoadSavedServerDir()
        If String.IsNullOrWhiteSpace(SavedServerDir) OrElse Not IO.Directory.Exists(SavedServerDir) Then
            Hint("请先在「工具 -> 服务器生成/管理」中设置有效的服务端目录！", HintType.Red)
            Return
        End If
        Dim IsPlugin = File.ResourceType.HasFlagF(ResourceTypes.Plugin) AndAlso Not (TargetResourceType = ResourceTypes.Mod AndAlso Not File.ResourceType.HasFlagF(ResourceTypes.Plugin))
        Dim SubFolderName = If(TargetResourceType = ResourceTypes.Plugin OrElse (File.ResourceType = ResourceTypes.Plugin), "plugins", If(IsPlugin AndAlso Not IO.Directory.Exists(IO.Path.Combine(SavedServerDir, "mods")), "plugins", "mods"))
        Dim TargetDir = IO.Path.Combine(SavedServerDir, SubFolderName)
        Try
            IO.Directory.CreateDirectory(TargetDir)
        Catch ex As Exception
            Hint($"无法创建服务端目标目录：{ex.Message}", HintType.Red)
            Return
        End Try
        Dim FileName = BuildFormattedFileName(File)
        Dim Target = IO.Path.Combine(TargetDir, FileName)
        Dim Desc = If(SubFolderName = "plugins", "服务端插件", "服务端 Mod ")
        Dim LoaderName As String = $"{Desc}装入服务端：{IO.Path.GetFileNameWithoutExtension(Target)} "
        Dim Loaders As New List(Of LoaderBase) From {
            New LoaderDownload("下载文件到服务端", {
                File.ToNetFile(Target, If(TargetResourceType = ResourceTypes.Any, ResourceVersion.DownloadReason.Dependency, ResourceVersion.DownloadReason.Standalone))
            }) With {.ProgressWeight = 6, .Block = True}
        }
        If Not String.IsNullOrEmpty(File.ExternalFallbackUrl) Then
            Loaders.Add(New LoaderTask(Of Integer, Integer)("校验安装包格式", Sub() ValidateDownloadedArchiveHeader(Target)) With {.ProgressWeight = 0.2})
        End If
        Dim FallbackUrl = File.ExternalFallbackUrl
        Dim Loader As New LoaderCombo(Of Integer)(LoaderName, Loaders) With {.OnStateChanged =
        Sub(MyLoader)
            LoaderStateChangedHintOnly(MyLoader)
            PromptExternalFallbackOnFailure(MyLoader, FallbackUrl)
        End Sub}
        Loader.Start(1)
        LoaderTaskbarAdd(Loader)
        FrmMain.BtnExtraDownload.ShowRefresh()
        FrmMain.BtnExtraDownload.Ribble()
        Hint($"已开始将 {FileName} 装入服务端 {SubFolderName}\ 目录", HintType.Green)
    End Sub

    '资源下载；整合包另存为
    Public Shared CachedFolder As New Dictionary(Of ResourceTypes, String) '仅在本次缓存的下载文件夹
    Public Sub Save_Click(sender As Object, e As EventArgs)
        Dim File As ResourceVersion = If(TypeOf sender Is MyListItem, sender, sender.Parent).Tag
        If TryRedirectExternalOnly(File) Then Return
        RunInNewThread(
        Sub()
            Try
                Dim Desc As String = Nothing
                Select Case File.ResourceType
                    Case ResourceTypes.ModPack : Desc = "整合包"
                    Case ResourceTypes.Mod : Desc = "Mod "
                    Case ResourceTypes.ResourcePack : Desc = "资源包"
                    Case ResourceTypes.Shader : Desc = "光影包"
                    Case ResourceTypes.DataPack : Desc = "数据包"
                    Case ResourceTypes.Plugin : Desc = "服务端插件"
                    Case ResourceTypes.Map : Desc = "地图"
                End Select
                '确认默认保存位置
                Dim DefaultFolder As String = Nothing
                If File.ResourceType = ResourceTypes.Plugin Then
                    Dim SavedServerDir As String = PageToolsServer.LoadSavedServerDir()
                    Dim ServersDir As String = Paths.Base & "PCL Linking\Servers\"
                    If CachedFolder.ContainsKey(File.ResourceType) Then
                        DefaultFolder = CachedFolder(File.ResourceType)
                    ElseIf Not String.IsNullOrWhiteSpace(SavedServerDir) AndAlso IO.Directory.Exists(SavedServerDir) Then
                        Dim PluginsDir = IO.Path.Combine(SavedServerDir, "plugins") & "\"
                        IO.Directory.CreateDirectory(PluginsDir)
                        DefaultFolder = PluginsDir
                    ElseIf DirectoryUtils.Exists(ServersDir) Then
                        DefaultFolder = ServersDir
                    Else
                        DefaultFolder = McFolderSelected
                    End If
                ElseIf File.ResourceType <> ResourceTypes.ModPack Then
                    Dim SubFolder As String = Nothing
                    Select Case File.ResourceType
                        Case ResourceTypes.Mod : SubFolder = "mods\"
                        Case ResourceTypes.ResourcePack : SubFolder = "resourcepacks\"
                        Case ResourceTypes.Shader : SubFolder = "shaderpacks\"
                        Case ResourceTypes.Map : SubFolder = "saves\"
                        Case ResourceTypes.DataPack : SubFolder = "" '导航到版本根目录
                    End Select
                    Dim IsInstanceSuitable As Func(Of McInstance, Boolean) = Nothing
                    '获取资源所需的加载器
                    Dim AllowedLoaders As ModLoaders = ModLoaders.None
                    If File.ModLoaders <> ModLoaders.None Then
                        AllowedLoaders = File.ModLoaders
                    ElseIf Project.ModLoaders <> ModLoaders.None Then
                        AllowedLoaders = Project.ModLoaders
                    End If
                    Logger.Info($"{Desc}要求的加载器种类：{AllowedLoaders}")
                    '判断某个版本是否符合资源要求
                    IsInstanceSuitable =
                    Function(Instance As McInstance)
                        If Not Instance.IsLoaded Then Instance.Load()
                        '只对 Mod 和数据包进行版本检测
                        If File.ResourceType = ResourceTypes.Mod OrElse File.ResourceType = ResourceTypes.DataPack Then
                            If Not File.GameVersions.Any( '判断是否有任何一个版本匹配
                            Function(FileVersion)
                                Dim FileIsSnapshot As Boolean = FileVersion.Contains("预览版") OrElse Not FileVersion.Contains(".")
                                Dim InstanceIsSnapshot As Boolean = Instance.Version.VanillaName.Contains("snapshot") OrElse Not Instance.Version.VanillaName.Contains(".")
                                If FileIsSnapshot <> InstanceIsSnapshot Then
                                    '只有一种是预览版
                                    Return False
                                ElseIf Not FileIsSnapshot AndAlso Not InstanceIsSnapshot Then
                                    '都不是预览版
                                    Return FileVersion = Instance.Version.VanillaName
                                Else
                                    '都是预览版
                                    Dim FileIsNewFormat As Boolean = FileVersion.Contains(".") AndAlso Val(FileVersion.BeforeFirst(".")) > 1
                                    Dim InstanceIsNewFormat As Boolean = Instance.Version.Drop > 250
                                    If FileIsNewFormat <> InstanceIsNewFormat Then
                                        '只有一种是新格式快照
                                        Return False
                                    ElseIf FileIsNewFormat AndAlso InstanceIsNewFormat Then
                                        '都是新格式快照
                                        Return FileVersion.BeforeFirst(" ") = Instance.Version.VanillaName.BeforeFirst("-")
                                    Else
                                        '都是旧格式快照
                                        If FileVersion.Contains("w") AndAlso Instance.Version.VanillaName.Contains("w") Then
                                            '都是 w 快照
                                            Return FileVersion = Instance.Version.VanillaName
                                        Else
                                            '不全是 w 快照
                                            Return True '只能假定相同，无法判断
                                        End If
                                    End If
                                End If
                            End Function) Then Return False
                        End If
                        '加载器
                        Return AllowedLoaders = ModLoaders.None OrElse AllowedLoaders.Flags.Intersect(Instance.Version.ModLoaders.Flags).Any
                    End Function
                    '获取常规资源默认下载位置
                    If CachedFolder.ContainsKey(File.ResourceType) Then
                        DefaultFolder = CachedFolder(File.ResourceType)
                        Logger.Info($"使用上次下载时的文件夹作为默认下载位置：{DefaultFolder}")
                    ElseIf McInstanceSelected IsNot Nothing AndAlso IsInstanceSuitable(McInstanceSelected) Then
                        DefaultFolder = $"{McInstanceSelected.PathIndie}{SubFolder}"
                        DirectoryUtils.Create(DefaultFolder)
                        Logger.Info($"使用当前版本作为默认下载位置：{DefaultFolder}")
                    Else
                        '查找所有可能的版本
                        Dim NeedLoad As Boolean = McInstanceListLoader.State <> LoadState.Finished
                        If NeedLoad Then
                            Hint("正在查找适合的游戏版本……")
                            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\", WaitForExit:=True)
                        End If
                        Dim SuitableInstanceList = McInstanceList.Values.SelectMany(Function(l) l).Where(Function(v) IsInstanceSuitable(v)).
                            Select(Function(v) DirectoryUtils.GetInfo($"{v.PathIndie}{SubFolder}"))
                        If SuitableInstanceList.Any Then
                            Dim SelectedInstance = SuitableInstanceList.
                                OrderByDescending(Function(Dir) If(Dir.Exists, Dir.LastWriteTimeUtc, Date.MinValue)). '先按文件夹更改时间降序
                                ThenByDescending(Function(Dir) If(Dir.Exists, Dir.GetFiles().Length, -1)). '再按文件夹中的文件数量降序
                                First()
                            DefaultFolder = PathUtils.RemoveExtendedPrefix(SelectedInstance.FullName)
                            DirectoryUtils.Create(DefaultFolder)
                            Logger.Info($"使用适合的游戏版本作为默认下载位置：{DefaultFolder}")
                        Else
                            DefaultFolder = McFolderSelected
                            If NeedLoad Then
                                Hint($"当前 MC 文件夹中没有找到合适的版本！")
                            Else
                                Logger.Info("由于当前版本不兼容，使用当前的 MC 文件夹作为默认下载位置")
                            End If
                        End If
                    End If
                End If
                '获取基本信息
                Dim FileName As String = BuildFormattedFileName(File)
                RunInUi(
                Sub()
                    '弹窗要求选择保存位置
                    Dim Target As String
                    Dim Ext As String = If(File.ResourceType = ResourceTypes.Mod OrElse File.ResourceType = ResourceTypes.Plugin,
                        If(PathUtils.GetExtension(File.FileName) = "litemod", "litemod", "jar"),
                        If(PathUtils.GetExtension(File.FileName) = "mrpack", "mrpack", "zip"))
                    Target = Dialogs.SaveFile("选择保存位置", FileName, DefaultFolder, {(Ext, Desc & "文件")})
                    If Target Is Nothing Then Return
                    '构造步骤加载器
                    Dim LoaderName As String = Desc & "下载：" & PathUtils.GetFileNameWithoutExtension(Target) & " "
                    If Not PathUtils.IsSame(PathUtils.RemoveLastPart(Target), DefaultFolder) Then CachedFolder(File.ResourceType) = PathUtils.RemoveLastPart(Target)
                    Dim Loaders As New List(Of LoaderBase)
                    Loaders.Add(New LoaderDownload("下载文件", {
                        File.ToNetFile(Target, If(TargetResourceType = ResourceTypes.Any, ResourceVersion.DownloadReason.Dependency, ResourceVersion.DownloadReason.Standalone))
                    }) With {.ProgressWeight = 6, .Block = True})
                    If Not String.IsNullOrEmpty(File.ExternalFallbackUrl) Then
                        Loaders.Add(New LoaderTask(Of Integer, Integer)("校验安装包格式", Sub() ValidateDownloadedArchiveHeader(Target)) With {.ProgressWeight = 0.2})
                    End If
                    Dim FallbackUrl = File.ExternalFallbackUrl
                    '启动
                    Dim Loader As New LoaderCombo(Of Integer)(LoaderName, Loaders) With {.OnStateChanged =
                    Sub(MyLoader)
                        LoaderStateChangedHintOnly(MyLoader)
                        PromptExternalFallbackOnFailure(MyLoader, FallbackUrl)
                        If MyLoader.State = LoadState.Finished AndAlso File.ResourceType = ResourceTypes.ModPack Then OpenExplorer(Target)
                    End Sub}
                    Loader.Start(1)
                    LoaderTaskbarAdd(Loader)
                    FrmMain.BtnExtraDownload.ShowRefresh()
                    FrmMain.BtnExtraDownload.Ribble()
                End Sub)
            Catch ex As Exception
                Logger.Error(ex, "保存资源文件失败")
            End Try
        End Sub, "Download Resource Detail Save")
    End Sub

    Private Sub BtnIntroWeb_Click(sender As Object, e As EventArgs) Handles BtnIntroWeb.Click
        OpenWebsite(Project.Website)
    End Sub
    Private Sub BtnIntroWiki_Click(sender As Object, e As EventArgs) Handles BtnIntroWiki.Click
        OpenWebsite("https://www.mcmod.cn/class/" & Project.WikiId & ".html")
    End Sub
    Private Sub BtnIntroCopy_Click(sender As Object, e As EventArgs) Handles BtnIntroCopy.Click
        ClipboardSet(ResourceItem.LabTitle.Text & ResourceItem.LabTitleRaw.Text)
    End Sub

    'Mod / 数据包 互相跳转
    Private Sub HintAlternative_Click() Handles HintAlternative.Click
        TargetResourceType = If(TargetResourceType = ResourceTypes.Mod, ResourceTypes.DataPack, ResourceTypes.Mod)
        FrmMain.PageCurrent.Additional(4) = TargetResourceType '加载器会从这里重新拿数据
        PageLoaderRestart(IsForceRestart:=True)
    End Sub

End Class
