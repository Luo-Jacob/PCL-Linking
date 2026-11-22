Imports System.IO
Imports System.IO.Compression
Imports System.Windows.Media.Imaging

Public Class PageInstanceSaves
    Implements IRefreshable

    Private Class WorldSaveEntry
        Public Property FolderPath As String
        Public Property FolderName As String
        Public Property LevelName As String
        Public Property GameVersion As String
        Public Property DataVersion As Integer
        Public Property IsV261Plus As Boolean
        Public Property GameType As Integer
        Public Property Hardcore As Boolean
        Public Property Difficulty As Integer '0..3
        Public Property DifficultyLocked As Boolean
        Public Property AllowCommands As Boolean
        Public Property SeedText As String
        Public Property SpawnText As String
        Public Property LastPlayed As DateTime?
        Public Property IconPath As String
        Public Property SizeBytes As Long
        Public Property HasError As Boolean
        Public Property ErrorText As String
    End Class

    Private Worlds As New List(Of WorldSaveEntry)
    Private SelectedFolderPath As String = Nothing
    Private LoadGeneration As Integer = 0
    Private IsSaving As Boolean = False
    Private ReadOnly GameRuleCheckboxes As New Dictionary(Of String, MyCheckBox)(StringComparer.OrdinalIgnoreCase)
    Private CurrentRescuePlayers As New List(Of ModNbt.WorldPlayerNbtInfo)

    Private Sub EnsureGameRuleCheckboxesBuilt()
        If PanCommonGameRules Is Nothing OrElse GameRuleCheckboxes.Count > 0 Then Return
        For Each Rule In ModNbt.CommonWorldGameRules
            Dim Cb As New MyCheckBox With {
                .Text = Rule.Title,
                .ToolTip = Rule.Description,
                .Margin = New Thickness(0, 0, 18, 6),
                .MinWidth = 220
            }
            GameRuleCheckboxes(Rule.Key) = Cb
            PanCommonGameRules.Children.Add(Cb)
        Next
    End Sub

    Private Function EnsureSelectedWorldWritable() As Boolean
        Dim Instance = If(PageInstanceLeft.Instance, McInstanceSelected)
        If Instance IsNot Nothing AndAlso McInstanceLock.IsLocked(Instance) Then
            Hint("当前实例正在运行或被锁定，请先退出游戏后再修改存档 NBT！", HintType.Red)
            Return False
        End If
        Return True
    End Function

    Private Sub PageInstanceSaves_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        EnsureGameRuleCheckboxesBuilt()
        PanBack.ScrollToHome()
        LoadWorldsAsync()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        LoadWorldsAsync()
    End Sub

    Private Sub BtnRefreshSaves_Click() Handles BtnRefreshSaves.Click
        LoadWorldsAsync()
    End Sub

    Private Sub BtnOpenSavesRoot_Click() Handles BtnOpenSavesRoot.Click
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim SavesDir As String = Instance.PathIndie & "saves\"
        Directory.CreateDirectory(SavesDir)
        OpenExplorer(SavesDir)
    End Sub

    Private Shared Function DifficultyIdToName(Id As Integer) As String
        Select Case Id
            Case 0 : Return "peaceful"
            Case 1 : Return "easy"
            Case 2 : Return "normal"
            Case 3 : Return "hard"
            Case Else : Return "normal"
        End Select
    End Function

    Private Shared Function DifficultyNameToId(Name As String) As Integer
        Select Case If(Name, "").Trim().ToLowerInvariant()
            Case "peaceful" : Return 0
            Case "easy" : Return 1
            Case "normal" : Return 2
            Case "hard" : Return 3
            Case Else : Return 2
        End Select
    End Function

    Private Shared Function FormatGameMode(GameType As Integer, Hardcore As Boolean) As String
        If Hardcore Then Return "极限模式"
        Select Case GameType
            Case 0 : Return "生存"
            Case 1 : Return "创造"
            Case 2 : Return "冒险"
            Case 3 : Return "旁观"
            Case Else : Return $"模式 {GameType}"
        End Select
    End Function

    Private Shared Function FormatSize(Bytes As Long) As String
        If Bytes < 0 Then Return "计算中"
        If Bytes < 1024 Then Return $"{Bytes} B"
        If Bytes < 1024 * 1024 Then Return $"{Bytes / 1024.0:F1} KB"
        If Bytes < 1024L * 1024L * 1024L Then Return $"{Bytes / (1024.0 * 1024.0):F1} MB"
        Return $"{Bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    End Function

    Private Shared Function CalculateDirSize(DirPath As String) As Long
        Try
            Dim Total As Long = 0
            For Each F In Directory.EnumerateFiles(DirPath, "*", SearchOption.AllDirectories)
                Try
                    Total += New FileInfo(F).Length
                Catch
                End Try
            Next
            Return Total
        Catch
            Return 0
        End Try
    End Function

    Private Shared Function ParseWorldEntry(WorldDir As String) As WorldSaveEntry
        Dim Info As New DirectoryInfo(WorldDir)
        Dim Entry As New WorldSaveEntry With {
            .FolderPath = WorldDir.TrimEnd("\"c) & "\",
            .FolderName = Info.Name,
            .LevelName = Info.Name,
            .GameVersion = "未知版本",
            .SeedText = "未知",
            .SpawnText = "未知",
            .Difficulty = 2,
            .SizeBytes = -1
        }
        Dim IconFile As String = Path.Combine(WorldDir, "icon.png")
        If File.Exists(IconFile) Then Entry.IconPath = IconFile

        Dim LevelDat As String = Path.Combine(WorldDir, "level.dat")
        If Not File.Exists(LevelDat) Then
            Entry.HasError = True
            Entry.ErrorText = "缺少 level.dat"
            Return Entry
        End If

        Try
            Dim Root = ReadNbtFile(LevelDat)
            Dim DataTag = Root.GetCompound("Data")
            If DataTag Is Nothing Then DataTag = Root

            Entry.LevelName = If(DataTag.GetString("LevelName"), Info.Name)
            If String.IsNullOrWhiteSpace(Entry.LevelName) Then Entry.LevelName = Info.Name

            Dim VerTag = DataTag.GetCompound("Version")
            If VerTag IsNot Nothing Then
                Dim VerName = VerTag.GetString("Name")
                If Not String.IsNullOrEmpty(VerName) Then Entry.GameVersion = VerName
            End If

            Dim DataVer = DataTag.GetInt("DataVersion")
            If DataVer.HasValue Then Entry.DataVersion = DataVer.Value

            Dim DiffSettings = DataTag.GetCompound("difficulty_settings")
            Entry.IsV261Plus = (Entry.DataVersion >= 4774) OrElse (DiffSettings IsNot Nothing)

            If DiffSettings IsNot Nothing Then
                Entry.Difficulty = DifficultyNameToId(DiffSettings.GetString("difficulty", "normal"))
                Entry.DifficultyLocked = (DiffSettings.GetByte("locked").GetValueOrDefault(0) <> 0)
                Entry.Hardcore = (DiffSettings.GetByte("hardcore").GetValueOrDefault(0) <> 0)
            Else
                Entry.Difficulty = DataTag.GetByte("Difficulty").GetValueOrDefault(2)
                Entry.DifficultyLocked = (DataTag.GetByte("DifficultyLocked").GetValueOrDefault(0) <> 0)
                Entry.Hardcore = (DataTag.GetByte("hardcore").GetValueOrDefault(0) <> 0)
            End If

            Entry.GameType = DataTag.GetInt("GameType").GetValueOrDefault(0)
            Entry.AllowCommands = (DataTag.GetByte("allowCommands").GetValueOrDefault(0) <> 0)

            'Seed：先 RandomSeed，再 WorldGenSettings.seed，最后 26.1+ 外部 world_gen_settings.dat
            Dim SeedVal = DataTag.GetLong("RandomSeed")
            If Not SeedVal.HasValue Then
                Dim Wgs = DataTag.GetCompound("WorldGenSettings")
                If Wgs IsNot Nothing Then SeedVal = Wgs.GetLong("seed")
            End If
            If Not SeedVal.HasValue Then
                Dim ExtWgs As String = Path.Combine(WorldDir, "data", "minecraft", "world_gen_settings.dat")
                If File.Exists(ExtWgs) Then
                    Try
                        Dim WgsRoot = ReadNbtFile(ExtWgs)
                        Dim WgsData = WgsRoot.GetCompound("data")
                        If WgsData IsNot Nothing Then SeedVal = WgsData.GetLong("seed")
                    Catch
                    End Try
                End If
            End If
            If SeedVal.HasValue Then Entry.SeedText = SeedVal.Value.ToString()

            'Spawn：先 26.1+ spawn.pos，再传统 SpawnX/Y/Z
            Dim SpawnTag = DataTag.GetCompound("spawn")
            Dim SpawnPos = If(SpawnTag IsNot Nothing, SpawnTag.GetIntArray("pos"), Nothing)
            If SpawnPos IsNot Nothing AndAlso SpawnPos.Length >= 3 Then
                Entry.SpawnText = $"{SpawnPos(0)}, {SpawnPos(1)}, {SpawnPos(2)}"
            Else
                Dim sx = DataTag.GetInt("SpawnX")
                Dim sy = DataTag.GetInt("SpawnY")
                Dim sz = DataTag.GetInt("SpawnZ")
                If sx.HasValue AndAlso sy.HasValue AndAlso sz.HasValue Then
                    Entry.SpawnText = $"{sx.Value}, {sy.Value}, {sz.Value}"
                End If
            End If

            Dim LastMs = DataTag.GetLong("LastPlayed")
            If LastMs.HasValue AndAlso LastMs.Value > 0 Then
                Entry.LastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(LastMs.Value).LocalDateTime
            End If
        Catch ex As Exception
            Entry.HasError = True
            Entry.ErrorText = $"level.dat 解析失败: {ex.Message}"
        End Try
        Return Entry
    End Function

    Private Sub LoadWorldsAsync()
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim SavesDir As String = Instance.PathIndie & "saves\"
        Dim Gen As Integer = Threading.Interlocked.Increment(LoadGeneration)
        LabSavesSummary.Text = "正在扫描世界存档..."

        RunInNewThread(
        Sub()
            Dim Result As New List(Of WorldSaveEntry)
            If Directory.Exists(SavesDir) Then
                For Each DirPath In Directory.GetDirectories(SavesDir)
                    Result.Add(ParseWorldEntry(DirPath))
                Next
                Result = Result.OrderByDescending(Function(w) If(w.LastPlayed, DateTime.MinValue)).ThenBy(Function(w) w.FolderName).ToList()
            End If

            RunInUi(
            Sub()
                If Gen <> LoadGeneration Then Return
                Worlds = Result
                If SelectedFolderPath IsNot Nothing AndAlso Not Worlds.Any(Function(w) PathUtils.IsSame(w.FolderPath, SelectedFolderPath)) Then
                    SelectedFolderPath = Nothing
                End If
                If SelectedFolderPath Is Nothing AndAlso Worlds.Any() Then
                    SelectedFolderPath = Worlds(0).FolderPath
                End If
                RenderWorldList()
                RenderSelectedDetail()
            End Sub)

            '异步补全占用空间大小
            For Each Entry In Result
                If Gen <> LoadGeneration Then Exit For
                Entry.SizeBytes = CalculateDirSize(Entry.FolderPath)
            Next
            RunInUi(
            Sub()
                If Gen <> LoadGeneration Then Return
                RenderWorldList()
                RenderSelectedDetail()
            End Sub)
        End Sub, "Instance Saves Scan")
    End Sub

    Private Sub RenderWorldList()
        PanSavesList.Children.Clear()
        HintNoSaves.Visibility = If(Worlds.Any(), Visibility.Collapsed, Visibility.Visible)
        LabSavesSummary.Text = If(Worlds.Any(), $"共发现 {Worlds.Count} 个世界存档（点击卡片可编辑规则与数据包）", "未发现世界存档")

        For Each Entry In Worlds
            Dim LocalEntry = Entry
            Dim IsSelected As Boolean = SelectedFolderPath IsNot Nothing AndAlso PathUtils.IsSame(SelectedFolderPath, LocalEntry.FolderPath)
            Dim ModeText As String = FormatGameMode(LocalEntry.GameType, LocalEntry.Hardcore)
            Dim TimeText As String = If(LocalEntry.LastPlayed.HasValue, LocalEntry.LastPlayed.Value.ToString("yyyy-MM-dd HH:mm"), "未知时间")
            Dim InfoStr As String = If(LocalEntry.HasError,
                                       $"文件夹: {LocalEntry.FolderName} · {LocalEntry.ErrorText}",
                                       $"文件夹: {LocalEntry.FolderName} · {LocalEntry.GameVersion} · {ModeText} · 大小 {FormatSize(LocalEntry.SizeBytes)} · 最后游玩 {TimeText}")

            Dim Item As New MyListItem With {
                .Title = LocalEntry.LevelName & If(IsSelected, "（当前选中）", ""),
                .Info = InfoStr,
                .Height = 46,
                .Type = MyListItem.CheckType.Clickable,
                .Logo = If(Not String.IsNullOrEmpty(LocalEntry.IconPath) AndAlso File.Exists(LocalEntry.IconPath),
                           LocalEntry.IconPath,
                           "pack://application:,,,/images/Blocks/Grass.png")
            }
            AddHandler Item.Click,
            Sub()
                SelectedFolderPath = LocalEntry.FolderPath
                RenderWorldList()
                RenderSelectedDetail()
            End Sub
            PanSavesList.Children.Add(Item)
        Next
    End Sub

    Private Function GetSelectedWorld() As WorldSaveEntry
        If SelectedFolderPath Is Nothing Then Return Nothing
        Return Worlds.FirstOrDefault(Function(w) PathUtils.IsSame(w.FolderPath, SelectedFolderPath))
    End Function

    Private Sub RenderSelectedDetail()
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing Then
            CardSaveDetail.Visibility = Visibility.Collapsed
            CardPlayerOverview.Visibility = Visibility.Collapsed
            CardMcaTrimmer.Visibility = Visibility.Collapsed
            CardDatapacks.Visibility = Visibility.Collapsed
            Return
        End If
        CardSaveDetail.Visibility = Visibility.Visible
        CardPlayerOverview.Visibility = Visibility.Visible
        CardMcaTrimmer.Visibility = Visibility.Visible
        CardDatapacks.Visibility = Visibility.Visible
        LabMcaStatus.Text = "点击「扫描可清理区块」预览主世界/下界/末地可回收的区块与空间大小。"

        '加载世界图标（使用 OnLoad 防止锁定文件）
        Try
            If Not String.IsNullOrEmpty(Entry.IconPath) AndAlso File.Exists(Entry.IconPath) Then
                Dim Bmp As New BitmapImage()
                Bmp.BeginInit()
                Bmp.CacheOption = BitmapCacheOption.OnLoad
                Bmp.UriSource = New Uri(Entry.IconPath, UriKind.Absolute)
                Bmp.EndInit()
                ImgWorldIcon.Source = Bmp
            Else
                ImgWorldIcon.Source = New BitmapImage(New Uri("pack://application:,,,/images/Blocks/Grass.png", UriKind.Absolute))
            End If
        Catch
            ImgWorldIcon.Source = Nothing
        End Try

        LabDetailTitle.Text = $"{Entry.LevelName}（{Entry.FolderName}）"
        LabDetailMeta.Text = $"版本: {Entry.GameVersion}   模式: {FormatGameMode(Entry.GameType, Entry.Hardcore)}   出生点: {Entry.SpawnText}   大小: {FormatSize(Entry.SizeBytes)}"
        LabDetailSeed.Text = $"世界种子: {Entry.SeedText}   " & If(Entry.IsV261Plus, "[26.1+ 格式]", "[传统 NBT 格式]")

        TextEditLevelName.Text = Entry.LevelName
        ComboEditDifficulty.SelectedIndex = Math.Max(0, Math.Min(3, Entry.Difficulty))
        CheckAllowCommands.Checked = Entry.AllowCommands
        CheckDifficultyLocked.Checked = Entry.DifficultyLocked

        BtnCopySeed.IsEnabled = (Entry.SeedText <> "未知")
        BtnSaveNbt.IsEnabled = Not Entry.HasError
        BtnEditSeedOrRule.IsEnabled = Not Entry.HasError

        EnsureGameRuleCheckboxesBuilt()
        Dim LevelDat = Path.Combine(Entry.FolderPath, "level.dat")
        Dim RuleValues As Dictionary(Of String, String) = Nothing
        If Not Entry.HasError AndAlso File.Exists(LevelDat) Then
            Try
                RuleValues = ModNbt.ReadWorldGameRules(LevelDat)
            Catch
                RuleValues = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            End Try
        Else
            RuleValues = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        End If
        For Each Rule In ModNbt.CommonWorldGameRules
            Dim Cb As MyCheckBox = Nothing
            If GameRuleCheckboxes.TryGetValue(Rule.Key, Cb) Then
                Dim RawVal As String = Nothing
                If Not RuleValues.TryGetValue(Rule.Key, RawVal) OrElse String.IsNullOrWhiteSpace(RawVal) Then
                    RawVal = Rule.DefaultValue
                End If
                Cb.Checked = RawVal.Equals("true", StringComparison.OrdinalIgnoreCase) OrElse RawVal = "1"
            End If
        Next

        RenderPlayerOverviewAsync(Entry)
        RenderDatapacksList(Entry)
    End Sub

    Private Sub RenderDatapacksList(Entry As WorldSaveEntry)
        PanDatapacksList.Children.Clear()
        Dim DatapackDir As String = Path.Combine(Entry.FolderPath, "datapacks")
        If Not Directory.Exists(DatapackDir) Then
            HintNoDatapacks.Visibility = Visibility.Visible
            Return
        End If

        Dim Items As New List(Of (FullPath As String, DisplayName As String, IsDir As Boolean, IsEnabled As Boolean))
        For Each F In Directory.GetFiles(DatapackDir)
            Dim Name = Path.GetFileName(F)
            If Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
                Items.Add((F, Name, False, True))
            ElseIf Name.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase) Then
                Items.Add((F, Name.Substring(0, Name.Length - ".disabled".Length), False, False))
            End If
        Next
        For Each D In Directory.GetDirectories(DatapackDir)
            Dim Name = Path.GetFileName(D)
            If Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) Then
                Items.Add((D, Name.Substring(0, Name.Length - ".disabled".Length), True, False))
            ElseIf File.Exists(Path.Combine(D, "pack.mcmeta")) Then
                Items.Add((D, Name, True, True))
            End If
        Next

        HintNoDatapacks.Visibility = If(Items.Any(), Visibility.Collapsed, Visibility.Visible)
        For Each Dp In Items.OrderByDescending(Function(x) x.IsEnabled).ThenBy(Function(x) x.DisplayName)
            Dim LocalDp = Dp
            Dim Row As New MyListItem With {
                .Title = LocalDp.DisplayName & If(LocalDp.IsEnabled, "", "（已禁用）"),
                .Info = If(LocalDp.IsDir, "文件夹数据包", "ZIP 压缩数据包") & " · 点击右侧图标切换启用状态或删除",
                .Height = 42,
                .Type = MyListItem.CheckType.Clickable,
                .Logo = If(LocalDp.IsEnabled, "pack://application:,,,/images/Blocks/CommandBlock.png", "pack://application:,,,/images/Blocks/CobbleStone.png")
            }

            Dim BtnToggle As New MyIconButton With {
                .ToolTip = If(LocalDp.IsEnabled, "禁用数据包", "启用数据包"),
                .Logo = If(LocalDp.IsEnabled, Logo.IconButtonCross, Logo.IconButtonRefresh)
            }
            AddHandler BtnToggle.Click,
            Sub()
                Try
                    Dim NewPath As String = If(LocalDp.IsEnabled, LocalDp.FullPath & ".disabled", LocalDp.FullPath.Substring(0, LocalDp.FullPath.Length - ".disabled".Length))
                    If LocalDp.IsDir Then
                        Directory.Move(LocalDp.FullPath, NewPath)
                    Else
                        File.Move(LocalDp.FullPath, NewPath)
                    End If
                    Hint(If(LocalDp.IsEnabled, $"已禁用 {LocalDp.DisplayName}", $"已启用 {LocalDp.DisplayName}"), HintType.Green)
                    RenderDatapacksList(Entry)
                Catch ex As Exception
                    Hint($"操作数据包失败：{ex.Message}", HintType.Red)
                End Try
            End Sub

            Dim BtnRemove As New MyIconButton With {.ToolTip = "删除数据包", .Logo = Logo.IconButtonDelete}
            AddHandler BtnRemove.Click,
            Sub()
                If MyMsgBox($"确定要删除数据包「{LocalDp.DisplayName}」吗？", "删除数据包", "删除", "取消", IsWarn:=True) <> 1 Then Return
                Try
                    If LocalDp.IsDir Then
                        Directory.Delete(LocalDp.FullPath, True)
                    Else
                        File.Delete(LocalDp.FullPath)
                    End If
                    Hint($"已删除数据包 {LocalDp.DisplayName}", HintType.Green)
                    RenderDatapacksList(Entry)
                Catch ex As Exception
                    Hint($"删除数据包失败：{ex.Message}", HintType.Red)
                End Try
            End Sub

            Row.Buttons = {BtnToggle, BtnRemove}
            PanDatapacksList.Children.Add(Row)
        Next
    End Sub

    Private Sub BtnCopySeed_Click() Handles BtnCopySeed.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing OrElse Entry.SeedText = "未知" Then Return
        ClipboardSet(Entry.SeedText)
        Hint($"已复制世界种子：{Entry.SeedText}", HintType.Green)
    End Sub

    Private Sub BtnOpenWorldDir_Click() Handles BtnOpenWorldDir.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing Then Return
        OpenExplorer(Entry.FolderPath)
    End Sub

    Private Sub BtnOpenDatapacksDir_Click() Handles BtnOpenDatapacksDir.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing Then Return
        Dim DpDir As String = Path.Combine(Entry.FolderPath, "datapacks")
        Directory.CreateDirectory(DpDir)
        OpenExplorer(DpDir)
    End Sub

    Private Sub BtnAddDatapack_Click() Handles BtnAddDatapack.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing Then Return
        Dim Chosen = Dialogs.SelectFile("选择要导入的 Minecraft 数据包 (.zip)", True, filter:="数据包压缩包 (*.zip)|*.zip")
        If Chosen Is Nothing OrElse Not Chosen.Any() Then Return
        Dim DpDir As String = Path.Combine(Entry.FolderPath, "datapacks")
        Directory.CreateDirectory(DpDir)
        Dim Added As Integer = 0
        For Each Src In Chosen
            Try
                Dim Dest = Path.Combine(DpDir, Path.GetFileName(Src))
                File.Copy(Src, Dest, True)
                Added += 1
            Catch ex As Exception
                Logger.Warn(ex, $"导入数据包失败: {Src}")
            End Try
        Next
        If Added > 0 Then
            Hint($"已成功导入 {Added} 个数据包", HintType.Green)
            RenderDatapacksList(Entry)
        End If
    End Sub

    Private Sub BtnSaveNbt_Click() Handles BtnSaveNbt.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing OrElse IsSaving Then Return
        If Not EnsureSelectedWorldWritable() Then Return
        Dim NewName = TextEditLevelName.Text.Trim()
        If String.IsNullOrEmpty(NewName) Then
            Hint("存档名称不能为空！", HintType.Red)
            Return
        End If

        Dim LevelDat As String = Path.Combine(Entry.FolderPath, "level.dat")
        If Not File.Exists(LevelDat) Then
            Hint("找不到 level.dat 文件！", HintType.Red)
            Return
        End If

        Dim DiffIdx As Integer = Math.Max(0, Math.Min(3, ComboEditDifficulty.SelectedIndex))
        Dim AllowCheat As Byte = If(CheckAllowCommands.Checked, CByte(1), CByte(0))
        Dim LockedByte As Byte = If(CheckDifficultyLocked.Checked, CByte(1), CByte(0))
        Dim RuleUpdates As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each Kv In GameRuleCheckboxes
            RuleUpdates(Kv.Key) = If(Kv.Value.Checked, "true", "false")
        Next

        IsSaving = True
        BtnSaveNbt.IsEnabled = False
        Try
            ModNbt.UpdateLevelDatFieldsAndRules(
                LevelDat,
                Sub(DataTag)
                    DataTag.SetString("LevelName", NewName)
                    DataTag.SetByte("allowCommands", AllowCheat)
                    Dim DiffSettings = DataTag.GetCompound("difficulty_settings")
                    Dim DataVer = DataTag.GetInt("DataVersion").GetValueOrDefault(0)
                    If DiffSettings IsNot Nothing OrElse DataVer >= 4774 Then
                        If DiffSettings Is Nothing Then
                            DiffSettings = New NbtTag With {
                                .Type = NbtTagType.Compound,
                                .Name = "difficulty_settings",
                                .Value = New Dictionary(Of String, NbtTag)(StringComparer.Ordinal)
                            }
                            DataTag.AsCompound()("difficulty_settings") = DiffSettings
                        End If
                        DiffSettings.SetString("difficulty", DifficultyIdToName(DiffIdx))
                        DiffSettings.SetByte("locked", LockedByte)
                    Else
                        DataTag.SetByte("Difficulty", CByte(DiffIdx))
                        DataTag.SetByte("DifficultyLocked", LockedByte)
                    End If
                End Sub,
                RuleUpdates)

            Hint("已保存存档设置与 GameRules 到 level.dat（已自动备份 level.dat_old）", HintType.Green)
            LoadWorldsAsync()
        Catch ex As Exception
            Logger.Error(ex, "写入 level.dat 失败")
            Hint($"保存失败：{ex.Message}", HintType.Red)
        Finally
            IsSaving = False
            BtnSaveNbt.IsEnabled = True
        End Try
    End Sub

    Private Sub BtnEditSeedOrRule_Click() Handles BtnEditSeedOrRule.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing OrElse Entry.HasError Then Return
        If Not EnsureSelectedWorldWritable() Then Return
        Dim LevelDat As String = Path.Combine(Entry.FolderPath, "level.dat")
        If Not File.Exists(LevelDat) Then Return

        Dim Options As IMyRadio() = {
            New MyRadioBox With {.Text = "修改自定义游戏规则 (GameRule 键值对，例如 randomTickSpeed / playersSleepingPercentage)"},
            New MyRadioBox With {.Text = "修改世界种子 (Seed)（注：仅对尚未生成的新区块生效）"}
        }
        Dim Picked = MyMsgBoxSelect(Options, $"高级 NBT 编辑 — {Entry.LevelName}：", "下一步", "取消")
        If Picked Is Nothing Then Return

        Try
            If Picked.Value = 0 Then
                Dim CurrentRules = ModNbt.ReadWorldGameRules(LevelDat)
                Dim RuleKey = MyMsgBoxInput("输入 GameRule 规则名",
                    "常见数值规则示例：randomTickSpeed（默认 3）、playersSleepingPercentage（默认 100）、spawnRadius（默认 10）", "randomTickSpeed")
                If String.IsNullOrWhiteSpace(RuleKey) Then Return
                Dim CleanKey = RuleKey.Trim()
                Dim ExistingVal = ""
                CurrentRules.TryGetValue(CleanKey, ExistingVal)
                Dim RuleVal = MyMsgBoxInput($"设置规则 {CleanKey} 的值", $"当前值：{If(String.IsNullOrEmpty(ExistingVal), "(未显式写入，使用原版默认)", ExistingVal)}", ExistingVal)
                If RuleVal Is Nothing Then Return
                Dim Updates As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {{CleanKey, RuleVal.Trim()}}
                ModNbt.UpdateLevelDatFieldsAndRules(LevelDat, Nothing, Updates)
                Hint($"已更新规则 {CleanKey} = {RuleVal.Trim()}（已备份 level.dat_old）！", HintType.Green)
                RenderSelectedDetail()
            Else
                Dim RawSeed = MyMsgBoxInput("修改世界种子 (64 位整数)", "警告：修改种子只会影响之后新探索生成的新区块，已生成地形不会改变。当前：" & Entry.SeedText, If(Entry.SeedText = "未知", "", Entry.SeedText))
                If String.IsNullOrWhiteSpace(RawSeed) Then Return
                Dim ParsedSeed As Long
                If Not Long.TryParse(RawSeed.Trim(), ParsedSeed) Then
                    Hint("请输入有效的 64 位整数种子（例如 -1234567890）！", HintType.Red)
                    Return
                End If
                ModNbt.UpdateLevelDatFieldsAndRules(LevelDat, Nothing, Nothing, ParsedSeed)
                Hint($"已修改世界种子为 {ParsedSeed}（已备份 level.dat_old）！", HintType.Green)
                LoadWorldsAsync()
            End If
        Catch ex As Exception
            Logger.Error(ex, "高级编辑 level.dat 失败")
            Hint($"保存失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnBackupWorld_Click() Handles BtnBackupWorld.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing Then Return
        Dim Stamp As String = DateTime.Now.ToString("yyyyMMdd-HHmmss")
        Dim DefaultName As String = $"{Entry.FolderName}-{Stamp}.zip"
        Dim OutZip As String = Dialogs.SaveFile("导出世界存档备份", DefaultName, filter:={("zip", "ZIP 存档备份 (*.zip)")})
        If String.IsNullOrEmpty(OutZip) Then Return

        Hint($"正在打包备份「{Entry.LevelName}」...", Log:=False)
        RunInNewThread(
        Sub()
            Try
                If File.Exists(OutZip) Then File.Delete(OutZip)
                ZipFile.CreateFromDirectory(Entry.FolderPath.TrimEnd("\"c), OutZip, CompressionLevel.Optimal, True)
                RunInUi(Sub() Hint($"已备份世界到 {Path.GetFileName(OutZip)}", HintType.Green))
            Catch ex As Exception
                Logger.Error(ex, "备份世界存档失败")
                RunInUi(Sub() Hint($"备份失败：{ex.Message}", HintType.Red))
            End Try
        End Sub, "World Backup Zip")
    End Sub

    Private Sub BtnDeleteWorld_Click() Handles BtnDeleteWorld.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing Then Return
        If MyMsgBox($"确定要将世界「{Entry.LevelName}」（文件夹：{Entry.FolderName}）移至回收站吗？", "删除世界存档", "移至回收站", "取消", IsWarn:=True) <> 1 Then Return
        Try
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                Entry.FolderPath.TrimEnd("\"c),
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin)
            SelectedFolderPath = Nothing
            Hint($"已将世界「{Entry.LevelName}」移至回收站", HintType.Green)
            LoadWorldsAsync()
        Catch ex As Exception
            Hint($"删除世界失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnQuickPlayWorld_Click() Handles BtnQuickPlayWorld.Click
        Dim Entry = GetSelectedWorld()
        Dim Instance = If(PageInstanceLeft.Instance, McInstanceSelected)
        If Entry Is Nothing OrElse Instance Is Nothing Then Return
        If Instance.Version IsNot Nothing AndAlso Instance.Version.ReleaseTime <= New Date(2023, 4, 4) Then
            Hint($"该实例版本较早，不支持 QuickPlay 直连；正在为你启动实例「{Instance.Name}」...", HintType.Blue)
        Else
            Hint($"正在启动「{Instance.Name}」并直接进入世界「{Entry.LevelName}」...", HintType.Green)
        End If
        ModLaunch.McLaunchStart(New McLaunchOptions With {
            .Instance = Instance,
            .SingleplayerWorld = Entry.FolderName
        })
    End Sub

    Private Sub BtnShortcutWorld_Click() Handles BtnShortcutWorld.Click
        Dim Entry = GetSelectedWorld()
        Dim Instance = If(PageInstanceLeft.Instance, McInstanceSelected)
        If Entry Is Nothing OrElse Instance Is Nothing Then Return
        Try
            Dim Args = $"--launch ""{Instance.Name}"" --world ""{Entry.FolderName}"" --folder ""{McFolderSelected.TrimEnd("\"c)}"""
            Dim SafeTitle = Entry.LevelName
            For Each C In Path.GetInvalidFileNameChars()
                SafeTitle = SafeTitle.Replace(C, "_"c)
            Next
            Dim Lnk = CreateDesktopShortcut($"进入世界 {SafeTitle}", Args, $"通过 PCL Linking 使用实例 {Instance.Name} 进入存档 {Entry.LevelName}")
            Hint($"已在桌面创建快捷方式：{Path.GetFileName(Lnk)}", HintType.Green)
        Catch ex As Exception
            Hint($"创建快捷方式失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Shared Function FormatDimensionLabel(DimTag As NbtTag) As String
        If DimTag Is Nothing OrElse DimTag.Value Is Nothing Then Return "主世界"
        Dim Raw = DimTag.Value.ToString()
        Select Case Raw.ToLowerInvariant()
            Case "0", "minecraft:overworld" : Return "主世界 (Overworld)"
            Case "-1", "minecraft:the_nether" : Return "下界 (Nether)"
            Case "1", "minecraft:the_end" : Return "末地 (End)"
            Case Else : Return Raw
        End Select
    End Function

    Public Shared Function ParsePlayerOverview(WorldDir As String) As (Summary As String, Inventory As String)
        If String.IsNullOrWhiteSpace(WorldDir) OrElse Not Directory.Exists(WorldDir) Then
            Return ("未找到世界目录", "")
        End If
        Dim PlayerTag As NbtTag = Nothing
        Dim SourceLabel As String = ""
        Try
            Dim PlayerDataDir = Path.Combine(WorldDir, "playerdata")
            If Directory.Exists(PlayerDataDir) Then
                Dim DatFile = Directory.GetFiles(PlayerDataDir, "*.dat").
                    OrderByDescending(Function(f) File.GetLastWriteTimeUtc(f)).
                    FirstOrDefault()
                If DatFile IsNot Nothing Then
                    PlayerTag = ReadNbtFile(DatFile)
                    SourceLabel = Path.GetFileNameWithoutExtension(DatFile)
                    If SourceLabel.Length > 8 Then SourceLabel = SourceLabel.Substring(0, 8) & "..."
                End If
            End If
            If PlayerTag Is Nothing Then
                Dim LevelDat = Path.Combine(WorldDir, "level.dat")
                If File.Exists(LevelDat) Then
                    Dim Root = ReadNbtFile(LevelDat)
                    Dim DataTag = If(Root.GetCompound("Data"), Root)
                    PlayerTag = DataTag.GetCompound("Player")
                    SourceLabel = "单人存档 (level.dat)"
                End If
            End If
        Catch ex As Exception
            Return ($"读取玩家 NBT 失败：{ex.Message}", "")
        End Try

        If PlayerTag Is Nothing Then
            Return ("该存档暂无玩家状态记录（可能尚未进入过世界）。", "")
        End If

        Dim DimText = FormatDimensionLabel(PlayerTag.GetChild("Dimension"))
        Dim PosText = "未知"
        Dim PosList = PlayerTag.GetList("Pos")
        If PosList IsNot Nothing AndAlso PosList.Count >= 3 Then
            PosText = $"({Convert.ToDouble(PosList(0).Value):F1}, {Convert.ToDouble(PosList(1).Value):F1}, {Convert.ToDouble(PosList(2).Value):F1})"
        End If
        Dim Health = PlayerTag.GetFloat("Health").GetValueOrDefault(20.0F)
        Dim Food = PlayerTag.GetInt("foodLevel").GetValueOrDefault(20)
        Dim Xp = PlayerTag.GetInt("XpLevel").GetValueOrDefault(0)
        Dim Summary = $"玩家: {SourceLabel}   所在维度: {DimText}   坐标: {PosText}{Environment.NewLine}生命值: {Health:F1} / 20.0   饱食度: {Food} / 20   经验等级: Lv.{Xp}"

        Dim InvCounts As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Dim InvList = PlayerTag.GetList("Inventory")
        If InvList IsNot Nothing Then
            For Each ItemTag In InvList
                Dim Id = ItemTag.GetString("id", "")
                If String.IsNullOrWhiteSpace(Id) Then Continue For
                Dim ShortId = If(Id.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase), Id.Substring("minecraft:".Length), Id)
                Dim Cnt = If(ItemTag.GetInt("count"), ItemTag.GetByte("Count")).GetValueOrDefault(1)
                If InvCounts.ContainsKey(ShortId) Then
                    InvCounts(ShortId) += Cnt
                Else
                    InvCounts(ShortId) = Cnt
                End If
            Next
        End If
        Dim InventoryText As String
        If InvCounts.Count = 0 Then
            InventoryText = "背包物品: （空）"
        Else
            Dim TopItems = InvCounts.OrderByDescending(Function(k) k.Value).Take(18).Select(Function(k) $"{k.Key} ×{k.Value}")
            InventoryText = $"背包物品（共 {InvCounts.Count} 种，显示前 {Math.Min(18, InvCounts.Count)} 项）: " & String.Join("、", TopItems)
        End If
        Return (Summary, InventoryText)
    End Function

    Private Sub RenderPlayerOverviewAsync(Entry As WorldSaveEntry)
        Dim TargetDir = Entry.FolderPath
        LabPlayerSummary.Text = "正在解析玩家 NBT 数据..."
        LabInventorySummary.Text = ""
        HintPlayerStuckRisk.Visibility = Visibility.Collapsed
        ComboRescuePlayer.Items.Clear()
        ComboRescuePlayer.IsEnabled = False
        BtnRescuePlayer.IsEnabled = False
        CurrentRescuePlayers = New List(Of ModNbt.WorldPlayerNbtInfo)

        RunInNewThread(
        Sub()
            Dim Info = ParsePlayerOverview(TargetDir)
            Dim Players = ModNbt.InspectWorldPlayers(TargetDir)
            RunInUi(
            Sub()
                Dim Current = GetSelectedWorld()
                If Current Is Nothing OrElse Not PathUtils.IsSame(Current.FolderPath, TargetDir) Then Return
                LabPlayerSummary.Text = Info.Summary
                LabInventorySummary.Text = Info.Inventory
                CurrentRescuePlayers = Players
                ComboRescuePlayer.Items.Clear()
                For Each P In Players
                    Dim RiskFlag = If(P.IsStuckRisk, " [疑似卡死/虚空]", "")
                    ComboRescuePlayer.Items.Add(New MyComboBoxItem With {
                        .Content = $"{P.DisplayName}{RiskFlag} · ({P.PosX:F0}, {P.PosY:F0}, {P.PosZ:F0})"
                    })
                Next
                If ComboRescuePlayer.Items.Count > 0 Then
                    ComboRescuePlayer.SelectedIndex = 0
                    ComboRescuePlayer.IsEnabled = True
                    BtnRescuePlayer.IsEnabled = True
                End If
                HintPlayerStuckRisk.Visibility = If(Players.Any(Function(p) p.IsStuckRisk), Visibility.Visible, Visibility.Collapsed)
            End Sub)
        End Sub, "World Player Overview")
    End Sub

    Private Sub BtnRescuePlayer_Click() Handles BtnRescuePlayer.Click
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing OrElse Entry.HasError Then Return
        If Not EnsureSelectedWorldWritable() Then Return
        Dim Idx = ComboRescuePlayer.SelectedIndex
        If Idx < 0 OrElse Idx >= CurrentRescuePlayers.Count Then
            Hint("请先选择要救援的玩家档案！", HintType.Blue)
            Return
        End If
        Dim Target = CurrentRescuePlayers(Idx)
        Dim ConfirmMsg = $"确定将「{Target.DisplayName}」安全传送回主世界出生点吗？{vbCrLf}{vbCrLf}" &
                         $"当前维度：{Target.Dimension}{vbCrLf}" &
                         $"当前坐标：({Target.PosX:F1}, {Target.PosY:F1}, {Target.PosZ:F1}) · 生命 {Target.Health:F1}{vbCrLf}{vbCrLf}" &
                         "操作会清空坠落速度、重置火焰状态并在同目录保留 _old 原文件备份。"
        If MyMsgBox(ConfirmMsg, "卡服玩家安全回出生点救援", "确认传送回出生点", "取消", IsWarn:=True) <> 1 Then Return

        Try
            Dim Res = ModNbt.RescueWorldPlayerToSpawn(Entry.FolderPath, Target.FilePath, Target.IsLevelDatPlayer)
            Hint($"已将 {Target.DisplayName} 安全传送回出生点 ({Res.SpawnX:F0}, {Res.SpawnY:F0}, {Res.SpawnZ:F0})！", HintType.Green)
            RenderPlayerOverviewAsync(Entry)
        Catch ex As Exception
            Logger.Error(ex, "卡服玩家回出生点救援失败")
            Hint($"救援失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private IsMcaBusy As Boolean = False

    Private Function GetSelectedMcaThreshold() As Long
        Dim Item = TryCast(ComboMcaThreshold.SelectedItem, MyComboBoxItem)
        Dim Val As Long = 1200
        If Item?.Tag IsNot Nothing Then Long.TryParse(Item.Tag.ToString(), Val)
        Return Math.Max(0L, Val)
    End Function

    Public Shared Function EnumerateWorldMcaFiles(WorldDir As String) As List(Of String)
        Dim List As New List(Of String)()
        If String.IsNullOrWhiteSpace(WorldDir) OrElse Not Directory.Exists(WorldDir) Then Return List
        Dim SubDirs = {"region", Path.Combine("DIM-1", "region"), Path.Combine("DIM1", "region")}
        For Each SubDir In SubDirs
            Dim FullSub = Path.Combine(WorldDir, SubDir)
            If Directory.Exists(FullSub) Then
                List.AddRange(Directory.GetFiles(FullSub, "*.mca", SearchOption.TopDirectoryOnly))
            End If
        Next
        Return List
    End Function

    Private Sub BtnScanMca_Click() Handles BtnScanMca.Click
        If IsMcaBusy Then Return
        Dim Entry = GetSelectedWorld()
        If Entry Is Nothing Then Return
        Dim Threshold = GetSelectedMcaThreshold()
        Dim TargetDir = Entry.FolderPath
        IsMcaBusy = True
        BtnScanMca.IsEnabled = False
        BtnTrimMca.IsEnabled = False
        LabMcaStatus.Text = "正在扫描 region/*.mca 区块停留时间，请稍候..."
        RunInNewThread(
        Sub()
            Try
                Dim Files = EnumerateWorldMcaFiles(TargetDir)
                Dim TotalRegions = Files.Count
                Dim TotalChunks As Integer = 0
                Dim PrunableChunks As Integer = 0
                Dim BytesBefore As Long = 0
                For Each F In Files
                    Dim R = ModNbt.CompactMcaRegionFile(F, Threshold, DryRun:=True)
                    TotalChunks += R.TotalChunks
                    PrunableChunks += R.PrunableChunks
                    BytesBefore += R.BytesBefore
                Next
                RunInUi(
                Sub()
                    LabMcaStatus.Text = If(TotalRegions = 0,
                        "未在该存档中找到 region/*.mca 区块文件。",
                        $"扫描完成：共 {TotalRegions} 个区域文件、{TotalChunks} 个已生成区块（总计 {FormatSize(BytesBefore)}），其中 {PrunableChunks} 个区块满足 InhabitedTime ≤ {Threshold} ticks，可点击「执行清理瘦身」安全清理。")
                End Sub)
            Catch ex As Exception
                RunInUi(Sub() LabMcaStatus.Text = $"扫描失败：{ex.Message}")
            Finally
                RunInUi(
                Sub()
                    IsMcaBusy = False
                    BtnScanMca.IsEnabled = True
                    BtnTrimMca.IsEnabled = True
                End Sub)
            End Try
        End Sub, "MCA Scan")
    End Sub

    Private Sub BtnTrimMca_Click() Handles BtnTrimMca.Click
        If IsMcaBusy Then Return
        Dim Entry = GetSelectedWorld()
        Dim Instance = If(PageInstanceLeft.Instance, McInstanceSelected)
        If Entry Is Nothing OrElse Instance Is Nothing Then Return
        Dim Threshold = GetSelectedMcaThreshold()
        Dim DoBackup = ChkMcaAutoBackup.Checked
        If MyMsgBox($"确定要清理世界「{Entry.LevelName}」中停留时间 ≤ {Threshold} ticks 的区块吗？" & Environment.NewLine &
                    If(DoBackup, "清理前会自动将所有 region/*.mca 备份到实例 PCL Linking/RegionBackups。", "未勾选自动备份，清理将直接覆盖 .mca 文件！"),
                    "确认区块清理瘦身", "开始清理", "取消", IsWarn:=True) <> 1 Then Return

        Dim TargetDir = Entry.FolderPath
        Dim WorldFolderName = Entry.FolderName
        Dim IndieDir = Instance.PathIndie
        IsMcaBusy = True
        BtnScanMca.IsEnabled = False
        BtnTrimMca.IsEnabled = False
        LabMcaStatus.Text = "正在备份并重排区域区块文件，请勿关闭启动器..."

        RunInNewThread(
        Sub()
            Try
                Dim Files = EnumerateWorldMcaFiles(TargetDir)
                If Files.Count = 0 Then
                    RunInUi(Sub() LabMcaStatus.Text = "未找到可清理的 region/*.mca 文件。")
                    Return
                End If

                Dim BackupZipName As String = ""
                If DoBackup Then
                    Dim BakDir = Path.Combine(IndieDir, "PCL Linking", "RegionBackups")
                    Directory.CreateDirectory(BakDir)
                    BackupZipName = Path.Combine(BakDir, $"{WorldFolderName}_{DateTime.Now:yyyyMMdd_HHmmss}.zip")
                    Using Zip = ZipFile.Open(BackupZipName, ZipArchiveMode.Create)
                        For Each F In Files
                            Dim Rel = F.Substring(TargetDir.TrimEnd("\"c).Length).TrimStart("\"c)
                            Zip.CreateEntryFromFile(F, Rel, CompressionLevel.Fastest)
                        Next
                    End Using
                End If

                Dim PrunedTotal As Integer = 0
                Dim SavedTotal As Long = 0
                For Each F In Files
                    Dim R = ModNbt.CompactMcaRegionFile(F, Threshold, DryRun:=False)
                    PrunedTotal += R.PrunableChunks
                    SavedTotal += R.BytesSaved
                Next
                RunInUi(
                Sub()
                    Dim BakMsg = If(String.IsNullOrEmpty(BackupZipName), "", $" 已备份到 {Path.GetFileName(BackupZipName)}。")
                    LabMcaStatus.Text = $"清理完成：共移除 {PrunedTotal} 个低停留区块，释放空间 {FormatSize(SavedTotal)}。{BakMsg}"
                    Hint($"区块清理完成，共释放 {FormatSize(SavedTotal)}", HintType.Green)
                    LoadWorldsAsync()
                End Sub)
            Catch ex As Exception
                Logger.Error(ex, "区块清理瘦身失败")
                RunInUi(
                Sub()
                    LabMcaStatus.Text = $"清理失败：{ex.Message}"
                    Hint($"区块清理失败：{ex.Message}", HintType.Red)
                End Sub)
            Finally
                RunInUi(
                Sub()
                    IsMcaBusy = False
                    BtnScanMca.IsEnabled = True
                    BtnTrimMca.IsEnabled = True
                End Sub)
            End Try
        End Sub, "MCA Trim")
    End Sub

End Class
