Public Class PageDownloadPresets
    Implements IRefreshable

    Private Presets As New List(Of ModPresetPack)
    Private CurrentPresetId As String = Nothing
    Private IsUpdatingUi As Boolean = False
    Private IsFirstLoaded As Boolean = False
    Private IsBusyDeploying As Boolean = False
    Private CurrentSearchLoader As LoaderTask(Of ResourceSearcher.SearchRequest, Integer) = Nothing

    Private Sub PageDownloadPresets_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If Not IsFirstLoaded Then
            IsFirstLoaded = True
            IsUpdatingUi = True
            Try
                ComboDeployVersion.SelectedIndex = -1
                ComboDeployVersion.Text = ""
                TextDeployName.Text = ""
            Finally
                IsUpdatingUi = False
            End Try
            ApplyDefaultSourceFilter()
            LoadAllPresets()
            InitVersionDropdownAsync()
        End If
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        LoadAllPresets()
        InitVersionDropdownAsync(ForceRestart:=True)
        Hint("正在刷新常用模组清单……", Log:=False)
    End Sub

    Public Sub ApplyDefaultSourceFilter()
        If ComboAddSource Is Nothing OrElse ComboAddSource.Items.Count = 0 Then Return
        Dim TargetTag As Integer = Settings.Get(Of Integer)("ToolDownloadResourceSource")
        For Each Item As MyComboBoxItem In ComboAddSource.Items
            If Val(Item.Tag) = TargetTag Then
                ComboAddSource.SelectedItem = Item
                Return
            End If
        Next
        ComboAddSource.SelectedIndex = 0
    End Sub

    Private Sub InitVersionDropdownAsync(Optional ForceRestart As Boolean = False)
        RunInNewThread(
        Sub()
            Try
                If DlClientListLoader.State <> LoadState.Finished OrElse ForceRestart Then
                    DlClientListLoader.WaitForExit(IsForceRestart:=ForceRestart)
                End If
                If DlClientListLoader.Output.Value Is Nothing Then Return
                Dim Releases = CType(DlClientListLoader.Output.Value("versions"), JArray).
                    Where(Function(v) v("type")?.ToString() = "release").
                    Select(Function(v) v("id").ToString()).
                    Take(60).ToList()
                RunInUi(
                Sub()
                    IsUpdatingUi = True
                    Try
                        Dim OldText = ComboDeployVersion.Text
                        ComboDeployVersion.Items.Clear()
                        For Each Ver In Releases
                            ComboDeployVersion.Items.Add(New MyComboBoxItem With {.Content = Ver})
                        Next
                        ComboDeployVersion.SelectedIndex = -1
                        ComboDeployVersion.Text = If(String.IsNullOrWhiteSpace(OldText), "", OldText)
                    Finally
                        IsUpdatingUi = False
                    End Try
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "加载常用清单 MC 版本列表失败")
            End Try
        End Sub, "Preset Load McVersions")
    End Sub

    Private ReadOnly Property CurrentPreset As ModPresetPack
        Get
            Return Presets.FirstOrDefault(Function(p) p.Id = CurrentPresetId)
        End Get
    End Property

    Public Sub LoadAllPresets()
        Presets = LoadPresetPacks()
        If Not Presets.Any() Then
            CurrentPresetId = Nothing
        ElseIf CurrentPresetId Is Nothing OrElse Not Presets.Any(Function(p) p.Id = CurrentPresetId) Then
            CurrentPresetId = Presets(0).Id
        End If
        RefreshPresetCombo()
        RefreshCurrentPresetView()
        BackfillPresetLoadersAsync()
    End Sub

    Private Sub BackfillPresetLoadersAsync()
        Dim NeedBackfill = Presets.SelectMany(Function(p) p.Items).
            Where(Function(i) CType(i.ResourceType, ResourceTypes) = ResourceTypes.Mod AndAlso i.SupportedLoaders = CInt(ModLoaders.None)).
            ToList()
        If Not NeedBackfill.Any() Then Return
        RunInNewThread(
        Sub()
            Dim Changed As Boolean = False
            For Each Entry In NeedBackfill
                Try
                    Dim Proj = Entry.ToProject()
                    If Proj IsNot Nothing AndAlso Proj.ModLoaders <> ModLoaders.None Then
                        Entry.SupportedLoaders = CInt(Proj.ModLoaders)
                        Changed = True
                    End If
                Catch
                End Try
            Next
            If Changed Then
                SavePresetPacks(Presets)
                RunInUi(Sub() RefreshCurrentPresetView())
            End If
        End Sub, "Preset Backfill Loaders")
    End Sub

    Private Sub RefreshPresetCombo()
        IsUpdatingUi = True
        Try
            ComboPreset.Items.Clear()
            For Each Pack In Presets
                Dim Item As New MyComboBoxItem With {.Content = $"{Pack.Name}（{Pack.Items.Count} 项）", .Tag = Pack.Id}
                ComboPreset.Items.Add(Item)
                If Pack.Id = CurrentPresetId Then ComboPreset.SelectedItem = Item
            Next
            Dim HasPreset = Presets.Any()
            ComboPreset.IsEnabled = HasPreset
            BtnRenamePreset.IsEnabled = HasPreset
            BtnDeletePreset.IsEnabled = HasPreset
            If BtnExportShareCode IsNot Nothing Then BtnExportShareCode.IsEnabled = HasPreset
            If BtnExportJsonFile IsNot Nothing Then BtnExportJsonFile.IsEnabled = HasPreset
            HintNoPreset.Visibility = If(HasPreset, Visibility.Collapsed, Visibility.Visible)
            CardDeploy.IsEnabled = HasPreset
            CardPresetItems.IsEnabled = HasPreset
            CardAddSearch.IsEnabled = HasPreset
        Finally
            IsUpdatingUi = False
        End Try
    End Sub

    Private Sub ComboPreset_SelectionChanged() Handles ComboPreset.SelectionChanged
        If IsUpdatingUi Then Return
        Dim Selected = TryCast(ComboPreset.SelectedItem, MyComboBoxItem)
        If Selected Is Nothing Then Return
        CurrentPresetId = CStr(Selected.Tag)
        RefreshCurrentPresetView()
    End Sub

    Private Function GetSelectedDeployLoader() As ModLoaders
        If ComboDeployLoader?.SelectedItem Is Nothing Then Return ModLoaders.None
        Return CType(CInt(Val(CType(ComboDeployLoader.SelectedItem, MyComboBoxItem).Tag)), ModLoaders)
    End Function

    Public Shared Function GetLoaderSingleIcon(Loader As ModLoaders) As String
        Select Case Loader
            Case ModLoaders.Fabric : Return "pack://application:,,,/images/Blocks/Fabric.png"
            Case ModLoaders.NeoForge : Return "pack://application:,,,/images/Blocks/NeoForge.png"
            Case ModLoaders.Forge : Return "pack://application:,,,/images/Blocks/Anvil.png"
            Case ModLoaders.LiteLoader : Return "pack://application:,,,/images/Blocks/Egg.png"
            Case Else : Return "pack://application:,,,/images/Blocks/CommandBlock.png"
        End Select
    End Function

    Public Shared Function GetPresetItemLogo(Supported As ModLoaders, ResType As ResourceTypes, SelectedLoader As ModLoaders) As String
        If ResType = ResourceTypes.ResourcePack Then Return "pack://application:,,,/images/Blocks/Grass.png"
        If SelectedLoader <> ModLoaders.None AndAlso (Supported = ModLoaders.None OrElse Supported.HasFlagF(SelectedLoader)) Then
            Return GetLoaderSingleIcon(SelectedLoader)
        End If
        If Supported.HasFlagF(ModLoaders.Fabric) Then Return GetLoaderSingleIcon(ModLoaders.Fabric)
        If Supported.HasFlagF(ModLoaders.NeoForge) Then Return GetLoaderSingleIcon(ModLoaders.NeoForge)
        If Supported.HasFlagF(ModLoaders.Forge) Then Return GetLoaderSingleIcon(ModLoaders.Forge)
        If Supported.HasFlagF(ModLoaders.LiteLoader) Then Return GetLoaderSingleIcon(ModLoaders.LiteLoader)
        Return "pack://application:,,,/images/Blocks/CommandBlock.png"
    End Function

    Private Shared Function FormatLoaderNames(Loaders As ModLoaders) As String
        If Loaders = ModLoaders.None Then Return ""
        Dim Names As New List(Of String)
        If Loaders.HasFlagF(ModLoaders.Fabric) Then Names.Add("Fabric")
        If Loaders.HasFlagF(ModLoaders.NeoForge) Then Names.Add("NeoForge")
        If Loaders.HasFlagF(ModLoaders.Forge) Then Names.Add("Forge")
        If Loaders.HasFlagF(ModLoaders.LiteLoader) Then Names.Add("LiteLoader")
        Return String.Join("/", Names)
    End Function

    Private Shared Function GetLoaderDisplayName(Loader As ModLoaders) As String
        Select Case Loader
            Case ModLoaders.Fabric : Return "Fabric"
            Case ModLoaders.NeoForge : Return "NeoForge"
            Case ModLoaders.Forge : Return "Forge"
            Case ModLoaders.LiteLoader : Return "LiteLoader"
            Case Else : Return "原版"
        End Select
    End Function

    Private Sub CheckLoaderCompatibility(Optional ShowToast As Boolean = False)
        If HintLoaderCompat Is Nothing Then Return
        Dim Pack = CurrentPreset
        If Pack Is Nothing OrElse Not Pack.Items.Any() Then
            HintLoaderCompat.Visibility = Visibility.Collapsed
            Return
        End If
        Dim TargetLoader = GetSelectedDeployLoader()
        Dim ModItems = Pack.Items.Where(Function(i) CType(i.ResourceType, ResourceTypes) = ResourceTypes.Mod).ToList()
        If Not ModItems.Any() Then
            HintLoaderCompat.Visibility = Visibility.Collapsed
            Return
        End If
        If TargetLoader = ModLoaders.None Then
            Dim PreviewNames = String.Join("、", ModItems.Take(4).Select(Function(x) x.Title)) & If(ModItems.Count > 4, " 等", "")
            HintLoaderCompat.Theme = MyHint.Themes.Yellow
            HintLoaderCompat.Text = $"当前选择为「原版」，以下 {ModItems.Count} 个 Mod 无法在原版运行（装配时将跳过 Mod）：{PreviewNames}"
            HintLoaderCompat.Visibility = Visibility.Visible
            If ShowToast Then Hint($"切换为「原版」：清单中的 {ModItems.Count} 个 Mod 将不兼容！", HintType.Red)
            Return
        End If

        Dim Incompat = ModItems.Where(Function(i) i.SupportedLoaders <> CInt(ModLoaders.None) AndAlso Not CType(i.SupportedLoaders, ModLoaders).HasFlagF(TargetLoader)).ToList()
        Dim Unknown = ModItems.Where(Function(i) i.SupportedLoaders = CInt(ModLoaders.None)).ToList()
        Dim LoaderLabel = GetLoaderDisplayName(TargetLoader)
        If Incompat.Any() Then
            Dim Preview = String.Join("、", Incompat.Take(4).Select(Function(x) $"{x.Title}({FormatLoaderNames(CType(x.SupportedLoaders, ModLoaders))})")) & If(Incompat.Count > 4, " 等", "")
            HintLoaderCompat.Theme = MyHint.Themes.Red
            HintLoaderCompat.Text = $"已切换为 {LoaderLabel}，检测到 {Incompat.Count} 个 Mod 可能不兼容：{Preview}"
            HintLoaderCompat.Visibility = Visibility.Visible
            If ShowToast Then Hint($"注意：{Incompat.Count} 个 Mod 可能与 {LoaderLabel} 不兼容！", HintType.Red)
        ElseIf Unknown.Any() Then
            HintLoaderCompat.Theme = MyHint.Themes.Yellow
            HintLoaderCompat.Text = $"已切换为 {LoaderLabel}，部分 Mod 暂未获取到加载器标识，可能存在不兼容情况，下载前将自动校验。"
            HintLoaderCompat.Visibility = Visibility.Visible
            If ShowToast Then Hint($"已切换为 {LoaderLabel}，部分 Mod 可能不兼容，点击下载时将自动校验。", HintType.Blue)
        Else
            HintLoaderCompat.Visibility = Visibility.Collapsed
        End If
    End Sub

    Private Sub RefreshCurrentPresetView()
        Dim Pack = CurrentPreset
        PanPresetItems.Children.Clear()
        If Pack Is Nothing Then
            CardPresetItems.Title = "当前清单已添加的资源 (0 项)"
            HintEmptyItems.Visibility = Visibility.Visible
            CheckLoaderCompatibility(ShowToast:=False)
            Return
        End If
        CardPresetItems.Title = $"当前清单已添加的资源 ({Pack.Items.Count} 项)"
        HintEmptyItems.Visibility = If(Pack.Items.Any(), Visibility.Collapsed, Visibility.Visible)
        Dim SelectedLoader = GetSelectedDeployLoader()
        For Each Entry In Pack.Items.ToList()
            Dim LocalEntry = Entry
            Dim ResType As ResourceTypes = CType(LocalEntry.ResourceType, ResourceTypes)
            Dim Supported As ModLoaders = CType(LocalEntry.SupportedLoaders, ModLoaders)
            Dim TypeLabel As String = If(ResType = ResourceTypes.ResourcePack, "材质包", "Mod")
            Dim LoaderTag As String = If(ResType = ResourceTypes.Mod, FormatLoaderNames(Supported), "")
            Dim TypeDesc As String = If(String.IsNullOrEmpty(LoaderTag), TypeLabel, $"{TypeLabel} · {LoaderTag}")
            Dim CompatNote As String = ""
            If ResType = ResourceTypes.Mod Then
                If SelectedLoader = ModLoaders.None Then
                    CompatNote = " · 原版不可用"
                ElseIf Supported <> ModLoaders.None AndAlso Not Supported.HasFlagF(SelectedLoader) Then
                    CompatNote = $" · 可能不兼容 {GetLoaderDisplayName(SelectedLoader)}"
                End If
            End If
            Dim PlatformLabel As String = If(CType(LocalEntry.Platform, ResourcePlatforms) = ResourcePlatforms.CurseForge, "CurseForge", "Modrinth")
            Dim Row As New MyListItem With {
                .Title = LocalEntry.Title,
                .Info = $"[{TypeDesc}] 来自 {PlatformLabel}" & If(String.IsNullOrEmpty(LocalEntry.Slug), "", $" · {LocalEntry.Slug}") & CompatNote,
                .Height = 44,
                .Type = MyListItem.CheckType.Clickable,
                .Logo = GetPresetItemLogo(Supported, ResType, SelectedLoader)
            }
            Dim BtnOpen As New MyIconButton With {.ToolTip = "打开项目网页", .Logo = Logo.IconButtonOpen}
            AddHandler BtnOpen.Click, Sub() If Not String.IsNullOrEmpty(LocalEntry.Website) Then OpenWebsite(LocalEntry.Website)
            Dim BtnRemove As New MyIconButton With {.ToolTip = "从清单移除", .Logo = Logo.IconButtonDelete}
            AddHandler BtnRemove.Click,
            Sub()
                Pack.Items.RemoveAll(Function(i) i.ProjectId = LocalEntry.ProjectId AndAlso i.Platform = LocalEntry.Platform AndAlso i.ResourceType = LocalEntry.ResourceType)
                SavePresetPacks(Presets)
                RefreshPresetCombo()
                RefreshCurrentPresetView()
                Hint($"已从清单移除 {LocalEntry.Title}", HintType.Green)
            End Sub
            Row.Buttons = {BtnOpen, BtnRemove}
            AddHandler Row.Click, Sub() If Not String.IsNullOrEmpty(LocalEntry.Website) Then OpenWebsite(LocalEntry.Website)
            PanPresetItems.Children.Add(Row)
        Next
        CheckLoaderCompatibility(ShowToast:=False)
    End Sub

    Private Sub BtnNewPreset_Click() Handles BtnNewPreset.Click
        Dim Name As String = MyMsgBoxInput(
            Title:="新建常用模组清单",
            Text:="请输入新清单名称（例如：日常优化包、建筑实用包）：",
            DefaultInput:="",
            ValidateRules:=New ObjectModel.Collection(Of Validate) From {New ValidateNullOrWhiteSpace})
        If String.IsNullOrWhiteSpace(Name) Then Return
        Dim NewPack As New ModPresetPack With {.Name = Name.Trim()}
        Presets.Add(NewPack)
        CurrentPresetId = NewPack.Id
        SavePresetPacks(Presets)
        RefreshPresetCombo()
        RefreshCurrentPresetView()
        Hint($"已新建清单「{NewPack.Name}」", HintType.Green)
    End Sub

    Private Sub BtnRenamePreset_Click() Handles BtnRenamePreset.Click
        Dim Pack = CurrentPreset
        If Pack Is Nothing Then Return
        Dim Name As String = MyMsgBoxInput(
            Title:="重命名清单",
            Text:="请输入新的清单名称：",
            DefaultInput:=Pack.Name,
            ValidateRules:=New ObjectModel.Collection(Of Validate) From {New ValidateNullOrWhiteSpace})
        If String.IsNullOrWhiteSpace(Name) Then Return
        Pack.Name = Name.Trim()
        SavePresetPacks(Presets)
        RefreshPresetCombo()
    End Sub

    Private Sub BtnDeletePreset_Click() Handles BtnDeletePreset.Click
        Dim Pack = CurrentPreset
        If Pack Is Nothing Then Return
        If MyMsgBox($"确定要删除清单「{Pack.Name}」吗？（不会删除已下载到本地的游戏版本和文件）", "删除清单", "删除", "取消", IsWarn:=True) <> 1 Then Return
        Presets.Remove(Pack)
        CurrentPresetId = If(Presets.Any(), Presets(0).Id, Nothing)
        SavePresetPacks(Presets)
        RefreshPresetCombo()
        RefreshCurrentPresetView()
        Hint("清单已删除")
    End Sub

