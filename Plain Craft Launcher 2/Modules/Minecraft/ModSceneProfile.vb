Imports System.IO
Imports Newtonsoft.Json

''' <summary>
''' 实例场景模式预设：按「单人画质向」「多人高帧联机向」或自定义配置一键切换内存、GC、JVM、options.txt 与 Mod 启用状态。
''' </summary>
Public Module ModSceneProfile

    Public Const BuiltInSoloVisualId As String = "builtin_solo_visual"
    Public Const BuiltInMultiFpsId As String = "builtin_multi_fps"

    Private ReadOnly VisualOnlyModKeywords As String() = {
        "iris", "oculus", "distanthorizons", "shader", "blur", "sound-physics", "soundphysics"
    }

    Public Class SceneProfile
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Description As String = ""
        Public Property IsBuiltIn As Boolean = False
        Public Property RamType As Integer = 2
        Public Property RamCustom As Integer = 21
        Public Property GcMode As Integer = 0
        Public Property ApplyJvmArgs As Boolean = True
        Public Property JvmArgs As String = ""
        Public Property OptionRenderDistance As Integer? = Nothing
        Public Property OptionMaxFps As Integer? = Nothing
        Public Property OptionEnableVsync As Boolean? = Nothing
        Public Property EnabledMods As New List(Of String)
        Public Property DisabledMods As New List(Of String)
        Public Property CreatedAtUtc As DateTime = DateTime.UtcNow
    End Class

    Public Class SceneApplyResult
        Public Property Success As Boolean
        Public Property Summary As String = ""
        Public Property ToggledModCount As Integer = 0
    End Class

    Private ReadOnly ProfileLock As New Object()

    Private Function GetProfileFilePath(Instance As McInstance) As String
        Return Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathVersion), "PCL Linking", "SceneProfiles.json")
    End Function

    Private Function GetSceneDisabledFilePath(Instance As McInstance) As String
        Return Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathVersion), "PCL Linking", "SceneDisabledMods.json")
    End Function

    Public Function GetBuiltInProfiles() As List(Of SceneProfile)
        Return New List(Of SceneProfile) From {
            New SceneProfile With {
                .Id = BuiltInSoloVisualId,
                .Name = "单人画质向（推荐光影/建筑）",
                .Description = "自动动态内存 + 调优 G1GC + 低卡顿参数 + 视距 14 + 帧率不限 + 尝试恢复光影加载器 Mod（Iris/Oculus）。",
                .IsBuiltIn = True,
                .RamType = 0,
                .RamCustom = 21,
                .GcMode = 5,
                .ApplyJvmArgs = True,
                .JvmArgs = "-Dfile.encoding=UTF-8 -XX:+UnlockExperimentalVMOptions -XX:+AlwaysPreTouch -XX:+DisableExplicitGC",
                .OptionRenderDistance = 14,
                .OptionMaxFps = 260,
                .OptionEnableVsync = False
            },
            New SceneProfile With {
                .Id = BuiltInMultiFpsId,
                .Name = "多人高帧联机向（推荐组网联机）",
                .Description = "自动动态内存 + 调优 G1GC 降卡顿 + 视距 8 降低远程联机区块传输压力 + 自动临时禁用纯画质光影扩展 Mod（Iris/Oculus/DistantHorizons 等）。",
                .IsBuiltIn = True,
                .RamType = 0,
                .RamCustom = 17,
                .GcMode = 5,
                .ApplyJvmArgs = True,
                .JvmArgs = "-Dfile.encoding=UTF-8 -XX:+UnlockExperimentalVMOptions -XX:+AlwaysPreTouch -XX:+DisableExplicitGC",
                .OptionRenderDistance = 8,
                .OptionMaxFps = 260,
                .OptionEnableVsync = False
            }
        }
    End Function

    Public Function ListProfiles(Instance As McInstance) As List(Of SceneProfile)
        Dim Result = GetBuiltInProfiles()
        If Instance Is Nothing Then Return Result
        SyncLock ProfileLock
            Try
                Dim JsonPath = GetProfileFilePath(Instance)
                If File.Exists(JsonPath) Then
                    Dim Raw = File.ReadAllText(JsonPath, Text.Encoding.UTF8)
                    Dim CustomList = JsonConvert.DeserializeObject(Of List(Of SceneProfile))(Raw)
                    If CustomList IsNot Nothing Then
                        For Each Item In CustomList
                            If Item IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(Item.Id) AndAlso Not Item.IsBuiltIn Then
                                Result.Add(Item)
                            End If
                        Next
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, "读取自定义场景预设失败")
            End Try
        End SyncLock
        Return Result
    End Function

    Private Sub SaveCustomProfiles(Instance As McInstance, CustomProfiles As List(Of SceneProfile))
        Dim JsonPath = GetProfileFilePath(Instance)
        Dim ParentDir = Path.GetDirectoryName(JsonPath)
        If Not String.IsNullOrEmpty(ParentDir) AndAlso Not Directory.Exists(ParentDir) Then
            Directory.CreateDirectory(ParentDir)
        End If
        Dim TmpPath = JsonPath & ".tmp"
        File.WriteAllText(TmpPath, JsonConvert.SerializeObject(CustomProfiles, Formatting.Indented), New Text.UTF8Encoding(False))
        If File.Exists(JsonPath) Then
            File.Replace(TmpPath, JsonPath, Nothing, True)
        Else
            File.Move(TmpPath, JsonPath)
        End If
    End Sub

    Public Function SaveCurrentAsCustomProfile(Instance As McInstance, ProfileName As String) As SceneProfile
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))
        Dim CleanName = If(ProfileName, "").Trim()
        If String.IsNullOrWhiteSpace(CleanName) Then Throw New ArgumentException("方案名称不能为空。")

        Dim Profile As New SceneProfile With {
            .Id = "custom_" & Guid.NewGuid().ToString("N").Substring(0, 8),
            .Name = CleanName,
            .Description = $"自定义场景：{DateTime.Now:yyyy-MM-dd HH:mm} 保存当前内存/GC/JVM/视频设置与 Mod 开关状态。",
            .IsBuiltIn = False,
            .RamType = Settings.Get(Of Integer)("VersionRamType", Instance:=Instance),
            .RamCustom = Settings.Get(Of Integer)("VersionRamCustom", Instance:=Instance),
            .GcMode = Settings.Get(Of Integer)("VersionAdvanceGC", Instance:=Instance),
            .ApplyJvmArgs = True,
            .JvmArgs = Settings.Get(Of String)("VersionAdvanceJvm", Instance:=Instance),
            .CreatedAtUtc = DateTime.UtcNow
        }

        '读取当前 options.txt 中的视距与帧率
        Dim Opts = ReadOptionsDictionary(Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathIndie), "options.txt"))
        Dim Rd As Integer
        If Opts.ContainsKey("renderDistance") AndAlso Integer.TryParse(Opts("renderDistance"), Rd) Then Profile.OptionRenderDistance = Rd
        Dim Fps As Integer
        If Opts.ContainsKey("maxFps") AndAlso Integer.TryParse(Opts("maxFps"), Fps) Then Profile.OptionMaxFps = Fps
        Dim Vsync As Boolean
        If Opts.ContainsKey("enableVsync") AndAlso Boolean.TryParse(Opts("enableVsync"), Vsync) Then Profile.OptionEnableVsync = Vsync

        '记录 mods\ 中已启用与已禁用的文件基础名（去重）
        Dim ModsDir = Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathIndie), "mods")
        If Directory.Exists(ModsDir) Then
            Dim EnabledSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim DisabledSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each FilePath In Directory.GetFiles(ModsDir)
                Dim FName = Path.GetFileName(FilePath)
                Dim Lower = FName.ToLowerInvariant()
                If Lower.EndsWith(".jar") OrElse Lower.EndsWith(".litemod") Then
                    If EnabledSet.Add(FName) Then Profile.EnabledMods.Add(FName)
                ElseIf Lower.EndsWith(".jar.disabled") OrElse Lower.EndsWith(".litemod.disabled") Then
                    Dim BaseName = FName.Substring(0, FName.Length - ".disabled".Length)
                    If DisabledSet.Add(BaseName) Then Profile.DisabledMods.Add(BaseName)
                ElseIf Lower.EndsWith(".jar.old") OrElse Lower.EndsWith(".litemod.old") Then
                    Dim BaseName = FName.Substring(0, FName.Length - ".old".Length)
                    If DisabledSet.Add(BaseName) Then Profile.DisabledMods.Add(BaseName)
                End If
            Next
        End If

        SyncLock ProfileLock
            Dim Existing = ListProfiles(Instance).Where(Function(p) Not p.IsBuiltIn).ToList()
            Existing.Add(Profile)
            SaveCustomProfiles(Instance, Existing)
        End SyncLock
        Return Profile
    End Function

    Public Function DeleteCustomProfile(Instance As McInstance, ProfileId As String) As Boolean
        If Instance Is Nothing OrElse String.IsNullOrWhiteSpace(ProfileId) Then Return False
        SyncLock ProfileLock
            Dim Existing = ListProfiles(Instance).Where(Function(p) Not p.IsBuiltIn).ToList()
            Dim Removed = Existing.RemoveAll(Function(p) String.Equals(p.Id, ProfileId, StringComparison.OrdinalIgnoreCase))
            If Removed > 0 Then
                SaveCustomProfiles(Instance, Existing)
                Return True
            End If
        End SyncLock
        Return False
    End Function

    Private Function IsInstanceBusyOrRunning(Instance As McInstance) As Boolean
        If Instance Is Nothing Then Return False
        If McInstanceLock.IsLocked(Instance) Then Return True
        Dim TargetNorm = PathUtils.RemoveExtendedPrefix(Instance.PathVersion).TrimEnd("\"c, "/"c)
        For Each W In ModWatcher.McWatcherList.ToArray()
            If W Is Nothing OrElse W.Instance Is Nothing Then Continue For
            If W.State = ModWatcher.Watcher.MinecraftState.Ended OrElse W.State = ModWatcher.Watcher.MinecraftState.Canceled Then Continue For
            Try
                If W.GameProcess IsNot Nothing AndAlso W.GameProcess.HasExited Then Continue For
            Catch
                Continue For
            End Try
            Dim WatchNorm = PathUtils.RemoveExtendedPrefix(W.Instance.PathVersion).TrimEnd("\"c, "/"c)
            If String.Equals(WatchNorm, TargetNorm, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

    Private Function MergeJvmTokens(ExistingArgs As String, ExtraTokens As String) As String
        Dim Result As New List(Of String)
        Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each Part In If(ExistingArgs, "").Split({" "c, ChrW(9), ChrW(10), ChrW(13)}, StringSplitOptions.RemoveEmptyEntries)
            If Seen.Add(Part) Then Result.Add(Part)
        Next
        For Each Tok In If(ExtraTokens, "").Split({" "c, ChrW(9), ChrW(10), ChrW(13)}, StringSplitOptions.RemoveEmptyEntries)
            If Seen.Add(Tok) Then Result.Add(Tok)
        Next
        Return String.Join(" ", Result)
    End Function

    Public Function ApplyProfile(Instance As McInstance, ProfileId As String) As SceneApplyResult
        If Instance Is Nothing Then Return New SceneApplyResult With {.Success = False, .Summary = "当前未选择实例。"}
        If IsInstanceBusyOrRunning(Instance) Then
            Return New SceneApplyResult With {.Success = False, .Summary = $"实例「{Instance.Name}」当前正在运行或被占用，请先关闭游戏后再切换场景模式。"}
        End If
        Dim Target = ListProfiles(Instance).FirstOrDefault(Function(p) String.Equals(p.Id, ProfileId, StringComparison.OrdinalIgnoreCase))
        If Target Is Nothing Then Return New SceneApplyResult With {.Success = False, .Summary = "未找到对应的场景配置方案。"}

        '1. 应用实例内存 / GC / JVM 设置
        Settings.Set("VersionRamType", Target.RamType, Instance:=Instance)
        If Target.RamType = 1 Then
            Settings.Set("VersionRamCustom", Target.RamCustom, Instance:=Instance)
        End If
        Settings.Set("VersionAdvanceGC", Target.GcMode, Instance:=Instance)
        If Target.ApplyJvmArgs Then
            If Target.IsBuiltIn Then
                Dim CurJvm = Settings.Get(Of String)("VersionAdvanceJvm", Instance:=Instance)
                If String.IsNullOrWhiteSpace(CurJvm) Then CurJvm = Settings.Get(Of String)("LaunchAdvanceJvm")
                Settings.Set("VersionAdvanceJvm", MergeJvmTokens(CurJvm, Target.JvmArgs), Instance:=Instance)
            Else
                Settings.Set("VersionAdvanceJvm", If(Target.JvmArgs, ""), Instance:=Instance)
            End If
        End If

        '2. 更新 options.txt 中的视距 / 最大帧率 / 垂直同步
        Dim OptsUpdated = UpdateOptionsTxt(Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathIndie), "options.txt"),
                                           Target.OptionRenderDistance, Target.OptionMaxFps, Target.OptionEnableVsync)

        '3. 应用 Mod 开关状态
        Dim ToggledNames As New List(Of String)
        Dim ModsDir = Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathIndie), "mods")
        If Directory.Exists(ModsDir) Then
            If Target.Id = BuiltInSoloVisualId Then
                ToggledNames.AddRange(SetVisualModsState(Instance, ModsDir, EnableVisual:=True))
            ElseIf Target.Id = BuiltInMultiFpsId Then
                ToggledNames.AddRange(SetVisualModsState(Instance, ModsDir, EnableVisual:=False))
            Else
                ToggledNames.AddRange(ApplyCustomModStates(ModsDir, Target.EnabledMods, Target.DisabledMods))
            End If
        End If

        Dim SummaryParts As New List(Of String) From {
            $"已应用「{Target.Name}」"
        }
        If Target.OptionRenderDistance.HasValue Then
            SummaryParts.Add(If(OptsUpdated, $"视距已调为 {Target.OptionRenderDistance.Value}", "未找到 options.txt，已跳过视频选项修改（请先启动一次游戏后再应用）"))
        End If
        If ToggledNames.Any() Then
            SummaryParts.Add($"已切换 {ToggledNames.Count} 个 Mod 启用状态（{String.Join("、", ToggledNames.Take(4))}{If(ToggledNames.Count > 4, " 等", "")}）")
        End If

        Return New SceneApplyResult With {
            .Success = True,
            .ToggledModCount = ToggledNames.Count,
            .Summary = String.Join("；", SummaryParts)
        }
    End Function

    Private Function IsVisualOnlyModFileName(BaseName As String) As Boolean
        Dim Lower = If(BaseName, "").ToLowerInvariant()
        If Lower.Contains("satin") OrElse Lower.Contains("-api") OrElse Lower.Contains("_api") OrElse
           Lower.Contains("api-") OrElse Lower.Contains("-lib") OrElse Lower.Contains("_lib") Then
            Return False
        End If
        Dim Stem = Lower
        For Each Suffix In {".disabled", ".old", ".jar", ".litemod"}
            If Stem.EndsWith(Suffix) Then Stem = Stem.Substring(0, Stem.Length - Suffix.Length)
        Next
        Return System.Text.RegularExpressions.Regex.IsMatch(
            Stem,
            "(?:^|[-_.])(iris|oculus|distanthorizons|shaderpacks?|blur|sound-?physics)(?:[-_.0-9]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    End Function

    Private Function LoadSceneDisabledList(Instance As McInstance) As List(Of String)
        Try
            Dim P = GetSceneDisabledFilePath(Instance)
            If Not File.Exists(P) Then Return Nothing
            Dim Loaded = JsonConvert.DeserializeObject(Of List(Of String))(File.ReadAllText(P, Text.Encoding.UTF8))
            If Loaded Is Nothing Then Return New List(Of String)
            Return Loaded.Where(Function(s) Not String.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Catch
            Return Nothing
        End Try
    End Function

    Private Sub SaveSceneDisabledList(Instance As McInstance, Names As List(Of String))
        Try
            Dim P = GetSceneDisabledFilePath(Instance)
            If Names Is Nothing OrElse Not Names.Any() Then
                If File.Exists(P) Then File.Delete(P)
                Return
            End If
            Directory.CreateDirectory(Path.GetDirectoryName(P))
            Dim TmpPath = P & ".tmp"
            File.WriteAllText(TmpPath, JsonConvert.SerializeObject(Names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Formatting.Indented), New Text.UTF8Encoding(False))
            If File.Exists(P) Then
                File.Replace(TmpPath, P, Nothing, True)
            Else
                File.Move(TmpPath, P)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "保存场景模式禁用 Mod 记录失败")
        End Try
    End Sub

    Private Function SetVisualModsState(Instance As McInstance, ModsDir As String, EnableVisual As Boolean) As List(Of String)
        Dim Changed As New List(Of String)
        If EnableVisual Then
            Dim RecordedDisabled = LoadSceneDisabledList(Instance)
            Dim HasRecordedList = (RecordedDisabled IsNot Nothing)
            Dim RecordedSet = If(HasRecordedList, New HashSet(Of String)(RecordedDisabled, StringComparer.OrdinalIgnoreCase), Nothing)
            Dim StillDisabled As New List(Of String)
            For Each FilePath In Directory.GetFiles(ModsDir)
                Dim Name = Path.GetFileName(FilePath)
                Dim Lower = Name.ToLowerInvariant()
                Dim SuffixLen = If(Lower.EndsWith(".jar.disabled") OrElse Lower.EndsWith(".litemod.disabled"), ".disabled".Length,
                                If(Lower.EndsWith(".jar.old") OrElse Lower.EndsWith(".litemod.old"), ".old".Length, 0))
                If SuffixLen <= 0 Then Continue For
                Dim BaseName = Name.Substring(0, Name.Length - SuffixLen)
                Dim ShouldEnable = If(HasRecordedList, RecordedSet.Contains(BaseName), IsVisualOnlyModFileName(Name))
                If Not ShouldEnable Then Continue For
                Try
                    Dim Dest = FilePath.Substring(0, FilePath.Length - SuffixLen)
                    If Not File.Exists(Dest) Then
                        File.Move(FilePath, Dest)
                        Changed.Add($"启用 {Path.GetFileName(Dest)}")
                    Else
                        If HasRecordedList Then StillDisabled.Add(BaseName)
                    End If
                Catch ex As Exception
                    If HasRecordedList Then StillDisabled.Add(BaseName)
                    Logger.Warn(ex, $"启用场景 Mod 状态失败: {FilePath}")
                End Try
            Next
            If HasRecordedList Then SaveSceneDisabledList(Instance, StillDisabled)
        Else
            Dim Recorded = If(LoadSceneDisabledList(Instance), New List(Of String))
            Dim RecordedSet As New HashSet(Of String)(Recorded, StringComparer.OrdinalIgnoreCase)
            For Each FilePath In Directory.GetFiles(ModsDir)
                Dim Name = Path.GetFileName(FilePath)
                Dim Lower = Name.ToLowerInvariant()
                If (Lower.EndsWith(".jar") OrElse Lower.EndsWith(".litemod")) AndAlso IsVisualOnlyModFileName(Name) Then
                    Try
                        Dim Dest = FilePath & ".disabled"
                        If Not File.Exists(Dest) Then
                            File.Move(FilePath, Dest)
                            Changed.Add($"禁用 {Name}")
                            RecordedSet.Add(Name)
                        End If
                    Catch ex As Exception
                        Logger.Warn(ex, $"禁用场景 Mod 状态失败: {FilePath}")
                    End Try
                End If
            Next
            SaveSceneDisabledList(Instance, RecordedSet.ToList())
        End If
        Return Changed
    End Function

    Private Function ApplyCustomModStates(ModsDir As String, EnabledList As List(Of String), DisabledList As List(Of String)) As List(Of String)
        Dim Changed As New List(Of String)
        Dim EnabledSet As New HashSet(Of String)(If(EnabledList, New List(Of String)), StringComparer.OrdinalIgnoreCase)
        Dim DisabledSet As New HashSet(Of String)(If(DisabledList, New List(Of String)), StringComparer.OrdinalIgnoreCase)

        For Each FilePath In Directory.GetFiles(ModsDir)
            Dim Name = Path.GetFileName(FilePath)
            Dim Lower = Name.ToLowerInvariant()
            Try
                If Lower.EndsWith(".jar") OrElse Lower.EndsWith(".litemod") Then
                    If DisabledSet.Contains(Name) Then
                        Dim Dest = FilePath & ".disabled"
                        If Not File.Exists(Dest) Then
                            File.Move(FilePath, Dest)
                            Changed.Add($"禁用 {Name}")
                        End If
                    End If
                ElseIf Lower.EndsWith(".jar.disabled") OrElse Lower.EndsWith(".litemod.disabled") Then
                    Dim BaseName = Name.Substring(0, Name.Length - ".disabled".Length)
                    If EnabledSet.Contains(BaseName) Then
                        Dim Dest = FilePath.Substring(0, FilePath.Length - ".disabled".Length)
                        If Not File.Exists(Dest) Then
                            File.Move(FilePath, Dest)
                            Changed.Add($"启用 {BaseName}")
                        End If
                    End If
                ElseIf Lower.EndsWith(".jar.old") OrElse Lower.EndsWith(".litemod.old") Then
                    Dim BaseName = Name.Substring(0, Name.Length - ".old".Length)
                    If EnabledSet.Contains(BaseName) Then
                        Dim Dest = FilePath.Substring(0, FilePath.Length - ".old".Length)
                        If Not File.Exists(Dest) Then
                            File.Move(FilePath, Dest)
                            Changed.Add($"启用 {BaseName}")
                        End If
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"还原自定义场景 Mod 状态失败: {FilePath}")
            End Try
        Next
        Return Changed
    End Function

    Private Function ReadOptionsDictionary(OptionsPath As String) As Dictionary(Of String, String)
        Dim Dict As New Dictionary(Of String, String)(StringComparer.Ordinal)
        If Not File.Exists(OptionsPath) Then Return Dict
        Try
            For Each Line In File.ReadAllLines(OptionsPath, Text.Encoding.UTF8)
                Dim Idx = Line.IndexOf(":"c)
                If Idx > 0 Then
                    Dim Key = Line.Substring(0, Idx).Trim()
                    Dim Val = Line.Substring(Idx + 1).Trim()
                    Dict(Key) = Val
                End If
            Next
        Catch ex As Exception
            Logger.Warn(ex, $"读取 options.txt 失败: {OptionsPath}")
        End Try
        Return Dict
    End Function

    Private Function UpdateOptionsTxt(OptionsPath As String, RenderDistance As Integer?, MaxFps As Integer?, EnableVsync As Boolean?) As Boolean
        If Not RenderDistance.HasValue AndAlso Not MaxFps.HasValue AndAlso Not EnableVsync.HasValue Then Return False
        If Not File.Exists(OptionsPath) Then Return False
        Try
            Dim Lines = File.ReadAllLines(OptionsPath, Text.Encoding.UTF8).ToList()
            Dim Updates As New Dictionary(Of String, String)(StringComparer.Ordinal)
            If RenderDistance.HasValue Then Updates("renderDistance") = RenderDistance.Value.ToString()
            If MaxFps.HasValue Then Updates("maxFps") = MaxFps.Value.ToString()
            If EnableVsync.HasValue Then Updates("enableVsync") = If(EnableVsync.Value, "true", "false")

            Dim Found As New HashSet(Of String)(StringComparer.Ordinal)
            For I = 0 To Lines.Count - 1
                Dim Line = Lines(I)
                Dim Idx = Line.IndexOf(":"c)
                If Idx > 0 Then
                    Dim Key = Line.Substring(0, Idx).Trim()
                    If Updates.ContainsKey(Key) Then
                        Lines(I) = $"{Key}:{Updates(Key)}"
                        Found.Add(Key)
                    End If
                End If
            Next
            For Each Kv In Updates
                If Not Found.Contains(Kv.Key) Then Lines.Add($"{Kv.Key}:{Kv.Value}")
            Next
            File.WriteAllLines(OptionsPath, Lines, New Text.UTF8Encoding(False))
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"更新 options.txt 失败: {OptionsPath}")
            Return False
        End Try
    End Function

End Module
