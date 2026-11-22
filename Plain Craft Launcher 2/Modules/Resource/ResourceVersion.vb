''' <summary>
''' 社区资源的单个可下载版本。
''' </summary>
Public Class ResourceVersion

    '基础字段

    ''' <summary>
    ''' 用于唯一性鉴别该版本的 ID。CurseForge 中为 123456 的大整数，Modrinth 中为英文乱码的 Version 字段。
    ''' </summary>
    Public Id As String
    ''' <summary>
    ''' 该版本所对应的单一资源类别。
    ''' </summary>
    Public ResourceType As ResourceTypes

    '描述性字段

    ''' <summary>
    ''' 版本描述名。
    ''' 并非文件名或版本号，是由上传者完全自定义的字段。
    ''' </summary>
    Public Display As String
    ''' <summary>
    ''' 发布时间。
    ''' </summary>
    Public ReleaseDate As Date
    ''' <summary>
    ''' 下载量计数。
    ''' 注意，仅为一个平台的计数，无法反映多平台加起来的下载量，且 CurseForge 可能错误地返回 0。
    ''' </summary>
    Public DownloadCount As Integer
    ''' <summary>
    ''' 版本号。
    ''' 不一定是标准格式。CurseForge 默认为 Nothing。
    ''' </summary>
    Public Version As String
    ''' <summary>
    ''' 所有支持的 Mod 加载器列表。可能为 <see cref="ModLoaders.None"/>。
    ''' </summary>
    Public ModLoaders As ModLoaders = ModLoaders.None
    ''' <summary>
    ''' 支持的游戏版本列表。类型包括："26.1.5"，"26.1"，"26.1 预览版"，"1.18.5"，"1.18"，"1.18 预览版"，"21w15a"，"未知版本"。
    ''' </summary>
    Public GameVersions As List(Of String)

    ''' <summary>
    ''' 发布状态。
    ''' </summary>
    Public ReleaseType As ReleaseTypes
    ''' <summary>
    ''' 发布状态的友好描述。
    ''' 例如："正式版"，"Beta 版"。
    ''' </summary>
    Public ReadOnly Property ReleaseTypeDisplay As String
        Get
            Select Case ReleaseType
                Case ReleaseTypes.Release
                    Return LocalizationCore.TranslateText("正式版")
                Case ReleaseTypes.Beta
                    Return LocalizationCore.TranslateText(If(ModeDebug, "Beta 版", "测试版"))
                Case Else
                    Return LocalizationCore.TranslateText(If(ModeDebug, "Alpha 版", "早期测试版"))
            End Select
        End Get
    End Property
    Public Enum ReleaseTypes
        'https://docs.curseforge.com/rest-api/#tocS_FileReleaseType
        Release = 1
        Beta = 2
        Alpha = 3
    End Enum

    '下载信息

    ''' <summary>
    ''' 下载信息是否可用。
    ''' </summary>
    Public ReadOnly Property DownloadAvailable As Boolean
        Get
            Return FileName IsNot Nothing AndAlso ((DownloadUrls IsNot Nothing AndAlso DownloadUrls.Any()) OrElse Not String.IsNullOrEmpty(ExternalOnlyUrl))
        End Get
    End Property
    ''' <summary>
    ''' 文件名。
    ''' </summary>
    Public FileName As String = Nothing
    ''' <summary>
    ''' 对应文件的所有可能的下载 URL。
    ''' </summary>
    Public DownloadUrls As List(Of String)
    ''' <summary>
    ''' 仅支持前往网页下载的外部链接（例如 SpigotMC 付费或外链非直链插件）。
    ''' </summary>
    Public ExternalOnlyUrl As String = Nothing
    ''' <summary>
    ''' 直接下载失败或被拦截时的网页备用链接。
    ''' </summary>
    Public ExternalFallbackUrl As String = Nothing
    ''' <summary>
    ''' 对应文件的 SHA1 / SHA256 / MD5。固定为小写。
    ''' </summary>
    Public Hash As String = Nothing
    ''' <summary>
    ''' 文件大小。不可用时为 -1。
    ''' </summary>
    Public Size As Integer = -1
    ''' <summary>
    ''' 作为该版本依赖项的项目 ID。
    ''' 它们可能没有加载，在加载后会添加到 Dependencies 中（主要是因为 Modrinth 返回的是字符串 ID 而非 Slug，导致 Project.Id 查询不到）。
    ''' </summary>
    Friend RawDependencies As New List(Of String)
    ''' <summary>
    ''' 作为该版本依赖项的项目 ID。
    ''' </summary>
    Public Dependencies As New List(Of String)

    '初始化

    ''' <summary>
    ''' 从平台 API 返回的 JSON 中初始化实例。若出错会抛出异常。
    ''' </summary>
    Public Shared Function FromPlatformJson(Data As JObject, ExpectedResourceType As ResourceTypes) As ResourceVersion
        Dim Result As New ResourceVersion
        With Result
            .ResourceType = ExpectedResourceType
            If Data.ContainsKey("gameId") Then
