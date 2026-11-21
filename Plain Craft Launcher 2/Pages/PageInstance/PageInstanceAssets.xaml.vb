Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Windows.Media.Imaging

Public Class PageInstanceAssets
    Implements IRefreshable

    Public Class AssetPackEntry
        Public Property FilePath As String
        Public Property FileName As String '可能带 .disabled
        Public Property BaseName As String '去掉 .disabled 的原始文件/文件夹名
        Public Property IsFolder As Boolean
        Public Property IsShaderPack As Boolean = False
        Public Property IsDiskDisabled As Boolean
        Public Property IsActiveInGame As Boolean
        Public Property PriorityOrder As Integer '仅针对已激活资源包：0=最高优先级
        Public Property Description As String = ""
        Public Property PackFormat As Integer = 0
        Public Property SizeBytes As Long = 0
        Public Property IconImage As BitmapImage
        Public Property SourcePlatform As String = ""
        Public Property RemoteProjectName As String = ""
        Public Property CurrentVersionText As String = ""
        Public Property UpdateVersion As ResourceVersion = Nothing
    End Class

    Private LoadGen As Integer = 0
    Private IsUpdatingUiState As Boolean = False
    Private IsCheckingUpdates As Boolean = False
    Private LastRpEntries As New List(Of AssetPackEntry)
    Private LastSpEntries As New List(Of AssetPackEntry)
    Private LastActiveShader As String = ""
    Private LastShaderModNote As String = ""

    Private Sub PageInstanceAssets_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        SyncAutoUpdateCheckbox()
        LoadAllAssetsAsync()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        SyncAutoUpdateCheckbox()
        LoadAllAssetsAsync()
    End Sub

    Private Sub SyncAutoUpdateCheckbox()
        If PageInstanceLeft.Instance Is Nothing Then Return
        IsUpdatingUiState = True
        Try
            ChkAutoUpdateAssets.Checked = Settings.Get(Of Boolean)("VersionAdvanceAutoAssetUpdate", Instance:=PageInstanceLeft.Instance)
        Finally
            IsUpdatingUiState = False
        End Try
    End Sub

    Private Sub ChkAutoUpdateAssets_Change() Handles ChkAutoUpdateAssets.Change
        If IsUpdatingUiState OrElse PageInstanceLeft.Instance Is Nothing Then Return
        Settings.Set("VersionAdvanceAutoAssetUpdate", ChkAutoUpdateAssets.Checked, Instance:=PageInstanceLeft.Instance)
        Hint(If(ChkAutoUpdateAssets.Checked, "已开启：启动游戏前将自动更新已知资源包与光影。", "已关闭：启动前自动更新已知资源包与光影。"), HintType.Blue)
    End Sub

    Private Sub BtnRefreshAssets_Click() Handles BtnRefreshAssets.Click
        LoadAllAssetsAsync()
    End Sub

#Region "纯文本与配置解析（可独立单测）"

    Public Shared Function StripMcFormatting(Raw As String) As String
        If String.IsNullOrEmpty(Raw) Then Return ""
        Return Regex.Replace(Raw, "§.", "").Replace(vbCr, " ").Replace(vbLf, " ").Trim()
    End Function

    Public Shared Function ExtractDescriptionFromMcmeta(JsonRaw As String) As (Desc As String, PackFormat As Integer)
        If String.IsNullOrWhiteSpace(JsonRaw) Then Return ("", 0)
        Try
            Dim Root = JObject.Parse(JsonRaw)
            Dim PackObj = TryCast(Root("pack"), JObject)
            If PackObj Is Nothing Then Return ("", 0)
            Dim Fmt As Integer = If(PackObj("pack_format") IsNot Nothing, PackObj("pack_format").ToObject(Of Integer)(), 0)
            Dim DescTok = PackObj("description")
            Dim Desc As String = ExtractTextComponent(DescTok)
            Return (StripMcFormatting(Desc), Fmt)
        Catch
            Return ("", 0)
        End Try
    End Function

    Private Shared Function ExtractTextComponent(Tok As JToken) As String
        If Tok Is Nothing Then Return ""
        If Tok.Type = JTokenType.String Then Return Tok.ToString()
        If Tok.Type = JTokenType.Array Then
            Return String.Join("", Tok.Select(Function(c) ExtractTextComponent(c)))
        End If
        If Tok.Type = JTokenType.Object Then
            Dim Sb As New StringBuilder()
            If Tok("text") IsNot Nothing Then Sb.Append(Tok("text").ToString())
            If Tok("extra") IsNot Nothing Then Sb.Append(ExtractTextComponent(Tok("extra")))
            Return Sb.ToString()
        End If
        Return Tok.ToString()
    End Function

    ''' <summary>解析 options.txt 的 resourcePacks 值，返回从高优先级（游戏最上层）到低优先级的列表。</summary>
    Public Shared Function ParseEnabledResourcePacksLine(RawLineValue As String) As List(Of String)
        Dim Result As New List(Of String)
        Dim Body = If(RawLineValue, "").Trim()
        If Body.StartsWith("[") Then Body = Body.Substring(1)
        If Body.EndsWith("]") Then Body = Body.Substring(0, Body.Length - 1)
        If String.IsNullOrWhiteSpace(Body) Then Return Result
        'options.txt 中末尾项优先级最高，因此逆序转为“UI 顶部=最高优先级”
        Dim RawItems As New List(Of String)
        Dim InQuote As Boolean = False
        Dim Cur As New StringBuilder()
        For Each Ch In Body
            If Ch = """"c Then
                InQuote = Not InQuote
            ElseIf Ch = ","c AndAlso Not InQuote Then
                Dim Item = Cur.ToString().Trim()
                If Item.Length > 0 Then RawItems.Add(Item)
                Cur.Clear()
            Else
                Cur.Append(Ch)
            End If
        Next
        Dim Last = Cur.ToString().Trim()
        If Last.Length > 0 Then RawItems.Add(Last)
        RawItems.Reverse()
        Return RawItems
    End Function

    ''' <summary>将 UI 从高优先级到低优先级的列表格式化回 options.txt 的 resourcePacks:[...] 格式。</summary>
    Public Shared Function FormatEnabledResourcePacksLine(TopToBottom As IEnumerable(Of String)) As String
        Dim List = If(TopToBottom, Enumerable.Empty(Of String)()).Where(Function(s) Not String.IsNullOrWhiteSpace(s)).ToList()
        If Not List.Any(Function(s) s.Equals("vanilla", StringComparison.OrdinalIgnoreCase)) Then
            List.Add("vanilla")
        End If
        List.Reverse()
        Return "[" & String.Join(",", List.Select(Function(s) If(s.Contains(" ") OrElse s.StartsWith("file/", StringComparison.OrdinalIgnoreCase) OrElse s = "vanilla", $"""{s}""", $"""{s}"""))) & "]"
    End Function

    Private Shared Function ReadOptionsValue(OptionsPath As String, Key As String) As String
        If Not File.Exists(OptionsPath) Then Return ""
        For Each Line In File.ReadAllLines(OptionsPath, New UTF8Encoding(False))
            Dim Idx = Line.IndexOf(":"c)
            If Idx <= 0 Then Continue For
            Dim K = Line.Substring(0, Idx).Trim()
            If K.Equals(Key, StringComparison.Ordinal) Then Return Line.Substring(Idx + 1).Trim()
        Next
        Return ""
    End Function

    Private Shared Sub WriteOptionsValue(OptionsPath As String, Key As String, NewVal As String)
        Dim Lines As New List(Of String)
        If File.Exists(OptionsPath) Then
            Lines.AddRange(File.ReadAllLines(OptionsPath, New UTF8Encoding(False)))
        Else
            Directory.CreateDirectory(Path.GetDirectoryName(OptionsPath))
        End If
        Dim Replaced As Boolean = False
        For i As Integer = 0 To Lines.Count - 1
            Dim Idx = Lines(i).IndexOf(":"c)
            If Idx > 0 AndAlso Lines(i).Substring(0, Idx).Trim().Equals(Key, StringComparison.Ordinal) Then
                Lines(i) = $"{Key}:{NewVal}"
                Replaced = True
                Exit For
            End If
        Next
        If Not Replaced Then Lines.Add($"{Key}:{NewVal}")
        File.WriteAllLines(OptionsPath, Lines, New UTF8Encoding(False))
    End Sub

    Private Shared Function ReadKvConfigValue(FilePath As String, Key As String) As String
        If Not File.Exists(FilePath) Then Return ""
        For Each Line In File.ReadAllLines(FilePath, New UTF8Encoding(False))
            Dim Trimmed = Line.Trim()
            If Trimmed.StartsWith("#") OrElse Not Trimmed.Contains("=") Then Continue For
            Dim Idx = Trimmed.IndexOf("="c)
            If Trimmed.Substring(0, Idx).Trim().Equals(Key, StringComparison.OrdinalIgnoreCase) Then
                Return Trimmed.Substring(Idx + 1).Trim()
            End If
        Next
        Return ""
    End Function

    Private Shared Sub WriteKvConfigValues(FilePath As String, Updates As Dictionary(Of String, String))
        Dim Lines As New List(Of String)
        If File.Exists(FilePath) Then
            Lines.AddRange(File.ReadAllLines(FilePath, New UTF8Encoding(False)))
        Else
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath))
        End If
        Dim Written As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For i As Integer = 0 To Lines.Count - 1
            Dim Trimmed = Lines(i).Trim()
            If Trimmed.StartsWith("#") OrElse Not Trimmed.Contains("=") Then Continue For
            Dim Idx = Trimmed.IndexOf("="c)
            Dim K = Trimmed.Substring(0, Idx).Trim()
            Dim MatchedKey = Updates.Keys.FirstOrDefault(Function(x) x.Equals(K, StringComparison.OrdinalIgnoreCase))
            If MatchedKey IsNot Nothing Then
                Lines(i) = $"{MatchedKey}={Updates(MatchedKey)}"
                Written.Add(MatchedKey)
            End If
        Next
        For Each Kvp In Updates
            If Not Written.Contains(Kvp.Key) Then Lines.Add($"{Kvp.Key}={Kvp.Value}")
        Next
        File.WriteAllLines(FilePath, Lines, New UTF8Encoding(False))
    End Sub

