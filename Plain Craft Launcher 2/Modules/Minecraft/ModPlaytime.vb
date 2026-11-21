Imports System.IO
Imports Newtonsoft.Json

''' <summary>
''' 按实例统计与持久化游戏游玩时长与游玩次数（独立存储于便携数据目录 PCL Linking\Playtime.json）。
''' </summary>
Public Module ModPlaytime

    Public Class PlaytimeRecord
        Public Property InstanceKey As String = ""
        Public Property InstanceName As String = ""
        Public Property TotalSeconds As Long = 0
        Public Property SessionCount As Integer = 0
        Public Property LastSessionSeconds As Long = 0
        Public Property LastPlayedUtc As DateTime? = Nothing
    End Class

    Private ReadOnly PlaytimeLock As New Object()
    Private _CachedRecords As Dictionary(Of String, PlaytimeRecord) = Nothing

    Private Function GetDataFilePath() As String
        Return GetPortableDataPath("Playtime.json")
    End Function

    Public Function GetInstanceKey(Instance As McInstance) As String
        If Instance Is Nothing OrElse String.IsNullOrWhiteSpace(Instance.PathVersion) Then Return ""
        Return PathUtils.RemoveExtendedPrefix(Instance.PathVersion).TrimEnd("\"c, "/"c).ToLowerInvariant()
    End Function

    Private Function CloneRecord(Rec As PlaytimeRecord) As PlaytimeRecord
        If Rec Is Nothing Then Return Nothing
        Return New PlaytimeRecord With {
            .InstanceKey = Rec.InstanceKey,
            .InstanceName = Rec.InstanceName,
            .TotalSeconds = Rec.TotalSeconds,
            .SessionCount = Rec.SessionCount,
            .LastSessionSeconds = Rec.LastSessionSeconds,
            .LastPlayedUtc = Rec.LastPlayedUtc
        }
    End Function

    Private Function EnsureLoadedNoLock(ByRef OutTransientError As Boolean) As Dictionary(Of String, PlaytimeRecord)
        OutTransientError = False
        If _CachedRecords IsNot Nothing Then Return _CachedRecords
        Dim Dict As New Dictionary(Of String, PlaytimeRecord)(StringComparer.OrdinalIgnoreCase)
        Dim DataPath = GetDataFilePath()
        If File.Exists(DataPath) Then
            Try
                Dim Raw = File.ReadAllText(DataPath, Text.Encoding.UTF8)
                Dim Items = JsonConvert.DeserializeObject(Of List(Of PlaytimeRecord))(Raw)
                If Items IsNot Nothing Then
                    For Each Item In Items
                        If Item IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(Item.InstanceKey) Then
                            Dict(Item.InstanceKey) = Item
                        End If
                    Next
                End If
            Catch exIo As IOException
                OutTransientError = True
                Logger.Warn(exIo, "读取实例游玩时长记录发生 I/O 异常，本次将跳过覆盖原文件")
                Return Dict
            Catch ex As Exception
                Logger.Warn(ex, "读取实例游玩时长记录失败，将备份损坏文件并重建记录")
                Try
                    File.Copy(DataPath, $"{DataPath}.corrupt.{DateTime.Now:yyyyMMddHHmmss}.bak", True)
                Catch
                End Try
            End Try
        End If
        _CachedRecords = Dict
        Return _CachedRecords
    End Function

    Private Sub SaveNoLock()
        If _CachedRecords Is Nothing Then Return
        Try
            Dim DataPath = GetDataFilePath()
            Dim ParentDir = Path.GetDirectoryName(DataPath)
            If Not String.IsNullOrEmpty(ParentDir) AndAlso Not Directory.Exists(ParentDir) Then
                Directory.CreateDirectory(ParentDir)
            End If
            Dim ListCopy = _CachedRecords.Values.OrderByDescending(Function(r) r.TotalSeconds).ToList()
            Dim Json = JsonConvert.SerializeObject(ListCopy, Formatting.Indented)
            Dim TmpPath = DataPath & ".tmp"
            File.WriteAllText(TmpPath, Json, New Text.UTF8Encoding(False))
            If File.Exists(DataPath) Then
                File.Replace(TmpPath, DataPath, Nothing, True)
            Else
                File.Move(TmpPath, DataPath)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "保存实例游玩时长记录失败")
        End Try
    End Sub

    Public Function RecordSession(Instance As McInstance, ElapsedSeconds As Long) As PlaytimeRecord
        If Instance Is Nothing Then Return Nothing
        Dim Key = GetInstanceKey(Instance)
        If String.IsNullOrEmpty(Key) Then Return Nothing
        Dim SafeSeconds = Math.Max(0L, ElapsedSeconds)
        If SafeSeconds < 5 Then Return GetRecord(Instance)
        SyncLock PlaytimeLock
            Dim TransientErr As Boolean = False
            Dim Dict = EnsureLoadedNoLock(TransientErr)
            If TransientErr Then Return Nothing
            Dim Rec As PlaytimeRecord = Nothing
            If Not Dict.TryGetValue(Key, Rec) Then
                Rec = New PlaytimeRecord With {.InstanceKey = Key}
                Dict(Key) = Rec
            End If
            Rec.InstanceName = Instance.Name
            Rec.LastPlayedUtc = DateTime.UtcNow
            Rec.TotalSeconds += SafeSeconds
            Rec.SessionCount += 1
            Rec.LastSessionSeconds = SafeSeconds
            SaveNoLock()
            Return CloneRecord(Rec)
        End SyncLock
    End Function

    Public Function GetRecord(Instance As McInstance) As PlaytimeRecord
        Dim Key = GetInstanceKey(Instance)
        If String.IsNullOrEmpty(Key) Then Return Nothing
        SyncLock PlaytimeLock
            Dim TransientErr As Boolean = False
            Dim Dict = EnsureLoadedNoLock(TransientErr)
            Dim Rec As PlaytimeRecord = Nothing
            If Dict.TryGetValue(Key, Rec) Then Return CloneRecord(Rec)
        End SyncLock
        Return Nothing
    End Function

    Public Function GetAllRecords() As List(Of PlaytimeRecord)
        SyncLock PlaytimeLock
            Dim TransientErr As Boolean = False
            Dim Dict = EnsureLoadedNoLock(TransientErr)
            Return Dict.Values.Select(Function(r) CloneRecord(r)).OrderByDescending(Function(r) r.TotalSeconds).ThenByDescending(Function(r) If(r.LastPlayedUtc, DateTime.MinValue)).ToList()
        End SyncLock
    End Function

    Public Function FormatDuration(TotalSeconds As Long) As String
        If TotalSeconds <= 0 Then Return "0 分钟"
        If TotalSeconds < 60 Then Return $"{TotalSeconds} 秒"
        Dim Ts = TimeSpan.FromSeconds(TotalSeconds)
        Dim Hours = CLng(Math.Floor(Ts.TotalHours))
        If Hours >= 1 Then
            If Ts.Minutes = 0 Then Return $"{Hours} 小时"
            Return $"{Hours} 小时 {Ts.Minutes} 分钟"
        End If
        Return $"{Ts.Minutes} 分钟"
    End Function

    Public Function FormatLastPlayed(LastUtc As DateTime?) As String
        If Not LastUtc.HasValue Then Return "尚未游玩"
        Dim LocalTime = LastUtc.Value.ToLocalTime()
        Dim Diff = DateTime.Now - LocalTime
        If Diff.TotalSeconds >= -60 AndAlso Diff.TotalMinutes < 2 Then Return "刚刚"
        If LocalTime.Date = DateTime.Now.Date Then Return $"今天 {LocalTime:HH:mm}"
        If LocalTime.Date = DateTime.Now.Date.AddDays(-1) Then Return $"昨天 {LocalTime:HH:mm}"
        Return LocalTime.ToString("yyyy-MM-dd HH:mm")
    End Function

    Public Function BuildGlobalRankingText(Optional Current As McInstance = Nothing) As String
        Dim All = GetAllRecords()
        If Not All.Any() Then
            Return "目前还没有记录到任何实例的游玩时长。启动一次游戏并在退出后，PCL Linking 会自动统计每个实例的累计时长、启动次数与最后游玩时间。"
        End If
        Dim CurrentKey = GetInstanceKey(Current)
        Dim TotalAllSeconds = All.Sum(Function(r) r.TotalSeconds)
        Dim TotalSessions = All.Sum(Function(r) r.SessionCount)
        Dim Sb As New Text.StringBuilder()
        Sb.AppendLine($"累计统计实例数：{All.Count} 个  ｜  总时长：{FormatDuration(TotalAllSeconds)}  ｜  总有效启动：{TotalSessions} 次")
        Sb.AppendLine()
        Dim Rank = 1
        For Each Rec In All.Take(15)
            Dim IsCurr = Not String.IsNullOrEmpty(CurrentKey) AndAlso String.Equals(Rec.InstanceKey, CurrentKey, StringComparison.OrdinalIgnoreCase)
            Dim Prefix = If(IsCurr, "★ ", "")
            Dim DisplayName = If(String.IsNullOrWhiteSpace(Rec.InstanceName), Path.GetFileName(Rec.InstanceKey), Rec.InstanceName)
            Sb.AppendLine($"{Rank}. {Prefix}{DisplayName} — {FormatDuration(Rec.TotalSeconds)}（{Rec.SessionCount} 次，最近：{FormatLastPlayed(Rec.LastPlayedUtc)}）")
            Rank += 1
        Next
        If All.Count > 15 Then
            Sb.AppendLine($"……其余 {All.Count - 15} 个实例略。")
        End If
        Return Sb.ToString().TrimEnd()
    End Function

End Module