#Region "CurseForge"
                '简单信息
                .Id = Data("id")
                .Display = Data("displayName").ToString.Replace("	", "").Trim(" ")
                .Version = Nothing
                .ReleaseDate = Data("fileDate")
                .ReleaseType = CType(Data("releaseType").ToObject(Of Integer), ReleaseTypes)
                .DownloadCount = Data("downloadCount")
                .FileName = Data("fileName")
                .Size = Data("fileLength")
                If .Size = 0 Then .Size = -1 'CurseForge 可能错误地返回 0 尺寸（#9104）
                .Hash = CType(Data("hashes"), JArray).ToList.FirstOrDefault(Function(s) s("algo").ToObject(Of Integer) = 1)?("value")?.ToString().Lower()
                If .Hash Is Nothing Then .Hash = CType(Data("hashes"), JArray).ToList.FirstOrDefault(Function(s) s("algo").ToObject(Of Integer) = 2)?("value")?.ToString().Lower()
                'DownloadAddress
                Dim Url = Data("downloadUrl").ToString
                If Url = "" AndAlso .Id.Length > 4 Then Url = $"https://edge.forgecdn.net/files/{CInt(.Id.ToString.Substring(0, 4))}/{CInt(.Id.ToString.Substring(4))}/{ .FileName}"
                Dim OfficialUrls = If(String.IsNullOrEmpty(Url), New List(Of String), ParseCurseForgeDownloadUrls(Url.Replace(.FileName, StringUtils.UrlEscape(.FileName)))) '对 CurseForge 下载地址进行多种修正
                .DownloadUrls = DlOrderModFileUrls(OfficialUrls, {DlSourceModGet(Url)}) '镜像源不能传入 URL 编码后的文件名
                'Dependencies
                If Data.ContainsKey("dependencies") Then
                    .RawDependencies = Data("dependencies").
                            Where(Function(d) d("relationType").ToObject(Of Integer) = 3 AndAlso '种类为依赖
                                              d("modId").ToObject(Of Integer) <> 306612 AndAlso d("modId").ToObject(Of Integer) <> 634179). '排除 Fabric API 和 Quilt API
                            Select(Function(d) d("modId").ToString).ToList
                End If
                'GameVersions
                Dim RawVersions As List(Of String) = Data("gameVersions").Select(Function(t) t.ToString.Trim.Lower).ToList
                .GameVersions = RawVersions.
                        Where(Function(v) McVersion.IsStandardFormat(v)).
                        Select(Function(v) v.Replace("-snapshot", " 预览版")).
                        Distinct.ToList
                If .GameVersions.IsSingle Then
                    .GameVersions = .GameVersions.ToList
                ElseIf .GameVersions.Count > 1 Then
                    .GameVersions = .GameVersions.SortByComparison(AddressOf McUtils.CompareVersionGE).ToList
                    If .ResourceType = ResourceTypes.ModPack Then .GameVersions = { .GameVersions(0)}.ToList '整合包理应只 “支持” 一个版本
                Else
                    .GameVersions = New List(Of String) From {"未知版本"}
                End If
                'ModLoaders
                .ModLoaders = ModLoaders.None
                For Each Loader In EnumUtils.GetAllFlags(Of ModLoaders)()
                    If RawVersions.Contains(Loader.ToString.Lower) Then .ModLoaders = .ModLoaders Or Loader
                Next
#End Region
            Else