#End Region

#Region "扫描加载"

    Public Shared Function ScanInstanceAssetEntries(Instance As McInstance, Optional ReadRpMetadata As Boolean = True) As (RpEntries As List(Of AssetPackEntry), SpEntries As List(Of AssetPackEntry), ActiveShader As String, ShaderModNote As String)
        Dim RpEntries As New List(Of AssetPackEntry)
        Dim SpEntries As New List(Of AssetPackEntry)
        Dim ActiveShader As String = ""
        Dim ShaderModNote As String = ""
        If Instance Is Nothing Then Return (RpEntries, SpEntries, ActiveShader, ShaderModNote)

        Dim IndieRoot As String = Instance.PathIndie.TrimEnd("\"c)
        Dim RpDir = Path.Combine(IndieRoot, "resourcepacks")
        Dim SpDir = Path.Combine(IndieRoot, "shaderpacks")
        Dim OptionsPath = Path.Combine(IndieRoot, "options.txt")
        Dim IrisCfgPath = Path.Combine(IndieRoot, "config", "iris.properties")
        Dim IrisCfgPath2 = Path.Combine(IndieRoot, "config", "oculus.properties")
        Dim OfShadersPath = Path.Combine(IndieRoot, "optionsshaders.txt")

        Dim EnabledIds = ParseEnabledResourcePacksLine(ReadOptionsValue(OptionsPath, "resourcePacks"))
        Dim ActiveMap As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        Dim OrderIdx As Integer = 0
        For Each Id In EnabledIds
            Dim CleanId = If(Id.StartsWith("file/", StringComparison.OrdinalIgnoreCase), Id.Substring(5), Id)
            If Not CleanId.Equals("vanilla", StringComparison.OrdinalIgnoreCase) AndAlso Not ActiveMap.ContainsKey(CleanId) Then
                ActiveMap(CleanId) = OrderIdx
                OrderIdx += 1
            End If
        Next

        If Directory.Exists(RpDir) Then
            For Each FilePath In Directory.GetFiles(RpDir)
                Dim Name = Path.GetFileName(FilePath)
                Dim IsDis = Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                Dim BaseName = If(IsDis, Name.Substring(0, Name.Length - 9), Name)
                If Not BaseName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then Continue For
                If ReadRpMetadata Then
                    RpEntries.Add(BuildRpZipEntry(FilePath, Name, BaseName, IsDis, ActiveMap))
                Else
                    RpEntries.Add(New AssetPackEntry With {
                        .FilePath = FilePath, .FileName = Name, .BaseName = BaseName, .IsFolder = False, .IsShaderPack = False,
                        .IsDiskDisabled = IsDis, .IsActiveInGame = (Not IsDis AndAlso ActiveMap.ContainsKey(BaseName)),
                        .PriorityOrder = If(ActiveMap.ContainsKey(BaseName), ActiveMap(BaseName), Integer.MaxValue),
                        .SizeBytes = New FileInfo(FilePath).Length
                    })
                End If
            Next
            If ReadRpMetadata Then
                For Each SubDir In Directory.GetDirectories(RpDir)
                    Dim Name = Path.GetFileName(SubDir)
                    Dim IsDis = Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    Dim BaseName = If(IsDis, Name.Substring(0, Name.Length - 9), Name)
                    If Not File.Exists(Path.Combine(SubDir, "pack.mcmeta")) AndAlso Not File.Exists(Path.Combine(SubDir, "pack.mcmeta.disabled")) Then Continue For
                    RpEntries.Add(BuildRpFolderEntry(SubDir, Name, BaseName, IsDis, ActiveMap))
                Next
            End If
        End If
        RpEntries = RpEntries.OrderByDescending(Function(r) r.IsActiveInGame).ThenBy(Function(r) If(r.IsActiveInGame, r.PriorityOrder, Integer.MaxValue)).ThenBy(Function(r) r.IsDiskDisabled).ThenBy(Function(r) r.BaseName, StringComparer.OrdinalIgnoreCase).ToList()

        ActiveShader = ReadKvConfigValue(IrisCfgPath, "shaderPack")
        If String.IsNullOrEmpty(ActiveShader) Then ActiveShader = ReadKvConfigValue(IrisCfgPath2, "shaderPack")
        If String.IsNullOrEmpty(ActiveShader) Then ActiveShader = ReadKvConfigValue(OfShadersPath, "shaderPack")
        If ActiveShader.Equals("(off)", StringComparison.OrdinalIgnoreCase) OrElse ActiveShader.Equals("OFF", StringComparison.OrdinalIgnoreCase) Then ActiveShader = ""

        If Directory.Exists(SpDir) Then
            For Each FilePath In Directory.GetFiles(SpDir)
                Dim Name = Path.GetFileName(FilePath)
                Dim IsDis = Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                Dim BaseName = If(IsDis, Name.Substring(0, Name.Length - 9), Name)
                If Not BaseName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then Continue For
                SpEntries.Add(New AssetPackEntry With {
                    .FilePath = FilePath, .FileName = Name, .BaseName = BaseName, .IsFolder = False, .IsShaderPack = True,
                    .IsDiskDisabled = IsDis,
                    .IsActiveInGame = (Not IsDis AndAlso Not String.IsNullOrEmpty(ActiveShader) AndAlso ActiveShader.Equals(BaseName, StringComparison.OrdinalIgnoreCase)),
                    .SizeBytes = New FileInfo(FilePath).Length
                })
            Next
            If ReadRpMetadata Then
                For Each SubDir In Directory.GetDirectories(SpDir)
                    Dim Name = Path.GetFileName(SubDir)
                    Dim IsDis = Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    Dim BaseName = If(IsDis, Name.Substring(0, Name.Length - 9), Name)
                    If Not Directory.Exists(Path.Combine(SubDir, "shaders")) Then Continue For
                    SpEntries.Add(New AssetPackEntry With {
                        .FilePath = SubDir, .FileName = Name, .BaseName = BaseName, .IsFolder = True, .IsShaderPack = True,
                        .IsDiskDisabled = IsDis,
                        .IsActiveInGame = (Not IsDis AndAlso Not String.IsNullOrEmpty(ActiveShader) AndAlso ActiveShader.Equals(BaseName, StringComparison.OrdinalIgnoreCase))
                    })
                Next
            End If
        End If
        SpEntries = SpEntries.OrderByDescending(Function(s) s.IsActiveInGame).ThenBy(Function(s) s.IsDiskDisabled).ThenBy(Function(s) s.BaseName, StringComparer.OrdinalIgnoreCase).ToList()
        If ReadRpMetadata Then ShaderModNote = DetectShaderModSupport(Instance)
        Return (RpEntries, SpEntries, ActiveShader, ShaderModNote)
    End Function

    Private Sub LoadAllAssetsAsync()
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        LoadGen += 1
        Dim CurrentGen As Integer = LoadGen
        LabRpSummary.Text = "正在扫描 resourcepacks..."
        LabSpSummary.Text = "正在扫描 shaderpacks..."
        BtnUpdateAllAssets.Visibility = Visibility.Collapsed

        RunInNewThread(
        Sub()
            Try
                Dim Scan = ScanInstanceAssetEntries(Instance, ReadRpMetadata:=True)
                RunInUi(
                Sub()
                    If CurrentGen <> LoadGen Then Return
                    LastRpEntries = Scan.RpEntries
                    LastSpEntries = Scan.SpEntries
                    LastActiveShader = Scan.ActiveShader
                    LastShaderModNote = Scan.ShaderModNote
                    RenderResourcePacks(LastRpEntries)
                    RenderShaderPacks(LastSpEntries, LastActiveShader, LastShaderModNote)
                    StartCheckAssetUpdatesAsync(CurrentGen, ManualHint:=False)
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "扫描资源包与光影包失败")
                RunInUi(Sub()
                            LabRpSummary.Text = "扫描失败：" & ex.Message
                            LabSpSummary.Text = "扫描失败：" & ex.Message
                        End Sub)
            End Try
        End Sub, "Scan Instance Assets")
    End Sub

    Private Shared Function DetectShaderModSupport(Instance As McInstance) As String
        Try
            Dim HasOptiFine As Boolean = Instance.Version.HasOptiFine
            Dim ModsDir = Instance.PathIndie & "mods\"
            Dim HasIris As Boolean = False
            Dim HasOculus As Boolean = False
            If Directory.Exists(ModsDir) Then
                For Each JarPath In Directory.EnumerateFiles(ModsDir, "*.jar", SearchOption.TopDirectoryOnly)
                    Dim Fn = Path.GetFileName(JarPath).ToLowerInvariant()
                    If Fn.Contains("iris") Then HasIris = True
                    If Fn.Contains("oculus") Then HasOculus = True
                    If Fn.Contains("optifine") Then HasOptiFine = True
                Next
            End If
            Dim Detected As New List(Of String)
            If HasIris Then Detected.Add("Iris")
            If HasOculus Then Detected.Add("Oculus")
            If HasOptiFine Then Detected.Add("OptiFine")
            If Detected.Any() Then
                Return $"已检测到光影支持：{String.Join(" / ", Detected)}（修改将同步写入 Iris / Oculus 与 OptiFine 配置文件）。"
            Else
                Return "未在当前实例检测到 Iris / Oculus / OptiFine；点击「设为当前光影」仍会预先写入配置文件。"
            End If
        Catch
            Return ""
        End Try
    End Function

    Private Shared Function BuildRpZipEntry(FilePath As String, Name As String, BaseName As String, IsDis As Boolean, ActiveMap As Dictionary(Of String, Integer)) As AssetPackEntry
        Dim Entry As New AssetPackEntry With {
            .FilePath = FilePath,
            .FileName = Name,
            .BaseName = BaseName,
            .IsFolder = False,
            .IsDiskDisabled = IsDis,
            .IsActiveInGame = (Not IsDis AndAlso ActiveMap.ContainsKey(BaseName)),
            .PriorityOrder = If(ActiveMap.ContainsKey(BaseName), ActiveMap(BaseName), Integer.MaxValue),
            .SizeBytes = New FileInfo(FilePath).Length
        }
        Try
            Using Arch = ZipFile.OpenRead(FilePath)
                Dim MetaEntry = Arch.GetEntry("pack.mcmeta")
                If MetaEntry IsNot Nothing Then
                    Using Sr As New StreamReader(MetaEntry.Open(), Encoding.UTF8)
                        Dim Parsed = ExtractDescriptionFromMcmeta(Sr.ReadToEnd())
                        Entry.Description = Parsed.Desc
                        Entry.PackFormat = Parsed.PackFormat
                    End Using
                End If
                Dim IconEntry = Arch.GetEntry("pack.png")
                If IconEntry IsNot Nothing Then
                    Using EntryStream = IconEntry.Open(), Ms As New MemoryStream()
                        EntryStream.CopyTo(Ms)
                        Entry.IconImage = DecodeBitmap(Ms.ToArray())
                    End Using
                End If
            End Using
        Catch
        End Try
        Return Entry
    End Function

    Private Shared Function BuildRpFolderEntry(DirPath As String, Name As String, BaseName As String, IsDis As Boolean, ActiveMap As Dictionary(Of String, Integer)) As AssetPackEntry
        Dim Entry As New AssetPackEntry With {
            .FilePath = DirPath,
            .FileName = Name,
            .BaseName = BaseName,
            .IsFolder = True,
            .IsDiskDisabled = IsDis,
            .IsActiveInGame = (Not IsDis AndAlso ActiveMap.ContainsKey(BaseName)),
            .PriorityOrder = If(ActiveMap.ContainsKey(BaseName), ActiveMap(BaseName), Integer.MaxValue)
        }
        Try
            Dim MetaPath = If(File.Exists(Path.Combine(DirPath, "pack.mcmeta")), Path.Combine(DirPath, "pack.mcmeta"), Path.Combine(DirPath, "pack.mcmeta.disabled"))
            If File.Exists(MetaPath) Then
                Dim Parsed = ExtractDescriptionFromMcmeta(File.ReadAllText(MetaPath, Encoding.UTF8))
                Entry.Description = Parsed.Desc
                Entry.PackFormat = Parsed.PackFormat
            End If
            Dim IconPath = Path.Combine(DirPath, "pack.png")
            If File.Exists(IconPath) Then
                Entry.IconImage = DecodeBitmap(File.ReadAllBytes(IconPath))
            End If
        Catch
        End Try
        Return Entry
    End Function

    Private Shared Function DecodeBitmap(Bytes As Byte()) As BitmapImage
        If Bytes Is Nothing OrElse Bytes.Length = 0 Then Return Nothing
        Dim Bmp As New BitmapImage()
        Using Ms As New MemoryStream(Bytes)
            Bmp.BeginInit()
            Bmp.CacheOption = BitmapCacheOption.OnLoad
            Bmp.DecodePixelWidth = 64
            Bmp.StreamSource = Ms
            Bmp.EndInit()
            Bmp.Freeze()
        End Using
        Return Bmp
    End Function

#End Region

#Region "渲染与操作"

    Private Sub RenderResourcePacks(Entries As List(Of AssetPackEntry))
        PanRpList.Children.Clear()
        Dim ActiveCount = Entries.Where(Function(r) r.IsActiveInGame).Count()
        LabRpSummary.Text = $"共 {Entries.Count} 个资源包（已加载启用 {ActiveCount} 个，越靠上优先级越高）"
        HintNoRp.Visibility = If(Entries.Count = 0, Visibility.Visible, Visibility.Collapsed)

        Dim ActiveList = Entries.Where(Function(r) r.IsActiveInGame).ToList()
        For Each Pack In Entries
            Dim LocalPack = Pack
            Dim Row As New Border With {
                .CornerRadius = New CornerRadius(6),
                .Padding = New Thickness(10, 8, 10, 8),
                .Margin = New Thickness(0, 0, 0, 8),
                .BorderThickness = New Thickness(1),
                .BorderBrush = New SolidColorBrush(If(LocalPack.IsActiveInGame, Color.FromArgb(90, 16, 185, 129), Color.FromArgb(30, 0, 0, 0))),
                .Background = New SolidColorBrush(If(LocalPack.IsDiskDisabled, Color.FromArgb(10, 0, 0, 0), If(LocalPack.IsActiveInGame, Color.FromArgb(14, 16, 185, 129), Colors.Transparent)))
            }
            Dim Grid As New Grid()
            Grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(46)})
            Grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim IconBox As New Border With {.Width = 40, .Height = 40, .CornerRadius = New CornerRadius(4), .ClipToBounds = True, .Background = New SolidColorBrush(Color.FromArgb(25, 0, 0, 0)), .HorizontalAlignment = HorizontalAlignment.Left}
            If LocalPack.IconImage IsNot Nothing Then
                Dim Img As New Image With {.Source = LocalPack.IconImage, .Stretch = Stretch.UniformToFill}
                RenderOptions.SetBitmapScalingMode(Img, BitmapScalingMode.NearestNeighbor)
                IconBox.Child = Img
            End If
            Grid.SetColumn(IconBox, 0)
            Grid.Children.Add(IconBox)

            Dim InfoSp As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(6, 0, 8, 0)}
            Dim StatusTag As String = If(LocalPack.IsDiskDisabled, " [文件已禁用]", If(LocalPack.IsActiveInGame, $" [已加载 · 优先级 #{LocalPack.PriorityOrder + 1}]", " [未加载]"))
            Dim TitleTb As New TextBlock With {
                .Text = LocalPack.BaseName & StatusTag,
                .FontWeight = FontWeights.SemiBold,
                .TextTrimming = TextTrimming.CharacterEllipsis
            }
            Dim SubParts As New List(Of String)
            If Not String.IsNullOrEmpty(LocalPack.SourcePlatform) Then
                SubParts.Add($"已知 ({LocalPack.SourcePlatform}" & If(String.IsNullOrEmpty(LocalPack.RemoteProjectName), "", $": {LocalPack.RemoteProjectName}") & ")")
            End If
            If LocalPack.PackFormat > 0 Then SubParts.Add($"pack_format {LocalPack.PackFormat}")
            If Not LocalPack.IsFolder AndAlso LocalPack.SizeBytes > 0 Then SubParts.Add($"{LocalPack.SizeBytes / 1048576.0:0.0} MB")
            If LocalPack.IsFolder Then SubParts.Add("文件夹")
            If LocalPack.UpdateVersion IsNot Nothing Then SubParts.Add($"可更新 → {LocalPack.UpdateVersion.FileName}")
            If Not String.IsNullOrWhiteSpace(LocalPack.Description) Then SubParts.Add(LocalPack.Description)
            Dim SubTb As New TextBlock With {
                .Text = If(SubParts.Any(), String.Join(" · ", SubParts), "无描述信息"),
                .FontSize = 11.5,
                .TextTrimming = TextTrimming.CharacterEllipsis,
                .Foreground = FindResource(If(LocalPack.UpdateVersion IsNot Nothing, "ColorBrush3", "ColorBrush2"))
            }
            InfoSp.Children.Add(TitleTb)
            InfoSp.Children.Add(SubTb)
            Grid.SetColumn(InfoSp, 1)
            Grid.Children.Add(InfoSp)

            Dim Actions As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
            If LocalPack.UpdateVersion IsNot Nothing Then
                Dim BtnUpd As New MyButton With {.Text = "更新", .Height = 26, .Padding = New Thickness(9, 0, 9, 0), .Margin = New Thickness(0, 0, 4, 0), .ColorType = MyButton.ColorState.Highlight}
                AddHandler BtnUpd.Click, Sub() RunSingleAssetUpdate(LocalPack)
                Actions.Children.Add(BtnUpd)
            End If
            If LocalPack.IsActiveInGame Then
                Dim IdxInActive = ActiveList.IndexOf(LocalPack)
                If IdxInActive > 0 Then
                    Dim BtnUp As New MyButton With {.Text = "上移", .Height = 26, .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 4, 0)}
                    AddHandler BtnUp.Click, Sub() MoveResourcePackPriority(LocalPack, -1)
                    Actions.Children.Add(BtnUp)
                End If
                If IdxInActive >= 0 AndAlso IdxInActive < ActiveList.Count - 1 Then
                    Dim BtnDown As New MyButton With {.Text = "下移", .Height = 26, .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 4, 0)}
                    AddHandler BtnDown.Click, Sub() MoveResourcePackPriority(LocalPack, 1)
                    Actions.Children.Add(BtnDown)
                End If
            End If
            Dim BtnToggleGame As New MyButton With {
                .Text = If(LocalPack.IsActiveInGame, "取消加载", "启用加载"),
                .Height = 26,
                .Padding = New Thickness(9, 0, 9, 0),
                .Margin = New Thickness(0, 0, 4, 0),
                .ColorType = If(LocalPack.IsActiveInGame, MyButton.ColorState.Normal, MyButton.ColorState.Highlight)
            }
            AddHandler BtnToggleGame.Click, Sub() ToggleResourcePackInOptions(LocalPack)
            Actions.Children.Add(BtnToggleGame)

            Dim BtnDisk As New MyButton With {.Text = If(LocalPack.IsDiskDisabled, "恢复扩展名", "禁用文件"), .Height = 26, .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 4, 0)}
            AddHandler BtnDisk.Click, Sub() TogglePackDiskDisabled(LocalPack, True)
            Actions.Children.Add(BtnDisk)

            Dim BtnDel As New MyButton With {.Text = "删除", .Height = 26, .Padding = New Thickness(8, 0, 8, 0), .ColorType = MyButton.ColorState.Red}
            AddHandler BtnDel.Click, Sub() DeleteAssetPack(LocalPack)
            Actions.Children.Add(BtnDel)

            Grid.SetColumn(Actions, 2)
            Grid.Children.Add(Actions)
            Row.Child = Grid
            PanRpList.Children.Add(Row)
        Next
    End Sub

    Private Sub RenderShaderPacks(Entries As List(Of AssetPackEntry), ActiveShader As String, ShaderModNote As String)
        PanSpList.Children.Clear()
        HintShaderModStatus.Text = ShaderModNote
        HintShaderModStatus.Visibility = If(String.IsNullOrWhiteSpace(ShaderModNote), Visibility.Collapsed, Visibility.Visible)
        LabSpSummary.Text = If(String.IsNullOrEmpty(ActiveShader), $"共 {Entries.Count} 个光影包（当前未启用光影）", $"共 {Entries.Count} 个光影包（当前启用：{ActiveShader}）")
        HintNoSp.Visibility = If(Entries.Count = 0, Visibility.Visible, Visibility.Collapsed)

        For Each Pack In Entries
            Dim LocalPack = Pack
            Dim Row As New Border With {
                .CornerRadius = New CornerRadius(6),
                .Padding = New Thickness(10, 8, 10, 8),
                .Margin = New Thickness(0, 0, 0, 8),
                .BorderThickness = New Thickness(1),
                .BorderBrush = New SolidColorBrush(If(LocalPack.IsActiveInGame, Color.FromArgb(100, 99, 102, 241), Color.FromArgb(30, 0, 0, 0))),
                .Background = New SolidColorBrush(If(LocalPack.IsDiskDisabled, Color.FromArgb(10, 0, 0, 0), If(LocalPack.IsActiveInGame, Color.FromArgb(14, 99, 102, 241), Colors.Transparent)))
            }
            Dim Grid As New Grid()
            Grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            Grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim InfoSp As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(0, 0, 8, 0)}
            Dim StatusTag = If(LocalPack.IsDiskDisabled, " [已禁用]", If(LocalPack.IsActiveInGame, " [当前使用中]", ""))
            InfoSp.Children.Add(New TextBlock With {
                .Text = LocalPack.BaseName & StatusTag,
                .FontWeight = FontWeights.SemiBold,
                .TextTrimming = TextTrimming.CharacterEllipsis
            })
            Dim SpMeta As New List(Of String)
            If Not String.IsNullOrEmpty(LocalPack.SourcePlatform) Then
                SpMeta.Add($"已知 ({LocalPack.SourcePlatform}" & If(String.IsNullOrEmpty(LocalPack.RemoteProjectName), "", $": {LocalPack.RemoteProjectName}") & ")")
            End If
            SpMeta.Add(If(LocalPack.IsFolder, "解压文件夹光影包", $"压缩包 · {LocalPack.SizeBytes / 1048576.0:0.0} MB"))
            If LocalPack.UpdateVersion IsNot Nothing Then SpMeta.Add($"可更新 → {LocalPack.UpdateVersion.FileName}")
            InfoSp.Children.Add(New TextBlock With {
                .Text = String.Join(" · ", SpMeta),
                .FontSize = 11.5,
                .TextTrimming = TextTrimming.CharacterEllipsis,
                .Foreground = FindResource(If(LocalPack.UpdateVersion IsNot Nothing, "ColorBrush3", "ColorBrush2"))
            })
            Grid.SetColumn(InfoSp, 0)
            Grid.Children.Add(InfoSp)

            Dim Actions As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
            If LocalPack.UpdateVersion IsNot Nothing Then
                Dim BtnUpd As New MyButton With {.Text = "更新", .Height = 26, .Padding = New Thickness(9, 0, 9, 0), .Margin = New Thickness(0, 0, 4, 0), .ColorType = MyButton.ColorState.Highlight}
                AddHandler BtnUpd.Click, Sub() RunSingleAssetUpdate(LocalPack)
                Actions.Children.Add(BtnUpd)
            End If
            If Not LocalPack.IsActiveInGame Then
                Dim BtnUse As New MyButton With {.Text = "设为当前光影", .Height = 26, .Padding = New Thickness(10, 0, 10, 0), .Margin = New Thickness(0, 0, 4, 0), .ColorType = MyButton.ColorState.Highlight}
                AddHandler BtnUse.Click, Sub() SetActiveShaderPack(LocalPack)
                Actions.Children.Add(BtnUse)
            End If
            Dim BtnDisk As New MyButton With {.Text = If(LocalPack.IsDiskDisabled, "恢复扩展名", "禁用"), .Height = 26, .Padding = New Thickness(8, 0, 8, 0), .Margin = New Thickness(0, 0, 4, 0)}
            AddHandler BtnDisk.Click, Sub() TogglePackDiskDisabled(LocalPack, False)
            Actions.Children.Add(BtnDisk)
            Dim BtnDel As New MyButton With {.Text = "删除", .Height = 26, .Padding = New Thickness(8, 0, 8, 0), .ColorType = MyButton.ColorState.Red}
            AddHandler BtnDel.Click, Sub() DeleteAssetPack(LocalPack)
            Actions.Children.Add(BtnDel)

            Grid.SetColumn(Actions, 1)
            Grid.Children.Add(Actions)
            Row.Child = Grid
            PanSpList.Children.Add(Row)
        Next
    End Sub

    Private Sub ToggleResourcePackInOptions(Pack As AssetPackEntry)
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Try
            If Pack.IsDiskDisabled Then
                Dim TargetPath = EnablePackFileOnDisk(Pack)
                If String.IsNullOrEmpty(TargetPath) Then Return
            End If
            Dim OptionsPath = Path.Combine(Instance.PathIndie.TrimEnd("\"c), "options.txt")
            Dim Current = ParseEnabledResourcePacksLine(ReadOptionsValue(OptionsPath, "resourcePacks"))
            Dim PackKey = "file/" & Pack.BaseName
            If Pack.IsActiveInGame Then
                Current = Current.Where(Function(x) Not x.Equals(PackKey, StringComparison.OrdinalIgnoreCase) AndAlso Not x.Equals(Pack.BaseName, StringComparison.OrdinalIgnoreCase)).ToList()
            Else
                Current = Current.Where(Function(x) Not x.Equals(PackKey, StringComparison.OrdinalIgnoreCase) AndAlso Not x.Equals(Pack.BaseName, StringComparison.OrdinalIgnoreCase)).ToList()
                Current.Insert(0, PackKey)
            End If
            WriteOptionsValue(OptionsPath, "resourcePacks", FormatEnabledResourcePacksLine(Current))
            Hint(If(Pack.IsActiveInGame, $"已取消加载资源包 {Pack.BaseName}", $"已启用资源包 {Pack.BaseName}（置于最高优先级）"), HintType.Green)
            LoadAllAssetsAsync()
        Catch ex As Exception
            Hint($"更新 options.txt 失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub MoveResourcePackPriority(Pack As AssetPackEntry, Delta As Integer)
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Try
            Dim OptionsPath = Path.Combine(Instance.PathIndie.TrimEnd("\"c), "options.txt")
            Dim Current = ParseEnabledResourcePacksLine(ReadOptionsValue(OptionsPath, "resourcePacks"))
            Dim PackKey = "file/" & Pack.BaseName
            Dim FileItems = Current.Where(Function(x) Not x.Equals("vanilla", StringComparison.OrdinalIgnoreCase)).ToList()
            Dim Idx = FileItems.FindIndex(Function(x) x.Equals(PackKey, StringComparison.OrdinalIgnoreCase) OrElse x.Equals(Pack.BaseName, StringComparison.OrdinalIgnoreCase))
            If Idx < 0 Then Return
            Dim TargetIdx = Math.Max(0, Math.Min(FileItems.Count - 1, Idx + Delta))
            If TargetIdx = Idx Then Return
            Dim Moving = FileItems(Idx)
            FileItems.RemoveAt(Idx)
            FileItems.Insert(TargetIdx, Moving)
            If Current.Any(Function(x) x.Equals("vanilla", StringComparison.OrdinalIgnoreCase)) Then FileItems.Add("vanilla")
            WriteOptionsValue(OptionsPath, "resourcePacks", FormatEnabledResourcePacksLine(FileItems))
            LoadAllAssetsAsync()
        Catch ex As Exception
            Hint($"调整优先级失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Function EnablePackFileOnDisk(Pack As AssetPackEntry) As String
        If Not Pack.IsDiskDisabled Then Return Pack.FilePath
        Dim Parent = Path.GetDirectoryName(Pack.FilePath)
        Dim NewPath = Path.Combine(Parent, Pack.BaseName)
        If File.Exists(NewPath) OrElse Directory.Exists(NewPath) Then
            Hint($"目标名称 {Pack.BaseName} 已存在，请先重命名或删除冲突文件。", HintType.Red)
            Return Nothing
        End If
        If Pack.IsFolder Then
            Directory.Move(Pack.FilePath, NewPath)
        Else
            File.Move(Pack.FilePath, NewPath)
        End If
        Return NewPath
    End Function

    Private Sub TogglePackDiskDisabled(Pack As AssetPackEntry, IsResourcePack As Boolean)
        Try
            If Pack.IsDiskDisabled Then
                EnablePackFileOnDisk(Pack)
                Hint($"已恢复：{Pack.BaseName}", HintType.Green)
            Else
                Dim NewPath = Pack.FilePath & ".disabled"
                If File.Exists(NewPath) OrElse Directory.Exists(NewPath) Then
                    Hint($"目标禁用名称已存在，无法重命名！", HintType.Red)
                    Return
                End If
                If Pack.IsFolder Then
                    Directory.Move(Pack.FilePath, NewPath)
                Else
                    File.Move(Pack.FilePath, NewPath)
                End If
                If IsResourcePack AndAlso Pack.IsActiveInGame AndAlso PageInstanceLeft.Instance IsNot Nothing Then
                    Dim OptionsPath = Path.Combine(PageInstanceLeft.Instance.PathIndie.TrimEnd("\"c), "options.txt")
                    Dim Cur = ParseEnabledResourcePacksLine(ReadOptionsValue(OptionsPath, "resourcePacks")).Where(Function(x) Not x.Equals("file/" & Pack.BaseName, StringComparison.OrdinalIgnoreCase) AndAlso Not x.Equals(Pack.BaseName, StringComparison.OrdinalIgnoreCase)).ToList()
                    WriteOptionsValue(OptionsPath, "resourcePacks", FormatEnabledResourcePacksLine(Cur))
                End If
                Hint($"已禁用文件：{Pack.BaseName}", HintType.Blue)
            End If
            LoadAllAssetsAsync()
        Catch ex As Exception
            Hint($"切换文件状态失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub SetActiveShaderPack(Pack As AssetPackEntry)
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Try
            If Pack.IsDiskDisabled Then
                If String.IsNullOrEmpty(EnablePackFileOnDisk(Pack)) Then Return
            End If
            Dim IndieRoot = Instance.PathIndie.TrimEnd("\"c)
            WriteKvConfigValues(Path.Combine(IndieRoot, "config", "iris.properties"), New Dictionary(Of String, String) From {
                {"shaderPack", Pack.BaseName},
                {"enableShaders", "true"}
            })
            If File.Exists(Path.Combine(IndieRoot, "config", "oculus.properties")) Then
                WriteKvConfigValues(Path.Combine(IndieRoot, "config", "oculus.properties"), New Dictionary(Of String, String) From {
                    {"shaderPack", Pack.BaseName},
                    {"enableShaders", "true"}
                })
            End If
            WriteKvConfigValues(Path.Combine(IndieRoot, "optionsshaders.txt"), New Dictionary(Of String, String) From {
                {"shaderPack", Pack.BaseName}
            })
            Hint($"已将当前光影设为：{Pack.BaseName}", HintType.Green)
            LoadAllAssetsAsync()
        Catch ex As Exception
            Hint($"设置光影配置失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnDisableActiveSp_Click() Handles BtnDisableActiveSp.Click
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Try
            Dim IndieRoot = Instance.PathIndie.TrimEnd("\"c)
            If File.Exists(Path.Combine(IndieRoot, "config", "iris.properties")) Then
                WriteKvConfigValues(Path.Combine(IndieRoot, "config", "iris.properties"), New Dictionary(Of String, String) From {{"enableShaders", "false"}})
            End If
            If File.Exists(Path.Combine(IndieRoot, "config", "oculus.properties")) Then
                WriteKvConfigValues(Path.Combine(IndieRoot, "config", "oculus.properties"), New Dictionary(Of String, String) From {{"enableShaders", "false"}})
            End If
            If File.Exists(Path.Combine(IndieRoot, "optionsshaders.txt")) Then
                WriteKvConfigValues(Path.Combine(IndieRoot, "optionsshaders.txt"), New Dictionary(Of String, String) From {{"shaderPack", "(off)"}})
            End If
            Hint("已关闭当前实例的启用光影。", HintType.Blue)
            LoadAllAssetsAsync()
        Catch ex As Exception
            Hint($"关闭光影失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub DeleteAssetPack(Pack As AssetPackEntry)
        If MyMsgBox($"确定要将「{Pack.FileName}」移至系统回收站吗？", "删除确认", "移至回收站", "取消", IsWarn:=True) <> 1 Then Return
        Try
            If Pack.IsFolder Then
                My.Computer.FileSystem.DeleteDirectory(Pack.FilePath, FileIO.UIOption.OnlyErrorDialogs, FileIO.RecycleOption.SendToRecycleBin)
            Else
                My.Computer.FileSystem.DeleteFile(Pack.FilePath, FileIO.UIOption.OnlyErrorDialogs, FileIO.RecycleOption.SendToRecycleBin)
            End If
            Hint($"已将 {Pack.FileName} 移至回收站。", HintType.Green)
            LoadAllAssetsAsync()
        Catch ex As Exception
            Hint($"删除失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnImportRp_Click() Handles BtnImportRp.Click
        ImportZipAsset("resourcepacks", "选择要导入的资源包 (.zip)", "资源包文件 (*.zip)|*.zip")
    End Sub

    Private Sub BtnImportSp_Click() Handles BtnImportSp.Click
        ImportZipAsset("shaderpacks", "选择要导入的光影包 (.zip)", "光影包文件 (*.zip)|*.zip")
    End Sub

    Private Sub ImportZipAsset(SubFolder As String, Title As String, Filter As String)
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim Files = Dialogs.SelectFile(Title, True, filter:=Filter)
        If Files Is Nothing OrElse Not Files.Any() Then Return
        Dim TargetDir = Path.Combine(Instance.PathIndie.TrimEnd("\"c), SubFolder)
        Directory.CreateDirectory(TargetDir)
        Dim Imported As Integer = 0
        For Each Src In Files
            If Not File.Exists(Src) Then Continue For
            Dim Dest = Path.Combine(TargetDir, Path.GetFileName(Src))
            File.Copy(Src, Dest, True)
            Imported += 1
        Next
        Hint($"已导入 {Imported} 个文件到 {SubFolder}。", HintType.Green)
        LoadAllAssetsAsync()
    End Sub

    Private Sub BtnOpenRpDir_Click() Handles BtnOpenRpDir.Click
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim Dir = Instance.PathIndie & "resourcepacks\"
        Directory.CreateDirectory(Dir)
        OpenExplorer(Dir)
    End Sub

    Private Sub BtnOpenSpDir_Click() Handles BtnOpenSpDir.Click
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim Dir = Instance.PathIndie & "shaderpacks\"
        Directory.CreateDirectory(Dir)
        OpenExplorer(Dir)
    End Sub

#End Region

#Region "已知资源自动检查与更新"

    Public Shared Sub UpdateAssetConfigReferences(IndieRoot As String, IsShaderPack As Boolean, OldBaseName As String, NewBaseName As String)
        If String.IsNullOrWhiteSpace(IndieRoot) OrElse String.IsNullOrWhiteSpace(OldBaseName) OrElse String.IsNullOrWhiteSpace(NewBaseName) Then Return
        If OldBaseName.Equals(NewBaseName, StringComparison.OrdinalIgnoreCase) Then Return
        Dim Root = IndieRoot.TrimEnd("\"c)
        If Not IsShaderPack Then
            Dim OptionsPath = Path.Combine(Root, "options.txt")
            If Not File.Exists(OptionsPath) Then Return
            Dim RawVal = ReadOptionsValue(OptionsPath, "resourcePacks")
            If String.IsNullOrWhiteSpace(RawVal) Then Return
            Dim TopToBottom = ParseEnabledResourcePacksLine(RawVal)
            Dim Changed As Boolean = False
            For i As Integer = 0 To TopToBottom.Count - 1
                Dim Cur = TopToBottom(i)
                Dim Clean = If(Cur.StartsWith("file/", StringComparison.OrdinalIgnoreCase), Cur.Substring(5), Cur)
                If Clean.Equals(OldBaseName, StringComparison.OrdinalIgnoreCase) Then
                    TopToBottom(i) = "file/" & NewBaseName
                    Changed = True
                End If
            Next
            If Changed Then WriteOptionsValue(OptionsPath, "resourcePacks", FormatEnabledResourcePacksLine(TopToBottom))
        Else
            Dim ConfigFiles = {
                Path.Combine(Root, "config", "iris.properties"),
                Path.Combine(Root, "config", "oculus.properties"),
                Path.Combine(Root, "optionsshaders.txt")
            }
            For Each CfgPath In ConfigFiles
                If Not File.Exists(CfgPath) Then Continue For
                Dim Cur = ReadKvConfigValue(CfgPath, "shaderPack")
                If Cur.Equals(OldBaseName, StringComparison.OrdinalIgnoreCase) Then
                    WriteKvConfigValues(CfgPath, New Dictionary(Of String, String) From {{"shaderPack", NewBaseName}})
                End If
            Next
        End If
    End Sub

    Private Shared Function GetMinorSeriesVersion(VanillaVersion As String) As String
        If String.IsNullOrWhiteSpace(VanillaVersion) Then Return ""
        Dim Parts = VanillaVersion.Split("."c)
        If Parts.Length >= 3 AndAlso Parts(0).All(AddressOf Char.IsDigit) AndAlso Parts(1).All(AddressOf Char.IsDigit) Then
            Return $"{Parts(0)}.{Parts(1)}"
        End If
        Return ""
    End Function

    Public Shared Function CheckKnownAssetUpdates(Instance As McInstance, RpEntries As IEnumerable(Of AssetPackEntry), SpEntries As IEnumerable(Of AssetPackEntry), Optional IncludeDisabled As Boolean = True) As List(Of AssetPackEntry)
        Dim Updatable As New List(Of AssetPackEntry)
        If Instance Is Nothing Then Return Updatable
        If Not Instance.IsLoaded Then Instance.Load()
        Dim VanillaVersion = If(Instance.Version?.VanillaName, "")
        If String.IsNullOrWhiteSpace(VanillaVersion) Then Return Updatable
        Dim MinorFallback = GetMinorSeriesVersion(VanillaVersion)

        Dim Candidates As New List(Of (Asset As AssetPackEntry, LocalRes As LocalResourceFile, ExpectedType As ResourceTypes))
        For Each E In If(RpEntries, Enumerable.Empty(Of AssetPackEntry)())
            If Not E.IsFolder AndAlso (IncludeDisabled OrElse Not E.IsDiskDisabled) AndAlso File.Exists(E.FilePath) Then
                E.IsShaderPack = False
                E.UpdateVersion = Nothing
                Candidates.Add((E, New LocalResourceFile(E.FilePath), ResourceTypes.ResourcePack))
            End If
        Next
        For Each E In If(SpEntries, Enumerable.Empty(Of AssetPackEntry)())
            If Not E.IsFolder AndAlso (IncludeDisabled OrElse Not E.IsDiskDisabled) AndAlso File.Exists(E.FilePath) Then
                E.IsShaderPack = True
                E.UpdateVersion = Nothing
                Candidates.Add((E, New LocalResourceFile(E.FilePath), ResourceTypes.Shader))
            End If
        Next
        If Not Candidates.Any() Then Return Updatable

        '1. Modrinth SHA1 匹配与更新查询
        Try
            Dim Sha1Map As New Dictionary(Of String, List(Of (Asset As AssetPackEntry, LocalRes As LocalResourceFile, ExpectedType As ResourceTypes)))(StringComparer.OrdinalIgnoreCase)
            For Each C In Candidates
                Dim H = C.LocalRes.ModrinthHash
                If String.IsNullOrWhiteSpace(H) Then Continue For
                If Not Sha1Map.ContainsKey(H) Then Sha1Map(H) = New List(Of (AssetPackEntry, LocalResourceFile, ResourceTypes))()
                Sha1Map(H).Add(C)
            Next
            If Sha1Map.Any() Then
                Dim VerResp = CType(DlModRequest("https://api.modrinth.com/v2/version_files", HttpMethod.Post,
                    $"{{""hashes"": [""{Sha1Map.Keys.Join(""",""")}""], ""algorithm"": ""sha1""}}", "application/json"), JObject)
                If VerResp IsNot Nothing AndAlso VerResp.Count > 0 Then
                    Dim ProjIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                    Dim Matched As New List(Of (Sha1 As String, Asset As AssetPackEntry, ExpectedType As ResourceTypes, CurVer As ResourceVersion, OrigLoaders As List(Of String), TargetGvs As List(Of String)))
                    For Each Kvp In VerResp
                        Dim VerJson = TryCast(Kvp.Value, JObject)
                        If VerJson Is Nothing OrElse Not Sha1Map.ContainsKey(Kvp.Key) Then Continue For
                        Dim ProjectId = VerJson("project_id")?.ToString()
                        If Not String.IsNullOrWhiteSpace(ProjectId) Then ProjIds.Add(ProjectId)
                        Dim Loaders = If(TryCast(VerJson("loaders"), JArray), New JArray()).Select(Function(x) x.ToString().Lower()).Distinct().OrderBy(Function(x) x).ToList()
                        Dim Gvs = If(TryCast(VerJson("game_versions"), JArray), New JArray()).Select(Function(x) x.ToString().Lower()).ToList()
                        Dim TargetGvs As New List(Of String) From {VanillaVersion}
                        If MinorFallback <> "" AndAlso Gvs.Contains(MinorFallback.Lower()) AndAlso Not Gvs.Contains(VanillaVersion.Lower()) Then
                            TargetGvs.Add(MinorFallback)
                        End If
                        For Each EntryTuple In Sha1Map(Kvp.Key)
                            Dim CurVer = ResourceVersion.FromPlatformJson(VerJson, EntryTuple.ExpectedType)
                            EntryTuple.Asset.SourcePlatform = "Modrinth"
                            EntryTuple.Asset.CurrentVersionText = CurVer.Version
                            Matched.Add((Kvp.Key, EntryTuple.Asset, EntryTuple.ExpectedType, CurVer, Loaders, TargetGvs))
                        Next
                    Next
                    If ProjIds.Any() Then
                        Try
                            Dim ProjArr = CType(DlModRequest($"https://api.modrinth.com/v2/projects?ids=[""{ProjIds.Join(""",""")}""]"), JArray)
                            Dim TitleByProj As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                            For Each P In ProjArr
                                Dim Id = P("id")?.ToString()
                                If Id IsNot Nothing Then TitleByProj(Id) = If(P("title")?.ToString(), "")
                            Next
                            For Each Kvp In VerResp
                                Dim VerJson = TryCast(Kvp.Value, JObject)
                                Dim Pid = VerJson?("project_id")?.ToString()
                                If Pid IsNot Nothing AndAlso TitleByProj.ContainsKey(Pid) AndAlso Sha1Map.ContainsKey(Kvp.Key) Then
                                    For Each EntryTuple In Sha1Map(Kvp.Key)
                                        EntryTuple.Asset.RemoteProjectName = TitleByProj(Pid)
                                    Next
                                End If
                            Next
                        Catch
                        End Try
                    End If
                    If Matched.Any() Then
                        Dim ReqItems = Matched.Select(Function(m)
                                                          Dim LoadersJson = If(m.OrigLoaders.Any(), """" & m.OrigLoaders.Join(""",""") & """", "")
                                                          Dim GvJson = """" & m.TargetGvs.Join(""",""") & """"
                                                          Return $"{{""hash"": ""{m.Sha1}"", ""loaders"": [{LoadersJson}], ""game_versions"": [{GvJson}]}}"
                                                      End Function).Distinct().ToList()
                        Dim UpdResp = CType(DlModRequest("https://api.modrinth.com/v2/version_files/update_individual", HttpMethod.Post,
                            $"{{""algorithm"": ""sha1"", ""hashes"": [{ReqItems.Join(",")}]}}", "application/json"), JObject)
                        If UpdResp IsNot Nothing Then
                            For Each M In Matched
                                If Not UpdResp.ContainsKey(M.Sha1) Then Continue For
                                Dim UpdJson = TryCast(UpdResp(M.Sha1), JObject)
                                If UpdJson Is Nothing Then Continue For
                                Dim UpdVer = ResourceVersion.FromPlatformJson(UpdJson, M.ExpectedType)
                                If Not UpdVer.DownloadAvailable Then Continue For
                                If M.CurVer.ReleaseDate >= UpdVer.ReleaseDate OrElse String.Equals(M.CurVer.Hash, UpdVer.Hash, StringComparison.OrdinalIgnoreCase) Then Continue For
                                Dim UpdLoaders = If(TryCast(UpdJson("loaders"), JArray), New JArray()).Select(Function(x) x.ToString().Lower()).Distinct().ToList()
                                If M.OrigLoaders.Any() AndAlso Not M.OrigLoaders.All(Function(l) UpdLoaders.Contains(l)) Then Continue For
                                M.Asset.UpdateVersion = UpdVer
                            Next
                        End If
                    End If
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "从 Modrinth 检查材质/光影更新失败")
        End Try

        '2. CurseForge Fingerprint 匹配与更新查询
        Try
            Dim HashMap As New Dictionary(Of UInteger, List(Of (Asset As AssetPackEntry, LocalRes As LocalResourceFile, ExpectedType As ResourceTypes)))
            For Each C In Candidates
                If C.Asset.UpdateVersion IsNot Nothing Then Continue For
                Dim H = C.LocalRes.CurseForgeHash
                If Not HashMap.ContainsKey(H) Then HashMap(H) = New List(Of (AssetPackEntry, LocalResourceFile, ResourceTypes))()
                HashMap(H).Add(C)
            Next
            If HashMap.Any() Then
                Dim Matches = TryCast(DlModRequest("https://api.curseforge.com/v1/fingerprints/432", HttpMethod.Post,
                    $"{{""fingerprints"": [{HashMap.Keys.Join(",")}]}}", "application/json")?("data")?("exactMatches"), JContainer)
                If Matches IsNot Nothing AndAlso Matches.Any() Then
                    Dim ProjIdMap As New Dictionary(Of Integer, List(Of (Asset As AssetPackEntry, ExpectedType As ResourceTypes, CurVer As ResourceVersion)))
                    For Each MatchObj In Matches
                        Dim ProjId = MatchObj("id").ToObject(Of Integer)()
                        Dim FileObj = TryCast(MatchObj("file"), JObject)
                        If FileObj Is Nothing Then Continue For
                        Dim Fp = FileObj("fileFingerprint").ToObject(Of UInteger)()
                        If Not HashMap.ContainsKey(Fp) Then Continue For
                        For Each EntryTuple In HashMap(Fp)
                            Dim CurVer = ResourceVersion.FromPlatformJson(FileObj, EntryTuple.ExpectedType)
                            If String.IsNullOrEmpty(EntryTuple.Asset.SourcePlatform) Then EntryTuple.Asset.SourcePlatform = "CurseForge"
                            If Not ProjIdMap.ContainsKey(ProjId) Then ProjIdMap(ProjId) = New List(Of (AssetPackEntry, ResourceTypes, ResourceVersion))()
                            ProjIdMap(ProjId).Add((EntryTuple.Asset, EntryTuple.ExpectedType, CurVer))
                        Next
                    Next
                    If ProjIdMap.Any() Then
                        Dim ModsArr = TryCast(DlModRequest("https://api.curseforge.com/v1/mods", HttpMethod.Post,
                            $"{{""modIds"": [{ProjIdMap.Keys.Join(",")}]}}", "application/json")?("data"), JContainer)
                        Dim UpdateFileIds As New Dictionary(Of Integer, List(Of (Asset As AssetPackEntry, ExpectedType As ResourceTypes, CurVer As ResourceVersion)))
                        If ModsArr IsNot Nothing Then
                            For Each ModJson In ModsArr
                                If ModJson("isAvailable") IsNot Nothing AndAlso Not ModJson("isAvailable").ToObject(Of Boolean)() Then Continue For
                                Dim ProjId = ModJson("id").ToObject(Of Integer)()
                                Dim ProjName = If(ModJson("name")?.ToString(), "")
                                If Not ProjIdMap.ContainsKey(ProjId) Then Continue For
                                For Each Item In ProjIdMap(ProjId)
                                    If String.IsNullOrEmpty(Item.Asset.RemoteProjectName) AndAlso Not String.IsNullOrEmpty(ProjName) Then Item.Asset.RemoteProjectName = ProjName
                                    Dim IdxArr = TryCast(ModJson("latestFilesIndexes"), JArray)
                                    If IdxArr Is Nothing Then Continue For
                                    Dim LatestMatches = IdxArr.Where(Function(idx)
                                                                         Dim Gv = If(idx("gameVersion")?.ToString(), "")
                                                                         Return Gv.Equals(VanillaVersion, StringComparison.OrdinalIgnoreCase) OrElse
                                                                                (MinorFallback <> "" AndAlso Gv.Equals(MinorFallback, StringComparison.OrdinalIgnoreCase) AndAlso
                                                                                 Not Item.CurVer.GameVersions.Any(Function(g) g.Equals(VanillaVersion, StringComparison.OrdinalIgnoreCase)))
                                                                     End Function).ToList()
                                    If Not LatestMatches.Any() Then Continue For
                                    Dim BestFileId = LatestMatches.OrderByDescending(Function(idx) idx("fileId").ToObject(Of Integer)()).First()("fileId").ToObject(Of Integer)()
                                    If Not UpdateFileIds.ContainsKey(BestFileId) Then UpdateFileIds(BestFileId) = New List(Of (AssetPackEntry, ResourceTypes, ResourceVersion))()
                                    UpdateFileIds(BestFileId).Add(Item)
                                Next
                            Next
                        End If
                        If UpdateFileIds.Any() Then
                            Dim FilesArr = TryCast(DlModRequest("https://api.curseforge.com/v1/mods/files", HttpMethod.Post,
                                $"{{""fileIds"": [{UpdateFileIds.Keys.Join(",")}]}}", "application/json")?("data"), JContainer)
                            If FilesArr IsNot Nothing Then
                                For Each FileJson As JObject In FilesArr
                                    Dim FileId = FileJson("id").ToObject(Of Integer)()
                                    If Not UpdateFileIds.ContainsKey(FileId) Then Continue For
                                    For Each Target In UpdateFileIds(FileId)
                                        Dim UpdVer = ResourceVersion.FromPlatformJson(FileJson, Target.ExpectedType)
                                        If Not UpdVer.DownloadAvailable Then Continue For
                                        If Target.CurVer.ReleaseDate >= UpdVer.ReleaseDate OrElse String.Equals(Target.CurVer.Hash, UpdVer.Hash, StringComparison.OrdinalIgnoreCase) Then Continue For
                                        If Target.Asset.UpdateVersion Is Nothing OrElse UpdVer.ReleaseDate > Target.Asset.UpdateVersion.ReleaseDate Then
                                            Target.Asset.UpdateVersion = UpdVer
                                        End If
                                    Next
                                Next
                            End If
                        End If
                    End If
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "从 CurseForge 检查材质/光影更新失败")
        End Try

        For Each C In Candidates
            If C.Asset.UpdateVersion IsNot Nothing Then Updatable.Add(C.Asset)
        Next
        Return Updatable
    End Function

    Public Shared Function ApplyAssetUpdates(Instance As McInstance, Items As IEnumerable(Of AssetPackEntry)) As List(Of String)
        Dim UpdatedNames As New List(Of String)
        If Instance Is Nothing OrElse Items Is Nothing Then Return UpdatedNames
        Dim Todo = Items.Where(Function(x) x IsNot Nothing AndAlso x.UpdateVersion IsNot Nothing AndAlso x.UpdateVersion.DownloadAvailable AndAlso File.Exists(x.FilePath)).ToList()
        If Not Todo.Any() Then Return UpdatedNames

        Dim TempDir = Path.Combine(PathTemp, "assets_update")
        Directory.CreateDirectory(TempDir)
        Dim NetFiles As New List(Of NetFile)
        Dim Plan As New List(Of (Entry As AssetPackEntry, TempPath As String, DestPath As String, NewBaseName As String))
        Dim TargetVanilla = If(Instance.Version?.VanillaName, "")
        Dim TargetLoaders = If(Instance.Version IsNot Nothing, Instance.Version.ModLoaders, ModLoaders.None)

        For Each Item In Todo
            Dim RawFileName = If(Item.UpdateVersion.FileName, "").Trim()
            If RawFileName = "" Then RawFileName = Item.BaseName
            For Each BadC In Path.GetInvalidFileNameChars()
                RawFileName = RawFileName.Replace(BadC, "_"c)
            Next
            If Not RawFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then RawFileName &= ".zip"
            Dim TempPath = Path.Combine(TempDir, $"{Guid.NewGuid():N}_{RawFileName}")
            Dim DestFileName = If(Item.IsDiskDisabled, RawFileName & ".disabled", RawFileName)
            Dim DestPath = Path.Combine(Path.GetDirectoryName(Item.FilePath), DestFileName)
            NetFiles.Add(Item.UpdateVersion.ToNetFile(TempPath, ResourceVersion.DownloadReason.Update, TargetVanilla, TargetLoaders))
            Plan.Add((Item, TempPath, DestPath, RawFileName))
        Next

        Dim DlLoader As New LoaderDownload("下载新版材质与光影", NetFiles)
        DlLoader.WaitForExit()
        If DlLoader.State <> LoadState.Finished Then
            Throw New Exception("部分材质或光影下载失败，请检查网络连接。")
        End If

        Dim IndieRoot = Instance.PathIndie.TrimEnd("\"c)
        For Each StepItem In Plan
            If Not File.Exists(StepItem.TempPath) OrElse New FileInfo(StepItem.TempPath).Length <= 0 Then Continue For
            If File.Exists(StepItem.Entry.FilePath) Then FileUtils.Delete(StepItem.Entry.FilePath, True)
            If File.Exists(StepItem.DestPath) Then FileUtils.Delete(StepItem.DestPath, True)
            File.Move(StepItem.TempPath, StepItem.DestPath)
            UpdateAssetConfigReferences(IndieRoot, StepItem.Entry.IsShaderPack, StepItem.Entry.BaseName, StepItem.NewBaseName)
            UpdatedNames.Add(StepItem.NewBaseName)
        Next
        Return UpdatedNames
    End Function

    Public Shared Function ScanAndCheckAssetUpdates(Instance As McInstance, Optional OnlyEnabled As Boolean = True) As List(Of AssetPackEntry)
        Dim Scan = ScanInstanceAssetEntries(Instance, ReadRpMetadata:=False)
        Return CheckKnownAssetUpdates(Instance, Scan.RpEntries, Scan.SpEntries, IncludeDisabled:=(Not OnlyEnabled))
    End Function

    Private Sub StartCheckAssetUpdatesAsync(ExpectedGen As Integer, ManualHint As Boolean)
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim HasZips = LastRpEntries.Any(Function(r) Not r.IsFolder) OrElse LastSpEntries.Any(Function(s) Not s.IsFolder)
        If Not HasZips Then
            LabAssetUpdateStatus.Text = "当前实例暂无可联网识别的 .zip 资源包或光影包。"
            BtnUpdateAllAssets.Visibility = Visibility.Collapsed
            Return
        End If
        If IsCheckingUpdates Then Return
        IsCheckingUpdates = True
        BtnCheckAssetUpdates.IsEnabled = False
        LabAssetUpdateStatus.Text = "正在从 Modrinth / CurseForge 识别已知材质与光影版本..."
        Dim RpSnap = LastRpEntries.ToList()
        Dim SpSnap = LastSpEntries.ToList()
        RunInNewThread(
        Sub()
            Try
                Dim Updates = CheckKnownAssetUpdates(Instance, RpSnap, SpSnap, IncludeDisabled:=True)
                Dim KnownCount = RpSnap.Concat(SpSnap).Where(Function(x) Not String.IsNullOrEmpty(x.SourcePlatform)).Count()
                RunInUi(
                Sub()
                    If ExpectedGen <> LoadGen Then Return
                    RenderResourcePacks(LastRpEntries)
                    RenderShaderPacks(LastSpEntries, LastActiveShader, LastShaderModNote)
                    If Updates.Any() Then
                        BtnUpdateAllAssets.Text = $"一键更新全部 ({Updates.Count})"
                        BtnUpdateAllAssets.Visibility = Visibility.Visible
                        LabAssetUpdateStatus.Text = $"已识别 {KnownCount} 个已知资源，其中 {Updates.Count} 个发现新版本可更新！"
                        If ManualHint Then Hint($"发现 {Updates.Count} 个已知材质/光影可更新！", HintType.Green)
                    Else
                        BtnUpdateAllAssets.Visibility = Visibility.Collapsed
                        LabAssetUpdateStatus.Text = If(KnownCount > 0, $"已识别 {KnownCount} 个已知资源，当前均已是适用此版本的最新版。", "未在 Modrinth / CurseForge 匹配到当前压缩包的在线版本记录（自定义/修改版包将被跳过）。")
                        If ManualHint Then Hint(LabAssetUpdateStatus.Text, HintType.Blue)
                    End If
                End Sub)
            Catch ex As Exception
                RunInUi(Sub()
                            If ExpectedGen = LoadGen Then LabAssetUpdateStatus.Text = $"检查已知资源更新失败：{ex.Message}"
                        End Sub)
            Finally
                RunInUi(Sub()
                            IsCheckingUpdates = False
                            BtnCheckAssetUpdates.IsEnabled = True
                        End Sub)
            End Try
        End Sub, "Check Asset Updates")
    End Sub

    Private Sub BtnCheckAssetUpdates_Click() Handles BtnCheckAssetUpdates.Click
        StartCheckAssetUpdatesAsync(LoadGen, ManualHint:=True)
    End Sub

    Private Sub BtnUpdateAllAssets_Click() Handles BtnUpdateAllAssets.Click
        Dim AllUpdatable = LastRpEntries.Concat(LastSpEntries).Where(Function(x) x.UpdateVersion IsNot Nothing).ToList()
        RunAssetUpdatesList(AllUpdatable)
    End Sub

    Private Sub RunSingleAssetUpdate(Pack As AssetPackEntry)
        If Pack Is Nothing OrElse Pack.UpdateVersion Is Nothing Then Return
        RunAssetUpdatesList({Pack})
    End Sub

    Private Sub RunAssetUpdatesList(Items As IEnumerable(Of AssetPackEntry))
        Dim Instance = PageInstanceLeft.Instance
        Dim List = If(Items, Enumerable.Empty(Of AssetPackEntry)()).Where(Function(x) x.UpdateVersion IsNot Nothing).ToList()
        If Instance Is Nothing OrElse Not List.Any() Then Return
        BtnCheckAssetUpdates.IsEnabled = False
        BtnUpdateAllAssets.IsEnabled = False
        LabAssetUpdateStatus.Text = $"正在下载并更新 {List.Count} 个资源，旧版本将移入回收站..."
        RunInNewThread(
        Sub()
            Try
                Dim Updated = ApplyAssetUpdates(Instance, List)
                RunInUi(
                Sub()
                    Hint($"已更新 {Updated.Count} 个资源包/光影：{String.Join("、", Updated)}", HintType.Green)
                    LoadAllAssetsAsync()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "更新材质/光影失败")
                RunInUi(Sub()
                            Hint($"更新失败：{ex.Message}", HintType.Red)
                            LabAssetUpdateStatus.Text = $"更新失败：{ex.Message}"
                        End Sub)
            Finally
                RunInUi(Sub()
                            BtnCheckAssetUpdates.IsEnabled = True
                            BtnUpdateAllAssets.IsEnabled = True
                        End Sub)
            End Try
        End Sub, "Apply Asset Updates")
    End Sub

#End Region

End Class
