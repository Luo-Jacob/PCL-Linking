Imports System.IO
Imports System.IO.Compression

''' <summary>
''' 实例快照的共享创建、列表、回滚与自动清理服务（供快照页、启动前自动备份与崩溃恢复共用）。
''' </summary>
Public Module ModSnapshot

    Public Class SnapshotMeta
        Public Property Id As String
        Public Property Name As String
        Public Property Note As String
        Public Property CreatedAt As DateTime
        Public Property McVersion As String
        Public Property Scopes As New List(Of String)
        Public Property ZipFileName As String
        Public Property FileCount As Integer
        Public Property SizeBytes As Long
        Public Property IsAutoBackup As Boolean = False
    End Class

    Public ReadOnly SnapshotOptionFileNames As String() = {"options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat"}

    Public Function GetSnapshotsDir(Instance As McInstance) As String
        Return Instance.PathVersion & "PCL Linking\Snapshots\"
    End Function

    Public Function FormatScopeName(Scope As String) As String
        Select Case Scope
            Case "mods" : Return "模组"
            Case "config" : Return "配置"
            Case "resourcepacks" : Return "材质"
            Case "shaderpacks" : Return "光影"
            Case "options" : Return "游戏设置"
            Case "saves" : Return "存档"
            Case Else : Return Scope
        End Select
    End Function

    Public Function ListSnapshots(Instance As McInstance) As List(Of SnapshotMeta)
        Dim Loaded As New List(Of SnapshotMeta)
        If Instance Is Nothing Then Return Loaded
        Dim SnapDir = GetSnapshotsDir(Instance)
        If Not Directory.Exists(SnapDir) Then Return Loaded
        For Each MetaPath In Directory.GetFiles(SnapDir, "*.meta.json")
            Try
                Dim Meta = Newtonsoft.Json.JsonConvert.DeserializeObject(Of SnapshotMeta)(File.ReadAllText(MetaPath, Text.Encoding.UTF8))
                If Meta IsNot Nothing AndAlso Not String.IsNullOrEmpty(Meta.ZipFileName) AndAlso File.Exists(Path.Combine(SnapDir, Meta.ZipFileName)) Then
                    Meta.SizeBytes = New FileInfo(Path.Combine(SnapDir, Meta.ZipFileName)).Length
                    Loaded.Add(Meta)
                End If
            Catch ex As Exception
                Logger.Warn(ex, $"读取快照元数据失败: {MetaPath}")
            End Try
        Next
        Return Loaded.OrderByDescending(Function(s) s.CreatedAt).ToList()
    End Function

    Public Function CreateSnapshotCore(Instance As McInstance, Name As String, Note As String, Scopes As IEnumerable(Of String), Optional IsAutoBackup As Boolean = False) As SnapshotMeta
        Dim ScopeList = If(Scopes, Array.Empty(Of String)()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        Dim SnapDir = GetSnapshotsDir(Instance)
        Directory.CreateDirectory(SnapDir)

        Dim Id As String = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 6)}"
        Dim ZipFileName As String = Id & ".zip"
        Dim TempZipPath As String = Path.Combine(SnapDir, ZipFileName & ".tmp")
        Dim FinalZipPath As String = Path.Combine(SnapDir, ZipFileName)
        Dim IndieRoot As String = Instance.PathIndie.TrimEnd("\"c)
        Dim ArchivedCount As Integer = 0

        Try
            Using Archive = ZipFile.Open(TempZipPath, ZipArchiveMode.Create)
                For Each Scope In ScopeList
                    If Scope = "options" Then
                        For Each OptFile In SnapshotOptionFileNames
                            Dim SrcFile = Path.Combine(IndieRoot, OptFile)
                            If File.Exists(SrcFile) Then
                                Archive.CreateEntryFromFile(SrcFile, OptFile, CompressionLevel.Optimal)
                                ArchivedCount += 1
                            End If
                        Next
                    ElseIf Scope = "config" Then
                        For Each SubDirName In {"config", "defaultconfigs"}
                            ArchivedCount += AddFolderToArchive(Archive, Path.Combine(IndieRoot, SubDirName), SubDirName)
                        Next
                    Else
                        ArchivedCount += AddFolderToArchive(Archive, Path.Combine(IndieRoot, Scope), Scope)
                    End If
                Next
            End Using

            If File.Exists(FinalZipPath) Then File.Delete(FinalZipPath)
            File.Move(TempZipPath, FinalZipPath)

            Dim Meta As New SnapshotMeta With {
                .Id = Id,
                .Name = Name,
                .Note = Note,
                .CreatedAt = DateTime.Now,
                .McVersion = Instance.Version.VanillaName,
                .Scopes = ScopeList,
                .ZipFileName = ZipFileName,
                .FileCount = ArchivedCount,
                .SizeBytes = New FileInfo(FinalZipPath).Length,
                .IsAutoBackup = IsAutoBackup
            }
            File.WriteAllText(Path.Combine(SnapDir, Id & ".meta.json"), Newtonsoft.Json.JsonConvert.SerializeObject(Meta, Newtonsoft.Json.Formatting.Indented), Text.Encoding.UTF8)
            Return Meta
        Catch
            Try
                If File.Exists(TempZipPath) Then File.Delete(TempZipPath)
            Catch
            End Try
            Throw
        End Try
    End Function

    Public Function RestoreSnapshotCore(Instance As McInstance, Snap As SnapshotMeta, Optional ProgressCallback As Action(Of String) = Nothing) As Integer
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))
        If Snap Is Nothing Then Throw New ArgumentNullException(NameOf(Snap))
        Dim SnapDir = GetSnapshotsDir(Instance)
        Dim ZipPath = Path.Combine(SnapDir, Snap.ZipFileName)
        If Not File.Exists(ZipPath) Then Throw New FileNotFoundException("快照压缩包不存在", ZipPath)

        ProgressCallback?.Invoke("正在校验并解压快照...")
        Dim IndieRoot As String = Path.GetFullPath(Instance.PathIndie).TrimEnd("\"c)
        Dim StageRoot As String = Path.Combine(SnapDir, "_restore_stage_" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Dim StagePrefix As String = Path.GetFullPath(StageRoot).TrimEnd("\"c) & Path.DirectorySeparatorChar
        Dim RestoredCount As Integer = 0
        Try
            Directory.CreateDirectory(StageRoot)
            Using Archive = ZipFile.OpenRead(ZipPath)
                For Each Entry In Archive.Entries
                    If String.IsNullOrEmpty(Entry.Name) Then Continue For
                    Dim CleanRel = Entry.FullName.Replace("/"c, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)
                    Dim StageDest = Path.GetFullPath(Path.Combine(StageRoot, CleanRel))
                    If Not StageDest.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase) Then
                        Throw New InvalidDataException($"快照包含非法路径：{Entry.FullName}")
                    End If
                    Directory.CreateDirectory(Path.GetDirectoryName(StageDest))
                    Entry.ExtractToFile(StageDest, True)
                    RestoredCount += 1
                Next
            End Using

            ProgressCallback?.Invoke("正在应用快照文件...")
            If Snap.Scopes IsNot Nothing Then
                For Each Scope In Snap.Scopes
                    If Scope = "options" Then
                        For Each OptFile In SnapshotOptionFileNames
                            Dim TargetFile = Path.Combine(IndieRoot, OptFile)
                            If File.Exists(TargetFile) Then File.Delete(TargetFile)
                        Next
                    ElseIf Scope = "config" Then
                        For Each SubDirName In {"config", "defaultconfigs"}
                            Dim TargetDir = Path.Combine(IndieRoot, SubDirName)
                            If Directory.Exists(TargetDir) Then Directory.Delete(TargetDir, True)
                        Next
                    Else
                        Dim TargetDir = Path.Combine(IndieRoot, Scope)
                        If Directory.Exists(TargetDir) Then Directory.Delete(TargetDir, True)
                    End If
                Next
            End If

            For Each FilePath In Directory.GetFiles(StageRoot, "*", SearchOption.AllDirectories)
                Dim Rel = FilePath.Substring(StagePrefix.Length)
                Dim Dest = Path.Combine(IndieRoot, Rel)
                Directory.CreateDirectory(Path.GetDirectoryName(Dest))
                If File.Exists(Dest) Then File.Delete(Dest)
                File.Move(FilePath, Dest)
            Next
        Finally
            If Directory.Exists(StageRoot) Then
                Try : Directory.Delete(StageRoot, True) : Catch : End Try
            End If
        End Try
        Return RestoredCount
    End Function

    Public Sub DeleteSnapshotFiles(Instance As McInstance, Snap As SnapshotMeta)
        If Instance Is Nothing OrElse Snap Is Nothing Then Return
        Dim SnapDir = GetSnapshotsDir(Instance)
        Dim ZipPath = Path.Combine(SnapDir, Snap.ZipFileName)
        Dim MetaPath = Path.Combine(SnapDir, Snap.Id & ".meta.json")
        If File.Exists(ZipPath) Then File.Delete(ZipPath)
        If File.Exists(MetaPath) Then File.Delete(MetaPath)
    End Sub

    Public Function PruneAutoBackups(Instance As McInstance, MaxKeep As Integer) As Integer
        If Instance Is Nothing OrElse MaxKeep <= 0 Then Return 0
        Dim AutoList = ListSnapshots(Instance).Where(Function(s) s.IsAutoBackup OrElse If(s.Name, "").StartsWith("[自动备份]", StringComparison.Ordinal)).ToList()
        If AutoList.Count <= MaxKeep Then Return 0
        Dim Removed As Integer = 0
        For Each OldSnap In AutoList.Skip(MaxKeep)
            Try
                DeleteSnapshotFiles(Instance, OldSnap)
                Removed += 1
            Catch ex As Exception
                Logger.Warn(ex, $"清理旧自动备份快照失败: {OldSnap.Name}")
            End Try
        Next
        Return Removed
    End Function

    Private Function AddFolderToArchive(Archive As ZipArchive, DirPath As String, Prefix As String) As Integer
        If Not Directory.Exists(DirPath) Then Return 0
        Dim Count As Integer = 0
        Dim RootFull As String = Path.GetFullPath(DirPath).TrimEnd("\"c)
        For Each FilePath In Directory.EnumerateFiles(RootFull, "*", SearchOption.AllDirectories)
            Dim FullFile = Path.GetFullPath(FilePath)
            Dim Rel = FullFile.Substring(RootFull.Length).TrimStart("\"c, "/"c).Replace("\"c, "/"c)
            Dim EntryName = Prefix & "/" & Rel
            Archive.CreateEntryFromFile(FullFile, EntryName, CompressionLevel.Optimal)
            Count += 1
        Next
        Return Count
    End Function

End Module
