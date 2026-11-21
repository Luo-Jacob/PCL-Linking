Imports System.Windows.Input
Imports System.Windows.Threading

Public Class WinInGameOverlay

    Private Shared _Instance As WinInGameOverlay = Nothing
    Private CurrentFriendId As String = ""
    Private CurrentTargetIsGroup As Boolean = False
    Private LatestInviteCode As String = ""
    Private LatestInviteVerify As String = ""
    Private AutoHideTimer As DispatcherTimer = Nothing
    Private PerfRefreshTimer As DispatcherTimer = Nothing
    Private IsPinnedByUser As Boolean = False
    Private _HasPositionedOnce As Boolean = False

    Public Sub New()
        InitializeComponent()
        AutoHideTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(8)}
        AddHandler AutoHideTimer.Tick, AddressOf OnAutoHideTick
        PerfRefreshTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(2)}
        AddHandler PerfRefreshTimer.Tick, AddressOf OnPerfRefreshTick
        AddHandler ModFriends.MessagesChanged, AddressOf OnFriendMessagesUpdated
    End Sub

    Private Sub OnPerfRefreshTick(sender As Object, e As EventArgs)
        If Visibility <> Visibility.Visible Then
            PerfRefreshTimer.Stop()
            Return
        End If
        LabOverlayPerf.Text = ModWatcher.Watcher.GetActiveMinecraftPerfSummary()
    End Sub

    Private Sub WinInGameOverlay_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs) Handles Me.IsVisibleChanged
        If CBool(e.NewValue) Then
            LabOverlayPerf.Text = ModWatcher.Watcher.GetActiveMinecraftPerfSummary()
            PerfRefreshTimer?.Start()
        Else
            PerfRefreshTimer?.Stop()
        End If
    End Sub

    Private Sub OnAutoHideTick(sender As Object, e As EventArgs)
        If IsPinnedByUser OrElse IsMouseOver OrElse TextQuickReply.IsKeyboardFocusWithin Then Return
        AutoHideTimer.Stop()
        Hide()
    End Sub

    Private Sub WinInGameOverlay_Closed(sender As Object, e As EventArgs) Handles Me.Closed
        AutoHideTimer?.Stop()
        PerfRefreshTimer?.Stop()
        RemoveHandler ModFriends.MessagesChanged, AddressOf OnFriendMessagesUpdated
        If Object.ReferenceEquals(_Instance, Me) Then _Instance = Nothing
    End Sub

    Private Sub OnFriendMessagesUpdated()
        RunInUi(
        Sub()
            If Visibility = Visibility.Visible Then RefreshOverlayUi()
        End Sub)
    End Sub

    Public Shared Sub ShowMessageToast(Msg As ModFriends.FriendMessage)
        If Msg Is Nothing OrElse Msg.FromMe Then Return
        RunInUi(
        Sub()
            Try
                EnsureCreated()
                Dim PreserveSession = (_Instance.Visibility = Visibility.Visible AndAlso
                                      (_Instance.IsPinnedByUser OrElse
                                       _Instance.TextQuickReply.IsKeyboardFocusWithin OrElse
                                       Not String.IsNullOrWhiteSpace(_Instance.TextQuickReply.Text)))
                If PreserveSession Then
                    _Instance.RefreshOverlayUi()
                    Return
                End If
                _Instance.CurrentFriendId = Msg.PeerId
                _Instance.CurrentTargetIsGroup = Msg.IsGroup
                _Instance.IsPinnedByUser = False
                _Instance.PositionTopRight()
                _Instance.RefreshOverlayUi()
                _Instance.Show()
                _Instance.AutoHideTimer.Stop()
                _Instance.AutoHideTimer.Start()
            Catch ex As Exception
                Logger.Warn(ex, "显示游戏内消息浮层失败")
            End Try
        End Sub)
    End Sub

    Public Shared Sub ToggleOverlay()
        RunInUi(
        Sub()
            Try
                EnsureCreated()
                If _Instance.Visibility = Visibility.Visible Then
                    _Instance.AutoHideTimer.Stop()
                    _Instance.Hide()
                Else
                    _Instance.IsPinnedByUser = True
                    _Instance.AutoHideTimer.Stop()
                    _Instance.PositionTopRight()
                    _Instance.RefreshOverlayUi()
                    _Instance.Show()
                    _Instance.Activate()
                    _Instance.TextQuickReply.Focus()
                End If
            Catch ex As Exception
                Logger.Warn(ex, "切换游戏内浮层失败")
            End Try
        End Sub)
    End Sub

    Public Shared Sub CloseOverlay()
        RunInUi(
        Sub()
            Try
                If _Instance IsNot Nothing Then
                    _Instance.Close()
                    _Instance = Nothing
                End If
            Catch
            End Try
        End Sub)
    End Sub

    Private Shared Sub EnsureCreated()
        If _Instance Is Nothing OrElse Not _Instance.IsLoaded Then
            If _Instance IsNot Nothing Then
                Try
                    _Instance.Close()
                Catch
                End Try
            End If
            _Instance = New WinInGameOverlay()
        End If
    End Sub

    Private Sub PositionTopRight()
        Dim VirtLeft = SystemParameters.VirtualScreenLeft
        Dim VirtTop = SystemParameters.VirtualScreenTop
        Dim VirtRight = VirtLeft + SystemParameters.VirtualScreenWidth
        Dim VirtBottom = VirtTop + SystemParameters.VirtualScreenHeight
        If _HasPositionedOnce AndAlso Not Double.IsNaN(Left) AndAlso Not Double.IsNaN(Top) AndAlso
           Left >= VirtLeft AndAlso Left <= VirtRight - 60 AndAlso
           Top >= VirtTop AndAlso Top <= VirtBottom - 60 Then Return
        Dim Work = SystemParameters.WorkArea
        Left = Math.Max(Work.Left + 12, Work.Right - Width - 18)
        Top = Work.Top + 20
        _HasPositionedOnce = True
    End Sub

    Private Sub RefreshOverlayUi()
        Dim LinkInfo = PageLinkMain.GetLiveLinkSummary()
        If String.IsNullOrWhiteSpace(LinkInfo) Then
            LabOverlayLinkState.Text = ""
            BorderLinkBadge.Visibility = Visibility.Collapsed
        Else
            LabOverlayLinkState.Text = LinkInfo
            BorderLinkBadge.Visibility = Visibility.Visible
        End If
        LabOverlayPerf.Text = ModWatcher.Watcher.GetActiveMinecraftPerfSummary()

        Dim D = ModFriends.GetData()
        Dim AllTargets As New List(Of (IsGroup As Boolean, Id As String))
        For Each F In D.Friends
            AllTargets.Add((False, F.Id))
        Next
        For Each G In D.Groups
            AllTargets.Add((True, G.Id))
        Next

        If Not AllTargets.Any() Then
            CurrentFriendId = ""
            CurrentTargetIsGroup = False
            LabOverlayFriend.Text = "暂无好友或群聊（请先在好友页添加）"
            TextQuickReply.IsEnabled = False
            BtnQuickSend.IsEnabled = False
            BtnPrevFriend.IsEnabled = False
            BtnNextFriend.IsEnabled = False
            PanOverlayMessages.Children.Clear()
            PanInviteBar.Visibility = Visibility.Collapsed
            Return
        End If

        BtnPrevFriend.IsEnabled = AllTargets.Count > 1
        BtnNextFriend.IsEnabled = AllTargets.Count > 1

        If String.IsNullOrEmpty(CurrentFriendId) OrElse Not AllTargets.Any(Function(t) t.IsGroup = CurrentTargetIsGroup AndAlso String.Equals(t.Id, CurrentFriendId, StringComparison.OrdinalIgnoreCase)) Then
            CurrentTargetIsGroup = AllTargets(0).IsGroup
            CurrentFriendId = AllTargets(0).Id
        End If

        Dim Display As String
        Dim Unread As Integer
        If CurrentTargetIsGroup Then
            Dim Grp = ModFriends.FindGroup(CurrentFriendId)
            Unread = ModFriends.GetGroupUnread(CurrentFriendId)
            Display = "[群] " & If(Grp Is Nothing OrElse String.IsNullOrWhiteSpace(Grp.Name), CurrentFriendId, Grp.Name)
        Else
            Dim FriendObj = ModFriends.FindFriend(CurrentFriendId)
            Unread = ModFriends.GetUnread(CurrentFriendId)
            Display = If(FriendObj Is Nothing OrElse String.IsNullOrWhiteSpace(FriendObj.Remark), CurrentFriendId, FriendObj.Remark)
        End If
        LabOverlayFriend.Text = If(Unread > 0, $"{Display}（{Unread} 条未读）", Display)
        TextQuickReply.IsEnabled = True
        BtnQuickSend.IsEnabled = True

        PanOverlayMessages.Children.Clear()
        LatestInviteCode = ""
        LatestInviteVerify = ""

        Dim Recent = If(CurrentTargetIsGroup, ModFriends.GetGroupMessages(CurrentFriendId), ModFriends.GetMessages(CurrentFriendId))
        Dim Tail = Recent.Skip(Math.Max(0, Recent.Count - 4)).ToList()
        If Not Tail.Any() Then
            PanOverlayMessages.Children.Add(New TextBlock With {
                .Text = "暂无聊天消息，可在下方直接发送快捷回复。",
                .Foreground = New SolidColorBrush(Color.FromRgb(&HA3, &HB8, &HD2)),
                .FontSize = 12,
                .Margin = New Thickness(2, 6, 2, 6)
            })
        Else
            For Each M In Tail
                Dim SenderTag = If(M.FromMe, "我", If(CurrentTargetIsGroup AndAlso Not String.IsNullOrWhiteSpace(M.SenderName), M.SenderName, Display))
                Dim BodyText As String
                If M.Kind = "invite" Then
                    BodyText = "[联机房间邀请]"
                    If Not M.FromMe AndAlso Not String.IsNullOrWhiteSpace(M.InviteCode) Then
                        LatestInviteCode = M.InviteCode
                        LatestInviteVerify = If(M.InviteVerify, "")
                    End If
                ElseIf M.Kind = "modsync" Then
                    BodyText = "[Mod 同步清单] " & M.Text
                ElseIf M.Kind = "file" Then
                    BodyText = $"[加密文件] {M.FileName}"
                ElseIf M.Kind = "group_invite" Then
                    BodyText = $"[群聊邀请] {M.GroupInviteName}"
                ElseIf M.Kind = "inst_sync" Then
                    BodyText = $"[实例同步] {M.SyncChannel} ({M.Text})"
                Else
                    BodyText = M.Text
                    If Not M.FromMe Then
                        Dim ExCode As String = ""
                        Dim ExVer As String = ""
                        If PageFriends.TryExtractInviteFromText(M.Text, ExCode, ExVer) Then
                            LatestInviteCode = ExCode
                            LatestInviteVerify = ExVer
                        End If
                    End If
                End If
                If M.SendFailed Then BodyText = "[发送失败] " & BodyText

                Dim Bubble As New Border With {
                    .Background = New SolidColorBrush(If(M.FromMe, Color.FromRgb(&H16, &H3C, &H63), Color.FromRgb(&H22, &H2E, &H42))),
                    .BorderBrush = New SolidColorBrush(If(M.FromMe, Color.FromRgb(&H2B, &H6C, &HB0), Color.FromRgb(&H38, &H4A, &H67))),
                    .BorderThickness = New Thickness(1),
                    .CornerRadius = New CornerRadius(8),
                    .Padding = New Thickness(10, 6, 10, 6),
                    .Margin = New Thickness(0, 0, 0, 5),
                    .MaxWidth = 324,
                    .HorizontalAlignment = If(M.FromMe, HorizontalAlignment.Right, HorizontalAlignment.Left)
                }
                Dim MsgStack As New StackPanel()
                MsgStack.Children.Add(New TextBlock With {
                    .Text = $"{SenderTag} · {M.TimeText}",
                    .Foreground = New SolidColorBrush(If(M.FromMe, Color.FromRgb(&H93, &HC5, &HFD), Color.FromRgb(&HC0, &HCD, &HE0))),
                    .FontSize = 11,
                    .FontWeight = FontWeights.SemiBold,
                    .Margin = New Thickness(0, 0, 0, 2)
                })
                MsgStack.Children.Add(New TextBlock With {
                    .Text = BodyText,
                    .Foreground = Brushes.White,
                    .FontSize = 12.5,
                    .LineHeight = 18,
                    .TextWrapping = TextWrapping.Wrap
                })
                Bubble.Child = MsgStack
                PanOverlayMessages.Children.Add(Bubble)
            Next
            ScrollOverlayMessages?.ScrollToEnd()
        End If

        PanInviteBar.Visibility = If(String.IsNullOrWhiteSpace(LatestInviteCode), Visibility.Collapsed, Visibility.Visible)
    End Sub

    Private Sub GridHeader_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs) Handles GridHeader.MouseLeftButtonDown
        If e.LeftButton = MouseButtonState.Pressed Then
            Try
                DragMove()
            Catch
            End Try
        End If
    End Sub

    Private Sub BtnCloseOverlay_Click() Handles BtnCloseOverlay.Click
        AutoHideTimer.Stop()
        Hide()
    End Sub

    Private Sub BtnToggleBorderless_Click() Handles BtnToggleBorderless.Click
        Dim Msg = ModWatcher.Watcher.ToggleActiveMinecraftBorderlessFullscreen()
        LabOverlayPerf.Text = ModWatcher.Watcher.GetActiveMinecraftPerfSummary()
        Hint(Msg, HintType.Blue)
    End Sub

    Private Sub BtnOpenFriends_Click() Handles BtnOpenFriends.Click
        AutoHideTimer.Stop()
        Hide()
        If FrmMain IsNot Nothing Then
            FrmMain.ShowWindowToTop()
            FrmMain.PageChange(FormMain.PageType.Friends)
        End If
    End Sub

    Private Sub CycleFriend(Delta As Integer)
        Dim D = ModFriends.GetData()
        Dim AllTargets As New List(Of (IsGroup As Boolean, Id As String))
        For Each F In D.Friends
            AllTargets.Add((False, F.Id))
        Next
        For Each G In D.Groups
            AllTargets.Add((True, G.Id))
        Next
        If AllTargets.Count <= 1 Then Return
        Dim Idx = AllTargets.FindIndex(Function(t) t.IsGroup = CurrentTargetIsGroup AndAlso String.Equals(t.Id, CurrentFriendId, StringComparison.OrdinalIgnoreCase))
        If Idx < 0 Then Idx = 0
        Idx = (Idx + Delta + AllTargets.Count) Mod AllTargets.Count
        CurrentTargetIsGroup = AllTargets(Idx).IsGroup
        CurrentFriendId = AllTargets(Idx).Id
        IsPinnedByUser = True
        AutoHideTimer.Stop()
        RefreshOverlayUi()
    End Sub

    Private Sub BtnPrevFriend_Click() Handles BtnPrevFriend.Click
        CycleFriend(-1)
    End Sub

    Private Sub BtnNextFriend_Click() Handles BtnNextFriend.Click
        CycleFriend(1)
    End Sub

    Private Sub BtnOverlayJoinRoom_Click() Handles BtnOverlayJoinRoom.Click
        If String.IsNullOrWhiteSpace(LatestInviteCode) Then Return
        Dim Code = LatestInviteCode, Verify = LatestInviteVerify
        AutoHideTimer.Stop()
        Hide()
        FrmMain?.ShowWindowToTop()
        PageLinkMain.Join(Code, Verify)
    End Sub

    Private Sub BtnOverlayJoinPlay_Click() Handles BtnOverlayJoinPlay.Click
        If String.IsNullOrWhiteSpace(LatestInviteCode) Then Return
        Dim Code = LatestInviteCode, Verify = LatestInviteVerify
        AutoHideTimer.Stop()
        Hide()
        FrmMain?.ShowWindowToTop()
        PageLinkMain.Join(Code, Verify, AutoLaunchGame:=True)
    End Sub

    Private Sub BtnQuickSend_Click() Handles BtnQuickSend.Click
        If String.IsNullOrWhiteSpace(CurrentFriendId) OrElse Not BtnQuickSend.IsEnabled Then Return
        Dim Text = If(TextQuickReply.Text, "").Trim()
        If Text = "" Then Return
        Dim TargetId = CurrentFriendId
        Dim TargetIsGroup = CurrentTargetIsGroup
        TextQuickReply.Text = ""
        BtnQuickSend.IsEnabled = False
        IsPinnedByUser = True
        AutoHideTimer.Stop()
        If TargetIsGroup Then ModFriends.MarkGroupRead(TargetId) Else ModFriends.MarkRead(TargetId)
        RunInNewThread(
        Sub()
            Dim Err As String = Nothing
            Try
                Err = If(TargetIsGroup, ModFriends.SendGroup(TargetId, "text", Text), ModFriends.Send(TargetId, "text", Text))
            Catch ex As Exception
                Err = ex.Message
            Finally
                RunInUi(
                Sub()
                    BtnQuickSend.IsEnabled = True
                    RefreshOverlayUi()
                    If Err IsNot Nothing Then Hint($"浮层发送失败：{Err}", HintType.Red)
                End Sub)
            End Try
        End Sub, "Overlay Quick Reply")
    End Sub

    Private Sub TextQuickReply_KeyDown(sender As Object, e As KeyEventArgs) Handles TextQuickReply.KeyDown
        If e.Key = Key.Enter Then
            e.Handled = True
            BtnQuickSend_Click()
        ElseIf e.Key = Key.Escape Then
            e.Handled = True
            AutoHideTimer.Stop()
            Hide()
        End If
    End Sub

End Class
