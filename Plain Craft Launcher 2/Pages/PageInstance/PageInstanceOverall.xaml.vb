Public Class PageInstanceOverall

    Private IsLoad As Boolean = False
    Private Sub PageSetupLaunch_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded

        '重复加载部分
        PanBack.ScrollToHome()

        '更新设置
        Reload()

        '非重复加载部分
        If IsLoad Then Return
        IsLoad = True
        PanDisplay.TriggerForceResize()

    End Sub

    Public InstanceHeaderItem As MyListItem
    ''' <summary>
    ''' 确保当前页面上的信息已正确显示。
    ''' </summary>
    Private Sub Reload()
        AniControlEnabled += 1

        '刷新设置项目
        ComboDisplayType.SelectedIndex = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "DisplayType", McInstanceCardType.Auto)
        BtnDisplayStar.Text = If(PageInstanceLeft.Instance.IsStar, "从收藏夹中移除", "加入收藏夹")
        BtnFolderMods.Visibility = If(PageInstanceLeft.Instance.Modable, Visibility.Visible, Visibility.Collapsed)
        '刷新版本显示
        PanDisplayItem.Children.Clear()
        InstanceHeaderItem = PageInstanceLeft.Instance.ToListItem().Init()
        InstanceHeaderItem.IsHitTestVisible = False
        PanDisplayItem.Children.Add(InstanceHeaderItem)
        FrmMain.PageNameRefresh()
        '刷新版本图标
        ComboDisplayLogo.SelectedIndex = 0
        Dim Logo As String = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "Logo", "")
        Dim LogoCustom As Boolean = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "LogoCustom", "False")
        If LogoCustom Then
            For Each Selection As MyComboBoxItem In ComboDisplayLogo.Items
                If Selection.Tag = Logo OrElse (Selection.Tag = "PCL Linking\Logo.png" AndAlso Logo.EndsWithF("PCL Linking\Logo.png")) Then
                    ComboDisplayLogo.SelectedItem = Selection
                    Exit For
                End If
            Next
        End If

        '刷新游玩时长统计
        Dim PtRec = ModPlaytime.GetRecord(PageInstanceLeft.Instance)
        If PtRec Is Nothing Then
            LabPlaytimeSummary.Text = "累计游玩：0 分钟  ·  有效启动：0 次  ·  最近：尚未游玩"
        Else
            LabPlaytimeSummary.Text = $"累计游玩：{ModPlaytime.FormatDuration(PtRec.TotalSeconds)}  ·  有效启动：{PtRec.SessionCount} 次  ·  最近：{ModPlaytime.FormatLastPlayed(PtRec.LastPlayedUtc)}"
        End If

        AniControlEnabled -= 1
    End Sub

#Region "卡片：个性化"

    '版本分类
    Private Sub ComboDisplayType_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboDisplayType.SelectionChanged
        If Not (IsLoad AndAlso AniControlEnabled = 0) Then Return
        If ComboDisplayType.SelectedIndex <> 1 Then
            '改为不隐藏
            Try
                '若设置分类为可安装 Mod，则显示正常的 Mod 管理页面
                WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "DisplayType", ComboDisplayType.SelectedIndex)
                PageInstanceLeft.Instance.DisplayType = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "DisplayType", McInstanceCardType.Auto)
                FrmInstanceLeft.RefreshModDisabled()

                WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "") '要求刷新缓存
                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            Catch ex As Exception
                Logger.Error(ex, $"修改版本分类失败（{PageInstanceLeft.Instance.Name}）")
            End Try
            Reload() '更新 “打开 Mod 文件夹” 按钮
        Else
            '改为隐藏
            Try
                If Not Settings.Get(Of Boolean)("HintHide") Then
                    If MyMsgBox("确认要从版本列表中隐藏该版本吗？隐藏该版本后，它将不再出现于 PCL Linking 显示的版本列表中。" & vbCrLf & "此后，在版本列表页面按下 F11 才可以查看被隐藏的版本。", "隐藏版本提示",, "取消") <> 1 Then
                        ComboDisplayType.SelectedIndex = 0
                        Return
                    End If
                    Settings.Set("HintHide", True)
                End If
                WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "DisplayType", McInstanceCardType.Hidden)
                WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "") '要求刷新缓存
                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            Catch ex As Exception
                Logger.Error(ex, $"隐藏版本 {PageInstanceLeft.Instance.Name} 失败")
            End Try
        End If
    End Sub

    '更改描述
    Private Sub BtnDisplayDesc_Click(sender As Object, e As EventArgs) Handles BtnDisplayDesc.Click
        Try
            Dim OldInfo As String = ReadIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "CustomInfo")
            Dim NewInfo As String = MyMsgBoxInput("更改描述", "修改版本的描述文本，留空则使用 PCL Linking 的默认描述。", OldInfo, New ObjectModel.Collection(Of Validate), "默认描述")
            If NewInfo IsNot Nothing AndAlso OldInfo <> NewInfo Then WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "CustomInfo", NewInfo)
            PageInstanceLeft.Instance = New McInstance(PageInstanceLeft.Instance.Name).Load()
            Reload()
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, $"版本 {PageInstanceLeft.Instance.Name} 描述更改失败", LogBehavior.Alert)
        End Try
    End Sub

    '重命名版本
    Private Sub BtnDisplayRename_Click(sender As Object, e As EventArgs) Handles BtnDisplayRename.Click
        Try
            If McInstanceLock.IsLocked(PageInstanceLeft.Instance) Then
                Hint("该版本正在被修改，请稍后再重命名！", HintType.Red)
                Return
            End If
            '确认输入的新名称
            Dim OldName As String = PageInstanceLeft.Instance.Name
            '修改此部分的同时修改快速安装的版本名检测*
            Dim NewName As String = MyMsgBoxInput("重命名版本", "", OldName, New ObjectModel.Collection(Of Validate) From {
                New ValidateFolderName(McFolderSelected & "versions", IgnoreList:=New List(Of String) From {OldName})
            })
            If String.IsNullOrWhiteSpace(NewName) Then Return
            Dim NewInstance = PageInstanceLeft.Instance.Rename(NewName)
            '刷新与提示
            Hint("重命名成功！", HintType.Green)
            PageInstanceLeft.Instance = NewInstance
            If Not IsNothing(McInstanceSelected) AndAlso McInstanceSelected.Equals(PageInstanceLeft.Instance) Then WriteIni(McFolderSelected & "PCL.ini", "Version", NewName)
            Reload()
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, "重命名版本失败", LogBehavior.Alert)
        End Try
    End Sub

    '版本图标
    Private Sub ComboDisplayLogo_SelectionChanged() Handles ItemDisplayLogoCustom.PreviewMouseLeftButtonUp, ComboDisplayLogo.SelectionChanged
        If Not (IsLoad AndAlso AniControlEnabled = 0) Then Return
        '选择 自定义 时修改图片
        Try
            If ComboDisplayLogo.SelectedItem Is ItemDisplayLogoCustom Then
                Dim FileName As String = Dialogs.SelectFile("选择图片", False, filter:={({"png", "jpeg", "jpg", "gif", "webp"}, "常用图片文件")}).FirstOrDefault()
                If String.IsNullOrEmpty(FileName) Then
                    Reload() '还原选项
                    Return
                End If
                FileUtils.Copy(FileName, PageInstanceLeft.Instance.PathVersion & "PCL Linking\Logo.png")
            Else
                FileUtils.Delete(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Logo.png")
            End If
        Catch ex As Exception
            Logger.Error(ex, $"更改自定义版本图标失败（{PageInstanceLeft.Instance.Name}）")
        End Try
        '进行更改
        Try
            Dim NewLogo As String = ComboDisplayLogo.SelectedItem.Tag
            WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "Logo", NewLogo)
            WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "LogoCustom", Not NewLogo = "")
            '刷新显示
            WriteIni(McFolderSelected & "PCL.ini", "InstanceCache", "") '要求刷新缓存
            PageInstanceLeft.Instance = New McInstance(PageInstanceLeft.Instance.Name).Load()
            Reload()
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, $"更改版本图标失败（{PageInstanceLeft.Instance.Name}）")
        End Try
    End Sub

    '收藏夹
    Private Sub BtnDisplayStar_Click(sender As Object, e As EventArgs) Handles BtnDisplayStar.Click
        Try
            WriteIni(PageInstanceLeft.Instance.PathVersion & "PCL Linking\Setup.ini", "IsStar", Not PageInstanceLeft.Instance.IsStar)
            PageInstanceLeft.Instance = New McInstance(PageInstanceLeft.Instance.Name).Load()
            Reload()
            McInstanceListForceRefresh = True
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
        Catch ex As Exception
            Logger.Error(ex, $"版本 {PageInstanceLeft.Instance.Name} 收藏状态更改失败", LogBehavior.Alert)
        End Try
    End Sub

#End Region

#Region "卡片：快捷方式"

    '版本文件夹
    Private Sub BtnFolderVersion_Click() Handles BtnFolderVersion.Click
        OpenInstanceFolder(PageInstanceLeft.Instance)
    End Sub
    Public Shared Sub OpenInstanceFolder(Instance As McInstance)
        OpenExplorer(Instance.PathVersion)
    End Sub

    '存档文件夹
    Private Sub BtnFolderSaves_Click() Handles BtnFolderSaves.Click
        Dim Folder As String = PageInstanceLeft.Instance.PathIndie & "saves\"
        DirectoryUtils.Create(Folder)
        OpenExplorer(Folder)
    End Sub

    'Mod 文件夹
    Private Sub BtnFolderMods_Click() Handles BtnFolderMods.Click
        Dim Folder As String = PageInstanceLeft.Instance.PathIndie & "mods\"
        DirectoryUtils.Create(Folder)
        OpenExplorer(Folder)
    End Sub

    '截图文件夹
    Private Sub BtnFolderScreen_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnFolderScreen.Click
        Dim Folder As String = PageInstanceLeft.Instance.PathIndie & "screenshots\"
        DirectoryUtils.Create(Folder)
        OpenExplorer(Folder)
    End Sub

#End Region

#Region "卡片：管理"

    '创建桌面快捷方式
    Private Sub BtnManageShortcut_Click() Handles BtnManageShortcut.Click
        Try
            Dim Inst = PageInstanceLeft.Instance
            If Inst Is Nothing Then Return
            Dim Args As String = $"--launch ""{Inst.Name}"" --folder ""{McFolderSelected.TrimEnd("\"c)}"""
            Dim Lnk = CreateDesktopShortcut("启动 " & Inst.Name, Args, $"使用 PCL Linking 启动 {Inst.Name}")
            Hint($"已在桌面创建快捷方式：{IO.Path.GetFileName(Lnk)}", HintType.Green)
        Catch ex As Exception
            Hint($"创建快捷方式失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    '打开实时日志
    Private Sub BtnManageGameLog_Click() Handles BtnManageGameLog.Click
        WinGameLog.ShowOrActivate()
    End Sub

    '实例对比 (Diff)
    Private Sub BtnManageInstanceDiff_Click() Handles BtnManageInstanceDiff.Click
        Dim CurInst = PageInstanceLeft.Instance
        If CurInst Is Nothing Then Return
        Dim Others = McInstanceList.Values.SelectMany(Function(l) l).
            Where(Function(inst) inst IsNot Nothing AndAlso inst.State <> McInstance.McInstanceState.Error AndAlso Not PathUtils.IsSame(inst.PathVersion, CurInst.PathVersion)).
            GroupBy(Function(inst) inst.PathVersion, StringComparer.OrdinalIgnoreCase).
            Select(Function(g) g.First()).
            OrderBy(Function(inst) inst.Name).ToList()
        If Not Others.Any() AndAlso IO.Directory.Exists(McFolderSelected & "versions\") Then
            For Each VerDir In IO.Directory.GetDirectories(McFolderSelected & "versions\")
                Try
                    Dim Inst = New McInstance(IO.Path.GetFileName(VerDir)).Load()
                    If Inst.State <> McInstance.McInstanceState.Error AndAlso Not PathUtils.IsSame(Inst.PathVersion, CurInst.PathVersion) Then
                        Others.Add(Inst)
                    End If
                Catch
                End Try
            Next
        End If
        If Not Others.Any() Then
            Hint("当前 Minecraft 文件夹下没有其他可对比的游戏实例！", HintType.Blue)
            Return
        End If

        Dim Radios As New List(Of IMyRadio)
        For Each Inst In Others
            Radios.Add(New MyRadioBox With {.Text = $"{Inst.Name}  ({Inst.Version.VanillaName} · {Inst.Version.ModLoaders})"})
        Next
        Dim SelectedIdx = MyMsgBoxSelect(Radios.ToArray(), $"选择要与「{CurInst.Name}」对比的目标实例：", "开始对比", "取消")
        If SelectedIdx Is Nothing Then Return
        Dim TargetInst = Others(SelectedIdx.Value)

        BtnManageInstanceDiff.IsEnabled = False
        Hint($"正在对比「{CurInst.Name}」与「{TargetInst.Name}」的 Mod 与 Config 差异……", HintType.Blue)
        RunInNewThread(
        Sub()
            Try
                If Not CurInst.IsLoaded Then CurInst.Load()
                If Not TargetInst.IsLoaded Then TargetInst.Load()

                Dim DiffSummary = InstanceDiffCore.CompareModAndConfigFolders(CurInst.PathIndie, TargetInst.PathIndie)
                Dim ExactModCount = DiffSummary.ExactModCount
                Dim DiffVersionMods = DiffSummary.DiffVersionMods
                Dim OnlyInCurMods = DiffSummary.OnlyInCurrentMods
                Dim OnlyInOtherMods = DiffSummary.OnlyInOtherMods
                Dim ExactCfgCount = DiffSummary.ExactConfigCount
                Dim DiffCfgs = DiffSummary.DiffConfigs
                Dim OnlyCurCfgCount = DiffSummary.OnlyInCurrentConfigsCount
                Dim OnlyOtherCfgCount = DiffSummary.OnlyInOtherConfigsCount

                Dim Sb As New Text.StringBuilder()
                Sb.AppendLine($"当前实例：{CurInst.Name}（{CurInst.Version.VanillaName} · {CurInst.Version.ModLoaders}）")
                Sb.AppendLine($"对比实例：{TargetInst.Name}（{TargetInst.Version.VanillaName} · {TargetInst.Version.ModLoaders}）")
                Sb.AppendLine()
                Sb.AppendLine($"【Mod 对比】完全一致 {ExactModCount} 个 | 同名不同内容 {DiffVersionMods.Count} 个 | 仅本实例有 {OnlyInCurMods.Count} 个 | 仅对方有 {OnlyInOtherMods.Count} 个")
                If OnlyInOtherMods.Any() Then
                    Sb.AppendLine("仅对方实例有（可一键复制补齐）：")
                    For Each Om In OnlyInOtherMods.Take(8) : Sb.AppendLine("• " & Om.FileName) : Next
                    If OnlyInOtherMods.Count > 8 Then Sb.AppendLine($"……等共 {OnlyInOtherMods.Count} 个")
                End If
                If OnlyInCurMods.Any() Then
                    Sb.AppendLine("仅本实例有：")
                    For Each Cm In OnlyInCurMods.Take(6) : Sb.AppendLine("• " & Cm) : Next
                    If OnlyInCurMods.Count > 6 Then Sb.AppendLine($"……等共 {OnlyInCurMods.Count} 个")
                End If
                If DiffVersionMods.Any() Then
                    Sb.AppendLine("同名但哈希不同：" & String.Join("、", DiffVersionMods.Take(5)))
                End If
                Sb.AppendLine()
                Sb.AppendLine($"【Config 配置对比】完全一致 {ExactCfgCount} 项 | 内容有差异 {DiffCfgs.Count} 项 | 仅本实例 {OnlyCurCfgCount} 项 | 仅对方 {OnlyOtherCfgCount} 项")
                If DiffCfgs.Any() Then
                    For Each D In DiffCfgs.Take(6) : Sb.AppendLine("• 差异配置：" & D) : Next
                    If DiffCfgs.Count > 6 Then Sb.AppendLine($"……等共 {DiffCfgs.Count} 项")
                End If
                Dim ReportText = Sb.ToString().TrimEnd()

                RunInUi(
                Sub()
                    BtnManageInstanceDiff.IsEnabled = True
                    If OnlyInOtherMods.Any() Then
                        Dim Choice = MyMsgBox(ReportText, "实例配置对比报告 (Diff)", "将对方独有 Mod 复制到本实例", "复制对比报告", "关闭")
                        If Choice = 1 Then
                            Dim DestModsDir = CurInst.PathIndie & "mods\"
                            IO.Directory.CreateDirectory(DestModsDir)
                            Dim Copied As Integer = 0
                            For Each Om In OnlyInOtherMods
                                Try
                                    IO.File.Copy(Om.FilePath, IO.Path.Combine(DestModsDir, Om.FileName), False)
                                    Copied += 1
                                Catch
                                End Try
                            Next
                            Hint($"已从「{TargetInst.Name}」复制 {Copied} 个独有 Mod 到「{CurInst.Name}」！", HintType.Green)
                        ElseIf Choice = 2 Then
                            ClipboardSet(ReportText, False)
                            Hint("已复制实例对比报告！", HintType.Green)
                        End If
                    Else
                        Dim Choice = MyMsgBox(ReportText, "实例配置对比报告 (Diff)", "复制对比报告", "关闭")
                        If Choice = 1 Then
                            ClipboardSet(ReportText, False)
                            Hint("已复制实例对比报告！", HintType.Green)
                        End If
                    End If
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "实例对比失败")
                RunInUi(
                Sub()
                    BtnManageInstanceDiff.IsEnabled = True
                    Hint($"实例对比失败：{ex.Message}", HintType.Red)
                End Sub)
            End Try
        End Sub, "Instance Diff")
    End Sub

    '一键瘦身清理
    Private Sub BtnManageInstanceSlim_Click() Handles BtnManageInstanceSlim.Click
        Dim CurInst = PageInstanceLeft.Instance
        If CurInst Is Nothing Then Return
        If McInstanceLock.IsLocked(CurInst) Then
            Hint("该版本正在运行或被修改，请先退出游戏后再清理！", HintType.Red)
            Return
        End If
        BtnManageInstanceSlim.IsEnabled = False
        Hint($"正在扫描实例「{CurInst.Name}」的冗余日志、崩溃报告与临时文件……", HintType.Blue)

        RunInNewThread(
        Sub()
            Try
                Dim IndieRoot = CurInst.PathIndie.TrimEnd("\"c)
                Dim Categories As New List(Of (Label As String, Files As List(Of IO.FileInfo)))
                Dim CollectMatching =
                Function(DirRel As String, Predicate As Func(Of IO.FileInfo, Boolean), Recursive As Boolean) As List(Of IO.FileInfo)
                    Dim Res As New List(Of IO.FileInfo)
                    Dim TargetDir = If(String.IsNullOrEmpty(DirRel), IndieRoot, IO.Path.Combine(IndieRoot, DirRel))
                    If Not IO.Directory.Exists(TargetDir) Then Return Res
                    Try
                        For Each F In New IO.DirectoryInfo(TargetDir).GetFiles("*", If(Recursive, IO.SearchOption.AllDirectories, IO.SearchOption.TopDirectoryOnly))
                            If Predicate(F) Then Res.Add(F)
                        Next
                    Catch
                    End Try
                    Return Res
                End Function

                Categories.Add(("历史压缩日志与旧日志 (logs 保留 latest.log)", CollectMatching("logs", Function(f) Not f.Name.Equals("latest.log", StringComparison.OrdinalIgnoreCase) AndAlso Not f.Name.Equals("debug.log", StringComparison.OrdinalIgnoreCase) AndAlso (f.Name.EndsWith(".log.gz", StringComparison.OrdinalIgnoreCase) OrElse f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)), False)))
                Categories.Add(("历史崩溃报告 (crash-reports)", CollectMatching("crash-reports", Function(f) True, True)))
                Categories.Add(("JVM 崩溃转储 (hs_err / replay)", CollectMatching("", Function(f) (f.Name.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase) OrElse f.Name.StartsWith("replay_pid", StringComparison.OrdinalIgnoreCase)) AndAlso f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase), False)))
                Categories.Add(("调试与 Mixin 转储 (debug / .mixin.out)", CollectMatching("debug", Function(f) True, True).Concat(CollectMatching(".mixin.out", Function(f) True, True)).ToList()))
                Categories.Add(("残留临时文件 (*.tmp / *.lmltmp)", CollectMatching("", Function(f) f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) OrElse f.Extension.Equals(".lmltmp", StringComparison.OrdinalIgnoreCase), True)))

                Dim TotalFiles = Categories.Sum(Function(c) c.Files.Count)
                Dim TotalBytes = Categories.Sum(Function(c) c.Files.Sum(Function(f) f.Length))

                RunInUi(
                Sub()
                    BtnManageInstanceSlim.IsEnabled = True
                    If TotalFiles = 0 Then
                        MyMsgBox($"实例「{CurInst.Name}」没有检测到历史崩溃报告、旧日志归档或残留临时文件，非常干净！", "实例瘦身扫描完成", "好的")
                        Return
                    End If
                    Dim Sb As New Text.StringBuilder()
                    Sb.AppendLine($"实例「{CurInst.Name}」共扫描到 {TotalFiles} 个可安全清理的冗余文件，预计释放 {StringUtils.FormatByteSize(TotalBytes)} 磁盘空间：")
                    Sb.AppendLine()
                    For Each Cat In Categories
                        If Not Cat.Files.Any() Then Continue For
                        Dim CatBytes = Cat.Files.Sum(Function(f) f.Length)
                        Sb.AppendLine($"• {Cat.Label}：{Cat.Files.Count} 个文件（{StringUtils.FormatByteSize(CatBytes)}）")
                    Next
                    If MyMsgBox(Sb.ToString().TrimEnd(), "确认一键瘦身清理", "立即清理释放空间", "取消") <> 1 Then Return

                    Dim DeletedFiles As Integer = 0
                    Dim FreedBytes As Long = 0
                    For Each Cat In Categories
                        For Each F In Cat.Files
                            Try
                                Dim Len = F.Length
                                F.Delete()
                                DeletedFiles += 1
                                FreedBytes += Len
                            Catch
                            End Try
                        Next
                    Next
                    Hint($"清理完成：已删除 {DeletedFiles} 个冗余文件，释放 {StringUtils.FormatByteSize(FreedBytes)}！", HintType.Green)
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "实例一键瘦身清理失败")
                RunInUi(
                Sub()
                    BtnManageInstanceSlim.IsEnabled = True
                    Hint($"瘦身扫描失败：{ex.Message}", HintType.Red)
                End Sub)
            End Try
        End Sub, "Instance Slim Clean")
    End Sub

    '导出启动脚本
    Private Sub BtnManageScript_Click() Handles BtnManageScript.Click
        Try
            '弹窗要求指定脚本的保存位置
            Dim SavePath As String = Dialogs.SaveFile("选择脚本保存位置", "启动 " & PageInstanceLeft.Instance.Name & ".bat", filter:={("bat", "批处理文件")})
            If SavePath Is Nothing Then Return
            '检查中断（等玩家选完弹窗指不定任务就结束了呢……）
            If McLaunchLoader.State = LoadState.Loading Then
                Hint("请在当前启动任务结束后再试！", HintType.Red)
                Return
            End If
            '生成脚本
            If McLaunchStart(New McLaunchOptions With {.SaveBatch = SavePath, .Instance = PageInstanceLeft.Instance}) Then
                If Settings.Get(Of McLoginType)("LoginType") = McLoginType.Legacy Then
                    Hint("正在导出启动脚本……")
                Else
                    Hint("正在导出启动脚本……（注意，使用脚本启动可能会导致登录失效！）")
                End If
            End If
        Catch ex As Exception
            Logger.Error(ex, $"导出启动脚本失败（{PageInstanceLeft.Instance.Name}）", LogBehavior.Alert)
        End Try
    End Sub

    '补全文件
    Private Sub BtnManageCheck_Click(sender As Object, e As EventArgs) Handles BtnManageCheck.Click
        Try
            If McInstanceLock.IsLocked(PageInstanceLeft.Instance) Then
                Hint("该版本正在被修改，请稍后再补全文件！", HintType.Red)
                Return
            End If
            '忽略文件检查提示
            If ShouldIgnoreFileCheck(PageInstanceLeft.Instance) Then
                Hint("请先关闭 [版本设置 → 设置 → 高级启动选项 → 关闭文件校验]，然后再尝试补全文件！", HintType.Blue)
                Return
            End If
            '重复任务检查
            For Each OngoingLoader In LoaderTaskbar
                If OngoingLoader.Name <> PageInstanceLeft.Instance.Name & " 文件补全" Then Continue For
                Hint("正在处理中，请稍候！", HintType.Red)
                Return
            Next
            '启动
            Dim Loader As New LoaderCombo(Of String)(PageInstanceLeft.Instance.Name & " 文件补全", DlClientFix(PageInstanceLeft.Instance, True, False))
            Loader.OnStateChanged =
            Sub()
                Select Case Loader.State
                    Case LoadState.Finished
                        Hint(Loader.Name & "成功！", HintType.Green)
                    Case LoadState.Failed
                        Hint(Loader.Name & "失败：" & Loader.Error.GetDisplay(False), HintType.Red)
                    Case LoadState.Canceled
                        Hint(Loader.Name & "已取消！", HintType.Blue)
                End Select
            End Sub
            Loader.Start(PageInstanceLeft.Instance.Name)
            LoaderTaskbarAdd(Loader)
            FrmMain.BtnExtraDownload.ShowRefresh()
            FrmMain.BtnExtraDownload.Ribble()
        Catch ex As Exception
            Logger.Error(ex, $"尝试补全文件失败（{PageInstanceLeft.Instance.Name}）", LogBehavior.Alert)
        End Try
    End Sub

    '删除版本
    '修改此代码时，同时修改 PageSelectRight 中的代码
    Private Sub BtnManageDelete_Click(sender As Object, e As EventArgs) Handles BtnManageDelete.Click
        Try
            If McInstanceLock.IsLocked(PageInstanceLeft.Instance) Then
                Hint("该版本正在被修改，请稍后再删除！", HintType.Red)
                Return
            End If
            Dim IsShiftPressed As Boolean = My.Computer.Keyboard.ShiftKeyDown
            Dim IsHintIndie As Boolean = PageInstanceLeft.Instance.State <> McInstance.McInstanceState.Error AndAlso PageInstanceLeft.Instance.PathIndie <> McFolderSelected
            Dim SavesFolder = DirectoryUtils.GetInfo(PageInstanceLeft.Instance.PathIndie & "saves\")
            Dim SaveEntries = If(IsHintIndie AndAlso SavesFolder.Exists, SavesFolder.EnumerateDirectories.OrderByDescending(Function(Save) Save.LastWriteTime).Select(Function(Save) $"{Save.Name}（上次修改：{StringUtils.FormatTimeSpan(Save.LastWriteTime - Date.Now, False)}）").ToList, New List(Of String))
            Select Case MyMsgBox($"你确定要{If(IsShiftPressed, "永久", "")}删除版本 {PageInstanceLeft.Instance.Name} 吗？" &
                        If(IsHintIndie, vbCrLf & "该版本对应的存档、资源包、Mod 等文件也将被一并删除！" &
                            If(SaveEntries.Any, vbCrLf & vbCrLf & "这会删除以下存档：" & vbCrLf & "· " & SaveEntries.Join(vbCrLf & "· "), ""), ""),
                        "版本删除确认", , "取消",, IsHintIndie OrElse IsShiftPressed)
                Case 1
                    PageInstanceLeft.Instance.ResetSettingsCache()
                    DirectoryUtils.Delete(PageInstanceLeft.Instance.PathVersion, Not IsShiftPressed)
                    Hint("已删除版本 " & PageInstanceLeft.Instance.Name & "！", HintType.Green)
                Case 2
                    Return
            End Select
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            FrmMain.PageBack()
        Catch ex As OperationCanceledException
            Logger.Warn(ex, $"删除版本 {PageInstanceLeft.Instance.Name} 被主动取消")
        Catch ex As Exception
            Logger.Error(ex, $"删除版本 {PageInstanceLeft.Instance.Name} 失败", LogBehavior.Alert)
        End Try
    End Sub

#End Region

#Region "游玩时长排行"

    Private Sub BtnPlaytimeRanking_Click() Handles BtnPlaytimeRanking.Click
        Dim RankingText = ModPlaytime.BuildGlobalRankingText(PageInstanceLeft.Instance)
        MyMsgBox(RankingText, "全部实例游玩统计排行", "关闭")
    End Sub

#End Region

End Class