#Region "Modrinth"
                '简单信息
                .Id = Data("id")
                .Display = Data("name").ToString.Replace("	", "").Trim(" ")
                .Version = Data("version_number")
                .ReleaseDate = Data("date_published")
                .ReleaseType = If(Data("version_type").ToString = "release", ReleaseTypes.Release, If(Data("version_type").ToString = "beta", ReleaseTypes.Beta, ReleaseTypes.Alpha))
                .DownloadCount = Data("downloads")
                If CType(Data("files"), JArray).Any() Then '可能为空
                    Dim File As JToken = Data("files")(0)
                    .FileName = File("filename")
                    Dim RawUrl As String = File("url")?.ToString()
                    .DownloadUrls = DlOrderModFileUrls({RawUrl}, {DlSourceModGet(RawUrl)}) '按设置决定镜像源与官方源顺序
                    .Size = File("size")
                    .Hash = File("hashes")("sha1")?.ToString.Lower()
                End If
                '类别与 Loaders
                '结果可能混杂着 Mod、数据包和服务端插件
                Dim RawLoaders As List(Of String) = Data("loaders").Select(Function(v) v.ToString).ToList
                .ModLoaders = ModLoaders.None
                For Each Loader As ModLoaders In EnumUtils.GetAllFlags(Of ModLoaders)()
                    If RawLoaders.Contains(Loader.ToString.Lower) Then .ModLoaders = .ModLoaders Or Loader
                Next
                If .ResourceType = ResourceTypes.Plugin Then
                    If RawLoaders.Any AndAlso Not RawLoaders.Intersect({"bukkit", "folia", "paper", "purpur", "spigot", "waterfall", "velocity", "bungeecord"}).Any() Then
                        .ResourceType = If(RawLoaders.Contains("datapack"), ResourceTypes.DataPack, ResourceTypes.Mod)
                    End If
                ElseIf .ResourceType.HasFlagF(ResourceTypes.Mod) OrElse .ResourceType.HasFlagF(ResourceTypes.DataPack) Then
                    Dim ComputedType As ResourceTypes = 0
                    If RawLoaders.Intersect({"bukkit", "folia", "paper", "purpur", "spigot", "waterfall", "velocity", "bungeecord"}).Any() Then ComputedType = ComputedType Or ResourceTypes.Plugin 'Veinminer Enchantment 等同时支持服务端插件与客户端 Mod
                    If RawLoaders.Contains("datapack") Then ComputedType = ComputedType Or ResourceTypes.DataPack
                    If .ModLoaders.Flags().Any() Then ComputedType = ComputedType Or ResourceTypes.Mod
                    If ComputedType <> 0 Then .ResourceType = ComputedType
                Else
                    '使用传入的类别，不作修改（#8377）
                End If
                'Dependencies
                If Data.ContainsKey("dependencies") Then
                    .RawDependencies = Data("dependencies").
                            Where(Function(d) d("dependency_type") = "required" AndAlso '种类为依赖
                                              d("project_id") <> "P7dR8mSH" AndAlso d("project_id") <> "qvIfYCYJ" AndAlso '排除 Fabric API 和 Quilt API
                                              d("project_id").ToString.Length > 0). '有时候真的会空……
                            Select(Function(d) d("project_id").ToString).ToList
                End If
                'GameVersions
                Dim RawVersions As List(Of String) = Data("game_versions").Select(Function(t) t.ToString.Trim.Lower).ToList
                .GameVersions = RawVersions.Where(Function(v) v.Contains(".")).
                                               Select(Function(v) If(v.Contains("-"), v.BeforeFirst("-") & " 预览版", If(v.StartsWithF("b1."), "远古版本", v))).
                                               Distinct.ToList
                If .GameVersions.IsSingle Then
                    '无需处理
                ElseIf .GameVersions.Count > 1 Then
                    .GameVersions = .GameVersions.SortByComparison(AddressOf McUtils.CompareVersionGE).ToList
                    If .ResourceType = ResourceTypes.ModPack Then .GameVersions = New List(Of String) From { .GameVersions(0)} '整合包理应只 “支持” 一个版本
                ElseIf RawVersions.Any(Function(v) v.RegexCheck("[0-9]{2}w[0-9]{2}[a-z]")) Then
                    .GameVersions = RawVersions.Where(Function(v) v.RegexCheck("[0-9]{2}w[0-9]{2}[a-z]")).ToList
                Else
                    .GameVersions = New List(Of String) From {"未知版本"}
                End If
