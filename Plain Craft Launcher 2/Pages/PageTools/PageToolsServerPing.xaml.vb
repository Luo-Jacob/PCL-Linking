Imports System.Diagnostics
Imports System.IO
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

Public Class PageToolsServerPing
    Implements IRefreshable

    Public Class WatchServerItem
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Address As String = ""
        Public Property LastLatencyMs As Long = -1
        Public Property LastOnline As Boolean = False
        Public Property LastStatusSummary As String = "尚未探测"
        Public Property LastMotd As String = ""
        Public Property LastCheckedUtc As DateTime? = Nothing
    End Class

    Public LastProbeResult As SlpProbeEngine.ServerProbeResult = Nothing
    Private ReadOnly WatchLock As New Object()
    Private ReadOnly WatchItems As New List(Of WatchServerItem)
    Private IsProbingSingle As Boolean = False
    Private IsRefreshingAll As Boolean = False
    Private HasLoadedOnce As Boolean = False

    Private Shared Function GetWatchlistPath() As String
        Return GetPortableDataPath("ServerPingWatchlist.json")
    End Function

    Private Sub PageToolsServerPing_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If Not HasLoadedOnce Then
            HasLoadedOnce = True
            LoadWatchlist()
        End If
        RenderWatchlistUi()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshAllWatchlist()
    End Sub

    Private Sub LoadWatchlist()
        SyncLock WatchLock
            WatchItems.Clear()
            Try
                Dim JsonPath = GetWatchlistPath()
                If File.Exists(JsonPath) Then
                    Dim Loaded = JsonConvert.DeserializeObject(Of List(Of WatchServerItem))(File.ReadAllText(JsonPath, Encoding.UTF8))
                    If Loaded IsNot Nothing Then
                        WatchItems.AddRange(Loaded.Where(Function(x) x IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(x.Address)))
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, "读取服务器关注列表失败")
            End Try
        End SyncLock
    End Sub

    Private Sub SaveWatchlist()
        SyncLock WatchLock
            Try
                Dim JsonPath = GetWatchlistPath()
                Dim ParentDir = Path.GetDirectoryName(JsonPath)
                If Not String.IsNullOrEmpty(ParentDir) AndAlso Not Directory.Exists(ParentDir) Then
                    Directory.CreateDirectory(ParentDir)
                End If
                Dim TmpPath = JsonPath & ".tmp"
                File.WriteAllText(TmpPath, JsonConvert.SerializeObject(WatchItems, Formatting.Indented), New UTF8Encoding(False))
                If File.Exists(JsonPath) Then
                    File.Replace(TmpPath, JsonPath, Nothing, True)
                Else
                    File.Move(TmpPath, JsonPath)
                End If
            Catch ex As Exception
                Logger.Warn(ex, "保存服务器关注列表失败")
            End Try
        End SyncLock
    End Sub

#Region "SLP 1.7+ 协议探测核心（委托至 MitCore.SlpProbeEngine）"

    Public Shared Function ParseHostAndPort(RawAddr As String, ByRef OutHost As String, ByRef OutPort As Integer) As Boolean
        Return SlpProbeEngine.ParseHostAndPort(RawAddr, OutHost, OutPort)
    End Function

    Public Shared Function FormatHostAndPort(Host As String, Port As Integer, Optional OmitDefaultPort As Boolean = False) As String
        Return SlpProbeEngine.FormatHostAndPort(Host, Port, OmitDefaultPort)
    End Function

    Public Shared Function ProbeMinecraftServer(RawAddr As String, Optional TimeoutMs As Integer = 4500) As SlpProbeEngine.ServerProbeResult
        Return SlpProbeEngine.ProbeMinecraftServer(RawAddr, TimeoutMs)
    End Function

#End Region

