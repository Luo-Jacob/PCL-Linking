' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone Minecraft options.txt parser, selective keybind/video/audio migrator, and keybind conflict detector/resolver (no PCL dependencies).

Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text

Public Module OptionsKeybindCore

    Public Class KeybindEntry
        Public Property OptionKey As String = ""
        Public Property DisplayName As String = ""
        Public Property RawValue As String = ""
        Public Property IsVanilla As Boolean = False
    End Class

    Public Class KeyConflictGroup
        Public Property NormalizedKey As String = ""
        Public Property DisplayKey As String = ""
        Public Property Entries As New List(Of KeybindEntry)
    End Class

    Private ReadOnly Utf8NoBom As New UTF8Encoding(False)

    Private ReadOnly AudioOptionKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "soundDevice", "showSubtitles", "directionalAudio"
    }

    Private ReadOnly VideoOptionKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        "renderDistance", "simulationDistance", "entityDistanceScaling", "fov", "maxFps",
        "enableVsync", "fullscreen", "graphicsMode", "ao", "biomeBlendRadius",
        "mipmapLevels", "particles", "renderClouds", "entityShadows", "gamma",
        "guiScale", "bobView", "attackIndicator", "autosaveIndicator", "glintSpeed",
        "glintStrength", "fovEffectScale", "distortionEffectScale", "darknessEffectScale",
        "screenEffectScale", "prioritizeChunkUpdates", "inactivityFpsLimit",
        "menuBackgroundBlurriness", "overrideWidth", "overrideHeight", "fullscreenResolution",
        "fancyGraphics", "clouds", "fboEnable", "useVbo"
    }

    Private ReadOnly VanillaKeybindNames As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"key_key.attack", "攻击 / 破坏 (原版)"},
        {"key_key.use", "使用物品 / 放置方块 (原版)"},
        {"key_key.forward", "前进 (原版)"},
        {"key_key.left", "向左移动 (原版)"},
        {"key_key.back", "后退 (原版)"},
        {"key_key.right", "向右移动 (原版)"},
        {"key_key.jump", "跳跃 (原版)"},
        {"key_key.sneak", "潜行 (原版)"},
        {"key_key.sprint", "疾跑 (原版)"},
        {"key_key.drop", "丢弃物品 (原版)"},
        {"key_key.inventory", "打开 / 关闭物品栏 (原版)"},
        {"key_key.chat", "打开聊天栏 (原版)"},
        {"key_key.playerlist", "显示玩家列表 (原版)"},
        {"key_key.pickItem", "选取方块 (原版)"},
        {"key_key.command", "输入命令 (原版)"},
        {"key_key.socialInteractions", "社交互动界面 (原版)"},
        {"key_key.screenshot", "截图 (原版)"},
        {"key_key.togglePerspective", "切换视角 (原版)"},
        {"key_key.smoothCamera", "切换电影视角 (原版)"},
        {"key_key.fullscreen", "切换全屏 (原版)"},
        {"key_key.spectatorOutlines", "高亮旁观玩家 (原版)"},
        {"key_key.swapOffhand", "与副手交换物品 (原版)"},
        {"key_key.saveToolbarActivator", "保存快捷栏 (原版)"},
        {"key_key.loadToolbarActivator", "加载快捷栏 (原版)"},
        {"key_key.advancements", "进度界面 (原版)"},
        {"key_key.hotbar.1", "快捷栏 1 (原版)"},
        {"key_key.hotbar.2", "快捷栏 2 (原版)"},
        {"key_key.hotbar.3", "快捷栏 3 (原版)"},
        {"key_key.hotbar.4", "快捷栏 4 (原版)"},
        {"key_key.hotbar.5", "快捷栏 5 (原版)"},
        {"key_key.hotbar.6", "快捷栏 6 (原版)"},
        {"key_key.hotbar.7", "快捷栏 7 (原版)"},
        {"key_key.hotbar.8", "快捷栏 8 (原版)"},
        {"key_key.hotbar.9", "快捷栏 9 (原版)"}
    }

    ''' <summary>
    ''' 将 options.txt 的单个配置键分类为 keybinds、audio、video 或 other。
    ''' </summary>
    Public Function ClassifyOptionKey(Key As String) As String
        If String.IsNullOrWhiteSpace(Key) Then Return "other"
        Dim K = Key.Trim()
        If K.StartsWith("key_", StringComparison.OrdinalIgnoreCase) Then Return "keybinds"
        If K.StartsWith("soundCategory_", StringComparison.OrdinalIgnoreCase) OrElse AudioOptionKeys.Contains(K) Then Return "audio"
        If VideoOptionKeys.Contains(K) Then Return "video"
        Return "other"
    End Function

    ''' <summary>
    ''' 按指定范围从源 options.txt 迁移配置到目标 options.txt，保留未迁移的目标项并自动生成 .bak 备份。
    ''' Scope 支持："keybinds"、"keybinds_video_audio"、"all"。
    ''' </summary>
    Public Function MigrateOptionsTxt(SourcePath As String, TargetPath As String, Scope As String) As (MigratedCount As Integer, BackupPath As String)
        If String.IsNullOrWhiteSpace(SourcePath) OrElse Not File.Exists(SourcePath) Then
            Throw New FileNotFoundException("未找到源 options.txt 文件。", SourcePath)
        End If
        If String.IsNullOrWhiteSpace(TargetPath) Then
            Throw New ArgumentException("目标 options.txt 路径不能为空。", NameOf(TargetPath))
        End If

        Dim NormalizedScope = If(Scope, "keybinds").Trim().ToLowerInvariant()
        Dim SourceEntries = ParseOptionsDictionary(SourcePath)
        Dim SelectedEntries As New Dictionary(Of String, String)(StringComparer.Ordinal)

        For Each Kv In SourceEntries
            If ShouldIncludeInScope(Kv.Key, NormalizedScope) Then
                SelectedEntries(Kv.Key) = Kv.Value
            End If
        Next

        If SelectedEntries.Count = 0 Then
            Return (0, "")
        End If

        Dim TargetDir = Path.GetDirectoryName(Path.GetFullPath(TargetPath))
        If Not String.IsNullOrEmpty(TargetDir) AndAlso Not Directory.Exists(TargetDir) Then
            Directory.CreateDirectory(TargetDir)
        End If

        Dim BackupPath = ""
        Dim ExistingLines As New List(Of String)
        If File.Exists(TargetPath) Then
            BackupPath = TargetPath & ".bak"
            File.Copy(TargetPath, BackupPath, True)
            ExistingLines.AddRange(File.ReadAllLines(TargetPath, Utf8NoBom))
        End If

        Dim RemainingToAppend As New Dictionary(Of String, String)(SelectedEntries, StringComparer.Ordinal)
        Dim OutputLines As New List(Of String)(ExistingLines.Count + SelectedEntries.Count)

        For Each Line In ExistingLines
            Dim Key As String = Nothing
            Dim Value As String = Nothing
            If TryParseOptionLine(Line, Key, Value) AndAlso RemainingToAppend.ContainsKey(Key) Then
                OutputLines.Add(Key & ":" & RemainingToAppend(Key))
                RemainingToAppend.Remove(Key)
            Else
                OutputLines.Add(Line)
            End If
        Next

        For Each Kv In RemainingToAppend
            OutputLines.Add(Kv.Key & ":" & Kv.Value)
        Next

        WriteLinesAtomically(TargetPath, OutputLines)
        Return (SelectedEntries.Count, BackupPath)
    End Function

    ''' <summary>
    ''' 检测 options.txt 中绑定到同一按键的冲突项。自动忽略未绑定值并保留 Forge 组合键修饰符差异。
    ''' </summary>
    Public Function DetectKeybindConflicts(OptionsPath As String) As List(Of KeyConflictGroup)
        Dim Result As New List(Of KeyConflictGroup)
        If String.IsNullOrWhiteSpace(OptionsPath) OrElse Not File.Exists(OptionsPath) Then
            Return Result
        End If

        Dim Entries = ParseOptionsOrdered(OptionsPath)
        Dim Groups As New Dictionary(Of String, List(Of KeybindEntry))(StringComparer.OrdinalIgnoreCase)

        For Each Kv In Entries
            If Not Kv.Key.StartsWith("key_", StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim Norm = NormalizeKeybindValue(Kv.Value)
            If String.IsNullOrEmpty(Norm) Then Continue For

            Dim IsVanilla As Boolean = VanillaKeybindNames.ContainsKey(Kv.Key)
            Dim FriendlyName As String = If(IsVanilla, VanillaKeybindNames(Kv.Key), FormatModKeybindName(Kv.Key))

            If Not Groups.ContainsKey(Norm) Then
                Groups(Norm) = New List(Of KeybindEntry)
            End If
            Groups(Norm).Add(New KeybindEntry With {
                .OptionKey = Kv.Key,
                .DisplayName = FriendlyName,
                .RawValue = Kv.Value.Trim(),
                .IsVanilla = IsVanilla
            })
        Next

        For Each Kv In Groups
            If Kv.Value.Count >= 2 Then
                Result.Add(New KeyConflictGroup With {
                    .NormalizedKey = Kv.Key,
                    .DisplayKey = FormatKeyDisplay(Kv.Key),
                    .Entries = Kv.Value
                })
            End If
        Next

        Result.Sort(Function(A, B) String.Compare(A.DisplayKey, B.DisplayKey, StringComparison.OrdinalIgnoreCase))
        Return Result
    End Function

    ''' <summary>
    ''' 自动将每组按键冲突中的非原版 Mod 冲突项解绑为 key.keyboard.unknown（若全是 Mod 冲突则保留第一项）。
    ''' </summary>
    Public Function UnbindNonVanillaConflicts(OptionsPath As String) As (UnboundCount As Integer, BackupPath As String)
        If String.IsNullOrWhiteSpace(OptionsPath) OrElse Not File.Exists(OptionsPath) Then
            Return (0, "")
        End If

        Dim Conflicts = DetectKeybindConflicts(OptionsPath)
        If Conflicts.Count = 0 Then Return (0, "")

        Dim KeysToUnbind As New HashSet(Of String)(StringComparer.Ordinal)
        For Each Group In Conflicts
            Dim HasVanilla = Group.Entries.Any(Function(E) E.IsVanilla)
            If HasVanilla Then
                For Each Entry In Group.Entries
                    If Not Entry.IsVanilla Then KeysToUnbind.Add(Entry.OptionKey)
                Next
            Else
                For I = 1 To Group.Entries.Count - 1
                    KeysToUnbind.Add(Group.Entries(I).OptionKey)
                Next
            End If
        Next

        If KeysToUnbind.Count = 0 Then Return (0, "")

        Dim BackupPath = OptionsPath & ".bak"
        File.Copy(OptionsPath, BackupPath, True)

        Dim Lines = File.ReadAllLines(OptionsPath, Utf8NoBom)
        Dim OutputLines As New List(Of String)(Lines.Length)
        Dim UnboundCount As Integer = 0

        For Each Line In Lines
            Dim Key As String = Nothing
            Dim Value As String = Nothing
            If TryParseOptionLine(Line, Key, Value) AndAlso KeysToUnbind.Contains(Key) Then
                OutputLines.Add(Key & ":key.keyboard.unknown")
                UnboundCount += 1
            Else
                OutputLines.Add(Line)
            End If
        Next

        WriteLinesAtomically(OptionsPath, OutputLines)
        Return (UnboundCount, BackupPath)
    End Function

    Private Function ShouldIncludeInScope(Key As String, Scope As String) As Boolean
        Select Case Scope
            Case "all"
                Return True
            Case "keybinds_video_audio"
                Dim Cat = ClassifyOptionKey(Key)
                Return Cat = "keybinds" OrElse Cat = "video" OrElse Cat = "audio"
            Case Else
                Return ClassifyOptionKey(Key) = "keybinds"
        End Select
    End Function

    Private Function ParseOptionsOrdered(FilePath As String) As List(Of KeyValuePair(Of String, String))
        Dim Map = ParseOptionsDictionary(FilePath)
        Dim Result As New List(Of KeyValuePair(Of String, String))(Map.Count)
        For Each Kv In Map
            Result.Add(Kv)
        Next
        Return Result
    End Function

    Private Function ParseOptionsDictionary(FilePath As String) As Dictionary(Of String, String)
        Dim Map As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each Line In File.ReadAllLines(FilePath, Utf8NoBom)
            Dim Key As String = Nothing
            Dim Value As String = Nothing
            If TryParseOptionLine(Line, Key, Value) Then
                Map(Key) = Value
            End If
        Next
        Return Map
    End Function

    Private Function TryParseOptionLine(Line As String, ByRef Key As String, ByRef Value As String) As Boolean
        If String.IsNullOrWhiteSpace(Line) Then Return False
        Dim ColonIdx = Line.IndexOf(":"c)
        If ColonIdx <= 0 Then Return False
        Key = Line.Substring(0, ColonIdx).Trim()
        If String.IsNullOrEmpty(Key) Then Return False
        Value = Line.Substring(ColonIdx + 1)
        Return True
    End Function

    Private Function NormalizeKeybindValue(RawValue As String) As String
        If String.IsNullOrWhiteSpace(RawValue) Then Return ""
        Dim Trimmed = RawValue.Trim().ToLowerInvariant()
        Dim Modifier = ""
        Dim BasePart = Trimmed

        Dim ColonIdx = Trimmed.IndexOf(":"c)
        If ColonIdx >= 0 Then
            BasePart = Trimmed.Substring(0, ColonIdx).Trim()
            Dim ModPart = Trimmed.Substring(ColonIdx + 1).Trim()
            If Not String.IsNullOrEmpty(ModPart) AndAlso ModPart <> "none" Then
                Modifier = ":" & ModPart
            End If
        End If

        If String.IsNullOrEmpty(BasePart) OrElse
           BasePart = "key.keyboard.unknown" OrElse
           BasePart = "key.mouse.unknown" OrElse
           BasePart = "none" OrElse
           BasePart = "0" OrElse
           BasePart = "-1" Then
            Return ""
        End If

        Return BasePart & Modifier
    End Function

    Private Function FormatModKeybindName(OptionKey As String) As String
        Dim Raw = OptionKey
        If Raw.StartsWith("key_key.", StringComparison.OrdinalIgnoreCase) Then
            Raw = Raw.Substring("key_key.".Length)
        ElseIf Raw.StartsWith("key_", StringComparison.OrdinalIgnoreCase) Then
            Raw = Raw.Substring("key_".Length)
        End If
        Return Raw
    End Function

    Private Function FormatKeyDisplay(NormalizedKey As String) As String
        If String.IsNullOrEmpty(NormalizedKey) Then Return ""
        Dim Parts = NormalizedKey.Split(":"c)
        Dim Base = Parts(0)
        Dim ModLabel = ""
        If Parts.Length > 1 Then
            Select Case Parts(1)
                Case "shift"
                    ModLabel = "Shift + "
                Case "control", "ctrl"
                    ModLabel = "Ctrl + "
                Case "alt"
                    ModLabel = "Alt + "
                Case Else
                    ModLabel = Parts(1).ToUpperInvariant() & " + "
            End Select
        End If

        Dim BaseLabel As String
        Select Case Base
            Case "key.mouse.left"
                BaseLabel = "鼠标左键"
            Case "key.mouse.right"
                BaseLabel = "鼠标右键"
            Case "key.mouse.middle"
                BaseLabel = "鼠标中键"
            Case Else
                If Base.StartsWith("key.keyboard.", StringComparison.OrdinalIgnoreCase) Then
                    Dim ShortName = Base.Substring("key.keyboard.".Length)
                    BaseLabel = ShortName.Replace("."c, " "c).ToUpperInvariant()
                ElseIf Base.StartsWith("key.mouse.", StringComparison.OrdinalIgnoreCase) Then
                    BaseLabel = "鼠标按键 " & Base.Substring("key.mouse.".Length).ToUpperInvariant()
                Else
                    BaseLabel = Base.ToUpperInvariant()
                End If
        End Select

        Return ModLabel & BaseLabel
    End Function

    Private Sub WriteLinesAtomically(TargetPath As String, Lines As IEnumerable(Of String))
        Dim TempPath = TargetPath & ".lmltmp"
        Dim Content = String.Join(Environment.NewLine, Lines)
        If Content.Length > 0 Then Content &= Environment.NewLine
        File.WriteAllText(TempPath, Content, Utf8NoBom)

        Try
            If File.Exists(TargetPath) Then
                File.Replace(TempPath, TargetPath, Nothing, True)
            Else
                File.Move(TempPath, TargetPath)
            End If
        Catch
            File.Copy(TempPath, TargetPath, True)
            If File.Exists(TempPath) Then File.Delete(TempPath)
        End Try
    End Sub

End Module
