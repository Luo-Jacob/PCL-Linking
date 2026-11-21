Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Public Class PageToolsResourceMerge

    Private Class PackSourceItem
        Public Property FullPath As String
        Public Property DisplayName As String
        Public Property IsDirectory As Boolean
    End Class

    Private ReadOnly Packs As New List(Of PackSourceItem)
    Private ReadOnly ConflictOverrides As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) 'DestRel -> Pack.FullPath
    Private IsBusy As Boolean = False

    Private Sub PageToolsResourceMerge_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        RenderPackList()
    End Sub

    Private Function ParsePackFormat() As Integer
        Dim Selected = TryCast(ComboPackFormat.SelectedItem, MyComboBoxItem)
        If Selected IsNot Nothing AndAlso Selected.Tag IsNot Nothing Then
            Dim Parsed As Integer
            If Integer.TryParse(Selected.Tag.ToString(), Parsed) Then Return Parsed
        End If
        Dim Digits = New String(ComboPackFormat.Text.TakeWhile(Function(c) Char.IsDigit(c)).ToArray())
        Dim Num As Integer
        If Integer.TryParse(Digits, Num) AndAlso Num > 0 Then Return Num
        Return 34
    End Function

    Private Sub RenderPackList()
        PanPackList.Children.Clear()
        HintNoPacks.Visibility = If(Packs.Any(), Visibility.Collapsed, Visibility.Visible)
        For i As Integer = 0 To Packs.Count - 1
            Dim LocalIndex As Integer = i
            Dim Item = Packs(LocalIndex)
            Dim PriorityTag = If(LocalIndex = 0, "最高优先级 · ", If(LocalIndex = Packs.Count - 1 AndAlso Packs.Count > 1, "最低优先级 · ", ""))
            Dim Row As New MyListItem With {
                .Title = $"{LocalIndex + 1}. {Item.DisplayName}",
                .Info = $"{PriorityTag}{If(Item.IsDirectory, "文件夹", "ZIP")} · {Item.FullPath}",
                .Height = 44,
                .Type = MyListItem.CheckType.Clickable,
                .Logo = If(Item.IsDirectory, "pack://application:,,,/images/Blocks/GrassPath.png", "pack://application:,,,/images/Blocks/Grass.png")
            }

            Dim BtnUp As New MyIconButton With {.ToolTip = "上移", .Logo = "M512 341.333l-256 256h512l-256-256z"}
            AddHandler BtnUp.Click,
            Sub()
                If LocalIndex <= 0 Then Return
                Dim Swap = Packs(LocalIndex - 1)
                Packs(LocalIndex - 1) = Packs(LocalIndex)
                Packs(LocalIndex) = Swap
                RenderPackList()
            End Sub

            Dim BtnDown As New MyIconButton With {.ToolTip = "下移", .Logo = "M512 682.667l256-256H256l256 256z"}
            AddHandler BtnDown.Click,
            Sub()
                If LocalIndex >= Packs.Count - 1 Then Return
                Dim Swap = Packs(LocalIndex + 1)
                Packs(LocalIndex + 1) = Packs(LocalIndex)
                Packs(LocalIndex) = Swap
                RenderPackList()
            End Sub

            Dim BtnDel As New MyIconButton With {.ToolTip = "移除", .Logo = Logo.IconButtonDelete}
            AddHandler BtnDel.Click,
            Sub()
                Packs.RemoveAt(LocalIndex)
                RenderPackList()
            End Sub

            Row.Buttons = {BtnUp, BtnDown, BtnDel}
            PanPackList.Children.Add(Row)
        Next
        CardPackList?.TriggerForceResize()
    End Sub

    Private Shared Function GetDefaultResourcePacksDir() As String
        If McInstanceSelected IsNot Nothing Then
            Dim InstPacks = Path.Combine(McInstanceSelected.PathIndie, "resourcepacks")
            If Directory.Exists(InstPacks) Then Return InstPacks
            If Directory.Exists(McInstanceSelected.PathIndie) Then Return McInstanceSelected.PathIndie
        End If
        If Not String.IsNullOrEmpty(McFolderSelected) Then
            Dim RootPacks = Path.Combine(McFolderSelected, "resourcepacks")
            If Directory.Exists(RootPacks) Then Return RootPacks
            If Directory.Exists(McFolderSelected) Then Return McFolderSelected
        End If
        Return Nothing
    End Function

    Private Sub AddPaths(PathsInput As IEnumerable(Of String))
        Dim Added As Integer = 0
        For Each RawPath In PathsInput
            If String.IsNullOrWhiteSpace(RawPath) Then Continue For
            If Directory.Exists(RawPath) Then
                Dim TargetDir = ResolvePackFolderRoot(RawPath)
                If TargetDir Is Nothing Then
                    Hint($"文件夹「{Path.GetFileName(RawPath.TrimEnd("\"c, "/"c))}」缺少 assets 或 pack.mcmeta，已跳过！", HintType.Red)
                    Continue For
                End If
                If Not Packs.Any(Function(p) PathUtils.IsSame(p.FullPath, TargetDir)) Then
                    Packs.Add(New PackSourceItem With {.FullPath = TargetDir, .DisplayName = New DirectoryInfo(TargetDir).Name, .IsDirectory = True})
                    Added += 1
                End If
            ElseIf File.Exists(RawPath) AndAlso RawPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
                If Not Packs.Any(Function(p) PathUtils.IsSame(p.FullPath, RawPath)) Then
                    Packs.Add(New PackSourceItem With {.FullPath = RawPath, .DisplayName = Path.GetFileName(RawPath), .IsDirectory = False})
                    Added += 1
                End If
            End If
        Next
        If Added > 0 Then
            RenderPackList()
            Hint($"已添加 {Added} 个材质包", HintType.Green)
        End If
    End Sub

    Private Shared Function ResolvePackFolderRoot(DirPath As String) As String
        If Directory.Exists(Path.Combine(DirPath, "assets")) OrElse File.Exists(Path.Combine(DirPath, "pack.mcmeta")) Then Return DirPath
        Dim SubDirs = Directory.GetDirectories(DirPath)
        If SubDirs.Length = 1 Then
            Dim Inner = SubDirs(0)
            If Directory.Exists(Path.Combine(Inner, "assets")) OrElse File.Exists(Path.Combine(Inner, "pack.mcmeta")) Then Return Inner
        End If
        Return Nothing
    End Function

    Private Sub PanMain_DragOver(sender As Object, e As DragEventArgs) Handles PanMain.DragOver
        e.Effects = If(e.Data.GetDataPresent(DataFormats.FileDrop), DragDropEffects.Copy, DragDropEffects.None)
        e.Handled = True
    End Sub

    Private Sub PanMain_Drop(sender As Object, e As DragEventArgs) Handles PanMain.Drop
        If Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        Dim Dropped = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If Dropped IsNot Nothing AndAlso Dropped.Any() Then AddPaths(Dropped)
    End Sub

    Private Sub BtnAddZipPack_Click() Handles BtnAddZipPack.Click
        Dim Files = Dialogs.SelectFile("选择要合并的材质包 (.zip)", True, defaultDirectory:=GetDefaultResourcePacksDir(), filter:="材质包压缩文件 (*.zip)|*.zip")
        If Files Is Nothing OrElse Not Files.Any() Then Return
        AddPaths(Files)
    End Sub

    Private Sub BtnAddFolderPack_Click() Handles BtnAddFolderPack.Click
        Dim Folders = Dialogs.SelectFolder("选择材质包文件夹", True, defaultDirectory:=GetDefaultResourcePacksDir())
        If Folders Is Nothing OrElse Not Folders.Any() Then Return
        AddPaths(Folders)
    End Sub

    Private Sub BtnClearPacks_Click() Handles BtnClearPacks.Click
        Packs.Clear()
        ConflictOverrides.Clear()
        LabConflictPreview.Text = ""
        LabMergeStatus.Text = ""
        PanConflictResolver.Children.Clear()
        PanConflictToolbar.Visibility = Visibility.Collapsed
        RenderPackList()
    End Sub

    Private Sub BtnResetOverrides_Click() Handles BtnResetOverrides.Click
        ConflictOverrides.Clear()
        BtnAnalyzeConflicts_Click()
    End Sub

    Private Shared Function DetectZipWrapperPrefix(Entries As IEnumerable(Of ZipArchiveEntry)) As String
        Dim RelPaths As New List(Of String)
        For Each Entry In Entries
            If String.IsNullOrEmpty(Entry.Name) Then Continue For
            Dim Rel = Entry.FullName.Replace("\"c, "/"c).TrimStart("/"c)
            If Not String.IsNullOrEmpty(Rel) AndAlso Not Rel.Contains("..") Then RelPaths.Add(Rel)
        Next
        '如果根目录已有 assets/ 或 pack.mcmeta，则无需剥离外层目录
        If RelPaths.Any(Function(p) p.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase) OrElse p.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) Then
            Return ""
        End If
        Dim TopFolders = RelPaths.Where(Function(p) p.Contains("/"c)).Select(Function(p) p.Substring(0, p.IndexOf("/"c))).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        If TopFolders.Count = 1 Then
            Dim Prefix = TopFolders(0) & "/"
            If RelPaths.Any(Function(p) p.Equals(Prefix & "pack.mcmeta", StringComparison.OrdinalIgnoreCase) OrElse p.StartsWith(Prefix & "assets/", StringComparison.OrdinalIgnoreCase)) Then
                Return Prefix
            End If
        End If
        Return ""
    End Function

    Private Shared Function EnumeratePackEntries(Pack As PackSourceItem) As List(Of (DestRel As String, SrcKey As String))
        Dim PathsList As New List(Of (DestRel As String, SrcKey As String))
        If Pack.IsDirectory Then
            Dim RootFull = Path.GetFullPath(Pack.FullPath).TrimEnd("\"c)
            For Each F In Directory.EnumerateFiles(RootFull, "*", SearchOption.AllDirectories)
                Dim Rel = Path.GetFullPath(F).Substring(RootFull.Length).TrimStart("\"c, "/"c).Replace("\"c, "/"c)
                If String.IsNullOrEmpty(Rel) OrElse Rel.Contains("..") Then Continue For
                PathsList.Add((Rel, Rel))
            Next
        Else
            Using Arc = ZipFile.OpenRead(Pack.FullPath)
                Dim Wrapper = DetectZipWrapperPrefix(Arc.Entries)
                For Each Entry In Arc.Entries
                    If String.IsNullOrEmpty(Entry.Name) Then Continue For
                    Dim RawKey = Entry.FullName.Replace("\"c, "/"c).TrimStart("/"c)
                    If String.IsNullOrEmpty(RawKey) OrElse RawKey.Contains("..") Then Continue For
                    Dim DestRel = If(Not String.IsNullOrEmpty(Wrapper) AndAlso RawKey.StartsWith(Wrapper, StringComparison.OrdinalIgnoreCase),
                                     RawKey.Substring(Wrapper.Length),
                                     RawKey)
                    If String.IsNullOrEmpty(DestRel) Then Continue For
                    PathsList.Add((DestRel, RawKey))
                Next
            End Using
        End If
        Return PathsList
    End Function

    Private Shared Function IsMergeableLangJson(RelPath As String) As Boolean
        Return RelPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) AndAlso
               RelPath.IndexOf("/lang/", StringComparison.OrdinalIgnoreCase) > 0 AndAlso
               RelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function IsMergeableFontJson(RelPath As String) As Boolean
        Return RelPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) AndAlso
               RelPath.IndexOf("/font/", StringComparison.OrdinalIgnoreCase) > 0 AndAlso
               RelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function ReadPackEntryText(Pack As PackSourceItem, SrcKey As String) As String
        If Pack.IsDirectory Then
            Dim FullSrc = Path.Combine(Path.GetFullPath(Pack.FullPath).TrimEnd("\"c), SrcKey.Replace("/"c, Path.DirectorySeparatorChar))
            If File.Exists(FullSrc) Then Return File.ReadAllText(FullSrc, New UTF8Encoding(False))
        Else
            Using Arc = ZipFile.OpenRead(Pack.FullPath)
                Dim Entry = Arc.Entries.FirstOrDefault(Function(en) Not String.IsNullOrEmpty(en.Name) AndAlso en.FullName.Replace("\"c, "/"c).TrimStart("/"c).Equals(SrcKey, StringComparison.OrdinalIgnoreCase))
                If Entry IsNot Nothing Then
                    Using Sr As New StreamReader(Entry.Open(), Encoding.UTF8, True)
                        Return Sr.ReadToEnd()
                    End Using
                End If
            End Using
        End If
        Return Nothing
    End Function

    Private Shared Function ReadPackEntryBytes(Pack As PackSourceItem, SrcKey As String) As Byte()
        If Pack.IsDirectory Then
            Dim FullSrc = Path.Combine(Path.GetFullPath(Pack.FullPath).TrimEnd("\"c), SrcKey.Replace("/"c, Path.DirectorySeparatorChar))
            If File.Exists(FullSrc) Then Return File.ReadAllBytes(FullSrc)
        Else
            Using Arc = ZipFile.OpenRead(Pack.FullPath)
                Dim Entry = Arc.Entries.FirstOrDefault(Function(en) Not String.IsNullOrEmpty(en.Name) AndAlso en.FullName.Replace("\"c, "/"c).TrimStart("/"c).Equals(SrcKey, StringComparison.OrdinalIgnoreCase))
                If Entry IsNot Nothing Then
                    Using Ms As New MemoryStream(), S = Entry.Open()
                        S.CopyTo(Ms)
                        Return Ms.ToArray()
                    End Using
                End If
            End Using
        End If
        Return Nothing
    End Function

    Private Shared Function TryDecodeThumbnail(RawBytes As Byte()) As BitmapImage
        If RawBytes Is Nothing OrElse RawBytes.Length = 0 OrElse RawBytes.Length > 4 * 1024 * 1024 Then Return Nothing
        Try
            Using Ms As New MemoryStream(RawBytes)
                Dim Bmp As New BitmapImage()
                Bmp.BeginInit()
                Bmp.CacheOption = BitmapCacheOption.OnLoad
                Bmp.DecodePixelWidth = 64
                Bmp.StreamSource = Ms
                Bmp.EndInit()
                Bmp.Freeze()
                Return Bmp
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    Private Class ConflictItemGroup
        Public Property DestRel As String
        Public Property IsJsonAutoMerge As Boolean
        Public Property Candidates As New List(Of (Pack As PackSourceItem, SrcKey As String, Thumbnail As BitmapImage))
    End Class

    Private Sub RenderInteractiveConflicts(Groups As List(Of ConflictItemGroup))
        PanConflictResolver.Children.Clear()
        Dim Interactive = Groups.Where(Function(g) Not g.IsJsonAutoMerge).ToList()
        If Not Interactive.Any() Then
            PanConflictToolbar.Visibility = Visibility.Collapsed
            Return
        End If
        PanConflictToolbar.Visibility = Visibility.Visible
        Dim OverrideCount = Interactive.Where(Function(g) ConflictOverrides.ContainsKey(g.DestRel)).Count()
        LabOverrideCount.Text = $"文件冲突自主选择（共 {Interactive.Count} 处非 JSON 冲突，已自定义 {OverrideCount} 处）："

        Dim DisplayLimit As Integer = 40
        For Each Grp In Interactive.Take(DisplayLimit)
            Dim LocalGrp = Grp
            Dim Box As New Border With {
                .Margin = New Thickness(0, 4, 0, 6),
                .Padding = New Thickness(10, 8, 10, 8),
                .CornerRadius = New CornerRadius(6),
                .BorderThickness = New Thickness(1)
            }
            Box.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5")
            Box.SetResourceReference(Border.BackgroundProperty, "ColorBrushSurface")

            Dim Inner As New StackPanel
            Dim PathLab As New TextBlock With {
                .Text = LocalGrp.DestRel,
                .FontSize = 12,
                .FontWeight = FontWeights.SemiBold,
                .TextWrapping = TextWrapping.Wrap,
                .Margin = New Thickness(0, 0, 0, 6)
            }
            PathLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
            Inner.Children.Add(PathLab)

            Dim ChosenPath As String = Nothing
            If ConflictOverrides.ContainsKey(LocalGrp.DestRel) AndAlso
               LocalGrp.Candidates.Any(Function(c) PathUtils.IsSame(c.Pack.FullPath, ConflictOverrides(LocalGrp.DestRel))) Then
                ChosenPath = ConflictOverrides(LocalGrp.DestRel)
            Else
                ChosenPath = LocalGrp.Candidates(0).Pack.FullPath
            End If

            Dim ChoicesWrap As New WrapPanel
            Dim RadioGroup As String = "Conflict_" & Guid.NewGuid().ToString("N")
            For i As Integer = 0 To LocalGrp.Candidates.Count - 1
                Dim Cand = LocalGrp.Candidates(i)
                Dim IsSelected As Boolean = PathUtils.IsSame(Cand.Pack.FullPath, ChosenPath)
                Dim CandCard As New Border With {
                    .Margin = New Thickness(0, 0, 8, 4),
                    .Padding = New Thickness(6, 4, 8, 4),
                    .CornerRadius = New CornerRadius(5),
                    .BorderThickness = New Thickness(1)
                }
                CandCard.SetResourceReference(Border.BorderBrushProperty, If(IsSelected, "ColorBrush3", "ColorBrushGray5"))
                Dim CandRow As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
                If Cand.Thumbnail IsNot Nothing Then
                    Dim ImgFrame As New Border With {
                        .Width = 28, .Height = 28, .Margin = New Thickness(0, 0, 6, 0),
                        .Background = New SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                        .CornerRadius = New CornerRadius(3)
                    }
                    Dim Img As New Image With {.Source = Cand.Thumbnail, .Stretch = Stretch.Uniform}
                    RenderOptions.SetBitmapScalingMode(Img, BitmapScalingMode.NearestNeighbor)
                    ImgFrame.Child = Img
                    CandRow.Children.Add(ImgFrame)
                End If
                Dim Rb As New RadioButton With {
                    .GroupName = RadioGroup,
                    .IsChecked = IsSelected,
                    .Content = Cand.Pack.DisplayName & If(i = 0, " (默认)", ""),
                    .VerticalAlignment = VerticalAlignment.Center,
                    .FontSize = 11.5
                }
                Rb.SetResourceReference(RadioButton.ForegroundProperty, "ColorBrush1")
                Dim RelCopy = LocalGrp.DestRel
                Dim PackPathCopy = Cand.Pack.FullPath
                Dim IsDefaultFirst = (i = 0)
                AddHandler Rb.Checked,
                Sub()
                    If IsDefaultFirst Then
                        ConflictOverrides.Remove(RelCopy)
                    Else
                        ConflictOverrides(RelCopy) = PackPathCopy
                    End If
                    Dim CountNow = Interactive.Where(Function(g) ConflictOverrides.ContainsKey(g.DestRel)).Count()
                    LabOverrideCount.Text = $"文件冲突自主选择（共 {Interactive.Count} 处非 JSON 冲突，已自定义 {CountNow} 处）："
                End Sub
                CandRow.Children.Add(Rb)
                CandCard.Child = CandRow
                ChoicesWrap.Children.Add(CandCard)
            Next
            Inner.Children.Add(ChoicesWrap)
            Box.Child = Inner
            PanConflictResolver.Children.Add(Box)
        Next
        If Interactive.Count > DisplayLimit Then
            Dim MoreLab As New TextBlock With {
                .Text = $"……仅展示前 {DisplayLimit} 处冲突用于交互预览，其余 {Interactive.Count - DisplayLimit} 处将按列表优先级处理。",
                .FontSize = 11.5,
                .Margin = New Thickness(0, 4, 0, 0)
            }
            MoreLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
            PanConflictResolver.Children.Add(MoreLab)
        End If
    End Sub

    Private Sub BtnAnalyzeConflicts_Click() Handles BtnAnalyzeConflicts.Click
        If IsBusy Then
            Hint("请等待当前分析或合并任务完成。", Log:=False)
            Return
        End If
        If Packs.Count < 2 Then
            Hint("至少需要添加 2 个材质包才能分析冲突。", Log:=False)
            Return
        End If
        IsBusy = True
        Dim Snapshot = Packs.ToList()
        LabMergeStatus.Text = "正在统计文件覆盖关系与加载预览..."
        RunInNewThread(
        Sub()
            Try
                Dim AllByRel As New Dictionary(Of String, List(Of (Pack As PackSourceItem, SrcKey As String)))(StringComparer.OrdinalIgnoreCase)
                Dim HasShaders As Boolean = False
                For Each Pack In Snapshot
                    For Each EntryPair In EnumeratePackEntries(Pack)
                        Dim Rel = EntryPair.DestRel
                        If Rel.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase) OrElse Rel.Equals("pack.png", StringComparison.OrdinalIgnoreCase) Then Continue For
                        If Rel.IndexOf("/shaders/", StringComparison.OrdinalIgnoreCase) >= 0 Then HasShaders = True
                        If Not AllByRel.ContainsKey(Rel) Then AllByRel(Rel) = New List(Of (PackSourceItem, String))
                        AllByRel(Rel).Add((Pack, EntryPair.SrcKey))
                    Next
                Next

                Dim Conflicts As New List(Of String)
                Dim Groups As New List(Of ConflictItemGroup)
                Dim PreviewPngLoaded As Integer = 0
                For Each Kv In AllByRel
                    If Kv.Value.Count < 2 Then Continue For
                    Dim IsJson = IsMergeableLangJson(Kv.Key) OrElse IsMergeableFontJson(Kv.Key)
                    Dim Grp As New ConflictItemGroup With {.DestRel = Kv.Key, .IsJsonAutoMerge = IsJson}
                    If IsJson Then
                        Conflicts.Add($"{Kv.Key}（将智能合并两包内容）")
                    Else
                        Conflicts.Add($"{Kv.Key}（默认保留「{Kv.Value(0).Pack.DisplayName}」，可下方手动改选）")
                        Dim LoadPng = (Not IsJson AndAlso Kv.Key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) AndAlso PreviewPngLoaded < 40)
                        If LoadPng Then PreviewPngLoaded += 1
                        For Each Cand In Kv.Value
                            Dim Thumb As BitmapImage = If(LoadPng, TryDecodeThumbnail(ReadPackEntryBytes(Cand.Pack, Cand.SrcKey)), Nothing)
                            Grp.Candidates.Add((Cand.Pack, Cand.SrcKey, Thumb))
                        Next
                    End If
                    Groups.Add(Grp)
                Next

                Dim SampleText = If(Conflicts.Any(),
                                    String.Join(vbCrLf, Conflicts.Take(6)) & If(Conflicts.Count > 6, vbCrLf & $"…… 共检测到 {Conflicts.Count} 处冲突，可在下方逐项切换保留来源", ""),
                                    "无路径冲突，所有资源均可完整合并。")
                If HasShaders Then
                    SampleText &= vbCrLf & "提示：包含核心着色器 (shaders/)，仅支持匹配的 MC 原版渲染管线，Sodium/光影模组下可能不会生效。"
                End If
                RunInUi(
                Sub()
                    LabMergeStatus.Text = $"唯一资源 {AllByRel.Count} 个，冲突 {Conflicts.Count} 处"
                    LabConflictPreview.Text = SampleText
                    RenderInteractiveConflicts(Groups)
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabMergeStatus.Text = $"分析失败：{ex.Message}")
            Finally
                RunInUi(Sub() IsBusy = False)
            End Try
        End Sub, "Analyze ResourcePack Conflicts")
    End Sub

    Private Sub BtnMergeExport_Click() Handles BtnMergeExport.Click
        If IsBusy Then Return
        If Packs.Count < 1 Then
            Hint("请至少添加 1 个材质包！", HintType.Red)
            Return
        End If

        Dim OutPath = Dialogs.SaveFile("导出合并后的材质包", $"MergedPack-{DateTime.Now:yyyyMMdd}.zip", defaultDirectory:=GetDefaultResourcePacksDir(), filter:={("zip", "Minecraft 材质包 (*.zip)")})
        If String.IsNullOrEmpty(OutPath) Then Return

        Dim Snapshot = Packs.ToList()
        Dim OverridesSnap = ConflictOverrides.ToDictionary(Function(k) k.Key, Function(v) v.Value, StringComparer.OrdinalIgnoreCase)
        Dim PackDesc = If(String.IsNullOrWhiteSpace(TextMergeDesc.Text), "Merged by PCL Linking", TextMergeDesc.Text.Trim())
        Dim PackFmt = ParsePackFormat()
        Dim IncludePng = ChkIncludePackPng.Checked

        IsBusy = True
        BtnMergeExport.IsEnabled = False
        LabMergeStatus.Text = "正在合并打包材质包..."

        RunInNewThread(
        Sub()
            Dim TempZip As String = OutPath & ".lmltmp"
            Try
                '按优先级从高到低收集（先出现的高优先级胜出），记录来源包索引与真实路径
                Dim WinnerMap As New Dictionary(Of String, (PackIndex As Integer, SrcKey As String))(StringComparer.OrdinalIgnoreCase)
                Dim AllByDest As New Dictionary(Of String, List(Of (PackIndex As Integer, SrcKey As String)))(StringComparer.OrdinalIgnoreCase)
                Dim PackIconWinner As (PackIndex As Integer, SrcKey As String)? = Nothing

                For i As Integer = 0 To Snapshot.Count - 1
                    For Each EntryPair In EnumeratePackEntries(Snapshot(i))
                        Dim Rel = EntryPair.DestRel
                        If Rel.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase) Then Continue For
                        If Rel.Equals("pack.png", StringComparison.OrdinalIgnoreCase) Then
                            If IncludePng AndAlso Not PackIconWinner.HasValue Then PackIconWinner = (i, EntryPair.SrcKey)
                            Continue For
                        End If
                        If Not AllByDest.ContainsKey(Rel) Then AllByDest(Rel) = New List(Of (Integer, String))
                        AllByDest(Rel).Add((i, EntryPair.SrcKey))
                        If Not WinnerMap.ContainsKey(Rel) Then
                            WinnerMap(Rel) = (i, EntryPair.SrcKey)
                        End If
                    Next
                Next

                '应用用户在冲突分析器中自定义挑选的胜出包
                For Each Kv In OverridesSnap
                    Dim Candidates As List(Of (PackIndex As Integer, SrcKey As String)) = Nothing
                    If AllByDest.TryGetValue(Kv.Key, Candidates) Then
                        Dim CustomWin = Candidates.FirstOrDefault(Function(c) PathUtils.IsSame(Snapshot(c.PackIndex).FullPath, Kv.Value))
                        If CustomWin.SrcKey IsNot Nothing Then
                            WinnerMap(Kv.Key) = CustomWin
                        End If
                    End If
                Next

                '识别多包共同修改的 lang / font JSON，进行键值/数组智能合并，其余按最高优先级覆盖
                Dim MergedJsonContents As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                For Each Kv In AllByDest
                    If Kv.Value.Count < 2 Then Continue For
                    Try
                        If IsMergeableLangJson(Kv.Key) Then
                            Dim MergedObj As New JObject()
                            For idx As Integer = Kv.Value.Count - 1 To 0 Step -1 '低优先级先写，高优先级覆盖
                                Dim RawJson = ReadPackEntryText(Snapshot(Kv.Value(idx).PackIndex), Kv.Value(idx).SrcKey)
                                If String.IsNullOrWhiteSpace(RawJson) Then Continue For
                                Dim Obj = JObject.Parse(RawJson)
                                MergedObj.Merge(Obj, New JsonMergeSettings With {.MergeArrayHandling = MergeArrayHandling.Replace})
                            Next
                            MergedJsonContents(Kv.Key) = MergedObj.ToString(Formatting.Indented)
                        ElseIf IsMergeableFontJson(Kv.Key) Then
                            Dim Providers As New JArray()
                            For Each RefItem In Kv.Value
                                Dim RawJson = ReadPackEntryText(Snapshot(RefItem.PackIndex), RefItem.SrcKey)
                                If String.IsNullOrWhiteSpace(RawJson) Then Continue For
                                Dim Obj = JObject.Parse(RawJson)
                                Dim Arr = TryCast(Obj("providers"), JArray)
                                If Arr IsNot Nothing Then
                                    For Each El In Arr
                                        Providers.Add(El)
                                    Next
                                End If
                            Next
                            If Providers.Count > 0 Then
                                Dim FontObj As New JObject From {{"providers", Providers}}
                                MergedJsonContents(Kv.Key) = FontObj.ToString(Formatting.Indented)
                            End If
                        End If
                    Catch
                        '解析非标准 JSON 时回退到普通覆盖
                    End Try
                Next

                '按 PackIndex 分组只打开每个来源 Zip 一次，按 SrcKey 写入目标归档
                Dim ByPack As New Dictionary(Of Integer, List(Of (DestRel As String, SrcKey As String)))
                For Each Kv In WinnerMap
                    If MergedJsonContents.ContainsKey(Kv.Key) Then Continue For
                    If Not ByPack.ContainsKey(Kv.Value.PackIndex) Then ByPack(Kv.Value.PackIndex) = New List(Of (String, String))
                    ByPack(Kv.Value.PackIndex).Add((Kv.Key, Kv.Value.SrcKey))
                Next
                If PackIconWinner.HasValue Then
                    Dim Pi = PackIconWinner.Value.PackIndex
                    If Not ByPack.ContainsKey(Pi) Then ByPack(Pi) = New List(Of (String, String))
                    ByPack(Pi).Add(("pack.png", PackIconWinner.Value.SrcKey))
                End If

                If File.Exists(TempZip) Then File.Delete(TempZip)
                Dim WrittenCount As Integer = 0
                Using OutArchive = ZipFile.Open(TempZip, ZipArchiveMode.Create)
                    '1. 写入兼容多版本 (1.20 - 1.21 - 26.x) 的 pack.mcmeta
                    Dim MinCompat As Integer = Math.Min(15, PackFmt)
                    Dim MaxCompat As Integer = Math.Max(1000, PackFmt)
                    Dim MetaObj As New JObject From {
                        {"pack", New JObject From {
                            {"pack_format", PackFmt},
                            {"supported_formats", New JObject From {
                                {"min_inclusive", MinCompat},
                                {"max_inclusive", MaxCompat}
                            }},
                            {"min_format", MinCompat},
                            {"max_format", MaxCompat},
                            {"description", PackDesc}
                        }}
                    }
                    Dim MetaEntry = OutArchive.CreateEntry("pack.mcmeta", CompressionLevel.Optimal)
                    Using Sw As New StreamWriter(MetaEntry.Open(), New UTF8Encoding(False))
                        Sw.Write(MetaObj.ToString(Formatting.Indented))
                    End Using

                    '2. 写入智能合并的 JSON 文件
                    For Each Mkv In MergedJsonContents
                        Dim JsonEntry = OutArchive.CreateEntry(Mkv.Key, CompressionLevel.Optimal)
                        Using Sw As New StreamWriter(JsonEntry.Open(), New UTF8Encoding(False))
                            Sw.Write(Mkv.Value)
                        End Using
                        WrittenCount += 1
                    Next

                    '3. 逐个源材质包批量流式写入文件
                    For Each Pair In ByPack
                        Dim SrcPack = Snapshot(Pair.Key)
                        If SrcPack.IsDirectory Then
                            Dim RootFull = Path.GetFullPath(SrcPack.FullPath).TrimEnd("\"c)
                            For Each ItemPair In Pair.Value
                                Dim FullSrc = Path.Combine(RootFull, ItemPair.SrcKey.Replace("/"c, Path.DirectorySeparatorChar))
                                If File.Exists(FullSrc) Then
                                    OutArchive.CreateEntryFromFile(FullSrc, ItemPair.DestRel, CompressionLevel.Optimal)
                                    WrittenCount += 1
                                End If
                            Next
                        Else
                            Using SrcArchive = ZipFile.OpenRead(SrcPack.FullPath)
                                Dim Lookup = SrcArchive.Entries.Where(Function(en) Not String.IsNullOrEmpty(en.Name)).
                                    GroupBy(Function(en) en.FullName.Replace("\"c, "/"c).TrimStart("/"c), StringComparer.OrdinalIgnoreCase).
                                    ToDictionary(Function(g) g.Key, Function(g) g.First(), StringComparer.OrdinalIgnoreCase)
                                For Each ItemPair In Pair.Value
                                    Dim SrcEntry As ZipArchiveEntry = Nothing
                                    If Lookup.TryGetValue(ItemPair.SrcKey, SrcEntry) Then
                                        Dim DestEntry = OutArchive.CreateEntry(ItemPair.DestRel, CompressionLevel.Optimal)
                                        Using InS = SrcEntry.Open(), OutS = DestEntry.Open()
                                            InS.CopyTo(OutS)
                                        End Using
                                        WrittenCount += 1
                                    End If
                                Next
                            End Using
                        End If
                    Next
                End Using

                If File.Exists(OutPath) Then File.Delete(OutPath)
                File.Move(TempZip, OutPath)
                RunInUi(
                Sub()
                    LabMergeStatus.Text = $"合并完成，共写入 {WrittenCount} 个文件"
                    Hint($"材质包合并成功：{Path.GetFileName(OutPath)}", HintType.Green)
                End Sub)
            Catch ex As Exception
                Try
                    If File.Exists(TempZip) Then File.Delete(TempZip)
                Catch
                End Try
                Logger.Warn(ex, "合并材质包失败")
                RunInUi(
                Sub()
                    LabMergeStatus.Text = $"合并失败：{ex.Message}"
                    Hint($"合并材质包失败：{ex.Message}", HintType.Red)
                End Sub)
            Finally
                RunInUi(
                Sub()
                    IsBusy = False
                    BtnMergeExport.IsEnabled = True
                End Sub)
            End Try
        End Sub, "Merge ResourcePacks")
    End Sub

End Class