#Region "即时探测交互"

    Private Sub BtnFillLocalServer_Click() Handles BtnFillLocalServer.Click
        Dim ServerDir = PageToolsServer.LoadSavedServerDir()
        Dim PortNum As Integer = 25565
        If Not String.IsNullOrWhiteSpace(ServerDir) Then
            Dim PropsPath = Path.Combine(ServerDir, "server.properties")
            If File.Exists(PropsPath) Then
                Try
                    For Each Line In File.ReadAllLines(PropsPath, Encoding.UTF8)
                        If Line.StartsWith("server-port=", StringComparison.OrdinalIgnoreCase) Then
                            Dim Raw = Line.Substring("server-port=".Length).Trim()
                            Dim TmpPort As Integer
                            If Integer.TryParse(Raw, TmpPort) AndAlso TmpPort >= 1 AndAlso TmpPort <= 65535 Then
                                PortNum = TmpPort
                            End If
                            Exit For
                        End If
                    Next
                Catch
                End Try
            End If
        End If
        TextPingAddress.Text = $"127.0.0.1:{PortNum}"
        If String.IsNullOrWhiteSpace(TextPingName.Text) Then TextPingName.Text = "本地开服"
        Hint($"已填入本地服务端探测地址 127.0.0.1:{PortNum}", HintType.Blue)
    End Sub

    Private Sub BtnFillLinkServer_Click() Handles BtnFillLinkServer.Click
        Dim Addr = PageLinkMain.CurrentClientAddress
        If String.IsNullOrWhiteSpace(Addr) Then
            Hint("当前未加入任何 EasyTier 联机房间，暂无可探测的虚拟地址。", HintType.Red)
            Return
        End If
        TextPingAddress.Text = Addr
        If String.IsNullOrWhiteSpace(TextPingName.Text) Then TextPingName.Text = "联机房主"
        Hint($"已填入联机房主地址 {Addr}", HintType.Blue)
    End Sub

    Private Sub BtnPingProbe_Click() Handles BtnPingProbe.Click
        If IsProbingSingle Then Return
        Dim Addr = If(TextPingAddress.Text, "").Trim()
        Dim DummyHost As String = ""
        Dim DummyPort As Integer = 25565
        If Not ParseHostAndPort(Addr, DummyHost, DummyPort) Then
            Hint("请输入合法的服务器地址或域名（如 mc.hypixel.net 或 127.0.0.1:25565）！", HintType.Red)
            Return
        End If

        IsProbingSingle = True
        BtnPingProbe.IsEnabled = False
        BorderProbeResult.Visibility = Visibility.Visible
        LabProbeTitle.Text = $"正在探测 {Addr} …"
        LabProbeLatency.Text = "连接中…"
        LabProbeDetails.Text = "正在执行 SLP 1.7+ 握手与状态请求，请稍候。"
        BtnProbeWriteServersDat.IsEnabled = False
        BtnProbeDirectLaunch.IsEnabled = False

        RunInNewThread(
            Sub()
                Dim Result = ProbeMinecraftServer(Addr)
                RunInUi(
                    Sub()
                        IsProbingSingle = False
                        BtnPingProbe.IsEnabled = True
                        LastProbeResult = Result
                        DisplayProbeResult(Result)
                    End Sub)
            End Sub, "MC SLP Probe Single")
    End Sub

    Private Sub DisplayProbeResult(Res As SlpProbeEngine.ServerProbeResult)
        BorderProbeResult.Visibility = Visibility.Visible
        BtnProbeWriteServersDat.IsEnabled = True
        BtnProbeDirectLaunch.IsEnabled = True

        If Res.Online Then
            LabProbeTitle.Text = $"[在线] {FormatHostAndPort(Res.Host, Res.Port)}"
            LabProbeLatency.Text = $"{Res.LatencyMs} ms"
            LabProbeLatency.SetResourceReference(TextBlock.ForegroundProperty, If(Res.LatencyMs <= 120, "ColorBrush2", "ColorBrush3"))
            Dim Sb As New StringBuilder()
            Sb.AppendLine($"服务端版本：{Res.VersionName} (协议 {Res.ProtocolVersion})   ｜   在线人数：{Res.OnlinePlayers} / {Res.MaxPlayers}")
            Sb.AppendLine($"MOTD 简介：{If(String.IsNullOrWhiteSpace(Res.Motd), "（无 MOTD）", Res.Motd)}")
            If Res.SamplePlayers.Any() Then
                Sb.Append($"在线玩家采样：{String.Join("、", Res.SamplePlayers)}")
            End If
            LabProbeDetails.Text = Sb.ToString().TrimEnd()
        Else
            LabProbeTitle.Text = $"[离线 / 不可达] {FormatHostAndPort(Res.Host, Res.Port)}"
            LabProbeLatency.Text = "超时/失败"
            LabProbeLatency.Foreground = New SolidColorBrush(Color.FromRgb(&HDC, &H26, &H26))
            LabProbeDetails.Text = $"探测失败原因：{Res.ErrorMessage}{vbCrLf}可检查服务器是否正在运行、端口是否被防火墙拦截，或是否需要先行联机组网。"
        End If
    End Sub

    Private Sub BtnPingAddWatch_Click() Handles BtnPingAddWatch.Click
        Dim Addr = If(TextPingAddress.Text, "").Trim()
        Dim Host As String = ""
        Dim Port As Integer = 25565
        If Not ParseHostAndPort(Addr, Host, Port) Then
            Hint("请先输入有效的服务器地址再加入关注列表！", HintType.Red)
            Return
        End If
        Dim OmitPort = (Port = 25565 AndAlso Not (Addr.Contains(":"c) AndAlso (Not Addr.StartsWith("[") OrElse Addr.Contains("]:"))))
        Dim NormalizedAddr = FormatHostAndPort(Host, Port, OmitDefaultPort:=OmitPort)
        Dim DisplayName = If(TextPingName.Text, "").Trim()
        If String.IsNullOrWhiteSpace(DisplayName) Then DisplayName = NormalizedAddr

        Dim AddedItem As WatchServerItem = Nothing
        SyncLock WatchLock
            Dim Existing = WatchItems.FirstOrDefault(Function(x) String.Equals(x.Address, NormalizedAddr, StringComparison.OrdinalIgnoreCase))
            If Existing IsNot Nothing Then
                Existing.Name = DisplayName
                AddedItem = Existing
            Else
                AddedItem = New WatchServerItem With {
                    .Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                    .Name = DisplayName,
                    .Address = NormalizedAddr
                }
                If LastProbeResult IsNot Nothing AndAlso String.Equals(LastProbeResult.Address, Addr, StringComparison.OrdinalIgnoreCase) Then
                    ApplyProbeToWatchItem(AddedItem, LastProbeResult)
                End If
                WatchItems.Add(AddedItem)
            End If
        End SyncLock
        SaveWatchlist()
        RenderWatchlistUi()
        Hint($"已将「{DisplayName}」加入关注列表", HintType.Green)
    End Sub

    Private Shared Sub ApplyProbeToWatchItem(Item As WatchServerItem, Res As SlpProbeEngine.ServerProbeResult)
        Item.LastCheckedUtc = DateTime.UtcNow
        Item.LastOnline = Res.Online
        Item.LastLatencyMs = Res.LatencyMs
        Item.LastMotd = Res.Motd
        If Res.Online Then
            Item.LastStatusSummary = $"在线 {Res.OnlinePlayers}/{Res.MaxPlayers}  ·  {Res.LatencyMs} ms  ·  {Res.VersionName}"
        Else
            Item.LastStatusSummary = $"离线/不可达（{Res.ErrorMessage}）"
        End If
    End Sub

    Private Sub BtnProbeWriteServersDat_Click() Handles BtnProbeWriteServersDat.Click
        If LastProbeResult Is Nothing Then Return
        Dim Name = If(TextPingName.Text, "").Trim()
        If String.IsNullOrWhiteSpace(Name) Then Name = LastProbeResult.Host
        WriteAddressToCurrentInstanceServersDat(Name, FormatHostAndPort(LastProbeResult.Host, LastProbeResult.Port, OmitDefaultPort:=True))
    End Sub

    Private Sub BtnProbeDirectLaunch_Click() Handles BtnProbeDirectLaunch.Click
        If LastProbeResult Is Nothing Then Return
        LaunchCurrentInstanceToServer(FormatHostAndPort(LastProbeResult.Host, LastProbeResult.Port, OmitDefaultPort:=True))
    End Sub

