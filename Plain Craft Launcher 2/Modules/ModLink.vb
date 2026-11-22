''' <summary>
''' PCL Linking 联机模块的本地配置、节点池与进程工具。
''' 节点池的实现参考 PCL 社区版（PCL.Core/Link/EasyTier/ETRelay + EasyTierEntity）：
''' 从 EasyTier 官方的公共节点统计接口拉取在线节点，失败时退回内置节点。
''' 与社区版不同的是，PCL Linking 不依赖任何账号体系，也不需要下载器——节点拉不到就静默退回内置列表。
''' </summary>
Public Module ModLink

#Region "配置"

    ''' <summary>
    ''' 随包预置的 EasyTier 版本标记。改变这个数字会让已解压的旧文件失效并重新解压。
    ''' </summary>
    Public Const LinkVersion As Integer = 1

    ''' <summary>
    ''' 随包预置的压缩包名，放在 PCLLK.exe 同级目录。
    ''' </summary>
    Public Const LinkPackageName As String = "EasyTier.zip"

    ''' <summary>
    ''' 核心程序（服务端与客户端都是它）。
    ''' </summary>
    Public Const LinkCoreName As String = "easytier-core.exe"

    ''' <summary>
    ''' 命令行程序，用于查询节点与 Ping。
    ''' </summary>
    Public Const LinkCliName As String = "easytier-cli.exe"

    ''' <summary>
    ''' 房主的固定虚拟 IP。客户端会把它映射到 localhost，玩家因此不用记 IP。
    ''' </summary>
    Public Const LinkServerAddress As String = "10.114.114.114"

    ''' <summary>
    ''' EasyTier 虚网点对点辅助控制/直传端口（Mod 一致性握手与大文件/存档接力直传）。
    ''' </summary>
    Public Const LinkControlPort As Integer = EasyTierSideChannelCore.DefaultSideChannelPort

    ''' <summary>
    ''' 写进邀请码的发现节点编号。
    ''' 两端只需要一致即可；使用节点池时任意非零值都行，但 0 会被判定为“要求自定义节点”。
    ''' </summary>
    Public Const LinkDiscoverID As Integer = 1

    ''' <summary>
    ''' 内置兜底节点。
    ''' 前两个是社区共享节点（来源：github.com/EasyTier/discussions#1848，2026-11 实测可用）；
    ''' 后两个是 EasyTier 官方域名的替代（官方 public.easytier.cn 已于 2026 年下线，域名 NXDOMAIN）。
    ''' 正常情况下会先尝试社区节点池，这里只是兜底；节点失效时可在设置里填自定义节点。
    ''' </summary>
    Public ReadOnly LinkPeers As String() = {
        "tcp://us01.225284.xyz:11010",
        "tcp://et-hk.clickor.click:11010",
        "tcp://public.easytier.top:11010",
        "tcp://public2.easytier.cn:54321"
    }

    ''' <summary>
    ''' 社区节点池接口（EasyTier 官方的公共节点统计服务，社区版用的同一个）。
    ''' </summary>
    Private Const LinkNodeApi As String = "https://uptime.easytier.cn/api/nodes?page=1&per_page=50&is_active=true"

    ''' <summary>
    ''' 一次最多同时连接多少个节点。太多会拖慢握手，太少又容易被单个节点拖死。
    ''' </summary>
    Public Const LinkMaxPeers As Integer = 6

    ''' <summary>
    ''' 联机模块压缩包的版本号（EasyTier 官方 v2.6.4 发布版）。
    ''' </summary>
    Public Const LinkDownloadVersion As String = "2.6.4"

    ''' <summary>
    ''' 联机模块官方下载地址（仅在随包 EasyTier.zip 与本地缓存都不存在时按架构从官方 GitHub Release 拉取）。
    ''' </summary>
    Public Function GetEasyTierDownloadUrls() As List(Of String)
        Return EasyTierNetworkCore.GetEasyTierDownloadUrls()
    End Function

    ''' <summary>
    ''' 返回官方 EasyTier v2.6.4 对应架构 ZIP 的 SHA-256 校验摘要。
    ''' </summary>
    Public Function GetEasyTierExpectedSha256() As String
        Return EasyTierNetworkCore.GetEasyTierExpectedSha256()
    End Function

#End Region

#Region "节点池"

    ''' <summary>
    ''' 从社区节点池获取当前在线的中继节点。任何失败都返回空列表，由调用方回退到内置节点。
    ''' </summary>
    Public Function GetCommunityPeers() As List(Of String)
        Dim Result As New List(Of String)
        If Not Settings.Get(Of Boolean)("LinkPeerPool") Then Return Result
        Try
            Dim Raw As String = NetRequestByClient(LinkNodeApi, Timeout:=6000, RequireJson:=True, MakeLog:=False)
            Result = EasyTierNetworkCore.ParseCommunityNodesJson(Raw)
            Logger.Info($"社区节点池返回 {Result.Count} 个可用节点")
        Catch ex As Exception
            Logger.Warn(ex, "获取社区节点池失败，将使用内置节点")
        End Try
        Return Result
    End Function

    ''' <summary>
    ''' 规范化并校验节点地址：接口返回的可能是裸 host:port，补上 tcp:// 前缀。
    ''' 严格校验协议、主机与端口，拒绝空白、引号及命令参数注入。
    ''' </summary>
    Private Function NormalizePeer(Address As String) As String
        Return EasyTierNetworkCore.NormalizePeerUri(Address)
    End Function

    ''' <summary>
    ''' 计算本次联机要连接的节点列表。
    ''' 规则：填了自定义节点就只用自定义节点（自建节点的场景，语义明确）；
    '''       否则按「社区节点池 → 内置兜底」的顺序取，去重后最多 LinkMaxPeers 个。
    ''' </summary>
    Public Function BuildPeerList(CustomPeers As String) As List(Of String)
        Dim Result As New List(Of String)
        '1. 自定义节点
        If Not String.IsNullOrWhiteSpace(CustomPeers) Then
            For Each Peer In CustomPeers.Replace(vbCr, ",").Replace(vbLf, ",").Split("，,;；".ToCharArray)
                Dim Normalized As String = NormalizePeer(Peer)
                If Normalized IsNot Nothing AndAlso Not Result.Contains(Normalized) Then Result.Add(Normalized)
            Next
            Logger.Info($"使用自定义节点 {Result.Count} 个")
            Return Result
        End If
        '2. 社区节点池
        For Each Peer In GetCommunityPeers()
            If Result.Count >= LinkMaxPeers Then Exit For
            If Not Result.Contains(Peer) Then Result.Add(Peer)
        Next
        '3. 内置兜底
        For Each Peer In LinkPeers
            If Result.Count >= LinkMaxPeers Then Exit For
            If Not Result.Contains(Peer) Then Result.Add(Peer)
        Next
        Logger.Info($"本次联机使用 {Result.Count} 个节点：{Result.Join("，")}")
        Return Result
    End Function

    ''' <summary>
    ''' 对候选联机节点执行并发 TCP 握手测速，按连通性优先、延迟从低到高排序返回。
    ''' </summary>
    Public Function TestPeersLatency(Optional IncludeCommunity As Boolean = True, Optional TimeoutMs As Integer = 1500) As List(Of (Peer As String, LatencyMs As Integer, Reachable As Boolean))
        Dim Candidates As New List(Of String)
        Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim AddTcpCandidate =
        Sub(Norm As String)
            If String.IsNullOrEmpty(Norm) Then Return
            If Norm.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) Then Return
            If Seen.Add(Norm) Then Candidates.Add(Norm)
        End Sub
        Dim CustomPeers As String = Settings.Get(Of String)("LinkCustomPeer")
        If Not String.IsNullOrWhiteSpace(CustomPeers) Then
            For Each Raw In CustomPeers.Replace(vbCr, ",").Replace(vbLf, ",").Split("，,;；".ToCharArray)
                AddTcpCandidate(NormalizePeer(Raw))
            Next
        End If
        For Each Raw In LinkPeers
            AddTcpCandidate(NormalizePeer(Raw))
        Next
        If IncludeCommunity Then
            For Each Raw In GetCommunityPeers()
                If Candidates.Count >= 12 Then Exit For
                AddTcpCandidate(Raw)
            Next
        End If

        Return EasyTierNetworkCore.ProbeTcpPeersConcurrent(Candidates, TimeoutMs)
    End Function

