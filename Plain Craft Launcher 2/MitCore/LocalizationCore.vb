' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone bilingual (Simplified Chinese / English) localization dictionary & dynamic pattern translator (no PCL/WPF dependencies).

Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Module LocalizationCore

    Public Enum LauncherLanguage As Integer
        ChineseSimplified = 0
        English = 1
    End Enum

    Public Property CurrentLanguage As LauncherLanguage = LauncherLanguage.ChineseSimplified

    Private ReadOnly Translations As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)

    Public ReadOnly Property DictionaryCount As Integer
        Get
            Return Translations.Count
        End Get
    End Property

    Public Function IsEnglishActive() As Boolean
        Return CurrentLanguage = LauncherLanguage.English
    End Function

    Public Sub LoadTranslationsFromJson(JsonText As String)
        If String.IsNullOrWhiteSpace(JsonText) Then Return
        Dim Obj = JObject.Parse(JsonText)
        For Each Prop In Obj.Properties()
            If Prop.Value Is Nothing OrElse Prop.Value.Type = JTokenType.Null Then Continue For
            Dim NormKey = NormalizeLineBreaks(Prop.Name)
            Translations(NormKey) = Prop.Value.ToString()
        Next
    End Sub

    Public Sub LoadEmbeddedTranslations(TargetAssembly As Assembly, Optional ResourceSuffix As String = "en-US.json")
        If TargetAssembly Is Nothing Then Return
        Dim Names = TargetAssembly.GetManifestResourceNames()
        For Each ResName In Names
            If ResName.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase) Then
                Using S = TargetAssembly.GetManifestResourceStream(ResName)
                    If S Is Nothing Then Continue For
                    Using Sr As New StreamReader(S, Encoding.UTF8)
                        LoadTranslationsFromJson(Sr.ReadToEnd())
                    End Using
                End Using
                Exit For
            End If
        Next
    End Sub

    Public Function NormalizeLineBreaks(Input As String) As String
        If String.IsNullOrEmpty(Input) Then Return ""
        Return Input.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf)
    End Function

    Public Function ContainsCjk(Text As String) As Boolean
        If String.IsNullOrEmpty(Text) Then Return False
        For Each Ch In Text
            Dim Code = AscW(Ch)
            If Code >= &H4E00 AndAlso Code <= &H9FFF Then Return True
        Next
        Return False
    End Function

    ''' <summary>
    ''' 将中文界面文本翻译为当前所选语言（简体中文模式原样返回）。
    ''' </summary>
    Public Function TranslateText(Text As String) As String
        If CurrentLanguage <> LauncherLanguage.English OrElse String.IsNullOrEmpty(Text) Then Return Text
        If Not ContainsCjk(Text) Then Return Text

        Dim Norm = NormalizeLineBreaks(Text)
        Dim Found As String = Nothing
        If Translations.TryGetValue(Norm, Found) Then Return Found

        Dim Trimmed = Norm.Trim()
        If Trimmed <> Norm AndAlso Translations.TryGetValue(Trimmed, Found) Then
            Dim LeadLen = Norm.Length - Norm.TrimStart().Length
            Dim TrailLen = Norm.Length - Norm.TrimEnd().Length
            Return Norm.Substring(0, LeadLen) & Found & If(TrailLen > 0, Norm.Substring(Norm.Length - TrailLen), "")
        End If

        Dim DynamicResult = TranslateDynamicPatterns(Norm)
        If Not String.Equals(DynamicResult, Norm, StringComparison.Ordinal) Then
            Return DynamicResult
        End If

        '按换行逐段翻译复合提示或报错正文
        If Norm.IndexOf(vbLf(0)) >= 0 Then
            Dim Lines = Norm.Split(vbLf(0))
            Dim AnyChanged As Boolean = False
            For i As Integer = 0 To Lines.Length - 1
                Dim LineRes = TranslateSingleLineFallback(Lines(i))
                If LineRes <> Lines(i) Then
                    Lines(i) = LineRes
                    AnyChanged = True
                End If
            Next
            If AnyChanged Then Return String.Join(Environment.NewLine, Lines)
        End If

        Return Text
    End Function

    Private Function TranslateSingleLineFallback(Line As String) As String
        If String.IsNullOrEmpty(Line) OrElse Not ContainsCjk(Line) Then Return Line
        Dim Found As String = Nothing
        If Translations.TryGetValue(Line, Found) Then Return Found
        Dim Trimmed = Line.Trim()
        If Trimmed <> Line AndAlso Translations.TryGetValue(Trimmed, Found) Then
            Dim LeadLen = Line.Length - Line.TrimStart().Length
            Dim TrailLen = Line.Length - Line.TrimEnd().Length
            Return Line.Substring(0, LeadLen) & Found & If(TrailLen > 0, Line.Substring(Line.Length - TrailLen), "")
        End If
        Return TranslateDynamicPatterns(Line)
    End Function

    Private Function TranslateDynamicPatterns(Text As String) As String
        Dim M As Match

        M = Regex.Match(Text, "^PCL Linking修改版\s+(v\s*[\w.\-]+)\s*·\s*基于\s*PCL\s*2\.14\.0\.0\s*开源版二次开发$")
        If M.Success Then Return $"PCL Linking {M.Groups(1).Value} · Forked from PCL 2.14.0.0 Open Source"

        M = Regex.Match(Text, "^版本设置\s*-\s*(.+)$")
        If M.Success Then Return $"Instance Settings - {M.Groups(1).Value}"

        M = Regex.Match(Text, "^好友\s*\(([^)]+)\)$")
        If M.Success Then Return $"Friends ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^(\d+)\s*好友$")
        If M.Success Then Return $"{M.Groups(1).Value} Friends"

        M = Regex.Match(Text, "^(\d+)\s*群\s*·\s*(\d+)\s*好友$")
        If M.Success Then Return $"{M.Groups(1).Value}G · {M.Groups(2).Value}F"

        M = Regex.Match(Text, "^中继:\s*公共节点$")
        If M.Success Then Return "Relay: Public"

        M = Regex.Match(Text, "^中继:\s*(.+)$")
        If M.Success Then Return $"Relay: {M.Groups(1).Value}"

        M = Regex.Match(Text, "^当前中继：([^\s（]+)（每\s*(\d+)\s*秒后台拉取一次）$")
        If M.Success Then Return $"Current relay: {M.Groups(1).Value} (polls every {M.Groups(2).Value}s)"

        M = Regex.Match(Text, "^黑名单\s*\((\d+)\)$")
        If M.Success Then Return $"Blocked ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^群号：([^\s·]+)\s*·\s*端到端加密群聊（切回即实时拉取）$")
        If M.Success Then Return $"Group ID: {M.Groups(1).Value} · E2E Encrypted Group (syncs on open)"

        M = Regex.Match(Text, "^好友\s*ID:\s*([^\s·]+)\s*·\s*(.+)$")
        If M.Success Then Return $"Friend ID: {M.Groups(1).Value} · {TranslateText(M.Groups(2).Value)}"

        M = Regex.Match(Text, "^节点：(.+?)\s*·\s*协议：(.+?)\s*·\s*(.+)$")
        If M.Success Then Return $"Node: {TranslateText(M.Groups(1).Value.Trim())}  ·  Protocol: {TranslateText(M.Groups(2).Value.Trim())}  ·  {TranslateText(M.Groups(3).Value.Trim())}"

        M = Regex.Match(Text, "^自定义节点\s*\((\d+)\s*个\)$")
        If M.Success Then Return $"Custom Nodes ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^(\d+)\s*次$")
        If M.Success Then Return $"{M.Groups(1).Value}x"

        M = Regex.Match(Text, "^发送时间：(.+)$")
        If M.Success Then Return $"Sent: {M.Groups(1).Value}"

        M = Regex.Match(Text, "^发送失败：(.+)（点击重试）$")
        If M.Success Then Return $"Send failed: {M.Groups(1).Value} (click to retry)"

        M = Regex.Match(Text, "^大小：(.+?)\s*·\s*AES-256 端到端加密（中继暂存\s*3\s*小时）$")
        If M.Success Then Return $"Size: {M.Groups(1).Value} · AES-256 E2E Encrypted (3h relay TTL)"

        M = Regex.Match(Text, "^设置设置项时出错（(.+)）$")
        If M.Success Then Return $"Failed to apply setting ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^白名单列表\s*\((\d+)\s*人\)$")
        If M.Success Then Return $"Whitelist ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^管理员列表\s*\(OP\s*·\s*(\d+)\s*人\)$")
        If M.Success Then Return $"Operators (OP · {M.Groups(1).Value})"

        M = Regex.Match(Text, "^(\d+)\s*人$")
        If M.Success Then Return $"{M.Groups(1).Value} Players"

        M = Regex.Match(Text, "^2\s+人$")
        If M.Success Then Return "2 Players"

        M = Regex.Match(Text, "^(\d+)\s*分钟$")
        If M.Success Then Return $"{M.Groups(1).Value} min"

        M = Regex.Match(Text, "^背景图片（(\d+)\s*张）$")
        If M.Success Then Return $"Background Images ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^关注服务器列表\s*\((\d+)\)$")
        If M.Success Then Return $"Watched Servers ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^历史快照列表\s*\((\d+)\)$")
        If M.Success Then Return $"Snapshots ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^已绑定快捷键\s*\((\d+)\)$")
        If M.Success Then Return $"Bound Hotkeys ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^当前清单已添加的资源\s*\((\d+)\s*项\)$")
        If M.Success Then Return $"Preset Resources ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^已选择\s*(\d+)\s*个文件$")
        If M.Success Then Return $"{M.Groups(1).Value} files selected"

        M = Regex.Match(Text, "^共\s*(\d+)\s*项说明$")
        If M.Success Then Return $"{M.Groups(1).Value} topics"

        M = Regex.Match(Text, "^已扫描\s*(\d+)\s*个插件（已启用\s*(\d+)\s*个，已禁用\s*(\d+)\s*个）$")
        If M.Success Then Return $"Scanned {M.Groups(1).Value} plugins ({M.Groups(2).Value} enabled, {M.Groups(3).Value} disabled)"

        '时间跨度格式（StringUtils.FormatTimeSpan 短格式与长格式）
        M = Regex.Match(Text, "^(\d+)\s*年\s*(\d+)\s*个月([前后])$")
        If M.Success Then Return If(M.Groups(3).Value = "前", $"{M.Groups(1).Value}y {M.Groups(2).Value}mo ago", $"in {M.Groups(1).Value}y {M.Groups(2).Value}mo")
        M = Regex.Match(Text, "^(\d+)\s*个月\s*(\d+)\s*天([前后])$")
        If M.Success Then Return If(M.Groups(3).Value = "前", $"{M.Groups(1).Value}mo {M.Groups(2).Value}d ago", $"in {M.Groups(1).Value}mo {M.Groups(2).Value}d")
        M = Regex.Match(Text, "^(\d+)\s*月\s*(\d+)\s*天([前后])$")
        If M.Success Then Return If(M.Groups(3).Value = "前", $"{M.Groups(1).Value}mo {M.Groups(2).Value}d ago", $"in {M.Groups(1).Value}mo {M.Groups(2).Value}d")
        M = Regex.Match(Text, "^(\d+)\s*天\s*(\d+)\s*小时([前后])$")
        If M.Success Then Return If(M.Groups(3).Value = "前", $"{M.Groups(1).Value}d {M.Groups(2).Value}h ago", $"in {M.Groups(1).Value}d {M.Groups(2).Value}h")
        M = Regex.Match(Text, "^(\d+)\s*小时\s*(\d+)\s*分钟([前后])$")
        If M.Success Then Return If(M.Groups(3).Value = "前", $"{M.Groups(1).Value}h {M.Groups(2).Value}m ago", $"in {M.Groups(1).Value}h {M.Groups(2).Value}m")
        M = Regex.Match(Text, "^(\d+)\s*分\s*(\d+)\s*秒([前后])$")
        If M.Success Then Return If(M.Groups(3).Value = "前", $"{M.Groups(1).Value}m {M.Groups(2).Value}s ago", $"in {M.Groups(1).Value}m {M.Groups(2).Value}s")
        M = Regex.Match(Text, "^(\d+)\s*(年|个月|月|天|小时|分钟|秒)([前后])$")
        If M.Success Then
            Dim Unit = M.Groups(2).Value
            Dim EnUnit = If(Unit = "年", "yr", If(Unit = "个月" OrElse Unit = "月", "mo", If(Unit = "天", "d", If(Unit = "小时", "h", If(Unit = "分钟", "m", "s")))))
            Return If(M.Groups(3).Value = "前", $"{M.Groups(1).Value} {EnUnit} ago", $"in {M.Groups(1).Value} {EnUnit}")
        End If

        '下载与安装版本列表描述
        M = Regex.Match(Text, "^最新(正式版|预览版)，发布于\s*(.+)$")
        If M.Success Then Return $"Latest {If(M.Groups(1).Value = "正式版", "Release", "Snapshot")}, Released {M.Groups(2).Value}"

        M = Regex.Match(Text, "^(推荐版|最新版|测试版|正式版|稳定版)(?:，(.*))?$")
        If M.Success Then
            Dim Badge = TranslateText(M.Groups(1).Value)
            Dim Rest = M.Groups(2).Value
            If String.IsNullOrEmpty(Rest) Then Return Badge
            Dim Parts = Rest.Split({"，"c}, StringSplitOptions.RemoveEmptyEntries).Select(Function(p) TranslateText(p.Trim()))
            Return Badge & ", " & String.Join(", ", Parts)
        End If

        M = Regex.Match(Text, "^发布于\s*(.+)$")
        If M.Success Then Return $"Released {M.Groups(1).Value}"

        M = Regex.Match(Text, "^兼容\s*Forge\s*(.+)$")
        If M.Success Then Return $"Forge {M.Groups(1).Value}"

        M = Regex.Match(Text, "^全部版本\s*\((\d+)\)$")
        If M.Success Then Return $"All Versions ({M.Groups(1).Value})"

        M = Regex.Match(Text, "^种类：(.+)$")
        If M.Success Then Return $"Type: {M.Groups(1).Value}"

        M = Regex.Match(Text, "^原名\s*[:：]\s*(.+)$")
        If M.Success Then Return $"Original: {M.Groups(1).Value}"

        M = Regex.Match(Text, "^转到\s+(.+)$")
        If M.Success Then Return $"Open {M.Groups(1).Value}"

        M = Regex.Match(Text, "^仅\s+(.+)$")
        If M.Success Then Return $"{M.Groups(1).Value} Only"

        M = Regex.Match(Text, "^(.+?)（所选版本）$")
        If M.Success Then Return $"{M.Groups(1).Value} (Selected)"

        M = Regex.Match(Text, "^当前选择为「原版」，以下\s*(\d+)\s*个\s*Mod\s*无法在原版运行（装配时将跳过\s*Mod）：(.*)$")
        If M.Success Then Return $"Vanilla selected: {M.Groups(1).Value} mod(s) cannot run on Vanilla and will be skipped: {M.Groups(2).Value}"

        M = Regex.Match(Text, "^共发现\s*(\d+)\s*个世界存档（点击卡片可编辑规则与数据包）$")
        If M.Success Then Return $"Found {M.Groups(1).Value} worlds (click a card to edit rules & datapacks)"

        M = Regex.Match(Text, "^发现\s*(\d+)\s*个可升级的服务端插件：$")
        If M.Success Then Return $"Found {M.Groups(1).Value} updatable server plugins:"

        M = Regex.Match(Text, "^状态：(.+)$")
        If M.Success Then Return $"Status: {TranslateText(M.Groups(1).Value)}"

        M = Regex.Match(Text, "^内存：(.+)$")
        If M.Success Then Return $"Memory: {M.Groups(1).Value}"

        M = Regex.Match(Text, "^运行时长：(.+)$")
        If M.Success Then Return $"Uptime: {M.Groups(1).Value}"

        M = Regex.Match(Text, "^插件/Mod：(.+)$")
        If M.Success Then Return $"Plugins/Mods: {M.Groups(1).Value}"

        M = Regex.Match(Text, "^当前\s+(v?\s*[\w.\-]+)\s+已是最新正式版（GitHub 最新：([^）]+)）。$")
        If M.Success Then Return $"Current version {M.Groups(1).Value} is up to date (GitHub latest: {M.Groups(2).Value})."

        M = Regex.Match(Text, "^发现新版本：([^\s（]+)（当前\s+([^）]+)），点击检查可打开下载页。$")
        If M.Success Then Return $"Update available: {M.Groups(1).Value} (current: {M.Groups(2).Value}). Click Check Update to view."

        M = Regex.Match(Text, "^搜索\s*(.+)$")
        If M.Success Then
            Dim SubTarget = M.Groups(1).Value.Trim()
            Dim TranslatedSub = If(Translations.ContainsKey(SubTarget), Translations(SubTarget), SubTarget)
            Return $"Search {TranslatedSub}"
        End If

        M = Regex.Match(Text, "^已加载\s*(\d+)\s*个\s*Mod(.*)$")
        If M.Success Then Return $"{M.Groups(1).Value} Mods loaded{M.Groups(2).Value}"

        M = Regex.Match(Text, "^关于\s+(.+)$")
        If M.Success Then Return $"About {M.Groups(1).Value}"

        If Text.StartsWith("正在获取", StringComparison.Ordinal) AndAlso Text.EndsWith("列表", StringComparison.Ordinal) Then
            Dim Mid = Text.Substring("正在获取".Length, Text.Length - "正在获取".Length - "列表".Length).Trim()
            If Mid = "" Then Return "Fetching list..."
            Return $"Fetching {TranslateText(Mid)} list..."
        End If
        If Text.StartsWith("正在安装 ", StringComparison.Ordinal) Then Return "Installing " & Text.Substring("正在安装 ".Length)
        If Text.StartsWith("正在下载 ", StringComparison.Ordinal) Then Return "Downloading " & Text.Substring("正在下载 ".Length)
        If Text.StartsWith("正在加载 ", StringComparison.Ordinal) Then Return "Loading " & Text.Substring("正在加载 ".Length)

        '局部高频后缀替换（用于拼接型长句）
        Dim Replaced = Text
        If Replaced.Contains("（当前选中）") Then Replaced = Replaced.Replace("（当前选中）", " (Selected)")
        If Replaced.Contains("（已禁用）") Then Replaced = Replaced.Replace("（已禁用）", " (Disabled)")
        If Replaced.Contains("[26.1+ 格式]") Then Replaced = Replaced.Replace("[26.1+ 格式]", "[26.1+ Format]")
        If Replaced.Contains("[传统 NBT 格式]") Then Replaced = Replaced.Replace("[传统 NBT 格式]", "[Legacy NBT]")
        If Replaced.Contains("世界种子:") Then Replaced = Replaced.Replace("世界种子:", "Seed:")
        If Replaced.Contains(" · 点击右侧图标切换启用状态或删除") Then
            Replaced = Replaced.Replace("文件夹数据包", "Folder Datapack").Replace("ZIP 压缩数据包", "ZIP Datapack").Replace(" · 点击右侧图标切换启用状态或删除", " · Click icons on the right to toggle or delete")
        End If
        Return Replaced
    End Function

End Module