#End Region

#Region "关注列表管理与巡检"

    Private Sub RenderWatchlistUi()
        If PanWatchlist Is Nothing OrElse CardWatchlist Is Nothing Then Return
        PanWatchlist.Children.Clear()
        Dim Snapshot As List(Of WatchServerItem)
        SyncLock WatchLock
            Snapshot = WatchItems.ToList()
        End SyncLock

        CardWatchlist.Title = $"关注服务器列表 ({Snapshot.Count})"
        HintWatchlistEmpty.Visibility = If(Snapshot.Any(), Visibility.Collapsed, Visibility.Visible)
        If Not Snapshot.Any() Then Return

        For Each Item In Snapshot
            Dim LocalItem = Item
            Dim RowCard As New Border With {
                .CornerRadius = New CornerRadius(6),
                .Padding = New Thickness(12, 10, 12, 10),
                .Margin = New Thickness(0, 0, 0, 8),
                .BorderThickness = New Thickness(1)
            }
            RowCard.SetResourceReference(Border.BackgroundProperty, "ColorBrush8")
            RowCard.SetResourceReference(Border.BorderBrushProperty, If(LocalItem.LastOnline, "ColorBrush3", "ColorBrush6"))

            Dim RootGrid As New Grid()
            RootGrid.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
            RootGrid.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})

            '第一行：名称、地址与状态摘要
            Dim TopPanel As New StackPanel()
            Dim TitleText As New TextBlock With {
                .Text = $"{If(LocalItem.LastOnline, "● [在线] ", "○ ")} {LocalItem.Name}  ({LocalItem.Address})",
                .FontSize = 13.5,
                .FontWeight = FontWeights.SemiBold,
                .TextTrimming = TextTrimming.CharacterEllipsis
            }
            TitleText.SetResourceReference(TextBlock.ForegroundProperty, If(LocalItem.LastOnline, "ColorBrush2", "ColorBrush1"))
            TopPanel.Children.Add(TitleText)

            Dim TimeTag = If(LocalItem.LastCheckedUtc.HasValue, $"（更新于 {LocalItem.LastCheckedUtc.Value.ToLocalTime():HH:mm:ss}）", "")
            Dim StatusText As New TextBlock With {
                .Text = $"{LocalItem.LastStatusSummary} {TimeTag}".Trim(),
                .FontSize = 12,
                .Margin = New Thickness(0, 3, 0, 2),
                .TextWrapping = TextWrapping.Wrap
            }
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
            TopPanel.Children.Add(StatusText)

            If Not String.IsNullOrWhiteSpace(LocalItem.LastMotd) Then
                Dim MotdTb As New TextBlock With {
                    .Text = $"MOTD：{LocalItem.LastMotd}",
                    .FontSize = 11.5,
                    .TextTrimming = TextTrimming.CharacterEllipsis,
                    .Margin = New Thickness(0, 1, 0, 4)
                }
                MotdTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                TopPanel.Children.Add(MotdTb)
            End If

            Grid.SetRow(TopPanel, 0)
            RootGrid.Children.Add(TopPanel)

            '第二行：快捷操作按钮
            Dim BtnWrap As New WrapPanel With {.Margin = New Thickness(0, 6, 0, 0)}

            Dim BtnRefreshOne As New MyButton With {
                .Text = "刷新",
                .Height = 26,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 0, 6, 4)
            }
            AddHandler BtnRefreshOne.Click,
                Sub()
                    BtnRefreshOne.IsEnabled = False
                    RunInNewThread(
                        Sub()
                            Dim Res = ProbeMinecraftServer(LocalItem.Address)
                            SyncLock WatchLock
                                ApplyProbeToWatchItem(LocalItem, Res)
                            End SyncLock
                            SaveWatchlist()
                            RunInUi(Sub() RenderWatchlistUi())
                        End Sub, "Refresh Single Watch Server")
                End Sub
            BtnWrap.Children.Add(BtnRefreshOne)

            Dim BtnWriteDat As New MyButton With {
                .Text = "写入 servers.dat",
                .Height = 26,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 0, 6, 4)
            }
            AddHandler BtnWriteDat.Click, Sub() WriteAddressToCurrentInstanceServersDat(LocalItem.Name, LocalItem.Address)
            BtnWrap.Children.Add(BtnWriteDat)

            Dim BtnLaunchDirect As New MyButton With {
                .Text = "直连启动",
                .Height = 26,
                .Padding = New Thickness(10, 0, 10, 0),
                .ColorType = MyButton.ColorState.Highlight,
                .Margin = New Thickness(0, 0, 6, 4)
            }
            AddHandler BtnLaunchDirect.Click, Sub() LaunchCurrentInstanceToServer(LocalItem.Address)
            BtnWrap.Children.Add(BtnLaunchDirect)

            Dim BtnDel As New MyButton With {
                .Text = "移除",
                .Height = 26,
                .Padding = New Thickness(10, 0, 10, 0),
                .ColorType = MyButton.ColorState.Red,
                .Margin = New Thickness(0, 0, 0, 4)
            }
            AddHandler BtnDel.Click,
                Sub()
                    SyncLock WatchLock
                        WatchItems.RemoveAll(Function(x) String.Equals(x.Id, LocalItem.Id, StringComparison.OrdinalIgnoreCase))
                    End SyncLock
                    SaveWatchlist()
                    RenderWatchlistUi()
                End Sub
            BtnWrap.Children.Add(BtnDel)

            Grid.SetRow(BtnWrap, 1)
            RootGrid.Children.Add(BtnWrap)
            RowCard.Child = RootGrid
            PanWatchlist.Children.Add(RowCard)
        Next
    End Sub

    Private Sub BtnWatchRefreshAll_Click() Handles BtnWatchRefreshAll.Click
        RefreshAllWatchlist()
    End Sub

    Private Sub RefreshAllWatchlist()
        If IsRefreshingAll Then Return
        Dim Targets As List(Of WatchServerItem)
        SyncLock WatchLock
            Targets = WatchItems.ToList()
        End SyncLock
        If Not Targets.Any() Then
            Hint("关注列表为空，暂无需刷新。", HintType.Blue)
            Return
        End If

        IsRefreshingAll = True
        BtnWatchRefreshAll.IsEnabled = False
        BtnWatchRefreshAll.Text = "探测中…"

        RunInNewThread(
            Sub()
                Dim HadError As Boolean = False
                Try
                    Using Sem As New SemaphoreSlim(6)
                        Dim Tasks = Targets.Select(
                            Function(item) Task.Run(
                                Sub()
                                    Sem.Wait()
                                    Try
                                        Dim Res = ProbeMinecraftServer(item.Address, 4000)
                                        SyncLock WatchLock
                                            ApplyProbeToWatchItem(item, Res)
                                        End SyncLock
                                    Catch
                                        HadError = True
                                    Finally
                                        Sem.Release()
                                    End Try
                                End Sub)).ToArray()
                        Task.WaitAll(Tasks)
                    End Using
                    SaveWatchlist()
                Catch ex As Exception
                    HadError = True
                    Logger.Warn(ex, "批量探测关注服务器异常")
                Finally
                    RunInUi(
                        Sub()
                            IsRefreshingAll = False
                            BtnWatchRefreshAll.IsEnabled = True
                            BtnWatchRefreshAll.Text = "全部刷新"
                            RenderWatchlistUi()
                            If HadError Then
                                Hint($"已完成 {Targets.Count} 个关注服务器状态刷新（部分探测发生异常）", HintType.Blue)
                            Else
                                Hint($"已完成 {Targets.Count} 个关注服务器状态刷新", HintType.Green)
                            End If
                        End Sub)
                End Try
            End Sub, "Batch Ping Watchlist")
    End Sub

    Private Sub WriteAddressToCurrentInstanceServersDat(ServerName As String, ServerIp As String)
        If McInstanceSelected Is Nothing Then
            Hint("请先在左侧/版本选择中选中一个游戏实例！", HintType.Red)
            Return
        End If
        Try
            Dim IndieRoot = PathUtils.RemoveExtendedPrefix(McInstanceSelected.PathIndie)
            Directory.CreateDirectory(IndieRoot)
            Dim DatPath = Path.Combine(IndieRoot, "servers.dat")
            Dim Added = ModNbt.EnsureServerInServersDat(DatPath, ServerName, ServerIp)
            If Added Then
                Hint($"已将「{ServerName}」({ServerIp}) 写入实例「{McInstanceSelected.Name}」的服务器列表！", HintType.Green)
            Else
                Hint($"实例「{McInstanceSelected.Name}」的 servers.dat 中已包含 {ServerIp}。", HintType.Blue)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "写入 servers.dat 失败")
            Hint($"写入失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub LaunchCurrentInstanceToServer(ServerIp As String)
        If McInstanceSelected Is Nothing Then
            Hint("请先选中一个游戏实例，再来直连启动服务器！", HintType.Red)
            FrmMain.PageChange(FormMain.PageType.InstanceSelect)
            Return
        End If
        Hint($"正在使用「{McInstanceSelected.Name}」直连启动 {ServerIp} …", HintType.Blue)
        McLaunchStart(New McLaunchOptions With {.Instance = McInstanceSelected, .ServerIp = ServerIp})
    End Sub

#End Region

End Class
