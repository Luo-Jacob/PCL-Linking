Imports System.Collections.Concurrent
Imports System.IO
Imports System.IO.Compression
Imports System.Text

''' <summary>
''' 「常用模组清单」预设包数据存储、分享导入导出与一键装配辅助模块。
''' </summary>
Public Module ModPreset

    Public Const ShareCodePrefix As String = "LML-PRESET-V1:"

    Public ReadOnly Property PresetConfigPath As String
        Get
            Return GetPortableDataPath("ModPresets.json")
        End Get
    End Property

    Public Class PresetResourceItem
        Public Property ProjectId As String = ""
        Public Property Platform As Integer = CInt(ResourcePlatforms.Modrinth)
        Public Property Slug As String = ""
        Public Property Title As String = ""
        Public Property ResourceType As Integer = CInt(ResourceTypes.Mod)
        Public Property SupportedLoaders As Integer = CInt(ModLoaders.None)
        Public Property LogoUrl As String = ""
        Public Property Website As String = ""

        Public Function ToProject() As ResourceProject
            Return ResourceProject.FromProjectId(ProjectId, CType(Platform, ResourcePlatforms))
        End Function
    End Class

    Public Class ModPresetPack
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = "我的常用清单"
        Public Property Description As String = ""
        Public Property Items As New List(Of PresetResourceItem)
    End Class

    Public Class PresetResolutionItem
        Public Property Entry As PresetResourceItem
        Public Property MatchedVersion As ResourceVersion
        Public Property MissingReason As String
        Public ReadOnly Property IsResolved As Boolean
            Get
                Return MatchedVersion IsNot Nothing AndAlso String.IsNullOrEmpty(MissingReason)
            End Get
        End Property
    End Class

    Public Function DeserializePresetPack(Tok As JToken, Optional GenerateNewId As Boolean = False) As ModPresetPack
        Dim Pack As New ModPresetPack With {
            .Id = If(GenerateNewId OrElse String.IsNullOrWhiteSpace(Tok("Id")?.ToString()), Guid.NewGuid().ToString("N"), Tok("Id").ToString()),
            .Name = If(Tok("Name")?.ToString(), "未命名清单"),
            .Description = If(Tok("Description")?.ToString(), "")
        }
        Dim ItemsArr = TryCast(Tok("Items"), JArray)
        If ItemsArr IsNot Nothing Then
            For Each ItemTok In ItemsArr
                Dim ProjectId = If(ItemTok("ProjectId")?.ToString(), "")
                If String.IsNullOrWhiteSpace(ProjectId) Then Continue For
                Pack.Items.Add(New PresetResourceItem With {
                    .ProjectId = ProjectId,
                    .Platform = If(ItemTok("Platform") IsNot Nothing, ItemTok("Platform").ToObject(Of Integer)(), CInt(ResourcePlatforms.Modrinth)),
                    .Slug = If(ItemTok("Slug")?.ToString(), ""),
                    .Title = If(ItemTok("Title")?.ToString(), ""),
                    .ResourceType = If(ItemTok("ResourceType") IsNot Nothing, ItemTok("ResourceType").ToObject(Of Integer)(), CInt(ResourceTypes.Mod)),
                    .SupportedLoaders = If(ItemTok("SupportedLoaders") IsNot Nothing, ItemTok("SupportedLoaders").ToObject(Of Integer)(), CInt(ModLoaders.None)),
                    .LogoUrl = If(ItemTok("LogoUrl")?.ToString(), ""),
                    .Website = If(ItemTok("Website")?.ToString(), "")
                })
            Next
        End If
        Return Pack
    End Function

    Public Function SerializePresetPack(Pack As ModPresetPack) As JObject
        Dim ItemsArr As New JArray
        For Each Entry In Pack.Items
            ItemsArr.Add(New JObject From {
                {"ProjectId", Entry.ProjectId},
                {"Platform", Entry.Platform},
                {"Slug", Entry.Slug},
                {"Title", Entry.Title},
                {"ResourceType", Entry.ResourceType},
                {"SupportedLoaders", Entry.SupportedLoaders},
                {"LogoUrl", If(Entry.LogoUrl, "")},
                {"Website", If(Entry.Website, "")}
            })
        Next
        Return New JObject From {
            {"Id", Pack.Id},
            {"Name", Pack.Name},
            {"Description", If(Pack.Description, "")},
            {"Items", ItemsArr}
        }
    End Function

    Public Function LoadPresetPacks() As List(Of ModPresetPack)
        Try
            If Not FileUtils.Exists(PresetConfigPath) Then Return New List(Of ModPresetPack)
            Dim Raw As String = FileUtils.TryReadAsString(PresetConfigPath)
            If String.IsNullOrWhiteSpace(Raw) Then Return New List(Of ModPresetPack)
            Dim Arr = JArray.Parse(Raw)
            Dim Result As New List(Of ModPresetPack)
            For Each Tok In Arr
                Result.Add(DeserializePresetPack(Tok))
            Next
            Return Result
        Catch ex As Exception
            Logger.Warn(ex, "读取常用模组清单失败")
            Return New List(Of ModPresetPack)
        End Try
    End Function

    Public Sub SavePresetPacks(Packs As IEnumerable(Of ModPresetPack))
        Try
            DirectoryUtils.Create(Paths.BaseThenName)
            Dim Arr As New JArray
            For Each Pack In Packs
                Arr.Add(SerializePresetPack(Pack))
            Next
            FileUtils.Write(PresetConfigPath, Arr.ToString())
        Catch ex As Exception
            Logger.Error(ex, "保存常用模组清单失败")
        End Try
    End Sub

    Public Function ExportPresetJson(Pack As ModPresetPack) As String
        Return SerializePresetPack(Pack).ToString(Newtonsoft.Json.Formatting.Indented)
    End Function

    Public Function ExportPresetShareCode(Pack As ModPresetPack) As String
        Dim Json = SerializePresetPack(Pack).ToString(Newtonsoft.Json.Formatting.None)
        Dim RawBytes = Encoding.UTF8.GetBytes(Json)
        Using Ms As New MemoryStream()
            Using Gz As New GZipStream(Ms, CompressionLevel.Optimal, True)
                Gz.Write(RawBytes, 0, RawBytes.Length)
            End Using
            Return ShareCodePrefix & Convert.ToBase64String(Ms.ToArray())
        End Using
    End Function

    Public Function ImportPresetFromText(RawText As String) As (Pack As ModPresetPack, ErrorMsg As String)
        Try
            Dim Trimmed = If(RawText, "").Trim()
            If String.IsNullOrEmpty(Trimmed) Then Return (Nothing, "导入内容为空！")
            Dim JsonStr As String = Trimmed
            If Trimmed.StartsWith(ShareCodePrefix, StringComparison.OrdinalIgnoreCase) Then
                Dim B64 = Trimmed.Substring(ShareCodePrefix.Length).Trim()
                Dim Compressed = Convert.FromBase64String(B64)
                Using MsIn As New MemoryStream(Compressed), Gz As New GZipStream(MsIn, CompressionMode.Decompress), MsOut As New MemoryStream()
                    Gz.CopyTo(MsOut)
                    JsonStr = Encoding.UTF8.GetString(MsOut.ToArray())
                End Using
            End If
            Dim Token = JToken.Parse(JsonStr)
            If TypeOf Token Is JArray Then
                Dim Arr = DirectCast(Token, JArray)
                If Not Arr.Any() Then Return (Nothing, "JSON 数组中没有找到清单数据！")
                Token = Arr(0)
            End If
            If TypeOf Token IsNot JObject Then Return (Nothing, "格式不正确：需要清单 JSON 或分享码！")
            Dim Pack = DeserializePresetPack(Token, GenerateNewId:=True)
            If String.IsNullOrWhiteSpace(Pack.Name) Then Pack.Name = $"导入清单 {DateTime.Now:MM-dd HH:mm}"
            If Not Pack.Items.Any() Then Return (Nothing, "清单中不包含有效资源条目！")
            Return (Pack, Nothing)
        Catch ex As Exception
            Return (Nothing, $"解析导入内容失败：{ex.Message}")
        End Try
    End Function

    ''' <summary>
    ''' 判断候选版本是否匹配目标游戏版本与资源类别。
    ''' </summary>
    Public Function ResolvePresetItems(Pack As ModPresetPack, GameVersion As String, TargetLoader As ModLoaders) As List(Of PresetResolutionItem)
        Dim Bag As New ConcurrentDictionary(Of Integer, PresetResolutionItem)
        Dim Threads As New List(Of Thread)
        Dim Index As Integer = 0
        For Each Entry In Pack.Items
            Dim LocalIndex As Integer = Index
            Dim LocalEntry As PresetResourceItem = Entry
            Index += 1
            Dim T = RunInNewThread(
            Sub()
                Try
                    Dim Platform As ResourcePlatforms = CType(LocalEntry.Platform, ResourcePlatforms)
                    Dim ResType As ResourceTypes = CType(LocalEntry.ResourceType, ResourceTypes)
                    If ResType = ResourceTypes.Mod AndAlso TargetLoader = ModLoaders.None Then
                        Bag(LocalIndex) = New PresetResolutionItem With {
                            .Entry = LocalEntry,
                            .MissingReason = "当前选择为纯原版（未选 Mod 加载器），无法安装 Mod"
                        }
                        Return
                    End If
                    Dim Versions = ResourceVersion.FromProjectId(LocalEntry.ProjectId, Platform, LoadDependencies:=False)
                    Dim Candidates = Versions.Where(
                    Function(v)
                        If Not v.DownloadAvailable Then Return False
                        If ResType = ResourceTypes.Mod Then
                            If Not (v.ResourceType = ResourceTypes.Mod OrElse v.ResourceType.HasFlagF(ResourceTypes.Mod)) Then Return False
                            If TargetLoader <> ModLoaders.None AndAlso Not v.ModLoaders.HasFlagF(TargetLoader) Then Return False
                        ElseIf ResType = ResourceTypes.ResourcePack Then
                            If v.ResourceType <> ResourceTypes.ResourcePack AndAlso v.ResourceType <> ResourceTypes.Any Then
                                '部分资源包仅标为 ResourcePack
                                If Not (v.FileName IsNot Nothing AndAlso v.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) Then Return False
                            End If
                        End If
                        Return IsVersionMatch(v.GameVersions, GameVersion)
                    End Function).
                    OrderBy(Function(v) CInt(v.ReleaseType)).
                    ThenByDescending(Function(v) v.ReleaseDate).
                    ToList()
                    If Candidates.Any() Then
                        Bag(LocalIndex) = New PresetResolutionItem With {
                            .Entry = LocalEntry,
                            .MatchedVersion = Candidates(0)
                        }
                    Else
                        Dim LoaderLabel As String = If(ResType = ResourceTypes.Mod AndAlso TargetLoader <> ModLoaders.None, $" / {TargetLoader}", "")
                        Bag(LocalIndex) = New PresetResolutionItem With {
                            .Entry = LocalEntry,
                            .MissingReason = $"未找到适配 {GameVersion}{LoaderLabel} 的可用文件"
                        }
                    End If
                Catch ex As Exception
                    Bag(LocalIndex) = New PresetResolutionItem With {
                        .Entry = LocalEntry,
                        .MissingReason = $"获取版本列表失败：{ex.Message}"
                    }
                End Try
            End Sub, $"Preset Resolve {LocalEntry.Title}")
            Threads.Add(T)
        Next
        For Each T In Threads
            T.Join()
        Next
        Dim Ordered As New List(Of PresetResolutionItem)
        For i As Integer = 0 To Pack.Items.Count - 1
            If Bag.ContainsKey(i) Then Ordered.Add(Bag(i))
        Next
        Return Ordered
    End Function

    ''' <summary>
    ''' 检查已解析列表中 Mod 声明的前置依赖，返回缺失且适配目标版本/加载器的前置资源。
    ''' </summary>
    Public Function ResolveMissingDependencies(Available As IEnumerable(Of PresetResolutionItem), GameVersion As String, TargetLoader As ModLoaders) As List(Of PresetResolutionItem)
        Dim Result As New List(Of PresetResolutionItem)
        Dim ExistingIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim PendingDeps As New Dictionary(Of String, ResourcePlatforms)(StringComparer.OrdinalIgnoreCase)

        For Each Item In Available
            If Not String.IsNullOrEmpty(Item.Entry?.ProjectId) Then ExistingIds.Add(Item.Entry.ProjectId)
        Next
        For Each Item In Available
            Dim DepIds = If(Item.MatchedVersion?.Dependencies IsNot Nothing AndAlso Item.MatchedVersion.Dependencies.Any(),
                            Item.MatchedVersion.Dependencies,
                            Item.MatchedVersion?.RawDependencies)
            If DepIds Is Nothing Then Continue For
            Dim Plat As ResourcePlatforms = CType(Item.Entry.Platform, ResourcePlatforms)
            For Each DepId In DepIds
                If String.IsNullOrWhiteSpace(DepId) OrElse ExistingIds.Contains(DepId) OrElse PendingDeps.ContainsKey(DepId) Then Continue For
                PendingDeps(DepId) = Plat
            Next
        Next
        If Not PendingDeps.Any() Then Return Result

        Dim Bag As New ConcurrentBag(Of PresetResolutionItem)
        Dim Threads As New List(Of Thread)
        For Each Kv In PendingDeps
            Dim DepId = Kv.Key
            Dim Plat = Kv.Value
            Dim T = RunInNewThread(
            Sub()
                Try
                    Dim Proj = ResourceProject.FromProjectId(DepId, Plat)
                    If Proj Is Nothing Then Return
                    Dim Versions = ResourceVersion.FromProjectId(DepId, Plat, LoadDependencies:=False)
                    Dim Matched = Versions.Where(
                    Function(v)
                        If Not v.DownloadAvailable Then Return False
                        If Not v.ResourceType.HasFlagF(ResourceTypes.Mod) Then Return False
                        If TargetLoader <> ModLoaders.None AndAlso Not v.ModLoaders.HasFlagF(TargetLoader) Then Return False
                        Return IsVersionMatch(v.GameVersions, GameVersion)
                    End Function).OrderBy(Function(v) CInt(v.ReleaseType)).ThenByDescending(Function(v) v.ReleaseDate).FirstOrDefault()
                    If Matched Is Nothing Then Return
                    Bag.Add(New PresetResolutionItem With {
                        .Entry = New PresetResourceItem With {
                            .ProjectId = Proj.Id,
                            .Platform = CInt(Proj.Platform),
                            .Slug = Proj.Slug,
                            .Title = Proj.TranslatedName,
                            .ResourceType = CInt(ResourceTypes.Mod),
                            .SupportedLoaders = CInt(Proj.ModLoaders),
                            .LogoUrl = If(Proj.LogoUrl, ""),
                            .Website = If(Proj.Website, "")
                        },
                        .MatchedVersion = Matched
                    })
                Catch ex As Exception
                    Logger.Warn(ex, $"解析清单前置依赖失败: {DepId}")
                End Try
            End Sub, $"Preset Dep {DepId}")
            Threads.Add(T)
        Next
        For Each T In Threads
            T.Join()
        Next
        Result.AddRange(Bag.OrderBy(Function(r) r.Entry.Title))
        Return Result
    End Function

    Private Function IsVersionMatch(SupportedVersions As List(Of String), TargetMcVersion As String) As Boolean
        If SupportedVersions Is Nothing OrElse Not SupportedVersions.Any() Then Return False
        Dim NormTarget As String = TargetMcVersion.Trim().Lower
        For Each V In SupportedVersions
            If String.IsNullOrWhiteSpace(V) Then Continue For
            Dim NormV As String = V.Trim().Lower
            If NormV = NormTarget Then Return True
        Next
        Return False
    End Function

End Module
