' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone semantic version comparator and GitHub Releases JSON parser for update checking (no PCL dependencies).

Imports System.Collections.Generic
Imports System.Text.RegularExpressions
Imports Newtonsoft.Json.Linq

Public Module GitHubReleaseCheckCore

    Public Const DefaultRepoOwner As String = "Luo-Jacob"
    Public Const DefaultRepoName As String = "PCL-Linking"
    Public Const LatestReleaseApiUrl As String = "https://api.github.com/repos/Luo-Jacob/PCL-Linking/releases/latest"
    Public Const ReleasesPageUrl As String = "https://github.com/Luo-Jacob/PCL-Linking/releases"

    Public Class ReleaseCheckResult
        Public Property HasUpdate As Boolean = False
        Public Property CurrentVersion As String = ""
        Public Property LatestTag As String = ""
        Public Property ReleaseName As String = ""
        Public Property HtmlUrl As String = ReleasesPageUrl
        Public Property PublishedAt As String = ""
        Public Property ReleaseNotes As String = ""
        Public Property AssetName As String = ""
        Public Property AssetSizeBytes As Long = 0
        Public Property AssetSha256 As String = ""
        Public Property AssetDownloadUrl As String = ""
    End Class

    ''' <summary>
    ''' 提取版本号主干数字（例如 "v 1.1.0" -> {1, 1, 0}）及可选预发布标签。
    ''' </summary>
    Public Function ParseVersionTuple(RawVersion As String) As (Major As Integer, Minor As Integer, Patch As Integer, Revision As Integer, PreRelease As String)
        Dim Clean = If(RawVersion, "").Trim()
        If Clean.StartsWith("v", StringComparison.OrdinalIgnoreCase) Then
            Clean = Clean.Substring(1).Trim()
        End If
        Dim PlusIdx = Clean.IndexOf("+"c)
        If PlusIdx >= 0 Then Clean = Clean.Substring(0, PlusIdx).Trim()

        Dim Pre = ""
        Dim DashIdx = Clean.IndexOf("-"c)
        If DashIdx >= 0 Then
            Pre = Clean.Substring(DashIdx + 1).Trim()
            Clean = Clean.Substring(0, DashIdx).Trim()
        End If

        Dim Parts = Clean.Split("."c)
        Dim Nums(3) As Integer
        For i As Integer = 0 To Math.Min(3, Parts.Length - 1)
            Dim M = Regex.Match(Parts(i).Trim(), "^\d+")
            If M.Success Then
                Integer.TryParse(M.Value, Nums(i))
            End If
        Next
        Return (Nums(0), Nums(1), Nums(2), Nums(3), Pre)
    End Function

    ''' <summary>
    ''' 比较两个版本号：A &gt; B 返回 1，A &lt; B 返回 -1，相等返回 0。
    ''' </summary>
    Public Function CompareSemVer(VerA As String, VerB As String) As Integer
        Dim A = ParseVersionTuple(VerA)
        Dim B = ParseVersionTuple(VerB)
        If A.Major <> B.Major Then Return A.Major.CompareTo(B.Major)
        If A.Minor <> B.Minor Then Return A.Minor.CompareTo(B.Minor)
        If A.Patch <> B.Patch Then Return A.Patch.CompareTo(B.Patch)
        If A.Revision <> B.Revision Then Return A.Revision.CompareTo(B.Revision)

        Dim AHasPre = Not String.IsNullOrEmpty(A.PreRelease)
        Dim BHasPre = Not String.IsNullOrEmpty(B.PreRelease)
        If Not AHasPre AndAlso Not BHasPre Then Return 0
        If Not AHasPre AndAlso BHasPre Then Return 1
        If AHasPre AndAlso Not BHasPre Then Return -1
        Return Math.Sign(String.Compare(A.PreRelease, B.PreRelease, StringComparison.OrdinalIgnoreCase))
    End Function

    Public Function IsAllowedGitHubReleaseUrl(Url As String) As Boolean
        If String.IsNullOrWhiteSpace(Url) Then Return False
        Dim U As Uri = Nothing
        If Not Uri.TryCreate(Url.Trim(), UriKind.Absolute, U) Then Return False
        If Not U.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) Then Return False
        Dim H = U.Host.ToLowerInvariant()
        Return H = "github.com" OrElse H = "api.github.com" OrElse
               H.EndsWith(".githubusercontent.com")
    End Function

    ''' <summary>
    ''' 解析 GitHub /releases/latest 接口 JSON，并与当前启动器版本对比。
    ''' </summary>
    Public Function ParseLatestReleaseJson(JsonText As String, CurrentVersionText As String) As ReleaseCheckResult
        If String.IsNullOrWhiteSpace(JsonText) Then
            Throw New FormatException("GitHub Releases 返回内容为空")
        End If
        Dim Obj = JObject.Parse(JsonText)
        Dim TagName = If(Obj("tag_name")?.ToString(), "").Trim()
        If String.IsNullOrEmpty(TagName) Then
            Throw New FormatException("Release JSON 缺少 tag_name 字段")
        End If

        Dim HtmlUrl = If(Obj("html_url")?.ToString(), "").Trim()
        If Not IsAllowedGitHubReleaseUrl(HtmlUrl) Then HtmlUrl = ReleasesPageUrl

        Dim Res As New ReleaseCheckResult With {
            .CurrentVersion = If(CurrentVersionText, "").Trim(),
            .LatestTag = TagName,
            .ReleaseName = If(Obj("name")?.ToString(), TagName).Trim(),
            .HtmlUrl = HtmlUrl,
            .PublishedAt = If(Obj("published_at")?.ToString(), "").Trim(),
            .ReleaseNotes = If(Obj("body")?.ToString(), "").Trim(),
            .HasUpdate = CompareSemVer(TagName, CurrentVersionText) > 0
        }

        Dim Assets = TryCast(Obj("assets"), JArray)
        If Assets IsNot Nothing Then
            Dim Chosen As JObject = Nothing
            For Each Item In Assets.OfType(Of JObject)()
                Dim Name = If(Item("name")?.ToString(), "").Trim()
                If Name.Equals("PCLLK.exe", StringComparison.OrdinalIgnoreCase) Then
                    Chosen = Item
                    Exit For
                End If
                If Chosen Is Nothing AndAlso Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then
                    Chosen = Item
                End If
            Next
            If Chosen IsNot Nothing Then
                Res.AssetName = If(Chosen("name")?.ToString(), "").Trim()
                Dim SizeTok = Chosen("size")
                If SizeTok IsNot Nothing Then Res.AssetSizeBytes = SizeTok.ToObject(Of Long)()
                Dim Digest = If(Chosen("digest")?.ToString(), "").Trim()
                If Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) Then
                    Res.AssetSha256 = Digest.Substring("sha256:".Length).Trim().ToLowerInvariant()
                End If
                Dim DlUrl = If(Chosen("browser_download_url")?.ToString(), "").Trim()
                If IsAllowedGitHubReleaseUrl(DlUrl) Then Res.AssetDownloadUrl = DlUrl
            End If
        End If

        Return Res
    End Function

End Module
