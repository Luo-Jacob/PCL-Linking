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
        Directory.CreateDirectory(TargetDir)
        Dim TargetRoot = Path.GetFullPath(TargetDir).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        Using Ms As New MemoryStream(ZipBytes), Zip As New ZipArchive(Ms, ZipArchiveMode.Read)
            If Zip.Entries.Count > MaxEntries Then
                Throw New InvalidDataException($"压缩包条目数（{Zip.Entries.Count}）超过 {MaxEntries} 安全上限！")
            End If
            Dim TotalDecompressed As Long = 0
            Dim Buf(16383) As Byte
            For Each Entry In Zip.Entries
                If String.IsNullOrEmpty(Entry.Name) Then Continue For
                Dim CleanRel = Entry.FullName.Replace("/"c, "\"c).TrimStart("\"c)
                If String.IsNullOrWhiteSpace(CleanRel) Then Continue For
                Dim DestFull = Path.GetFullPath(Path.Combine(TargetRoot, CleanRel))
                If Not DestFull.StartsWith(TargetRoot, StringComparison.OrdinalIgnoreCase) Then
                    OnPathTraversalBlocked?.Invoke(Entry.FullName)
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
            Next
        End Using
    End Sub

End Module
