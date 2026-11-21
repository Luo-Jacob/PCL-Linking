Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Newtonsoft.Json.Linq

''' <summary>
''' PCL Linking 好友与私信模块。
''' 传输借用公共消息中继（默认 ntfy.sh），不自建服务器；内容端到端加密并逐条鉴权：
'''   - 身份：10 位好友 ID + 20 位随机密码（也可自设 8 位以上），存在 PCL Linking\friends.json。
'''   - 会话密钥：由双方的 ID 与密码按字典序拼接后做 SHA-256，双方各自独立算出同一个值。
'''   - 每条消息：AES-256-CBC 加密载荷 + HMAC-SHA256 校验（覆盖 from/to/时间/随机数/密文）。
'''     接收方校验不通过就直接丢弃，等价于「每次发消息都要核对好友 ID 和密码」。
'''   - 主题名由会话密钥派生，除了知道双方 ID 和密码的人，没人能订阅或伪造。
''' 公共中继只看到密文与主题名；离线消息由中继缓存 12 小时，本地再去重。
''' </summary>
Public Module ModFriends

#Region "数据模型"

    Public Class ModSyncEntry
        Public Property P As Integer = CInt(ResourcePlatforms.Modrinth)
        Public Property I As String = ""
        Public Property V As String = ""
        Public Property H As String = ""
        Public Property N As String = ""
        Public Property F As String = ""
    End Class

    Public Class ModSyncManifest
        Public Property Ver As String = ""
        Public Property Ldr As Integer = CInt(ModLoaders.None)
        Public Property Items As New List(Of ModSyncEntry)
    End Class

    Public Class FriendEntry
        Public Property Id As String = ""
        ''' <summary>好友的密码，由好友私下告知，用于派生会话密钥。</summary>
        Public Property Password As String = ""
        Public Property Remark As String = ""
        Public Property VerifiedUtc As Long = 0

        Public ReadOnly Property DisplayName As String
            Get
                Return If(String.IsNullOrWhiteSpace(Remark), Id, Remark & " (" & Id & ")")
            End Get
        End Property
    End Class

    Public Class GroupEntry
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Secret As String = ""
        Public Property CreatedAt As Long = 0

        Public ReadOnly Property DisplayName As String
            Get
                Return If(String.IsNullOrWhiteSpace(Name), Id, Name)
            End Get
        End Property
    End Class

    Public Class FriendMessage
        Public Property Id As String = ""
        Public Property PeerId As String = ""
        Public Property FromMe As Boolean = False
        Public Property Time As Long = 0
        ''' <summary>text、invite、modsync、file、group_invite、inst_sync、ping、pong。</summary>
        Public Property Kind As String = "text"
        Public Property Text As String = ""
        Public Property InviteCode As String = ""
        Public Property InviteVerify As String = ""
        Public Property ModSyncData As String = ""

        Public Property IsGroup As Boolean = False
        Public Property SenderId As String = ""
        Public Property SenderName As String = ""

        Public Property FileName As String = ""
        Public Property FileSize As Long = 0
        Public Property FileUrl As String = ""
        Public Property FileKey As String = ""
        Public Property FileSha256 As String = ""

        Public Property GroupInviteId As String = ""
        Public Property GroupInviteName As String = ""
        Public Property GroupInviteSecret As String = ""

        Public Property SyncChannel As String = ""
        Public Property SyncRev As Long = 0
        Public Property ConfigUrl As String = ""
        Public Property ConfigKey As String = ""
        Public Property ConfigSha256 As String = ""

        Public Property SendFailed As Boolean = False
        Public Property SendError As String = ""

        Public ReadOnly Property TimeText As String
            Get
                Try
                    Return DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime().ToString("MM-dd HH:mm")
                Catch
                    Return ""
                End Try
            End Get
        End Property
    End Class

    Public Class FriendData
        Public Property MyId As String = ""
        Public Property MyPassword As String = ""
        Public Property MyNickname As String = ""
        Public Property Friends As New List(Of FriendEntry)
        Public Property Messages As New List(Of FriendMessage)
        Public Property Groups As New List(Of GroupEntry)
        Public Property GroupMessages As New List(Of FriendMessage)
        Public Property Seen As New List(Of String)
        ''' <summary>删除好友后拉黑的 ID：不再订阅其主题，且即使收到也直接丢弃。</summary>
        Public Property Blocked As New List(Of String)
        Public Property Since As Long = 0
        Public Property RelayUrl As String = "https://ntfy.sh"
        Public Property PollSeconds As Integer = 6
        Public Property UnreadCounts As New Dictionary(Of String, Integer)
        Public Property GroupUnreadCounts As New Dictionary(Of String, Integer)
    End Class

    Public Const MaxEncryptedFileBytes As Long = 10L * 1024L * 1024L
    Private Const MaxManifestJsonBytes As Integer = 2 * 1024 * 1024
    Private Data As FriendData = Nothing
    Private ReadOnly DataLock As New Object
    Private ReadOnly PollLock As New Object
    Private ReadOnly PollExecLock As New Object
    Private ReadOnly PollWakeSignal As New ManualResetEventSlim(False)
    Private ReadOnly PendingCatchupTopics As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private LastImmediatePollTicks As Long = 0
    Private ReadOnly Property DataPath As String
        Get
            Return GetPortableDataPath("friends.json")
        End Get
    End Property

    Public Event MessagesChanged()
    Public Event FriendMessageReceived(Msg As FriendMessage)
    Public Event InstanceSyncMessageReceived(Msg As FriendMessage)

#End Region

#Region "本地数据"

    ''' <summary>
    ''' 载入本地数据；首次运行会生成 10 位 ID 与 20 位密码。
    ''' </summary>
    Public Function GetData() As FriendData
        SyncLock DataLock
            If Data Is Nothing Then
                Try
                    If IO.File.Exists(DataPath) Then
                        Dim Raw = IO.File.ReadAllText(DataPath, System.Text.Encoding.UTF8).TrimStart(ChrW(&HFEFF))
                        Data = Newtonsoft.Json.JsonConvert.DeserializeObject(Of FriendData)(Raw)
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "读取好友数据失败，将备份损坏文件并使用全新配置")
                    Try
                        If IO.File.Exists(DataPath) Then
                            IO.File.Copy(DataPath, $"{DataPath}.corrupt.{DateTime.Now:yyyyMMddHHmmss}.bak", True)
                        End If
                    Catch
                    End Try
                End Try
                If Data Is Nothing Then Data = New FriendData
                If Data.Friends Is Nothing Then Data.Friends = New List(Of FriendEntry)
                If Data.Messages Is Nothing Then Data.Messages = New List(Of FriendMessage)
                If Data.Groups Is Nothing Then Data.Groups = New List(Of GroupEntry)
                If Data.GroupMessages Is Nothing Then Data.GroupMessages = New List(Of FriendMessage)
                If Data.Seen Is Nothing Then Data.Seen = New List(Of String)
                If Data.Blocked Is Nothing Then Data.Blocked = New List(Of String)
                If Data.UnreadCounts Is Nothing Then Data.UnreadCounts = New Dictionary(Of String, Integer)
                Dim NormGrpUnread As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
                If Data.GroupUnreadCounts IsNot Nothing Then
                    For Each Kv In Data.GroupUnreadCounts
                        If String.IsNullOrWhiteSpace(Kv.Key) Then Continue For
                        Dim KeyNorm = Kv.Key.Trim().ToUpperInvariant()
                        NormGrpUnread(KeyNorm) = If(NormGrpUnread.ContainsKey(KeyNorm), NormGrpUnread(KeyNorm), 0) + Math.Max(0, Kv.Value)
                    Next
                End If
                Data.GroupUnreadCounts = NormGrpUnread
                For Each G In Data.Groups
                    If G IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(G.Id) Then G.Id = G.Id.Trim().ToUpperInvariant()
                Next
                If String.IsNullOrWhiteSpace(Data.RelayUrl) Then Data.RelayUrl = "https://ntfy.sh"
                If Data.PollSeconds < 3 OrElse Data.PollSeconds > 60 Then Data.PollSeconds = 6
                Dim Changed As Boolean = False
                If String.IsNullOrEmpty(Data.MyId) OrElse Data.MyId.Length <> 10 OrElse Not Data.MyId.All(Function(c) Char.IsDigit(c)) Then
                    Data.MyId = RandomDigits(10)
                    Changed = True
                End If
                If String.IsNullOrEmpty(Data.MyPassword) Then
                    Data.MyPassword = RandomPassword(20)
                    Changed = True
                End If
                If Data.Since = 0 Then Data.Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 12 * 3600
                If Changed Then SaveData()
            End If
            Return Data
        End SyncLock
    End Function

    Public Sub SaveData()
        SyncLock DataLock
            Try
                If Data Is Nothing Then Return
                If Data.Seen.Count > 1500 Then Data.Seen = Data.Seen.Skip(Data.Seen.Count - 1500).ToList()
                If Data.Messages.Count > 1200 Then Data.Messages = Data.Messages.Skip(Data.Messages.Count - 1200).ToList()
                If Data.GroupMessages.Count > 1500 Then Data.GroupMessages = Data.GroupMessages.Skip(Data.GroupMessages.Count - 1500).ToList()
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(DataPath))
                Dim TmpPath = DataPath & ".tmp"
                IO.File.WriteAllText(TmpPath, Newtonsoft.Json.JsonConvert.SerializeObject(Data, Newtonsoft.Json.Formatting.Indented), New System.Text.UTF8Encoding(False))
                If IO.File.Exists(DataPath) Then
                    IO.File.Replace(TmpPath, DataPath, Nothing, True)
                Else
                    IO.File.Move(TmpPath, DataPath)
                End If
            Catch ex As Exception
                Logger.Warn(ex, "保存好友数据失败")
            End Try
        End SyncLock
    End Sub

    Public Function GetFriendsSnapshot() As List(Of FriendEntry)
        SyncLock DataLock
            Return GetData().Friends.ToList()
        End SyncLock
    End Function

    Public Function GetMyDisplayName() As String
        Dim D = GetData()
        If Not String.IsNullOrWhiteSpace(D.MyNickname) Then Return D.MyNickname.Trim()
        Try
            Dim Names = Settings.Get(Of String)("LoginLegacyName")
            If Not String.IsNullOrWhiteSpace(Names) Then
                Dim First = Names.Split("¨"c)(0).Trim()
                If Not String.IsNullOrWhiteSpace(First) Then Return First
            End If
        Catch
        End Try
        Return "玩家-" & D.MyId.Substring(Math.Max(0, D.MyId.Length - 4))
    End Function

    Public Sub SetMyNickname(NewName As String)
        SyncLock DataLock
            GetData().MyNickname = If(NewName, "").Trim()
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
    End Sub

    Private Function RandomDigits(Count As Integer) As String
        Dim Sb As New System.Text.StringBuilder(Count)
        For i = 1 To Count
            Sb.Append(CryptoRandomInt(10))
        Next
        Return Sb.ToString()
    End Function

    Private Function RandomPassword(Count As Integer) As String
        Const Alphabet As String = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
        Dim Sb As New System.Text.StringBuilder(Count)
        For i = 1 To Count
            Sb.Append(Alphabet(CryptoRandomInt(Alphabet.Length)))
        Next
        Return Sb.ToString()
    End Function

    ''' <summary>自设密码，至少 8 位。</summary>
    Public Function SetMyPassword(NewPassword As String) As String
        If String.IsNullOrWhiteSpace(NewPassword) OrElse NewPassword.Trim.Length < 8 Then Return "密码至少需要 8 位！"
        SyncLock DataLock
            GetData().MyPassword = NewPassword.Trim
        End SyncLock
        SaveData()
        Return Nothing
    End Function

    ''' <summary>重新生成一个 20 位密码。</summary>
    Public Sub RegenerateMyPassword()
        SyncLock DataLock
            GetData().MyPassword = RandomPassword(20)
        End SyncLock
        SaveData()
    End Sub

    Public Function AddFriend(Id As String, Password As String, Remark As String) As String
        Id = If(Id, "").Trim
        Password = If(Password, "").Trim
        Remark = If(Remark, "").Trim
        If Id.Length <> 10 OrElse Not Id.All(Function(c) Char.IsDigit(c)) Then Return "好友 ID 必须是 10 位数字！"
        If Password.Length < 8 Then Return "好友密码至少需要 8 位！"
        Dim D = GetData()
        SyncLock DataLock
            If Id = D.MyId Then Return "不能添加自己为好友！"
            Dim Existing = D.Friends.FirstOrDefault(Function(f) f.Id = Id)
            If Existing Is Nothing Then
                D.Friends.Add(New FriendEntry With {.Id = Id, .Password = Password, .Remark = Remark})
            Else
                If Not String.Equals(Existing.Password, Password, StringComparison.Ordinal) Then
                    Existing.Password = Password
                    Existing.VerifiedUtc = 0
                End If
                If Not String.IsNullOrWhiteSpace(Remark) Then Existing.Remark = Remark
            End If
            '重新添加即视为解除屏蔽并加入 12 小时补拉列表
            D.Blocked.RemoveAll(Function(x) x = Id)
            PendingCatchupTopics.Add(Topic(Id))
        End SyncLock
        SaveData()
        TriggerImmediatePoll()
        Return Nothing
    End Function

    ''' <summary>
    ''' 从列表删除好友：解除订阅并移除该好友。
    ''' 若 AlsoBlock 为 True（默认），还会清空聊天记录并把 ID 写进黑名单，之后不接受它的任何消息；
    ''' 若为 False，则保留聊天记录、不拉黑，之后重新添加好友即可继续聊天。
    ''' </summary>
    Public Sub RemoveFriend(Id As String, Optional AlsoBlock As Boolean = True)
        Dim D = GetData()
        SyncLock DataLock
            D.Friends.RemoveAll(Function(f) f.Id = Id)
            D.UnreadCounts.Remove(Id)
            If AlsoBlock Then
                D.Messages.RemoveAll(Function(m) m.PeerId = Id)
                If Not D.Blocked.Contains(Id) Then D.Blocked.Add(Id)
            End If
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
    End Sub

    Public Function BlockedIds() As List(Of String)
        SyncLock DataLock
            Return GetData().Blocked.ToList()
        End SyncLock
    End Function

    ''' <summary>解除屏蔽（之后重新添加好友即可继续聊天）。</summary>
    Public Sub Unblock(Id As String)
        Dim D = GetData()
        SyncLock DataLock
            D.Blocked.RemoveAll(Function(x) x = Id)
        End SyncLock
        SaveData()
    End Sub

    Public Function FindFriend(Id As String) As FriendEntry
        SyncLock DataLock
            Return GetData().Friends.FirstOrDefault(Function(f) f.Id = Id)
        End SyncLock
    End Function

    Public Sub UpdateRemark(Id As String, NewRemark As String)
        Dim D = GetData()
        SyncLock DataLock
            Dim F = D.Friends.FirstOrDefault(Function(x) x.Id = Id)
            If F Is Nothing Then Return
            F.Remark = If(NewRemark, "").Trim
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
    End Sub

    ''' <summary>尝试从粘贴的名片文本中解析出 10 位 ID 与密码。</summary>
    Public Function TryParseCard(Input As String, ByRef Id As String, ByRef Password As String) As Boolean
        Id = ""
        Password = ""
        If String.IsNullOrWhiteSpace(Input) Then Return False
        Dim MCard = System.Text.RegularExpressions.Regex.Match(Input, "(\d{10})[^\w\r\n]+密码\s*[:：]?\s*(\S{8,})")
        If MCard.Success Then
            Id = MCard.Groups(1).Value
            Password = MCard.Groups(2).Value.Trim("。"c, ","c, "，"c, ";"c, "；"c)
            Return True
        End If
        Dim MPlain = System.Text.RegularExpressions.Regex.Match(Input.Trim(), "^(\d{10})\s*[:：,\s/|]+\s*(\S{8,})$")
        If MPlain.Success Then
            Id = MPlain.Groups(1).Value
            Password = MPlain.Groups(2).Value
            Return True
        End If
        Return False
    End Function

    Public Function GetMessages(FriendId As String) As List(Of FriendMessage)
        SyncLock DataLock
            Return GetData().Messages.Where(Function(m) m.PeerId = FriendId).OrderBy(Function(m) m.Time).ToList()
        End SyncLock
    End Function

    Public Function GetLastMessage(FriendId As String) As FriendMessage
        SyncLock DataLock
            Return GetData().Messages.Where(Function(m) m.PeerId = FriendId).OrderByDescending(Function(m) m.Time).FirstOrDefault()
        End SyncLock
    End Function

    Public Function GetUnread(FriendId As String) As Integer
        SyncLock DataLock
            Dim Count As Integer = 0
            GetData().UnreadCounts.TryGetValue(FriendId, Count)
            Return Count
        End SyncLock
    End Function

    Public Function TotalUnread() As Integer
        SyncLock DataLock
            Dim D = GetData()
            Dim FriendIds = New HashSet(Of String)(D.Friends.Select(Function(f) f.Id), StringComparer.Ordinal)
            Dim GroupIds = New HashSet(Of String)(D.Groups.Select(Function(g) g.Id), StringComparer.OrdinalIgnoreCase)
            Dim FSum = D.UnreadCounts.Where(Function(kv) FriendIds.Contains(kv.Key)).Sum(Function(kv) kv.Value)
            Dim GSum = D.GroupUnreadCounts.Where(Function(kv) GroupIds.Contains(kv.Key)).Sum(Function(kv) kv.Value)
            Return FSum + GSum
        End SyncLock
    End Function

    Public Function CreateGroup(GroupName As String) As GroupEntry
        Dim CleanName = If(GroupName, "").Trim()
        If String.IsNullOrWhiteSpace(CleanName) Then CleanName = "开黑小队"
        Dim Entry As New GroupEntry With {
            .Id = "G" & RandomDigits(8),
            .Name = CleanName,
            .Secret = RandomPassword(24),
            .CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }
        SyncLock DataLock
            GetData().Groups.Add(Entry)
            PendingCatchupTopics.Add(GroupTopic(Entry.Id))
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
        TriggerImmediatePoll()
        Return Entry
    End Function

    Public Function JoinGroup(GroupId As String, Secret As String, Optional GroupName As String = "") As String
        GroupId = If(GroupId, "").Trim().ToUpperInvariant()
        Secret = If(Secret, "").Trim()
        GroupName = If(GroupName, "").Trim()
        If Not System.Text.RegularExpressions.Regex.IsMatch(GroupId, "^G\d{8}$") Then Return "群号格式不正确（应为 G 开头 + 8 位数字）！"
        If Secret.Length < 8 Then Return "群密钥至少需要 8 位！"
        Dim D = GetData()
        SyncLock DataLock
            Dim Existing = D.Groups.FirstOrDefault(Function(g) String.Equals(g.Id, GroupId, StringComparison.OrdinalIgnoreCase))
            If Existing Is Nothing Then
                D.Groups.Add(New GroupEntry With {
                    .Id = GroupId,
                    .Secret = Secret,
                    .Name = If(String.IsNullOrWhiteSpace(GroupName), $"群聊 {GroupId}", GroupName),
                    .CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                })
            Else
                Existing.Id = GroupId
                Existing.Secret = Secret
                If Not String.IsNullOrWhiteSpace(GroupName) Then Existing.Name = GroupName
            End If
            PendingCatchupTopics.Add(GroupTopic(GroupId))
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
        TriggerImmediatePoll()
        Return Nothing
    End Function

    Public Sub LeaveGroup(GroupId As String)
        Dim NormId = If(GroupId, "").Trim().ToUpperInvariant()
        Dim D = GetData()
        SyncLock DataLock
            D.Groups.RemoveAll(Function(g) String.Equals(g.Id, NormId, StringComparison.OrdinalIgnoreCase))
            D.GroupUnreadCounts.Remove(NormId)
            D.GroupMessages.RemoveAll(Function(m) String.Equals(m.PeerId, NormId, StringComparison.OrdinalIgnoreCase))
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
    End Sub

    Public Sub RenameGroup(GroupId As String, NewName As String)
        Dim NormId = If(GroupId, "").Trim().ToUpperInvariant()
        Dim D = GetData()
        SyncLock DataLock
            Dim G = D.Groups.FirstOrDefault(Function(x) String.Equals(x.Id, NormId, StringComparison.OrdinalIgnoreCase))
            If G Is Nothing OrElse String.IsNullOrWhiteSpace(NewName) Then Return
            G.Name = NewName.Trim()
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
    End Sub

    Public Function FindGroup(GroupId As String) As GroupEntry
        If String.IsNullOrWhiteSpace(GroupId) Then Return Nothing
        Dim NormId = GroupId.Trim().ToUpperInvariant()
        SyncLock DataLock
            Return GetData().Groups.FirstOrDefault(Function(g) String.Equals(g.Id, NormId, StringComparison.OrdinalIgnoreCase))
        End SyncLock
    End Function

    Public Function FormatGroupCard(G As GroupEntry) As String
        If G Is Nothing Then Return ""
        Return $"PCL Linking 群名片：群号 {G.Id}，群名【{G.Name}】，密钥 {G.Secret}"
    End Function

    Public Function TryParseGroupCard(Input As String, ByRef OutId As String, ByRef OutSecret As String, ByRef OutName As String) As Boolean
        OutId = ""
        OutSecret = ""
        OutName = ""
        If String.IsNullOrWhiteSpace(Input) Then Return False
        Dim MId = System.Text.RegularExpressions.Regex.Match(Input, "\b(G\d{8})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        If Not MId.Success Then Return False
        OutId = MId.Groups(1).Value.ToUpperInvariant()

        Dim MName = System.Text.RegularExpressions.Regex.Match(Input, "群名[：:\s【\[]+([^】\]，,\r\n]+)")
        If MName.Success Then OutName = MName.Groups(1).Value.Trim()

        Dim MSec = System.Text.RegularExpressions.Regex.Match(Input, "密钥\s*[:：]?\s*(\S{8,})")
        If MSec.Success Then
            OutSecret = MSec.Groups(1).Value.Trim("。"c, ","c, "，"c, ";"c, "；"c, "】"c, "]"c)
            Return True
        End If
        Dim MPlain = System.Text.RegularExpressions.Regex.Match(Input.Trim(), "^(G\d{8})\s*[:：,\s/|]+\s*(\S{8,})(?:\s*[:：,\s/|]+\s*(.+))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        If MPlain.Success Then
            OutSecret = MPlain.Groups(2).Value.Trim()
            If MPlain.Groups(3).Success AndAlso String.IsNullOrWhiteSpace(OutName) Then OutName = MPlain.Groups(3).Value.Trim()
            Return True
        End If
        Return False
    End Function

    Public Function GetGroupMessages(GroupId As String) As List(Of FriendMessage)
        Dim NormId = If(GroupId, "").Trim().ToUpperInvariant()
        SyncLock DataLock
            Return GetData().GroupMessages.Where(Function(m) String.Equals(m.PeerId, NormId, StringComparison.OrdinalIgnoreCase)).OrderBy(Function(m) m.Time).ToList()
        End SyncLock
    End Function

    Public Function GetGroupLastMessage(GroupId As String) As FriendMessage
        Dim NormId = If(GroupId, "").Trim().ToUpperInvariant()
        SyncLock DataLock
            Return GetData().GroupMessages.Where(Function(m) String.Equals(m.PeerId, NormId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(Function(m) m.Time).FirstOrDefault()
        End SyncLock
    End Function

    Public Function GetGroupUnread(GroupId As String) As Integer
        Dim NormId = If(GroupId, "").Trim().ToUpperInvariant()
        SyncLock DataLock
            Dim Count As Integer = 0
            GetData().GroupUnreadCounts.TryGetValue(NormId, Count)
            Return Count
        End SyncLock
    End Function

    Public Sub MarkGroupRead(GroupId As String)
        If String.IsNullOrWhiteSpace(GroupId) Then Return
        Dim NormId = GroupId.Trim().ToUpperInvariant()
        Dim Changed As Boolean = False
        SyncLock DataLock
            Dim D = GetData()
            If D.GroupUnreadCounts.ContainsKey(NormId) AndAlso D.GroupUnreadCounts(NormId) > 0 Then
                D.GroupUnreadCounts.Remove(NormId)
                Changed = True
            End If
        End SyncLock
        If Changed Then
            SaveData()
            RaiseEvent MessagesChanged()
        End If
    End Sub

    Public Sub ClearGroupHistory(GroupId As String)
        Dim NormId = If(GroupId, "").Trim().ToUpperInvariant()
        Dim D = GetData()
        SyncLock DataLock
            D.GroupMessages.RemoveAll(Function(m) String.Equals(m.PeerId, NormId, StringComparison.OrdinalIgnoreCase))
            D.GroupUnreadCounts.Remove(NormId)
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
    End Sub

    Public Sub MarkRead(FriendId As String)
        If String.IsNullOrEmpty(FriendId) Then Return
        Dim Changed As Boolean = False
        SyncLock DataLock
            Dim D = GetData()
            If D.UnreadCounts.ContainsKey(FriendId) AndAlso D.UnreadCounts(FriendId) > 0 Then
                D.UnreadCounts.Remove(FriendId)
                Changed = True
            End If
        End SyncLock
        If Changed Then
            SaveData()
            RaiseEvent MessagesChanged()
        End If
    End Sub

#End Region

#Region "加密与鉴权（委托至 MitCore.FriendsCryptoProtocol）"

    Private Structure ChannelKeys
        Public TopicName As String
        Public EncKey As Byte()
        Public MacKey As Byte()
    End Structure

    Private Function Sha256Utf8(Text As String) As Byte()
        Return FriendsCryptoProtocol.Sha256Utf8(Text)
    End Function

    Private Function DerivePairKeys(FriendId As String) As ChannelKeys
        Dim D = GetData()
        Dim F = FindFriend(FriendId)
        If F Is Nothing Then Throw New Exception("好友不存在：" & FriendId)
        Dim Keys = FriendsCryptoProtocol.DerivePairKeysCore(D.MyId, D.MyPassword, F.Id, F.Password)
        Return New ChannelKeys With {.TopicName = Keys.TopicName, .EncKey = Keys.EncKey, .MacKey = Keys.MacKey}
    End Function

    Private Function Topic(FriendId As String) As String
        Return DerivePairKeys(FriendId).TopicName
    End Function

    Private Function DeriveGroupKeys(GroupId As String) As ChannelKeys
        Dim G = FindGroup(GroupId)
        If G Is Nothing Then Throw New Exception("群聊不存在：" & GroupId)
        Dim Keys = FriendsCryptoProtocol.DeriveGroupKeysCore(G.Id, G.Secret)
        Return New ChannelKeys With {.TopicName = Keys.TopicName, .EncKey = Keys.EncKey, .MacKey = Keys.MacKey}
    End Function

    Private Function GroupTopic(GroupId As String) As String
        Return DeriveGroupKeys(GroupId).TopicName
    End Function

    Private Function FixedTimeEquals(ExpectedHex As String, ActualHex As String) As Boolean
        Return FriendsCryptoProtocol.FixedTimeEquals(ExpectedHex, ActualHex)
    End Function

    Private Function EncryptBytes(Plain As Byte(), Key As Byte()) As Byte()
        Return FriendsCryptoProtocol.EncryptBytes(Plain, Key)
    End Function

    Private Function DecryptBytes(Raw As Byte(), Key As Byte()) As Byte()
        Return FriendsCryptoProtocol.DecryptBytes(Raw, Key)
    End Function

    Private Function EncryptText(Plain As String, Key As Byte()) As String
        Return FriendsCryptoProtocol.EncryptText(Plain, Key)
    End Function

    Private Function DecryptText(Encoded As String, Key As Byte()) As String
        Return FriendsCryptoProtocol.DecryptText(Encoded, Key)
    End Function

    Private Function MacOf(Text As String, Key As Byte()) As String
        Return FriendsCryptoProtocol.MacOf(Text, Key)
    End Function

#End Region

#Region "收发消息"

    Private Function FriendsUserAgent() As String
        Return "PCL-Linking-Fork/" & VersionBaseName.Replace(" ", "")
    End Function

    Private Function Relay() As String
        SyncLock DataLock
            Return GetData().RelayUrl.Trim().TrimEnd("/"c)
        End SyncLock
    End Function

    Public Function CompressModSyncManifest(Manifest As ModSyncManifest) As String
        If Manifest Is Nothing OrElse Manifest.Items Is Nothing Then Return ""
        Dim Arr As New JArray
        For Each Entry In Manifest.Items
            Arr.Add(New JArray From {Entry.P, If(Entry.I, ""), If(Entry.V, ""), If(Entry.H, ""), If(Entry.N, ""), If(Entry.F, "")})
        Next
        Dim Obj As New JObject From {
            {"v", If(Manifest.Ver, "")},
            {"l", Manifest.Ldr},
            {"a", Arr}
        }
        Dim Raw = Encoding.UTF8.GetBytes(Obj.ToString(Newtonsoft.Json.Formatting.None))
        Using Ms As New MemoryStream()
            Using Gz As New GZipStream(Ms, CompressionLevel.Optimal, True)
                Gz.Write(Raw, 0, Raw.Length)
            End Using
            Return Convert.ToBase64String(Ms.ToArray())
        End Using
    End Function

    Public Function DecompressModSyncManifest(Data As String) As ModSyncManifest
        If String.IsNullOrWhiteSpace(Data) Then Return Nothing
        Try
            Dim Bytes = Convert.FromBase64String(Data.Trim())
            Dim JsonStr As String
            Using MsIn As New MemoryStream(Bytes), Gz As New GZipStream(MsIn, CompressionMode.Decompress), MsOut As New MemoryStream()
                Dim Buf(8191) As Byte
                Dim TotalRead As Integer = 0
                Do
                    Dim n = Gz.Read(Buf, 0, Buf.Length)
                    If n <= 0 Then Exit Do
                    TotalRead += n
                    If TotalRead > MaxManifestJsonBytes Then
                        Throw New InvalidDataException($"Mod 同步清单解压体积超过 {MaxManifestJsonBytes \ (1024 * 1024)} MB 上限！")
                    End If
                    MsOut.Write(Buf, 0, n)
                Loop
                JsonStr = Encoding.UTF8.GetString(MsOut.ToArray())
            End Using
            Dim Obj = JObject.Parse(JsonStr)
            Dim Manifest As New ModSyncManifest With {
                .Ver = If(Obj.Value(Of String)("v"), ""),
                .Ldr = If(Obj("l") IsNot Nothing, Obj.Value(Of Integer)("l"), 0)
            }
            Dim Arr = TryCast(Obj("a"), JArray)
            If Arr IsNot Nothing Then
                For Each Row In Arr.OfType(Of JArray)()
                    If Row.Count < 6 Then Continue For
                    Manifest.Items.Add(New ModSyncEntry With {
                        .P = Row(0).ToObject(Of Integer)(),
                        .I = Row(1).ToString(),
                        .V = Row(2).ToString(),
                        .H = Row(3).ToString(),
                        .N = Row(4).ToString(),
                        .F = Row(5).ToString()
                    })
                Next
            End If
            Return Manifest
        Catch ex As Exception
            Logger.Warn(ex, "解析好友 Mod 同步清单失败")
            Return Nothing
        End Try
    End Function

    Public Function BuildInstanceModSyncManifest(Instance As McInstance) As ModSyncManifest
        If Instance Is Nothing Then Return Nothing
        If Not Instance.IsLoaded Then Instance.Load()
        Dim Scan = ScanInstanceModsWithCache(Instance)
        Dim Enabled = Scan.ModList.Where(Function(m) m.IsEnabled).ToList()
        If Enabled.Any() AndAlso Enabled.Any(Function(m) Not m.OnlineDataLoaded) Then
            Try
                Dim OnlineLoader As New LoaderTask(Of (ModList As List(Of LocalResourceFile), Cache As JObject, Target As McVersion), Integer)(
                    "生成好友同步 Mod 清单", AddressOf LocalResourceOnlineLoad)
                Using Cts As New CancellationTokenSource(20000)
                    OnlineLoader.WaitForExit(Input:=(Enabled, Scan.Cache, Instance.Version), c:=Cts.Token)
                End Using
            Catch ex As Exception
                Logger.Warn(ex, "联机 Mod 清单联网补全失败，将回退本地信息")
            End Try
        End If
        Dim Manifest As New ModSyncManifest With {
            .Ver = Instance.Version.VanillaName,
            .Ldr = CInt(Instance.Version.ModLoaders)
        }
        For Each M In Enabled
            Manifest.Items.Add(New ModSyncEntry With {
                .P = If(M.Project IsNot Nothing, CInt(M.Project.Platform), CInt(ResourcePlatforms.Modrinth)),
                .I = If(M.Project?.Id, ""),
                .V = If(M.ProjectVersion?.Id, ""),
                .H = If(M.ModrinthHash, ""),
                .N = If(M.Project?.TranslatedName, M.Display),
                .F = M.File.Name
            })
        Next
        Return Manifest
    End Function

    Private Function ComputeSha256Hex(Bytes As Byte()) As String
        Return FriendsCryptoProtocol.ComputeSha256Hex(Bytes)
    End Function

    Public Function UploadEncryptedBlob(RawBytes As Byte(), ByRef OutUrl As String, ByRef OutKeyB64 As String, ByRef OutSha256Hex As String) As String
        Try
            Return FriendsCryptoProtocol.UploadEncryptedBlobCore(Relay(), FriendsUserAgent(), MaxEncryptedFileBytes, RawBytes, OutUrl, OutKeyB64, OutSha256Hex)
        Catch ex As Exception
            Logger.Warn(ex, "上传加密附件失败")
            Return "上传加密文件失败：" & ex.Message
        End Try
    End Function

    Public Function DownloadAndDecryptBlob(FileUrl As String, KeyB64 As String, ExpectedSha256Hex As String, ByRef OutBytes As Byte()) As String
        Try
            Return FriendsCryptoProtocol.DownloadAndDecryptBlobCore(Relay(), FriendsUserAgent(), MaxEncryptedFileBytes, FileUrl, KeyB64, ExpectedSha256Hex, OutBytes)
        Catch ex As Exception
            Logger.Warn(ex, "下载并解密附件失败")
            Return "下载或解密失败：" & ex.Message
        End Try
    End Function

    Private Function BuildPayloadJson(Msg As FriendMessage) As JObject
        Dim Payload As New JObject()
        Payload.Add("k", New JValue(Msg.Kind))
        Payload.Add("t", New JValue(If(Msg.Text, "")))
        Payload.Add("c", New JValue(If(Msg.InviteCode, "")))
        Payload.Add("p", New JValue(If(Msg.InviteVerify, "")))
        If Not String.IsNullOrEmpty(Msg.ModSyncData) Then Payload.Add("m", New JValue(Msg.ModSyncData))
        If Not String.IsNullOrEmpty(Msg.SenderName) Then Payload.Add("sn", New JValue(Msg.SenderName))
        If Not String.IsNullOrEmpty(Msg.FileName) Then Payload.Add("fn", New JValue(Msg.FileName))
        If Msg.FileSize > 0 Then Payload.Add("fs", New JValue(Msg.FileSize))
        If Not String.IsNullOrEmpty(Msg.FileUrl) Then Payload.Add("fu", New JValue(Msg.FileUrl))
        If Not String.IsNullOrEmpty(Msg.FileKey) Then Payload.Add("fk", New JValue(Msg.FileKey))
        If Not String.IsNullOrEmpty(Msg.FileSha256) Then Payload.Add("fh", New JValue(Msg.FileSha256))
        If Not String.IsNullOrEmpty(Msg.GroupInviteId) Then Payload.Add("gi", New JValue(Msg.GroupInviteId))
        If Not String.IsNullOrEmpty(Msg.GroupInviteName) Then Payload.Add("gn", New JValue(Msg.GroupInviteName))
        If Not String.IsNullOrEmpty(Msg.GroupInviteSecret) Then Payload.Add("gk", New JValue(Msg.GroupInviteSecret))
        If Not String.IsNullOrEmpty(Msg.SyncChannel) Then Payload.Add("sc", New JValue(Msg.SyncChannel))
        If Msg.SyncRev > 0 Then Payload.Add("sr", New JValue(Msg.SyncRev))
        If Not String.IsNullOrEmpty(Msg.ConfigUrl) Then Payload.Add("cu", New JValue(Msg.ConfigUrl))
        If Not String.IsNullOrEmpty(Msg.ConfigKey) Then Payload.Add("ck", New JValue(Msg.ConfigKey))
        If Not String.IsNullOrEmpty(Msg.ConfigSha256) Then Payload.Add("ch", New JValue(Msg.ConfigSha256))
        Return Payload
    End Function

    Private Sub PopulateMessageFromPayload(Msg As FriendMessage, Payload As JObject)
        Msg.Kind = If(Payload.Value(Of String)("k"), "text")
        Msg.Text = If(Payload.Value(Of String)("t"), "")
        Msg.InviteCode = If(Payload.Value(Of String)("c"), "")
        Msg.InviteVerify = If(Payload.Value(Of String)("p"), "")
        Msg.ModSyncData = If(Payload.Value(Of String)("m"), "")
        Msg.SenderName = If(Payload.Value(Of String)("sn"), "")
        Msg.FileName = If(Payload.Value(Of String)("fn"), "")
        Msg.FileSize = If(Payload("fs") IsNot Nothing, Payload.Value(Of Long)("fs"), 0L)
        Msg.FileUrl = If(Payload.Value(Of String)("fu"), "")
        Msg.FileKey = If(Payload.Value(Of String)("fk"), "")
        Msg.FileSha256 = If(Payload.Value(Of String)("fh"), "")
        Msg.GroupInviteId = If(Payload.Value(Of String)("gi"), "")
        Msg.GroupInviteName = If(Payload.Value(Of String)("gn"), "")
        Msg.GroupInviteSecret = If(Payload.Value(Of String)("gk"), "")
        Msg.SyncChannel = If(Payload.Value(Of String)("sc"), "")
        Msg.SyncRev = If(Payload("sr") IsNot Nothing, Payload.Value(Of Long)("sr"), 0L)
        Msg.ConfigUrl = If(Payload.Value(Of String)("cu"), "")
        Msg.ConfigKey = If(Payload.Value(Of String)("ck"), "")
        Msg.ConfigSha256 = If(Payload.Value(Of String)("ch"), "")
    End Sub

    Private Function TransmitMessageCore(TargetId As String, IsGroup As Boolean, TemplateMsg As FriendMessage,
                                         Optional RecordLocally As Boolean = True, Optional ExistingLocalId As String = Nothing) As String
        Dim NormTarget = If(TargetId, "").Trim()
        If IsGroup Then NormTarget = NormTarget.ToUpperInvariant()
        Dim D = GetData()
        Dim ChanKeys As ChannelKeys
        If IsGroup Then
            Dim G = FindGroup(NormTarget)
            If G Is Nothing Then Return "群聊不存在！"
            ChanKeys = DeriveGroupKeys(NormTarget)
        Else
            Dim F = FindFriend(NormTarget)
            If F Is Nothing Then Return "好友不存在！"
            ChanKeys = DerivePairKeys(NormTarget)
        End If
        If If(TemplateMsg.Text, "").Length > 2000 Then Return "单条消息最多 2000 字！"

        Dim Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        Dim Nonce = RandomPassword(12)
        Dim PayloadJson As String
        SyncLock DataLock
            TemplateMsg.IsGroup = IsGroup
            TemplateMsg.PeerId = NormTarget
            TemplateMsg.FromMe = True
            TemplateMsg.SenderId = D.MyId
            If String.IsNullOrWhiteSpace(TemplateMsg.SenderName) Then TemplateMsg.SenderName = GetMyDisplayName()
            TemplateMsg.Time = Ts
            PayloadJson = BuildPayloadJson(TemplateMsg).ToString(Newtonsoft.Json.Formatting.None)
        End SyncLock

        Try
            Dim Cipher = EncryptText(PayloadJson, ChanKeys.EncKey)
            Dim Envelope As New JObject()
            If IsGroup Then
                Envelope.Add("v", New JValue(2))
                Envelope.Add("grp", New JValue(NormTarget))
                Envelope.Add("from", New JValue(D.MyId))
                Envelope.Add("ts", New JValue(Ts))
                Envelope.Add("nonce", New JValue(Nonce))
                Envelope.Add("data", New JValue(Cipher))
                Envelope.Add("mac", New JValue(MacOf($"2|{NormTarget}|{D.MyId}|{Ts}|{Nonce}|{Cipher}", ChanKeys.MacKey)))
            Else
                Envelope.Add("v", New JValue(1))
                Envelope.Add("from", New JValue(D.MyId))
                Envelope.Add("to", New JValue(NormTarget))
                Envelope.Add("ts", New JValue(Ts))
                Envelope.Add("nonce", New JValue(Nonce))
                Envelope.Add("data", New JValue(Cipher))
                Envelope.Add("mac", New JValue(MacOf($"1|{D.MyId}|{NormTarget}|{Ts}|{Nonce}|{Cipher}", ChanKeys.MacKey)))
            End If
            Dim Url = Relay() & "/" & ChanKeys.TopicName
            NetRequestByClient(Url, HttpMethod.Post, Content:=Envelope.ToString(Newtonsoft.Json.Formatting.None),
                               ContentType:="text/plain; charset=utf-8", Timeout:=12000, MakeLog:=False)
            SyncLock DataLock
                TemplateMsg.SendFailed = False
                TemplateMsg.SendError = ""
                If RecordLocally AndAlso String.IsNullOrEmpty(ExistingLocalId) Then
                    TemplateMsg.Id = "local-" & Nonce
                    If IsGroup Then D.GroupMessages.Add(TemplateMsg) Else D.Messages.Add(TemplateMsg)
                End If
            End SyncLock
            If RecordLocally Then
                SaveData()
                RaiseEvent MessagesChanged()
            End If
            Return Nothing
        Catch ex As Exception
            Logger.Warn(ex, "发送消息失败")
            Dim ErrText = "发送失败：" & ex.Message
            SyncLock DataLock
                TemplateMsg.SendFailed = True
                TemplateMsg.SendError = ex.Message
                If RecordLocally AndAlso String.IsNullOrEmpty(ExistingLocalId) Then
                    TemplateMsg.Id = "local-" & Nonce
                    If IsGroup Then D.GroupMessages.Add(TemplateMsg) Else D.Messages.Add(TemplateMsg)
                End If
            End SyncLock
            If RecordLocally Then
                SaveData()
                RaiseEvent MessagesChanged()
            End If
            Return ErrText
        End Try
    End Function

    ''' <summary>发送一条私信、联机邀请或 Mod 同步清单。返回错误信息，成功返回 Nothing。</summary>
    Public Function Send(FriendId As String, Kind As String, Text As String,
                         Optional InviteCode As String = "", Optional InviteVerify As String = "",
                         Optional ModSyncData As String = "") As String
        Dim Msg As New FriendMessage With {
            .Kind = Kind,
            .Text = If(Text, ""),
            .InviteCode = If(InviteCode, ""),
            .InviteVerify = If(InviteVerify, ""),
            .ModSyncData = If(ModSyncData, "")
        }
        Return TransmitMessageCore(FriendId, False, Msg, RecordLocally:=True)
    End Function

    Public Function SendGroup(GroupId As String, Kind As String, Text As String,
                              Optional InviteCode As String = "", Optional InviteVerify As String = "",
                              Optional ModSyncData As String = "") As String
        Dim Msg As New FriendMessage With {
            .Kind = Kind,
            .Text = If(Text, ""),
            .InviteCode = If(InviteCode, ""),
            .InviteVerify = If(InviteVerify, ""),
            .ModSyncData = If(ModSyncData, "")
        }
        Return TransmitMessageCore(GroupId, True, Msg, RecordLocally:=True)
    End Function

    Public Function SendRichMessage(TargetId As String, IsGroup As Boolean, Msg As FriendMessage) As String
        If Msg Is Nothing Then Return "消息对象为空"
        Return TransmitMessageCore(TargetId, IsGroup, Msg, RecordLocally:=True)
    End Function

    Public Function RetryMessage(MsgId As String, IsGroup As Boolean) As String
        If String.IsNullOrWhiteSpace(MsgId) Then Return "消息 ID 无效"
        Dim TargetMsg As FriendMessage = Nothing
        SyncLock DataLock
            Dim D = GetData()
            Dim SourceList = If(IsGroup, D.GroupMessages, D.Messages)
            TargetMsg = SourceList.FirstOrDefault(Function(m) String.Equals(m.Id, MsgId, StringComparison.OrdinalIgnoreCase))
        End SyncLock
        If TargetMsg Is Nothing Then Return "未找到该失败消息！"
        Return TransmitMessageCore(TargetMsg.PeerId, TargetMsg.IsGroup, TargetMsg, RecordLocally:=True, ExistingLocalId:=TargetMsg.Id)
    End Function

    Public Sub SendHandshakePing(FriendId As String)
        If String.IsNullOrWhiteSpace(FriendId) OrElse FindFriend(FriendId) Is Nothing Then Return
        Try
            TransmitMessageCore(FriendId, False, New FriendMessage With {.Kind = "ping", .Text = "handshake"}, RecordLocally:=False)
        Catch
        End Try
    End Sub

    Private Class PollTopicItem
        Public IsGroup As Boolean
        Public Id As String
        Public DefaultLabel As String
        Public VerifiedUtc As Long
        Public Keys As ChannelKeys
    End Class

    ''' <summary>拉取一次所有好友与群聊的新消息（单次 HTTP 请求订阅全部主题）。</summary>
    Public Sub PollOnce()
        SyncLock PollExecLock
            Dim D = GetData()
            Dim TopicMap As New Dictionary(Of String, PollTopicItem)(StringComparer.OrdinalIgnoreCase)
            Dim AllTopics As New List(Of String)
            Dim CatchupTopics As New List(Of String)
            Dim MyIdSnap As String
            Dim SinceSnap As Long
            SyncLock DataLock
                If D.Friends.Count = 0 AndAlso D.Groups.Count = 0 Then Return
                For Each F In D.Friends
                    Try
                        Dim Keys = DerivePairKeys(F.Id)
                        TopicMap(Keys.TopicName) = New PollTopicItem With {
                            .IsGroup = False,
                            .Id = F.Id,
                            .DefaultLabel = If(String.IsNullOrWhiteSpace(F.Remark), F.Id, F.Remark),
                            .VerifiedUtc = F.VerifiedUtc,
                            .Keys = Keys
                        }
                        AllTopics.Add(Keys.TopicName)
                        If PendingCatchupTopics.Contains(Keys.TopicName) Then CatchupTopics.Add(Keys.TopicName)
                    Catch exTopic As Exception
                        Logger.Warn(exTopic, $"派生好友主题失败：{F.Id}")
                    End Try
                Next
                For Each G In D.Groups
                    Try
                        Dim Keys = DeriveGroupKeys(G.Id)
                        TopicMap(Keys.TopicName) = New PollTopicItem With {
                            .IsGroup = True,
                            .Id = G.Id.Trim().ToUpperInvariant(),
                            .DefaultLabel = G.Name,
                            .Keys = Keys
                        }
                        AllTopics.Add(Keys.TopicName)
                        If PendingCatchupTopics.Contains(Keys.TopicName) Then CatchupTopics.Add(Keys.TopicName)
                    Catch exTopic As Exception
                        Logger.Warn(exTopic, $"派生群聊主题失败：{G.Id}")
                    End Try
                Next
                MyIdSnap = D.MyId
                SinceSnap = D.Since
            End SyncLock
            If Not AllTopics.Any() Then Return

            Dim NowSec As Long = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            Dim CatchupRaw As String = Nothing
            Dim CatchupSucceeded As Boolean = False
            If CatchupTopics.Any() Then
                Dim CatchupSince = Math.Max(0L, Math.Min(Math.Max(0L, SinceSnap - 5), NowSec - 12L * 3600L))
                Dim CatchupUrl = Relay() & "/" & String.Join(",", CatchupTopics) &
                                 "/json?poll=1&since=" & CatchupSince &
                                 "&_=" & DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                Try
                    CatchupRaw = NetRequestByClient(CatchupUrl, HttpMethod.Get, Timeout:=12000, MakeLog:=False)
                    CatchupSucceeded = True
                    SyncLock DataLock
                        For Each Tp In CatchupTopics
                            PendingCatchupTopics.Remove(Tp)
                        Next
                    End SyncLock
                Catch ex As Exception
                    Logger.Warn(ex, "新加好友/群聊 12 小时历史补拉失败")
                End Try
            End If

            Dim RegularTopics = If(CatchupSucceeded, AllTopics.Where(Function(tp) Not CatchupTopics.Contains(tp, StringComparer.OrdinalIgnoreCase)).ToList(), AllTopics)
            Dim RegularRaw As String = Nothing
            If RegularTopics.Any() Then
                Dim Url = Relay() & "/" & String.Join(",", RegularTopics) &
                          "/json?poll=1&since=" & Math.Max(0, SinceSnap - 5) &
                          "&_=" & DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                Try
                    RegularRaw = NetRequestByClient(Url, HttpMethod.Get, Timeout:=12000, MakeLog:=False)
                Catch ex As Exception
                    Logger.Warn(ex, "好友/群聊消息拉取失败")
                End Try
            End If

            Dim NewMessages As New List(Of FriendMessage)
            Dim NewSyncMessages As New List(Of FriendMessage)
            Dim PongTargets As New HashSet(Of String)
            Dim Changed As Boolean = False

            If Not String.IsNullOrWhiteSpace(CatchupRaw) Then
                If ProcessPollRawLines(D, CatchupRaw, TopicMap, MyIdSnap, SinceSnap, NewMessages, NewSyncMessages, PongTargets) Then Changed = True
            End If
            If Not String.IsNullOrWhiteSpace(RegularRaw) Then
                If ProcessPollRawLines(D, RegularRaw, TopicMap, MyIdSnap, SinceSnap, NewMessages, NewSyncMessages, PongTargets) Then Changed = True
            End If

            If Changed Then SaveData()

            For Each PingSender In PongTargets
                Dim CapId = PingSender
                RunInNewThread(
                    Sub()
                        Try
                            TransmitMessageCore(CapId, False, New FriendMessage With {.Kind = "pong", .Text = "ok"}, RecordLocally:=False)
                        Catch
                        End Try
                    End Sub, "Friend Handshake Pong")
            Next

            For Each SyncMsg In NewSyncMessages
                RaiseEvent InstanceSyncMessageReceived(SyncMsg)
            Next
            For Each Msg In NewMessages
                RaiseEvent FriendMessageReceived(Msg)
            Next
            If NewMessages.Any() OrElse Changed Then RaiseEvent MessagesChanged()
        End SyncLock
    End Sub

    Private Function ProcessPollRawLines(D As FriendData,
                                         Raw As String,
                                         TopicMap As Dictionary(Of String, PollTopicItem),
                                         MyIdSnap As String,
                                         SinceSnap As Long,
                                         NewMessages As List(Of FriendMessage),
                                         NewSyncMessages As List(Of FriendMessage),
                                         PongTargets As HashSet(Of String)) As Boolean
        Dim Added As Integer = 0
        Dim VerifiedChanged As Boolean = False
        Dim Latest As Long = SinceSnap
        Dim SeenAdded As Boolean = False
        Dim NowSec As Long = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

        For Each Line In Raw.Replace(vbCrLf, vbLf).Split(vbLf)
            If String.IsNullOrWhiteSpace(Line) Then Continue For
            Try
                Dim Ev = JObject.Parse(Line)
                If Ev.Value(Of String)("event") <> "message" Then Continue For
                Dim TopicName = Ev.Value(Of String)("topic")
                If String.IsNullOrEmpty(TopicName) Then Continue For
                Dim Item As PollTopicItem = Nothing
                If Not TopicMap.TryGetValue(TopicName, Item) Then Continue For

                Dim Id = Ev.Value(Of String)("id")
                If String.IsNullOrEmpty(Id) Then Continue For
                Dim EvTime = Ev.Value(Of Long)("time")
                If EvTime > Latest Then Latest = EvTime
                SyncLock DataLock
                    If D.Seen.Contains(Id) Then Continue For
                End SyncLock

                Dim MsgRaw = Ev.Value(Of String)("message")
                If String.IsNullOrWhiteSpace(MsgRaw) Then Continue For
                Dim Env = JObject.Parse(MsgRaw)
                Dim Ver = Env.Value(Of Integer)("v")
                Dim FromId = Env.Value(Of String)("from")
                Dim Ts = Env.Value(Of Long)("ts")
                If Ts < NowSec - 36L * 3600L OrElse Ts > NowSec + 600L Then
                    Logger.Warn($"丢弃时间窗口异常的消息（来自 {FromId}，ts={Ts}）")
                    Continue For
                End If
                Dim Nonce = Env.Value(Of String)("nonce")
                If String.IsNullOrEmpty(Nonce) Then Continue For
                Dim Cipher = Env.Value(Of String)("data")
                If String.IsNullOrEmpty(Cipher) Then Continue For

                If Not Item.IsGroup Then
                    If Ver <> 1 Then Continue For
                    Dim ToId = Env.Value(Of String)("to")
                    If ToId <> MyIdSnap OrElse FromId <> Item.Id Then Continue For
                    SyncLock DataLock
                        If D.Blocked.Contains(FromId) Then Continue For
                    End SyncLock
                    Dim SignText = $"1|{FromId}|{ToId}|{Ts}|{Nonce}|{Cipher}"
                    If Not FixedTimeEquals(MacOf(SignText, Item.Keys.MacKey), Env.Value(Of String)("mac")) Then
                        Logger.Warn($"丢弃一条鉴权失败的好友消息（来自 {FromId}）")
                        Continue For
                    End If
                    Dim NonceKey = "n:" & FromId & ":" & Nonce
                    SyncLock DataLock
                        If D.Seen.Contains(Id) OrElse D.Seen.Contains(NonceKey) Then Continue For
                        D.Seen.Add(Id)
                        D.Seen.Add(NonceKey)
                        SeenAdded = True
                    End SyncLock

                    Dim Payload = JObject.Parse(DecryptText(Cipher, Item.Keys.EncKey))
                    Dim Msg As New FriendMessage With {
                        .Id = Id, .PeerId = Item.Id, .FromMe = False, .IsGroup = False,
                        .SenderId = FromId, .Time = Ts
                    }
                    PopulateMessageFromPayload(Msg, Payload)
                    If String.IsNullOrWhiteSpace(Msg.SenderName) Then
                        Msg.SenderName = Item.DefaultLabel
                    End If

                    Dim StillExists As Boolean = False
                    SyncLock DataLock
                        Dim LiveF = D.Friends.FirstOrDefault(Function(x) x.Id = Item.Id)
                        If LiveF IsNot Nothing Then
                            StillExists = True
                            If LiveF.VerifiedUtc <= 0 OrElse (NowSec - LiveF.VerifiedUtc) > 300 Then
                                LiveF.VerifiedUtc = NowSec
                                VerifiedChanged = True
                            End If
                            If Msg.Kind <> "ping" AndAlso Msg.Kind <> "pong" Then
                                D.Messages.Add(Msg)
                                Dim CurUnread As Integer = 0
                                D.UnreadCounts.TryGetValue(LiveF.Id, CurUnread)
                                D.UnreadCounts(LiveF.Id) = CurUnread + 1
                            End If
                        End If
                    End SyncLock
                    If Not StillExists Then Continue For
                    If Msg.Kind = "ping" Then
                        PongTargets.Add(Item.Id)
                        Continue For
                    ElseIf Msg.Kind = "pong" Then
                        Continue For
                    End If
                    If Msg.Kind = "inst_sync" Then NewSyncMessages.Add(Msg)
                    NewMessages.Add(Msg)
                    Added += 1
                Else
                    If Ver <> 2 Then Continue For
                    Dim GrpId = Env.Value(Of String)("grp")
                    If Not String.Equals(GrpId, Item.Id, StringComparison.OrdinalIgnoreCase) Then Continue For
                    If String.IsNullOrWhiteSpace(FromId) OrElse FromId = MyIdSnap Then Continue For
                    SyncLock DataLock
                        If D.Blocked.Contains(FromId) Then Continue For
                    End SyncLock
                    Dim SignText = $"2|{GrpId}|{FromId}|{Ts}|{Nonce}|{Cipher}"
                    If Not FixedTimeEquals(MacOf(SignText, Item.Keys.MacKey), Env.Value(Of String)("mac")) Then
                        Logger.Warn($"丢弃一条群聊鉴权失败消息（群 {Item.Id}，发送者 {FromId}）")
                        Continue For
                    End If
                    Dim NonceKey = "gn:" & Item.Id & ":" & FromId & ":" & Nonce
                    SyncLock DataLock
                        If D.Seen.Contains(Id) OrElse D.Seen.Contains(NonceKey) Then Continue For
                        D.Seen.Add(Id)
                        D.Seen.Add(NonceKey)
                        SeenAdded = True
                    End SyncLock

                    Dim Payload = JObject.Parse(DecryptText(Cipher, Item.Keys.EncKey))
                    Dim Msg As New FriendMessage With {
                        .Id = Id, .PeerId = Item.Id, .FromMe = False, .IsGroup = True,
                        .SenderId = FromId, .Time = Ts
                    }
                    PopulateMessageFromPayload(Msg, Payload)
                    If String.IsNullOrWhiteSpace(Msg.SenderName) Then
                        Dim KnownF = FindFriend(FromId)
                        Msg.SenderName = If(KnownF IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(KnownF.Remark),
                                            KnownF.Remark,
                                            "玩家-" & FromId.Substring(Math.Max(0, FromId.Length - 4)))
                    End If

                    Dim StillInGroup As Boolean = False
                    SyncLock DataLock
                        Dim LiveG = D.Groups.FirstOrDefault(Function(x) String.Equals(x.Id, Item.Id, StringComparison.OrdinalIgnoreCase))
                        If LiveG IsNot Nothing Then
                            StillInGroup = True
                            Dim NormGId = LiveG.Id.Trim().ToUpperInvariant()
                            Msg.PeerId = NormGId
                            D.GroupMessages.Add(Msg)
                            Dim CurUnread As Integer = 0
                            D.GroupUnreadCounts.TryGetValue(NormGId, CurUnread)
                            D.GroupUnreadCounts(NormGId) = CurUnread + 1
                        End If
                    End SyncLock
                    If Not StillInGroup Then Continue For
                    If Msg.Kind = "inst_sync" Then NewSyncMessages.Add(Msg)
                    NewMessages.Add(Msg)
                    Added += 1
                End If
            Catch ex As Exception
                Logger.Warn(ex, "解析好友/群聊消息失败")
            End Try
        Next

        Dim PrevSince As Long
        SyncLock DataLock
            PrevSince = D.Since
            D.Since = Math.Max(D.Since, Latest)
        End SyncLock
        Return Added > 0 OrElse Latest > PrevSince OrElse SeenAdded OrElse VerifiedChanged
    End Function

#End Region

#Region "后台轮询"

    Private PollThread As Thread = Nothing
    Private StopFlag As Integer = 0

    Public Sub TriggerImmediatePoll()
        If Threading.Volatile.Read(StopFlag) = 1 Then Return
        Dim NowTicks = DateTime.UtcNow.Ticks
        Dim PrevTicks = Interlocked.Read(LastImmediatePollTicks)
        If (NowTicks - PrevTicks) < TimeSpan.FromMilliseconds(1200).Ticks Then Return
        If Interlocked.CompareExchange(LastImmediatePollTicks, NowTicks, PrevTicks) <> PrevTicks Then Return
        Try
            PollWakeSignal.Set()
        Catch
        End Try
    End Sub

    Public Sub StartPolling()
        SyncLock PollLock
            If PollThread IsNot Nothing AndAlso PollThread.IsAlive Then Return
            Threading.Interlocked.Exchange(StopFlag, 0)
            PollWakeSignal.Reset()
            PollThread = New Thread(AddressOf PollLoop) With {.IsBackground = True, .Name = "PCL Linking 好友与群聊轮询"}
            PollThread.Start()
        End SyncLock
    End Sub

    Public Sub StopPolling()
        Threading.Interlocked.Exchange(StopFlag, 1)
        Try
            PollWakeSignal.Set()
        Catch
        End Try
        Dim T = PollThread
        If T IsNot Nothing AndAlso T IsNot Thread.CurrentThread AndAlso T.IsAlive Then
            Try
                T.Join(500)
            Catch
            End Try
        End If
    End Sub

    Private Sub PollLoop()
        Do
            If Threading.Volatile.Read(StopFlag) = 1 Then Return
            Try
                PollOnce()
            Catch ex As Exception
                Logger.Warn(ex, "好友消息轮询异常")
            End Try
            If Threading.Volatile.Read(StopFlag) = 1 Then Return
            Dim Seconds = Math.Max(3, Math.Min(60, GetData().PollSeconds))
            Try
                PollWakeSignal.Wait(Seconds * 1000)
                PollWakeSignal.Reset()
            Catch
                If Threading.Volatile.Read(StopFlag) = 1 Then Return
                Thread.Sleep(1000)
            End Try
        Loop
    End Sub

#End Region

#Region "中继设置"

    Public Function RelayUrl() As String
        SyncLock DataLock
            Return GetData().RelayUrl
        End SyncLock
    End Function

    Public Sub SetRelayUrl(Url As String)
        Url = If(Url, "").Trim()
        If Url = "" Then Url = "https://ntfy.sh"
        If Not Url.Contains("://") Then Url = "https://" & Url
        Dim Parsed As Uri = Nothing
        If Not Uri.TryCreate(Url, UriKind.Absolute, Parsed) OrElse (Parsed.Scheme <> Uri.UriSchemeHttp AndAlso Parsed.Scheme <> Uri.UriSchemeHttps) Then
            Throw New ArgumentException("中继服务器地址必须为有效的 http/https URL！")
        End If
        SyncLock DataLock
            GetData().RelayUrl = Url.TrimEnd("/"c)
        End SyncLock
        SaveData()
    End Sub

    Public Sub ClearHistory(FriendId As String)
        Dim D = GetData()
        SyncLock DataLock
            D.Messages.RemoveAll(Function(m) m.PeerId = FriendId)
            D.UnreadCounts.Remove(FriendId)
        End SyncLock
        SaveData()
        RaiseEvent MessagesChanged()
    End Sub

#End Region

End Module
