Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

''' <summary>
''' 启动前 Mod 静态元数据读取、冲突/前置预检，以及纯客户端模组识别（供启动预检与服务端/客户端互转共用）。
''' </summary>
Public Module ModPrecheck

    Public Class ModMetadataInfo
        Public Property FilePath As String = ""
        Public Property FileName As String = ""
        Public Property ModId As String = ""
        Public Property DisplayName As String = ""
        Public Property Version As String = ""
        Public Property LoaderType As ModLoaders = ModLoaders.None
        ''' <summary>"*" / "client" / "server"</summary>
        Public Property Environment As String = "*"
        Public Property RequiredMcVersion As String = ""
        Public Property RequiredDeps As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property BreaksDeps As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property ProvidedModIds As New List(Of String)
    End Class

    Public Class ModPrecheckReport
        Public Property ScannedModCount As Integer = 0
        Public Property DuplicateMods As New List(Of String)
        Public Property LoaderMismatches As New List(Of String)
        Public Property ServerOnlyModsInClient As New List(Of String)
        Public Property MissingDependencies As New List(Of String)
        Public Property ConflictBreaks As New List(Of String)
        Public Property McVersionMismatches As New List(Of String)
        Public Property AutoDisableCandidates As New List(Of String)

        Public ReadOnly Property HasIssues As Boolean
            Get
                Return DuplicateMods.Any() OrElse
                       LoaderMismatches.Any() OrElse
                       ServerOnlyModsInClient.Any() OrElse
                       MissingDependencies.Any() OrElse
                       ConflictBreaks.Any() OrElse
                       McVersionMismatches.Any()
            End Get
        End Property

        Public Function FormatReport(Instance As McInstance) As String
            Dim Sb As New StringBuilder()
            Sb.AppendLine($"实例：{Instance.Name}（MC {Instance.Version.VanillaName} · {Instance.Version.ModLoaders}）")
            Sb.AppendLine($"已扫描启用 Mod：{ScannedModCount} 个")
            Sb.AppendLine()
            If Not HasIssues Then
                Sb.AppendLine("✅ 未发现重复 Mod ID、加载器错放、缺失前置、冲突声明或 MC 版本不匹配问题。")
                Return Sb.ToString().TrimEnd()
            End If

            AppendSection(Sb, "【重复 Mod ID（多份并存会导致崩溃）】", DuplicateMods, 8)
            AppendSection(Sb, "【加载器不匹配（错放导致无法加载）】", LoaderMismatches, 8)
            AppendSection(Sb, "【纯服务端 Mod 错放进客户端】", ServerOnlyModsInClient, 6)
            AppendSection(Sb, "【声明冲突 / 不兼容的 Mod】", ConflictBreaks, 8)
            AppendSection(Sb, "【缺失前置依赖 Mod】", MissingDependencies, 10)
            AppendSection(Sb, "【MC 游戏版本不兼容】", McVersionMismatches, 8)

            Return Sb.ToString().TrimEnd()
        End Function

        Private Shared Sub AppendSection(Sb As StringBuilder, Header As String, Items As List(Of String), MaxShow As Integer)
            If Not Items.Any() Then Return
            Sb.AppendLine(Header)
            For Each Item In Items.Take(MaxShow)
                Sb.AppendLine("• " & Item)
            Next
            If Items.Count > MaxShow Then
                Sb.AppendLine($"……其余 {Items.Count - MaxShow} 项略")
            End If
            Sb.AppendLine()
        End Sub
    End Class

    Private ReadOnly BuiltinModIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "minecraft", "java", "fabricloader", "fabric-loader", "fabric", "fabric-api", "fabric_api",
        "quilt_loader", "quilt_base", "quilted_fabric_api", "forge", "neoforge", "fml", "javafml",
        "lowcodefml", "mixinextras", "modlauncher", "optifine"
    }

    Private ReadOnly ClientOnlyKeywords As String() = {
        "iris", "oculus", "optifine", "optifabric", "sodium-extra", "sodium_extra", "sodiumextra",
        "reeses-sodium-options", "reeses_sodium_options", "indium", "continuity", "lambdynamiclights",
        "entity_model_features", "entity_texture_features", "entityculling", "moreculling",
        "immediatelyfast", "modmenu", "catalogue", "controlling", "searchables", "mousetweaks",
        "xaeros_minimap", "xaerominimap", "xaeros-minimap", "voxelmap", "bobby", "distanthorizons",
        "presencefootsteps", "sound_physics", "soundphysics", "3dskinlayers", "skinlayers3d",
        "enhancedblockentities", "betterf3", "toastcontrol", "legendarytooltips", "advancementplaques",
        "chat_heads", "chatheads", "zoomify", "wi-zoom", "okzoomer", "customskinloader",
        "waveycapes", "notenoughanimations", "firstperson", "freecam", "replaymod", "flashback",
        "auditory", "ambientsounds", "dripsounds", "dynamic_fps", "dynamicfps", "fpsreducer",
        "cubes-without-borders", "borderlessmining", "blur", "citresewn", "animatica",
        "colytra", "appleskin", "shulkerboxtooltip", "rei_plugin_compatibilities", "fancymenu",
        "drippyloadingscreen", "loadmyresources", "konkrete", "smoothboot", "fastload",
        "cullleaves", "betterleaves", "visuality", "falling_leaves", "fallingleaves",
        "make_bubbles_pop", "particular", "subtle_effects", "eatinganimation", "itemphysic_lite",
        "bettermounthud", "raised", "inventoryprofiles", "inventoryhud", "jade-addons"
    }

    ''' <summary>
    ''' 检查单个 Mod jar 是否为纯客户端模组（先看元数据 environment/clientSideOnly，再匹配常见客户端特征词）。
    ''' </summary>
    Public Function IsClientOnlyModJar(JarPath As String) As (IsClientOnly As Boolean, Reason As String)
        If String.IsNullOrWhiteSpace(JarPath) OrElse Not File.Exists(JarPath) Then Return (False, "")
        Dim Infos = InspectModJar(JarPath)
        If Infos.Any() AndAlso Infos.All(Function(i) i.Environment.Equals("client", StringComparison.OrdinalIgnoreCase)) Then
            Return (True, "元数据声明 environment=client")
        End If
        Dim FileBase = Path.GetFileNameWithoutExtension(JarPath).ToLowerInvariant()
        For Each Kw In ClientOnlyKeywords
            If FileBase.Contains(Kw) OrElse Infos.Any(Function(i) i.ModId.Equals(Kw, StringComparison.OrdinalIgnoreCase) OrElse i.ModId.Replace("_", "").Replace("-", "").Equals(Kw.Replace("_", "").Replace("-", ""), StringComparison.OrdinalIgnoreCase)) Then
                Return (True, $"识别为纯客户端模组 ({Kw})")
            End If
        Next
        Return (False, "")
    End Function

    ''' <summary>
    ''' 从指定 Mod .jar 文件解析 Fabric / Quilt / NeoForge / Forge 元数据（含内嵌 META-INF/jars 提供的 modId）。
    ''' </summary>
    Public Function InspectModJar(JarPath As String) As List(Of ModMetadataInfo)
        Dim Results As New List(Of ModMetadataInfo)
        If String.IsNullOrWhiteSpace(JarPath) OrElse Not File.Exists(JarPath) Then Return Results
        Try
            Using Archive = ZipFile.OpenRead(JarPath)
                InspectZipArchiveInto(Archive, JarPath, Path.GetFileName(JarPath), Results, IncludeNestedJars:=True)
            End Using
        Catch ex As Exception
            Logger.Warn(ex, $"读取 Mod 元数据失败（可能文件已损坏）：{JarPath}")
        End Try
        Return Results
    End Function

    Private Sub InspectZipArchiveInto(Archive As ZipArchive, FilePath As String, FileName As String, Results As List(Of ModMetadataInfo), IncludeNestedJars As Boolean)
        Dim NestedProvidedIds As New List(Of String)
        If IncludeNestedJars Then
            For Each Entry In Archive.Entries
                If Entry.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) AndAlso
                   Entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) AndAlso
                   Entry.Length > 0 AndAlso Entry.Length < 12 * 1024 * 1024 Then
                    Try
                        Using Es = Entry.Open(), NestedZip As New ZipArchive(Es, ZipArchiveMode.Read)
                            Dim NestedList As New List(Of ModMetadataInfo)
                            InspectZipArchiveInto(NestedZip, FilePath, FileName, NestedList, IncludeNestedJars:=False)
                            For Each Ni In NestedList
                                If Not String.IsNullOrWhiteSpace(Ni.ModId) Then NestedProvidedIds.Add(Ni.ModId)
                                NestedProvidedIds.AddRange(Ni.ProvidedModIds)
                            Next
                        End Using
                    Catch
                    End Try
                End If
            Next
        End If

        Dim FabricEntry = Archive.GetEntry("fabric.mod.json")
        If FabricEntry IsNot Nothing Then
            Dim Info = ParseFabricModJson(ReadZipEntryUtf8(FabricEntry), FilePath, FileName)
            If Info IsNot Nothing Then
                Info.ProvidedModIds.AddRange(NestedProvidedIds)
                Results.Add(Info)
            End If
        Else
            Dim QuiltEntry = Archive.GetEntry("quilt.mod.json")
            If QuiltEntry IsNot Nothing Then
                Dim Info = ParseQuiltModJson(ReadZipEntryUtf8(QuiltEntry), FilePath, FileName)
                If Info IsNot Nothing Then
                    Info.ProvidedModIds.AddRange(NestedProvidedIds)
                    Results.Add(Info)
                End If
            End If
        End If

        Dim NeoToml = Archive.GetEntry("META-INF/neoforge.mods.toml")
        If NeoToml IsNot Nothing Then
            Dim TomlList = ParseModsToml(ReadZipEntryUtf8(NeoToml), ModLoaders.NeoForge, FilePath, FileName)
            For Each Item In TomlList
                Item.ProvidedModIds.AddRange(NestedProvidedIds)
            Next
            Results.AddRange(TomlList)
        Else
            Dim ForgeToml = Archive.GetEntry("META-INF/mods.toml")
            If ForgeToml IsNot Nothing Then
                Dim TomlList = ParseModsToml(ReadZipEntryUtf8(ForgeToml), ModLoaders.Forge, FilePath, FileName)
                For Each Item In TomlList
                    Item.ProvidedModIds.AddRange(NestedProvidedIds)
                Next
                Results.AddRange(TomlList)
            ElseIf Not Results.Any() Then
                Dim LegacyInfo = Archive.GetEntry("mcmod.info")
                If LegacyInfo IsNot Nothing Then
                    Dim LegacyList = ParseMcModInfo(ReadZipEntryUtf8(LegacyInfo), FilePath, FileName)
                    Results.AddRange(LegacyList)
                End If
            End If
        End If
    End Sub

    Private Function ReadZipEntryUtf8(Entry As ZipArchiveEntry) As String
        Using S = Entry.Open(), Sr As New StreamReader(S, Encoding.UTF8, True)
            Return Sr.ReadToEnd()
        End Using
    End Function

    Private Function ParseFabricModJson(JsonText As String, FilePath As String, FileName As String) As ModMetadataInfo
        If String.IsNullOrWhiteSpace(JsonText) Then Return Nothing
        Try
            Dim Obj = JObject.Parse(JsonText)
            Dim Id = If(Obj("id")?.ToString(), "").Trim()
            If Id = "" Then Return Nothing
            Dim Env = If(Obj("environment")?.ToString(), "*").Trim().ToLowerInvariant()
            If Env <> "client" AndAlso Env <> "server" Then Env = "*"
            Dim Info As New ModMetadataInfo With {
                .FilePath = FilePath,
                .FileName = FileName,
                .ModId = Id,
                .DisplayName = If(Not String.IsNullOrWhiteSpace(Obj("name")?.ToString()), Obj("name").ToString().Trim(), Id),
                .Version = If(Obj("version")?.ToString(), "").Trim(),
                .LoaderType = ModLoaders.Fabric,
                .Environment = Env
            }
            Dim ProvidesArr = TryCast(Obj("provides"), JArray)
            If ProvidesArr IsNot Nothing Then
                For Each P In ProvidesArr
                    Dim PId = P.ToString().Trim()
                    If PId <> "" Then Info.ProvidedModIds.Add(PId)
                Next
            End If
            Dim DepsObj = TryCast(Obj("depends"), JObject)
            If DepsObj IsNot Nothing Then
                For Each Prop In DepsObj.Properties()
                    Dim RangeText = FormatJsonVersionRange(Prop.Value)
                    If Prop.Name.Equals("minecraft", StringComparison.OrdinalIgnoreCase) Then
                        Info.RequiredMcVersion = RangeText
                    Else
                        Info.RequiredDeps(Prop.Name.Trim()) = RangeText
                    End If
                Next
            End If
            For Each ConflictKey In {"breaks", "conflicts"}
                Dim BreaksObj = TryCast(Obj(ConflictKey), JObject)
                If BreaksObj IsNot Nothing Then
                    For Each Prop In BreaksObj.Properties()
                        Info.BreaksDeps(Prop.Name.Trim()) = FormatJsonVersionRange(Prop.Value)
                    Next
                End If
            Next
            Return Info
        Catch
            Return Nothing
        End Try
    End Function

    Private Function ParseQuiltModJson(JsonText As String, FilePath As String, FileName As String) As ModMetadataInfo
        If String.IsNullOrWhiteSpace(JsonText) Then Return Nothing
        Try
            Dim Root = JObject.Parse(JsonText)
            Dim LoaderObj = TryCast(Root("quilt_loader"), JObject)
            If LoaderObj Is Nothing Then Return Nothing
            Dim Id = If(LoaderObj("id")?.ToString(), "").Trim()
            If Id = "" Then Return Nothing
            Dim MetaObj = TryCast(LoaderObj("metadata"), JObject)
            Dim Info As New ModMetadataInfo With {
                .FilePath = FilePath,
                .FileName = FileName,
                .ModId = Id,
                .DisplayName = If(Not String.IsNullOrWhiteSpace(MetaObj?("name")?.ToString()), MetaObj("name").ToString().Trim(), Id),
                .Version = If(LoaderObj("version")?.ToString(), "").Trim(),
                .LoaderType = ModLoaders.Fabric
            }
            Dim DepsArr = TryCast(LoaderObj("depends"), JArray)
            If DepsArr IsNot Nothing Then
                For Each DepToken In DepsArr
                    If DepToken.Type = JTokenType.String Then
                        Dim DepId = DepToken.ToString().Trim()
                        If DepId <> "" Then Info.RequiredDeps(DepId) = "*"
                    ElseIf DepToken.Type = JTokenType.Object Then
                        Dim DepObj = CType(DepToken, JObject)
                        If DepObj("optional")?.Value(Of Boolean)() = True Then Continue For
                        Dim DepId = If(DepObj("id")?.ToString(), "").Trim()
                        Dim Vers = FormatJsonVersionRange(DepObj("versions"))
                        If DepId.Equals("minecraft", StringComparison.OrdinalIgnoreCase) Then
                            Info.RequiredMcVersion = Vers
                        ElseIf DepId <> "" Then
                            Info.RequiredDeps(DepId) = Vers
                        End If
                    End If
                Next
            End If
            Return Info
        Catch
            Return Nothing
        End Try
    End Function

    Private Function FormatJsonVersionRange(Tok As JToken) As String
        If Tok Is Nothing Then Return "*"
        If Tok.Type = JTokenType.Array Then
            Return String.Join(" ", CType(Tok, JArray).Select(Function(x) x.ToString().Trim()))
        End If
        Return Tok.ToString().Trim()
    End Function

    Private Function ParseModsToml(TomlText As String, Loader As ModLoaders, FilePath As String, FileName As String) As List(Of ModMetadataInfo)
        Dim ModsList As New List(Of ModMetadataInfo)
        If String.IsNullOrWhiteSpace(TomlText) Then Return ModsList

        Dim Section As String = ""
        Dim CurrentMod As ModMetadataInfo = Nothing
        Dim DepOwner As String = ""
        Dim DepModId As String = ""
        Dim DepVersionRange As String = "*"
        Dim DepSide As String = "BOTH"
        Dim DepMandatory As Boolean = True
        Dim DepType As String = "required"
        Dim RootClientSideOnly As Boolean = False

        Dim FlushDep =
        Sub()
            If DepModId = "" Then Return
            Dim TargetMod = ModsList.FirstOrDefault(Function(m) m.ModId.Equals(DepOwner, StringComparison.OrdinalIgnoreCase))
            If TargetMod Is Nothing Then TargetMod = ModsList.FirstOrDefault()
            If TargetMod Is Nothing Then
                DepModId = ""
                Return
            End If
            If DepType.Equals("incompatible", StringComparison.OrdinalIgnoreCase) Then
                TargetMod.BreaksDeps(DepModId) = DepVersionRange
            ElseIf DepMandatory AndAlso DepType.Equals("required", StringComparison.OrdinalIgnoreCase) Then
                If DepModId.Equals("minecraft", StringComparison.OrdinalIgnoreCase) Then
                    If String.IsNullOrEmpty(TargetMod.RequiredMcVersion) Then TargetMod.RequiredMcVersion = DepVersionRange
                ElseIf Not DepSide.Equals("SERVER", StringComparison.OrdinalIgnoreCase) Then
                    TargetMod.RequiredDeps(DepModId) = DepVersionRange
                End If
            End If
            DepModId = ""
        End Sub

        For Each RawLine In TomlText.Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
            Dim Line = StripTomlComment(RawLine).Trim()
            If Line = "" Then Continue For
            If Line.StartsWith("[[") AndAlso Line.EndsWith("]]") Then
                FlushDep()
                Dim SecName = Line.Substring(2, Line.Length - 4).Trim()
                If SecName.Equals("mods", StringComparison.OrdinalIgnoreCase) Then
                    Section = "mods"
                    CurrentMod = New ModMetadataInfo With {
                        .FilePath = FilePath,
                        .FileName = FileName,
                        .LoaderType = Loader,
                        .Environment = If(RootClientSideOnly, "client", "*")
                    }
                    ModsList.Add(CurrentMod)
                ElseIf SecName.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase) Then
                    Section = "dep"
                    DepOwner = SecName.Substring("dependencies.".Length).Trim().Trim(""""c, "'"c)
                    DepModId = ""
                    DepVersionRange = "*"
                    DepSide = "BOTH"
                    DepMandatory = True
                    DepType = "required"
                Else
                    Section = SecName
                End If
                Continue For
            ElseIf Line.StartsWith("[") AndAlso Line.EndsWith("]") Then
                FlushDep()
                Section = Line.Substring(1, Line.Length - 2).Trim()
                Continue For
            End If

            Dim EqIdx = Line.IndexOf("="c)
            If EqIdx <= 0 Then Continue For
            Dim Key = Line.Substring(0, EqIdx).Trim().Trim(""""c, "'"c).ToLowerInvariant()
            Dim Val = Line.Substring(EqIdx + 1).Trim().Trim(""""c, "'"c)

            Select Case Section
                Case ""
                    If Key = "clientsideonly" AndAlso Val.Equals("true", StringComparison.OrdinalIgnoreCase) Then
                        RootClientSideOnly = True
                    End If
                Case "mods"
                    If CurrentMod IsNot Nothing Then
                        Select Case Key
                            Case "modid" : CurrentMod.ModId = Val
                            Case "displayname" : CurrentMod.DisplayName = Val
                            Case "version" : CurrentMod.Version = Val
                            Case "clientsideonly"
                                If Val.Equals("true", StringComparison.OrdinalIgnoreCase) Then CurrentMod.Environment = "client"
                        End Select
                        If CurrentMod.DisplayName = "" AndAlso CurrentMod.ModId <> "" Then CurrentMod.DisplayName = CurrentMod.ModId
                    End If
                Case "dep"
                    Select Case Key
                        Case "modid" : DepModId = Val
                        Case "versionrange" : DepVersionRange = Val
                        Case "side" : DepSide = Val.ToUpperInvariant()
                        Case "mandatory" : DepMandatory = Not Val.Equals("false", StringComparison.OrdinalIgnoreCase)
                        Case "type" : DepType = Val.ToLowerInvariant()
                    End Select
            End Select
        Next
        FlushDep()

        For Each M In ModsList
            If M.DisplayName = "" Then M.DisplayName = If(M.ModId <> "", M.ModId, FileName)
        Next
        Return ModsList.Where(Function(m) Not String.IsNullOrWhiteSpace(m.ModId)).ToList()
    End Function

    Private Function StripTomlComment(Line As String) As String
        Dim InSingle As Boolean = False
        Dim InDouble As Boolean = False
        For i As Integer = 0 To Line.Length - 1
            Dim Ch = Line(i)
            If Ch = """"c AndAlso Not InSingle AndAlso (i = 0 OrElse Line(i - 1) <> "\"c) Then
                InDouble = Not InDouble
            ElseIf Ch = "'"c AndAlso Not InDouble Then
                InSingle = Not InSingle
            ElseIf Ch = "#"c AndAlso Not InSingle AndAlso Not InDouble Then
                Return Line.Substring(0, i)
            End If
        Next
        Return Line
    End Function

    Private Function ParseMcModInfo(JsonText As String, FilePath As String, FileName As String) As List(Of ModMetadataInfo)
        Dim List As New List(Of ModMetadataInfo)
        If String.IsNullOrWhiteSpace(JsonText) Then Return List
        Try
            Dim Tok = JToken.Parse(JsonText)
            Dim Arr As JArray = TryCast(Tok, JArray)
            If Arr Is Nothing AndAlso Tok.Type = JTokenType.Object Then
                Arr = TryCast(Tok("modList"), JArray)
                If Arr Is Nothing Then Arr = New JArray(Tok)
            End If
            If Arr Is Nothing Then Return List
            For Each Item In Arr.OfType(Of JObject)()
                Dim Id = If(Item("modid")?.ToString(), "").Trim()
                If Id = "" Then Continue For
                Dim Info As New ModMetadataInfo With {
                    .FilePath = FilePath,
                    .FileName = FileName,
                    .ModId = Id,
                    .DisplayName = If(Not String.IsNullOrWhiteSpace(Item("name")?.ToString()), Item("name").ToString().Trim(), Id),
                    .Version = If(Item("version")?.ToString(), "").Trim(),
                    .LoaderType = ModLoaders.Forge,
                    .RequiredMcVersion = If(Item("mcversion")?.ToString(), "").Trim()
                }
                Dim ReqArr = TryCast(Item("requiredMods"), JArray)
                If ReqArr IsNot Nothing Then
                    For Each R In ReqArr
                        Dim DepRaw = R.ToString().Trim()
                        If DepRaw = "" Then Continue For
                        Dim DepId = DepRaw.Split("@"c)(0).Trim()
                        If DepId <> "" AndAlso Not DepId.Equals("Forge", StringComparison.OrdinalIgnoreCase) AndAlso Not DepId.Equals("FML", StringComparison.OrdinalIgnoreCase) Then
                            Info.RequiredDeps(DepId) = "*"
                        End If
                    Next
                End If
                List.Add(Info)
            Next
        Catch
        End Try
        Return List
    End Function

    ''' <summary>
    ''' 对指定游戏实例运行静态 Mod 冲突、重复、加载器与前置依赖预检。
    ''' </summary>
    Public Function RunInstanceModPrecheck(Instance As McInstance) As ModPrecheckReport
        Dim Report As New ModPrecheckReport()
        If Instance Is Nothing Then Return Report
        If Not Instance.IsLoaded Then Instance.Load()

        Dim ModsDir = Instance.PathIndie & "mods\"
        If Not Directory.Exists(ModsDir) Then Return Report

        Dim JarFiles As New List(Of String)
        For Each F In Directory.EnumerateFiles(ModsDir, "*.jar", SearchOption.TopDirectoryOnly)
            JarFiles.Add(F)
        Next
        If Instance.Version.ModLoaders.HasFlagF(ModLoaders.Forge) AndAlso Instance.Version.Vanilla.Major < 13 Then
            Dim SubVerDir = Path.Combine(ModsDir, $"1.{Instance.Version.Vanilla.Major}.{Instance.Version.Vanilla.Build}")
            If Directory.Exists(SubVerDir) Then
                JarFiles.AddRange(Directory.EnumerateFiles(SubVerDir, "*.jar", SearchOption.TopDirectoryOnly))
            End If
        End If
        Report.ScannedModCount = JarFiles.Count
        If JarFiles.Count = 0 Then Return Report

        Dim AllMetas As New List(Of ModMetadataInfo)
        Dim FileToMetas As New Dictionary(Of String, List(Of ModMetadataInfo))(StringComparer.OrdinalIgnoreCase)
        For Each Jar In JarFiles
            Dim Metas = InspectModJar(Jar)
            FileToMetas(Jar) = Metas
            AllMetas.AddRange(Metas)
        Next

        Dim ProvidedIds As New HashSet(Of String)(BuiltinModIds, StringComparer.OrdinalIgnoreCase)
        Dim IdToFiles As New Dictionary(Of String, List(Of ModMetadataInfo))(StringComparer.OrdinalIgnoreCase)
        For Each M In AllMetas
            If Not String.IsNullOrWhiteSpace(M.ModId) Then
                ProvidedIds.Add(M.ModId)
                If Not IdToFiles.ContainsKey(M.ModId) Then IdToFiles(M.ModId) = New List(Of ModMetadataInfo)
                IdToFiles(M.ModId).Add(M)
            End If
            For Each PId In M.ProvidedModIds
                ProvidedIds.Add(PId)
            Next
        Next
        Dim HasFabricApi = ProvidedIds.Contains("fabric-api") OrElse ProvidedIds.Contains("fabric_api") OrElse ProvidedIds.Contains("quilted_fabric_api")
        Dim HasSinytraConnector = ProvidedIds.Contains("connectormod") OrElse ProvidedIds.Contains("connector") OrElse ProvidedIds.Contains("sinytra_connector")

        '1. 重复 Mod ID
        For Each Kv In IdToFiles
            Dim DistinctFiles = Kv.Value.Select(Function(x) x.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If DistinctFiles.Count > 1 Then
                Dim Names = DistinctFiles.Select(Function(p) Path.GetFileName(p)).ToList()
                Report.DuplicateMods.Add($"ModId [{Kv.Key}] 存在 {DistinctFiles.Count} 份重复文件：{String.Join(" / ", Names)}")
                '默认保留修改时间最新的那份，其余加入候选禁用列表
                Dim Ordered = DistinctFiles.OrderByDescending(Function(p) File.GetLastWriteTimeUtc(p)).Skip(1)
                For Each DupPath In Ordered
                    If Not Report.AutoDisableCandidates.Contains(DupPath, StringComparer.OrdinalIgnoreCase) Then
                        Report.AutoDisableCandidates.Add(DupPath)
                    End If
                Next
            End If
        Next

        '2. 加载器匹配检查 & 3. 纯服务端 Mod 检查
        Dim InstLoaders = Instance.Version.ModLoaders
        For Each Kv In FileToMetas
            Dim JarPath = Kv.Key
            Dim FileName = Path.GetFileName(JarPath)
            Dim Metas = Kv.Value
            If Not Metas.Any() Then Continue For

            If Metas.All(Function(m) m.Environment.Equals("server", StringComparison.OrdinalIgnoreCase)) Then
                Report.ServerOnlyModsInClient.Add($"{FileName}（声明仅限服务端运行）")
                If Not Report.AutoDisableCandidates.Contains(JarPath, StringComparer.OrdinalIgnoreCase) Then
                    Report.AutoDisableCandidates.Add(JarPath)
                End If
            End If

            If InstLoaders <> ModLoaders.None Then
                Dim HasFabricMeta = Metas.Any(Function(m) m.LoaderType = ModLoaders.Fabric)
                Dim HasForgeMeta = Metas.Any(Function(m) m.LoaderType = ModLoaders.Forge)
                Dim HasNeoMeta = Metas.Any(Function(m) m.LoaderType = ModLoaders.NeoForge)

                If InstLoaders.HasFlagF(ModLoaders.Fabric) AndAlso Not InstLoaders.HasFlagF(ModLoaders.Forge) AndAlso Not InstLoaders.HasFlagF(ModLoaders.NeoForge) Then
                    If Not HasFabricMeta AndAlso (HasForgeMeta OrElse HasNeoMeta) Then
                        Report.LoaderMismatches.Add($"{FileName} 是 {If(HasNeoMeta, "NeoForge", "Forge")} Mod，无法在 Fabric 实例中加载")
                        If Not Report.AutoDisableCandidates.Contains(JarPath, StringComparer.OrdinalIgnoreCase) Then Report.AutoDisableCandidates.Add(JarPath)
                    End If
                ElseIf (InstLoaders.HasFlagF(ModLoaders.Forge) OrElse InstLoaders.HasFlagF(ModLoaders.NeoForge)) AndAlso Not InstLoaders.HasFlagF(ModLoaders.Fabric) Then
                    If HasFabricMeta AndAlso Not HasForgeMeta AndAlso Not HasNeoMeta AndAlso Not HasSinytraConnector Then
                        Report.LoaderMismatches.Add($"{FileName} 是纯 Fabric Mod，当前实例为 {InstLoaders}（且未安装 Sinytra Connector）")
                        If Not Report.AutoDisableCandidates.Contains(JarPath, StringComparer.OrdinalIgnoreCase) Then Report.AutoDisableCandidates.Add(JarPath)
                    End If
                End If
            End If
        Next

        '4. 依赖缺失、5. 显式冲突、6. MC 版本不匹配
        Dim SeenMissing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim SeenBreaks As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim SeenMcMismatch As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim InstanceMcVer = If(Instance.Version.VanillaName, "")

        For Each M In AllMetas
            If Not String.IsNullOrWhiteSpace(M.RequiredMcVersion) AndAlso Not String.IsNullOrWhiteSpace(InstanceMcVer) Then
                If Not IsMcVersionConstraintSatisfied(M.RequiredMcVersion, InstanceMcVer) Then
                    Dim Key = $"{M.FileName}|{M.RequiredMcVersion}"
                    If SeenMcMismatch.Add(Key) Then
                        Report.McVersionMismatches.Add($"{M.DisplayName}（{M.FileName}）要求 MC {M.RequiredMcVersion}，当前为 {InstanceMcVer}")
                    End If
                End If
            End If

            For Each DepKv In M.RequiredDeps
                Dim DepId = DepKv.Key
                If BuiltinModIds.Contains(DepId) Then Continue For
                If HasFabricApi AndAlso DepId.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase) Then Continue For
                If DepId.StartsWith("minecraft", StringComparison.OrdinalIgnoreCase) OrElse DepId.StartsWith("java", StringComparison.OrdinalIgnoreCase) Then Continue For
                If Not ProvidedIds.Contains(DepId) AndAlso Not ProvidedIds.Contains(DepId.Replace("-", "_")) AndAlso Not ProvidedIds.Contains(DepId.Replace("_", "-")) Then
                    Dim MissKey = $"{M.ModId}->{DepId}"
                    If SeenMissing.Add(MissKey) Then
                        Report.MissingDependencies.Add($"{M.DisplayName}（{M.FileName}）缺少前置 [{DepId}]" & If(DepKv.Value = "*" OrElse DepKv.Value = "", "", $" ({DepKv.Value})"))
                    End If
                End If
            Next

            For Each BrKv In M.BreaksDeps
                Dim TargetId = BrKv.Key
                If TargetId.Equals("minecraft", StringComparison.OrdinalIgnoreCase) Then Continue For
                If ProvidedIds.Contains(TargetId) Then
                    Dim BrKey = $"{M.ModId}X{TargetId}"
                    If SeenBreaks.Add(BrKey) Then
                        Report.ConflictBreaks.Add($"{M.DisplayName}（{M.FileName}）声明与已安装的 [{TargetId}] 冲突/不兼容")
                    End If
                End If
            Next
        Next

        Return Report
    End Function

    Private Function IsMcVersionConstraintSatisfied(Constraint As String, InstanceVer As String) As Boolean
        If String.IsNullOrWhiteSpace(Constraint) OrElse String.IsNullOrWhiteSpace(InstanceVer) Then Return True
        Dim Raw = Constraint.Trim()
        If Raw = "*" OrElse Raw = "any" OrElse Raw.Contains("${") Then Return True
        '只解析明确可判定的精确/区间格式，避免因复杂语义范围误报
        '1. Maven 区间 [1.20.1,1.21) / (1.20,1.21] / [1.20.1]
        If (Raw.StartsWith("[") OrElse Raw.StartsWith("(")) AndAlso (Raw.EndsWith("]") OrElse Raw.EndsWith(")")) Then
            Dim Inner = Raw.Substring(1, Raw.Length - 2).Trim()
            Dim InclLow = Raw.StartsWith("[")
            Dim InclHigh = Raw.EndsWith("]")
            If Not Inner.Contains(",") Then
                Return IsExactOrPrefixMcMatch(Inner, InstanceVer)
            End If
            Dim Parts = Inner.Split(","c)
            If Parts.Length = 2 Then
                Dim Low = Parts(0).Trim()
                Dim High = Parts(1).Trim()
                If Low <> "" Then
                    Dim CmpLow = McUtils.CompareVersion(InstanceVer, Low)
                    If If(InclLow, CmpLow < 0, CmpLow <= 0) Then Return False
                End If
                If High <> "" Then
                    Dim CmpHigh = McUtils.CompareVersion(InstanceVer, High)
                    If If(InclHigh, CmpHigh > 0, CmpHigh >= 0) Then Return False
                End If
                Return True
            End If
            Return True
        End If

        '2. Fabric ~1.20.1 / =1.20.1 / 1.20.x / 1.20.* 或精确版本号 1.20.1
        Dim Tokens = Raw.Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
        For Each Tok In Tokens
            If Tok = "*" Then Continue For
            If Tok.StartsWith(">=") Then
                Dim Ver = Tok.Substring(2).Trim()
                If Ver <> "" AndAlso McUtils.CompareVersion(InstanceVer, Ver) < 0 Then Return False
            ElseIf Tok.StartsWith(">") Then
                Dim Ver = Tok.Substring(1).Trim()
                If Ver <> "" AndAlso McUtils.CompareVersion(InstanceVer, Ver) <= 0 Then Return False
            ElseIf Tok.StartsWith("<=") Then
                Dim Ver = Tok.Substring(2).Trim()
                If Ver <> "" AndAlso McUtils.CompareVersion(InstanceVer, Ver) > 0 Then Return False
            ElseIf Tok.StartsWith("<") Then
                Dim Ver = Tok.Substring(1).Trim()
                If Ver <> "" AndAlso McUtils.CompareVersion(InstanceVer, Ver) >= 0 Then Return False
            ElseIf Tok.StartsWith("~") Then
                Dim Ver = Tok.Substring(1).Trim()
                If Not IsSameMinorSeries(Ver, InstanceVer) OrElse McUtils.CompareVersion(InstanceVer, Ver) < 0 Then Return False
            ElseIf Tok.StartsWith("^") Then
                Dim Ver = Tok.Substring(1).Trim()
                If Not IsSameMajorSeries(Ver, InstanceVer) OrElse McUtils.CompareVersion(InstanceVer, Ver) < 0 Then Return False
            ElseIf Tok.StartsWith("=") Then
                Dim Ver = Tok.Substring(1).Trim()
                If Not IsExactOrPrefixMcMatch(Ver, InstanceVer) Then Return False
            ElseIf Regex.IsMatch(Tok, "^1\.\d+(\.\d+|\.[x*])?$", RegexOptions.IgnoreCase) Then
                '无修饰符仅允许同系列，例如 1.20.x 或明确版本号 1.20.1
                If Not IsExactOrPrefixMcMatch(Tok, InstanceVer) Then Return False
            End If
        Next
        Return True
    End Function

    Private Function IsExactOrPrefixMcMatch(PatternVer As String, InstanceVer As String) As Boolean
        If String.IsNullOrWhiteSpace(PatternVer) Then Return True
        Dim Clean = PatternVer.Trim().ToLowerInvariant()
        If Clean.EndsWith(".x") OrElse Clean.EndsWith(".*") Then
            Dim Prefix = Clean.Substring(0, Clean.Length - 2)
            Return InstanceVer.Equals(Prefix, StringComparison.OrdinalIgnoreCase) OrElse
                   InstanceVer.StartsWith(Prefix & ".", StringComparison.OrdinalIgnoreCase)
        End If
        Return IsSameMinorSeries(Clean, InstanceVer)
    End Function

    Private Function IsSameMinorSeries(VerA As String, VerB As String) As Boolean
        Dim PartsA = VerA.Split("."c)
        Dim PartsB = VerB.Split("."c)
        If PartsA.Length >= 2 AndAlso PartsB.Length >= 2 Then
            Return PartsA(0) = PartsB(0) AndAlso PartsA(1) = PartsB(1)
        End If
        Return String.Equals(VerA, VerB, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Function IsSameMajorSeries(VerA As String, VerB As String) As Boolean
        Dim PartsA = VerA.Split("."c)
        Dim PartsB = VerB.Split("."c)
        If PartsA.Length >= 1 AndAlso PartsB.Length >= 1 Then Return PartsA(0) = PartsB(0)
        Return True
    End Function

End Module