#Region "搜索添加资源"

    Private Sub TextAddKeyword_KeyDown(sender As Object, e As KeyEventArgs) Handles TextAddKeyword.KeyDown
        If e.Key = Key.Enter Then StartAddSearch()
    End Sub

    Private Sub BtnAddSearch_Click() Handles BtnAddSearch.Click
        StartAddSearch()
    End Sub

    Private Sub StartAddSearch()
        If CurrentPreset Is Nothing Then
            Hint("请先新建或选择一个清单！", HintType.Red)
            Return
        End If
        Dim Keyword As String = TextAddKeyword.Text.Trim()
        Dim ResType As ResourceTypes = CType(Val(CType(ComboAddType.SelectedItem, MyComboBoxItem).Tag), ResourceTypes)
        Dim Sources As ResourcePlatforms = CType(Val(CType(ComboAddSource.SelectedItem, MyComboBoxItem).Tag), ResourcePlatforms)
        BtnAddSearch.IsEnabled = False
        LabSearchStatus.Visibility = Visibility.Visible
        LabSearchStatus.Text = "正在搜索资源，请稍候……"
        PanAddResults.Children.Clear()

        Dim Storage As New ResourceSearcher.SearchResult
        Dim Request As New ResourceSearcher.SearchRequest(ResType, Storage, 20) With {
            .SearchText = Keyword,
            .Sources = Sources
        }
        CurrentSearchLoader = ResourceSearcher.GetLoader(Function() Request)
        RunInNewThread(
        Sub()
            Try
                CurrentSearchLoader.WaitForExit(Input:=Request)
                RunInUi(
                Sub()
                    BtnAddSearch.IsEnabled = True
                    PanAddResults.Children.Clear()
                    If CurrentSearchLoader.State = LoadState.Failed Then
                        LabSearchStatus.Text = "搜索失败：" & If(CurrentSearchLoader.Error?.Message, "未知错误")
                        Return
                    End If
                    Dim Results = Storage.Results.Take(20).ToList()
                    If Not Results.Any() Then
                        LabSearchStatus.Text = "未找到匹配结果，请尝试英文原名（如 Sodium、AppleSkin）或切换来源。"
                        Return
                    End If
                    LabSearchStatus.Text = $"找到 {Results.Count} 条结果，点击左侧列表项或右侧「+」加入清单："
                    For Each Proj In Results
                        Dim LocalProj = Proj
                        Dim PlatformName = If(LocalProj.Platform = ResourcePlatforms.CurseForge, "CurseForge", "Modrinth")
                        Dim LoaderTag = If(ResType = ResourceTypes.Mod, FormatLoaderNames(LocalProj.ModLoaders), "")
                        Dim Prefix = If(String.IsNullOrEmpty(LoaderTag), $"[{PlatformName}]", $"[{PlatformName} · {LoaderTag}]")
                        Dim Item As New MyListItem With {
                            .Title = LocalProj.TranslatedName,
                            .Info = $"{Prefix} {LocalProj.Description}".ReplaceLineEndings(" "),
                            .Height = 46,
                            .Type = MyListItem.CheckType.Clickable,
                            .Logo = GetPresetItemLogo(LocalProj.ModLoaders, ResType, GetSelectedDeployLoader())
                        }
                        Dim BtnAdd As New MyIconButton With {.ToolTip = "添加到当前清单", .Logo = "M469 469V128a43 43 0 0 1 86 0v341h341a43 43 0 0 1 0 86H555v341a43 43 0 0 1-86 0V555H128a43 43 0 0 1 0-86h341z"}
                        Dim AddAction =
                        Sub()
                            Dim Pack = CurrentPreset
                            If Pack Is Nothing Then Return
                            If Pack.Items.Any(Function(x) x.ProjectId = LocalProj.Id AndAlso x.Platform = CInt(LocalProj.Platform) AndAlso x.ResourceType = CInt(ResType)) Then
                                Hint($"「{LocalProj.TranslatedName}」已在当前清单中！", HintType.Blue)
                                Return
                            End If
                            Pack.Items.Add(New PresetResourceItem With {
                                .ProjectId = LocalProj.Id,
                                .Platform = CInt(LocalProj.Platform),
                                .Slug = If(LocalProj.Slug, ""),
                                .Title = LocalProj.TranslatedName,
                                .ResourceType = CInt(ResType),
                                .SupportedLoaders = CInt(LocalProj.ModLoaders),
                                .LogoUrl = If(LocalProj.LogoUrl, ""),
                                .Website = If(LocalProj.Website, "")
                            })
                            SavePresetPacks(Presets)
                            RefreshPresetCombo()
                            RefreshCurrentPresetView()
                            Hint($"已添加「{LocalProj.TranslatedName}」到清单！", HintType.Green)
                        End Sub
                        AddHandler BtnAdd.Click, Sub() AddAction()
                        AddHandler Item.Click, Sub() AddAction()
                        Item.Buttons = {BtnAdd}
                        PanAddResults.Children.Add(Item)
                    Next
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    BtnAddSearch.IsEnabled = True
                    LabSearchStatus.Text = "搜索异常：" & ex.Message
                End Sub)
            End Try
        End Sub, "Preset Add Search")
    End Sub