#End Region
            End If
        End With
        Return Result
    End Function
    Private Shared Function SanitizeJarName(ProjectName As String, VersionName As String) As String
        Dim BaseName As String = $"{ProjectName}-{VersionName}".Trim()
        For Each Ch In Path.GetInvalidFileNameChars()
            BaseName = BaseName.Replace(Ch, "-"c)
        Next
        BaseName = BaseName.Trim("."c, " "c)
        If String.IsNullOrEmpty(BaseName) Then BaseName = "plugin"
        If Not BaseName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) Then BaseName &= ".jar"
        Return BaseName
    End Function

    Private Shared Function NormalizeStandardGameVersions(RawItems As IEnumerable(Of String)) As List(Of String)
        Dim Collected As New List(Of String)
        If RawItems IsNot Nothing Then
            For Each Raw In RawItems
                If String.IsNullOrWhiteSpace(Raw) Then Continue For
                For Each Part In Raw.Split({","c, "-"c, " "c}, StringSplitOptions.RemoveEmptyEntries)
                    Dim Clean = Part.Trim().Replace(".x", "").Replace(".*", "")
                    If McVersion.IsStandardFormat(Clean) Then Collected.Add(Clean)
                Next
            Next
        End If
        Collected = Collected.Distinct().ToList()
        If Collected.Count > 1 Then
            Collected = Collected.SortByComparison(AddressOf McUtils.CompareVersionGE).ToList()
        ElseIf Collected.Count = 0 Then
            Collected.Add("未知版本")
        End If
        Return Collected
    End Function

    ''' <summary>
    ''' 从项目 ID 获取其下的全部版本列表，并获取其前置项目信息。
    ''' </summary>
    Public Shared Function FromProjectId(ProjectId As String, Platform As ResourcePlatforms, Optional LoadDependencies As Boolean = True) As List(Of ResourceVersion)
        If Not Platform.Flags.IsSingle Then Throw New ArgumentException($"必须指定单一平台，当前值为 {Platform}")
        Dim TargetProject = ResourceProject.FromProjectId(ProjectId, Platform)
        '获取项目对象的文件列表
        Dim Result As List(Of ResourceVersion) = Nothing
        If Not ProjectFilesCache.TryGetValue(ProjectId, Result) Then
            Logger.Info($"开始获取项目版本列表：{ProjectId}")
            Dim Parsed As List(Of ResourceVersion)
            If Platform = ResourcePlatforms.Hangar Then
                Dim HangarJson As JObject = DlModRequest($"https://hangar.papermc.io/api/v1/projects/{ProjectId}/versions?limit=25&offset=0")
                Dim HangarArr = If(TryCast(HangarJson("result"), JArray), New JArray)
                Parsed = New List(Of ResourceVersion)
                For Each V As JObject In HangarArr
                    Dim VerName As String = If(V("name")?.ToString(), "").Trim()
                    Dim Item As New ResourceVersion With {
                        .ResourceType = TargetProject.Types,
                        .Id = $"{ProjectId}@{VerName}",
                        .Version = VerName,
                        .Display = If(String.IsNullOrWhiteSpace(VerName), TargetProject.RawName, VerName),
                        .ReleaseDate = If(V("createdAt") IsNot Nothing, V("createdAt").ToObject(Of Date), TargetProject.LastUpdate),
                        .DownloadCount = If(V("stats")?("totalDownloads")?.ToObject(Of Integer)(), 0)
                    }
                    Dim ChannelName As String = If(V("channel")?("name")?.ToString(), "Release").ToLowerInvariant()
                    If ChannelName.Contains("alpha") Then
                        Item.ReleaseType = ReleaseTypes.Alpha
                    ElseIf ChannelName.Contains("beta") OrElse ChannelName.Contains("snapshot") Then
                        Item.ReleaseType = ReleaseTypes.Beta
                    Else
                        Item.ReleaseType = ReleaseTypes.Release
                    End If
                    Dim DownloadsObj = TryCast(V("downloads"), JObject)
                    Dim DlObj As JObject = Nothing
                    If DownloadsObj IsNot Nothing Then
                        For Each PrefKey In {"PAPER", "VELOCITY", "WATERFALL"}
                            DlObj = TryCast(DownloadsObj(PrefKey), JObject)
                            If DlObj IsNot Nothing Then Exit For
                        Next
                        If DlObj Is Nothing Then
                            DlObj = DownloadsObj.Properties().Select(Function(p) TryCast(p.Value, JObject)).FirstOrDefault(Function(o) o IsNot Nothing)
                        End If
                    End If
                    If DlObj IsNot Nothing Then
                        Dim FileInfoObj = TryCast(DlObj("fileInfo"), JObject)
                        Dim FileInfoName = FileInfoObj?("name")?.ToString()
                        Dim SizeBytes As Long = If(FileInfoObj?("sizeBytes") IsNot Nothing, FileInfoObj("sizeBytes").ToObject(Of Long), -1)
                        Item.Size = If(SizeBytes > 0 AndAlso SizeBytes <= Integer.MaxValue, CInt(SizeBytes), -1)
                        Dim Sha256 = FileInfoObj?("sha256Hash")?.ToString()?.Trim()?.ToLowerInvariant()
                        If Not String.IsNullOrEmpty(Sha256) AndAlso Sha256.Length = 64 Then Item.Hash = Sha256
                        Dim DlUrl = DlObj("downloadUrl")?.ToString()
                        Dim ExtUrl = DlObj("externalUrl")?.ToString()
                        Item.FileName = If(Not String.IsNullOrWhiteSpace(FileInfoName), FileInfoName, SanitizeJarName(TargetProject.RawName, VerName))
                        If Not String.IsNullOrWhiteSpace(DlUrl) Then
                            Item.DownloadUrls = New List(Of String) From {DlUrl}
                            Item.ExternalFallbackUrl = If(Not String.IsNullOrWhiteSpace(ExtUrl), ExtUrl, TargetProject.Website)
                        ElseIf Not String.IsNullOrWhiteSpace(ExtUrl) Then
                            If ExtUrl.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) Then
                                Item.DownloadUrls = New List(Of String) From {ExtUrl}
                                Item.ExternalFallbackUrl = TargetProject.Website
                            Else
                                Item.ExternalOnlyUrl = ExtUrl
                            End If
                        End If
                    End If
                    Dim DepVers As New List(Of String)
                    Dim PlatDepsObj = TryCast(V("platformDependencies"), JObject)
                    If PlatDepsObj IsNot Nothing Then
                        For Each Prop In PlatDepsObj.Properties()
                            Dim Arr = TryCast(Prop.Value, JArray)
                            If Arr IsNot Nothing Then
                                For Each Tok In Arr
                                    DepVers.Add(Tok.ToString())
                                Next
                            End If
                        Next
                    End If
                    Item.GameVersions = NormalizeStandardGameVersions(DepVers)
                    If Item.DownloadAvailable Then Parsed.Add(Item)
                Next
                Parsed = Parsed.DistinctBy(Function(a) a.Id).ToList()
            ElseIf Platform = ResourcePlatforms.SpigotMC Then
                Dim RawId = If(ProjectId.StartsWith("spigot:", StringComparison.OrdinalIgnoreCase), ProjectId.Substring(7), ProjectId)
                Dim Detail As JObject = DlModRequest($"https://api.spiget.org/v2/resources/{RawId}")
                Dim VersArr As JArray = DlModRequest($"https://api.spiget.org/v2/resources/{RawId}/versions?size=25&sort=-releaseDate")
                Dim IsPremium As Boolean = If(Detail("premium")?.ToObject(Of Boolean)(), False)
                Dim IsExternal As Boolean = If(Detail("external")?.ToObject(Of Boolean)(), False)
                Dim ExtUrl As String = Detail("file")?("externalUrl")?.ToString()
                Dim TestedRaw = If(TryCast(Detail("testedVersions"), JArray)?.Select(Function(t) t.ToString()), {})
                Dim TestedVers = NormalizeStandardGameVersions(TestedRaw)
                If VersArr Is Nothing OrElse VersArr.Count = 0 Then
                    Dim LatestId = Detail("version")?("id")?.ToString()
                    If Not String.IsNullOrWhiteSpace(LatestId) Then
                        VersArr = New JArray(New JObject From {
                            {"id", LatestId},
                            {"name", LatestId},
                            {"downloads", Detail("downloads")},
                            {"releaseDate", Detail("updateDate")}
                        })
                    Else
                        VersArr = New JArray()
                    End If
                End If
                Parsed = New List(Of ResourceVersion)
                For Each V As JObject In VersArr
                    Dim VerId = If(V("id")?.ToString(), "")
                    Dim VerName = If(Not String.IsNullOrWhiteSpace(V("name")?.ToString()), V("name").ToString().Trim(), VerId)
                    Dim RelSec As Long = If(V("releaseDate") IsNot Nothing, V("releaseDate").ToObject(Of Long), 0)
                    Dim Item As New ResourceVersion With {
                        .ResourceType = TargetProject.Types,
                        .Id = $"spigot:{RawId}@{VerId}",
                        .Version = VerName,
                        .Display = VerName,
                        .FileName = SanitizeJarName(TargetProject.RawName, VerName),
                        .ReleaseDate = If(RelSec > 0, DateTimeOffset.FromUnixTimeSeconds(RelSec).LocalDateTime, TargetProject.LastUpdate),
                        .DownloadCount = If(V("downloads")?.ToObject(Of Integer)(), 0),
                        .ReleaseType = ReleaseTypes.Release,
                        .GameVersions = TestedVers.ToList(),
                        .Size = -1,
                        .Hash = Nothing
                    }
                    If IsPremium Then
                        Item.ExternalOnlyUrl = TargetProject.Website
                    ElseIf IsExternal Then
                        If Not String.IsNullOrWhiteSpace(ExtUrl) AndAlso ExtUrl.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) Then
                            Item.DownloadUrls = New List(Of String) From {ExtUrl}
                            Item.ExternalFallbackUrl = TargetProject.Website
                        Else
                            Item.ExternalOnlyUrl = If(Not String.IsNullOrWhiteSpace(ExtUrl), ExtUrl, TargetProject.Website)
                        End If
                    Else
                        Item.DownloadUrls = New List(Of String) From {
                            $"https://api.spiget.org/v2/resources/{RawId}/versions/{VerId}/download",
                            $"https://api.spiget.org/v2/resources/{RawId}/download"
                        }
                        Item.ExternalFallbackUrl = TargetProject.Website
                    End If
                    If Item.DownloadAvailable Then Parsed.Add(Item)
                Next
                Parsed = Parsed.DistinctBy(Function(a) a.Id).ToList()
            Else
                Dim ResultJsonArray As JArray = Nothing
                If Platform = ResourcePlatforms.CurseForge Then
                    For RetryCount As Integer = 0 To 2
                        Dim ResultJson As JObject = DlModRequest($"https://api.curseforge.com/v1/mods/{ProjectId}/files?pageSize=" &
                        (10000 + RetryCount)) '每次重试多请求一个文件，以避免触发 CDN 缓存
                        If ResultJson("pagination")("resultCount").ToObject(Of Integer) = ResultJson("pagination")("totalCount").ToObject(Of Integer) Then
                            ResultJsonArray = ResultJson("data")
                            Exit For
                        ElseIf RetryCount < 2 Then
                            Logger.Warn($"CurseForge 返回的资源版本列表存在缺失，即将进行第 {RetryCount + 1} 次重试") '#6224
                            Logger.Info($"返回的原始内容如下：{vbCrLf}{ResultJson}")
                        Else
                            Logger.Info($"CurseForge 返回的资源版本列表存在缺失，返回的原始内容如下：{vbCrLf}{ResultJson}")
                            Throw New Exception("CurseForge 返回的资源版本列表存在缺失")
                        End If
                    Next
                Else 'Modrinth
                    ResultJsonArray = DlModRequest($"https://api.modrinth.com/v2/project/{ProjectId}/version?include_changelog=false")
                End If
                Parsed = ResultJsonArray.Select(Function(a) FromPlatformJson(a, TargetProject.Types)).
                    Where(Function(a) a.DownloadAvailable).
                    DistinctBy(Function(a) a.Id).ToList 'CurseForge 可能会重复返回相同项（#1330）
            End If
            Result = ProjectFilesCache.GetOrAdd(ProjectId, Parsed)
        End If
        If Not LoadDependencies Then Return Result
        '加载前置信息
        Dim Dependencies As List(Of String) = Result.SelectMany(Function(f) f.RawDependencies).Distinct().ToList
        If Dependencies.Any Then
            Logger.Info($"项目 {ProjectId} 中需要加载的前置：{Dependencies.Join("，")}")
            Dim DepProjects = ResourceProject.FromProjectIds(Dependencies, TargetProject.Platform)
            SyncLock Result
                For Each DependencyProject In DepProjects
                    For Each File In Result
                        If File.RawDependencies.Contains(DependencyProject.Id) AndAlso DependencyProject.Id <> ProjectId AndAlso Not File.Dependencies.Contains(DependencyProject.Id) Then
                            File.Dependencies.Add(DependencyProject.Id)
                        End If
                    Next
                Next
            End SyncLock
        End If
        Return Result
    End Function
    ''' <summary>
    ''' 项目 ID 对应的版本信息的缓存。不会保存到文件。
    ''' </summary>
    Protected Friend Shared ProjectFilesCache As New ConcurrentDictionary(Of String, List(Of ResourceVersion))

    '序列化

    ''' <summary>
    ''' 从缓存 JSON 读取字段数据。
    ''' </summary>
    Public Shared Function FromCacheJson(Data As JObject) As ResourceVersion
        Dim Result As New ResourceVersion
        With Result
            .ResourceType = Data("ResourceType").ToObject(Of ResourceTypes)
            .Id = Data("Id").ToString
            .Display = Data("DisplayName").ToString
            If Data.ContainsKey("Version") Then .Version = Data("Version").ToString
            .ReleaseDate = Data("ReleaseDate").ToObject(Of Date)
            .DownloadCount = Data("DownloadCount").ToObject(Of Integer)
            .ReleaseType = Data("ReleaseType").ToObject(Of ReleaseTypes)
            If Data.ContainsKey("FileName") Then .FileName = Data("FileName").ToString
            If Data.ContainsKey("DownloadUrls") Then .DownloadUrls = Data("DownloadUrls").ToObject(Of List(Of String))
            If Data.ContainsKey("ExternalOnlyUrl") Then .ExternalOnlyUrl = Data("ExternalOnlyUrl").ToString
            If Data.ContainsKey("ExternalFallbackUrl") Then .ExternalFallbackUrl = Data("ExternalFallbackUrl").ToString
            If Data.ContainsKey("ModLoaders") Then .ModLoaders = Data("ModLoaders").ToObject(Of ModLoaders)
            If Data.ContainsKey("Size") Then .Size = Data("Size").ToObject(Of Integer)
            If Data.ContainsKey("Hash") Then .Hash = Data("Hash").ToString
            If Data.ContainsKey("GameVersions") Then .GameVersions = Data("GameVersions").ToObject(Of List(Of String))
            If Data.ContainsKey("RawDependencies") Then .RawDependencies = Data("RawDependencies").ToObject(Of List(Of String))
            If Data.ContainsKey("Dependencies") Then .Dependencies = Data("Dependencies").ToObject(Of List(Of String))
        End With
        Return Result
    End Function
    ''' <summary>
    ''' 转为缓存 JSON。
    ''' </summary>
    Public Function ToCacheJson() As JObject
        Dim Result As New JObject
        With Result
            .Add("ResourceType", CInt(ResourceType))
            .Add("Id", Id)
            If Version IsNot Nothing Then .Add("Version", Version)
            .Add("DisplayName", Display)
            .Add("ReleaseDate", ReleaseDate)
            .Add("DownloadCount", DownloadCount)
            .Add("ModLoaders", CInt(ModLoaders))
            .Add("GameVersions", New JArray(GameVersions))
            .Add("ReleaseType", CInt(ReleaseType))
            If FileName IsNot Nothing Then .Add("FileName", FileName)
            If DownloadUrls IsNot Nothing Then .Add("DownloadUrls", New JArray(DownloadUrls))
            If ExternalOnlyUrl IsNot Nothing Then .Add("ExternalOnlyUrl", ExternalOnlyUrl)
            If ExternalFallbackUrl IsNot Nothing Then .Add("ExternalFallbackUrl", ExternalFallbackUrl)
            If Hash IsNot Nothing Then .Add("Hash", Hash)
            If Size >= 0 Then .Add("Size", Size)
            .Add("RawDependencies", New JArray(RawDependencies))
            .Add("Dependencies", New JArray(Dependencies))
        End With
        Return Result
    End Function

    '杂项

    ''' <summary>
    ''' 转为列表项。
    ''' </summary>
    Public Function ToListItem(ClickHandler As MyListItem.ClickEventHandler, Optional SaveAsButtonHandler As MyIconButton.ClickEventHandler = Nothing,
                               Optional IsBadDisplay As Boolean = False, Optional ActionButtonToolTip As String = "另存为") As MyVirtualizingElement(Of MyListItem)
        Return New MyVirtualizingElement(Of MyListItem)(
        Function()
            Dim Result As New MyListItem
            With Result

                '基础属性
                .SnapsToDevicePixels = True
                .Height = 42
                .Type = MyListItem.CheckType.Clickable
                .Tag = Me
                .Title = If(IsBadDisplay, FileName, Display).BeforeLastOfAny({".zip", ".jar", ".mrpack", ".litemod"}, True)
                .Logo = $"{PathImage}ReleaseTypes/{ReleaseType}.png"
                AddHandler .Click, ClickHandler
                '描述
                Dim IsEn = LocalizationCore.IsEnglishActive()
                .Info =
                Iterator Function()
                    If ModLoaders <> ModLoaders.None Then Yield ModLoaders.Flags.Join(If(IsEn, ", ", "、"))
                    If Not String.IsNullOrEmpty(ExternalOnlyUrl) Then Yield LocalizationCore.TranslateText("需在网页下载")
                    If .Title <> PathUtils.GetFileNameWithoutExtension(FileName) Then Yield PathUtils.GetFileNameWithoutExtension(FileName)
                    If Dependencies.Any Then Yield If(IsEn, $"{Dependencies.Count} dep(s)", $"{Dependencies.Count} 项前置")
                    If GameVersions.All(Function(v) Not v.Contains(".") OrElse {"w", "snapshot", "rc", "pre", "experimental", "-"}.Any(Function(s) v.ContainsIgnoreCase(s))) Then
                        Yield If(IsEn, $"MC {GameVersions.Join(", ")}", $"游戏版本 {GameVersions.Join("、"c)}")
                    End If
                    If DownloadCount > 0 Then 'CurseForge 的下载次数经常错误地返回 0
                        Yield If(IsEn, $"{ResourceProject.FormatCompactDownloadCount(DownloadCount)} downloads", $"下载 {ResourceProject.FormatCompactDownloadCount(DownloadCount)} 次")
                    End If
                    Dim TimeStr = LocalizationCore.TranslateText(StringUtils.FormatTimeSpan(ReleaseDate - Date.Now, False))
                    Yield If(IsEn, $"Updated {TimeStr}", $"更新于 {TimeStr}")
                    If ReleaseType <> ReleaseTypes.Release Then Yield ReleaseTypeDisplay
                End Function().Join(If(IsEn, ", ", "，"))

                '附加操作按钮（整合包另存为 / 装入服务端）
                If SaveAsButtonHandler IsNot Nothing Then
                    Dim BtnSave As New MyIconButton With {.Logo = Logo.IconButtonSave, .ToolTip = LocalizationCore.TranslateText(ActionButtonToolTip)}
                    AddHandler BtnSave.Click, SaveAsButtonHandler
                    .Buttons = {BtnSave}
                End If

            End With
            Return Result
        End Function) With {.Height = 42}
    End Function
    ''' <summary>
    ''' 获取下载信息。
    ''' </summary>
    ''' <param name="LocalFileOrFolder">目标本地文件夹，或完整的文件路径。会自动判断类型。</param>
    Public Function ToNetFile(LocalFileOrFolder As String,
            Optional TrackDownloadReason As DownloadReason? = Nothing, Optional TrackGameVersion As String = Nothing, Optional TrackLoader As ModLoaders = ModLoaders.None) As NetFile
        Dim Checker As New FileChecker With {.Hash = Hash, .ActualSize = Size}
        If Size <= 0 AndAlso String.IsNullOrEmpty(Hash) Then Checker.MinSize = 512
        Return New NetFile(
            ParseModrinthTrackArguments(DownloadUrls, TrackDownloadReason, TrackGameVersion, TrackLoader),
            LocalFileOrFolder & If(LocalFileOrFolder.EndsWithF("\"), FileName, ""),
            Checker,
            SimulateBrowserHeaders:=True)
    End Function
    ''' <summary>
    ''' 添加 Modrinth 追踪参数。
    ''' </summary>
    Public Shared Iterator Function ParseModrinthTrackArguments(Urls As List(Of String),
            Optional TrackDownloadReason As DownloadReason? = Nothing, Optional TrackGameVersion As String = Nothing, Optional TrackLoader As ModLoaders = ModLoaders.None) As IEnumerable(Of String)
        Dim Arguments As New Dictionary(Of String, String)
        If TrackDownloadReason IsNot Nothing Then Arguments.Add("mr_download_reason", TrackDownloadReason.ToString.Lower)
        If TrackGameVersion IsNot Nothing Then Arguments.Add("mr_game_version", TrackGameVersion)
        If TrackLoader.Flags.Any Then Arguments.Add("mr_loader", TrackLoader.Flags.First.ToString.Lower)
        Dim Argument = Arguments.Select(Function(a) $"{a.Key}={a.Value}").Join("&")
        For Each Url In Urls
            If Not Url.ContainsIgnoreCase("cdn.modrinth.com/") Then
                Yield Url
                Continue For
            End If
            Yield $"{Url}{If(Url.Contains("?"), "&", "?")}{Argument}"
        Next
    End Function
    Public Enum DownloadReason
        Standalone
        Dependency
        ModPack
        Update
    End Enum
    ''' <summary>
    ''' 重新整理 CurseForge 的下载地址。
    ''' </summary>
    Public Shared Function ParseCurseForgeDownloadUrls(Url As String) As List(Of String)
        Return {
            Url.Replace("-service.overwolf.wtf", ".forgecdn.net").Replace("://edge.", "://mediafilez.").Replace("://media.", "://mediafilez."),
            Url.Replace("://edge.", "://mediafilez.").Replace("://media.", "://mediafilez."),
            Url.Replace("-service.overwolf.wtf", ".forgecdn.net"),
            Url.Replace("://media.", "://edge."),
            Url
        }.Distinct.ToList
    End Function
    Public Overrides Function ToString() As String
        Return $"{Id}: {FileName}"
    End Function

End Class
