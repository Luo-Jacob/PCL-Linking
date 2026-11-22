' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone Minecraft server plugin metadata inspector, performance config parser/writer, and JVM flag builder (no PCL dependencies).

Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Module ServerConfigAndPluginCore

    Public Class ServerPluginInfo
        Public Property FilePath As String = ""
        Public Property FileName As String = ""
        Public Property IsEnabled As Boolean = True
        Public Property Name As String = ""
        Public Property Version As String = ""
        Public Property Description As String = ""
        Public Property ApiVersion As String = ""
        Public Property PlatformKind As String = "Bukkit"
        Public Property Depends As New List(Of String)
        Public Property SoftDepends As New List(Of String)
        Public Property Sha1 As String = ""
    End Class

    Public Class ServerPerformanceConfig
        Public Property SimulationDistance As Integer = 10
        Public Property ViewDistance As Integer = 10
        Public Property SpawnProtection As Integer = 16
        Public Property NetworkCompressionThreshold As Integer = 256
        Public Property MaxTickTime As Integer = 60000
        Public Property AllowFlight As Boolean = False
        Public Property EnableCommandBlock As Boolean = False
        Public Property SyncChunkWrites As Boolean = True
        Public Property MonsterSpawnLimit As Integer = 70
        Public Property AnimalSpawnLimit As Integer = 10
        Public Property PaperMaxAutoSaveChunks As Integer = 24
        Public Property PaperOptimizeExplosions As Boolean = False
        Public Property PaperPreventUnloadedChunks As Boolean = True
        Public Property JvmPreset As String = "Standard"
    End Class

    Public Function ComputeFileSha1Lower(FilePath As String) As String
        Using Fs = File.OpenRead(FilePath), Sha = SHA1.Create()
            Return BitConverter.ToString(Sha.ComputeHash(Fs)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Public Function InspectServerPluginJar(JarPath As String) As ServerPluginInfo
        Dim Info As New ServerPluginInfo With {
            .FilePath = If(JarPath, ""),
            .FileName = Path.GetFileName(If(JarPath, "")),
            .IsEnabled = Not If(JarPath, "").EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
            .Name = Path.GetFileNameWithoutExtension(If(JarPath, "").Replace(".jar.disabled", ".jar"))
        }
        If String.IsNullOrWhiteSpace(JarPath) OrElse Not File.Exists(JarPath) Then Return Info

        Try
            Using Archive = ZipFile.OpenRead(JarPath)
                Dim PaperYml = Archive.GetEntry("paper-plugin.yml")
                Dim BukkitYml = Archive.GetEntry("plugin.yml")
                Dim BungeeYml = Archive.GetEntry("bungee.yml")
                Dim VelocityJson = Archive.GetEntry("velocity-plugin.json")

                If PaperYml IsNot Nothing Then
                    Info.PlatformKind = "Paper"
                    ParsePluginYml(ReadZipText(PaperYml), Info, IsPaperPlugin:=True)
                ElseIf BukkitYml IsNot Nothing Then
                    Info.PlatformKind = "Bukkit"
                    ParsePluginYml(ReadZipText(BukkitYml), Info, IsPaperPlugin:=False)
                ElseIf BungeeYml IsNot Nothing Then
                    Info.PlatformKind = "Bungee"
                    ParsePluginYml(ReadZipText(BungeeYml), Info, IsPaperPlugin:=False)
                ElseIf VelocityJson IsNot Nothing Then
                    Info.PlatformKind = "Velocity"
                    ParseVelocityJson(ReadZipText(VelocityJson), Info)
                End If
            End Using
        Catch
            '损坏或非标准压缩包保留文件名信息即可
        End Try
        If String.IsNullOrWhiteSpace(Info.Name) Then
            Info.Name = Info.FileName.Replace(".jar.disabled", "").Replace(".jar", "")
        End If
        Info.Depends = Info.Depends.Where(Function(d) Not String.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Info.SoftDepends = Info.SoftDepends.Where(Function(d) Not String.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Return Info
    End Function

    Public Function InspectServerPluginsDir(ServerDir As String) As List(Of ServerPluginInfo)
        Dim Results As New List(Of ServerPluginInfo)
        If String.IsNullOrWhiteSpace(ServerDir) Then Return Results
        Dim PluginsDir = Path.Combine(ServerDir, "plugins")
        If Not Directory.Exists(PluginsDir) Then Return Results
        Dim Files = Directory.GetFiles(PluginsDir, "*.jar", SearchOption.TopDirectoryOnly).
            Concat(Directory.GetFiles(PluginsDir, "*.jar.disabled", SearchOption.TopDirectoryOnly)).
            Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        For Each F In Files
            Results.Add(InspectServerPluginJar(F))
        Next
        Return Results.OrderByDescending(Function(p) p.IsEnabled).ThenBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    Public Function FindMissingPluginDependencies(Plugins As IEnumerable(Of ServerPluginInfo)) As List(Of String)
        Dim Missing As New List(Of String)
        If Plugins Is Nothing Then Return Missing
        Dim EnabledList = Plugins.Where(Function(p) p IsNot Nothing AndAlso p.IsEnabled).ToList()
        Dim ProvidedNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each P In EnabledList
            If Not String.IsNullOrWhiteSpace(P.Name) Then ProvidedNames.Add(P.Name.Trim())
        Next
        For Each P In EnabledList
            For Each Dep In P.Depends
                Dim Clean = If(Dep, "").Trim()
                If Clean = "" Then Continue For
                If Not ProvidedNames.Contains(Clean) Then
                    Missing.Add($"{P.Name} ({P.FileName}) 缺少必需前置插件 [{Clean}]")
                End If
            Next
        Next
        Return Missing
    End Function

    Private Function ReadZipText(Entry As ZipArchiveEntry) As String
        Using S = Entry.Open(), Sr As New StreamReader(S, Encoding.UTF8, True)
            Return Sr.ReadToEnd()
        End Using
    End Function

    Private Sub ParseVelocityJson(JsonText As String, Info As ServerPluginInfo)
        Try
            Dim Obj = JObject.Parse(JsonText)
            Dim NameStr = If(Obj("name")?.ToString(), Obj("id")?.ToString())
            If Not String.IsNullOrWhiteSpace(NameStr) Then Info.Name = NameStr.Trim()
            If Not String.IsNullOrWhiteSpace(Obj("version")?.ToString()) Then Info.Version = Obj("version").ToString().Trim()
            If Not String.IsNullOrWhiteSpace(Obj("description")?.ToString()) Then Info.Description = Obj("description").ToString().Trim()
            Dim DepsArr = TryCast(Obj("dependencies"), JArray)
            If DepsArr IsNot Nothing Then
                For Each Tok In DepsArr
                    Dim DepObj = TryCast(Tok, JObject)
                    If DepObj Is Nothing Then Continue For
                    Dim DepId = If(DepObj("id")?.ToString(), "").Trim()
                    If DepId = "" Then Continue For
                    If DepObj("optional")?.ToObject(Of Boolean)() = True Then
                        Info.SoftDepends.Add(DepId)
                    Else
                        Info.Depends.Add(DepId)
                    End If
                Next
            End If
        Catch
        End Try
    End Sub

    Private Sub ParsePluginYml(YmlText As String, Info As ServerPluginInfo, IsPaperPlugin As Boolean)
        If String.IsNullOrWhiteSpace(YmlText) Then Return
        Dim ListTarget As String = ""
        Dim InDependenciesSection As Boolean = False
        Dim InPaperServerDeps As Boolean = False
        Dim CurrentPaperDepName As String = ""
        Dim CurrentPaperDepRequired As Boolean = True

        Dim FlushPaperDep =
        Sub()
            If CurrentPaperDepName <> "" Then
                If CurrentPaperDepRequired Then
                    Info.Depends.Add(CurrentPaperDepName)
                Else
                    Info.SoftDepends.Add(CurrentPaperDepName)
                End If
                CurrentPaperDepName = ""
                CurrentPaperDepRequired = True
            End If
        End Sub

        For Each RawLine In YmlText.Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
            Dim NoComment = StripYamlComment(RawLine)
            If String.IsNullOrWhiteSpace(NoComment) Then Continue For
            Dim Indent = NoComment.Length - NoComment.TrimStart(" "c).Length
            Dim Trim = NoComment.Trim()

            If Indent = 0 Then
                FlushPaperDep()
                InDependenciesSection = False
                InPaperServerDeps = False
                ListTarget = ""
            End If

            If Trim.StartsWith("-") AndAlso ListTarget <> "" Then
                Dim Item = UnquoteYamlScalar(Trim.Substring(1).Trim())
                If Item <> "" Then
                    If ListTarget = "depend" Then
                        Info.Depends.Add(Item)
                    ElseIf ListTarget = "softdepend" Then
                        Info.SoftDepends.Add(Item)
                    End If
                End If
                Continue For
            End If

            Dim ColonIdx = Trim.IndexOf(":"c)
            If ColonIdx <= 0 Then Continue For
            Dim Key = Trim.Substring(0, ColonIdx).Trim()
            Dim KeyLower = Key.ToLowerInvariant()
            Dim RawVal = Trim.Substring(ColonIdx + 1).Trim()

            If Indent = 0 Then
                Select Case KeyLower
                    Case "name"
                        If RawVal <> "" Then Info.Name = UnquoteYamlScalar(RawVal)
                    Case "version"
                        If RawVal <> "" Then Info.Version = UnquoteYamlScalar(RawVal)
                    Case "description"
                        If RawVal <> "" AndAlso RawVal <> "|" AndAlso RawVal <> ">" Then Info.Description = UnquoteYamlScalar(RawVal)
                    Case "api-version"
                        If RawVal <> "" Then Info.ApiVersion = UnquoteYamlScalar(RawVal)
                    Case "depend"
                        If RawVal.StartsWith("[") AndAlso RawVal.EndsWith("]") Then
                            ParseYamlInlineArray(RawVal, Info.Depends)
                        ElseIf RawVal <> "" Then
                            Info.Depends.Add(UnquoteYamlScalar(RawVal))
                        Else
                            ListTarget = "depend"
                        End If
                    Case "softdepend"
                        If RawVal.StartsWith("[") AndAlso RawVal.EndsWith("]") Then
                            ParseYamlInlineArray(RawVal, Info.SoftDepends)
                        ElseIf RawVal <> "" Then
                            Info.SoftDepends.Add(UnquoteYamlScalar(RawVal))
                        Else
                            ListTarget = "softdepend"
                        End If
                    Case "dependencies"
                        If IsPaperPlugin Then InDependenciesSection = True
                End Select
            ElseIf IsPaperPlugin AndAlso InDependenciesSection Then
                If Indent <= 2 AndAlso RawVal = "" Then
                    FlushPaperDep()
                    InPaperServerDeps = KeyLower = "server"
                ElseIf InPaperServerDeps AndAlso Indent <= 4 AndAlso RawVal = "" Then
                    FlushPaperDep()
                    CurrentPaperDepName = UnquoteYamlScalar(Key)
                    CurrentPaperDepRequired = True
                ElseIf InPaperServerDeps AndAlso CurrentPaperDepName <> "" AndAlso KeyLower = "required" Then
                    CurrentPaperDepRequired = Not RawVal.Equals("false", StringComparison.OrdinalIgnoreCase)
                End If
            End If
        Next
        FlushPaperDep()
    End Sub

    Private Sub ParseYamlInlineArray(RawBracketVal As String, Target As List(Of String))
        Dim Inner = RawBracketVal.Substring(1, RawBracketVal.Length - 2).Trim()
        If Inner = "" Then Return
        For Each Part In Inner.Split(","c)
            Dim Clean = UnquoteYamlScalar(Part.Trim())
            If Clean <> "" Then Target.Add(Clean)
        Next
    End Sub

    Private Function UnquoteYamlScalar(Raw As String) As String
        Dim S = If(Raw, "").Trim()
        If (S.StartsWith("""") AndAlso S.EndsWith("""") AndAlso S.Length >= 2) OrElse
           (S.StartsWith("'") AndAlso S.EndsWith("'") AndAlso S.Length >= 2) Then
            Return S.Substring(1, S.Length - 2).Trim()
        End If
        Return S
    End Function

    Private Function StripYamlComment(Line As String) As String
        Dim InSingle As Boolean = False
        Dim InDouble As Boolean = False
        For i As Integer = 0 To Line.Length - 1
            Dim Ch = Line(i)
            If Ch = """"c AndAlso Not InSingle Then
                InDouble = Not InDouble
            ElseIf Ch = "'"c AndAlso Not InDouble Then
                InSingle = Not InSingle
            ElseIf Ch = "#"c AndAlso Not InSingle AndAlso Not InDouble Then
                If i = 0 OrElse Char.IsWhiteSpace(Line(i - 1)) Then Return Line.Substring(0, i)
            End If
        Next
        Return Line
    End Function

    Public Function BuildOptimizationPreset(PresetName As String) As ServerPerformanceConfig
        Select Case If(PresetName, "").Trim().ToLowerInvariant()
            Case "smoothcoop", "smooth"
                Return New ServerPerformanceConfig With {
                    .ViewDistance = 10,
                    .SimulationDistance = 6,
                    .SpawnProtection = 0,
                    .NetworkCompressionThreshold = 256,
                    .MaxTickTime = 60000,
                    .AllowFlight = True,
                    .EnableCommandBlock = False,
                    .SyncChunkWrites = False,
                    .MonsterSpawnLimit = 50,
                    .AnimalSpawnLimit = 8,
                    .PaperMaxAutoSaveChunks = 8,
                    .PaperOptimizeExplosions = True,
                    .PaperPreventUnloadedChunks = True,
                    .JvmPreset = "Aikar"
                }
            Case "lowspec", "light"
                Return New ServerPerformanceConfig With {
                    .ViewDistance = 8,
                    .SimulationDistance = 5,
                    .SpawnProtection = 0,
                    .NetworkCompressionThreshold = 512,
                    .MaxTickTime = 60000,
                    .AllowFlight = True,
                    .EnableCommandBlock = False,
                    .SyncChunkWrites = False,
                    .MonsterSpawnLimit = 35,
                    .AnimalSpawnLimit = 6,
                    .PaperMaxAutoSaveChunks = 6,
                    .PaperOptimizeExplosions = True,
                    .PaperPreventUnloadedChunks = True,
                    .JvmPreset = "Aikar"
                }
            Case Else
                Return New ServerPerformanceConfig()
        End Select
    End Function

    Public Function ParseMemoryToMb(MemStr As String) As Integer
        If String.IsNullOrWhiteSpace(MemStr) Then Return 4096
        Dim M = Regex.Match(MemStr.Trim(), "^(\d+)\s*([KMGkmg])")
        If Not M.Success Then Return 4096
        Dim Num As Integer = 4096
        Integer.TryParse(M.Groups(1).Value, Num)
        Select Case M.Groups(2).Value.ToUpperInvariant()
            Case "G" : Return Math.Max(256, Num * 1024)
            Case "K" : Return Math.Max(256, Num \ 1024)
            Case Else : Return Math.Max(256, Num)
        End Select
    End Function

    Public Function BuildJvmPresetFlags(PresetName As String, XmxMb As Integer, Optional JavaMajor As Integer = 21) As String
        Select Case If(PresetName, "").Trim()
            Case "Aikar"
                Dim LargeHeap = XmxMb >= 12288
                Dim NewSize = If(LargeHeap, 40, 30)
                Dim MaxNewSize = If(LargeHeap, 50, 40)
                Dim RegionSize = If(LargeHeap, "16M", "8M")
                Dim Reserve = If(LargeHeap, 15, 20)
                Dim Ihop = If(LargeHeap, 20, 15)
                Return $"-XX:+UseG1GC -XX:+ParallelRefProcEnabled -XX:MaxGCPauseMillis=200 -XX:+UnlockExperimentalVMOptions -XX:+DisableExplicitGC -XX:+AlwaysPreTouch -XX:G1NewSizePercent={NewSize} -XX:G1MaxNewSizePercent={MaxNewSize} -XX:G1HeapRegionSize={RegionSize} -XX:G1ReservePercent={Reserve} -XX:G1HeapWastePercent=5 -XX:G1MixedGCCountTarget=4 -XX:InitiatingHeapOccupancyPercent={Ihop} -XX:G1MixedGCLiveThresholdPercent=90 -XX:G1RSetUpdatingPauseTimePercent=5 -XX:SurvivorRatio=32 -XX:+PerfDisableSharedMem -XX:MaxTenuringThreshold=1 -Dusing.aikars.flags=https://mcflags.emc.gs -Daikars.new.flags=true"
            Case "GenerationalZGC"
                'Java 23+ 默认启用分代 ZGC，无需且不可传 -XX:+ZGenerational
                If JavaMajor >= 23 Then
                    Return "-XX:+UseZGC -XX:+AlwaysPreTouch"
                ElseIf JavaMajor >= 21 Then
                    Return "-XX:+UseZGC -XX:+ZGenerational -XX:+AlwaysPreTouch"
                ElseIf JavaMajor >= 17 Then
                    Return "-XX:+UseZGC -XX:+AlwaysPreTouch"
                Else
                    Return "-XX:+UseG1GC -XX:+AlwaysPreTouch"
                End If
            Case Else
                Return ""
        End Select
    End Function

    Public Function LoadPerformanceConfig(ServerDir As String) As ServerPerformanceConfig
        Dim Cfg As New ServerPerformanceConfig()
        If String.IsNullOrWhiteSpace(ServerDir) OrElse Not Directory.Exists(ServerDir) Then Return Cfg

        Dim PropPath = Path.Combine(ServerDir, "server.properties")
        If File.Exists(PropPath) Then
            Try
                For Each Raw In File.ReadAllLines(PropPath, Encoding.UTF8)
                    Dim Trim = Raw.Trim()
                    If Trim = "" OrElse Trim.StartsWith("#") OrElse Not Trim.Contains("=") Then Continue For
                    Dim Eq = Trim.IndexOf("="c)
                    Dim Key = Trim.Substring(0, Eq).Trim().ToLowerInvariant()
                    Dim Val = Trim.Substring(Eq + 1).Trim()
                    Dim IntVal As Integer
                    Select Case Key
                        Case "simulation-distance"
                            If Integer.TryParse(Val, IntVal) Then Cfg.SimulationDistance = ClampInt(IntVal, 2, 32)
                        Case "view-distance"
                            If Integer.TryParse(Val, IntVal) Then Cfg.ViewDistance = ClampInt(IntVal, 2, 32)
                        Case "spawn-protection"
                            If Integer.TryParse(Val, IntVal) Then Cfg.SpawnProtection = ClampInt(IntVal, 0, 256)
                        Case "network-compression-threshold"
                            If Integer.TryParse(Val, IntVal) Then Cfg.NetworkCompressionThreshold = ClampInt(IntVal, -1, 65536)
                        Case "max-tick-time"
                            If Integer.TryParse(Val, IntVal) Then Cfg.MaxTickTime = ClampInt(IntVal, -1, 600000)
                        Case "allow-flight"
                            Cfg.AllowFlight = Val.Equals("true", StringComparison.OrdinalIgnoreCase)
                        Case "enable-command-block"
                            Cfg.EnableCommandBlock = Val.Equals("true", StringComparison.OrdinalIgnoreCase)
                        Case "sync-chunk-writes"
                            Cfg.SyncChunkWrites = Not Val.Equals("false", StringComparison.OrdinalIgnoreCase)
                    End Select
                Next
            Catch
            End Try
        End If

        Dim BukkitPath = Path.Combine(ServerDir, "bukkit.yml")
        If File.Exists(BukkitPath) Then
            Dim MonStr = GetNestedYamlScalar(BukkitPath, "spawn-limits", "monsters")
            Dim AniStr = GetNestedYamlScalar(BukkitPath, "spawn-limits", "animals")
            Dim IntVal As Integer
            If Integer.TryParse(MonStr, IntVal) Then Cfg.MonsterSpawnLimit = ClampInt(IntVal, 1, 500)
            If Integer.TryParse(AniStr, IntVal) Then Cfg.AnimalSpawnLimit = ClampInt(IntVal, 1, 500)
        End If

        Dim PaperWorldPath = Path.Combine(ServerDir, "config\paper-world-defaults.yml")
        If File.Exists(PaperWorldPath) Then
            Dim AutoSaveStr = GetNestedYamlScalar(PaperWorldPath, "chunks", "max-auto-save-chunks-per-tick")
            Dim PreventStr = GetNestedYamlScalar(PaperWorldPath, "chunks", "prevent-moving-into-unloaded-chunks")
            Dim OptExpStr = GetNestedYamlScalar(PaperWorldPath, "environment", "optimize-explosions")
            Dim IntVal As Integer
            If Integer.TryParse(AutoSaveStr, IntVal) Then Cfg.PaperMaxAutoSaveChunks = ClampInt(IntVal, 1, 100)
            If PreventStr <> "" Then Cfg.PaperPreventUnloadedChunks = PreventStr.Equals("true", StringComparison.OrdinalIgnoreCase)
            If OptExpStr <> "" Then Cfg.PaperOptimizeExplosions = OptExpStr.Equals("true", StringComparison.OrdinalIgnoreCase)
        End If

        Dim PerfJsonPath = Path.Combine(ServerDir, "pcl-server-perf.json")
        If File.Exists(PerfJsonPath) Then
            Try
                Dim Obj = JObject.Parse(File.ReadAllText(PerfJsonPath, Encoding.UTF8))
                Dim P = If(Obj("JvmPreset")?.ToString(), "").Trim()
                If P = "Standard" OrElse P = "Aikar" OrElse P = "GenerationalZGC" Then
                    Cfg.JvmPreset = P
                End If
            Catch
            End Try
        End If

        Return Cfg
    End Function

    Public Sub SavePerformanceConfig(ServerDir As String, Cfg As ServerPerformanceConfig, Optional AllowCreatePluginYaml As Boolean = False)
        If String.IsNullOrWhiteSpace(ServerDir) OrElse Cfg Is Nothing Then Return
        Directory.CreateDirectory(ServerDir)

        Dim PropUpdates As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"view-distance", ClampInt(Cfg.ViewDistance, 2, 32).ToString()},
            {"simulation-distance", ClampInt(Cfg.SimulationDistance, 2, 32).ToString()},
            {"spawn-protection", ClampInt(Cfg.SpawnProtection, 0, 256).ToString()},
            {"network-compression-threshold", ClampInt(Cfg.NetworkCompressionThreshold, -1, 65536).ToString()},
            {"max-tick-time", ClampInt(Cfg.MaxTickTime, -1, 600000).ToString()},
            {"allow-flight", If(Cfg.AllowFlight, "true", "false")},
            {"enable-command-block", If(Cfg.EnableCommandBlock, "true", "false")},
            {"sync-chunk-writes", If(Cfg.SyncChunkWrites, "true", "false")}
        }
        UpdatePropertiesAtomic(Path.Combine(ServerDir, "server.properties"), PropUpdates)

        Dim BukkitPath = Path.Combine(ServerDir, "bukkit.yml")
        If File.Exists(BukkitPath) OrElse AllowCreatePluginYaml Then
            SetNestedYamlScalars(BukkitPath, New List(Of (Section As String, Key As String, Value As String)) From {
                ("spawn-limits", "monsters", ClampInt(Cfg.MonsterSpawnLimit, 1, 500).ToString()),
                ("spawn-limits", "animals", ClampInt(Cfg.AnimalSpawnLimit, 1, 500).ToString())
            })
        End If

        Dim PaperWorldPath = Path.Combine(ServerDir, "config\paper-world-defaults.yml")
        If File.Exists(PaperWorldPath) OrElse AllowCreatePluginYaml Then
            SetNestedYamlScalars(PaperWorldPath, New List(Of (Section As String, Key As String, Value As String)) From {
                ("chunks", "max-auto-save-chunks-per-tick", ClampInt(Cfg.PaperMaxAutoSaveChunks, 1, 100).ToString()),
                ("chunks", "prevent-moving-into-unloaded-chunks", If(Cfg.PaperPreventUnloadedChunks, "true", "false")),
                ("environment", "optimize-explosions", If(Cfg.PaperOptimizeExplosions, "true", "false"))
            })
        End If

        Dim PerfJsonPath = Path.Combine(ServerDir, "pcl-server-perf.json")
        Dim JsonObj As New JObject From {{"JvmPreset", If(Cfg.JvmPreset, "Standard")}}
        WriteTextAtomic(PerfJsonPath, JsonObj.ToString())
    End Sub

    Private Function ClampInt(Value As Integer, MinVal As Integer, MaxVal As Integer) As Integer
        If Value < MinVal Then Return MinVal
        If Value > MaxVal Then Return MaxVal
        Return Value
    End Function

    Private Sub UpdatePropertiesAtomic(PropPath As String, Updates As Dictionary(Of String, String))
        Dim Lines As New List(Of String)
        Dim Written As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If File.Exists(PropPath) Then
            For Each Raw In File.ReadAllLines(PropPath, Encoding.UTF8)
                Dim Eq = Raw.IndexOf("="c)
                If Eq > 0 AndAlso Not Raw.TrimStart().StartsWith("#") Then
                    Dim Key = Raw.Substring(0, Eq).Trim()
                    If Updates.ContainsKey(Key) Then
                        Lines.Add($"{Key}={Updates(Key)}")
                        Written.Add(Key)
                        Continue For
                    End If
                End If
                Lines.Add(Raw)
            Next
        Else
            Lines.Add("# Minecraft server properties (updated by PCL Linking)")
        End If
        For Each Kv In Updates
            If Not Written.Contains(Kv.Key) Then Lines.Add($"{Kv.Key}={Kv.Value}")
        Next
        WriteTextAtomic(PropPath, String.Join(Environment.NewLine, Lines) & Environment.NewLine)
    End Sub

    Private Function GetNestedYamlScalar(YamlPath As String, SectionName As String, KeyName As String) As String
        If Not File.Exists(YamlPath) Then Return ""
        Try
            Dim SectionStack As New List(Of (Indent As Integer, Name As String))
            For Each Raw In File.ReadAllLines(YamlPath, Encoding.UTF8)
                Dim NoComment = StripYamlComment(Raw)
                If String.IsNullOrWhiteSpace(NoComment) Then Continue For
                Dim Indent = NoComment.Length - NoComment.TrimStart(" "c).Length
                Dim Trim = NoComment.Trim()
                If Trim.StartsWith("-") Then Continue For
                Dim ColonIdx = Trim.IndexOf(":"c)
                If ColonIdx <= 0 Then Continue For
                Dim K = UnquoteYamlScalar(Trim.Substring(0, ColonIdx).Trim())
                Dim V = UnquoteYamlScalar(Trim.Substring(ColonIdx + 1).Trim())
                While SectionStack.Count > 0 AndAlso SectionStack(SectionStack.Count - 1).Indent >= Indent
                    SectionStack.RemoveAt(SectionStack.Count - 1)
                End While
                If V = "" Then
                    SectionStack.Add((Indent, K))
                ElseIf K.Equals(KeyName, StringComparison.OrdinalIgnoreCase) AndAlso
                       SectionStack.Count > 0 AndAlso SectionStack(SectionStack.Count - 1).Name.Equals(SectionName, StringComparison.OrdinalIgnoreCase) Then
                    Return V
                End If
            Next
        Catch
        End Try
        Return ""
    End Function

    Private Sub SetNestedYamlScalars(YamlPath As String, Entries As List(Of (Section As String, Key As String, Value As String)))
        Dim ParentDir = Path.GetDirectoryName(YamlPath)
        If Not String.IsNullOrEmpty(ParentDir) Then Directory.CreateDirectory(ParentDir)

        Dim Lines As List(Of String) = If(File.Exists(YamlPath), File.ReadAllLines(YamlPath, Encoding.UTF8).ToList(), New List(Of String) From {"# Managed by PCL Linking"})
        For Each Entry In Entries
            Lines = UpsertSingleYamlKey(Lines, Entry.Section, Entry.Key, Entry.Value)
        Next
        WriteTextAtomic(YamlPath, String.Join(Environment.NewLine, Lines) & Environment.NewLine)
    End Sub

    Private Function UpsertSingleYamlKey(Lines As List(Of String), SectionName As String, KeyName As String, ValueStr As String) As List(Of String)
        Dim OutLines As New List(Of String)(Lines)
        Dim SectionIdx As Integer = -1
        Dim SectionIndent As Integer = 0
        Dim Stack As New List(Of (Indent As Integer, Name As String))

        For i As Integer = 0 To OutLines.Count - 1
            Dim NoComment = StripYamlComment(OutLines(i))
            If String.IsNullOrWhiteSpace(NoComment) Then Continue For
            Dim Indent = NoComment.Length - NoComment.TrimStart(" "c).Length
            Dim Trim = NoComment.Trim()
            If Trim.StartsWith("-") Then Continue For
            Dim ColonIdx = Trim.IndexOf(":"c)
            If ColonIdx <= 0 Then Continue For
            Dim K = UnquoteYamlScalar(Trim.Substring(0, ColonIdx).Trim())
            Dim V = Trim.Substring(ColonIdx + 1).Trim()

            While Stack.Count > 0 AndAlso Stack(Stack.Count - 1).Indent >= Indent
                Stack.RemoveAt(Stack.Count - 1)
            End While
            If V = "" Then
                If Indent = 0 AndAlso K.Equals(SectionName, StringComparison.OrdinalIgnoreCase) AndAlso SectionIdx = -1 Then
                    SectionIdx = i
                    SectionIndent = Indent
                End If
                Stack.Add((Indent, K))
            ElseIf K.Equals(KeyName, StringComparison.OrdinalIgnoreCase) AndAlso
                   Stack.Count > 0 AndAlso Stack(Stack.Count - 1).Name.Equals(SectionName, StringComparison.OrdinalIgnoreCase) Then
                OutLines(i) = New String(" "c, Indent) & $"{KeyName}: {ValueStr}"
                Return OutLines
            End If
        Next

        If SectionIdx >= 0 Then
            OutLines.Insert(SectionIdx + 1, New String(" "c, SectionIndent + 2) & $"{KeyName}: {ValueStr}")
        Else
            OutLines.Add($"{SectionName}:")
            OutLines.Add($"  {KeyName}: {ValueStr}")
        End If
        Return OutLines
    End Function

    Private Sub WriteTextAtomic(TargetPath As String, Content As String)
        Dim Parent = Path.GetDirectoryName(TargetPath)
        If Not String.IsNullOrEmpty(Parent) Then Directory.CreateDirectory(Parent)
        Dim TmpPath = TargetPath & ".tmp"
        File.WriteAllText(TmpPath, Content, New UTF8Encoding(False))
        If File.Exists(TargetPath) Then
            File.Replace(TmpPath, TargetPath, Nothing, True)
        Else
            File.Move(TmpPath, TargetPath)
        End If
    End Sub

End Module