#End Region

#Region "一键检测与部署"

    Private Sub ComboDeployLoader_SelectionChanged() Handles ComboDeployLoader.SelectionChanged
        If Not IsFirstLoaded OrElse IsUpdatingUi Then Return
        RefreshCurrentPresetView()
        CheckLoaderCompatibility(ShowToast:=True)
    End Sub

    Private Sub BtnDeployStart_Click() Handles BtnDeployStart.Click
        If IsBusyDeploying Then Return
        Dim Pack = CurrentPreset
        If Pack Is Nothing Then
            Hint("请先创建或选择一个清单！", HintType.Red)
            Return
        End If
        Dim TargetMcVer As String = ComboDeployVersion.Text.Trim()
        If String.IsNullOrWhiteSpace(TargetMcVer) Then
            Hint("请填写或选择目标游戏版本！", HintType.Red)
            Return
        End If
        Dim LoaderTag As Integer = CInt(Val(CType(ComboDeployLoader.SelectedItem, MyComboBoxItem).Tag))
        Dim TargetLoader As ModLoaders = CType(LoaderTag, ModLoaders)
        Dim InstanceName As String = TextDeployName.Text.Trim()
        If String.IsNullOrWhiteSpace(InstanceName) Then
            Hint("实例名称不能为空！", HintType.Red)
            Return
        End If
        If InstanceName.IndexOfAny(IO.Path.GetInvalidFileNameChars()) >= 0 Then
            Hint("实例名称包含非法字符！", HintType.Red)
            Return
        End If
        Dim VersionFolder As String = $"{McFolderSelected}versions\{InstanceName}\"
        If FileUtils.Exists($"{VersionFolder}{InstanceName}.json") Then
            Hint($"版本「{InstanceName}」已经存在，请更换实例名称！", HintType.Red)
            Return
        End If
        '检查版本隔离提示（与 PageDownloadInstall 一致）
        If TargetLoader <> ModLoaders.None AndAlso (Settings.Get(Of Integer)("LaunchArgumentIndieV2") = 0 OrElse Settings.Get(Of Integer)("LaunchArgumentIndieV2") = 2) Then
            If MyMsgBox("你尚未开启全局版本隔离，多个版本会共用同一个 mods 文件夹。" & vbCrLf &
                        "是否仍要继续一键安装？（建议在「设置 → 启动选项 → 默认版本隔离」开启）", "版本隔离提示", "继续安装", "取消") <> 1 Then
                Return
            End If
        End If

        Dim IncludeFabricApi As Boolean = CheckAutoFabricApi.Checked
        IsBusyDeploying = True
        BtnDeployStart.IsEnabled = False
        LabDeployStatus.Text = $"正在拉取 {TargetMcVer} 原版与清单资源兼容信息……"

        RunInNewThread(
        Sub()
            Try
                '1. 确认原版版本存在
                If DlClientListLoader.State <> LoadState.Finished Then DlClientListLoader.WaitForExit()
                Dim VanillaData As JObject = CType(DlClientListLoader.Output.Value("versions"), JArray).
                    OfType(Of JObject)().
                    FirstOrDefault(Function(v) v("id")?.ToString().Equals(TargetMcVer, StringComparison.OrdinalIgnoreCase))
                If VanillaData Is Nothing Then
                    Throw New Exception($"在 Minecraft 版本列表中找不到版本「{TargetMcVer}」。")
                End If
                Dim ExactMcVer As String = VanillaData("id").ToString()
                Dim MinecraftJsonUrl As String = VanillaData("url").ToString()

                '2. 解析加载器版本
                Dim SelectedFabric As String = Nothing
                Dim SelectedFabricApi As ResourceVersion = Nothing
                Dim SelectedForge As DlForgeVersionEntry = Nothing
                Dim SelectedNeoForge As DlNeoForgeListEntry = Nothing

                Select Case TargetLoader
                    Case ModLoaders.Fabric
                        RunInUi(Sub() LabDeployStatus.Text = $"正在查询 {ExactMcVer} 的 Fabric 版本……")
                        If DlFabricListLoader.State <> LoadState.Finished Then DlFabricListLoader.WaitForExit()
                        Dim GameSupported As Boolean = CType(DlFabricListLoader.Output.Value("game"), JArray).
                            Any(Function(g) g("version")?.ToString() = ExactMcVer.Replace("∞", "infinite").Replace("Combat Test 7c", "1.16_combat-3"))
                        If Not GameSupported Then Throw New Exception($"Minecraft {ExactMcVer} 暂无可用的 Fabric 加载器！")
                        SelectedFabric = CType(DlFabricListLoader.Output.Value("loader"), JArray)(0)("version").ToString()
                        If IncludeFabricApi Then
                            If DlFabricApiLoader.State <> LoadState.Finished Then DlFabricApiLoader.WaitForExit()
                            SelectedFabricApi = DlFabricApiLoader.Output?.
                                Where(Function(f) PageDownloadInstall.IsFabricApiCompatible(f, ExactMcVer)).
                                OrderByDescending(Function(f) f.ReleaseDate).
                                FirstOrDefault()
                        End If
                    Case ModLoaders.Forge
                        RunInUi(Sub() LabDeployStatus.Text = $"正在查询 {ExactMcVer} 的 Forge 版本……")
                        Dim ForgeLoader As New LoaderTask(Of String, List(Of DlForgeVersionEntry))("PresetForge " & ExactMcVer, AddressOf DlForgeVersionMain)
                        ForgeLoader.WaitForExit(Input:=ExactMcVer)
                        Dim ForgeList = ForgeLoader.Output?.
                            Where(Function(f) f.Category <> "universal" AndAlso f.Category <> "client").
                            OrderByDescending(Function(f) f.IsRecommended).
                            ThenByDescending(Function(f) f).
                            ToList()
                        If ForgeList Is Nothing OrElse Not ForgeList.Any() Then
                            Throw New Exception($"Minecraft {ExactMcVer} 暂无可用自动安装的 Forge！")
                        End If
                        SelectedForge = ForgeList(0)
                    Case ModLoaders.NeoForge
                        RunInUi(Sub() LabDeployStatus.Text = $"正在查询 {ExactMcVer} 的 NeoForge 版本……")
                        If DlNeoForgeListLoader.State <> LoadState.Finished Then DlNeoForgeListLoader.WaitForExit()
                        Dim NeoList = DlNeoForgeListLoader.Output.Value?.
                            Where(Function(n) n.Inherit = ExactMcVer).
                            OrderBy(Function(n) n.IsBeta).
                            ThenByDescending(Function(n) n).
                            ToList()
                        If NeoList Is Nothing OrElse Not NeoList.Any() Then
                            Throw New Exception($"Minecraft {ExactMcVer} 暂无可用的 NeoForge 加载器！")
                        End If
                        SelectedNeoForge = NeoList(0)
                End Select

                '3. 校验清单内每个 Mod 和材质包
                RunInUi(Sub() LabDeployStatus.Text = $"正在校验 {Pack.Items.Count} 个 Mod 与材质包是否支持 {ExactMcVer}……")
                Dim Resolved = ResolvePresetItems(Pack, ExactMcVer, TargetLoader)
                Dim Missing = Resolved.Where(Function(r) Not r.IsResolved).ToList()
                Dim Available = Resolved.Where(Function(r) r.IsResolved).ToList()

                '如果有缺失项，报警并让用户决定是否跳过下载其余内容
                If Missing.Any() Then
                    Dim Sb As New StringBuilder()
                    Sb.AppendLine($"在清单「{Pack.Name}」中，共有 {Missing.Count} 项暂无适配 {ExactMcVer} 的版本：")
                    Sb.AppendLine()
                    For Each M In Missing.Take(12)
                        Dim KindName As String = If(CType(M.Entry.ResourceType, ResourceTypes) = ResourceTypes.ResourcePack, "材质包", "Mod")
                        Sb.AppendLine($"• [{KindName}] {M.Entry.Title}：{M.MissingReason}")
                    Next
                    If Missing.Count > 12 Then Sb.AppendLine($"……以及另外 {Missing.Count - 12} 项")
                    Sb.AppendLine()
                    Sb.AppendLine($"可正常下载的其余资源共 {Available.Count} 项。是否在不下载上述缺失项的情况下，继续下载游戏本体与其他可用资源？")
                    Dim Choice As Integer = MyMsgBox(Sb.ToString(), "部分 Mod 或材质包缺少对应版本", "跳过缺失项并继续下载", "取消下载", IsWarn:=True)
                    If Choice <> 1 Then
                        RunInUi(
                        Sub()
                            LabDeployStatus.Text = "已取消安装。"
                            IsBusyDeploying = False
                            BtnDeployStart.IsEnabled = True
                        End Sub)
                        Return
                    End If
                End If

                '3.5 自动检测缺失的必需前置依赖
                If Available.Any() AndAlso TargetLoader <> ModLoaders.None Then
                    RunInUi(Sub() LabDeployStatus.Text = "正在检测清单 Mod 所需的缺失前置依赖……")
                    Dim MissingDeps = ResolveMissingDependencies(Available, ExactMcVer, TargetLoader)
                    If IncludeFabricApi AndAlso TargetLoader = ModLoaders.Fabric Then
                        MissingDeps = MissingDeps.Where(Function(d) d.Entry.ProjectId <> "P7dR8mSH" AndAlso Not d.Entry.Slug.Equals("fabric-api", StringComparison.OrdinalIgnoreCase)).ToList()
                    End If
                    If MissingDeps.Any() Then
                        Dim DepListText = String.Join(vbCrLf, MissingDeps.Take(12).Select(Function(d) $"• {d.Entry.Title}"))
                        If MissingDeps.Count > 12 Then DepListText &= vbCrLf & $"……等共 {MissingDeps.Count} 项"
                        Dim DepChoice = MyMsgBox($"检测到清单中的 Mod 依赖以下未加入清单的前置 Mod（适配 {ExactMcVer} / {TargetLoader}）：{vbCrLf}{vbCrLf}{DepListText}{vbCrLf}{vbCrLf}是否一并自动下载这些前置？", "发现缺失前置依赖", "一并自动下载", "仅下载原清单")
                        If DepChoice = 1 Then
                            Available.AddRange(MissingDeps)
                        End If
                    End If
                End If

                '4. 构造安装加载器并加入任务列表
                Dim ModLoaderVersions As New Dictionary(Of ModLoaders, String)
                If SelectedForge IsNot Nothing Then ModLoaderVersions(ModLoaders.Forge) = SelectedForge.VersionName
                If SelectedNeoForge IsNot Nothing Then ModLoaderVersions(ModLoaders.NeoForge) = SelectedNeoForge.VersionName
                If Not String.IsNullOrEmpty(SelectedFabric) Then ModLoaderVersions(ModLoaders.Fabric) = SelectedFabric
                Dim FabricApiVer As String = If(SelectedFabricApi?.Version, If(SelectedFabricApi?.Id, SelectedFabricApi?.Display))
                Dim Components As New McComponents(ExactMcVer, ModLoaderVersions, FabricApiVersion:=FabricApiVer)
                Dim Request As New McInstallRequest With {
                    .NewInstanceName = InstanceName,
                    .InstanceFolder = VersionFolder,
                    .Components = Components,
                    .MinecraftJson = MinecraftJsonUrl,
                    .ForgeEntry = SelectedForge,
                    .NeoForgeEntry = SelectedNeoForge,
                    .FabricApi = SelectedFabricApi
                }
                Dim Loaders = McInstallLoadersGet(Request)
                If Available.Any() Then
                    Dim PresetSubLoaders As New List(Of LoaderBase)
                    PresetSubLoaders.Add(New LoaderTask(Of String, List(Of NetFile))("生成清单资源下载列表",
                    Sub(Task As LoaderTask(Of String, List(Of NetFile)))
                        Dim TargetIndie As String = New McInstance(VersionFolder).PathIndie
                        Dim ModsDir As String = TargetIndie & "mods\"
                        Dim RpDir As String = TargetIndie & "resourcepacks\"
                        DirectoryUtils.Create(ModsDir)
                        DirectoryUtils.Create(RpDir)
                        Dim Files As New List(Of NetFile)
                        For Each Item In Available
                            Dim ResType As ResourceTypes = CType(Item.Entry.ResourceType, ResourceTypes)
                            Dim DestDir As String = If(ResType = ResourceTypes.ResourcePack, RpDir, ModsDir)
                            Files.Add(Item.MatchedVersion.ToNetFile(DestDir, ResourceVersion.DownloadReason.Standalone, ExactMcVer, TargetLoader))
                        Next
                        Task.Output = Files
                    End Sub) With {.ProgressWeight = 1, .Show = False})
                    PresetSubLoaders.Add(New LoaderDownload("下载清单 Mod 与材质包", New List(Of NetFile)) With {.ProgressWeight = 18, .Block = True})
                    Dim InsertPos As Integer = Math.Max(0, Loaders.Count - 1) '放在删除 .pclignore 之前
                    Loaders.Insert(InsertPos, New LoaderCombo(Of String)("装配清单 Mod 与材质包", PresetSubLoaders) With {.ProgressWeight = 19, .Block = True})
                End If

                RunInUi(
                Sub()
                    Dim MainLoader As New LoaderCombo(Of String)($"{InstanceName}（清单装配）", Loaders) With {.OnStateChanged = AddressOf McInstallState}
                    MainLoader.Start(VersionFolder)
                    LoaderTaskbarAdd(MainLoader)
                    FrmMain.BtnExtraDownload.ShowRefresh()
                    FrmMain.BtnExtraDownload.Ribble()
                    LabDeployStatus.Text = $"已开始下载安装「{InstanceName}」，可在右上角任务管理查看进度！"
                    Hint($"已开始一键下载装配「{InstanceName}」（含 {Available.Count} 项清单资源）", HintType.Green)
                    IsBusyDeploying = False
                    BtnDeployStart.IsEnabled = True
                End Sub)
            Catch ex As Exception
                Logger.Error(ex, "常用模组清单一键部署失败")
                RunInUi(
                Sub()
                    LabDeployStatus.Text = "检测/装配失败：" & ex.Message
                    Hint("一键部署失败：" & ex.Message, HintType.Red)
                    IsBusyDeploying = False
                    BtnDeployStart.IsEnabled = True
                End Sub)
            End Try
        End Sub, "Preset Deploy Start")
    End Sub

    Private Sub BtnExportShareCode_Click() Handles BtnExportShareCode.Click
        If CurrentPreset Is Nothing Then Return
        Dim Code = ModPreset.ExportPresetShareCode(CurrentPreset)
        ClipboardSet(Code)
        Hint($"已复制清单「{CurrentPreset.Name}」的分享码到剪贴板！", HintType.Green)
    End Sub

    Private Sub BtnExportJsonFile_Click() Handles BtnExportJsonFile.Click
        If CurrentPreset Is Nothing Then Return
        Dim OutPath = Dialogs.SaveFile("导出常用模组清单", $"{CurrentPreset.Name}.json", filter:={("json", "常用清单文件 (*.json)")})
        If String.IsNullOrEmpty(OutPath) Then Return
        Try
            IO.File.WriteAllText(OutPath, ModPreset.ExportPresetJson(CurrentPreset), New System.Text.UTF8Encoding(False))
            Hint($"已导出清单到 {IO.Path.GetFileName(OutPath)}", HintType.Green)
        Catch ex As Exception
            Hint($"导出失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnImportPreset_Click() Handles BtnImportPreset.Click
        Dim Choice = MyMsgBox("请选择导入常用模组清单的方式：", "导入常用清单", "粘贴分享码 / JSON 文本", "从 .json 文件导入", "取消")
        If Choice = 1 Then
            Dim Raw = MyMsgBoxInput("导入常用模组清单", "请粘贴 LML-PRESET-V1:... 分享码或 JSON 文本：", HintText:="分享码 / JSON")
            If String.IsNullOrWhiteSpace(Raw) Then Return
            ApplyImportedPreset(Raw)
        ElseIf Choice = 2 Then
            Dim Files = Dialogs.SelectFile("选择常用模组清单 JSON 文件", False, filter:="常用清单文件 (*.json)|*.json")
            Dim FilePath = If(Files IsNot Nothing, Files.FirstOrDefault(), Nothing)
            If String.IsNullOrEmpty(FilePath) Then Return
            Try
                ApplyImportedPreset(IO.File.ReadAllText(FilePath, System.Text.Encoding.UTF8))
            Catch ex As Exception
                Hint($"读取文件失败：{ex.Message}", HintType.Red)
            End Try
        End If
    End Sub

    Private Sub ApplyImportedPreset(Raw As String)
        Dim Res = ModPreset.ImportPresetFromText(Raw)
        If Res.Pack Is Nothing Then
            Hint(Res.ErrorMsg, HintType.Red)
            Return
        End If
        Presets.Add(Res.Pack)
        CurrentPresetId = Res.Pack.Id
        SavePresetPacks(Presets)
        RefreshPresetCombo()
        RefreshCurrentPresetView()
        BackfillPresetLoadersAsync()
        Hint($"已导入清单「{Res.Pack.Name}」（含 {Res.Pack.Items.Count} 项资源）！", HintType.Green)
    End Sub

#End Region

End Class
