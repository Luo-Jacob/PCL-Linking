Imports System.Collections.Concurrent
Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json

''' <summary>
''' 好友 / 群聊单向实例同步服务：支持将实例作为发布源推送 Mod + Config 修订版，
''' 或由接收方绑定本地实例并在每次启动 PCL Linking 时自动检测同步（也支持随时手动触发同步）。
''' </summary>
Public Module ModInstanceSync

    Public Const MaxConfigZipBytes As Long = 8L * 1024L * 1024L
    Private Const MaxConfigUncompressedBytes As Long = 64L * 1024L * 1024L
    Private Const MaxConfigZipEntries As Integer = 2000

    Public Class InstanceSyncBinding
        Public Property Id As String = ""
        ''' <summary>Publisher 或 Subscriber</summary>
        Public Property Role As String = "Subscriber"
        ''' <summary>friend 或 group</summary>
        Public Property TargetKind As String = "friend"
        Public Property TargetId As String = ""
        Public Property TargetName As String = ""
        Public Property ChannelName As String = ""
        Public Property LocalInstancePath As String = ""
        Public Property LocalInstanceName As String = ""
        Public Property IncludeConfig As Boolean = False
        Public Property DisableExtraMods As Boolean = False
        Public Property AutoSyncOnStartup As Boolean = True
        Public Property LastRevision As Long = 0
        Public Property LastLocalSig As String = ""
        Public Property LastSyncUtc As DateTime? = Nothing
        Public Property LastStatus As String = "尚未同步"
    End Class

    Public Class RemoteSyncSnapshot
        Public Property SourceId As String = ""
        Public Property SourceName As String = ""
        Public Property TargetKind As String = "friend"
        Public Property ChannelName As String = ""
        Public Property Revision As Long = 0
        Public Property McVersion As String = ""
        Public Property ModLoader As Integer = 0
        Public Property ModSyncData As String = ""
        Public Property ConfigUrl As String = ""
        Public Property ConfigKey As String = ""
        Public Property ConfigSha256 As String = ""
        Public Property ReceivedUtc As DateTime = DateTime.UtcNow
    End Class

    Public Class InstanceSyncConfig
        Public Property Bindings As New List(Of InstanceSyncBinding)
        Public Property RemoteSnapshots As New List(Of RemoteSyncSnapshot)
    End Class

    Private ReadOnly SyncLockObj As New Object()
    Private ReadOnly ActiveInstanceSyncs As New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)
    Private _CachedConfig As InstanceSyncConfig = Nothing
    Private IsInitializedFlag As Integer = 0
    Private StartupRunFlag As Integer = 0

    Public Event SyncStateChanged()

    Private Function GetSyncConfigPath() As String
        Return GetPortableDataPath("InstanceSync.json")
    End Function

    Private Function LoadConfigNoLock() As InstanceSyncConfig
        If _CachedConfig IsNot Nothing Then Return _CachedConfig
        Dim Loaded As InstanceSyncConfig = Nothing
        Dim JsonPath = GetSyncConfigPath()
        Try
            If File.Exists(JsonPath) Then
                Loaded = JsonConvert.DeserializeObject(Of InstanceSyncConfig)(File.ReadAllText(JsonPath, Encoding.UTF8))
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取实例同步配置失败，将备份损坏文件并重建空配置")
            Try
                If File.Exists(JsonPath) Then
                    File.Copy(JsonPath, $"{JsonPath}.corrupt.{DateTime.Now:yyyyMMddHHmmss}.bak", True)
                End If
            Catch
            End Try
        End Try
        If Loaded Is Nothing Then Loaded = New InstanceSyncConfig
        If Loaded.Bindings Is Nothing Then Loaded.Bindings = New List(Of InstanceSyncBinding)
        If Loaded.RemoteSnapshots Is Nothing Then Loaded.RemoteSnapshots = New List(Of RemoteSyncSnapshot)
        _CachedConfig = Loaded
        Return _CachedConfig
    End Function

    Private Sub SaveConfigNoLock()
        If _CachedConfig Is Nothing Then Return
        Try
            Dim JsonPath = GetSyncConfigPath()
            Dim ParentDir = Path.GetDirectoryName(JsonPath)
            If Not String.IsNullOrEmpty(ParentDir) AndAlso Not Directory.Exists(ParentDir) Then
                Directory.CreateDirectory(ParentDir)
            End If
            Dim TmpPath = JsonPath & ".tmp"
            File.WriteAllText(TmpPath, JsonConvert.SerializeObject(_CachedConfig, Formatting.Indented), New UTF8Encoding(False))
            If File.Exists(JsonPath) Then
                File.Replace(TmpPath, JsonPath, Nothing, True)
            Else
                File.Move(TmpPath, JsonPath)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "保存实例同步配置失败")
        End Try
    End Sub

    Public Sub Init()
        If Interlocked.Exchange(IsInitializedFlag, 1) = 1 Then Return
        SyncLock SyncLockObj
            LoadConfigNoLock()
        End SyncLock
        AddHandler ModFriends.InstanceSyncMessageReceived, AddressOf OnInstanceSyncMessageReceived
    End Sub

    Private Sub OnInstanceSyncMessageReceived(Msg As ModFriends.FriendMessage)
        If Msg Is Nothing OrElse Msg.FromMe OrElse Msg.Kind <> "inst_sync" Then Return
        If String.IsNullOrWhiteSpace(Msg.ModSyncData) Then Return
        Dim Manifest = ModFriends.DecompressModSyncManifest(Msg.ModSyncData)
        If Manifest Is Nothing Then Return
        Dim TKind = If(Msg.IsGroup, "group", "friend")
        Dim Ch = If(String.IsNullOrWhiteSpace(Msg.SyncChannel), "默认同步实例", Msg.SyncChannel.Trim())
        Dim Rev = Math.Max(1L, Msg.SyncRev)
        Dim Changed As Boolean = False

        SyncLock SyncLockObj
            Dim Cfg = LoadConfigNoLock()
            Dim Existing = Cfg.RemoteSnapshots.FirstOrDefault(
                Function(s) String.Equals(s.SourceId, Msg.PeerId, StringComparison.OrdinalIgnoreCase) AndAlso
                            String.Equals(s.TargetKind, TKind, StringComparison.OrdinalIgnoreCase) AndAlso
                            String.Equals(s.ChannelName, Ch, StringComparison.OrdinalIgnoreCase))
            Dim IsNew = (Existing Is Nothing)
            If IsNew Then
                Existing = New RemoteSyncSnapshot With {
                    .SourceId = Msg.PeerId,
                    .TargetKind = TKind,
                    .ChannelName = Ch
                }
                Cfg.RemoteSnapshots.Add(Existing)
            End If
            If IsNew OrElse Rev > Existing.Revision Then
                Existing.SourceName = If(String.IsNullOrWhiteSpace(Msg.SenderName), Msg.PeerId, Msg.SenderName)
                Existing.Revision = Rev
                Existing.McVersion = If(Manifest.Ver, "")
                Existing.ModLoader = Manifest.Ldr
                Existing.ModSyncData = Msg.ModSyncData
                Existing.ConfigUrl = If(Msg.ConfigUrl, "")
                Existing.ConfigKey = If(Msg.ConfigKey, "")
                Existing.ConfigSha256 = If(Msg.ConfigSha256, "")
                Existing.ReceivedUtc = DateTime.UtcNow
                Changed = True
            End If
            If Changed Then SaveConfigNoLock()
        End SyncLock
        If Changed Then RaiseEvent SyncStateChanged()
    End Sub

    Public Function GetBindings() As List(Of InstanceSyncBinding)
        SyncLock SyncLockObj
            Return LoadConfigNoLock().Bindings.Where(Function(b) b IsNot Nothing).Select(Function(b) CloneBinding(b)).ToList()
        End SyncLock
    End Function

    Public Function GetRemoteSnapshots() As List(Of RemoteSyncSnapshot)
        SyncLock SyncLockObj
            Return LoadConfigNoLock().RemoteSnapshots.OrderByDescending(Function(s) s.ReceivedUtc).ToList()
        End SyncLock
    End Function

    Private Function CloneBinding(B As InstanceSyncBinding) As InstanceSyncBinding
        If B Is Nothing Then Return Nothing
        Return New InstanceSyncBinding With {
            .Id = B.Id, .Role = B.Role, .TargetKind = B.TargetKind, .TargetId = B.TargetId,
            .TargetName = B.TargetName, .ChannelName = B.ChannelName,
            .LocalInstancePath = B.LocalInstancePath, .LocalInstanceName = B.LocalInstanceName,
            .IncludeConfig = B.IncludeConfig, .DisableExtraMods = B.DisableExtraMods,
            .AutoSyncOnStartup = B.AutoSyncOnStartup, .LastRevision = B.LastRevision,
            .LastLocalSig = B.LastLocalSig, .LastSyncUtc = B.LastSyncUtc, .LastStatus = B.LastStatus
        }
    End Function

    Public Function UpsertPublisherBinding(TargetId As String, IsGroup As Boolean, TargetName As String,
                                           Instance As McInstance, ChannelName As String,
                                           IncludeConfig As Boolean, AutoOnStartup As Boolean) As InstanceSyncBinding
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))
        Dim RawPath = PathUtils.RemoveExtendedPrefix(Instance.PathVersion).TrimEnd("\"c, "/"c)
        Dim NormPath = NormalizeInstPath(RawPath)
        Dim TKind = If(IsGroup, "group", "friend")
        Dim Ch = If(String.IsNullOrWhiteSpace(ChannelName), Instance.Name, ChannelName.Trim())
        Dim Result As InstanceSyncBinding
        SyncLock SyncLockObj
            Dim Cfg = LoadConfigNoLock()
            Dim Existing = Cfg.Bindings.FirstOrDefault(
                Function(b) b.Role = "Publisher" AndAlso
                            String.Equals(b.TargetId, TargetId, StringComparison.OrdinalIgnoreCase) AndAlso
                            String.Equals(b.TargetKind, TKind, StringComparison.OrdinalIgnoreCase) AndAlso
                            NormalizeInstPath(b.LocalInstancePath) = NormPath)
            If Existing Is Nothing Then
                Existing = New InstanceSyncBinding With {
                    .Id = "pub_" & Guid.NewGuid().ToString("N").Substring(0, 8),
                    .Role = "Publisher",
                    .TargetKind = TKind,
                    .TargetId = TargetId,
                    .LocalInstancePath = RawPath
                }
                Cfg.Bindings.Add(Existing)
            Else
                Existing.LocalInstancePath = RawPath
            End If
            Existing.TargetName = TargetName
            Existing.LocalInstanceName = Instance.Name
            Existing.ChannelName = Ch
            Existing.IncludeConfig = IncludeConfig
            Existing.AutoSyncOnStartup = AutoOnStartup
            SaveConfigNoLock()
            Result = CloneBinding(Existing)
        End SyncLock
        RaiseEvent SyncStateChanged()
        Return Result
    End Function

    Public Function UpsertSubscriberBinding(SourceId As String, IsGroup As Boolean, SourceName As String,
                                            ChannelName As String, Instance As McInstance,
                                            IncludeConfig As Boolean, DisableExtraMods As Boolean,
                                            AutoOnStartup As Boolean) As InstanceSyncBinding
        If Instance Is Nothing Then Throw New ArgumentNullException(NameOf(Instance))
        Dim RawPath = PathUtils.RemoveExtendedPrefix(Instance.PathVersion).TrimEnd("\"c, "/"c)
        Dim NormPath = NormalizeInstPath(RawPath)
        Dim TKind = If(IsGroup, "group", "friend")
        Dim Ch = If(String.IsNullOrWhiteSpace(ChannelName), Instance.Name, ChannelName.Trim())
        Dim Result As InstanceSyncBinding
        SyncLock SyncLockObj
            Dim Cfg = LoadConfigNoLock()
            Dim Existing = Cfg.Bindings.FirstOrDefault(
                Function(b) b.Role = "Subscriber" AndAlso
                            String.Equals(b.TargetId, SourceId, StringComparison.OrdinalIgnoreCase) AndAlso
                            String.Equals(b.TargetKind, TKind, StringComparison.OrdinalIgnoreCase) AndAlso
                            String.Equals(b.ChannelName, Ch, StringComparison.OrdinalIgnoreCase) AndAlso
                            NormalizeInstPath(b.LocalInstancePath) = NormPath)
            If Existing Is Nothing Then
                Existing = New InstanceSyncBinding With {
                    .Id = "sub_" & Guid.NewGuid().ToString("N").Substring(0, 8),
                    .Role = "Subscriber",
                    .TargetKind = TKind,
                    .TargetId = SourceId,
                    .ChannelName = Ch,
                    .LocalInstancePath = RawPath
                }
                Cfg.Bindings.Add(Existing)
            Else
                Existing.LocalInstancePath = RawPath
            End If
            Existing.TargetName = SourceName
            Existing.LocalInstanceName = Instance.Name
            Existing.IncludeConfig = IncludeConfig
            Existing.DisableExtraMods = DisableExtraMods
            Existing.AutoSyncOnStartup = AutoOnStartup
            SaveConfigNoLock()
            Result = CloneBinding(Existing)
        End SyncLock
        RaiseEvent SyncStateChanged()
        Return Result
    End Function

    Public Sub ToggleBindingAutoStartup(BindingId As String)
        SyncLock SyncLockObj
            Dim Cfg = LoadConfigNoLock()
            Dim Target = Cfg.Bindings.FirstOrDefault(Function(b) String.Equals(b.Id, BindingId, StringComparison.OrdinalIgnoreCase))
            If Target Is Nothing Then Return
            Target.AutoSyncOnStartup = Not Target.AutoSyncOnStartup
            SaveConfigNoLock()
        End SyncLock
        RaiseEvent SyncStateChanged()
    End Sub

    Public Function DeleteBinding(BindingId As String) As Boolean
        Dim Removed As Integer
        SyncLock SyncLockObj
            Dim Cfg = LoadConfigNoLock()
            Removed = Cfg.Bindings.RemoveAll(Function(b) String.Equals(b.Id, BindingId, StringComparison.OrdinalIgnoreCase))
            If Removed > 0 Then SaveConfigNoLock()
        End SyncLock
        If Removed > 0 Then RaiseEvent SyncStateChanged()
        Return Removed > 0
    End Function

    Private Function NormalizeInstPath(PathStr As String) As String
        If String.IsNullOrWhiteSpace(PathStr) Then Return ""
        Return PathUtils.RemoveExtendedPrefix(PathStr).TrimEnd("\"c, "/"c).ToLowerInvariant()
    End Function

    Public Function FindLocalInstanceByPath(InstancePath As String) As McInstance
        Dim Target = NormalizeInstPath(InstancePath)
        If String.IsNullOrEmpty(Target) Then Return Nothing
        Dim Selected = McInstanceSelected
        If Selected IsNot Nothing AndAlso NormalizeInstPath(Selected.PathVersion) = Target Then Return Selected
        Try
            If McInstanceList IsNot Nothing Then
                For Each PairList In McInstanceList.Values.ToArray()
                    If PairList Is Nothing Then Continue For
                    For Each Inst In PairList.ToArray()
                        If Inst IsNot Nothing AndAlso NormalizeInstPath(Inst.PathVersion) = Target Then Return Inst
                    Next
                Next
            End If
        Catch
        End Try
        Dim RawPath = PathUtils.RemoveExtendedPrefix(If(InstancePath, "")).Trim().TrimEnd("\"c, "/"c)
        If RawPath.Contains(":") AndAlso Directory.Exists(RawPath) Then
            Try
                Dim DirectInst As New McInstance(RawPath)
                DirectInst.Load()
                If DirectInst.State <> McInstance.McInstanceState.Error Then Return DirectInst
            Catch
            End Try
        End If
        Return Nothing
    End Function

    Private Function IsInstanceBusyOrRunning(TargetInst As McInstance) As Boolean
        If TargetInst Is Nothing Then Return False
        If McInstanceLock.IsLocked(TargetInst) Then Return True
        Dim TargetNorm = NormalizeInstPath(TargetInst.PathVersion)
        For Each W In ModWatcher.McWatcherList.ToArray()
            If W Is Nothing OrElse W.Instance Is Nothing Then Continue For
            If W.State = ModWatcher.Watcher.MinecraftState.Ended OrElse W.State = ModWatcher.Watcher.MinecraftState.Canceled Then Continue For
            Try
                If W.GameProcess IsNot Nothing AndAlso W.GameProcess.HasExited Then Continue For
            Catch
                Continue For
            End Try
            If NormalizeInstPath(W.Instance.PathVersion) = TargetNorm Then Return True
        Next
        Return False
    End Function

    Public Function ComputeInstanceQuickSignature(Instance As McInstance, IncludeConfig As Boolean) As String
        If Instance Is Nothing Then Return ""
        Dim Sb As New StringBuilder()
        Dim IndieRoot = PathUtils.RemoveExtendedPrefix(Instance.PathIndie)
        Dim ModsDir = Path.Combine(IndieRoot, "mods")
        If Directory.Exists(ModsDir) Then
            For Each F In Directory.GetFiles(ModsDir).OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase)
                Dim Ext = Path.GetExtension(F).ToLowerInvariant()
                If Ext = ".jar" OrElse Ext = ".litemod" Then
                    Try
                        Dim Info As New FileInfo(F)
                        Sb.Append($"M:{Info.Name}:{Info.Length}:{Info.LastWriteTimeUtc.Ticks}|")
                    Catch
                    End Try
                End If
            Next
        End If
        If IncludeConfig Then
            Dim ConfigDir = Path.Combine(IndieRoot, "config")
            If Directory.Exists(ConfigDir) Then
                For Each F In Directory.EnumerateFiles(ConfigDir, "*", SearchOption.AllDirectories).OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase)
                    If IsIgnoredConfigFile(F) Then Continue For
                    Try
                        Dim Info As New FileInfo(F)
                        Sb.Append($"C:{F.Substring(ConfigDir.Length)}:{Info.Length}:{Info.LastWriteTimeUtc.Ticks}|")
                    Catch
                    End Try
                Next
            End If
        End If
        Using Sha = SHA256.Create()
            Return BitConverter.ToString(Sha.ComputeHash(Encoding.UTF8.GetBytes(Sb.ToString()))).Replace("-", "")
        End Using
    End Function

    Private Function IsIgnoredConfigFile(FullPath As String) As Boolean
        Dim Lower = FullPath.ToLowerInvariant()
        If Lower.EndsWith(".log") OrElse Lower.EndsWith(".tmp") OrElse Lower.EndsWith(".bak") Then Return True
        If Lower.Contains("\logs\") OrElse Lower.Contains("\cache\") OrElse Lower.Contains("\.cache\") OrElse Lower.Contains("\backups\") Then Return True
        Return False
    End Function

    Public Function PackConfigFolderToBytes(Instance As McInstance, ByRef ErrMsg As String) As Byte()
        ErrMsg = Nothing
        Dim ConfigDir = Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathIndie), "config")
        If Not Directory.Exists(ConfigDir) Then Return Nothing

        Dim Files As New List(Of String)
        Dim TotalUncompressed As Long = 0
        For Each FPath In Directory.EnumerateFiles(ConfigDir, "*", SearchOption.AllDirectories)
            If IsIgnoredConfigFile(FPath) Then Continue For
            Files.Add(FPath)
            If Files.Count > MaxConfigZipEntries Then
                ErrMsg = $"当前实例 config 目录包含超过 {MaxConfigZipEntries} 个文件，已超出安全同步上限，请清理缓存或取消勾选附带 Config。"
                Return Nothing
            End If
            Try
                TotalUncompressed += New FileInfo(FPath).Length
                If TotalUncompressed > MaxConfigUncompressedBytes Then
                    ErrMsg = $"当前实例 config 目录未压缩体积超过 {MaxConfigUncompressedBytes \ (1024 * 1024)} MB 上限，请取消勾选附带 Config 或清理大文件。"
                    Return Nothing
                End If
            Catch
            End Try
        Next
        If Not Files.Any() Then Return Nothing

        Using Ms As New MemoryStream()
            Using Zip As New ZipArchive(Ms, ZipArchiveMode.Create, True)
                Dim RootLen = ConfigDir.TrimEnd("\"c, "/"c).Length + 1
                For Each FPath In Files
                    Try
                        Dim Rel = FPath.Substring(RootLen).Replace("\"c, "/"c)
                        Zip.CreateEntryFromFile(FPath, Rel, CompressionLevel.Optimal)
                    Catch ex As Exception
                        Logger.Warn(ex, $"打包同步 config 文件跳过：{FPath}")
                    End Try
                Next
            End Using
            Dim Bytes = Ms.ToArray()
            If Bytes.LongLength > MaxConfigZipBytes Then
                ErrMsg = $"当前实例 config 目录压缩后为 {Bytes.LongLength / 1048576.0:0.1} MB，超过 {MaxConfigZipBytes \ (1024 * 1024)} MB 同步上限，请取消勾选附带 Config 或清理大文件。"
                Return Nothing
            End If
            Return Bytes
        End Using
    End Function

    Private Function BackupFolderRolling(SourceDir As String, BackupRootDir As String, KeepCount As Integer, ByRef OutSnapFolder As String) As Boolean
        OutSnapFolder = Nothing
        If String.IsNullOrWhiteSpace(SourceDir) OrElse Not Directory.Exists(SourceDir) Then Return False
        Try
            OutSnapFolder = Path.Combine(BackupRootDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"))
            CopyDirectorySafe(SourceDir, OutSnapFolder)
            If Directory.Exists(BackupRootDir) Then
                Dim OldBackups = New DirectoryInfo(BackupRootDir).GetDirectories().OrderByDescending(Function(d) d.CreationTimeUtc).Skip(Math.Max(1, KeepCount)).ToList()
                For Each OldDir In OldBackups
                    Try
                        OldDir.Delete(True)
                    Catch
                    End Try
                Next
            End If
            Return True
        Catch ex As Exception
            Logger.Warn(ex, $"创建滚动备份失败：{SourceDir}")
            Return False
        End Try
    End Function

    Public Sub ApplyConfigZipBytes(Instance As McInstance, ZipBytes As Byte())
        If Instance Is Nothing OrElse ZipBytes Is Nothing OrElse ZipBytes.Length = 0 Then Return
        Dim IndieRoot = PathUtils.RemoveExtendedPrefix(Instance.PathIndie)
        Dim ConfigDir = Path.Combine(IndieRoot, "config")
        Dim BackupRoot = Path.Combine(PathUtils.RemoveExtendedPrefix(Instance.PathVersion), "PCL Linking", "ConfigSyncBackups")

        '1. 覆盖前先保留一份本地 config 备份（滚动留最近 3 份）
        Dim SnapFolder As String = Nothing
        Dim HasBackup = BackupFolderRolling(ConfigDir, BackupRoot, 3, SnapFolder)

        '2. 防 ZipSlip 与 Zip 炸弹解压覆盖
        Try
            ZipSafetyCore.ExtractZipBytesSafely(
                ZipBytes, ConfigDir, MaxConfigZipEntries, MaxConfigUncompressedBytes,
                Sub(EntryName) Logger.Warn($"拦截可疑 Config Zip 路径穿越条目：{EntryName}"))
        Catch
            If HasBackup AndAlso Not String.IsNullOrEmpty(SnapFolder) AndAlso Directory.Exists(SnapFolder) Then
                Try
                    If Directory.Exists(ConfigDir) Then Directory.Delete(ConfigDir, True)
                    ZipSafetyCore.CopyDirectorySafe(SnapFolder, ConfigDir)
                Catch exRestore As Exception
                    Logger.Warn(exRestore, "Config 同步回滚恢复原配置失败")
                End Try
            End If
            Throw
        End Try
    End Sub

    Public ReadOnly AllowedEnvPackFolders As String() = {"mods", "config", "defaultconfigs", "kubejs", "resourcepacks", "shaderpacks"}
    Private Const MaxEnvPackEntries As Integer = 15000
    Private Const MaxEnvPackUncompressedBytes As Long = 4L * 1024L * 1024L * 1024L

    Public Function PackInstanceEnvironmentToZip(Instance As McInstance, IncludedDirs As IEnumerable(Of String),
                                                 ExcludeModrinthJars As Boolean, ByRef OutTempZipPath As String,
                                                 ByRef OutFileCount As Integer, ByRef OutSizeBytes As Long,
                                                 Optional OnProgress As Action(Of Integer, Integer) = Nothing) As String
        OutTempZipPath = ""
        OutFileCount = 0
        OutSizeBytes = 0
        If Instance Is Nothing Then Return "未选择有效的游戏实例！"

        Dim ValidDirs = If(IncludedDirs, Array.Empty(Of String)()) _
            .Select(Function(d) If(d, "").Trim().ToLowerInvariant()) _
            .Where(Function(d) AllowedEnvPackFolders.Contains(d)) _
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        If Not ValidDirs.Any() Then Return "请至少勾选一个要打包的实例目录！"

        Dim SkipModFileNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If ExcludeModrinthJars AndAlso ValidDirs.Contains("mods") Then
            Try
                Dim Manifest = ModFriends.BuildInstanceModSyncManifest(Instance)
                If Manifest?.Items IsNot Nothing Then
                    For Each Item In Manifest.Items
                        If Not String.IsNullOrWhiteSpace(Item.F) AndAlso
                           Not String.IsNullOrWhiteSpace(Item.I) AndAlso
                           Not String.IsNullOrWhiteSpace(Item.H) AndAlso
                           Item.P = CInt(ResourcePlatforms.Modrinth) Then
                            SkipModFileNames.Add(Item.F)
                        End If
                    Next
                End If
            Catch exMan As Exception
                Logger.Warn(exMan, "构建 Modrinth 差分剔除列表失败，将改打包全部 Mods")
            End Try
        End If

        Dim IndieRoot = PathUtils.RemoveExtendedPrefix(Instance.PathIndie).TrimEnd("\"c, "/"c)
        Dim Entries As New List(Of (FullPath As String, RelPath As String))
        For Each SubDirName In ValidDirs
            Dim FolderPath = Path.Combine(IndieRoot, SubDirName)
            If Not Directory.Exists(FolderPath) Then Continue For
            For Each FPath In Directory.EnumerateFiles(FolderPath, "*", SearchOption.AllDirectories)
                If IsIgnoredConfigFile(FPath) Then Continue For
                If SubDirName = "mods" AndAlso SkipModFileNames.Contains(Path.GetFileName(FPath)) Then Continue For
                Dim Rel = SubDirName & "\" & FPath.Substring(FolderPath.Length).TrimStart("\"c, "/"c)
                Entries.Add((FPath, Rel.Replace("\"c, "/"c)))
                If Entries.Count > MaxEnvPackEntries Then
                    Return $"待打包文件总数超过 {MaxEnvPackEntries} 个安全上限，请取消勾选大型材质/光影后重试！"
                End If
            Next
        Next
        If Not Entries.Any() Then Return "所选目录中没有可打包的文件（或全部为可在线拉取的 Modrinth 模组）！"

        Dim CacheDir = Path.Combine(PathUtils.RemoveExtendedPrefix(PathTemp), "Cache")
        Directory.CreateDirectory(CacheDir)
        Dim SafeInstName = If(Instance.Name, "Instance")
        For Each BadCh In Path.GetInvalidFileNameChars()
            SafeInstName = SafeInstName.Replace(BadCh, "_"c)
        Next
        SafeInstName = SafeInstName.Replace("..", "_").Trim("."c, " "c)
        If String.IsNullOrWhiteSpace(SafeInstName) Then SafeInstName = "Instance"
        Dim TempZip = Path.Combine(CacheDir, $"EnvPack_{SafeInstName}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.zip")
        Try
            Using Zip = ZipFile.Open(TempZip, ZipArchiveMode.Create)
                Dim MetaJson = New Newtonsoft.Json.Linq.JObject From {
                    {"v", 1},
                    {"instance", Instance.Name},
                    {"mc", Instance.Version.VanillaName},
                    {"loader", CInt(Instance.Version.ModLoaders)},
                    {"dirs", New Newtonsoft.Json.Linq.JArray(ValidDirs)},
                    {"diff_skip_modrinth", ExcludeModrinthJars},
                    {"packed_utc", DateTime.UtcNow.ToString("o")}
                }
                Dim MetaEntry = Zip.CreateEntry("pcllk-env-meta.json", CompressionLevel.Optimal)
                Using Sw As New StreamWriter(MetaEntry.Open(), New UTF8Encoding(False))
                    Sw.Write(MetaJson.ToString(Newtonsoft.Json.Formatting.Indented))
                End Using

                Dim TotalCnt = Entries.Count
                For i = 0 To TotalCnt - 1
                    Dim Item = Entries(i)
                    Try
                        Zip.CreateEntryFromFile(Item.FullPath, Item.RelPath, CompressionLevel.Optimal)
                        OutFileCount += 1
                    Catch exAdd As Exception
                        Logger.Warn(exAdd, $"环境快照打包跳过文件：{Item.FullPath}")
                    End Try
                    If (i + 1) Mod 20 = 0 OrElse i = TotalCnt - 1 Then
                        OnProgress?.Invoke(i + 1, TotalCnt)
                    End If
                Next
            End Using
            OutSizeBytes = New FileInfo(TempZip).Length
            OutTempZipPath = TempZip
            AppEventBus.Publish("instance.env_pack.created", "ModInstanceSync",
                New Newtonsoft.Json.Linq.JObject From {{"instance", Instance.Name}, {"files", OutFileCount}, {"bytes", OutSizeBytes}})
            Return Nothing
        Catch ex As Exception
            Try
                If File.Exists(TempZip) Then File.Delete(TempZip)
            Catch
            End Try
            Return "打包环境快照失败：" & ex.Message
        End Try
    End Function

    Public Function ApplyEnvironmentPackZip(TargetInst As McInstance, ZipFilePath As String, ByRef OutExtractedFiles As Integer, ByRef OutBackupSummary As String) As String
        OutExtractedFiles = 0
        OutBackupSummary = ""
        If TargetInst Is Nothing Then Return "请先在左侧/主界面选择要应用环境包的本地游戏实例！"
        If IsInstanceBusyOrRunning(TargetInst) Then
            Return $"实例「{TargetInst.Name}」正在运行或被占用，请先关闭游戏后再应用环境快照！"
        End If
        Dim IndieRoot = PathUtils.RemoveExtendedPrefix(TargetInst.PathIndie).TrimEnd("\"c, "/"c)
        Dim BackupRoot = Path.Combine(PathUtils.RemoveExtendedPrefix(TargetInst.PathVersion), "PCL Linking", "EnvPackBackups")

        Dim ConfigSnap As String = Nothing
        If BackupFolderRolling(Path.Combine(IndieRoot, "config"), Path.Combine(BackupRoot, "config"), 3, ConfigSnap) Then
            OutBackupSummary = "已自动备份旧 config"
        End If

        Try
            OutExtractedFiles = ZipSafetyCore.ExtractZipFileSafely(
                ZipFilePath, IndieRoot, MaxEnvPackEntries, MaxEnvPackUncompressedBytes,
                AllowedTopFolders:=AllowedEnvPackFolders,
                OnEntrySkippedOrBlocked:=Sub(EntryName) Logger.Warn($"环境包导入已跳过/拦截条目：{EntryName}"))
            If OutExtractedFiles <= 0 Then
                Return "压缩包中未包含可应用的游戏环境目录（仅支持 mods / config / defaultconfigs / kubejs / resourcepacks / shaderpacks）。"
            End If
            AppEventBus.Publish("instance.env_pack.applied", "ModInstanceSync",
                New Newtonsoft.Json.Linq.JObject From {{"instance", TargetInst.Name}, {"files", OutExtractedFiles}})
            Return Nothing
        Catch ex As Exception
            Return "应用环境快照失败：" & ex.Message
        End Try
    End Function

    Public Function PackWorldSaveToTempZip(WorldDirPath As String, ByRef OutTempZip As String, ByRef OutSizeBytes As Long, ByRef OutSha256Hex As String,
                                           Optional OnProgress As Action(Of Integer, Integer) = Nothing) As String
        OutTempZip = ""
        OutSizeBytes = 0
        OutSha256Hex = ""
        If String.IsNullOrWhiteSpace(WorldDirPath) OrElse Not Directory.Exists(WorldDirPath) Then
            Return "找不到指定的世界存档目录！"
        End If
        Dim CacheDir = Path.Combine(PathUtils.RemoveExtendedPrefix(PathTemp), "Cache")
        Directory.CreateDirectory(CacheDir)
        Dim WorldName = Path.GetFileName(WorldDirPath.TrimEnd("\"c, "/"c))
        Dim TmpZip = Path.Combine(CacheDir, $"SaveRelay_{Guid.NewGuid().ToString("N").Substring(0, 10)}.zip")
        Try
            Dim Res = EasyTierSideChannelCore.PackWorldSaveToZip(WorldDirPath, TmpZip, OnProgress)
            OutTempZip = TmpZip
            OutSizeBytes = Res.SizeBytes
            OutSha256Hex = Res.Sha256
            Return Nothing
        Catch ex As Exception
            Try
                If File.Exists(TmpZip) Then File.Delete(TmpZip)
            Catch
            End Try
            Return "打包世界存档失败：" & ex.Message
        End Try
    End Function

    Public Function ApplySaveRelayZipToInstance(TargetInst As McInstance, ZipFilePath As String, WorldName As String, ByRef OutFinalWorldDir As String, ByRef OutBackupNote As String) As String
        OutFinalWorldDir = ""
        OutBackupNote = ""
        If TargetInst Is Nothing Then Return "请先选择要导入存档的游戏实例！"
        If IsInstanceBusyOrRunning(TargetInst) Then
            Return $"实例「{TargetInst.Name}」正在运行或被占用，请先退出游戏后再导入接力存档！"
        End If
        Dim CleanWorld = If(WorldName, "RelayWorld").Trim()
        For Each BadCh In Path.GetInvalidFileNameChars()
            CleanWorld = CleanWorld.Replace(BadCh, "_"c)
        Next
        CleanWorld = CleanWorld.Trim("."c, " "c)
        If String.IsNullOrWhiteSpace(CleanWorld) OrElse Not SecurityGuard.IsSafeRelativePath(CleanWorld) Then
            CleanWorld = "RelayWorld"
        End If

        Dim IndieRoot = PathUtils.RemoveExtendedPrefix(TargetInst.PathIndie).TrimEnd("\"c, "/"c)
        Dim SavesDir = Path.Combine(IndieRoot, "saves")
        Directory.CreateDirectory(SavesDir)
        Dim DestWorldDir = Path.Combine(SavesDir, CleanWorld)

        If Directory.Exists(DestWorldDir) Then
            Dim BackupRoot = Path.Combine(PathUtils.RemoveExtendedPrefix(TargetInst.PathVersion), "PCL Linking", "SaveRelayBackups", CleanWorld)
            Dim SnapDir As String = Nothing
            If BackupFolderRolling(DestWorldDir, BackupRoot, 5, SnapDir) Then
                OutBackupNote = $"已自动备份原本地存档至 {Path.GetFileName(SnapDir)}"
            End If
        End If

        EasyTierSideChannelCore.ExtractWorldSaveZipSafely(ZipFilePath, DestWorldDir, DeleteZipAfter:=True)
        OutFinalWorldDir = DestWorldDir
        AppEventBus.Publish("save_relay.imported", "ModInstanceSync",
            New Newtonsoft.Json.Linq.JObject From {{"instance", TargetInst.Name}, {"world", CleanWorld}})
        Return Nothing
    End Function

    Private Sub CopyDirectorySafe(SrcDir As String, DestDir As String)
        ZipSafetyCore.CopyDirectorySafe(SrcDir, DestDir)
    End Sub

    Public Function PublishBindingNow(BindingId As String, Optional SilentIfUnchanged As Boolean = False) As String
        Dim Binding As InstanceSyncBinding = Nothing
        SyncLock SyncLockObj
            Binding = CloneBinding(LoadConfigNoLock().Bindings.FirstOrDefault(Function(b) String.Equals(b.Id, BindingId, StringComparison.OrdinalIgnoreCase)))
        End SyncLock
        If Binding Is Nothing OrElse Binding.Role <> "Publisher" Then Return "未找到对应的发布绑定记录。"

        Dim Inst = FindLocalInstanceByPath(Binding.LocalInstancePath)
        If Inst Is Nothing Then
            UpdateBindingStatus(BindingId, Binding.LastRevision, Binding.LastLocalSig, "未找到本地实例，已跳过")
            Return $"未找到本地实例「{Binding.LocalInstanceName}」"
        End If

        Dim Sig = ComputeInstanceQuickSignature(Inst, Binding.IncludeConfig)
        If SilentIfUnchanged AndAlso Binding.LastRevision > 0 AndAlso String.Equals(Sig, Binding.LastLocalSig, StringComparison.OrdinalIgnoreCase) Then
            Return Nothing
        End If

        Dim Manifest = ModFriends.BuildInstanceModSyncManifest(Inst)
        If Manifest Is Nothing Then Return "生成实例 Mod 清单失败"
        Dim ModData = ModFriends.CompressModSyncManifest(Manifest)

        Dim CfgUrl As String = ""
        Dim CfgKey As String = ""
        Dim CfgSha As String = ""
        If Binding.IncludeConfig Then
            Dim PackErr As String = Nothing
            Dim CfgBytes = PackConfigFolderToBytes(Inst, PackErr)
            If PackErr IsNot Nothing Then
                UpdateBindingStatus(BindingId, Binding.LastRevision, Binding.LastLocalSig, PackErr)
                Return PackErr
            End If
            If CfgBytes IsNot Nothing AndAlso CfgBytes.Length > 0 Then
                Dim UpErr = ModFriends.UploadEncryptedBlob(CfgBytes, CfgUrl, CfgKey, CfgSha)
                If UpErr IsNot Nothing Then
                    UpdateBindingStatus(BindingId, Binding.LastRevision, Binding.LastLocalSig, UpErr)
                    Return UpErr
                End If
            End If
        End If

        Dim NewRev = Math.Max(Binding.LastRevision + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        Dim CfgNote = If(Not String.IsNullOrEmpty(CfgUrl), " + 附带加密 Config", "")
        Dim Msg As New ModFriends.FriendMessage With {
            .Kind = "inst_sync",
            .Text = $"实例同步「{Binding.ChannelName}」已推送修订版 r{NewRev}（MC {Manifest.Ver} · {Manifest.Items.Count} 个 Mod{CfgNote}）",
            .SyncChannel = Binding.ChannelName,
            .SyncRev = NewRev,
            .ModSyncData = ModData,
            .ConfigUrl = CfgUrl,
            .ConfigKey = CfgKey,
            .ConfigSha256 = CfgSha
        }
        Dim SendErr = ModFriends.SendRichMessage(Binding.TargetId, Binding.TargetKind = "group", Msg)
        If SendErr IsNot Nothing Then
            UpdateBindingStatus(BindingId, Binding.LastRevision, Binding.LastLocalSig, SendErr)
            Return SendErr
        End If
        UpdateBindingStatus(BindingId, NewRev, Sig, $"已推送修订版 r{NewRev}（{Manifest.Items.Count} 个 Mod{CfgNote}）")
        Return Nothing
    End Function

    Public Sub SyncSubscriberBindingNow(BindingId As String, Optional IsAutoStartup As Boolean = False)
        Dim Binding As InstanceSyncBinding = Nothing
        Dim Snap As RemoteSyncSnapshot = Nothing
        SyncLock SyncLockObj
            Dim Cfg = LoadConfigNoLock()
            Dim FoundB = Cfg.Bindings.FirstOrDefault(Function(b) String.Equals(b.Id, BindingId, StringComparison.OrdinalIgnoreCase))
            If FoundB Is Nothing OrElse FoundB.Role <> "Subscriber" Then Return
            Binding = CloneBinding(FoundB)
            Snap = Cfg.RemoteSnapshots.Where(
                Function(s) String.Equals(s.SourceId, Binding.TargetId, StringComparison.OrdinalIgnoreCase) AndAlso
                            String.Equals(s.TargetKind, Binding.TargetKind, StringComparison.OrdinalIgnoreCase) AndAlso
                            String.Equals(s.ChannelName, Binding.ChannelName, StringComparison.OrdinalIgnoreCase)) _
                .OrderByDescending(Function(s) s.Revision).FirstOrDefault()
        End SyncLock

        If Snap Is Nothing Then
            If Not IsAutoStartup Then
                Hint($"尚未收到「{Binding.ChannelName}」的最新同步推送，请让发布者先点击推送或等待拉取。", HintType.Blue)
            End If
            Return
        End If
        If IsAutoStartup AndAlso Snap.Revision <= Binding.LastRevision Then
            Return
        End If

        Dim TargetInst = FindLocalInstanceByPath(Binding.LocalInstancePath)
        If TargetInst Is Nothing Then
            UpdateBindingStatus(BindingId, Binding.LastRevision, Binding.LastLocalSig, $"找不到本地实例「{Binding.LocalInstanceName}」")
            If Not IsAutoStartup Then Hint($"找不到本地实例「{Binding.LocalInstanceName}」", HintType.Red)
            Return
        End If
        If IsInstanceBusyOrRunning(TargetInst) Then
            UpdateBindingStatus(BindingId, Binding.LastRevision, Binding.LastLocalSig, "实例正在运行或被占用，已跳过本次同步")
            If Not IsAutoStartup Then Hint($"实例「{TargetInst.Name}」当前正在运行或使用中，无法覆盖同步！", HintType.Red)
            Return
        End If

        Dim SyncKey = NormalizeInstPath(TargetInst.PathVersion)
        If Not ActiveInstanceSyncs.TryAdd(SyncKey, 0) Then
            If Not IsAutoStartup Then Hint($"实例「{TargetInst.Name}」正在执行同步，请稍候！", HintType.Blue)
            Return
        End If

        Dim HandedOffAsync As Boolean = False
        Try
            Dim HostManifest = ModFriends.DecompressModSyncManifest(Snap.ModSyncData)
            If HostManifest Is Nothing Then Return
            Dim Diff = PageFriends.ScanAndCompareHostMods(HostManifest, TargetInst)
            If Diff.IsVersionMismatch OrElse Diff.IsLoaderMismatch Then
                Dim WarnText = $"MC 版本或加载器不匹配（远端 {HostManifest.Ver} vs 本地 {TargetInst.Version.VanillaName}），请确认绑定正确实例"
                UpdateBindingStatus(BindingId, Binding.LastRevision, Binding.LastLocalSig, WarnText)
                If Not IsAutoStartup Then Hint(WarnText, HintType.Red)
                Return
            End If

            Dim NeedModWork = Diff.DisabledMatch.Any() OrElse Diff.MissingDownloadable.Any() OrElse
                              Diff.MismatchDownloadable.Any() OrElse (Binding.DisableExtraMods AndAlso Diff.ExtraLocal.Any())

            Dim ConfigFailed As Boolean = False
            Dim ConfigSummary As String = ""
            If Binding.IncludeConfig AndAlso Not String.IsNullOrWhiteSpace(Snap.ConfigUrl) AndAlso Not String.IsNullOrWhiteSpace(Snap.ConfigKey) Then
                Dim ZipBytes As Byte() = Nothing
                Dim DlErr = ModFriends.DownloadAndDecryptBlob(Snap.ConfigUrl, Snap.ConfigKey, Snap.ConfigSha256, ZipBytes)
                If DlErr Is Nothing AndAlso ZipBytes IsNot Nothing Then
                    Try
                        ApplyConfigZipBytes(TargetInst, ZipBytes)
                        ConfigSummary = "，已同步 Config 配置"
                    Catch exCfg As Exception
                        ConfigFailed = True
                        ConfigSummary = $"（Config 同步失败：{exCfg.Message}）"
                    End Try
                Else
                    ConfigFailed = True
                    ConfigSummary = $"（Config 同步跳过：{DlErr}）"
                End If
            End If

            If NeedModWork Then
                Dim CapturedTargetRev = Snap.Revision
                Dim CapturedPrevRev = Binding.LastRevision
                Dim CapturedCfgFailed = ConfigFailed
                Dim CapturedCfgNote = ConfigSummary
                HandedOffAsync = True
                PageFriends.ExecuteFriendModSync(
                    TargetInst,
                    HostManifest,
                    Diff.MissingDownloadable,
                    Diff.MismatchDownloadable,
                    Diff.DisabledMatch,
                    If(Binding.DisableExtraMods, Diff.ExtraLocal, New List(Of LocalResourceFile)),
                    Sub(summaryText As String, allSucceeded As Boolean)
                        Try
                            Dim NextRev = If(allSucceeded AndAlso Not CapturedCfgFailed, CapturedTargetRev, CapturedPrevRev)
                            UpdateBindingStatus(BindingId, NextRev, "", $"r{CapturedTargetRev} {summaryText}{CapturedCfgNote}")
                        Finally
                            ActiveInstanceSyncs.TryRemove(SyncKey, Nothing)
                        End Try
                    End Sub)
                If Not IsAutoStartup Then
                    Hint($"正在自动同步实例「{TargetInst.Name}」的差异 Mod…{ConfigSummary}", HintType.Blue)
                Else
                    Hint($"[启动自动同步] 检测到「{Binding.ChannelName}」更新至 r{Snap.Revision}，正在为「{TargetInst.Name}」补齐 Mod…", HintType.Blue)
                End If
            Else
                Dim NextRev = If(Not ConfigFailed, Snap.Revision, Binding.LastRevision)
                UpdateBindingStatus(BindingId, NextRev, "", $"{If(ConfigFailed, "同步未完整", "已最新")} (r{Snap.Revision}，Mod 全部一致{ConfigSummary})")
                If ConfigFailed Then
                    If Not IsAutoStartup Then Hint($"实例「{TargetInst.Name}」Config 同步未成功{ConfigSummary}", HintType.Red)
                ElseIf Not IsAutoStartup OrElse ConfigSummary <> "" Then
                    Hint($"实例「{TargetInst.Name}」已同步至 r{Snap.Revision}{ConfigSummary}", HintType.Green)
                End If
            End If
        Finally
            If Not HandedOffAsync Then ActiveInstanceSyncs.TryRemove(SyncKey, Nothing)
        End Try
    End Sub

    Private Sub UpdateBindingStatus(BindingId As String, Revision As Long, LocalSig As String, StatusText As String)
        SyncLock SyncLockObj
            Dim Cfg = LoadConfigNoLock()
            Dim Target = Cfg.Bindings.FirstOrDefault(Function(b) String.Equals(b.Id, BindingId, StringComparison.OrdinalIgnoreCase))
            If Target Is Nothing Then Return
            Target.LastRevision = Revision
            If Not String.IsNullOrEmpty(LocalSig) Then Target.LastLocalSig = LocalSig
            Target.LastSyncUtc = DateTime.UtcNow
            Target.LastStatus = StatusText
            SaveConfigNoLock()
        End SyncLock
        RaiseEvent SyncStateChanged()
    End Sub

    Public Sub RunStartupAutoSyncAsync()
        If Interlocked.Exchange(StartupRunFlag, 1) = 1 Then Return
        RunInNewThread(
            Sub()
                Try
                    '等待文件夹与实例扫描完成（最多等待 15 秒）
                    Dim WaitCount As Integer = 0
                    While (McFolderListLoader.State = LoadState.Loading OrElse McInstanceListLoader.State = LoadState.Loading) AndAlso WaitCount < 75
                        Thread.Sleep(200)
                        WaitCount += 1
                    End While

                    '先拉取一次最新离线推送
                    Dim D = ModFriends.GetData()
                    If D.Friends.Any() OrElse D.Groups.Any() Then
                        ModFriends.PollOnce()
                    End If

                    Dim Bindings = GetBindings().Where(Function(b) b.AutoSyncOnStartup).ToList()
                    If Not Bindings.Any() Then Return

                    '1. 先检查作为发布源且开启启动检测的绑定：若本地实例有变动，自动推一版
                    For Each Pub In Bindings.Where(Function(b) b.Role = "Publisher")
                        Try
                            PublishBindingNow(Pub.Id, SilentIfUnchanged:=True)
                        Catch exEach As Exception
                            Logger.Warn(exEach, $"启动自动发布实例失败：{Pub.ChannelName}")
                        End Try
                    Next

                    '2. 再检查全部自动订阅方：若有更新修订版则自动执行同步
                    For Each SubB In Bindings.Where(Function(b) b.Role = "Subscriber")
                        Try
                            SyncSubscriberBindingNow(SubB.Id, IsAutoStartup:=True)
                        Catch exEach As Exception
                            Logger.Warn(exEach, $"启动自动同步实例失败：{SubB.ChannelName}")
                        End Try
                    Next
                Catch ex As Exception
                    Logger.Warn(ex, "启动 PCL Linking 自动实例同步流程异常")
                End Try
            End Sub, "PCL Linking Startup Instance Auto Sync")
    End Sub

End Module