#End Region

#Region "进程工具"

    ''' <summary>
    ''' 启动进程并返回它的标准输出。超时会强制结束进程，并返回已读到的内容。
    ''' </summary>
    Public Function StartProcessAndGetOutput(FileName As String, Arguments As String, TimeoutMs As Integer,
                                            Optional Encoding As Text.Encoding = Nothing,
                                            Optional PrintLog As Boolean = False) As String
        If Encoding Is Nothing Then Encoding = Text.Encoding.UTF8
        Dim Output As New Text.StringBuilder()
        Dim OnData As DataReceivedEventHandler =
        Sub(sender As Object, e As DataReceivedEventArgs)
            If e.Data Is Nothing Then Return
            SyncLock Output
                Output.AppendLine(e.Data)
            End SyncLock
        End Sub
        Using Proc As New Process()
            Proc.StartInfo = New ProcessStartInfo With {
                .FileName = FileName,
                .Arguments = Arguments,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding,
                .StandardErrorEncoding = Encoding
            }
            AddHandler Proc.OutputDataReceived, OnData
            AddHandler Proc.ErrorDataReceived, OnData
            Proc.Start()
            Proc.BeginOutputReadLine()
            Proc.BeginErrorReadLine()
            If Not Proc.WaitForExit(TimeoutMs) Then
                Try
                    Proc.Kill()
                Catch ex As Exception
                    Logger.Warn(ex, $"结束超时进程失败（{FileName}）")
                End Try
                Proc.WaitForExit(1000)
            Else
                Proc.WaitForExit()
            End If
        End Using
        Dim Result As String
        SyncLock Output
            Result = Output.ToString()
        End SyncLock
        If PrintLog AndAlso Result <> "" Then Logger.Info($"进程输出（{FileName}）：{vbCrLf}{Result}")
        Return Result
    End Function

#End Region

#Region "房间历史与收藏夹"

    Public Class LinkRoomBookmark
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property InviteCode As String = ""
        Public Property VerifyCode As String = ""
        Public Property IsFavorite As Boolean = False
        Public Property LastUsedUtc As DateTime = DateTime.UtcNow
        Public Property UseCount As Integer = 1
    End Class

    Private ReadOnly BookmarkLock As New Object()

    Private Function GetBookmarkFilePath() As String
        Return GetPortableDataPath("LinkBookmarks.json")
    End Function

    Public Function LoadRoomBookmarks() As List(Of LinkRoomBookmark)
        SyncLock BookmarkLock
            Try
                Dim JsonPath = GetBookmarkFilePath()
                If IO.File.Exists(JsonPath) Then
                    Dim Raw = IO.File.ReadAllText(JsonPath, Text.Encoding.UTF8)
                    Dim Loaded = Newtonsoft.Json.JsonConvert.DeserializeObject(Of List(Of LinkRoomBookmark))(Raw)
                    If Loaded IsNot Nothing Then
                        Return Loaded.Where(Function(b) b IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(b.InviteCode)) _
                                     .OrderByDescending(Function(b) b.IsFavorite) _
                                     .ThenByDescending(Function(b) b.LastUsedUtc) _
                                     .ToList()
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, "读取联机房间收藏/历史记录失败")
            End Try
        End SyncLock
        Return New List(Of LinkRoomBookmark)
    End Function

    Private Sub SaveRoomBookmarksNoLock(Bookmarks As List(Of LinkRoomBookmark))
        Try
            Dim JsonPath = GetBookmarkFilePath()
            Dim ParentDir = IO.Path.GetDirectoryName(JsonPath)
            If Not String.IsNullOrEmpty(ParentDir) AndAlso Not IO.Directory.Exists(ParentDir) Then
                IO.Directory.CreateDirectory(ParentDir)
            End If
            Dim Json = Newtonsoft.Json.JsonConvert.SerializeObject(Bookmarks, Newtonsoft.Json.Formatting.Indented)
            Dim TmpPath = JsonPath & ".tmp"
            IO.File.WriteAllText(TmpPath, Json, New Text.UTF8Encoding(False))
            If IO.File.Exists(JsonPath) Then
                IO.File.Replace(TmpPath, JsonPath, Nothing, True)
            Else
                IO.File.Move(TmpPath, JsonPath)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "保存联机房间收藏/历史记录失败")
        End Try
    End Sub

    Public Function RecordJoinedRoomBookmark(InviteCode As String, VerifyCode As String, Optional CustomName As String = Nothing) As LinkRoomBookmark
        Dim CleanCode = If(InviteCode, "").Trim()
        If String.IsNullOrWhiteSpace(CleanCode) Then Return Nothing
        Dim CleanVerify = If(VerifyCode, "").Trim()
        SyncLock BookmarkLock
            Dim All = New List(Of LinkRoomBookmark)
            Try
                Dim JsonPath = GetBookmarkFilePath()
                If IO.File.Exists(JsonPath) Then
                    Dim Loaded = Newtonsoft.Json.JsonConvert.DeserializeObject(Of List(Of LinkRoomBookmark))(IO.File.ReadAllText(JsonPath, Text.Encoding.UTF8))
                    If Loaded IsNot Nothing Then All = Loaded.Where(Function(b) b IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(b.InviteCode)).ToList()
                End If
            Catch
            End Try

            Dim Existing = All.FirstOrDefault(Function(b) String.Equals(b.InviteCode, CleanCode, StringComparison.OrdinalIgnoreCase))
            If Existing Is Nothing Then
                Dim ShortTag = If(CleanCode.Length > 11, CleanCode.Substring(0, 11), CleanCode)
                Existing = New LinkRoomBookmark With {
                    .Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                    .Name = If(String.IsNullOrWhiteSpace(CustomName), $"房间 {ShortTag}", CustomName.Trim()),
                    .InviteCode = CleanCode,
                    .VerifyCode = CleanVerify,
                    .IsFavorite = False,
                    .LastUsedUtc = DateTime.UtcNow,
                    .UseCount = 1
                }
                All.Add(Existing)
            Else
                Existing.VerifyCode = CleanVerify
                Existing.LastUsedUtc = DateTime.UtcNow
                Existing.UseCount += 1
                If Not String.IsNullOrWhiteSpace(CustomName) Then Existing.Name = CustomName.Trim()
            End If

            '保留全部收藏项 + 最近 20 条普通历史记录
            Dim Favs = All.Where(Function(b) b.IsFavorite).OrderByDescending(Function(b) b.LastUsedUtc).ToList()
            Dim Recents = All.Where(Function(b) Not b.IsFavorite).OrderByDescending(Function(b) b.LastUsedUtc).Take(20).ToList()
            Dim Pruned = Favs.Concat(Recents).ToList()
            SaveRoomBookmarksNoLock(Pruned)
            Return Existing
        End SyncLock
    End Function

    Public Function ToggleFavoriteBookmark(Id As String, Optional NewName As String = Nothing) As Boolean
        If String.IsNullOrWhiteSpace(Id) Then Return False
        SyncLock BookmarkLock
            Dim All = LoadRoomBookmarksInternalNoLock()
            Dim Target = All.FirstOrDefault(Function(b) String.Equals(b.Id, Id, StringComparison.OrdinalIgnoreCase))
            If Target Is Nothing Then Return False
            Target.IsFavorite = Not Target.IsFavorite
            If Not String.IsNullOrWhiteSpace(NewName) Then Target.Name = NewName.Trim()
            SaveRoomBookmarksNoLock(All)
            Return True
        End SyncLock
    End Function

    Public Function RenameBookmark(Id As String, NewName As String) As Boolean
        If String.IsNullOrWhiteSpace(Id) OrElse String.IsNullOrWhiteSpace(NewName) Then Return False
        SyncLock BookmarkLock
            Dim All = LoadRoomBookmarksInternalNoLock()
            Dim Target = All.FirstOrDefault(Function(b) String.Equals(b.Id, Id, StringComparison.OrdinalIgnoreCase))
            If Target Is Nothing Then Return False
            Target.Name = NewName.Trim()
            SaveRoomBookmarksNoLock(All)
            Return True
        End SyncLock
    End Function

    Public Function DeleteBookmark(Id As String) As Boolean
        If String.IsNullOrWhiteSpace(Id) Then Return False
        SyncLock BookmarkLock
            Dim All = LoadRoomBookmarksInternalNoLock()
            Dim Removed = All.RemoveAll(Function(b) String.Equals(b.Id, Id, StringComparison.OrdinalIgnoreCase))
            If Removed > 0 Then
                SaveRoomBookmarksNoLock(All)
                Return True
            End If
        End SyncLock
        Return False
    End Function

    Private Function LoadRoomBookmarksInternalNoLock() As List(Of LinkRoomBookmark)
        Try
            Dim JsonPath = GetBookmarkFilePath()
            If IO.File.Exists(JsonPath) Then
                Dim Loaded = Newtonsoft.Json.JsonConvert.DeserializeObject(Of List(Of LinkRoomBookmark))(IO.File.ReadAllText(JsonPath, Text.Encoding.UTF8))
                If Loaded IsNot Nothing Then
                    Return Loaded.Where(Function(b) b IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(b.InviteCode)).ToList()
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "内部读取房间书签失败")
        End Try
        Return New List(Of LinkRoomBookmark)
    End Function

#End Region

#Region "本机局域网存档扫描与扩展端口解析"

    Public Class LanWorldInfo
        Public Property Motd As String = ""
        Public Property Port As Integer
        Public Property SourceLabel As String = "MC 局域网"
    End Class

    Public Function ParseExtraPorts(Raw As String, MainServerPort As Integer) As List(Of Integer)
        Dim Result As New List(Of Integer)
        If String.IsNullOrWhiteSpace(Raw) Then Return Result
        For Each Token In Raw.Split({","c, ";"c, " "c, ChrW(13), ChrW(10), "|"c}, StringSplitOptions.RemoveEmptyEntries)
            Dim PortNum As Integer
            If Integer.TryParse(Token.Trim(), PortNum) AndAlso PortNum >= 1 AndAlso PortNum <= 65535 Then
                If PortNum <> MainServerPort AndAlso Not Result.Contains(PortNum) Then
                    Result.Add(PortNum)
                    If Result.Count >= 8 Then Exit For
                End If
            End If
        Next
        Return Result
    End Function

    Public Function ScanLocalLanWorlds(Optional TimeoutMs As Integer = 2200) As List(Of LanWorldInfo)
        Dim LocalServerPortHint As Integer = 0
        Try
            Dim ServerDir = PageToolsServer.LoadSavedServerDir()
            If Not String.IsNullOrWhiteSpace(ServerDir) Then
                Dim PropsPath = IO.Path.Combine(ServerDir, "server.properties")
                LocalServerPortHint = 25565
                If IO.File.Exists(PropsPath) Then
                    For Each Line In IO.File.ReadAllLines(PropsPath, Text.Encoding.UTF8)
                        If Line.StartsWith("server-port=", StringComparison.OrdinalIgnoreCase) Then
                            Dim ParsedSrvPort As Integer
                            If Integer.TryParse(Line.Substring("server-port=".Length).Trim(), ParsedSrvPort) AndAlso ParsedSrvPort >= 1 AndAlso ParsedSrvPort <= 65535 Then
                                LocalServerPortHint = ParsedSrvPort
                            End If
                            Exit For
                        End If
                    Next
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取本地开服端口配置失败")
        End Try

        Return EasyTierNetworkCore.ScanLocalLanBroadcasts(LocalServerPortHint, TimeoutMs).
            Select(Function(x) New LanWorldInfo With {
                .Port = x.Port,
                .Motd = x.Motd,
                .SourceLabel = x.SourceLabel
            }).ToList()
    End Function

#End Region

End Module
