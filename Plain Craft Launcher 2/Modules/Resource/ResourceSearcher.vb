Public Class ResourceSearcher
    Private Const RESULT_PAGE_SIZE = 40
    Private Const MSG_NO_CHINESE_RESULT As String = "无搜索结果，请尝试搜索其英文名称"

    '输入
    Public Class SearchRequest

        '结果要求

        ''' <summary>
        ''' 结果存储器，会从中获取需加载的页面等。
        ''' 加载后也应输出到此处。
        ''' </summary>
        Public Storage As SearchResult
        ''' <summary>
        ''' 应当尽量达成的结果数量。
        ''' </summary>
        Public TargetResultCount As Integer
        ''' <summary>
        ''' 根据加载位置记录，是否还可以继续获取内容。
        ''' </summary>
        Public ReadOnly Property CanContinue As Boolean
            Get
                If Tag.StartsWithF("/") OrElse Not Sources.HasFlagF(ResourcePlatforms.CurseForge) OrElse
                   (Type = ResourceTypes.Plugin AndAlso PluginLoaders = "velocity" AndAlso Sources <> ResourcePlatforms.CurseForge) Then Storage.CurseForgeTotal = 0
                If Type = ResourceTypes.Map OrElse Tag.EndsWithF("/") OrElse Not Sources.HasFlagF(ResourcePlatforms.Modrinth) Then Storage.ModrinthTotal = 0
                If Type <> ResourceTypes.Plugin OrElse Not Sources.HasFlagF(ResourcePlatforms.Hangar) OrElse
                   (Tag <> "" AndAlso GetHangarCategory(Tag) = "" AndAlso Sources <> ResourcePlatforms.Hangar) Then Storage.HangarTotal = 0
                If Type <> ResourceTypes.Plugin OrElse Not Sources.HasFlagF(ResourcePlatforms.SpigotMC) OrElse
                   (PluginLoaders = "velocity" AndAlso Sources <> ResourcePlatforms.SpigotMC) OrElse
                   (Tag <> "" AndAlso GetSpigetCategoryId(Tag) = "" AndAlso Sources <> ResourcePlatforms.SpigotMC) Then Storage.SpigotHasMore = False
                If Sources.HasFlagF(ResourcePlatforms.CurseForge) AndAlso (Storage.CurseForgeTotal = -1 OrElse Storage.CurseForgeOffset < Storage.CurseForgeTotal) Then Return True
                If Type <> ResourceTypes.Map AndAlso Sources.HasFlagF(ResourcePlatforms.Modrinth) AndAlso (Storage.ModrinthTotal = -1 OrElse Storage.ModrinthOffset < Storage.ModrinthTotal) Then Return True
                If Type = ResourceTypes.Plugin AndAlso Sources.HasFlagF(ResourcePlatforms.Hangar) AndAlso (Storage.HangarTotal = -1 OrElse Storage.HangarOffset < Storage.HangarTotal) Then Return True
                If Type = ResourceTypes.Plugin AndAlso Sources.HasFlagF(ResourcePlatforms.SpigotMC) AndAlso Storage.SpigotHasMore Then Return True
                Return False
            End Get
        End Property

        '输入内容

        ''' <summary>
        ''' 筛选资源种类。
        ''' </summary>
        Public Type As ResourceTypes
        ''' <summary>
        ''' 筛选资源标签。空字符串代表不限制。格式例如 "406/worldgen"，分别是 CurseForge 和 Modrinth 的 ID。
        ''' </summary>
        Public Tag As String = ""
        ''' <summary>
        ''' 筛选 Mod 加载器类别。
        ''' <see cref="ModLoaders.None"/> 代表不筛选。
        ''' 对于 Modrinth 请求，支持多个枚举值。
        ''' </summary>
        Public ModLoaders As ModLoaders = ModLoaders.None
        ''' <summary>
        ''' 筛选 MC 版本。
        ''' </summary>
        Public GameVersion As String = Nothing
        ''' <summary>
        ''' 搜索的初始内容。
        ''' </summary>
        Public SearchText As String = Nothing
        ''' <summary>
        ''' 筛选服务端插件平台（如 "" 代表 paper/purpur，"paper"、"purpur"）。
        ''' </summary>
        Public PluginLoaders As String = ""
        ''' <summary>
        ''' 允许的来源。
        ''' </summary>
        Public Sources As ResourcePlatforms = ResourcePlatforms.Any
        Public Sub New(Type As ResourceTypes, Storage As SearchResult, TargetResultCount As Integer)
            Me.Type = Type
            Me.Storage = Storage
            Me.TargetResultCount = TargetResultCount
        End Sub

        '构造请求

        ''' <summary>
        ''' 获取对应的 CurseForge API 请求链接。
        ''' </summary>
        Public Function GetCurseForgeAddress(SearchText As String, IgnoreModLoaderFilter As Boolean) As String
            If Tag.StartsWithF("/") Then Storage.CurseForgeTotal = 0
            '应用筛选参数
            Dim Address As String = $"https://api.curseforge.com/v1/mods/search?gameId=432&sortField=2&sortOrder=desc&pageSize={RESULT_PAGE_SIZE}"
            Select Case Type
                Case ResourceTypes.Mod
                    Address += "&classId=6"
                Case ResourceTypes.ModPack
                    Address += "&classId=4471"
                Case ResourceTypes.DataPack
                    Address += "&classId=6945"
                Case ResourceTypes.Shader
                    Address += "&classId=6552"
                Case ResourceTypes.ResourcePack
                    Address += "&classId=12"
                Case ResourceTypes.Plugin
                    Address += "&classId=5"
                Case ResourceTypes.Map
                    Address += "&classId=17"
            End Select
            If Tag <> "" Then Address += "&categoryId=" & Tag.BeforeFirst("/")
            If Type <> ResourceTypes.Plugin AndAlso Type <> ResourceTypes.Map AndAlso ModLoaders <> ModLoaders.None AndAlso Not IgnoreModLoaderFilter Then Address += "&modLoaderType=" & Resource.ToCurseForgeModLoaderType(ModLoaders.Flags.First)
            If Not String.IsNullOrEmpty(GameVersion) Then Address += "&gameVersion=" & GameVersion
            If Not String.IsNullOrEmpty(SearchText) Then Address += "&searchFilter=" & StringUtils.UrlEscape(SearchText)
            If Storage.CurseForgeOffset > 0 Then Address += "&index=" & Storage.CurseForgeOffset
            Return Address
        End Function
        ''' <summary>
        ''' 获取对应的 Modrinth API 请求链接。
        ''' </summary>
        Public Function GetModrinthAddress(SearchText As String, IgnoreModLoaderFilter As Boolean) As String
            If Tag.EndsWithF("/") Then Storage.ModrinthTotal = 0
            '应用筛选参数
            Dim Address As String = $"https://api.modrinth.com/v2/search?limit={RESULT_PAGE_SIZE}&index=relevance"
            If Not String.IsNullOrEmpty(SearchText) Then Address += "&query=" & StringUtils.UrlEscape(SearchText)
            If Storage.ModrinthOffset > 0 Then Address += "&offset=" & Storage.ModrinthOffset
            'facets=[["categories:'game-mechanics'"],["categories:forge","categories:fabric"],["versions:1.19.3"],["project_type:mod"]]
            Dim Facets As New List(Of String)
            If Type = ResourceTypes.Plugin Then
                Facets.Add("[""project_type:plugin""]")
                If PluginLoaders = "paper" Then
                    Facets.Add("[""categories:paper""]")
                ElseIf PluginLoaders = "purpur" Then
                    Facets.Add("[""categories:purpur""]")
                ElseIf PluginLoaders = "velocity" Then
                    Facets.Add("[""categories:velocity""]")
                Else
                    Facets.Add("[""categories:paper"",""categories:purpur"",""categories:spigot"",""categories:velocity""]")
                End If
            Else
                Facets.Add($"[""project_type:{Type.ToString.Lower}""]")
            End If
            If Not String.IsNullOrEmpty(Tag) Then Facets.Add($"[""categories:'{Tag.AfterLast("/")}'""]")
            If Type <> ResourceTypes.Plugin AndAlso ModLoaders <> ModLoaders.None AndAlso Not IgnoreModLoaderFilter Then
                Facets.Add($"[""categories:{ModLoaders.Flags.Select(Function(f) f.ToString.Lower).Join(""",""categories:")}""]")
            End If
            If Not String.IsNullOrEmpty(GameVersion) Then Facets.Add($"[""versions:'{GameVersion}'""]")
            Address += "&facets=[" & String.Join(",", Facets) & "]"
            Return Address
        End Function
        Public Shared Function GetHangarCategory(Tag As String) As String
            Select Case Tag
                Case "115/management" : Return "admin_tools"
                Case "123/economy" : Return "economy"
                Case "124/world-management" : Return "world_management"
                Case "129/game-mechanics" : Return "gameplay"
                Case "117/chat" : Return "chat"
                Case "127/utility" : Return "misc"
                Case "125/optimization" : Return "admin_tools"
                Case "126/minigame" : Return "games"
                Case "132/adventure" : Return "role_playing"
                Case "116/" : Return "protection"
                Case "122/", "/library" : Return "dev_tools"
                Case Else : Return ""
            End Select
        End Function
        Public Shared Function GetSpigetCategoryId(Tag As String) As String
            Select Case Tag
                Case "115/management", "127/utility", "125/optimization" : Return "7"
                Case "123/economy" : Return "23"
                Case "124/world-management" : Return "18"
                Case "129/game-mechanics" : Return "22"
                Case "117/chat" : Return "14"
                Case "126/minigame" : Return "24"
                Case "132/adventure" : Return "17"
                Case "134/" : Return "5"
                Case "122/", "/library" : Return "26"
                Case "130/" : Return "27"
                Case Else : Return ""
            End Select
        End Function
        ''' <summary>
        ''' 获取对应的 Hangar (PaperMC) API 请求链接。
        ''' </summary>
        Public Function GetHangarAddress(SearchText As String) As String
            Dim Address As String = $"https://hangar.papermc.io/api/v1/projects?limit={RESULT_PAGE_SIZE}&offset={Storage.HangarOffset}&sort=-downloads"
            If Not String.IsNullOrEmpty(SearchText) Then Address += "&q=" & StringUtils.UrlEscape(SearchText)
            If PluginLoaders = "paper" OrElse PluginLoaders = "purpur" Then
                Address += "&platform=PAPER"
            ElseIf PluginLoaders = "velocity" Then
                Address += "&platform=VELOCITY"
            End If
            If Not String.IsNullOrEmpty(GameVersion) Then Address += "&version=" & StringUtils.UrlEscape(GameVersion)
            Dim Cat = GetHangarCategory(Tag)
            If Cat <> "" Then Address += "&category=" & Cat
            Return Address
        End Function
        ''' <summary>
        ''' 获取对应的 SpigotMC (Spiget v2) API 请求链接。
        ''' </summary>
        Public Function GetSpigetAddress(SearchText As String) As String
            If Not String.IsNullOrEmpty(SearchText) Then
                Return $"https://api.spiget.org/v2/search/resources/{StringUtils.UrlEscape(SearchText)}?size={RESULT_PAGE_SIZE}&page={Storage.SpigotPage}&sort=-downloads"
            End If
            Dim CatId = GetSpigetCategoryId(Tag)
            If CatId <> "" Then
                Return $"https://api.spiget.org/v2/categories/{CatId}/resources?size={RESULT_PAGE_SIZE}&page={Storage.SpigotPage}&sort=-downloads"
            End If
            Return $"https://api.spiget.org/v2/resources?size={RESULT_PAGE_SIZE}&page={Storage.SpigotPage}&sort=-downloads"
        End Function

        '相同判断
        Public Overrides Function Equals(obj As Object) As Boolean
            Dim request = TryCast(obj, SearchRequest)
            Return request IsNot Nothing AndAlso
                Type = request.Type AndAlso TargetResultCount = request.TargetResultCount AndAlso
                Tag = request.Tag AndAlso ModLoaders = request.ModLoaders AndAlso Sources = request.Sources AndAlso
                PluginLoaders = request.PluginLoaders AndAlso
                GameVersion = request.GameVersion AndAlso SearchText = request.SearchText
        End Function
        Public Shared Operator =(left As SearchRequest, right As SearchRequest) As Boolean
            Return EqualityComparer(Of SearchRequest).Default.Equals(left, right)
        End Operator
        Public Shared Operator <>(left As SearchRequest, right As SearchRequest) As Boolean
            Return Not left = right
        End Operator
        Public Overrides Function GetHashCode() As Integer
            Return (Type, Tag, ModLoaders, PluginLoaders, GameVersion, SearchText, Sources).GetHashCode()
        End Function

    End Class

    '输出
    Public Class SearchResult

        '加载位置记录

        Public CurseForgeOffset As Integer = 0
        Public CurseForgeTotal As Integer = -1

        Public ModrinthOffset As Integer = 0
        Public ModrinthTotal As Integer = -1

        Public HangarOffset As Integer = 0
        Public HangarTotal As Integer = -1

        Public SpigotPage As Integer = 1
        Public SpigotHasMore As Boolean = True

        '结果列表

        ''' <summary>
        ''' 可供展示的所有工程的列表。
        ''' </summary>
        Public Results As New List(Of ResourceProject)
        ''' <summary>
        ''' 当前的错误信息。如果没有则为 Nothing。
        ''' </summary>
        Public ErrorMessage As String = Nothing

    End Class

    ''' <summary>
    ''' 根据搜索请求获取一系列的工程列表。
    ''' </summary>
    Private Shared Function IsHttp404Exception(Ex As Exception) As Boolean
        Dim Cur = Ex
        While Cur IsNot Nothing
            Dim WebEx = TryCast(Cur, WebException)
            Dim Resp = TryCast(WebEx?.Response, HttpWebResponse)
            If Resp IsNot Nothing AndAlso Resp.StatusCode = HttpStatusCode.NotFound Then Return True
            Cur = Cur.InnerException
        End While
        Dim Msg = Ex.ToString()
        Return Msg.IndexOf("(404)", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
               Msg.IndexOf("404 Not Found", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
               Msg.IndexOf("Status Code: 404", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
               Msg.IndexOf("NotFound", StringComparison.OrdinalIgnoreCase) >= 0
    End Function

    Public Shared Function GetLoader(InputDelegate As Func(Of SearchRequest)) As LoaderTask(Of SearchRequest, Integer)
        Return New LoaderTask(Of SearchRequest, Integer)("社区资源获取", AddressOf Run, InputDelegate) With {.ReloadTimeout = 60 * 1000}
    End Function
    Private Shared Sub Run(Task As LoaderTask(Of SearchRequest, Integer))
        Dim Request As SearchRequest = Task.Input
        Dim Storage = Request.Storage '避免其他线程对 Request.Storage 重新进行了赋值

#Region "前置检查"

        If Storage.Results.Count >= Request.TargetResultCount Then
            Logger.Info($"已有 {Storage.Results.Count} 个结果，多于所需的 {Request.TargetResultCount} 个结果，结束处理")
            Return
        ElseIf Not Request.CanContinue Then
            If Not Storage.Results.Any() Then
                Throw New Exception("没有符合条件的结果")
            Else
                Logger.Info($"已有 {Storage.Results.Count} 个结果，少于所需的 {Request.TargetResultCount} 个结果，但无法继续获取，结束处理")
                Return
            End If
        End If

#End Region

#Region "中文搜索"

        Dim RawSearchText As String = If(Request.SearchText, "").Trim
        RawSearchText = RawSearchText.Lower
        Logger.Info($"工程列表搜索原始文本：{RawSearchText}")
        RawSearchText = StrConv(RawSearchText, VbStrConv.SimplifiedChinese) '繁体转简体

        Dim IsChineseSearch As Boolean =
            RawSearchText.RegexCheck("[\u4e00-\u9fbb]") AndAlso Not String.IsNullOrEmpty(RawSearchText) AndAlso
            (Request.Type = ResourceTypes.Mod OrElse Request.Type = ResourceTypes.DataPack) '目前仅对 Mod 和数据包进行中文搜索，注意整合包的名称可能已经有中文了
        Dim CurseForgeAltSearchText As String = Nothing, ModrinthAltSearchText As String = Nothing, ModrinthSlugs As New List(Of String) '从中文转为英文的替代搜索内容
        If IsChineseSearch Then
            '帮助方法：从搜索项提取可能的英文单词
            Dim ExtractWords =
            Function(Result As SearchEntry(Of WikiEntry), Source As ResourcePlatforms) As IEnumerable(Of String)
                '从各个可能的来源提取候选
                Dim Candidates As New List(Of String)
                If Result.Item.Slugs.ContainsKey(Source) Then
                    Candidates.Add(Result.Item.Slugs(Source).
                                   Replace("-", " ").Replace("/", " "))
                End If
                If Result.Item.ChineseName IsNot Nothing Then
                    Candidates.Add(Result.Item.ChineseName.
                                   AfterLast(" (").TrimEnd(") ").BeforeFirst(" - ").Replace("-", " ").Replace("/", " ").Replace(":", " ").Replace("(", " ").Replace(")", ""))
                End If
                '分词、清洗、去重
                Candidates = Candidates.
                    SelectMany(Function(c) c.Split(" ")).
                    Select(Function(w) w.TrimStart("{[(").TrimEnd("}])").Lower).
                    Where(
                    Function(w)
                        If w.Length <= 1 Then Return False '单字或空白
                        If {"the", "of", "mod", "and", "forge", "fabric", "for", "quilt", "neoforge"}.Contains(w) Then Return False '常见词
                        If Val(w) > 0 Then Return False '数字
                        If Not w.IsAsciiOnly Then Return False '非纯英文
                        Return True
                    End Function).Distinct.ToList
                '如果一个词可以由其他词拼成，则去掉这个词（例如将 ender io enderio 的 enderio 剔除，只保留 ender io）
                Dim CanForm As Func(Of String, Boolean) = Nothing
                CanForm = Function(s) Candidates.Contains(s) OrElse Candidates.Any(Function(c) s.StartsWith(c) AndAlso CanForm(s.Substring(c.Length)))
                Candidates = Candidates.Where(Function(w) Not Candidates.Any(Function(c) c.Length < w.Length AndAlso w.StartsWith(c) AndAlso CanForm(w.Substring(c.Length)))).ToList()
                Return Candidates
            End Function
            Dim GetSearchEntries =
            Iterator Function(Source As ResourcePlatforms)
                For Each Entry In WikiEntry.All.Value
                    If Not Entry.Slugs.ContainsKey(Source) Then Continue For
                    If Entry.ChineseName IsNot Nothing Then
                        Yield New SearchEntry(Of WikiEntry) With {.Item = Entry, .SearchSource = New List(Of SearchSource) From {
                            New SearchSource(StrConv(Entry.ChineseName.BeforeFirst(" ("), VbStrConv.SimplifiedChinese).Split("/"c, True), 1), '部分 Mod 有别名
                            New SearchSource(StrConv(Entry.ChineseName.AfterFirst(" ("), VbStrConv.SimplifiedChinese) & Entry.Slugs(Source), 0.5)
                        }}
                    Else
                        Yield New SearchEntry(Of WikiEntry) With {.Item = Entry, .SearchSource = New List(Of SearchSource) From {
                            New SearchSource(Entry.Slugs(Source), 0.5)
                        }}
                    End If
                Next
            End Function
            'CurseForge
            If Request.Sources.HasFlagF(ResourcePlatforms.CurseForge) Then
                '数据库搜索
                Static CurseForgeSearchEntries As List(Of SearchEntry(Of WikiEntry)) = GetSearchEntries(ResourcePlatforms.CurseForge).ToList()
                Dim CurseForgeSearchResults = Search(CurseForgeSearchEntries, RawSearchText, 100, 0.25)
                If CurseForgeSearchResults.Any Then
                    '选取目标（CurseForge 要求每个词都必须匹配上，所以只能选择一个 Mod 进行搜索）
                    Dim CurseForgeTarget =
                        If(CurseForgeSearchResults.First.AbsoluteRight,
                            CurseForgeSearchResults.Where(Function(s) s.AbsoluteRight).ToList, '优先使用所有完全匹配的
                            CurseForgeSearchResults.MaxByAll(Function(s) s.Similarity)). '其次使用所有相似度最高的
                        MaxBy(Function(s) s.Item.Popularity) '然后从中选择最受欢迎的那一个
                    '后处理
                    CurseForgeAltSearchText = ExtractWords(CurseForgeTarget, ResourcePlatforms.CurseForge).Join(" ")
                    Logger.Warn($"中文搜索关键词（CurseForge）：{CurseForgeAltSearchText}")
                End If
            End If
            'Modrinth
            If Request.Sources.HasFlagF(ResourcePlatforms.Modrinth) Then
                '数据库搜索
                Static ModrinthSearchEntries As List(Of SearchEntry(Of WikiEntry)) = GetSearchEntries(ResourcePlatforms.Modrinth).ToList()
                Dim ModrinthSearchResults = Search(ModrinthSearchEntries, RawSearchText, 100, 0.25)
                If ModrinthSearchResults.Any Then
                    '分词
                    Dim WordWeights As New Dictionary(Of String, Double) '各个单词及其出现的权重
                    For Each Result In ModrinthSearchResults
                        For Each Word In ExtractWords(Result, ResourcePlatforms.Modrinth)
                            If Not WordWeights.ContainsKey(Word) Then WordWeights.Add(Word, 0)
                            Dim Similarity = If(Result.SearchSource.Any(Function(s) s.Aliases.Contains(RawSearchText)), 1000, Result.Similarity) '完全匹配为 1000
                            WordWeights(Word) += Similarity * Result.Item.Popularity '权重 += 相似度 * 受欢迎程度
                        Next
                    Next
                    ModrinthAltSearchText = WordWeights.MaxBy(Function(w) w.Value).Key
                    Logger.Warn($"中文搜索关键词（Modrinth）：{ModrinthAltSearchText}")
                    '直接请求工程
                    ModrinthSlugs = ModrinthSearchResults.Take(100).Select(Function(r) r.Item.Slugs(ResourcePlatforms.Modrinth)).ToList
                End If
            End If
            '结束
            If String.IsNullOrEmpty(CurseForgeAltSearchText) AndAlso String.IsNullOrEmpty(ModrinthAltSearchText) AndAlso Not ModrinthSlugs.Any Then
                Throw New Exception(MSG_NO_CHINESE_RESULT)
            End If
        End If
        Task.Progress = 0.05

#End Region

        Dim RealResults As New List(Of ResourceProject)
        Dim LoopCount As Integer = 0
NextPage:
        LoopCount += 1
        Dim RawResults As New List(Of ResourceProject)

#Region "从 CurseForge、Modrinth、Hangar 与 SpigotMC 获取结果列表，存储于 RawResults"

        Dim WorkThreads As New List(Of Thread)
        Dim Errors As New ConcurrentBag(Of (Ex As Exception, Source As ResourcePlatforms))

        '在 1.14-，部分老 Mod 没有设置支持的加载器，因此添加 Forge 筛选就会出现遗漏
        '所以，在发起请求时不筛选加载器，然后在返回的结果中自行筛除不是 Forge 的 Mod
        Dim IgnoreModLoaderFilter = Request.ModLoaders = ModLoaders.Forge AndAlso McVersion.VersionToDrop(Request.GameVersion) < 140
        Dim SearchResults As New ConcurrentBag(Of ResourceProject)
        Try

            'CurseForge 搜索
            If Request.Sources.HasFlagF(ResourcePlatforms.CurseForge) AndAlso
               Not (Storage.CurseForgeTotal > -1 AndAlso Storage.CurseForgeTotal <= Storage.CurseForgeOffset) AndAlso '剩余的未显示的搜索结果不足
               (Not IsChineseSearch OrElse (IsChineseSearch AndAlso Not String.IsNullOrEmpty(CurseForgeAltSearchText))) Then '如果是中文搜索，就只在有对应搜索关键词的时候才继续
                WorkThreads.Add(RunInNewThread(
                Sub()
                    Try
                        '获取工程列表
                        Dim CurseForgeUrl As String = Request.GetCurseForgeAddress(If(CurseForgeAltSearchText, RawSearchText), IgnoreModLoaderFilter)
                        Logger.Info($"开始 CurseForge 搜索：{CurseForgeUrl}")
                        Dim RequestResult As JObject = DlModRequest(CurseForgeUrl)
                        Dim ProjectList As New List(Of ResourceProject)
                        For Each JsonEntry As JObject In RequestResult("data")
                            Dim Project As New ResourceProject(JsonEntry, ResourcePlatforms.CurseForge)
                            If Request.Type = ResourceTypes.ResourcePack AndAlso Project.Tags.Contains("数据包") Then Continue For 'CurseForge 将一些数据包分类成了资源包
                            If Request.Type <> ResourceTypes.Any AndAlso Not Project.Types.HasFlagF(Request.Type) Then Continue For '过滤分区不匹配的搜索结果（#8265）
                            ProjectList.Add(Project)
                        Next
                        '更新结果
                        ProjectList.ForEach(Sub(p) SearchResults.Add(p))
                        Storage.CurseForgeOffset += RequestResult("data").Count
                        Storage.CurseForgeTotal = RequestResult("pagination")("totalCount").ToObject(Of Integer)
                        Logger.Info($"从 CurseForge 搜索到了 {ProjectList.Count} 个工程（总计已获取 {Storage.CurseForgeOffset} 个，共 {Storage.CurseForgeTotal} 个）")
                    Catch ex As Exception
                        Logger.Warn(ex, "CurseForge 搜索失败")
                        Storage.CurseForgeTotal = -1
                        Errors.Add((ex, ResourcePlatforms.CurseForge))
                    End Try
                    If Task.Progress < 0.75 Then Task.Progress += 0.25 '可能重复加载多页，所以不能直接给够
                End Sub, "CurseForge Search"))
            End If

            'Modrinth 搜索
            If Request.Type <> ResourceTypes.Map AndAlso
               Request.Sources.HasFlagF(ResourcePlatforms.Modrinth) AndAlso
               Not (Storage.ModrinthTotal > -1 AndAlso Storage.ModrinthTotal <= Storage.ModrinthOffset) AndAlso '剩余的未显示的搜索结果不足
               (Not IsChineseSearch OrElse (IsChineseSearch AndAlso Not String.IsNullOrEmpty(ModrinthAltSearchText))) Then '如果是中文搜索，就只在有对应搜索关键词的时候才继续
                WorkThreads.Add(RunInNewThread(
                Sub()
                    Try
                        Dim ModrinthUrl As String = Request.GetModrinthAddress(If(ModrinthAltSearchText, RawSearchText), IgnoreModLoaderFilter)
                        Logger.Info($"开始 Modrinth 搜索：{ModrinthUrl}")
                        Dim RequestResult As JObject = DlModRequest(ModrinthUrl)
                        Dim ProjectList As New List(Of ResourceProject)
                        For Each JsonEntry As JObject In RequestResult("hits")
                            ProjectList.Add(New ResourceProject(JsonEntry, ResourcePlatforms.Modrinth))
                        Next
                        '更新结果
                        ProjectList.ForEach(Sub(p) SearchResults.Add(p))
                        Storage.ModrinthOffset += RequestResult("hits").Count
                        Storage.ModrinthTotal = RequestResult("total_hits").ToObject(Of Integer)
                        Logger.Info($"从 Modrinth 搜索到了 {ProjectList.Count} 个工程（总计已获取 {Storage.ModrinthOffset} 个，共 {Storage.ModrinthTotal} 个）")
                    Catch ex As Exception
                        Logger.Warn(ex, "Modrinth 搜索失败")
                        Storage.ModrinthTotal = -1
                        Errors.Add((ex, ResourcePlatforms.Modrinth))
                    End Try
                    If Task.Progress < 0.75 Then Task.Progress += 0.25 '可能重复加载多页，所以不能直接给够
                End Sub, "Modrinth Search"))
            End If

            'Modrinth 直接获取工程
            If Request.Type <> ResourceTypes.Map AndAlso
               Request.Sources.HasFlagF(ResourcePlatforms.Modrinth) AndAlso
               Not (Storage.ModrinthTotal > -1 AndAlso Storage.ModrinthTotal <= Storage.ModrinthOffset) AndAlso '剩余的未显示的搜索结果不足
               ModrinthSlugs.Any Then '有直接获取的 Slug
                WorkThreads.Add(RunInNewThread(
                Sub()
                    Try
                        Dim ModrinthUrl As String = $"https://api.modrinth.com/v2/projects?ids=[""{ModrinthSlugs.Join(""",""")}""]"
                        Logger.Info($"开始 Modrinth 直接获取：{ModrinthUrl}")
                        Dim ProjectList As New List(Of ResourceProject)
                        For Each JsonEntry As JObject In DlModRequest(ModrinthUrl)
                            Dim Project As New ResourceProject(JsonEntry, ResourcePlatforms.Modrinth)
                            '应用筛选
                            If Request.Type <> ResourceTypes.Any AndAlso Not Project.Types.HasFlagF(Request.Type) Then Continue For
                            If Not String.IsNullOrEmpty(Request.Tag) AndAlso
                                Not JsonEntry("categories").Any(Function(c) c.ToString = Request.Tag.AfterLast("/")) Then Continue For 'Project.Tags 已经转换成中文了，只能从 json 判
                            If Request.ModLoaders <> ModLoaders.None AndAlso Not IgnoreModLoaderFilter AndAlso
                                Not Project.ModLoaders.Flags.Intersect(Request.ModLoaders.Flags).Any() Then Continue For
                            If Not String.IsNullOrEmpty(Request.GameVersion) AndAlso
                                Not Project.UnsafeGameVersions.Any(Function(d) d = Request.GameVersion) Then Continue For
                            ProjectList.Add(Project)
                        Next
                        If ProjectList.Any AndAlso Request.ModLoaders <> ModLoaders.None AndAlso Not IgnoreModLoaderFilter AndAlso
                                Not String.IsNullOrEmpty(Request.GameVersion) Then
                            Dim ProjectIds = ProjectList.Select(Function(p) p.Id).Join(""",""project_id:")
                            Dim Loaders = Request.ModLoaders.Flags.Select(Function(f) f.ToString.Lower).Join(""",""categories:")
                            Dim FilterUrl = $"https://api.modrinth.com/v2/search?limit={ProjectList.Count}&facets=" &
                                $"[[""project_id:{ProjectIds}""],[""categories:{Loaders}""],[""versions:{Request.GameVersion}""]]"
                            Dim MatchedIds = DlModRequest(FilterUrl)("hits").Select(Function(h) h("project_id").ToString).ToList
                            ProjectList.KeepIf(Function(p) MatchedIds.Contains(p.Id))
                        End If
                        '更新结果
                        ProjectList.ForEach(Sub(p) SearchResults.Add(p))
                        Logger.Info($"从 Modrinth 直接获取到了 {ProjectList.Count} 个工程")
                        ModrinthSlugs.Clear() '防止重试/加载下一页时重复获取
                    Catch ex As Exception
                        Logger.Warn(ex, "Modrinth 直接获取失败")
                        Errors.Add((ex, ResourcePlatforms.Modrinth))
                    End Try
                    If Task.Progress < 0.75 Then Task.Progress += 0.25 '可能重复加载多页，所以不能直接给够
                End Sub, "Modrinth Get"))
            End If

            'Hangar 搜索
            If Request.Type = ResourceTypes.Plugin AndAlso
               Request.Sources.HasFlagF(ResourcePlatforms.Hangar) AndAlso
               Not (Storage.HangarTotal > -1 AndAlso Storage.HangarTotal <= Storage.HangarOffset) Then
                WorkThreads.Add(RunInNewThread(
                Sub()
                    Try
                        Dim HangarUrl As String = Request.GetHangarAddress(RawSearchText)
                        Logger.Info($"开始 Hangar 搜索：{HangarUrl}")
                        Dim RequestResult As JObject = DlModRequest(HangarUrl)
                        Dim ResultArr = If(TryCast(RequestResult("result"), JArray), New JArray)
                        Dim ProjectList As New List(Of ResourceProject)
                        For Each JsonEntry As JObject In ResultArr
                            ProjectList.Add(New ResourceProject(JsonEntry, ResourcePlatforms.Hangar))
                        Next
                        ProjectList.ForEach(Sub(p) SearchResults.Add(p))
                        Storage.HangarOffset += ResultArr.Count
                        If ResultArr.Count = 0 Then
                            Storage.HangarTotal = Storage.HangarOffset
                        Else
                            Storage.HangarTotal = If(RequestResult("pagination")?("count") IsNot Nothing, RequestResult("pagination")("count").ToObject(Of Integer), Storage.HangarOffset)
                        End If
                        Logger.Info($"从 Hangar 搜索到了 {ProjectList.Count} 个工程（总计已获取 {Storage.HangarOffset} 个，共 {Storage.HangarTotal} 个）")
                    Catch ex As Exception
                        Logger.Warn(ex, "Hangar 搜索失败")
                        Storage.HangarTotal = -1
                        Errors.Add((ex, ResourcePlatforms.Hangar))
                    End Try
                    If Task.Progress < 0.75 Then Task.Progress += 0.25
                End Sub, "Hangar Search"))
            End If

            'SpigotMC (Spiget v2) 搜索
            If Request.Type = ResourceTypes.Plugin AndAlso
               Request.Sources.HasFlagF(ResourcePlatforms.SpigotMC) AndAlso
               Storage.SpigotHasMore Then
                WorkThreads.Add(RunInNewThread(
                Sub()
                    Try
                        Dim SpigetUrl As String = Request.GetSpigetAddress(RawSearchText)
                        Logger.Info($"开始 SpigotMC 搜索：{SpigetUrl}")
                        Dim JsonTok As JToken = Nothing
                        For TryIdx As Integer = 1 To 2
                            Try
                                JsonTok = NetRequestByClient(SpigetUrl, Timeout:=15000, Encoding:=Encoding.UTF8, RequireJson:=True, MakeLog:=(TryIdx = 1)).DeserializeJson()
                                Exit For
                            Catch ex As Exception
                                If IsHttp404Exception(ex) Then
                                    Storage.SpigotHasMore = False
                                    Logger.Info($"SpigotMC 无匹配结果（HTTP 404）：{SpigetUrl}")
                                    Return
                                End If
                                If TryIdx = 2 Then Throw
                                Thread.Sleep(350)
                            End Try
                        Next
                        Dim ResultArr = If(TryCast(JsonTok, JArray), New JArray)
                        Dim TargetDrop As Integer = If(Not String.IsNullOrEmpty(Request.GameVersion) AndAlso McVersion.IsStandardFormat(Request.GameVersion), McVersion.VersionToDrop(Request.GameVersion), -1)
                        Dim ProjectList As New List(Of ResourceProject)
                        For Each JsonEntry As JObject In ResultArr
                            Dim Project As New ResourceProject(JsonEntry, ResourcePlatforms.SpigotMC)
                            If TargetDrop > 0 AndAlso Project.Drops.Any() AndAlso Not Project.Drops.Contains(TargetDrop) Then Continue For
                            ProjectList.Add(Project)
                        Next
                        ProjectList.ForEach(Sub(p) SearchResults.Add(p))
                        Storage.SpigotPage += 1
                        Storage.SpigotHasMore = (ResultArr.Count >= RESULT_PAGE_SIZE)
                        Logger.Info($"从 SpigotMC 搜索到了 {ProjectList.Count} 个工程（已翻至第 {Storage.SpigotPage - 1} 页，是否还有更多：{Storage.SpigotHasMore}）")
                    Catch ex As Exception
                        Logger.Warn(ex, "SpigotMC 搜索失败")
                        Errors.Add((ex, ResourcePlatforms.SpigotMC))
                    End Try
                    If Task.Progress < 0.75 Then Task.Progress += 0.25
                End Sub, "SpigotMC Search"))
            End If

            '等待线程结束
            For Each Thread In WorkThreads
                Thread.Join()
                If Task.IsCanceled Then Return '会自动触发 Finally 以清理线程
            Next
            RawResults = New List(Of ResourceProject)(SearchResults)

            '仅保留兼容 Forge 的 Mod，或老版本中没有标注任何 Mod Loader 的 Mod
            If IgnoreModLoaderFilter Then
                RawResults.KeepIf(Function(p) p.ModLoaders = ModLoaders.None OrElse p.ModLoaders.HasFlagF(ModLoaders.Forge))
            End If

            '确保存在结果
            Storage.ErrorMessage = Nothing
            If Not RawResults.Any() Then
                If Errors.Any() Then
                    Throw Errors.First.Ex
                Else
                    If IsChineseSearch AndAlso Not (Request.Type = ResourceTypes.Mod OrElse Request.Type = ResourceTypes.DataPack) Then
                        Throw New Exception(MSG_NO_CHINESE_RESULT)
                    ElseIf Request.Sources = ResourcePlatforms.CurseForge AndAlso Request.Tag.StartsWithF("/") Then
                        Throw New Exception("CurseForge 不兼容所选的类型")
                    ElseIf Request.Sources = ResourcePlatforms.Modrinth AndAlso Request.Tag.EndsWithF("/") Then
                        Throw New Exception("Modrinth 不兼容所选的类型")
                    Else
                        Throw New Exception("没有搜索结果")
                    End If
                End If
            ElseIf Errors.Any() Then
                '有结果但是有错误
                Dim FailedNames = Errors.Select(Function(e) If(e.Source = ResourcePlatforms.SpigotMC, "SpigotMC", e.Source.ToString())).
                    Distinct().Join("、")
                Storage.ErrorMessage = $"无法连接到 {FailedNames}，目前仅显示其他可用来源的内容，搜索结果可能不全。{vbCrLf}请稍后再试，或检查网络环境。"
            End If

        Finally
            For Each Thread In WorkThreads
                If Thread.IsAlive Then Thread.Interrupt()
            Next
        End Try

#End Region

#Region "提取非重复项，存储于 RealResults"

        '按 CurseForge -> Modrinth -> Hangar -> SpigotMC 固定顺序排列，避免加载结束顺序不同导致排名与去重不同
        Dim GetPlatformPriority =
        Function(P As ResourcePlatforms) As Integer
            Select Case P
                Case ResourcePlatforms.CurseForge : Return 0
                Case ResourcePlatforms.Modrinth : Return 1
                Case ResourcePlatforms.Hangar : Return 2
                Case Else : Return 3
            End Select
        End Function
        RawResults = RawResults.OrderBy(Function(x) GetPlatformPriority(x.Platform)).ToList
        '去重
        RawResults = RawResults.Distinct(Function(a, b) a.IsLike(b)).ToList
        '已有内容去重
        RawResults = RawResults.Where(Function(r) Not RealResults.Any(Function(b) r.IsLike(b)) AndAlso
                                                  Not Storage.Results.Any(Function(b) r.IsLike(b))).ToList
        '加入列表
        RealResults.AddRange(RawResults)
        Logger.Info($"去重、筛选后累计新增结果 {RealResults.Count} 个（目前已有结果 {Storage.Results.Count} 个）")

#End Region

#Region "检查结果数量，如果不足且可继续，会继续加载下一页"

        If RealResults.Count + Storage.Results.Count < Request.TargetResultCount Then
            Logger.Info($"总结果数需求最少 {Request.TargetResultCount} 个，仅获得了 {RealResults.Count + Storage.Results.Count} 个")
            If Request.CanContinue AndAlso Not Errors.Any AndAlso LoopCount < 5 Then '如果有某个源失败则不再重试，且限制单次翻页深度
                Logger.Info("将继续尝试加载下一页")
                GoTo NextPage
            Else
                Logger.Info("无法继续加载，将强制结束")
            End If
        End If

#End Region

#Region "将结果排序并添加"

        Dim Scores As New Dictionary(Of ResourceProject, Double) '排序分
        Dim GetDownloadCountMult =
        Function(Project As ResourceProject) As Double
            Select Case Request.Type
                Case ResourceTypes.Mod, ResourceTypes.ModPack
                    Return If(Project.Platform = ResourcePlatforms.CurseForge, 1, 5)
                Case ResourceTypes.DataPack
                    Return If(Project.Platform = ResourcePlatforms.CurseForge, 10, 1)
                Case ResourceTypes.ResourcePack, ResourceTypes.Shader
                    Return If(Project.Platform = ResourcePlatforms.CurseForge, 1, 4)
                Case Else
                    Return 1
            End Select
        End Function
        If String.IsNullOrEmpty(RawSearchText) Then
            '如果没有搜索文本，按下载量将结果排序
            For Each Result As ResourceProject In RealResults
                Scores(Result) = Result.DownloadCount * GetDownloadCountMult(Result)
            Next
        Else
            '如果有搜索文本，按关联度将结果排序
            '排序分 = 搜索相对相似度 (1) + 下载量权重 (对数，10 亿时为 1) + 有中文名 (0.2)
            Dim SearchEntries As New List(Of SearchEntry(Of ResourceProject))
            For Each Result As ResourceProject In RealResults
                Scores(Result) = If(Result.WikiId > 0, 0.2, 0) +
                           Math.Log10(Math.Max(Result.DownloadCount, 1) * GetDownloadCountMult(Result)) / 9
                SearchEntries.Add(New SearchEntry(Of ResourceProject) With {.Item = Result, .SearchSource = New List(Of SearchSource) From {
                    New SearchSource(If(IsChineseSearch, Result.ChineseName, Result.RawName).Split("/"c, True), 1),
                    New SearchSource(Result.Description, 0.05)}})
            Next
            Dim SearchResult = Search(SearchEntries, RawSearchText, 10000, -1)
            For Each OneResult In SearchResult
                Scores(OneResult.Item) +=
                    If(OneResult.AbsoluteRight, 10, OneResult.Similarity) /
                    If(SearchResult.First.AbsoluteRight, 10, SearchResult.First.Similarity) '最高 1 分的相似度分
            Next
        End If
        '根据排序分得出结果并添加
        If Task.IsCanceled Then Throw New OperationCanceledException '#8246
        Storage.Results.AddRange(
            Scores.OrderByDescending(Function(s) s.Value).Select(Function(r) r.Key))

#End Region

    End Sub

End Class
