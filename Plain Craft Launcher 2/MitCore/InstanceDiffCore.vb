' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone Minecraft instance mod/config comparison (Diff) and log line anomaly filter utilities (no PCL dependencies).

Imports System.IO
Imports System.Security.Cryptography

Public Module InstanceDiffCore

    Public Class ModFileEntry
        Public Property FilePath As String = ""
        Public Property FileName As String = ""
        Public Property Sha1 As String = ""
    End Class

    Public Class InstanceFolderDiffResult
        Public Property ExactModCount As Integer
        Public Property DiffVersionMods As New List(Of String)
        Public Property OnlyInCurrentMods As New List(Of String)
        Public Property OnlyInOtherMods As New List(Of ModFileEntry)
        Public Property ExactConfigCount As Integer
        Public Property DiffConfigs As New List(Of String)
        Public Property OnlyInCurrentConfigsCount As Integer
        Public Property OnlyInOtherConfigsCount As Integer
    End Class

    Public Function ComputeSha1Lower(FilePath As String) As String
        Using Fs = File.OpenRead(FilePath), Sha = SHA1.Create()
            Return BitConverter.ToString(Sha.ComputeHash(Fs)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function

    Public Function CompareModAndConfigFolders(CurIndieRoot As String, OtherIndieRoot As String) As InstanceFolderDiffResult
        Dim ReadJarHashes =
        Function(IndieRoot As String) As List(Of ModFileEntry)
            Dim Dir = Path.Combine(IndieRoot.TrimEnd("\"c), "mods")
            Dim List As New List(Of ModFileEntry)
            If Not Directory.Exists(Dir) Then Return List
            For Each F In Directory.GetFiles(Dir, "*.jar", SearchOption.TopDirectoryOnly)
                List.Add(New ModFileEntry With {
                    .FilePath = F,
                    .FileName = Path.GetFileName(F),
                    .Sha1 = ComputeSha1Lower(F)
                })
            Next
            Return List
        End Function

        Dim ReadConfigHashes =
        Function(IndieRoot As String) As Dictionary(Of String, String)
            Dim Dict As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim CfgRoot = Path.Combine(IndieRoot.TrimEnd("\"c), "config")
            If Not Directory.Exists(CfgRoot) Then Return Dict
            For Each F In Directory.GetFiles(CfgRoot, "*", SearchOption.AllDirectories)
                Dim Rel = F.Substring(CfgRoot.Length).TrimStart("\"c)
                Dict(Rel) = ComputeSha1Lower(F)
            Next
            Return Dict
        End Function

        Dim Res As New InstanceFolderDiffResult()
        Dim CurMods = ReadJarHashes(CurIndieRoot)
        Dim OtherMods = ReadJarHashes(OtherIndieRoot)
        Dim MatchedOther As New HashSet(Of Integer)

        For Each Cm In CurMods
            Dim ExactIdx = OtherMods.FindIndex(Function(om) String.Equals(om.Sha1, Cm.Sha1, StringComparison.OrdinalIgnoreCase))
            If ExactIdx >= 0 Then
                MatchedOther.Add(ExactIdx)
                Res.ExactModCount += 1
            Else
                Dim SameNameIdx = OtherMods.FindIndex(Function(om) String.Equals(om.FileName, Cm.FileName, StringComparison.OrdinalIgnoreCase))
                If SameNameIdx >= 0 Then
                    MatchedOther.Add(SameNameIdx)
                    Res.DiffVersionMods.Add(Cm.FileName)
                Else
                    Res.OnlyInCurrentMods.Add(Cm.FileName)
                End If
            End If
        Next
        For i As Integer = 0 To OtherMods.Count - 1
            If Not MatchedOther.Contains(i) Then Res.OnlyInOtherMods.Add(OtherMods(i))
        Next

        Dim CurCfgs = ReadConfigHashes(CurIndieRoot)
        Dim OtherCfgs = ReadConfigHashes(OtherIndieRoot)
        For Each Kv In CurCfgs
            Dim OtherHash As String = Nothing
            If OtherCfgs.TryGetValue(Kv.Key, OtherHash) Then
                If String.Equals(Kv.Value, OtherHash, StringComparison.OrdinalIgnoreCase) Then
                    Res.ExactConfigCount += 1
                Else
                    Res.DiffConfigs.Add(Kv.Key)
                End If
            Else
                Res.OnlyInCurrentConfigsCount += 1
            End If
        Next
        For Each Kv In OtherCfgs
            If Not CurCfgs.ContainsKey(Kv.Key) Then Res.OnlyInOtherConfigsCount += 1
        Next
        Return Res
    End Function

    Public Function ExtractSuspiciousLogLines(Lines As IEnumerable(Of String), Optional MaxCount As Integer = 8) As List(Of String)
        If Lines Is Nothing Then Return New List(Of String)
        Return Lines.Where(
            Function(l) Not String.IsNullOrWhiteSpace(l) AndAlso
                        (l.Contains("/ERROR") OrElse l.Contains("/FATAL") OrElse l.Contains("Exception") OrElse
                         l.Contains("Caused by:") OrElse (l.IndexOf("mixin", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso l.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0))).
            Select(Function(l) l.Trim()).
            Distinct().
            Take(Math.Max(1, MaxCount)).
            ToList()
    End Function

End Module
