' SPDX-License-Identifier: MIT
' Copyright (c) 2026 Luo-Jacob / PCL Linking Contributors
' Standalone ZipSlip and Zip-bomb safe archive extractor & directory copy utilities (no PCL dependencies).

Imports System.IO
Imports System.IO.Compression

Public Module ZipSafetyCore

    Public Sub CopyDirectorySafe(SrcDir As String, DestDir As String)
        Directory.CreateDirectory(DestDir)
        For Each FilePath In Directory.GetFiles(SrcDir)
            File.Copy(FilePath, Path.Combine(DestDir, Path.GetFileName(FilePath)), True)
        Next
        For Each SubDirPath In Directory.GetDirectories(SrcDir)
            CopyDirectorySafe(SubDirPath, Path.Combine(DestDir, Path.GetFileName(SubDirPath)))
        Next
    End Sub

    Public Sub ExtractZipBytesSafely(ZipBytes As Byte(), TargetDir As String, MaxEntries As Integer, MaxUncompressedBytes As Long,
                                     Optional OnPathTraversalBlocked As Action(Of String) = Nothing)
        If ZipBytes Is Nothing OrElse ZipBytes.Length = 0 Then Throw New ArgumentException("压缩包内容为空")
        Using Ms As New MemoryStream(ZipBytes), Zip As New ZipArchive(Ms, ZipArchiveMode.Read)
            ExtractArchiveCore(Zip, TargetDir, MaxEntries, MaxUncompressedBytes, Nothing, OnPathTraversalBlocked)
        End Using
    End Sub

    Public Function ExtractZipFileSafely(ZipFilePath As String, TargetDir As String, MaxEntries As Integer, MaxUncompressedBytes As Long,
                                         Optional AllowedTopFolders As IEnumerable(Of String) = Nothing,
                                         Optional OnEntrySkippedOrBlocked As Action(Of String) = Nothing) As Integer
        If String.IsNullOrWhiteSpace(ZipFilePath) OrElse Not File.Exists(ZipFilePath) Then
            Throw New FileNotFoundException("找不到压缩包文件", ZipFilePath)
        End If
        Using Fs As New FileStream(ZipFilePath, FileMode.Open, FileAccess.Read, FileShare.Read),
              Zip As New ZipArchive(Fs, ZipArchiveMode.Read)
            Return ExtractArchiveCore(Zip, TargetDir, MaxEntries, MaxUncompressedBytes, AllowedTopFolders, OnEntrySkippedOrBlocked)
        End Using
    End Function

    Private Function ExtractArchiveCore(Zip As ZipArchive, TargetDir As String, MaxEntries As Integer, MaxUncompressedBytes As Long,
                                        AllowedTopFolders As IEnumerable(Of String),
                                        OnEntrySkippedOrBlocked As Action(Of String)) As Integer
        Directory.CreateDirectory(TargetDir)
        Dim TargetRoot = Path.GetFullPath(TargetDir).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        If Zip.Entries.Count > MaxEntries Then
            Throw New InvalidDataException($"压缩包条目数（{Zip.Entries.Count}）超过 {MaxEntries} 安全上限！")
        End If
        Dim AllowSet As HashSet(Of String) = Nothing
        If AllowedTopFolders IsNot Nothing Then
            AllowSet = New HashSet(Of String)(AllowedTopFolders.Where(Function(x) Not String.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase)
        End If

        Dim ExtractedCount As Integer = 0
        Dim TotalDecompressed As Long = 0
        Dim Buf(65535) As Byte
        For Each Entry In Zip.Entries
            If String.IsNullOrEmpty(Entry.Name) Then Continue For
            Dim CleanRel = Entry.FullName.Replace("/"c, "\"c).TrimStart("\"c)
            If String.IsNullOrWhiteSpace(CleanRel) Then Continue For
            If CleanRel.IndexOf(ChrW(0)) >= 0 OrElse CleanRel.Contains(":") Then
                OnEntrySkippedOrBlocked?.Invoke(Entry.FullName)
                Continue For
            End If
            If AllowSet IsNot Nothing Then
                Dim SepIdx = CleanRel.IndexOf("\"c)
                If SepIdx <= 0 Then Continue For
                Dim TopFolder = CleanRel.Substring(0, SepIdx)
                If Not AllowSet.Contains(TopFolder) Then
                    OnEntrySkippedOrBlocked?.Invoke(Entry.FullName)
                    Continue For
                End If
            End If
            Dim DestFull = Path.GetFullPath(Path.Combine(TargetRoot, CleanRel))
            If Not DestFull.StartsWith(TargetRoot, StringComparison.OrdinalIgnoreCase) Then
                OnEntrySkippedOrBlocked?.Invoke(Entry.FullName)
                Continue For
            End If
            Dim DestParent = Path.GetDirectoryName(DestFull)
            If Not String.IsNullOrEmpty(DestParent) Then Directory.CreateDirectory(DestParent)
            Using SrcStream = Entry.Open(), DestFs As New FileStream(DestFull, FileMode.Create, FileAccess.Write, FileShare.None)
                Do
                    Dim ReadBytes = SrcStream.Read(Buf, 0, Buf.Length)
                    If ReadBytes <= 0 Then Exit Do
                    TotalDecompressed += ReadBytes
                    If TotalDecompressed > MaxUncompressedBytes Then
                        Throw New InvalidDataException($"压缩包解压体积超过 {MaxUncompressedBytes \ (1024 * 1024)} MB 安全上限！")
                    End If
                    DestFs.Write(Buf, 0, ReadBytes)
                Loop
            End Using
            ExtractedCount += 1
        Next
        Return ExtractedCount
    End Function

End Module
