Public Class PageFriends

    Private CurrentFriendId As String = ""
    Private CurrentIsGroup As Boolean = False
    Private PasswordVisible As Boolean = False
    Private Subscribed As Boolean = False

    Private Const LogoEyeOpen As String = "M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5Z"
    Private Const LogoEyeClosed As String = "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.2 16.38,18.66L16.81,19.08L19.73,22L21,20.73L3.27,3M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z"

    Private Sub PageFriends_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ModFriends.GetData()
        ModInstanceSync.Init()
        ModFriends.StartPolling()
        ModFriends.TriggerImmediatePoll()
        If Not Subscribed Then
            Subscribed = True
            AddHandler ModFriends.MessagesChanged, AddressOf OnMessagesChanged
        End If
        RefreshAll(ForceScroll:=True)
    End Sub

    Private Sub PageFriends_Enter() Handles Me.PageEnter
        If CurrentFriendId <> "" Then
            If CurrentIsGroup Then
                If ModFriends.GetGroupUnread(CurrentFriendId) > 0 Then ModFriends.MarkGroupRead(CurrentFriendId)
            Else
                If ModFriends.GetUnread(CurrentFriendId) > 0 Then ModFriends.MarkRead(CurrentFriendId)
            End If
        End If
        ModFriends.TriggerImmediatePoll()
        RefreshAll(ForceScroll:=False)
    End Sub

    Private Sub OnMessagesChanged()
        RunInUi(
            Sub()
                If CurrentFriendId <> "" AndAlso FrmMain IsNot Nothing AndAlso FrmMain.PageCurrent = FormMain.PageType.Friends Then
                    If CurrentIsGroup AndAlso ModFriends.GetGroupUnread(CurrentFriendId) > 0 Then
                        ModFriends.MarkGroupRead(CurrentFriendId)
                        Return
                    ElseIf (Not CurrentIsGroup) AndAlso ModFriends.GetUnread(CurrentFriendId) > 0 Then
                        ModFriends.MarkRead(CurrentFriendId)
                        Return
                    End If
                End If
                RefreshFriends()
                RenderMessages(ForceScroll:=False)
            End Sub)
    End Sub

    Private Sub RefreshAll(Optional ForceScroll As Boolean = False)
        RefreshCard()
        RefreshFriends()
        RenderMessages(ForceScroll:=ForceScroll)
    End Sub

    Public Sub RefreshLanguageUi()
        LocalizationService.TranslateElement(Me)
        RefreshAll(ForceScroll:=False)
    End Sub

#Region "名片与状态"

    Private Sub RefreshCard()
        Dim D = ModFriends.GetData()
        LabMyId.Text = D.MyId
        LabMyPassword.Text = If(PasswordVisible, D.MyPassword, New String("•"c, Math.Max(8, D.MyPassword.Length)))
        BtnTogglePwd.Logo = If(PasswordVisible, LogoEyeClosed, LogoEyeOpen)
        LabMyNickname.Text = ModFriends.GetMyDisplayName()

        Dim RelayRaw = ModFriends.RelayUrl()
        Dim RelayHost = RelayRaw.Replace("https://", "").Replace("http://", "")
        Dim BaseRelayText = LocalizationCore.TranslateText(If(RelayHost = "ntfy.sh", "中继: 公共节点", $"中继: {RelayHost}"))
        Dim EffProvider = ModFriends.ResolveEffectiveFileStorageProvider()
        Dim ProviderTag As String = ""
        Select Case EffProvider
            Case "s3" : ProviderTag = "S3"
            Case "webdav" : ProviderTag = "DAV"
            Case "pixeldrain" : ProviderTag = "PD"
        End Select
        LabRelay.Text = If(String.IsNullOrEmpty(ProviderTag), BaseRelayText, $"{BaseRelayText} · {ProviderTag}")
        LabRelay.ToolTip = LocalizationCore.TranslateText($"当前中继：{RelayRaw}（每 {D.PollSeconds} 秒后台拉取一次）") &
                           vbCrLf & LocalizationCore.TranslateText($"当前大文件默认存储：{If(EffProvider = "ntfy", "ntfy 分片（3小时）", EffProvider.ToUpperInvariant())}")

        Dim BlockedCount = D.Blocked.Count
        BtnUnblock.Text = LocalizationCore.TranslateText(If(BlockedCount > 0, $"黑名单 ({BlockedCount})", "黑名单"))
    End Sub

    Private Sub LabMyId_Click(sender As Object, e As MouseButtonEventArgs)
        ClipboardSet(ModFriends.GetData().MyId, False)
        Hint("已复制你的 10 位好友 ID！", HintType.Green)
    End Sub

    Private Sub LabMyNickname_Click(sender As Object, e As MouseButtonEventArgs)
        Dim Input = MyMsgBoxInput("设置我的群聊昵称", "在群聊消息和实例同步推送时向其他成员展示的名称（留空使用当前离线游戏名）：", ModFriends.GetData().MyNickname, HintText:="昵称（最多 20 字）")
        If Input Is Nothing Then Return
        ModFriends.SetMyNickname(If(Input.Length > 20, Input.Substring(0, 20), Input))
        RefreshCard()
        Hint("群聊昵称已更新！", HintType.Green)
    End Sub

    Private Sub BtnTogglePwd_Click(sender As Object, e As EventArgs) Handles BtnTogglePwd.Click
        PasswordVisible = Not PasswordVisible
        RefreshCard()
    End Sub

#End Region

#Region "好友列表"

    Private Shared Function FormatMsgPreview(Msg As ModFriends.FriendMessage, DefaultId As String, IsGroup As Boolean) As String
        If Msg Is Nothing Then Return $"ID: {DefaultId}"
        Dim Prefix = If(Msg.FromMe, LocalizationCore.TranslateText("我: "), If(IsGroup AndAlso Not String.IsNullOrWhiteSpace(Msg.SenderName), Msg.SenderName & ": ", ""))
        If Msg.SendFailed Then Prefix = LocalizationCore.TranslateText("[失败] ") & Prefix
        Select Case Msg.Kind
            Case "invite" : Return Prefix & LocalizationCore.TranslateText("[联机邀请]")
            Case "modsync" : Return Prefix & LocalizationCore.TranslateText("[Mod 清单]")
            Case "file" : Return Prefix & LocalizationCore.TranslateText("[文件] ") & Msg.FileName
            Case "save_relay" : Return Prefix & LocalizationCore.TranslateText("[存档接力] ") & If(String.IsNullOrWhiteSpace(Msg.WorldName), Msg.FileName, Msg.WorldName)
            Case "group_invite" : Return Prefix & LocalizationCore.TranslateText("[群邀请] ") & Msg.GroupInviteName
            Case "inst_sync" : Return Prefix & LocalizationCore.TranslateText("[实例同步] ") & Msg.SyncChannel
            Case Else
                Return Prefix & If(Msg.Text, "").Replace(vbCr, " ").Replace(vbLf, " ").Trim()
        End Select
    End Function

    Private Sub RefreshFriends()
        Dim D = ModFriends.GetData()
        LabFriendCount.Text = LocalizationCore.TranslateText(If(D.Groups.Any(), $"{D.Groups.Count} 群 · {D.Friends.Count} 好友", $"{D.Friends.Count} 好友"))

        Dim HasValidSelection = (CurrentFriendId <> "" AndAlso
            ((CurrentIsGroup AndAlso D.Groups.Any(Function(g) String.Equals(g.Id, CurrentFriendId, StringComparison.OrdinalIgnoreCase))) OrElse
             ((Not CurrentIsGroup) AndAlso D.Friends.Any(Function(f) String.Equals(f.Id, CurrentFriendId, StringComparison.OrdinalIgnoreCase)))))

        If Not HasValidSelection Then
            If D.Groups.Any() Then
                CurrentIsGroup = True
                CurrentFriendId = D.Groups(0).Id
            ElseIf D.Friends.Any() Then
                CurrentIsGroup = False
                CurrentFriendId = D.Friends(0).Id
            Else
                CurrentFriendId = ""
                CurrentIsGroup = False
            End If
            If CurrentFriendId <> "" AndAlso FrmMain IsNot Nothing AndAlso FrmMain.PageCurrent = FormMain.PageType.Friends Then
                If CurrentIsGroup Then
                    If ModFriends.GetGroupUnread(CurrentFriendId) > 0 Then ModFriends.MarkGroupRead(CurrentFriendId)
                Else
                    If ModFriends.GetUnread(CurrentFriendId) > 0 Then ModFriends.MarkRead(CurrentFriendId)
                End If
            End If
        End If

        PanFriends.Children.Clear()
        PanEmptyFriends.Visibility = If(D.Friends.Count = 0 AndAlso D.Groups.Count = 0, Visibility.Visible, Visibility.Collapsed)

        For Each G In D.Groups
            PanFriends.Children.Add(BuildGroupItem(G, CurrentIsGroup AndAlso String.Equals(G.Id, CurrentFriendId, StringComparison.OrdinalIgnoreCase)))
        Next
        For Each F In D.Friends
            PanFriends.Children.Add(BuildFriendItem(F, (Not CurrentIsGroup) AndAlso String.Equals(F.Id, CurrentFriendId, StringComparison.OrdinalIgnoreCase)))
        Next
    End Sub

    Private Function BuildGroupItem(G As ModFriends.GroupEntry, IsSelected As Boolean) As Border
        Dim DisplayTitle = "[群] " & G.DisplayName
        Dim LastMsg = ModFriends.GetGroupLastMessage(G.Id)
        Dim Preview = FormatMsgPreview(LastMsg, G.Id, True)
        Dim Unread = ModFriends.GetGroupUnread(G.Id)
        Return BuildConversationListItem(G.Id, True, DisplayTitle, "群", Preview, Unread, IsSelected, False)
    End Function

    Private Function BuildFriendItem(F As ModFriends.FriendEntry, IsSelected As Boolean) As Border
        Dim DisplayTitle = If(String.IsNullOrWhiteSpace(F.Remark), F.Id, F.Remark)
        Dim AvatarChar = DisplayTitle.Substring(0, 1).ToUpperInvariant()
        Dim LastMsg = ModFriends.GetLastMessage(F.Id)
        Dim Preview = FormatMsgPreview(LastMsg, F.Id, False)
        Dim Unread = ModFriends.GetUnread(F.Id)
        Return BuildConversationListItem(F.Id, False, DisplayTitle, AvatarChar, Preview, Unread, IsSelected, F.VerifiedUtc > 0)
    End Function

    Private Function BuildConversationListItem(TargetId As String, IsGroup As Boolean, DisplayTitle As String, AvatarChar As String,
                                               Preview As String, Unread As Integer, IsSelected As Boolean, IsVerifiedFriend As Boolean) As Border
        Dim Card As New Border With {
            .CornerRadius = New CornerRadius(6),
            .Padding = New Thickness(10, 8, 10, 8),
            .Margin = New Thickness(0, 0, 0, 6),
            .Cursor = Cursors.Hand,
            .BorderThickness = New Thickness(1)
        }
        If IsSelected Then
            Card.SetResourceReference(Border.BackgroundProperty, "ColorBrush7")
            Card.SetResourceReference(Border.BorderBrushProperty, "ColorBrush3")
        Else
            Card.SetResourceReference(Border.BackgroundProperty, "ColorBrushSurface")
            Card.BorderBrush = Brushes.Transparent
        End If

        Dim GridLayout As New Grid
        GridLayout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
        GridLayout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        GridLayout.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim Avatar As New Border With {
            .Width = 32,
            .Height = 32,
            .CornerRadius = New CornerRadius(If(IsGroup, 8, 16)),
            .Margin = New Thickness(0, 0, 9, 0),
            .VerticalAlignment = VerticalAlignment.Center
        }
        Avatar.SetResourceReference(Border.BackgroundProperty, If(IsSelected OrElse IsGroup, "ColorBrush3", "ColorBrush5"))
        Avatar.Child = New TextBlock With {
            .Text = AvatarChar,
            .Foreground = Brushes.White,
            .FontWeight = FontWeights.Bold,
            .FontSize = 13,
            .HorizontalAlignment = HorizontalAlignment.Center,
            .VerticalAlignment = VerticalAlignment.Center
        }
        Grid.SetColumn(Avatar, 0)
        GridLayout.Children.Add(Avatar)

        Dim InfoStack As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center}
        Dim NameLab As New TextBlock With {
            .Text = If(IsVerifiedFriend, DisplayTitle & " ✓", DisplayTitle),
            .FontWeight = FontWeights.Bold,
            .FontSize = 13,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        NameLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Dim SubLab As New TextBlock With {
            .Text = Preview,
            .FontSize = 11.5,
            .TextTrimming = TextTrimming.CharacterEllipsis,
            .Margin = New Thickness(0, 2, 0, 0)
        }
        SubLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        InfoStack.Children.Add(NameLab)
        InfoStack.Children.Add(SubLab)
        Grid.SetColumn(InfoStack, 1)
        GridLayout.Children.Add(InfoStack)

        If Unread > 0 Then
            Dim Badge As New Border With {
                .CornerRadius = New CornerRadius(9),
                .MinWidth = 18,
                .Height = 18,
                .Padding = New Thickness(5, 0, 5, 0),
                .Margin = New Thickness(6, 0, 0, 0),
                .VerticalAlignment = VerticalAlignment.Center,
                .Background = New SolidColorBrush(Color.FromRgb(220, 56, 56))
            }
            Badge.Child = New TextBlock With {
                .Text = If(Unread > 99, "99+", Unread.ToString()),
                .Foreground = Brushes.White,
                .FontSize = 10.5,
                .FontWeight = FontWeights.Bold,
                .HorizontalAlignment = HorizontalAlignment.Center,
                .VerticalAlignment = VerticalAlignment.Center
            }
            Grid.SetColumn(Badge, 2)
            GridLayout.Children.Add(Badge)
        End If

        Card.Child = GridLayout

        AddHandler Card.MouseEnter,
            Sub()
                If Not (CurrentIsGroup = IsGroup AndAlso String.Equals(CurrentFriendId, TargetId, StringComparison.OrdinalIgnoreCase)) Then
                    Card.SetResourceReference(Border.BorderBrushProperty, "ColorBrush5")
                End If
            End Sub
        AddHandler Card.MouseLeave,
            Sub()
                If Not (CurrentIsGroup = IsGroup AndAlso String.Equals(CurrentFriendId, TargetId, StringComparison.OrdinalIgnoreCase)) Then
                    Card.BorderBrush = Brushes.Transparent
                End If
            End Sub
        AddHandler Card.MouseLeftButtonUp,
            Sub()
                SelectConversation(TargetId, IsGroup)
            End Sub

        Return Card
    End Function

    Private Sub SelectConversation(TargetId As String, IsGroup As Boolean)
        Dim SameTarget = (CurrentIsGroup = IsGroup AndAlso String.Equals(CurrentFriendId, TargetId, StringComparison.OrdinalIgnoreCase))
        Dim Unread = If(IsGroup, ModFriends.GetGroupUnread(TargetId), ModFriends.GetUnread(TargetId))
        If SameTarget AndAlso Unread = 0 Then Return
        CurrentIsGroup = IsGroup
        CurrentFriendId = TargetId
        If IsGroup Then ModFriends.MarkGroupRead(TargetId) Else ModFriends.MarkRead(TargetId)
        ModFriends.TriggerImmediatePoll()
        RefreshFriends()
        RenderMessages(ForceScroll:=True)
    End Sub

#End Region

#Region "聊天记录"

    Private Sub UpdateChatHeader()
        If CurrentFriendId = "" Then
            AvatarChatBorder.Visibility = Visibility.Collapsed
            LabChatTitle.Text = LocalizationCore.TranslateText("选择一位好友或群聊开始沟通")
            LabChatSubTitle.Text = LocalizationCore.TranslateText("点击左侧列表，或点击「+ 添加」添加好友 / 加入或创建群聊")
            PanChatActions.Visibility = Visibility.Collapsed
            BtnSendFile.IsEnabled = False
            TextChatInput.IsEnabled = False
            BtnSend.IsEnabled = False
            Return
        End If

        If CurrentIsGroup Then
            Dim G = ModFriends.FindGroup(CurrentFriendId)
            If G Is Nothing Then
                CurrentFriendId = ""
                UpdateChatHeader()
                Return
            End If
            AvatarChatBorder.Visibility = Visibility.Visible
            LabChatAvatar.Text = LocalizationCore.TranslateText("群")
            LabChatTitle.Text = G.DisplayName
            LabChatSubTitle.Text = LocalizationCore.TranslateText($"群号：{G.Id} · 端到端加密群聊（切回即实时拉取）")
            PanChatActions.Visibility = Visibility.Visible
            BtnRemark.Visibility = Visibility.Collapsed
            BtnRemoveFriend.Visibility = Visibility.Collapsed
            BtnGroupCard.Visibility = Visibility.Visible
            BtnInviteToGroup.Visibility = Visibility.Visible
            BtnRenameGroup.Visibility = Visibility.Visible
            BtnLeaveGroup.Visibility = Visibility.Visible
        Else
            Dim F = ModFriends.FindFriend(CurrentFriendId)
            If F Is Nothing Then
                CurrentFriendId = ""
                UpdateChatHeader()
                Return
            End If
            Dim DisplayTitle = If(String.IsNullOrWhiteSpace(F.Remark), F.Id, F.Remark)
            AvatarChatBorder.Visibility = Visibility.Visible
            LabChatAvatar.Text = DisplayTitle.Substring(0, 1).ToUpperInvariant()
            LabChatTitle.Text = DisplayTitle
            LabChatSubTitle.Text = LocalizationCore.TranslateText($"好友 ID: {F.Id} · {If(F.VerifiedUtc > 0, "✓ 已验证双向互通 · 端到端加密", "端到端加密 · 等待对方上线完成握手")}")
            PanChatActions.Visibility = Visibility.Visible
            BtnRemark.Visibility = Visibility.Visible
            BtnRemoveFriend.Visibility = Visibility.Visible
            BtnGroupCard.Visibility = Visibility.Collapsed
            BtnInviteToGroup.Visibility = Visibility.Collapsed
            BtnRenameGroup.Visibility = Visibility.Collapsed
            BtnLeaveGroup.Visibility = Visibility.Collapsed
        End If

        BtnSendFile.IsEnabled = True
        TextChatInput.IsEnabled = True
        BtnSend.IsEnabled = True
    End Sub

    Private PinnedBarCollapsed As Boolean = False

    Private Sub BtnTogglePinned_Click() Handles BtnTogglePinned.Click
        PinnedBarCollapsed = Not PinnedBarCollapsed
        RefreshPinnedBar(If(CurrentFriendId = "", New List(Of ModFriends.FriendMessage),
                            If(CurrentIsGroup, ModFriends.GetGroupMessages(CurrentFriendId), ModFriends.GetMessages(CurrentFriendId))))
    End Sub

    Private Sub RefreshPinnedBar(Msgs As IList(Of ModFriends.FriendMessage))
        If BorderPinnedBar Is Nothing OrElse PanPinnedChips Is Nothing Then Return
        PanPinnedChips.Children.Clear()
        If String.IsNullOrEmpty(CurrentFriendId) OrElse Msgs Is Nothing OrElse Not Msgs.Any() Then
            BorderPinnedBar.Visibility = Visibility.Collapsed
            Return
        End If

        Dim Ordered = Msgs.OrderByDescending(Function(m) m.Time).ToList()
        Dim LatestInvite = Ordered.FirstOrDefault(Function(m) m.Kind = "invite" AndAlso Not m.FromMe AndAlso Not String.IsNullOrWhiteSpace(m.InviteCode))
        Dim LatestRelay = Ordered.FirstOrDefault(Function(m) m.Kind = "save_relay" AndAlso Not String.IsNullOrWhiteSpace(m.FileUrl))
        Dim LatestSync = Ordered.FirstOrDefault(Function(m) m.Kind = "inst_sync" AndAlso Not String.IsNullOrWhiteSpace(m.ModSyncData))
        Dim LatestFile = Ordered.FirstOrDefault(Function(m) m.Kind = "file" AndAlso Not String.IsNullOrWhiteSpace(m.FileUrl))

        Dim PinnedCount = If(LatestInvite IsNot Nothing, 1, 0) +
                          If(LatestRelay IsNot Nothing, 1, 0) +
                          If(LatestSync IsNot Nothing, 1, 0) +
                          If(LatestFile IsNot Nothing, 1, 0)
        If PinnedCount = 0 Then
            BorderPinnedBar.Visibility = Visibility.Collapsed
            Return
        End If

        BorderPinnedBar.Visibility = Visibility.Visible
        BtnTogglePinned.Text = LocalizationCore.TranslateText(If(PinnedBarCollapsed, "展开", "收起"))
        LabPinnedHeader.Text = LocalizationCore.TranslateText($"置顶重要卡片 ({PinnedCount})")
        PanPinnedChips.Visibility = If(PinnedBarCollapsed, Visibility.Collapsed, Visibility.Visible)
        If PinnedBarCollapsed Then Return

        If LatestInvite IsNot Nothing Then
            Dim CapInvite = LatestInvite
            Dim ChipInvite As New MyButton With {
                .Text = LocalizationCore.TranslateText("最新联机邀请 · 加入"),
                .Height = 24,
                .Padding = New Thickness(9, 0, 9, 0),
                .Margin = New Thickness(0, 2, 6, 2),
                .ColorType = MyButton.ColorState.Highlight
            }
            AddHandler ChipInvite.Click, Sub() PageLinkMain.Join(CapInvite.InviteCode, CapInvite.InviteVerify)
            PanPinnedChips.Children.Add(ChipInvite)
        End If
        If LatestRelay IsNot Nothing Then
            Dim CapRelay = LatestRelay
            Dim WLabel = If(String.IsNullOrWhiteSpace(CapRelay.WorldName), CapRelay.FileName, CapRelay.WorldName)
            Dim ChipRelay As New MyButton With {
                .Text = LocalizationCore.TranslateText("接力存档: ") & WLabel,
                .Height = 24,
                .Padding = New Thickness(9, 0, 9, 0),
                .Margin = New Thickness(0, 2, 6, 2)
            }
            AddHandler ChipRelay.Click, Sub() ApplySaveRelayFromMessage(CapRelay, ChipRelay, ChipRelay.Text)
            PanPinnedChips.Children.Add(ChipRelay)
        End If
        If LatestSync IsNot Nothing Then
            Dim CapSync = LatestSync
            Dim ChipSync As New MyButton With {
                .Text = LocalizationCore.TranslateText("实例同步: ") & CapSync.SyncChannel,
                .Height = 24,
                .Padding = New Thickness(9, 0, 9, 0),
                .Margin = New Thickness(0, 2, 6, 2)
            }
            AddHandler ChipSync.Click,
                Sub()
                    Dim ParsedM = ModFriends.DecompressModSyncManifest(CapSync.ModSyncData)
                    If ParsedM IsNot Nothing Then StartFriendModSyncDiff(ParsedM)
                End Sub
            PanPinnedChips.Children.Add(ChipSync)
        End If
        If LatestFile IsNot Nothing Then
            Dim CapFile = LatestFile
            Dim ChipFile As New MyButton With {
                .Text = LocalizationCore.TranslateText("最新文件: ") & CapFile.FileName,
                .Height = 24,
                .Padding = New Thickness(9, 0, 9, 0),
                .Margin = New Thickness(0, 2, 6, 2)
            }
            AddHandler ChipFile.Click, Sub() PromptAndDownloadEncryptedFile(CapFile.FileName, CapFile.FileUrl, CapFile.FileKey, CapFile.FileSha256, ChipFile, ChipFile.Text)
            PanPinnedChips.Children.Add(ChipFile)
        End If
    End Sub

    Private Sub RenderMessages(Optional ForceScroll As Boolean = False)
        UpdateChatHeader()
        If BorderPinnedBar IsNot Nothing AndAlso CurrentFriendId = "" Then BorderPinnedBar.Visibility = Visibility.Collapsed

        Dim ShouldScroll As Boolean = ForceScroll OrElse (PanBack.ScrollableHeight - PanBack.VerticalOffset <= 80)
        PanMessages.Children.Clear()

        If CurrentFriendId = "" Then
            LabEmptyHeading.Text = LocalizationCore.TranslateText("选择一位好友或群聊开始沟通")
            LabEmptySub.Text = LocalizationCore.TranslateText("支持私聊与多人群聊、端到端加密大文件传输与离线取件码、启动 PCL Linking 自动实例同步与联机邀请。")
            PanNoChat.Visibility = Visibility.Visible
            Return
        End If

        Dim List = If(CurrentIsGroup, ModFriends.GetGroupMessages(CurrentFriendId), ModFriends.GetMessages(CurrentFriendId))
        RefreshPinnedBar(List)
        If List.Count = 0 Then
            LabEmptyHeading.Text = LocalizationCore.TranslateText("还没有聊天记录")
            LabEmptySub.Text = LocalizationCore.TranslateText(If(CurrentIsGroup,
                "发一句欢迎语，点击右上角「拉好友」邀请好友入群，或点击「实例同步」发布同步频道。",
                "在下方输入消息（支持直接拖入文件/Ctrl+V 截图），或使用「发联机邀请」「实例同步」。"))
            PanNoChat.Visibility = Visibility.Visible
            Return
        End If

        PanNoChat.Visibility = Visibility.Collapsed
        Dim LastTime As Long = 0

        For Each M In List
            If LastTime = 0 OrElse Math.Abs(M.Time - LastTime) >= 300 Then
                Dim TimeLab As New TextBlock With {
                    .Text = M.TimeText,
                    .FontSize = 11,
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .Margin = New Thickness(0, 8, 0, 8)
                }
                TimeLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                PanMessages.Children.Add(TimeLab)
            End If
            LastTime = M.Time

            If CurrentIsGroup AndAlso Not M.FromMe AndAlso Not String.IsNullOrWhiteSpace(M.SenderName) Then
                Dim SenderCap As New TextBlock With {
                    .Text = M.SenderName,
                    .FontSize = 11,
                    .FontWeight = FontWeights.SemiBold,
                    .HorizontalAlignment = HorizontalAlignment.Left,
                    .Margin = New Thickness(4, 2, 0, 3)
                }
                SenderCap.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                PanMessages.Children.Add(SenderCap)
            End If

            Select Case M.Kind
                Case "invite", "modsync"
                    PanMessages.Children.Add(BuildInviteBubble(M))
                Case "file"
                    PanMessages.Children.Add(BuildFileBubble(M))
                Case "save_relay"
                    PanMessages.Children.Add(BuildSaveRelayBubble(M))
                Case "group_invite"
                    PanMessages.Children.Add(BuildGroupInviteBubble(M))
                Case "inst_sync"
                    PanMessages.Children.Add(BuildInstSyncBubble(M))
                Case Else
                    PanMessages.Children.Add(BuildTextBubble(M))
            End Select
        Next

        If ShouldScroll Then PanBack.ScrollToEnd()
    End Sub

    Private Const LogoCopyMessage As String = "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z"

    Private Shared Function CreateCopyMenu(FullText As String, Optional TargetTextBox As TextBox = Nothing, Optional CopyAllLabel As String = "复制整条消息") As ContextMenu
        Dim Menu As New ContextMenu
        If TargetTextBox IsNot Nothing Then
            Dim ItemSel As New MenuItem With {.Header = "复制选中内容"}
            AddHandler ItemSel.Click, Sub()
                                          Dim Sel = TargetTextBox.SelectedText
                                          If Not String.IsNullOrEmpty(Sel) Then
                                              ClipboardSet(Sel, False)
                                              Hint("已复制选中内容！", HintType.Green)
                                          End If
                                      End Sub
            Menu.Items.Add(ItemSel)

            Dim ItemSelectAll As New MenuItem With {.Header = "全选"}
            AddHandler ItemSelectAll.Click, Sub() TargetTextBox.SelectAll()
            Menu.Items.Add(ItemSelectAll)

            AddHandler Menu.Opened, Sub()
                                        ItemSel.IsEnabled = Not String.IsNullOrEmpty(TargetTextBox.SelectedText)
                                    End Sub
        End If
        Dim ItemFull As New MenuItem With {.Header = CopyAllLabel}
        AddHandler ItemFull.Click, Sub()
                                       ClipboardSet(FullText, False)
                                       Hint("已复制消息内容！", HintType.Green)
                                   End Sub
        Menu.Items.Add(ItemFull)
        Return Menu
    End Function

    Private Function BuildTextBubble(M As ModFriends.FriendMessage) As FrameworkElement
        Dim MsgText = If(M.Text, "")
        Dim Bubble As New Border With {
            .Padding = New Thickness(12, 8, 12, 8),
            .MaxWidth = 420,
            .CornerRadius = If(M.FromMe, New CornerRadius(12, 4, 12, 12), New CornerRadius(4, 12, 12, 12)),
            .BorderThickness = New Thickness(1)
        }
        If M.FromMe Then
            Bubble.SetResourceReference(Border.BackgroundProperty, "ColorBrush7")
            Bubble.SetResourceReference(Border.BorderBrushProperty, "ColorBrush5")
        Else
            Bubble.SetResourceReference(Border.BackgroundProperty, "ColorBrushSurface")
            Bubble.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5")
        End If

        Dim Txt As New TextBox With {
            .Text = MsgText,
            .IsReadOnly = True,
            .IsReadOnlyCaretVisible = False,
            .TextWrapping = TextWrapping.Wrap,
            .FontSize = 13,
            .BorderThickness = New Thickness(0),
            .Background = Brushes.Transparent,
            .Padding = New Thickness(0),
            .Margin = New Thickness(0),
            .IsTabStop = False,
            .VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .Cursor = Cursors.IBeam
        }
        TextBlock.SetLineHeight(Txt, 19)
        Txt.SetResourceReference(TextBox.ForegroundProperty, "ColorBrush1")
        Dim Ctx = CreateCopyMenu(MsgText, Txt)
        Txt.ContextMenu = Ctx
        AddHandler Txt.PreviewMouseWheel,
            Sub(sender As Object, e As MouseWheelEventArgs)
                e.Handled = True
                Dim ReRaised As New MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) With {
                    .RoutedEvent = UIElement.MouseWheelEvent,
                    .Source = Bubble
                }
                Bubble.RaiseEvent(ReRaised)
            End Sub
        Bubble.ContextMenu = Ctx

        Dim ExtractedCode As String = ""
        Dim ExtractedVerify As String = ""
        If TryExtractInviteFromText(MsgText, ExtractedCode, ExtractedVerify) Then
            Dim Sp As New StackPanel()
            Sp.Children.Add(Txt)
            If Not M.FromMe Then
                Dim Bar As New WrapPanel With {.Margin = New Thickness(0, 8, 0, 0)}
                Dim CaptureCode = ExtractedCode, CaptureVerify = ExtractedVerify
                Dim BtnJoinQuick As New MyButton With {
                    .Text = LocalizationCore.TranslateText("一键加入联机房间"),
                    .Height = 26,
                    .Padding = New Thickness(10, 0, 10, 0),
                    .Margin = New Thickness(0, 0, 6, 2),
                    .ColorType = MyButton.ColorState.Highlight
                }
                AddHandler BtnJoinQuick.Click, Sub() PageLinkMain.Join(CaptureCode, CaptureVerify)
                Bar.Children.Add(BtnJoinQuick)

                Dim BtnJoinPlay As New MyButton With {
                    .Text = LocalizationCore.TranslateText("加入并直连游戏"),
                    .Height = 26,
                    .Padding = New Thickness(10, 0, 10, 0),
                    .Margin = New Thickness(0, 0, 0, 2)
                }
                AddHandler BtnJoinPlay.Click, Sub() PageLinkMain.Join(CaptureCode, CaptureVerify, AutoLaunchGame:=True)
                Bar.Children.Add(BtnJoinPlay)
                Sp.Children.Add(Bar)
            Else
                Dim SentHint As New TextBlock With {
                    .Text = LocalizationCore.TranslateText("已附带联机邀请 · 好友聊天框将显示一键加入按钮"),
                    .FontSize = 11,
                    .Margin = New Thickness(0, 5, 0, 0)
                }
                SentHint.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                Sp.Children.Add(SentHint)
            End If
            Bubble.Child = Sp
        Else
            Bubble.Child = Txt
        End If
        Bubble.ToolTip = LocalizationCore.TranslateText($"发送时间：{M.TimeText}")

        Dim BtnCopy As New MyIconButton With {
            .Width = 22,
            .Height = 22,
            .Theme = MyIconButton.Themes.Black,
            .LogoScale = 0.78,
            .Logo = LogoCopyMessage,
            .ToolTip = LocalizationCore.TranslateText("复制消息"),
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = If(M.FromMe, New Thickness(0, 0, 6, 0), New Thickness(6, 0, 0, 0)),
            .Opacity = 0.35
        }
        AddHandler BtnCopy.Click, Sub()
                                      ClipboardSet(MsgText, False)
                                      Hint("已复制消息内容！", HintType.Green)
                                  End Sub

        Dim Row As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .Margin = New Thickness(0, 0, 0, 8),
            .HorizontalAlignment = If(M.FromMe, HorizontalAlignment.Right, HorizontalAlignment.Left),
            .Background = Brushes.Transparent
        }
        AddHandler Row.MouseEnter, Sub() BtnCopy.Opacity = 0.9
        AddHandler Row.MouseLeave, Sub() BtnCopy.Opacity = 0.35
        If M.FromMe Then
            If M.SendFailed Then Row.Children.Add(BuildRetryButton(M))
            Row.Children.Add(BtnCopy)
            Row.Children.Add(Bubble)
        Else
            Row.Children.Add(Bubble)
            Row.Children.Add(BtnCopy)
        End If
        Return Row
    End Function

    Private Function BuildRetryButton(M As ModFriends.FriendMessage) As MyButton
        Dim BtnRetry As New MyButton With {
            .Text = LocalizationCore.TranslateText("⚠ 重发"),
            .Height = 24,
            .Padding = New Thickness(8, 0, 8, 0),
            .Margin = New Thickness(0, 0, 6, 0),
            .ColorType = MyButton.ColorState.Red,
            .VerticalAlignment = VerticalAlignment.Center,
            .ToolTip = LocalizationCore.TranslateText(If(String.IsNullOrWhiteSpace(M.SendError), "点击重新发送此消息", $"发送失败：{M.SendError}（点击重试）"))
        }
        Dim MsgId = M.Id, IsGrp = M.IsGroup
        AddHandler BtnRetry.Click,
            Sub()
                BtnRetry.IsEnabled = False
                RunInNewThread(
                    Sub()
                        Dim Err = ModFriends.RetryMessage(MsgId, IsGrp)
                        RunInUi(
                            Sub()
                                If Err IsNot Nothing Then
                                    BtnRetry.IsEnabled = True
                                    Hint(Err, HintType.Red)
                                Else
                                    Hint("消息已重发成功！", HintType.Green)
                                End If
                            End Sub)
                    End Sub, "Retry Friend Message")
            End Sub
        Return BtnRetry
    End Function

    Private Shared Function FormatByteSizeText(SizeBytes As Long) As String
        If SizeBytes >= 1073741824L Then Return $"{SizeBytes / 1073741824.0:0.00} GB"
        If SizeBytes >= 1048576L Then Return $"{SizeBytes / 1048576.0:0.00} MB"
        Return $"{Math.Max(1.0, SizeBytes / 1024.0):0.0} KB"
    End Function

    Private Sub PromptAndDownloadEncryptedFile(FileName As String, FileUrl As String, KeyB64 As String, Sha256Hex As String,
                                               TriggerBtn As MyButton, IdleText As String)
        Dim RawFileName = If(String.IsNullOrWhiteSpace(FileName), "pcl_linking_file.bin", FileName)
        Dim SafeFileName = IO.Path.GetFileName(RawFileName)
        For Each BadCh In IO.Path.GetInvalidFileNameChars()
            SafeFileName = SafeFileName.Replace(BadCh, "_"c)
        Next
        If String.IsNullOrWhiteSpace(SafeFileName) OrElse SafeFileName = "." OrElse SafeFileName = ".." Then SafeFileName = "pcl_linking_file.bin"
        Dim Dlg As New Microsoft.Win32.SaveFileDialog With {
            .FileName = SafeFileName,
            .Title = LocalizationCore.TranslateText("保存解密后的好友文件")
        }
        Try
            If Dlg.ShowDialog() <> True Then Return
        Catch exDlg As Exception
            Hint($"打开保存对话框失败：{exDlg.Message}", HintType.Red)
            Return
        End Try
        Dim SavePath = Dlg.FileName
        TriggerBtn.IsEnabled = False
        TriggerBtn.Text = LocalizationCore.TranslateText("解密下载中…")
        RunInNewThread(
            Sub()
                Dim Err = ModFriends.DownloadAndDecryptFileToPath(
                    FileUrl, KeyB64, Sha256Hex, SavePath,
                    Sub(Stage, Cur, Total)
                        RunInUi(
                            Sub()
                                If Total > 1 Then
                                    TriggerBtn.Text = LocalizationCore.TranslateText("下载") & $" {Cur}/{Total}…"
                                Else
                                    TriggerBtn.Text = LocalizationCore.TranslateText("解密下载中…")
                                End If
                            End Sub)
                    End Sub)
                RunInUi(
                    Sub()
                        TriggerBtn.IsEnabled = True
                        TriggerBtn.Text = LocalizationCore.TranslateText(IdleText)
                        If Err IsNot Nothing Then
                            Hint(Err, HintType.Red)
                        Else
                            Hint($"文件已解密保存至：{SavePath}", HintType.Green)
                            If MyMsgBox($"文件已成功解密并保存至：{vbCrLf}{SavePath}", "下载完成", "打开所在文件夹", "关闭") = 1 Then
                                OpenExplorer(IO.Path.GetDirectoryName(SavePath))
                            End If
                        End If
                    End Sub)
            End Sub, "Download Encrypted Friend File")
    End Sub

    Private Function BuildFileBubble(M As ModFriends.FriendMessage) As FrameworkElement
        Dim SizeText = FormatByteSizeText(M.FileSize)
        Dim Card As New Border With {
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 2, 0, 10),
            .Width = 318,
            .CornerRadius = New CornerRadius(8),
            .BorderThickness = New Thickness(1)
        }
        Card.SetResourceReference(Border.BackgroundProperty, If(M.FromMe, "ColorBrush7", "ColorBrushSurface"))
        Card.SetResourceReference(Border.BorderBrushProperty, "ColorBrush3")

        Dim Stack As New StackPanel()
        Dim Header As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim TagBadge As New Border With {.CornerRadius = New CornerRadius(4), .Padding = New Thickness(6, 2, 6, 2), .Margin = New Thickness(0, 0, 7, 0)}
        TagBadge.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
        TagBadge.Child = New TextBlock With {.Text = LocalizationCore.TranslateText("加密文件"), .Foreground = Brushes.White, .FontSize = 10.5, .FontWeight = FontWeights.Bold}
        Header.Children.Add(TagBadge)
        Dim NameTb As New TextBlock With {.Text = If(String.IsNullOrWhiteSpace(M.FileName), LocalizationCore.TranslateText("未知文件"), M.FileName), .FontWeight = FontWeights.Bold, .FontSize = 13, .TextTrimming = TextTrimming.CharacterEllipsis, .MaxWidth = 220}
        NameTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Header.Children.Add(NameTb)
        Stack.Children.Add(Header)

        Dim IsPixeldrain = Not String.IsNullOrWhiteSpace(M.FileUrl) AndAlso M.FileUrl.IndexOf("pixeldrain.com", StringComparison.OrdinalIgnoreCase) >= 0
        Dim IsS3 = Not String.IsNullOrWhiteSpace(M.FileUrl) AndAlso M.FileUrl.IndexOf("X-Amz-Signature", StringComparison.OrdinalIgnoreCase) >= 0
        Dim IsManifest = Not String.IsNullOrWhiteSpace(M.FileUrl) AndAlso M.FileUrl.StartsWith(FriendsCryptoProtocol.ManifestUrlPrefix, StringComparison.OrdinalIgnoreCase)
        Dim StorageNoteKey = If(IsPixeldrain,
                                "AES-256 端到端加密（Pixeldrain 长期托管 120 天）",
                                If(IsS3, "AES-256 端到端加密（S3 预签名云端存储）",
                                   If(IsManifest, "AES-256 分片端到端加密（支持断点续传）", "AES-256 端到端加密")))
        Dim DescTb As New TextBlock With {
            .Text = LocalizationCore.TranslateText("大小：") & SizeText & " · " & LocalizationCore.TranslateText(StorageNoteKey),
            .FontSize = 11.5,
            .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(0, 2, 0, 8)
        }
        DescTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Stack.Children.Add(DescTb)

        If Not String.IsNullOrWhiteSpace(M.FileUrl) Then
            Dim CapUrl = M.FileUrl, CapKey = M.FileKey, CapSha = M.FileSha256, CapName = M.FileName, CapSize = M.FileSize
            Dim CapTarget = M.PeerId, CapIsGrp = M.IsGroup
            Dim BtnRow As New WrapPanel With {.Orientation = Orientation.Horizontal}

            Dim BtnSave As New MyButton With {
                .Text = LocalizationCore.TranslateText("解密并下载保存"),
                .Height = 28,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 2, 6, 2),
                .ColorType = MyButton.ColorState.Highlight
            }
            AddHandler BtnSave.Click,
                Sub()
                    PromptAndDownloadEncryptedFile(CapName, CapUrl, CapKey, CapSha, BtnSave, "解密并下载保存")
                End Sub
            BtnRow.Children.Add(BtnSave)

            If Not String.IsNullOrWhiteSpace(CapName) AndAlso CapName.StartsWith("EnvPack_", StringComparison.OrdinalIgnoreCase) AndAlso CapName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
                Dim BtnApplyEnv As New MyButton With {
                    .Text = LocalizationCore.TranslateText("应用环境包到当前实例"),
                    .Height = 28,
                    .Padding = New Thickness(10, 0, 10, 0),
                    .Margin = New Thickness(0, 2, 6, 2),
                    .ToolTip = LocalizationCore.TranslateText("解密下载并一键解压覆写当前选中实例的 mods/config 等环境目录（覆盖 config 前自动备份）")
                }
                AddHandler BtnApplyEnv.Click,
                    Sub()
                        Dim InstSnap = McInstanceSelected
                        If InstSnap Is Nothing Then
                            Hint("请先在主界面选择一个本地游戏实例！", HintType.Red)
                            Return
                        End If
                        If MyMsgBox($"即将解密下载环境快照「{CapName}」({SizeText}) 并覆盖写入实例「{InstSnap.Name}」的对应目录。{vbCrLf}覆盖 config 前会自动备份至 EnvPackBackups\。确定继续吗？",
                                    "一键应用整合包环境快照", "下载并应用", "取消") <> 1 Then Return
                        BtnApplyEnv.IsEnabled = False
                        BtnApplyEnv.Text = LocalizationCore.TranslateText("下载应用中…")
                        Dim CacheKey = If(String.IsNullOrWhiteSpace(CapSha), Guid.NewGuid().ToString("N"), CapSha).Substring(0, 12)
                        Dim CacheZip = IO.Path.Combine(PathUtils.RemoveExtendedPrefix(PathTemp), "Cache", $"ApplyEnv_{CacheKey}.zip")
                        RunInNewThread(
                            Sub()
                                Dim Err = ModFriends.DownloadAndDecryptFileToPath(
                                    CapUrl, CapKey, CapSha, CacheZip,
                                    Sub(St, Cur, Tot)
                                        RunInUi(Sub() BtnApplyEnv.Text = If(Tot > 1, $"{LocalizationCore.TranslateText("下载")} {Cur}/{Tot}", LocalizationCore.TranslateText("下载应用中…")))
                                    End Sub)
                                Dim ExtractedCount As Integer = 0
                                Dim BackupNote As String = ""
                                If Err Is Nothing Then
                                    Err = ModInstanceSync.ApplyEnvironmentPackZip(InstSnap, CacheZip, ExtractedCount, BackupNote)
                                End If
                                Try
                                    If IO.File.Exists(CacheZip) Then IO.File.Delete(CacheZip)
                                Catch
                                End Try
                                RunInUi(
                                    Sub()
                                        BtnApplyEnv.IsEnabled = True
                                        BtnApplyEnv.Text = LocalizationCore.TranslateText("应用环境包到当前实例")
                                        If Err IsNot Nothing Then
                                            Hint(Err, HintType.Red)
                                        Else
                                            Dim NoteStr = If(String.IsNullOrEmpty(BackupNote), "", $"（{BackupNote}）")
                                            Hint($"环境快照应用成功！已写入 {ExtractedCount} 个文件{NoteStr}", HintType.Green)
                                        End If
                                    End Sub)
                            End Sub, "Download & Apply Env Pack")
                    End Sub
                BtnRow.Children.Add(BtnApplyEnv)
            End If

            Dim BtnCopyCode As New MyButton With {
                .Text = LocalizationCore.TranslateText("复制取件码"),
                .Height = 28,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 2, 6, 2),
                .ToolTip = LocalizationCore.TranslateText("生成离线加密取件码；即使好友超过 12 小时上线收不到卡片，也能通过取件码随时下载")
            }
            AddHandler BtnCopyCode.Click,
                Sub()
                    Dim Code = ModFriends.BuildFilePickupCode(CapName, CapSize, CapUrl, CapKey, CapSha)
                    ClipboardSet(Code, False)
                    Hint("已复制离线加密取件码！可通过 QQ/微信发给好友，在好友页点「取件码」随时解密下载。", HintType.Green)
                End Sub
            BtnRow.Children.Add(BtnCopyCode)

            If M.FromMe Then
                Dim BtnResendCard As New MyButton With {
                    .Text = LocalizationCore.TranslateText("重发卡片"),
                    .Height = 28,
                    .Padding = New Thickness(10, 0, 10, 0),
                    .Margin = New Thickness(0, 2, 0, 2),
                    .ToolTip = LocalizationCore.TranslateText("无需重新上传密文，直接向当前会话再次推送此文件卡片")
                }
                AddHandler BtnResendCard.Click,
                    Sub()
                        BtnResendCard.IsEnabled = False
                        BtnResendCard.Text = LocalizationCore.TranslateText("推送中…")
                        RunInNewThread(
                            Sub()
                                Dim NewMsg As New ModFriends.FriendMessage With {
                                    .Kind = "file",
                                    .Text = $"发送了文件：{CapName}",
                                    .FileName = CapName,
                                    .FileSize = CapSize,
                                    .FileUrl = CapUrl,
                                    .FileKey = CapKey,
                                    .FileSha256 = CapSha
                                }
                                Dim Err = ModFriends.SendRichMessage(CapTarget, CapIsGrp, NewMsg)
                                RunInUi(
                                    Sub()
                                        BtnResendCard.IsEnabled = True
                                        BtnResendCard.Text = LocalizationCore.TranslateText("重发卡片")
                                        If Err IsNot Nothing Then
                                            Hint(Err, HintType.Red)
                                        Else
                                            Hint("已重新向会话推送该文件卡片！", HintType.Green)
                                        End If
                                    End Sub)
                            End Sub, "Resend Encrypted File Card")
                    End Sub
                BtnRow.Children.Add(BtnResendCard)
            End If

            Stack.Children.Add(BtnRow)
        End If
        Card.Child = Stack

        Dim Outer As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = If(M.FromMe, HorizontalAlignment.Right, HorizontalAlignment.Left)}
        If M.FromMe AndAlso M.SendFailed Then Outer.Children.Add(BuildRetryButton(M))
        Outer.Children.Add(Card)
        Return Outer
    End Function

    Private Sub ApplySaveRelayFromMessage(M As ModFriends.FriendMessage, TriggerBtn As MyButton, IdleText As String)
        If M Is Nothing OrElse String.IsNullOrWhiteSpace(M.FileUrl) Then Return
        Dim TargetInst = McInstanceSelected
        If TargetInst Is Nothing Then
            Hint("请先在左侧/主界面选中一个游戏实例来接收接力存档！", HintType.Red)
            Return
        End If
        Dim TargetWorldName = If(String.IsNullOrWhiteSpace(M.WorldName), IO.Path.GetFileNameWithoutExtension(If(M.FileName, "RelayWorld")), M.WorldName)
        Dim SizeText = FormatByteSizeText(M.FileSize)
        If MyMsgBox($"即将接收「{TargetWorldName}」（{SizeText}）并写入实例「{TargetInst.Name}」的 saves\ 目录。{vbCrLf}若本地已有同名存档，将先自动滚动备份至 SaveRelayBackups\ 防丢失。是否继续？",
                    "接力导入好友存档", "下载并导入接力存档", "取消") <> 1 Then Return
        TriggerBtn.IsEnabled = False
        TriggerBtn.Text = LocalizationCore.TranslateText("导入中…")
        Dim RelayKey = If(String.IsNullOrWhiteSpace(M.FileSha256), Guid.NewGuid().ToString("N"), M.FileSha256).Substring(0, 12)
        Dim TempZip = IO.Path.Combine(PathUtils.RemoveExtendedPrefix(PathTemp), "Cache", $"RecvRelay_{RelayKey}.zip")
        RunInNewThread(
            Sub()
                Dim Err = ModFriends.DownloadAndDecryptFileToPath(
                    M.FileUrl, M.FileKey, M.FileSha256, TempZip,
                    Sub(St, Cur, Tot)
                        RunInUi(Sub() TriggerBtn.Text = If(Tot > 1, $"{LocalizationCore.TranslateText("下载")} {Cur}/{Tot}", LocalizationCore.TranslateText("导入中…")))
                    End Sub)
                Dim FinalWorldDir As String = "", BackupNote As String = ""
                If Err Is Nothing Then
                    Err = ModInstanceSync.ApplySaveRelayZipToInstance(TargetInst, TempZip, TargetWorldName, FinalWorldDir, BackupNote)
                End If
                Try
                    If IO.File.Exists(TempZip) Then IO.File.Delete(TempZip)
                Catch
                End Try
                RunInUi(
                    Sub()
                        TriggerBtn.IsEnabled = True
                        TriggerBtn.Text = LocalizationCore.TranslateText(IdleText)
                        If Err IsNot Nothing Then
                            Hint(Err, HintType.Red)
                        Else
                            Dim NoteStr = If(String.IsNullOrEmpty(BackupNote), "", vbCrLf & BackupNote)
                            Hint($"接力存档已安全导入至 {IO.Path.GetFileName(FinalWorldDir)}！", HintType.Green)
                            If MyMsgBox($"接力存档「{IO.Path.GetFileName(FinalWorldDir)}」已导入到实例「{TargetInst.Name}」并完成 SHA-256 校验！{NoteStr}{vbCrLf}{vbCrLf}进入单人游戏即可打开该世界并对局域网开放接力当房主。",
                                        "存档接力就绪", "查看存档文件夹", "完成") = 1 Then
                                OpenExplorer(FinalWorldDir)
                            End If
                        End If
                    End Sub)
            End Sub, "Import Save Relay")
    End Sub

    Private Function BuildSaveRelayBubble(M As ModFriends.FriendMessage) As FrameworkElement
        Dim SizeText = FormatByteSizeText(M.FileSize)
        Dim WorldTitle = If(String.IsNullOrWhiteSpace(M.WorldName), M.FileName, M.WorldName)
        Dim Card As New Border With {
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 2, 0, 10),
            .Width = 318,
            .CornerRadius = New CornerRadius(8),
            .BorderThickness = New Thickness(1)
        }
        Card.SetResourceReference(Border.BackgroundProperty, If(M.FromMe, "ColorBrush7", "ColorBrushSurface"))
        Card.SetResourceReference(Border.BorderBrushProperty, "ColorBrush3")

        Dim Stack As New StackPanel()
        Dim Header As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim TagBadge As New Border With {.CornerRadius = New CornerRadius(4), .Padding = New Thickness(6, 2, 6, 2), .Margin = New Thickness(0, 0, 7, 0), .Background = New SolidColorBrush(Color.FromRgb(46, 154, 106))}
        TagBadge.Child = New TextBlock With {.Text = LocalizationCore.TranslateText("存档接力"), .Foreground = Brushes.White, .FontSize = 10.5, .FontWeight = FontWeights.Bold}
        Header.Children.Add(TagBadge)
        Dim NameTb As New TextBlock With {.Text = WorldTitle, .FontWeight = FontWeights.Bold, .FontSize = 13, .TextTrimming = TextTrimming.CharacterEllipsis, .MaxWidth = 220}
        NameTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Header.Children.Add(NameTb)
        Stack.Children.Add(Header)

        Dim DescTb As New TextBlock With {
            .Text = LocalizationCore.TranslateText("大小：") & SizeText & " · " & LocalizationCore.TranslateText("轮值主机共享存档（导入自动备份本地同名世界）"),
            .FontSize = 11.5,
            .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(0, 2, 0, 8)
        }
        DescTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Stack.Children.Add(DescTb)

        If Not String.IsNullOrWhiteSpace(M.FileUrl) Then
            Dim BtnRow As New WrapPanel With {.Orientation = Orientation.Horizontal}
            Dim BtnImport As New MyButton With {
                .Text = LocalizationCore.TranslateText("接力导入到当前实例"),
                .Height = 28,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 2, 6, 2),
                .ColorType = MyButton.ColorState.Highlight
            }
            AddHandler BtnImport.Click, Sub() ApplySaveRelayFromMessage(M, BtnImport, "接力导入到当前实例")
            BtnRow.Children.Add(BtnImport)

            Dim BtnCode As New MyButton With {
                .Text = LocalizationCore.TranslateText("复制取件码"),
                .Height = 28,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 2, 0, 2)
            }
            AddHandler BtnCode.Click,
                Sub()
                    ClipboardSet(ModFriends.BuildFilePickupCode(M.FileName, M.FileSize, M.FileUrl, M.FileKey, M.FileSha256), False)
                    Hint("已复制存档接力取件码！", HintType.Green)
                End Sub
            BtnRow.Children.Add(BtnCode)
            Stack.Children.Add(BtnRow)
        End If
        Card.Child = Stack

        Dim Outer As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = If(M.FromMe, HorizontalAlignment.Right, HorizontalAlignment.Left)}
        If M.FromMe AndAlso M.SendFailed Then Outer.Children.Add(BuildRetryButton(M))
        Outer.Children.Add(Card)
        Return Outer
    End Function

    Private Function BuildGroupInviteBubble(M As ModFriends.FriendMessage) As FrameworkElement
        Dim Card As New Border With {
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 2, 0, 10),
            .Width = 286,
            .CornerRadius = New CornerRadius(8),
            .BorderThickness = New Thickness(1)
        }
        Card.SetResourceReference(Border.BackgroundProperty, If(M.FromMe, "ColorBrush7", "ColorBrushSurface"))
        Card.SetResourceReference(Border.BorderBrushProperty, "ColorBrush3")

        Dim Stack As New StackPanel()
        Dim TitleTb As New TextBlock With {.Text = $"[群聊邀请] {M.GroupInviteName}", .FontWeight = FontWeights.Bold, .FontSize = 13, .TextWrapping = TextWrapping.Wrap}
        TitleTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Stack.Children.Add(TitleTb)
        Dim SubTb As New TextBlock With {.Text = $"群号：{M.GroupInviteId}", .FontSize = 11.5, .Margin = New Thickness(0, 4, 0, 8)}
        SubTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Stack.Children.Add(SubTb)

        If Not M.FromMe AndAlso Not String.IsNullOrWhiteSpace(M.GroupInviteId) Then
            Dim BtnJoinGrp As New MyButton With {.Text = "一键加入群聊", .Height = 28, .Padding = New Thickness(12, 0, 12, 0), .ColorType = MyButton.ColorState.Highlight, .HorizontalAlignment = HorizontalAlignment.Left}
            Dim GId = M.GroupInviteId, GSec = M.GroupInviteSecret, GName = M.GroupInviteName
            AddHandler BtnJoinGrp.Click,
                Sub()
                    Dim Err = ModFriends.JoinGroup(GId, GSec, GName)
                    If Err IsNot Nothing Then
                        Hint(Err, HintType.Red)
                    Else
                        SelectConversation(GId, True)
                        Hint($"已加入群聊「{GName}」！", HintType.Green)
                    End If
                End Sub
            Stack.Children.Add(BtnJoinGrp)
        End If
        Card.Child = Stack
        Dim Outer As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = If(M.FromMe, HorizontalAlignment.Right, HorizontalAlignment.Left)}
        If M.FromMe AndAlso M.SendFailed Then Outer.Children.Add(BuildRetryButton(M))
        Outer.Children.Add(Card)
        Return Outer
    End Function

    Private Function BuildInstSyncBubble(M As ModFriends.FriendMessage) As FrameworkElement
        Dim Manifest = If(String.IsNullOrWhiteSpace(M.ModSyncData), Nothing, ModFriends.DecompressModSyncManifest(M.ModSyncData))
        Dim Card As New Border With {
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 2, 0, 10),
            .Width = 306,
            .CornerRadius = New CornerRadius(8),
            .BorderThickness = New Thickness(1)
        }
        Card.SetResourceReference(Border.BackgroundProperty, If(M.FromMe, "ColorBrush7", "ColorBrushSurface"))
        Card.SetResourceReference(Border.BorderBrushProperty, "ColorBrush3")

        Dim Stack As New StackPanel()
        Dim Head As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim TagBadge As New Border With {.CornerRadius = New CornerRadius(4), .Padding = New Thickness(6, 2, 6, 2), .Margin = New Thickness(0, 0, 7, 0)}
        TagBadge.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
        TagBadge.Child = New TextBlock With {.Text = "实例同步", .Foreground = Brushes.White, .FontSize = 10.5, .FontWeight = FontWeights.Bold}
        Head.Children.Add(TagBadge)
        Dim ChTb As New TextBlock With {.Text = $"{M.SyncChannel} (r{M.SyncRev})", .FontWeight = FontWeights.Bold, .FontSize = 13, .TextTrimming = TextTrimming.CharacterEllipsis, .MaxWidth = 210}
        ChTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Head.Children.Add(ChTb)
        Stack.Children.Add(Head)

        Dim HasCfg = Not String.IsNullOrWhiteSpace(M.ConfigUrl)
        Dim DescText = If(Manifest IsNot Nothing, $"MC {Manifest.Ver} · {Manifest.Items.Count} 个 Mod{If(HasCfg, " · 附带加密 Config", "")}", M.Text)
        Dim DescTb As New TextBlock With {.Text = DescText, .FontSize = 11.5, .TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 2, 0, 8)}
        DescTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Stack.Children.Add(DescTb)

        If Not M.FromMe AndAlso Manifest IsNot Nothing Then
            Dim Wrap As New WrapPanel()
            Dim BtnBindAuto As New MyButton With {.Text = "绑定当前实例并自动同步", .Height = 27, .Padding = New Thickness(10, 0, 10, 0), .ColorType = MyButton.ColorState.Highlight, .Margin = New Thickness(0, 0, 6, 4)}
            Dim CapMsg = M, CapManifest = Manifest
            AddHandler BtnBindAuto.Click,
                Sub()
                    If McInstanceSelected Is Nothing Then
                        Hint("请先在主界面/版本选择中选中要接收同步的本地游戏实例！", HintType.Red)
                        Return
                    End If
                    Dim PromptChoice = MyMsgBox($"将把本地实例「{McInstanceSelected.Name}」绑定订阅到频道「{CapMsg.SyncChannel}」：每次启动 PCL Linking 将自动检测并对齐差异。{vbCrLf}{vbCrLf}是否同时自动禁用本地多出来的 Mod（重命名为 .disabled，不删除）？",
                                                "设置启动自动同步", "完全对齐（禁用多出 Mod）", "仅补齐缺失与更新", "取消")
                    If PromptChoice = 0 OrElse PromptChoice = 3 Then Return
                    Dim Binding = ModInstanceSync.UpsertSubscriberBinding(
                        CapMsg.PeerId, CapMsg.IsGroup, CapMsg.SenderName, CapMsg.SyncChannel,
                        McInstanceSelected, IncludeConfig:=(Not String.IsNullOrWhiteSpace(CapMsg.ConfigUrl)),
                        DisableExtraMods:=(PromptChoice = 1), AutoOnStartup:=True)
                    Hint($"已绑定实例「{McInstanceSelected.Name}」，正在执行首次同步…", HintType.Blue)
                    RunInNewThread(Sub() ModInstanceSync.SyncSubscriberBindingNow(Binding.Id, IsAutoStartup:=False), "Initial Instance Sync")
                End Sub
            Wrap.Children.Add(BtnBindAuto)

            Dim BtnOnce As New MyButton With {.Text = "仅对比 Mod 一次", .Height = 27, .Padding = New Thickness(10, 0, 10, 0), .Margin = New Thickness(0, 0, 0, 4)}
            AddHandler BtnOnce.Click, Sub() StartFriendModSyncDiff(CapManifest)
            Wrap.Children.Add(BtnOnce)
            Stack.Children.Add(Wrap)
        End If
        Card.Child = Stack

        Dim Outer As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = If(M.FromMe, HorizontalAlignment.Right, HorizontalAlignment.Left)}
        If M.FromMe AndAlso M.SendFailed Then Outer.Children.Add(BuildRetryButton(M))
        Outer.Children.Add(Card)
        Return Outer
    End Function

    Public Shared Function TryExtractInviteFromText(RawText As String, ByRef OutCode As String, ByRef OutVerify As String) As Boolean
        Return EasyTierNetworkCore.TryExtractInviteFromText(RawText, OutCode, OutVerify)
    End Function

    Private Function BuildInviteBubble(M As ModFriends.FriendMessage) As FrameworkElement
        Dim IsModSyncOnly As Boolean = (M.Kind = "modsync")
        Dim Manifest = If(String.IsNullOrWhiteSpace(M.ModSyncData), Nothing, ModFriends.DecompressModSyncManifest(M.ModSyncData))
        Dim Card As New Border With {
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 2, 0, 10),
            .Width = 286,
            .HorizontalAlignment = If(M.FromMe, HorizontalAlignment.Right, HorizontalAlignment.Left),
            .CornerRadius = New CornerRadius(8),
            .BorderThickness = New Thickness(1)
        }
        Card.SetResourceReference(Border.BackgroundProperty, If(M.FromMe, "ColorBrush7", "ColorBrushSurface"))
        Card.SetResourceReference(Border.BorderBrushProperty, "ColorBrush3")

        Dim Stack As New StackPanel
        Dim Header As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 4)}
        Dim TagBadge As New Border With {
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(6, 2, 6, 2),
            .Margin = New Thickness(0, 0, 7, 0),
            .VerticalAlignment = VerticalAlignment.Center
        }
        TagBadge.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
        TagBadge.Child = New TextBlock With {
            .Text = If(IsModSyncOnly, "Mod 同步", "联机邀请"),
            .Foreground = Brushes.White,
            .FontSize = 10.5,
            .FontWeight = FontWeights.Bold
        }
        Header.Children.Add(TagBadge)

        Dim TitleLab As New TextBlock With {
            .Text = If(IsModSyncOnly,
                       If(M.FromMe, "已发送 Mod 清单", "分享了 Mod 清单"),
                       If(M.FromMe, "已邀请好友加入房间", "邀请你加入联机房间")),
            .FontWeight = FontWeights.Bold,
            .FontSize = 13,
            .VerticalAlignment = VerticalAlignment.Center
        }
        TitleLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Header.Children.Add(TitleLab)
        Stack.Children.Add(Header)

        Dim DescText As String
        If IsModSyncOnly Then
            DescText = If(Manifest IsNot Nothing, $"MC {Manifest.Ver} · 共 {Manifest.Items.Count} 个 Mod", "包含实例 Mod 列表")
        Else
            DescText = If(String.IsNullOrWhiteSpace(M.InviteCode), "房间口令已附带在邀请中", $"邀请码前缀：{M.InviteCode.Substring(0, Math.Min(6, M.InviteCode.Length))}…")
            If Manifest IsNot Nothing Then DescText &= $" · 附带 {Manifest.Items.Count} 个 Mod ({Manifest.Ver})"
        End If
        Dim DescLab As New TextBlock With {
            .Text = DescText,
            .FontSize = 11.5,
            .TextWrapping = TextWrapping.Wrap,
            .Margin = New Thickness(0, 2, 0, 8)
        }
        DescLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Stack.Children.Add(DescLab)

        Dim ActionWrap As New WrapPanel
        If Not IsModSyncOnly AndAlso Not M.FromMe Then
            Dim BtnJoin As New MyButton With {
                .Text = "一键加入联机房间",
                .Height = 28,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 0, 6, 4),
                .ColorType = MyButton.ColorState.Highlight
            }
            Dim Code = M.InviteCode, Verify = M.InviteVerify
            AddHandler BtnJoin.Click, Sub() PageLinkMain.Join(Code, Verify)
            ActionWrap.Children.Add(BtnJoin)

            Dim BtnJoinAndPlay As New MyButton With {
                .Text = "加入并直连游戏",
                .Height = 28,
                .Padding = New Thickness(10, 0, 10, 0),
                .Margin = New Thickness(0, 0, 6, 4)
            }
            Dim CapturedManifest = Manifest
            AddHandler BtnJoinAndPlay.Click,
                Sub()
                    If CapturedManifest Is Nothing OrElse McInstanceSelected Is Nothing Then
                        PageLinkMain.Join(Code, Verify, AutoLaunchGame:=True)
                        Return
                    End If
                    Dim InstSnap = McInstanceSelected
                    Hint("正在快速核对实例与房主 Mod 环境……", HintType.Blue)
                    RunInNewThread(
                    Sub()
                        Try
                            Dim Diff = ScanAndCompareHostMods(CapturedManifest, InstSnap)
                            RunInUi(
                            Sub()
                                If Diff.IsFullySynced Then
                                    PageLinkMain.Join(Code, Verify, AutoLaunchGame:=True)
                                Else
                                    Dim Choice = MyMsgBox(
                                        $"检测到当前实例「{InstSnap.Name}」与房主 Mod 清单存在差异（缺失 {Diff.MissingDownloadable.Count}、待启用 {Diff.DisabledMatch.Count}、版本不同 {Diff.MismatchDownloadable.Count + Diff.MismatchLocalOnly.Count}、多出 {Diff.ExtraLocal.Count}）。{vbCrLf}建议先一键对齐 Mod，以免进房失败。",
                                        "进房前 Mod 差异提示",
                                        "先对比并同步 Mod", "直接进房并启动", "取消")
                                    If Choice = 1 Then
                                        StartFriendModSyncDiff(CapturedManifest)
                                    ElseIf Choice = 2 Then
                                        PageLinkMain.Join(Code, Verify, AutoLaunchGame:=True)
                                    End If
                                End If
                            End Sub)
                        Catch
                            RunInUi(Sub() PageLinkMain.Join(Code, Verify, AutoLaunchGame:=True))
                        End Try
                    End Sub, "Pre-Join Mod Diff Check")
                End Sub
            ActionWrap.Children.Add(BtnJoinAndPlay)
        End If
        If Manifest IsNot Nothing Then
            Dim BtnSyncDiff As New MyButton With {
                .Text = "对比 / 同步 Mod",
                .Height = 28,
                .Padding = New Thickness(11, 0, 11, 0),
                .Margin = New Thickness(0, 0, 0, 4),
                .ColorType = If(IsModSyncOnly, MyButton.ColorState.Highlight, MyButton.ColorState.Normal)
            }
            Dim LocalManifest = Manifest
            AddHandler BtnSyncDiff.Click, Sub() StartFriendModSyncDiff(LocalManifest)
            ActionWrap.Children.Add(BtnSyncDiff)
        End If
        If ActionWrap.Children.Count > 0 Then Stack.Children.Add(ActionWrap)

        Card.Child = Stack
        If Not IsModSyncOnly AndAlso Not String.IsNullOrWhiteSpace(M.InviteCode) Then
            Dim InviteCopyText = If(String.IsNullOrWhiteSpace(M.InviteVerify), M.InviteCode, $"邀请码：{M.InviteCode}  验证码：{M.InviteVerify}")
            Card.ContextMenu = CreateCopyMenu(InviteCopyText, Nothing, "复制联机邀请码")
        End If
        If M.FromMe AndAlso M.SendFailed Then
            Dim Outer As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = HorizontalAlignment.Right}
            Outer.Children.Add(BuildRetryButton(M))
            Outer.Children.Add(Card)
            Return Outer
        End If
        Return Card
    End Function

#End Region

#Region "操作按钮"

    Private Sub BtnSend_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnSend.Click
        If Not BtnSend.IsEnabled Then Return
        If CurrentFriendId = "" Then
            Hint("请先在左侧选中好友或群聊！", HintType.Red)
            Return
        End If
        Dim Text As String = If(TextChatInput.Text, "").Trim()
        If Text = "" Then Return
        Dim TargetId As String = CurrentFriendId
        Dim TargetIsGroup As Boolean = CurrentIsGroup
        TextChatInput.Text = ""
        BtnSend.IsEnabled = False
        '网络请求不能在 UI 线程执行（NetRequestByClient 会直接抛异常）
        RunInNewThread(
            Sub()
                Dim Err As String = Nothing
                Try
                    Err = If(TargetIsGroup, ModFriends.SendGroup(TargetId, "text", Text), ModFriends.Send(TargetId, "text", Text))
                Catch ex As Exception
                    Err = "发送失败：" & ex.Message
                Finally
                    RunInUi(
                        Sub()
                            BtnSend.IsEnabled = True
                            If Err IsNot Nothing Then Hint(Err, HintType.Red)
                        End Sub)
                End Try
            End Sub, "PCL Linking 发送好友/群聊消息")
    End Sub

    Private Sub TextChatInput_PreviewKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter AndAlso (Keyboard.Modifiers And ModifierKeys.Shift) = 0 Then
            e.Handled = True
            If BtnSend.IsEnabled Then BtnSend_Click(Nothing, Nothing)
        ElseIf e.Key = Key.V AndAlso (Keyboard.Modifiers And ModifierKeys.Control) <> 0 Then
            TryHandleClipboardMediaPaste(e)
        End If
    End Sub

    Private Sub TryHandleClipboardMediaPaste(e As KeyEventArgs)
        If CurrentFriendId = "" Then Return
        Try
            If Clipboard.ContainsFileDropList() Then
                Dim Files = Clipboard.GetFileDropList()
                If Files IsNot Nothing AndAlso Files.Count > 0 Then
                    Dim FirstFile = Files(0)
                    e.Handled = True
                    SendLocalFileWithConfirm(FirstFile)
                    Return
                End If
            End If
            If Clipboard.ContainsImage() Then
                Dim TargetId = CurrentFriendId, IsGrp = CurrentIsGroup
                Dim Img = Clipboard.GetImage()
                If Img IsNot Nothing Then
                    e.Handled = True
                    Using Ms As New IO.MemoryStream()
                        Dim Encoder As New System.Windows.Media.Imaging.PngBitmapEncoder()
                        Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(Img))
                        Encoder.Save(Ms)
                        Dim Bytes = Ms.ToArray()
                        Dim Kb = Math.Max(1.0, Bytes.Length / 1024.0)
                        If MyMsgBox($"检测到剪贴板截图（约 {Kb:0.0} KB），是否加密发送给当前会话？", "发送剪贴板截图", "加密发送", "取消") = 1 Then
                            SendFileBytesAsync(TargetId, IsGrp, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png", Bytes)
                        End If
                    End Using
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取剪贴板文件/截图失败")
        End Try
    End Sub

    Private Sub ChatArea_PreviewDragOver(sender As Object, e As DragEventArgs)
        If CurrentFriendId <> "" AndAlso e.Data.GetDataPresent(DataFormats.FileDrop) Then
            e.Effects = DragDropEffects.Copy
        Else
            e.Effects = DragDropEffects.None
        End If
        e.Handled = True
    End Sub

    Private Sub ChatArea_Drop(sender As Object, e As DragEventArgs)
        If CurrentFriendId = "" OrElse Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
        e.Handled = True
        Dim Files = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
        If Files IsNot Nothing AndAlso Files.Length > 0 Then
            SendLocalFileWithConfirm(Files(0))
        End If
    End Sub

    Private Sub BtnPickupFile_Click() Handles BtnPickupFile.Click
        If Not BtnPickupFile.IsEnabled Then Return
        Dim CodeInput = MyMsgBoxInput("通过离线加密取件码下载",
            $"粘贴好友发送的取件码（以 {FriendsCryptoProtocol.PickupCodePrefix} 开头），即可在好友离线后随时从云端拉取并端到端解密保存文件：",
            HintText:=FriendsCryptoProtocol.PickupCodePrefix & "...")
        If CodeInput Is Nothing Then Return
        Dim FileName As String = "", FileUrl As String = "", FileKey As String = "", FileSha As String = "", ParseErr As String = Nothing
        Dim FileSize As Long = 0
        If Not ModFriends.TryParseFilePickupCode(CodeInput, FileName, FileSize, FileUrl, FileKey, FileSha, ParseErr) Then
            Hint(ParseErr, HintType.Red)
            Return
        End If
        Dim SizeLabel = FormatByteSizeText(FileSize)
        Dim HostNote = If(FileUrl.IndexOf("pixeldrain.com", StringComparison.OrdinalIgnoreCase) >= 0,
                          "Pixeldrain 长期托管",
                          "配置中继")
        If MyMsgBox($"已识别离线加密文件：{vbCrLf}文件名：{FileName}{vbCrLf}大小：{SizeLabel}{vbCrLf}存储源：{HostNote}{vbCrLf}{vbCrLf}下载后将自动校验 SHA-256 完整性摘要。是否选择保存位置？",
                    "确认解密下载", "选择保存位置", "取消") <> 1 Then Return
        PromptAndDownloadEncryptedFile(FileName, FileUrl, FileKey, FileSha, BtnPickupFile, "取件码")
    End Sub

    Private Sub BtnSendFile_Click() Handles BtnSendFile.Click
        If CurrentFriendId = "" Then
            Hint("请先选择一个好友或群聊再发送文件！", HintType.Red)
            Return
        End If
        Dim Dlg As New Microsoft.Win32.OpenFileDialog With {
            .Title = LocalizationCore.TranslateText("选择要加密发送的文件（支持大文件分片加密）"),
            .Filter = "所有文件 (*.*)|*.*|Mod 与压缩包 (*.jar;*.zip)|*.jar;*.zip|图片 (*.png;*.jpg;*.webp)|*.png;*.jpg;*.webp"
        }
        If Dlg.ShowDialog() <> True Then Return
        SendLocalFileWithConfirm(Dlg.FileName)
    End Sub

    Private Sub SendLocalFileWithConfirm(FilePath As String)
        If String.IsNullOrWhiteSpace(FilePath) OrElse Not IO.File.Exists(FilePath) Then Return
        Dim TargetId = CurrentFriendId, IsGrp = CurrentIsGroup
        If String.IsNullOrEmpty(TargetId) Then Return
        Try
            Dim Info As New IO.FileInfo(FilePath)
            If Info.Length = 0 Then
                Hint("无法发送空文件！", HintType.Red)
                Return
            End If
            Dim SizeLabel = FormatByteSizeText(Info.Length)
            Dim EffProvider = ModFriends.ResolveEffectiveFileStorageProvider()
            Dim RouteDesc As String
            Select Case EffProvider
                Case "s3"
                    RouteDesc = "将通过已配置的 S3 兼容对象存储（R2 / OSS / MinIO）进行 AES-256 流式分片加密托管并生成预签名链接。"
                Case "webdav"
                    RouteDesc = "将通过已配置的 WebDAV / Alist 存储进行 AES-256 流式分片加密上传（长期保存）。"
                Case "pixeldrain"
                    RouteDesc = "将通过 Pixeldrain 进行 AES-256 流式分片加密托管（保留 120 天，好友免账号直下）。"
                Case Else
                    If Info.Length <= ModFriends.MaxEncryptedFileBytes Then
                        RouteDesc = "将通过当前 ntfy 中继加密传输（公共站附件约 3 小时有效）。"
                    Else
                        RouteDesc = "文件大于 10 MB，将自动使用 ntfy 分片流式加密上传（公共站约 3 小时有效；如需长期保留，可在左下角「中继」配置 Pixeldrain / WebDAV / S3）。"
                    End If
            End Select
            If MyMsgBox($"确定将「{Info.Name}」（{SizeLabel}）加密上传并发送给当前会话吗？{vbCrLf}{vbCrLf}{RouteDesc}", "发送加密文件", "加密发送", "取消") <> 1 Then Return
            Dim SafeName = Info.Name
            Dim FileLen = Info.Length
            BtnSendFile.IsEnabled = False
            BtnSendFile.Text = LocalizationCore.TranslateText("上传中…")
            Hint($"正在流式 AES-256 加密上传「{SafeName}」（{SizeLabel}）…", HintType.Blue)
            RunInNewThread(
                Sub()
                    Dim FileUrl As String = "", FileKey As String = "", FileSha As String = "", Provider As String = ""
                    Dim Err As String = ModFriends.UploadEncryptedFile(
                        FilePath, FileUrl, FileKey, FileSha, Provider,
                        Sub(Stage, Cur, Total)
                            RunInUi(
                                Sub()
                                    If Stage = "写入索引" Then
                                        BtnSendFile.Text = LocalizationCore.TranslateText("索引中…")
                                    ElseIf Total > 1 Then
                                        BtnSendFile.Text = LocalizationCore.TranslateText("上传") & $" {Cur}/{Total}"
                                    Else
                                        BtnSendFile.Text = LocalizationCore.TranslateText("上传中…")
                                    End If
                                End Sub)
                        End Sub)
                    If Err Is Nothing Then
                        Dim Msg As New ModFriends.FriendMessage With {
                            .Kind = "file",
                            .Text = $"发送了文件：{SafeName}",
                            .FileName = SafeName,
                            .FileSize = FileLen,
                            .FileUrl = FileUrl,
                            .FileKey = FileKey,
                            .FileSha256 = FileSha
                        }
                        Err = ModFriends.SendRichMessage(TargetId, IsGrp, Msg)
                    End If
                    RunInUi(
                        Sub()
                            BtnSendFile.IsEnabled = True
                            BtnSendFile.Text = LocalizationCore.TranslateText("文件")
                            If Err IsNot Nothing Then
                                Hint(Err, HintType.Red)
                            Else
                                If FileLen > ModFriends.MaxEncryptedFileBytes Then
                                    Hint($"已加密发送大文件「{SafeName}」！可点文件卡片上的「复制取件码」方便好友随时下载。", HintType.Green)
                                Else
                                    Hint($"已加密发送文件「{SafeName}」！", HintType.Green)
                                End If
                            End If
                        End Sub)
                End Sub, "Send Encrypted Local File")
        Catch ex As Exception
            BtnSendFile.IsEnabled = True
            BtnSendFile.Text = LocalizationCore.TranslateText("文件")
            Hint($"无法访问所选文件：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub UploadAndSendFileCore(TargetId As String, IsGrp As Boolean, FileName As String, PlainBytes As Byte(), ByRef OutErr As String)
        Dim FileUrl As String = "", FileKey As String = "", FileSha As String = ""
        OutErr = ModFriends.UploadEncryptedBlob(PlainBytes, FileUrl, FileKey, FileSha)
        If OutErr Is Nothing Then
            Dim Msg As New ModFriends.FriendMessage With {
                .Kind = "file",
                .Text = $"发送了文件：{FileName}",
                .FileName = FileName,
                .FileSize = PlainBytes.LongLength,
                .FileUrl = FileUrl,
                .FileKey = FileKey,
                .FileSha256 = FileSha
            }
            OutErr = ModFriends.SendRichMessage(TargetId, IsGrp, Msg)
        End If
    End Sub

    Private Sub SendFileBytesAsync(TargetId As String, IsGrp As Boolean, FileName As String, PlainBytes As Byte())
        If String.IsNullOrEmpty(TargetId) Then Return
        BtnSendFile.IsEnabled = False
        Hint($"正在 AES-256 加密并上传「{FileName}」…", HintType.Blue)
        RunInNewThread(
            Sub()
                Dim Err As String = Nothing
                Try
                    UploadAndSendFileCore(TargetId, IsGrp, FileName, PlainBytes, Err)
                Catch ex As Exception
                    Err = "发送失败：" & ex.Message
                End Try
                RunInUi(
                    Sub()
                        BtnSendFile.IsEnabled = True
                        If Err IsNot Nothing Then
                            Hint(Err, HintType.Red)
                        Else
                            Hint($"已加密发送文件「{FileName}」！", HintType.Green)
                        End If
                    End Sub)
            End Sub, "Send Encrypted File")
    End Sub

    Private Sub BtnSendInvite_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnSendInvite.Click
        If Not BtnSendInvite.IsEnabled Then Return
        If CurrentFriendId = "" Then
            Hint("请先选中一位好友或群聊！", HintType.Red)
            Return
        End If
        Dim Invite = PageLinkMain.GetCurrentInvite()
        If Invite Is Nothing Then
            Hint("请先在「联机」页创建一个房间，再来发送邀请！", HintType.Red)
            Return
        End If
        Dim IncludeMods As Boolean = False
        If McInstanceSelected IsNot Nothing Then
            Dim Choice = MyMsgBox($"是否随联机邀请附带当前实例「{McInstanceSelected.Name}」的 Mod 清单，方便一键对比/同步差异？",
                                  "发送联机邀请", "附带 Mod 清单发送", "仅发送联机邀请", "取消")
            If Choice = 3 OrElse Choice = 0 Then Return
            IncludeMods = (Choice = 1)
        End If

        Dim TargetId As String = CurrentFriendId, IsGrp As Boolean = CurrentIsGroup
        Dim Code As String = Invite(0), Verify As String = Invite(1)
        Dim InstanceSnap = McInstanceSelected
        BtnSendInvite.IsEnabled = False
        If IncludeMods Then Hint("正在整理当前实例 Mod 信息并发送邀请……", HintType.Blue)
        RunInNewThread(
            Sub()
                Dim Err As String = Nothing
                Try
                    Dim SyncData As String = ""
                    If IncludeMods AndAlso InstanceSnap IsNot Nothing Then
                        Dim Manifest = ModFriends.BuildInstanceModSyncManifest(InstanceSnap)
                        SyncData = ModFriends.CompressModSyncManifest(Manifest)
                    End If
                    Dim InviteText = If(IncludeMods, "一起来联机吧（含 Mod 同步清单）", "一起来联机吧")
                    Err = If(IsGrp, ModFriends.SendGroup(TargetId, "invite", InviteText, Code, Verify, SyncData), ModFriends.Send(TargetId, "invite", InviteText, Code, Verify, SyncData))
                Catch ex As Exception
                    Err = "发送失败：" & ex.Message
                Finally
                    RunInUi(
                        Sub()
                            BtnSendInvite.IsEnabled = True
                            If Err IsNot Nothing Then
                                Hint(Err, HintType.Red)
                            Else
                                Hint(If(IncludeMods, "联机邀请（含 Mod 清单）已发送！", "联机邀请已发送！"), HintType.Green)
                            End If
                        End Sub)
                End Try
            End Sub, "PCL Linking 发送联机邀请")
    End Sub

    Private Sub BtnBroadcastInvite_Click(sender As Object, e As EventArgs) Handles BtnBroadcastInvite.Click
        If Not BtnBroadcastInvite.IsEnabled Then Return
        Dim Invite = PageLinkMain.GetCurrentInvite()
        If Invite Is Nothing Then
            Hint("请先在「联机」页创建一个房间，再来群发邀请！", HintType.Red)
            Return
        End If
        Dim Targets = ModFriends.GetFriendsSnapshot()
        If Not Targets.Any() Then
            Hint("好友列表为空，请先添加至少一位好友！", HintType.Red)
            Return
        End If

        Dim IncludeMods As Boolean = False
        If McInstanceSelected IsNot Nothing Then
            Dim Choice = MyMsgBox($"即将向好友列表中的全部 {Targets.Count} 位好友群发联机邀请（当前选中实例：「{McInstanceSelected.Name}」）。是否同时附带 Mod 同步清单？",
                                  "一键群发联机邀请", "附带 Mod 清单群发", "仅群发联机邀请", "取消")
            If Choice = 3 OrElse Choice = 0 Then Return
            IncludeMods = (Choice = 1)
        Else
            If MyMsgBox($"即将向好友列表中的全部 {Targets.Count} 位好友群发联机邀请，确定发送吗？", "一键群发联机邀请", "确认群发", "取消") <> 1 Then Return
        End If

        Dim Code As String = Invite(0), Verify As String = Invite(1)
        Dim InstanceSnap = McInstanceSelected
        BtnBroadcastInvite.IsEnabled = False
        Hint($"正在向 {Targets.Count} 位好友群发联机邀请……", HintType.Blue)

        RunInNewThread(
            Sub()
                Dim OkCount As Integer = 0
                Dim FailCount As Integer = 0
                Dim LastErr As String = Nothing
                Try
                    Dim SyncData As String = ""
                    If IncludeMods AndAlso InstanceSnap IsNot Nothing Then
                        Dim Manifest = ModFriends.BuildInstanceModSyncManifest(InstanceSnap)
                        SyncData = ModFriends.CompressModSyncManifest(Manifest)
                    End If
                    Dim InviteText = If(IncludeMods, "一起来联机吧（群发邀请，含 Mod 同步清单）", "一起来联机吧（群发邀请）")
                    For Each F In Targets
                        Try
                            Dim Err = ModFriends.Send(F.Id, "invite", InviteText, Code, Verify, SyncData)
                            If Err Is Nothing Then
                                OkCount += 1
                            Else
                                FailCount += 1
                                LastErr = Err
                            End If
                        Catch exEach As Exception
                            FailCount += 1
                            LastErr = exEach.Message
                        End Try
                    Next
                Catch ex As Exception
                    FailCount = Math.Max(1, FailCount)
                    LastErr = ex.Message
                Finally
                    RunInUi(
                        Sub()
                            BtnBroadcastInvite.IsEnabled = True
                            If FailCount = 0 Then
                                Hint($"已向全部 {OkCount} 位好友群发联机邀请！", HintType.Green)
                            ElseIf OkCount > 0 Then
                                Hint($"群发完成：成功 {OkCount} 位，失败 {FailCount} 位（{LastErr}）", HintType.Blue)
                            Else
                                Hint($"群发失败：{LastErr}", HintType.Red)
                            End If
                        End Sub)
                End Try
            End Sub, "PCL Linking 群发联机邀请")
    End Sub

    Private Sub BtnShareModSync_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnShareModSync.Click
        If Not BtnShareModSync.IsEnabled Then Return
        If CurrentFriendId = "" Then
            Hint("请先选中一位好友或群聊！", HintType.Red)
            Return
        End If
        If McInstanceSelected Is Nothing Then
            Hint("请先选择一个游戏实例！", HintType.Red)
            Return
        End If
        Dim TargetId = CurrentFriendId, IsGrp = CurrentIsGroup
        Dim InstanceSnap = McInstanceSelected
        BtnShareModSync.IsEnabled = False
        Hint($"正在读取实例「{InstanceSnap.Name}」的 Mod 清单并发送……", HintType.Blue)
        RunInNewThread(
            Sub()
                Dim Err As String = Nothing
                Dim SentCount As Integer = 0
                Dim NoMods As Boolean = False
                Try
                    Dim Manifest = ModFriends.BuildInstanceModSyncManifest(InstanceSnap)
                    If Manifest Is Nothing OrElse Not Manifest.Items.Any() Then
                        NoMods = True
                        Return
                    End If
                    SentCount = Manifest.Items.Count
                    Dim SyncData = ModFriends.CompressModSyncManifest(Manifest)
                    Dim TextDesc = $"分享了 {InstanceSnap.Version.VanillaName} 的 {Manifest.Items.Count} 个 Mod 清单"
                    Err = If(IsGrp, ModFriends.SendGroup(TargetId, "modsync", TextDesc, ModSyncData:=SyncData), ModFriends.Send(TargetId, "modsync", TextDesc, ModSyncData:=SyncData))
                Catch ex As Exception
                    Err = "发送失败：" & ex.Message
                Finally
                    RunInUi(
                        Sub()
                            BtnShareModSync.IsEnabled = True
                            If NoMods Then
                                Hint("当前实例没有已启用的 Mod！", HintType.Red)
                            ElseIf Err IsNot Nothing Then
                                Hint(Err, HintType.Red)
                            Else
                                Hint($"已发送 {SentCount} 个 Mod 清单！", HintType.Green)
                            End If
                        End Sub)
                End Try
            End Sub, "PCL Linking 发送 Mod 同步清单")
    End Sub

    Private Sub BtnInstSyncMenu_Click() Handles BtnInstSyncMenu.Click
        Dim Actions As New List(Of IMyRadio) From {
            New MyRadioBox With {.Text = "将当前选中实例设为「发布源」并立即推送修订版"},
            New MyRadioBox With {.Text = "管理全部同步绑定（手动同步 / 开关启动自动同步 / 删除）"},
            New MyRadioBox With {.Text = "打包整合包环境快照发给好友（可选 mods / config / kubejs / 材质光影）"},
            New MyRadioBox With {.Text = "发送世界存档给好友 / 群接力开房（轮值主机共享存档）"}
        }
        Dim Picked = MyMsgBoxSelect(Actions.ToArray(), "实例同步、整合包快照与存档接力", "下一步", "取消")
        If Picked Is Nothing Then Return
        If Picked.Value = 0 Then
            StartPublishInstanceSyncWizard()
        ElseIf Picked.Value = 1 Then
            ShowManageSyncBindingsDialog()
        ElseIf Picked.Value = 2 Then
            StartEnvPackSnapshotWizard()
        Else
            PromptAndSendWorldSaveRelay(McInstanceSelected, CurrentFriendId, CurrentIsGroup)
        End If
    End Sub

    Private Sub StartEnvPackSnapshotWizard()
        If CurrentFriendId = "" Then
            Hint("请先在左侧选择要发送给的好友或群聊！", HintType.Red)
            Return
        End If
        Dim TargetInst = McInstanceSelected
        If TargetInst Is Nothing Then
            Hint("请先在启动器主界面选中一个游戏实例！", HintType.Red)
            Return
        End If

        Dim Presets As IMyRadio() = {
            New MyRadioBox With {.Text = "核心运行环境（mods + config + defaultconfigs + kubejs）"},
            New MyRadioBox With {.Text = "差分精简包（同上，但自动跳过 Modrinth 在线可下载的 Mod，体积大幅减小）"},
            New MyRadioBox With {.Text = "完整外观环境（core + resourcepacks + shaderpacks，适合直接开玩）"}
        }
        Dim Pick = MyMsgBoxSelect(Presets, $"选择要从「{TargetInst.Name}」打包发送的目录范围：", "开始打包并加密上传", "取消")
        If Pick Is Nothing Then Return

        Dim SelectedDirs As New List(Of String) From {"mods", "config", "defaultconfigs", "kubejs"}
        Dim SkipModrinth = (Pick.Value = 1)
        If Pick.Value = 2 Then
            SelectedDirs.Add("resourcepacks")
            SelectedDirs.Add("shaderpacks")
        End If

        Dim TargetId = CurrentFriendId, IsGrp = CurrentIsGroup
        BtnInstSyncMenu.IsEnabled = False
        BtnInstSyncMenu.Text = LocalizationCore.TranslateText("打包中…")
        Hint($"正在打包实例「{TargetInst.Name}」环境快照…", HintType.Blue)

        RunInNewThread(
            Sub()
                Dim TempZip As String = "", FileCount As Integer = 0
                Dim ZipBytesLen As Long = 0
                Dim Err As String = ModInstanceSync.PackInstanceEnvironmentToZip(
                    TargetInst, SelectedDirs, SkipModrinth, TempZip, FileCount, ZipBytesLen,
                    Sub(DoneCnt, TotCnt)
                        RunInUi(Sub() BtnInstSyncMenu.Text = LocalizationCore.TranslateText("打包") & $" {DoneCnt}/{TotCnt}")
                    End Sub)
                Try
                    If Err Is Nothing AndAlso Not String.IsNullOrEmpty(TempZip) Then
                        Dim SizeLabel = FormatByteSizeText(ZipBytesLen)
                        RunInUi(Sub() Hint($"打包完成（{FileCount} 个文件，{SizeLabel}），正在流式加密上传…", HintType.Blue))
                        Dim FileUrl As String = "", FileKey As String = "", FileSha As String = "", Provider As String = ""
                        Err = ModFriends.UploadEncryptedFile(
                            TempZip, FileUrl, FileKey, FileSha, Provider,
                            Sub(St, Cur, Tot)
                                RunInUi(Sub() BtnInstSyncMenu.Text = If(Tot > 1, $"{LocalizationCore.TranslateText("上传")} {Cur}/{Tot}", LocalizationCore.TranslateText("上传中…")))
                            End Sub)
                        If Err Is Nothing Then
                            Dim SafeInst = TargetInst.Name.Replace(" ", "_")
                            For Each BadCh In IO.Path.GetInvalidFileNameChars()
                                SafeInst = SafeInst.Replace(BadCh, "_"c)
                            Next
                            Dim PackFileName = $"EnvPack_{SafeInst}_{DateTime.Now:yyyyMMdd_HHmm}.zip"
                            Dim Msg As New ModFriends.FriendMessage With {
                                .Kind = "file",
                                .Text = $"发送了整合包环境快照：{PackFileName}",
                                .FileName = PackFileName,
                                .FileSize = ZipBytesLen,
                                .FileUrl = FileUrl,
                                .FileKey = FileKey,
                                .FileSha256 = FileSha
                            }
                            Err = ModFriends.SendRichMessage(TargetId, IsGrp, Msg)
                        End If
                    End If
                Finally
                    Try
                        If Not String.IsNullOrEmpty(TempZip) AndAlso IO.File.Exists(TempZip) Then IO.File.Delete(TempZip)
                    Catch
                    End Try
                End Try
                RunInUi(
                    Sub()
                        BtnInstSyncMenu.IsEnabled = True
                        BtnInstSyncMenu.Text = LocalizationCore.TranslateText("实例同步")
                        If Err IsNot Nothing Then
                            Hint(Err, HintType.Red)
                        Else
                            Hint("整合包环境快照已加密发送！好友可一键应用到实例或复制取件码。", HintType.Green)
                        End If
                    End Sub)
            End Sub, "Pack & Upload Env Snapshot")
    End Sub

    Public Shared Sub PromptAndSendWorldSaveRelay(TargetInst As McInstance, Optional DefaultTargetId As String = "", Optional DefaultIsGroup As Boolean = False)
        If TargetInst Is Nothing Then
            Hint("请先在主界面选中一个包含 saves 存档的游戏实例！", HintType.Red)
            Return
        End If
        Dim IndieRoot = PathUtils.RemoveExtendedPrefix(TargetInst.PathIndie).TrimEnd("\"c, "/"c)
        Dim SavesDir = IO.Path.Combine(IndieRoot, "saves")
        If Not IO.Directory.Exists(SavesDir) Then
            Hint($"实例「{TargetInst.Name}」的 saves 目录不存在！", HintType.Red)
            Return
        End If
        Dim Worlds = New IO.DirectoryInfo(SavesDir).GetDirectories() _
            .Where(Function(d) IO.File.Exists(IO.Path.Combine(d.FullName, "level.dat"))) _
            .OrderByDescending(Function(d) d.LastWriteTimeUtc).ToList()
        If Not Worlds.Any() Then
            Hint("当前实例 saves 目录中没有包含 level.dat 的世界存档！", HintType.Red)
            Return
        End If
        Dim WorldRadios = Worlds.Select(Function(w) CType(New MyRadioBox With {.Text = $"{w.Name}（更新于 {w.LastWriteTime:MM-dd HH:mm}）"}, IMyRadio)).ToArray()
        Dim WIdx = MyMsgBoxSelect(WorldRadios, $"选择要上传接力的世界存档（实例：{TargetInst.Name}）：", "下一步", "取消")
        If WIdx Is Nothing Then Return
        Dim ChosenWorld = Worlds(WIdx.Value)

        Dim SendTargetId = If(DefaultTargetId, "").Trim()
        Dim SendIsGrp = DefaultIsGroup
        Dim SendLabel = SendTargetId
        If String.IsNullOrEmpty(SendTargetId) Then
            Dim D = ModFriends.GetData()
            Dim Targets As New List(Of (Id As String, IsGrp As Boolean, Label As String))
            For Each G In D.Groups
                Targets.Add((G.Id, True, $"[群聊] {G.DisplayName}"))
            Next
            For Each F In D.Friends
                Targets.Add((F.Id, False, $"[好友] {F.DisplayName}"))
            Next
            If Not Targets.Any() Then
                Hint("请先在「好友」页至少添加一位好友或加入一个群聊！", HintType.Red)
                Return
            End If
            Dim TRadios = Targets.Select(Function(t) CType(New MyRadioBox With {.Text = t.Label}, IMyRadio)).ToArray()
            Dim TIdx = MyMsgBoxSelect(TRadios, $"选择要接收接力存档「{ChosenWorld.Name}」的群聊或好友：", "打包加密并发送", "取消")
            If TIdx Is Nothing Then Return
            SendTargetId = Targets(TIdx.Value).Id
            SendIsGrp = Targets(TIdx.Value).IsGrp
            SendLabel = Targets(TIdx.Value).Label
        Else
            SendLabel = If(SendIsGrp, If(ModFriends.FindGroup(SendTargetId)?.DisplayName, SendTargetId), If(ModFriends.FindFriend(SendTargetId)?.DisplayName, SendTargetId))
        End If

        Hint($"正在打包并流式加密上传存档「{ChosenWorld.Name}」（自动跳过 session.lock）…", HintType.Blue)
        RunInNewThread(
            Sub()
                Dim TempZip As String = "", SizeBytes As Long = 0, ZipSha As String = ""
                Dim Err = ModInstanceSync.PackWorldSaveToTempZip(ChosenWorld.FullName, TempZip, SizeBytes, ZipSha)
                Try
                    If Err Is Nothing AndAlso Not String.IsNullOrEmpty(TempZip) Then
                        Dim FileUrl As String = "", FileKey As String = "", FileSha As String = "", Provider As String = ""
                        Err = ModFriends.UploadEncryptedFile(TempZip, FileUrl, FileKey, FileSha, Provider)
                        If Err Is Nothing Then
                            Dim Msg As New ModFriends.FriendMessage With {
                                .Kind = "save_relay",
                                .Text = $"发送了接力存档：{ChosenWorld.Name}",
                                .WorldName = ChosenWorld.Name,
                                .FileName = ChosenWorld.Name & ".zip",
                                .FileSize = SizeBytes,
                                .FileUrl = FileUrl,
                                .FileKey = FileKey,
                                .FileSha256 = FileSha
                            }
                            Err = ModFriends.SendRichMessage(SendTargetId, SendIsGrp, Msg)
                        End If
                    End If
                Finally
                    Try
                        If Not String.IsNullOrEmpty(TempZip) AndAlso IO.File.Exists(TempZip) Then IO.File.Delete(TempZip)
                    Catch
                    End Try
                End Try
                RunInUi(
                    Sub()
                        If Err IsNot Nothing Then
                            Hint("发送接力存档失败：" & Err, HintType.Red)
                        Else
                            Hint($"已将接力存档「{ChosenWorld.Name}」加密发送给 {SendLabel}！", HintType.Green)
                        End If
                    End Sub)
            End Sub, "Send Rotating Save Relay")
    End Sub

    Private Sub StartPublishInstanceSyncWizard()
        If CurrentFriendId = "" Then
            Hint("请先在左侧选择要接收推送的好友或群聊！", HintType.Red)
            Return
        End If
        If McInstanceSelected Is Nothing Then
            Hint("请先在启动器主界面选中一个游戏实例！", HintType.Red)
            Return
        End If
        Dim TargetName = If(CurrentIsGroup, If(ModFriends.FindGroup(CurrentFriendId)?.DisplayName, CurrentFriendId), If(ModFriends.FindFriend(CurrentFriendId)?.DisplayName, CurrentFriendId))
        Dim ChName = MyMsgBoxInput("设置同步频道名称", $"将作为「发布源」推送实例「{McInstanceSelected.Name}」至：{TargetName}。", McInstanceSelected.Name, HintText:="同步频道名")
        If String.IsNullOrWhiteSpace(ChName) Then Return
        Dim ModeChoice = MyMsgBox($"是否同时加密打包上传 config 配置目录？（<= 8 MB，将在接收方覆盖其 config 前自动备份原配置）",
                                  "实例同步内容设置", "同步 Mod + Config", "仅同步 Mod 清单", "取消")
        If ModeChoice = 0 OrElse ModeChoice = 3 Then Return
        Dim IncludeCfg = (ModeChoice = 1)
        Dim AutoStartup = (MyMsgBox("是否在每次启动 PCL Linking 时自动检查该实例变动并静默推送最新修订版？", "启动自动检测推送", "开启启动自动检测", "仅手动推送") = 1)

        Dim Binding = ModInstanceSync.UpsertPublisherBinding(CurrentFriendId, CurrentIsGroup, TargetName, McInstanceSelected, ChName, IncludeCfg, AutoStartup)
        Hint($"正在构建并推送实例同步「{Binding.ChannelName}」…", HintType.Blue)
        RunInNewThread(
            Sub()
                Dim Err = ModInstanceSync.PublishBindingNow(Binding.Id, SilentIfUnchanged:=False)
                RunInUi(
                    Sub()
                        If Err IsNot Nothing Then
                            Hint("推送失败：" & Err, HintType.Red)
                        Else
                            Hint($"已向 {TargetName} 推送实例同步「{Binding.ChannelName}」！", HintType.Green)
                        End If
                    End Sub)
            End Sub, "Publish Instance Sync")
    End Sub

    Private Sub ShowManageSyncBindingsDialog()
        Dim Bindings = ModInstanceSync.GetBindings()
        If Not Bindings.Any() Then
            Hint("暂无任何实例同步绑定。先向好友/群推送一次实例，或点击对方发来的「实例同步」卡片绑定即可。", HintType.Blue)
            Return
        End If
        Dim Radios As New List(Of IMyRadio)
        For Each B In Bindings
            Dim RoleTag = If(B.Role = "Publisher", "[我发布]", "[我订阅]")
            Dim AutoTag = If(B.AutoSyncOnStartup, "启动自动", "仅手动")
            Radios.Add(New MyRadioBox With {.Text = $"{RoleTag} {B.ChannelName} ({B.LocalInstanceName} <-> {B.TargetName}) | {AutoTag} | {B.LastStatus}"})
        Next
        Dim Picked = MyMsgBoxSelect(Radios.ToArray(), "选择要操作的实例同步绑定：", "操作选中项", "关闭")
        If Picked Is Nothing Then Return
        Dim TargetB = Bindings(Picked.Value)
        Dim OpText = If(TargetB.Role = "Publisher", "立即推送一次最新修订版", "立即手动检查并同步一次")
        Dim OpChoice = MyMsgBox($"当前绑定：{TargetB.ChannelName}（{TargetB.LocalInstanceName}）{vbCrLf}角色：{If(TargetB.Role = "Publisher", "发布源", "订阅接收方")}  ·  启动自动：{If(TargetB.AutoSyncOnStartup, "开启", "关闭")}{vbCrLf}最近状态：{TargetB.LastStatus}",
                                "管理同步绑定", OpText, If(TargetB.AutoSyncOnStartup, "关闭启动自动", "开启启动自动"), "解除此绑定")
        If OpChoice = 1 Then
            If TargetB.Role = "Publisher" Then
                Hint($"正在推送「{TargetB.ChannelName}」…", HintType.Blue)
                RunInNewThread(
                    Sub()
                        Dim Err = ModInstanceSync.PublishBindingNow(TargetB.Id, SilentIfUnchanged:=False)
                        RunInUi(Sub() Hint(If(Err, $"已手动推送「{TargetB.ChannelName}」最新修订版！"), If(Err Is Nothing, HintType.Green, HintType.Red)))
                    End Sub, "Manual Publish Instance Sync")
            Else
                RunInNewThread(Sub() ModInstanceSync.SyncSubscriberBindingNow(TargetB.Id, IsAutoStartup:=False), "Manual Subscribe Instance Sync")
            End If
        ElseIf OpChoice = 2 Then
            ModInstanceSync.ToggleBindingAutoStartup(TargetB.Id)
            Hint($"已切换「{TargetB.ChannelName}」启动自动同步状态！", HintType.Green)
        ElseIf OpChoice = 3 Then
            If MyMsgBox($"确定解除「{TargetB.ChannelName}」的自动同步绑定吗？（不会删除本地实例文件）", "确认解绑", "确认解除", "取消", IsWarn:=True) = 1 Then
                ModInstanceSync.DeleteBinding(TargetB.Id)
                Hint("已解除该实例同步绑定。", HintType.Blue)
            End If
        End If
    End Sub

    Public Class ModSyncDiffResult
        Public Property IsVersionMismatch As Boolean
        Public Property IsLoaderMismatch As Boolean
        Public Property ExactCount As Integer
        Public Property LocalEnabledCount As Integer
        Public Property DisabledMatch As New List(Of LocalResourceFile)
        Public Property MissingDownloadable As New List(Of ModFriends.ModSyncEntry)
        Public Property MismatchDownloadable As New List(Of (HostEntry As ModFriends.ModSyncEntry, LocalMod As LocalResourceFile))
        Public Property MismatchLocalOnly As New List(Of (HostEntry As ModFriends.ModSyncEntry, LocalMod As LocalResourceFile))
        Public Property HostLocalOnly As New List(Of ModFriends.ModSyncEntry)
        Public Property ExtraLocal As New List(Of LocalResourceFile)

        Public ReadOnly Property IsFullySynced As Boolean
            Get
                Return Not IsVersionMismatch AndAlso
                       Not IsLoaderMismatch AndAlso
                       Not DisabledMatch.Any() AndAlso
                       Not MissingDownloadable.Any() AndAlso
                       Not MismatchDownloadable.Any() AndAlso
                       Not MismatchLocalOnly.Any() AndAlso
                       Not HostLocalOnly.Any() AndAlso
                       Not ExtraLocal.Any()
            End Get
        End Property
    End Class

    Public Shared Function CanResolveHostEntryOnline(HostItem As ModFriends.ModSyncEntry) As Boolean
        Return Not String.IsNullOrWhiteSpace(HostItem.I) OrElse
               Not String.IsNullOrWhiteSpace(HostItem.H) OrElse
               Not String.IsNullOrWhiteSpace(HostItem.N)
    End Function

    Public Shared Function ScanAndCompareHostMods(HostManifest As ModFriends.ModSyncManifest, TargetInstance As McInstance) As ModSyncDiffResult
        If Not TargetInstance.IsLoaded Then TargetInstance.Load()
        Dim Scan = ScanInstanceModsWithCache(TargetInstance)
        If Scan.ModList.Any() AndAlso Scan.ModList.Any(Function(m) Not m.OnlineDataLoaded) Then
            Try
                Dim OnlineLoader As New LoaderTask(Of (ModList As List(Of LocalResourceFile), Cache As JObject, Target As McVersion), Integer)(
                    "好友 Mod 同步本地扫描", AddressOf LocalResourceOnlineLoad)
                Using Cts As New System.Threading.CancellationTokenSource(20000)
                    OnlineLoader.WaitForExit(Input:=(Scan.ModList, Scan.Cache, TargetInstance.Version), c:=Cts.Token)
                End Using
            Catch
            End Try
        End If
        Return CompareHostModsWithLocal(HostManifest, Scan.ModList, TargetInstance)
    End Function

    Public Shared Function CompareHostModsWithLocal(HostManifest As ModFriends.ModSyncManifest,
                                                    AllLocalMods As List(Of LocalResourceFile),
                                                    TargetInstance As McInstance) As ModSyncDiffResult
        Dim LocalEnabled = AllLocalMods.Where(Function(m) m.IsEnabled).ToList()
        Dim LocalDisabled = AllLocalMods.Where(Function(m) Not m.IsEnabled).ToList()
        Dim Result As New ModSyncDiffResult With {
            .LocalEnabledCount = LocalEnabled.Count,
            .IsVersionMismatch = Not String.IsNullOrEmpty(HostManifest.Ver) AndAlso
                                 Not String.Equals(If(TargetInstance.Version.VanillaName, ""), HostManifest.Ver, StringComparison.OrdinalIgnoreCase),
            .IsLoaderMismatch = Not String.IsNullOrEmpty(HostManifest.Ver) AndAlso HostManifest.Ldr <> CInt(TargetInstance.Version.ModLoaders)
        }

        Dim MatchedLocal As New HashSet(Of LocalResourceFile)
        Dim MatchedDisabled As New HashSet(Of LocalResourceFile)
        For Each HostItem In HostManifest.Items
            Dim ByHash = LocalEnabled.FirstOrDefault(
                Function(l) Not MatchedLocal.Contains(l) AndAlso
                            Not String.IsNullOrEmpty(HostItem.H) AndAlso
                            String.Equals(l.ModrinthHash, HostItem.H, StringComparison.OrdinalIgnoreCase))
            If ByHash IsNot Nothing Then
                MatchedLocal.Add(ByHash)
                Result.ExactCount += 1
                Continue For
            End If

            Dim ByProject = LocalEnabled.FirstOrDefault(
                Function(l) Not MatchedLocal.Contains(l) AndAlso
                            Not String.IsNullOrEmpty(HostItem.I) AndAlso
                            l.Project IsNot Nothing AndAlso
                            String.Equals(l.Project.Id, HostItem.I, StringComparison.OrdinalIgnoreCase))
            If ByProject IsNot Nothing Then
                MatchedLocal.Add(ByProject)
                If String.IsNullOrEmpty(HostItem.H) AndAlso
                   Not String.IsNullOrEmpty(HostItem.V) AndAlso
                   ByProject.ProjectVersion IsNot Nothing AndAlso
                   String.Equals(ByProject.ProjectVersion.Id, HostItem.V, StringComparison.OrdinalIgnoreCase) Then
                    Result.ExactCount += 1
                Else
                    Result.MismatchDownloadable.Add((HostItem, ByProject))
                End If
                Continue For
            End If

            Dim ByName = LocalEnabled.FirstOrDefault(
                Function(l) Not MatchedLocal.Contains(l) AndAlso
                            String.Equals(l.File.Name, HostItem.F, StringComparison.OrdinalIgnoreCase))
            If ByName IsNot Nothing Then
                MatchedLocal.Add(ByName)
                If String.IsNullOrEmpty(HostItem.H) OrElse
                   String.Equals(ByName.ModrinthHash, HostItem.H, StringComparison.OrdinalIgnoreCase) Then
                    Result.ExactCount += 1
                ElseIf CanResolveHostEntryOnline(HostItem) Then
                    Result.MismatchDownloadable.Add((HostItem, ByName))
                Else
                    Result.MismatchLocalOnly.Add((HostItem, ByName))
                End If
                Continue For
            End If

            '优先检查本地已禁用（.disabled / .old）的同款 Mod，可直接启用免下载
            Dim DisabledExact = LocalDisabled.FirstOrDefault(
                Function(l) Not MatchedDisabled.Contains(l) AndAlso (
                    (Not String.IsNullOrEmpty(HostItem.H) AndAlso String.Equals(l.ModrinthHash, HostItem.H, StringComparison.OrdinalIgnoreCase)) OrElse
                    (Not String.IsNullOrEmpty(HostItem.I) AndAlso l.Project IsNot Nothing AndAlso String.Equals(l.Project.Id, HostItem.I, StringComparison.OrdinalIgnoreCase) AndAlso
                     (String.IsNullOrEmpty(HostItem.V) OrElse (l.ProjectVersion IsNot Nothing AndAlso String.Equals(l.ProjectVersion.Id, HostItem.V, StringComparison.OrdinalIgnoreCase)))) OrElse
                    (Not String.IsNullOrEmpty(HostItem.F) AndAlso String.Equals(l.EnabledName, HostItem.F, StringComparison.OrdinalIgnoreCase) AndAlso
                     (String.IsNullOrEmpty(HostItem.H) OrElse String.Equals(l.ModrinthHash, HostItem.H, StringComparison.OrdinalIgnoreCase)))))
            If DisabledExact IsNot Nothing Then
                MatchedDisabled.Add(DisabledExact)
                Result.DisabledMatch.Add(DisabledExact)
                Continue For
            End If

            If CanResolveHostEntryOnline(HostItem) Then
                Result.MissingDownloadable.Add(HostItem)
            Else
                Result.HostLocalOnly.Add(HostItem)
            End If
        Next

        Result.ExtraLocal = LocalEnabled.Where(Function(l) Not MatchedLocal.Contains(l)).ToList()
        Return Result
    End Function

    Public Shared Function BuildRemainingSyncIssues(Diff As ModSyncDiffResult,
                                                    Optional FailedTitles As ICollection(Of String) = Nothing,
                                                    Optional NonExactFallbacks As ICollection(Of String) = Nothing) As List(Of String)
        Dim Issues As New List(Of String)
        If Diff.IsVersionMismatch OrElse Diff.IsLoaderMismatch Then Issues.Add("游戏版本或加载器不一致")
        If FailedTitles IsNot Nothing AndAlso FailedTitles.Any() Then Issues.Add($"{FailedTitles.Count} 个未找到直链（{String.Join("、", FailedTitles.Take(3))}）")
        If NonExactFallbacks IsNot Nothing AndAlso NonExactFallbacks.Any() Then Issues.Add($"{NonExactFallbacks.Count} 个仅匹配到替代版本")
        If Diff.DisabledMatch.Any() Then Issues.Add($"仍有 {Diff.DisabledMatch.Count} 个本地已禁用 Mod 待启用")
        If Diff.MissingDownloadable.Any() Then Issues.Add($"仍缺 {Diff.MissingDownloadable.Count} 个可下载 Mod")
        Dim MismatchTotal = Diff.MismatchDownloadable.Count + Diff.MismatchLocalOnly.Count
        If MismatchTotal > 0 Then Issues.Add($"仍有 {MismatchTotal} 个 Mod 版本/文件不一致")
        If Diff.HostLocalOnly.Any() Then Issues.Add($"{Diff.HostLocalOnly.Count} 个房主私有 Mod 需手动传送")
        If Diff.ExtraLocal.Any() Then Issues.Add($"本地仍多出 {Diff.ExtraLocal.Count} 个已启用 Mod")
        Return Issues
    End Function

    Public Shared Function BuildCurrentInstanceManifestJson() As String
        Dim TargetInst = McInstanceSelected
        If TargetInst Is Nothing Then Return "{}"
        Dim Manifest = ModFriends.BuildInstanceModSyncManifest(TargetInst)
        If Manifest Is Nothing Then Return "{}"
        Return Newtonsoft.Json.JsonConvert.SerializeObject(Manifest)
    End Function

    Public Shared Sub StartFriendModSyncDiff(HostManifest As ModFriends.ModSyncManifest,
                                             Optional SilentIfSynced As Boolean = False,
                                             Optional OnStatusUpdate As Action(Of String) = Nothing,
                                             Optional P2pPullMod As Func(Of String, String, Boolean) = Nothing)
        If HostManifest Is Nothing OrElse (String.IsNullOrEmpty(HostManifest.Ver) AndAlso (HostManifest.Items Is Nothing OrElse Not HostManifest.Items.Any())) Then
            OnStatusUpdate?.Invoke("Mod 核对：房主端未选择游戏实例，已跳过比对")
            If Not SilentIfSynced Then RunInUi(Sub() Hint("房主端未选中任何游戏实例，暂无 Mod 清单可供对比！", HintType.Blue))
            Return
        End If
        Dim TargetInstance = McInstanceSelected
        If TargetInstance Is Nothing Then
            OnStatusUpdate?.Invoke("Mod 核对：本地尚未选择游戏实例")
            If Not SilentIfSynced Then RunInUi(Sub() Hint("请先在左侧/主界面选择你要对比与同步的本地游戏实例！", HintType.Red))
            Return
        End If
        If Not SilentIfSynced Then
            RunInUi(Sub() Hint($"正在与实例「{TargetInstance.Name}」对比 {HostManifest.Items.Count} 个 Mod 差异……", HintType.Blue))
        End If

        RunInNewThread(
        Sub()
            Try
                Dim Diff = ScanAndCompareHostMods(HostManifest, TargetInstance)
                If Diff.IsFullySynced Then
                    OnStatusUpdate?.Invoke($"Mod 一致性：与房主完全一致 ({Diff.ExactCount} 个) ✓")
                    If Not SilentIfSynced Then
                        RunInUi(Sub() MyMsgBox($"当前实例「{TargetInstance.Name}」({TargetInstance.Version.VanillaName}, {TargetInstance.Version.ModLoaders}) 的 {Diff.ExactCount} 个 Mod 与房主完全一致，可以直接联机！", "Mod 同步对比完成", "太棒了"))
                    End If
                    Return
                End If
                Dim P2pPendingCount = If(P2pPullMod IsNot Nothing, Diff.HostLocalOnly.Count + Diff.MismatchLocalOnly.Count, 0)
                OnStatusUpdate?.Invoke($"Mod 一致性：发现差异（{Diff.MissingDownloadable.Count + Diff.MismatchDownloadable.Count + Diff.DisabledMatch.Count + P2pPendingCount} 项待同步）")

                Dim Sb As New System.Text.StringBuilder()
                Sb.AppendLine($"房主环境：{HostManifest.Ver} ({CType(HostManifest.Ldr, ModLoaders)}) · 共 {HostManifest.Items.Count} 个 Mod")
                Sb.AppendLine($"本地实例：{TargetInstance.Name} ({TargetInstance.Version.VanillaName}, {TargetInstance.Version.ModLoaders}) · 启用 {Diff.LocalEnabledCount} 个 · 一致 {Diff.ExactCount} 个")
                Sb.AppendLine()
                If Diff.IsVersionMismatch OrElse Diff.IsLoaderMismatch Then
                    Sb.AppendLine("【游戏环境不一致（请确认选对了实例）：】")
                    If Diff.IsVersionMismatch Then Sb.AppendLine($"• MC 版本不一致：房主 {HostManifest.Ver}，本地 {TargetInstance.Version.VanillaName}")
                    If Diff.IsLoaderMismatch Then Sb.AppendLine($"• Mod 加载器不一致：房主 {CType(HostManifest.Ldr, ModLoaders)}，本地 {TargetInstance.Version.ModLoaders}")
                    Sb.AppendLine()
                End If
                If Diff.DisabledMatch.Any() Then
                    Sb.AppendLine($"【本地已有但已禁用（可一键直接启用）：{Diff.DisabledMatch.Count} 个】")
                    For Each E In Diff.DisabledMatch.Take(6) : Sb.AppendLine($"• {E.Display}（{E.File.Name}）") : Next
                    If Diff.DisabledMatch.Count > 6 Then Sb.AppendLine($"……等共 {Diff.DisabledMatch.Count} 个")
                    Sb.AppendLine()
                End If
                If Diff.MissingDownloadable.Any() Then
                    Sb.AppendLine($"【缺少（可一键自动检索下载）：{Diff.MissingDownloadable.Count} 个】")
                    For Each E In Diff.MissingDownloadable.Take(8) : Sb.AppendLine($"• {E.N}") : Next
                    If Diff.MissingDownloadable.Count > 8 Then Sb.AppendLine($"……等共 {Diff.MissingDownloadable.Count} 个")
                    Sb.AppendLine()
                End If
                If Diff.MismatchDownloadable.Any() Then
                    Sb.AppendLine($"【版本或文件不一致（可一键对齐）：{Diff.MismatchDownloadable.Count} 个】")
                    For Each E In Diff.MismatchDownloadable.Take(6) : Sb.AppendLine($"• {E.HostEntry.N}（本地：{E.LocalMod.File.Name}）") : Next
                    If Diff.MismatchDownloadable.Count > 6 Then Sb.AppendLine($"……等共 {Diff.MismatchDownloadable.Count} 个")
                    Sb.AppendLine()
                End If
                If Diff.MismatchLocalOnly.Any() Then
                    Dim LocalMismatchTitle = If(P2pPullMod IsNot Nothing,
                        $"【同名但文件哈希不一致（可经虚网 P2P 覆盖对齐）：{Diff.MismatchLocalOnly.Count} 个】",
                        $"【同名但文件哈希不一致（无线上源，需手动替换）：{Diff.MismatchLocalOnly.Count} 个】")
                    Sb.AppendLine(LocalMismatchTitle)
                    For Each E In Diff.MismatchLocalOnly.Take(5) : Sb.AppendLine($"• {E.LocalMod.File.Name}") : Next
                    If Diff.MismatchLocalOnly.Count > 5 Then Sb.AppendLine($"……等共 {Diff.MismatchLocalOnly.Count} 个")
                    Sb.AppendLine()
                End If
                If Diff.ExtraLocal.Any() Then
                    Sb.AppendLine($"【仅本地多出（未在房主清单中）：{Diff.ExtraLocal.Count} 个】")
                    For Each E In Diff.ExtraLocal.Take(6) : Sb.AppendLine($"• {E.Display}（{E.File.Name}）") : Next
                    If Diff.ExtraLocal.Count > 6 Then Sb.AppendLine($"……等共 {Diff.ExtraLocal.Count} 个")
                    Sb.AppendLine()
                End If
                If Diff.HostLocalOnly.Any() Then
                    Dim HostLocalTitle = If(P2pPullMod IsNot Nothing,
                        $"【房主私有本地 Mod（无线上源，支持虚网 P2P 直接拉取补齐）：{Diff.HostLocalOnly.Count} 个】",
                        $"【房主私有本地 Mod（无线上源，需手动传送）：{Diff.HostLocalOnly.Count} 个】")
                    Sb.AppendLine(HostLocalTitle)
                    For Each E In Diff.HostLocalOnly.Take(5) : Sb.AppendLine($"• {E.F}") : Next
                    If Diff.HostLocalOnly.Count > 5 Then Sb.AppendLine($"……等共 {Diff.HostLocalOnly.Count} 个")
                End If

                Dim NeedDownloadOrEnableCount = Diff.MissingDownloadable.Count + Diff.MismatchDownloadable.Count + Diff.DisabledMatch.Count + P2pPendingCount
                Dim HasExtra = Diff.ExtraLocal.Any()
                RunInUi(
                Sub()
                    Dim Btn1 As String
                    Dim Btn2 As String = ""
                    Dim Btn3 As String = ""
                    If NeedDownloadOrEnableCount > 0 AndAlso HasExtra Then
                        Btn1 = "一键补齐 / 启用差异 Mod"
                        Btn2 = "完全同步并禁用多出 Mod"
                        Btn3 = "取消"
                    ElseIf NeedDownloadOrEnableCount > 0 Then
                        Btn1 = "一键补齐 / 启用差异 Mod"
                        Btn2 = "关闭"
                    ElseIf HasExtra Then
                        Btn1 = "禁用本地多出 Mod"
                        Btn2 = "关闭"
                    Else
                        Btn1 = "关闭"
                    End If

                    Dim Choice = MyMsgBox(Sb.ToString().TrimEnd(), "好友联机 Mod 差异对比", Btn1, Btn2, Btn3)
                    If Choice = 0 OrElse Choice = 3 Then Return
                    If NeedDownloadOrEnableCount = 0 AndAlso Not HasExtra Then Return
                    If Choice = 2 AndAlso Not (NeedDownloadOrEnableCount > 0 AndAlso HasExtra) Then Return

                    Dim DisableExtra As Boolean = (NeedDownloadOrEnableCount = 0 AndAlso HasExtra AndAlso Choice = 1) OrElse (NeedDownloadOrEnableCount > 0 AndAlso HasExtra AndAlso Choice = 2)
                    ExecuteFriendModSync(
                        TargetInstance, HostManifest,
                        Diff.MissingDownloadable, Diff.MismatchDownloadable, Diff.DisabledMatch,
                        If(DisableExtra, Diff.ExtraLocal, New List(Of LocalResourceFile)),
                        OnCompleted:=If(OnStatusUpdate IsNot Nothing, Sub(ActionDesc, AllOk) OnStatusUpdate($"Mod 同步结果：{ActionDesc}"), Nothing),
                        P2pMissingList:=If(P2pPullMod IsNot Nothing, Diff.HostLocalOnly, Nothing),
                        P2pMismatchList:=If(P2pPullMod IsNot Nothing, Diff.MismatchLocalOnly, Nothing),
                        P2pPullMod:=P2pPullMod)
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "好友 Mod 差异对比失败")
                RunInUi(Sub() Hint($"Mod 差异对比失败：{ex.Message}", HintType.Red))
            End Try
        End Sub, "Friend Mod Diff")
    End Sub

    Public Shared Sub VerifyFriendModSyncAfterApply(TargetInstance As McInstance,
                                                    HostManifest As ModFriends.ModSyncManifest,
                                                    DownloadedCount As Integer,
                                                    ReEnabledCount As Integer,
                                                    DisabledCount As Integer,
                                                    FailedTitles As ICollection(Of String),
                                                    NonExactFallbacks As ICollection(Of String),
                                                    Optional OnCompleted As Action(Of String, Boolean) = Nothing)
        RunInNewThread(
        Sub()
            Try
                Dim Scan = ScanInstanceModsWithCache(TargetInstance)
                Dim PostDiff = CompareHostModsWithLocal(HostManifest, Scan.ModList, TargetInstance)
                Dim Issues = BuildRemainingSyncIssues(PostDiff, FailedTitles, NonExactFallbacks)
                Dim AllSucceeded = Not Issues.Any()
                RunInUi(
                Sub()
                    If PageInstanceLeft.Instance Is TargetInstance Then FrmInstanceMod?.ReloadModList(True)
                    Dim Parts As New List(Of String)
                    If DownloadedCount > 0 Then Parts.Add($"下载补齐 {DownloadedCount} 个")
                    If ReEnabledCount > 0 Then Parts.Add($"重新启用 {ReEnabledCount} 个")
                    If DisabledCount > 0 Then Parts.Add($"禁用多余 {DisabledCount} 个")
                    Dim ActionDesc = If(Parts.Any(), String.Join("、", Parts), "未发生变更")
                    If AllSucceeded Then
                        Hint($"Mod 已完全同步：{ActionDesc}（当前 {PostDiff.ExactCount} 个与目标一致）", HintType.Green)
                    Else
                        Hint($"已执行同步（{ActionDesc}），仍存在差异：{String.Join("，", Issues)}；请核对", HintType.Blue)
                    End If
                    OnCompleted?.Invoke(ActionDesc, AllSucceeded)
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "同步后校验 Mod 状态失败")
                RunInUi(Sub()
                            Hint($"已同步 {DownloadedCount + ReEnabledCount} 个 Mod，请重新核对结果", HintType.Blue)
                            OnCompleted?.Invoke($"已处理 {DownloadedCount + ReEnabledCount} 个 Mod", False)
                        End Sub)
            End Try
        End Sub, "Verify Friend Mod Sync")
    End Sub

    Private Shared Function PickBestCompatibleVersion(Versions As IEnumerable(Of ResourceVersion),
                                                      LocalEntry As ModFriends.ModSyncEntry,
                                                      TargetVer As String,
                                                      TargetLdr As ModLoaders,
                                                      ByRef IsNonExactFallback As Boolean) As ResourceVersion
        IsNonExactFallback = False
        If Versions Is Nothing Then Return Nothing
        Dim List = Versions.ToList()
        Dim Exact = List.FirstOrDefault(Function(v) v.DownloadAvailable AndAlso Not String.IsNullOrEmpty(LocalEntry.H) AndAlso LocalEntry.H.Equals(v.Hash, StringComparison.OrdinalIgnoreCase))
        If Exact IsNot Nothing Then Return Exact
        If Not String.IsNullOrEmpty(LocalEntry.V) Then
            Exact = List.FirstOrDefault(Function(v) v.DownloadAvailable AndAlso v.Id.Equals(LocalEntry.V, StringComparison.OrdinalIgnoreCase) AndAlso (String.IsNullOrEmpty(LocalEntry.H) OrElse LocalEntry.H.Equals(v.Hash, StringComparison.OrdinalIgnoreCase)))
            If Exact IsNot Nothing Then Return Exact
        End If
        Exact = List.Where(Function(v) v.DownloadAvailable AndAlso v.ResourceType.HasFlagF(ResourceTypes.Mod) AndAlso
                                       (TargetLdr = ModLoaders.None OrElse v.ModLoaders.HasFlagF(TargetLdr)) AndAlso
                                       v.GameVersions IsNot Nothing AndAlso v.GameVersions.Any(Function(gv) gv.Equals(TargetVer, StringComparison.OrdinalIgnoreCase))).
                     OrderByDescending(Function(v) v.ReleaseDate).FirstOrDefault()
        If Exact IsNot Nothing AndAlso Not String.IsNullOrEmpty(LocalEntry.H) AndAlso Not String.IsNullOrEmpty(Exact.Hash) AndAlso Exact.Hash.Length = LocalEntry.H.Length AndAlso Not LocalEntry.H.Equals(Exact.Hash, StringComparison.OrdinalIgnoreCase) Then
            IsNonExactFallback = True
        End If
        Return Exact
    End Function

    Private Shared Function ResolveHostEntryVersion(LocalEntry As ModFriends.ModSyncEntry,
                                                    TargetVer As String,
                                                    TargetLdr As ModLoaders,
                                                    ByRef IsNonExactFallback As Boolean) As ResourceVersion
        IsNonExactFallback = False
        '1. 优先按 40 位十六进制 SHA1 Hash 在 Modrinth 反查精确文件版本
        Dim CleanHash = If(LocalEntry.H, "").Trim().ToLowerInvariant()
        If System.Text.RegularExpressions.Regex.IsMatch(CleanHash, "^[0-9a-f]{40}$") Then
            Try
                Dim VerTok = DlModRequest($"https://api.modrinth.com/v2/version_file/{CleanHash}?algorithm=sha1")
                Dim VerObj = TryCast(VerTok, JObject)
                If VerObj IsNot Nothing Then
                    Dim ByHashVer = ResourceVersion.FromPlatformJson(VerObj, ResourceTypes.Mod)
                    If ByHashVer IsNot Nothing AndAlso ByHashVer.DownloadAvailable Then Return ByHashVer
                End If
            Catch
            End Try
        End If
        '2. 其次按校验过的 ProjectId 获取版本列表匹配
        Dim CleanProjId = If(LocalEntry.I, "").Trim()
        If System.Text.RegularExpressions.Regex.IsMatch(CleanProjId, "^[A-Za-z0-9_-]{1,64}$") Then
            Try
                Dim Plat As ResourcePlatforms = CType(LocalEntry.P, ResourcePlatforms)
                Dim Versions = ResourceVersion.FromProjectId(Uri.EscapeDataString(CleanProjId), Plat, LoadDependencies:=False)
                Dim Matched = PickBestCompatibleVersion(Versions, LocalEntry, TargetVer, TargetLdr, IsNonExactFallback)
                If Matched IsNot Nothing Then Return Matched
            Catch
            End Try
        End If
        '3. 兜底：按 Mod 名称或文件名在 Modrinth 精确匹配 title / slug
        Dim CleanQuery = If(Not String.IsNullOrWhiteSpace(LocalEntry.N), LocalEntry.N, IO.Path.GetFileNameWithoutExtension(If(LocalEntry.F, ""))).Trim()
        CleanQuery = System.Text.RegularExpressions.Regex.Replace(CleanQuery, "^(?:【[^】]+】|\[[^\]]+\])\s*", "").Trim()
        If Not String.IsNullOrWhiteSpace(CleanQuery) Then
            Try
                Dim SearchTok = DlModRequest($"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(CleanQuery)}&limit=5&facets=%5B%5B%22project_type:mod%22%5D%5D")
                Dim Hits = TryCast(SearchTok?("hits"), JArray)
                If Hits IsNot Nothing Then
                    Dim DashedQuery = CleanQuery.Replace(" ", "-")
                    For Each Hit In Hits.OfType(Of JObject)()
                        Dim HitTitle = If(Hit("title")?.ToString(), "").Trim()
                        Dim HitSlug = If(Hit("slug")?.ToString(), "").Trim()
                        If Not (String.Equals(HitTitle, CleanQuery, StringComparison.OrdinalIgnoreCase) OrElse
                                String.Equals(HitSlug, CleanQuery, StringComparison.OrdinalIgnoreCase) OrElse
                                String.Equals(HitSlug, DashedQuery, StringComparison.OrdinalIgnoreCase)) Then Continue For
                        Dim Pid = Hit("project_id")?.ToString()
                        If String.IsNullOrWhiteSpace(Pid) Then Continue For
                        Dim Versions = ResourceVersion.FromProjectId(Pid, ResourcePlatforms.Modrinth, LoadDependencies:=False)
                        Dim Matched = PickBestCompatibleVersion(Versions, LocalEntry, TargetVer, TargetLdr, IsNonExactFallback)
                        If Matched IsNot Nothing Then Return Matched
                    Next
                End If
            Catch
            End Try
        End If
        Return Nothing
    End Function

    Public Shared Sub ExecuteFriendModSync(TargetInstance As McInstance, HostManifest As ModFriends.ModSyncManifest,
                                           MissingList As List(Of ModFriends.ModSyncEntry),
                                           MismatchList As List(Of (HostEntry As ModFriends.ModSyncEntry, LocalMod As LocalResourceFile)),
                                           EnableDisabledList As List(Of LocalResourceFile),
                                           DisableExtraList As List(Of LocalResourceFile),
                                           Optional OnCompleted As Action(Of String, Boolean) = Nothing,
                                           Optional P2pMissingList As List(Of ModFriends.ModSyncEntry) = Nothing,
                                           Optional P2pMismatchList As List(Of (HostEntry As ModFriends.ModSyncEntry, LocalMod As LocalResourceFile)) = Nothing,
                                           Optional P2pPullMod As Func(Of String, String, Boolean) = Nothing)
        Hint("正在开始同步好友 Mod 差异……", HintType.Blue)
        RunInNewThread(
        Sub()
            Try
                Dim ModsDir = TargetInstance.PathIndie & "mods\"
                IO.Directory.CreateDirectory(ModsDir)

                '1. 重新启用本地已有的 .disabled / .old 同款 Mod
                Dim ReEnabledCount As Integer = 0
                If EnableDisabledList IsNot Nothing Then
                    For Each DisMod In EnableDisabledList
                        Try
                            If IO.File.Exists(DisMod.File.FullName) AndAlso Not IO.File.Exists(DisMod.EnabledFullName) Then
                                FileUtils.Move(DisMod.File.FullName, DisMod.EnabledFullName)
                                ReEnabledCount += 1
                            End If
                        Catch ex As Exception
                            Logger.Warn(ex, $"重新启用本地 Mod 失败：{DisMod.File.FullName}")
                        End Try
                    Next
                End If

                '2. 可选禁用多余本地 Mod（重命名为 .disabled，不删除）
                Dim DisabledCount As Integer = 0
                For Each Extra In DisableExtraList
                    Try
                        Dim NewPath = PathUtils.RemoveExtendedPrefix(Extra.File.FullName) & ".disabled"
                        If Not IO.File.Exists(NewPath) AndAlso IO.File.Exists(Extra.File.FullName) Then
                            FileUtils.Move(Extra.File.FullName, NewPath)
                            DisabledCount += 1
                        End If
                    Catch ex As Exception
                        Logger.Warn(ex, $"禁用多余 Mod 失败：{Extra.File.FullName}")
                    End Try
                Next

                '3. 三段式解析并下载缺失/版本不一致的 Mod
                Dim PendingWork As New List(Of (HostEntry As ModFriends.ModSyncEntry, OldMod As LocalResourceFile))
                If MissingList IsNot Nothing Then
                    For Each MEntry In MissingList
                        PendingWork.Add((MEntry, Nothing))
                    Next
                End If
                If MismatchList IsNot Nothing Then
                    For Each Pair In MismatchList
                        PendingWork.Add((Pair.HostEntry, Pair.LocalMod))
                    Next
                End If

                Dim FailedTitles As New System.Collections.Concurrent.ConcurrentBag(Of String)
                Dim NonExactFallbackTitles As New System.Collections.Concurrent.ConcurrentBag(Of String)
                Dim BagItems As New System.Collections.Concurrent.ConcurrentBag(Of (NetFileObj As NetFile, OldMod As LocalResourceFile))
                Dim P2pFallbackWork As New System.Collections.Concurrent.ConcurrentBag(Of (HostEntry As ModFriends.ModSyncEntry, OldMod As LocalResourceFile))
                If PendingWork.Any() Then
                    Dim PendingQueue As New System.Collections.Concurrent.ConcurrentQueue(Of (HostEntry As ModFriends.ModSyncEntry, OldMod As LocalResourceFile))(PendingWork)
                    Dim WorkerCount As Integer = Math.Min(4, PendingWork.Count)
                    Dim TargetLdr As ModLoaders = CType(HostManifest.Ldr, ModLoaders)
                    Dim Workers As New List(Of System.Threading.Thread)(WorkerCount)
                    For w As Integer = 1 To WorkerCount
                        Dim T = RunInNewThread(
                        Sub()
                            Dim WorkItem As (HostEntry As ModFriends.ModSyncEntry, OldMod As LocalResourceFile) = Nothing
                            While PendingQueue.TryDequeue(WorkItem)
                                Dim LocalEntry = WorkItem.HostEntry
                                Try
                                    Dim NonExact As Boolean = False
                                    Dim Exact = ResolveHostEntryVersion(LocalEntry, HostManifest.Ver, TargetLdr, NonExact)
                                    If Exact IsNot Nothing Then
                                        If NonExact Then NonExactFallbackTitles.Add(LocalEntry.N)
                                        Dim Nf = Exact.ToNetFile(ModsDir, ResourceVersion.DownloadReason.Standalone, HostManifest.Ver, TargetLdr)
                                        BagItems.Add((Nf, WorkItem.OldMod))
                                    ElseIf P2pPullMod IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(LocalEntry.F) Then
                                        P2pFallbackWork.Add(WorkItem)
                                    Else
                                        FailedTitles.Add(If(Not String.IsNullOrWhiteSpace(LocalEntry.N), LocalEntry.N, LocalEntry.F))
                                    End If
                                Catch
                                    If P2pPullMod IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(LocalEntry.F) Then
                                        P2pFallbackWork.Add(WorkItem)
                                    Else
                                        FailedTitles.Add(If(Not String.IsNullOrWhiteSpace(LocalEntry.N), LocalEntry.N, LocalEntry.F))
                                    End If
                                End Try
                            End While
                        End Sub, $"SyncMod Worker {w}")
                        Workers.Add(T)
                    Next
                    For Each T In Workers
                        T.Join()
                    Next
                End If

                '3b. 虚网 P2P 直传补齐私有 Mod 与线上解析回退项
                Dim P2pPulledCount As Integer = 0
                If P2pPullMod IsNot Nothing Then
                    Dim AllP2pWork As New List(Of (HostEntry As ModFriends.ModSyncEntry, OldMod As LocalResourceFile))
                    If P2pMissingList IsNot Nothing Then
                        For Each Pm In P2pMissingList
                            AllP2pWork.Add((Pm, Nothing))
                        Next
                    End If
                    If P2pMismatchList IsNot Nothing Then
                        For Each Pmm In P2pMismatchList
                            AllP2pWork.Add(Pmm)
                        Next
                    End If
                    For Each Fb In P2pFallbackWork
                        AllP2pWork.Add(Fb)
                    Next
                    For Each Item In AllP2pWork
                        Dim RawFileName = IO.Path.GetFileName(If(Item.HostEntry.F, "").Trim())
                        If String.IsNullOrWhiteSpace(RawFileName) Then
                            FailedTitles.Add(If(Not String.IsNullOrWhiteSpace(Item.HostEntry.N), Item.HostEntry.N, "未知 Mod"))
                            Continue For
                        End If
                        Dim DestPath = IO.Path.Combine(ModsDir, RawFileName)
                        If P2pPullMod(RawFileName, DestPath) Then
                            P2pPulledCount += 1
                            If Item.OldMod IsNot Nothing Then
                                Try
                                    Dim OldPath = PathUtils.RemoveExtendedPrefix(Item.OldMod.File.FullName)
                                    If Not String.Equals(DestPath, OldPath, StringComparison.OrdinalIgnoreCase) AndAlso IO.File.Exists(OldPath) Then
                                        FileUtils.Delete(OldPath, True)
                                    End If
                                Catch
                                End Try
                            End If
                        Else
                            FailedTitles.Add(If(Not String.IsNullOrWhiteSpace(Item.HostEntry.N), Item.HostEntry.N, RawFileName))
                        End If
                    Next
                End If

                '按本地路径去重，并记录仅在下载完成后才回收的旧 Mod
                Dim NetFiles As New List(Of NetFile)
                Dim SeenPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                Dim ReplacementPairs As New List(Of (NewPath As String, OldMod As LocalResourceFile))
                For Each Item In BagItems
                    Dim NormLocal = PathUtils.RemoveExtendedPrefix(Item.NetFileObj.LocalPath)
                    If SeenPaths.Add(NormLocal) Then
                        NetFiles.Add(Item.NetFileObj)
                    End If
                    If Item.OldMod IsNot Nothing Then
                        ReplacementPairs.Add((NormLocal, Item.OldMod))
                    End If
                Next

                If NetFiles.Any() Then
                    Dim FailedSnap = FailedTitles.ToList()
                    Dim FallbackSnap = NonExactFallbackTitles.ToList()
                    Dim P2pCountSnap = P2pPulledCount
                    Dim CompletionFired As Integer = 0
                    Dim SubLoaders As New List(Of LoaderBase) From {
                        New LoaderTask(Of Integer, Integer)("清理损坏的目标缓存",
                        Sub()
                            For Each Nf In NetFiles
                                Try
                                    If IO.File.Exists(Nf.LocalPath) AndAlso Nf.Check IsNot Nothing AndAlso Nf.Check.Check(Nf.LocalPath) IsNot Nothing Then
                                        FileUtils.Delete(Nf.LocalPath, True)
                                    End If
                                Catch
                                End Try
                            Next
                        End Sub) With {.ProgressWeight = 1},
                        New LoaderDownload("下载好友联机 Mod", NetFiles) With {.ProgressWeight = NetFiles.Count * 1.5},
                        New LoaderTask(Of Integer, Integer)("清理被替换的旧版 Mod",
                        Sub()
                            For Each Pair In ReplacementPairs
                                Try
                                    Dim OldPath = PathUtils.RemoveExtendedPrefix(Pair.OldMod.File.FullName)
                                    If Not String.Equals(Pair.NewPath, OldPath, StringComparison.OrdinalIgnoreCase) AndAlso
                                       IO.File.Exists(Pair.NewPath) AndAlso IO.File.Exists(Pair.OldMod.File.FullName) Then
                                        FileUtils.Delete(Pair.OldMod.File.FullName, True)
                                    End If
                                Catch exDel As Exception
                                    Logger.Warn(exDel, $"删除旧版本 Mod 失败: {Pair.OldMod.File.FullName}")
                                End Try
                            Next
                        End Sub) With {.ProgressWeight = 1}
                    }
                    Dim SyncLoader As New LoaderCombo(Of Integer)($"同步好友 Mod（{TargetInstance.Name}）", SubLoaders) With {
                        .OnStateChanged =
                        Sub(L)
                            LoaderStateChangedHintOnly(L)
                            If L.State = LoadState.Finished Then
                                If Threading.Interlocked.Exchange(CompletionFired, 1) = 0 Then
                                    VerifyFriendModSyncAfterApply(TargetInstance, HostManifest, NetFiles.Count + P2pCountSnap, ReEnabledCount, DisabledCount, FailedSnap, FallbackSnap, OnCompleted)
                                End If
                            ElseIf L.State = LoadState.Failed OrElse L.State = LoadState.Canceled Then
                                If Threading.Interlocked.Exchange(CompletionFired, 1) = 0 Then
                                    RunInUi(Sub() OnCompleted?.Invoke("下载失败或已取消", False))
                                End If
                            End If
                        End Sub
                    }
                    RunInUi(
                    Sub()
                        SyncLoader.Start(1)
                        LoaderTaskbarAdd(SyncLoader)
                        FrmMain.BtnExtraDownload.ShowRefresh()
                        FrmMain.BtnExtraDownload.Ribble()
                    End Sub)
                Else
                    If DisabledCount > 0 OrElse ReEnabledCount > 0 OrElse P2pPulledCount > 0 Then
                        VerifyFriendModSyncAfterApply(TargetInstance, HostManifest, P2pPulledCount, ReEnabledCount, DisabledCount, FailedTitles.ToList(), NonExactFallbackTitles.ToList(), OnCompleted)
                    Else
                        RunInUi(
                        Sub()
                            If PageInstanceLeft.Instance Is TargetInstance Then FrmInstanceMod?.ReloadModList(True)
                            Hint("未能解析到可自动下载的差异 Mod。", HintType.Red)
                            OnCompleted?.Invoke("无可用下载源", False)
                        End Sub)
                    End If
                End If
            Catch ex As Exception
                Logger.Error(ex, "执行好友 Mod 同步失败")
                RunInUi(
                    Sub()
                        Hint($"同步失败：{ex.Message}", HintType.Red)
                        OnCompleted?.Invoke($"同步异常：{ex.Message}", False)
                    End Sub)
            End Try
        End Sub, "Execute Friend Mod Sync")
    End Sub

    Public Shared Function GetFriendAgreementText() As String
        Return "在使用 PCL Linking 好友与联机聊天功能前，请仔细阅读并同意以下条款：" & vbCrLf & vbCrLf &
               "1. 用户不得利用本服务制作、复制、传播违法有害信息；" & vbCrLf &
               "2. 发现违法信息的处置、配合主管部门调查义务；" & vbCrLf &
               "3. 不得对未成年人不良引导；" & vbCrLf &
               "4. 严格遵守《中华人民共和国法律》。" & vbCrLf & vbCrLf &
               "点击「同意并继续」即表示你知悉并承诺遵守上述全部约定。"
    End Function

    Public Shared Function EnsureFriendAgreementAccepted() As Boolean
        If Settings.Get(Of Boolean)("FriendsAgreementAccepted") Then Return True
        Dim Choice = MyMsgBox(GetFriendAgreementText(), "好友服务使用协议", "同意并继续", "不同意")
        If Choice = 1 Then
            Settings.Set("FriendsAgreementAccepted", True)
            Return True
        End If
        Return False
    End Function

    Private Sub BtnFriendAgreement_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnFriendAgreement.Click
        If Settings.Get(Of Boolean)("FriendsAgreementAccepted") Then
            MyMsgBox(GetFriendAgreementText(), "好友服务使用协议（已同意）", "关闭")
        Else
            EnsureFriendAgreementAccepted()
        End If
    End Sub

    Private Sub BtnAddFriend_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnAddFriend.Click
        If Not EnsureFriendAgreementAccepted() Then
            Hint("请先阅读并同意好友服务使用协议后再添加！", HintType.Blue)
            Return
        End If
        Dim Dlg As New WinAddFriend With {.Owner = FrmMain}
        If Dlg.ShowDialog() = True AndAlso Not String.IsNullOrWhiteSpace(Dlg.ResultTargetId) Then
            CurrentIsGroup = Dlg.ResultIsGroup
            CurrentFriendId = Dlg.ResultTargetId
            RefreshAll(ForceScroll:=True)
        End If
    End Sub

    Private Sub BtnGroupCard_Click() Handles BtnGroupCard.Click
        If Not CurrentIsGroup OrElse CurrentFriendId = "" Then Return
        Dim G = ModFriends.FindGroup(CurrentFriendId)
        If G Is Nothing Then Return
        ClipboardSet(ModFriends.FormatGroupCard(G), False)
        Hint($"已复制群聊「{G.Name}」名片到剪贴板！", HintType.Green)
    End Sub

    Private Sub BtnInviteToGroup_Click() Handles BtnInviteToGroup.Click
        If Not CurrentIsGroup OrElse CurrentFriendId = "" Then Return
        Dim G = ModFriends.FindGroup(CurrentFriendId)
        If G Is Nothing Then Return
        Dim FriendsList = ModFriends.GetFriendsSnapshot()
        If Not FriendsList.Any() Then
            Hint("好友列表为空，可点「群名片」复制群号与密钥直接发给朋友。", HintType.Blue)
            Return
        End If
        Dim Radios As New List(Of IMyRadio)
        For Each F In FriendsList
            Radios.Add(New MyRadioBox With {.Text = F.DisplayName})
        Next
        Dim Picked = MyMsgBoxSelect(Radios.ToArray(), $"邀请好友加入群聊「{G.Name}」：", "发送入群邀请", "取消")
        If Picked Is Nothing Then Return
        Dim TargetFriend = FriendsList(Picked.Value)
        RunInNewThread(
            Sub()
                Dim Msg As New ModFriends.FriendMessage With {
                    .Kind = "group_invite",
                    .Text = $"邀请你加入 PCL Linking 群聊「{G.Name}」",
                    .GroupInviteId = G.Id,
                    .GroupInviteName = G.Name,
                    .GroupInviteSecret = G.Secret
                }
                Dim Err = ModFriends.SendRichMessage(TargetFriend.Id, False, Msg)
                RunInUi(Sub() Hint(If(Err, $"已向 {TargetFriend.DisplayName} 发送入群邀请！"), If(Err Is Nothing, HintType.Green, HintType.Red)))
            End Sub, "Send Group Invite")
    End Sub

    Private Sub BtnRenameGroup_Click() Handles BtnRenameGroup.Click
        If Not CurrentIsGroup OrElse CurrentFriendId = "" Then Return
        Dim G = ModFriends.FindGroup(CurrentFriendId)
        If G Is Nothing Then Return
        Dim NewName = MyMsgBoxInput("修改群聊名称", $"输入群聊 {G.Id} 的新名称：", G.Name, HintText:="群名称")
        If String.IsNullOrWhiteSpace(NewName) Then Return
        ModFriends.RenameGroup(G.Id, NewName)
        RefreshAll(ForceScroll:=False)
        Hint("群名称已更新！", HintType.Green)
    End Sub

    Private Sub BtnLeaveGroup_Click() Handles BtnLeaveGroup.Click
        If Not CurrentIsGroup OrElse CurrentFriendId = "" Then Return
        Dim G = ModFriends.FindGroup(CurrentFriendId)
        If G Is Nothing Then Return
        If MyMsgBox($"确定要退出群聊「{G.Name}」并删除本地群消息记录吗？", "退出群聊", "退出", "取消", IsWarn:=True) <> 1 Then Return
        ModFriends.LeaveGroup(G.Id)
        CurrentFriendId = ""
        CurrentIsGroup = False
        RefreshAll(ForceScroll:=False)
        Hint($"已退出群聊「{G.Name}」。", HintType.Blue)
    End Sub

    Private Sub BtnRemark_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRemark.Click
        If CurrentIsGroup OrElse CurrentFriendId = "" Then Return
        Dim F = ModFriends.FindFriend(CurrentFriendId)
        If F Is Nothing Then Return
        Dim NewRemark As String = MyMsgBoxInput("修改备注",
            $"为好友 {F.Id} 设置备注名（留空将恢复显示 ID）：",
            DefaultInput:=F.Remark, HintText:="好友备注名")
        If NewRemark Is Nothing Then Return
        ModFriends.UpdateRemark(F.Id, NewRemark)
        RefreshAll(ForceScroll:=False)
        Hint("备注已更新。", HintType.Green)
    End Sub

    Private Sub BtnClearHistory_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnClearHistory.Click
        If CurrentFriendId = "" Then Return
        If MyMsgBox("是否清空当前会话的所有本地聊天记录？此操作不可撤销。",
                    "清空聊天记录", "清空", "取消", IsWarn:=True) <> 1 Then Return
        If CurrentIsGroup Then
            ModFriends.ClearGroupHistory(CurrentFriendId)
        Else
            ModFriends.ClearHistory(CurrentFriendId)
        End If
        RefreshFriends()
        RenderMessages(ForceScroll:=False)
        Hint("本地聊天记录已清空。", HintType.Green)
    End Sub

    Private Sub BtnRemoveFriend_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRemoveFriend.Click
        If CurrentIsGroup OrElse CurrentFriendId = "" Then
            Hint("请先选中一位好友！", HintType.Red)
            Return
        End If
        Dim F = ModFriends.FindFriend(CurrentFriendId)
        Dim Name As String = If(F Is Nothing, CurrentFriendId, F.DisplayName)
        Dim Choice = MyMsgBoxSelect(
            {CType(New MyRadioBox With {.Text = "仅从列表删除：保留聊天记录，之后可重新添加。"}, IMyRadio),
             CType(New MyRadioBox With {.Text = "删除并拉黑：清空记录并屏蔽此 ID，之后不接收消息。"}, IMyRadio)},
            $"要如何删除 {Name}？", "删除", "取消", IsWarn:=True)
        If Choice Is Nothing Then Return
        ModFriends.RemoveFriend(CurrentFriendId, Choice = 1)
        CurrentFriendId = ""
        RefreshAll(ForceScroll:=False)
        Hint(If(Choice = 1, "已删除并加入黑名单。", "已从好友列表删除。"), HintType.Green)
    End Sub

    Private Sub BtnUnblock_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnUnblock.Click
        Dim Blocked = ModFriends.BlockedIds
        If Blocked.Count = 0 Then
            Hint("黑名单是空的。", HintType.Blue)
            Return
        End If
        Dim Radios As New List(Of IMyRadio)
        For Each BId In Blocked
            Radios.Add(New MyRadioBox With {.Text = $"好友 ID：{BId}"})
        Next
        Dim Selected = MyMsgBoxSelect(Radios.ToArray(), "选择要解除屏蔽的好友 ID（解除后可重新添加好友）：", "解除屏蔽", "取消")
        If Selected Is Nothing Then Return
        ModFriends.Unblock(Blocked(Selected.Value))
        RefreshCard()
        Hint($"已解除对 {Blocked(Selected.Value)} 的屏蔽。", HintType.Green)
    End Sub

    Private Sub BtnCopyCard_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnCopyCard.Click
        Dim D = ModFriends.GetData()
        ClipboardSet($"PCL Linking 好友名片：ID {D.MyId}，密码 {D.MyPassword}", False)
        Hint("名片已复制，私下发给朋友吧！", HintType.Green)
    End Sub

    Private Sub BtnSetPassword_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnSetPassword.Click
        Dim Input As String = MyMsgBoxInput("设置好友密码",
            $"至少 8 位。修改后所有好友都需要重新添加你的新密码，否则消息会被拒绝。{vbCrLf}当前密码：{ModFriends.GetData().MyPassword}",
            HintText:="新密码（至少 8 位）")
        If Input Is Nothing Then Return
        Dim Err As String = ModFriends.SetMyPassword(Input)
        If Err IsNot Nothing Then
            Hint(Err, HintType.Red)
            Return
        End If
        RefreshCard()
        Hint("密码已更新，请将新名片发送给好友。", HintType.Green)
    End Sub

    Private Sub BtnRegenerate_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRegenerate.Click
        If MyMsgBox("重新生成会得到一个全新的 20 位密码，所有好友都必须重新添加你才能继续聊天。确定吗？",
                    "重新生成密码", "确定", "取消", IsWarn:=True) <> 1 Then Return
        ModFriends.RegenerateMyPassword()
        RefreshCard()
        Hint("已生成新密码，请记得同步给好友。", HintType.Green)
    End Sub

    Private Sub BtnRelay_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnRelay.Click
        Dim EffProvider = ModFriends.ResolveEffectiveFileStorageProvider()
        Dim ModeLabel = ModFriends.GetFileStorageMode()
        Dim PdOk = ModFriends.HasPixeldrainApiKey()
        Dim DavCfg = ModFriends.GetWebDavConfig()
        Dim S3Cfg = ModFriends.GetS3Config()

        Dim MenuItems As IMyRadio() = {
            New MyRadioBox With {.Text = $"消息中继地址 (ntfy)：{ModFriends.RelayUrl()}"},
            New MyRadioBox With {.Text = $"切换默认大文件托管后端（模式: {ModeLabel} -> 实际: {EffProvider}）"},
            New MyRadioBox With {.Text = $"配置 Pixeldrain API Key（{If(PdOk, "已配置 · 120天托管", "未配置")}）"},
            New MyRadioBox With {.Text = $"配置 WebDAV / Alist 存储（{If(DavCfg IsNot Nothing, "已配置 · 支持永久直链", "未配置")}）"},
            New MyRadioBox With {.Text = $"配置 S3 兼容存储 R2 / OSS / MinIO（{If(S3Cfg IsNot Nothing, "已配置 · 预签名直下", "未配置")}）"},
            New MyRadioBox With {.Text = "大文件传输记录 / 断点续传缓存清理与总线审计"}
        }
        Dim Picked = MyMsgBoxSelect(MenuItems, "中继、大文件存储与传输管理", "确定", "关闭")
        If Picked Is Nothing Then Return

        Select Case Picked.Value
            Case 0
                Dim Input As String = MyMsgBoxInput("消息中继设置",
                    $"默认使用公共中继 ntfy.sh（密文传输，服务端缓存 12 小时）。{vbCrLf}如网络受限，可填入自建 ntfy 服务地址。{vbCrLf}{vbCrLf}当前：{ModFriends.RelayUrl()}",
                    DefaultInput:=ModFriends.RelayUrl(), HintText:="https://…")
                If Input Is Nothing Then Return
                Try
                    ModFriends.SetRelayUrl(Input)
                    RefreshCard()
                    Hint("中继地址已保存，下一轮拉取生效。", HintType.Green)
                Catch ex As Exception
                    Hint(ex.Message, HintType.Red)
                End Try
            Case 1
                Dim Backends As String() = {"auto", "pixeldrain", "webdav", "s3", "ntfy"}
                Dim BLabels As IMyRadio() = {
                    New MyRadioBox With {.Text = "auto（自动优先：Pixeldrain -> S3 -> WebDAV -> ntfy）"},
                    New MyRadioBox With {.Text = "pixeldrain（强制使用 Pixeldrain 120 天托管）"},
                    New MyRadioBox With {.Text = "webdav（强制使用 WebDAV / Alist 存储）"},
                    New MyRadioBox With {.Text = "s3（强制使用 S3 / Cloudflare R2 / OSS 预签名）"},
                    New MyRadioBox With {.Text = "ntfy（仅使用当前消息中继分片，约 3 小时）"}
                }
                Dim BIdx = MyMsgBoxSelect(BLabels, "选择发送大文件时的默认后端：", "保存选择", "取消")
                If BIdx Is Nothing Then Return
                ModFriends.SetFileStorageMode(Backends(BIdx.Value))
                RefreshCard()
                Hint($"已将默认大文件存储切换为：{Backends(BIdx.Value)}", HintType.Green)
            Case 2
                ConfigurePixeldrainDialog()
            Case 3
                ConfigureWebDavDialog()
            Case 4
                ConfigureS3Dialog()
            Case 5
                ShowTransferAndAuditDialog()
        End Select
    End Sub

    Private Sub ConfigurePixeldrainDialog()
        Dim ExistingKey = ModFriends.GetPixeldrainApiKey()
        Dim MaskedHint = If(String.IsNullOrEmpty(ExistingKey), "未配置", $"已配置（尾号 {ExistingKey.Substring(Math.Max(0, ExistingKey.Length - 4))}）")
        Dim KeyInput = MyMsgBoxInput("Pixeldrain 大文件托管设置",
            $"配置免费 Pixeldrain API Key 后，发送大文件将自动流式加密分片上传（64 MB/片，理论不限大小，云端保留 120 天），好友下载无需账号或 Key。{vbCrLf}免费获取地址：https://pixeldrain.com/user/api_keys{vbCrLf}{vbCrLf}当前状态：{MaskedHint}（留空确定可清除配置，本机通过 Windows DPAPI 加密存放）",
            DefaultInput:=ExistingKey, HintText:="输入 Pixeldrain API Key，留空清除")
        If KeyInput Is Nothing Then Return
        ModFriends.SetPixeldrainApiKey(KeyInput)
        RefreshCard()
        If String.IsNullOrWhiteSpace(KeyInput) Then
            Hint("已清除 Pixeldrain API Key。", HintType.Blue)
        Else
            Hint("Pixeldrain API Key 已通过 DPAPI 加密保存！", HintType.Green)
        End If
    End Sub

    Private Sub ConfigureWebDavDialog()
        Dim Existing = If(ModFriends.GetWebDavConfig(), New CloudStorageCore.WebDavStorageConfig())
        If Not String.IsNullOrEmpty(Existing.EndpointUrl) Then
            Dim Act = MyMsgBox($"当前 WebDAV 地址：{Existing.EndpointUrl}{vbCrLf}公开直链前缀：{If(String.IsNullOrWhiteSpace(Existing.PublicBaseUrl), "未设置", Existing.PublicBaseUrl)}",
                               "WebDAV / Alist 配置", "重新填写配置", "清除现有配置", "取消")
            If Act = 2 Then
                ModFriends.SetWebDavConfig(Nothing)
                RefreshCard()
                Hint("已清除 WebDAV 配置。", HintType.Blue)
                Return
            ElseIf Act <> 1 Then
                Return
            End If
        End If
        Dim Ep = MyMsgBoxInput("WebDAV 第 1/4 步：服务端写目录 URL", "输入 WebDAV 目录（例如 https://alist.example.com/dav/pcllk/）：", Existing.EndpointUrl, HintText:="https://…/dav/pcllk/")
        If String.IsNullOrWhiteSpace(Ep) Then Return
        Dim User = MyMsgBoxInput("WebDAV 第 2/4 步：用户名", "输入 WebDAV 登录用户名（无认证可留空）：", Existing.Username)
        If User Is Nothing Then Return
        Dim Pwd = MyMsgBoxInput("WebDAV 第 3/4 步：密码 / 应用专用密码", "输入 WebDAV 密码（将通过 Windows DPAPI 本机加密存放，不会发给好友）：", Existing.Password)
        If Pwd Is Nothing Then Return
        Dim Pub = MyMsgBoxInput("WebDAV 第 4/4 步：好友下载公开直链前缀（强烈建议填写）",
                                $"例如 Alist 直链前缀 https://alist.example.com/d/pcllk/{vbCrLf}填写后好友下载完全无需你的 WebDAV 账号密码！", Existing.PublicBaseUrl, HintText:="https://…/d/pcllk/")
        If Pub Is Nothing Then Return

        Dim Cfg As New CloudStorageCore.WebDavStorageConfig With {
            .EndpointUrl = Ep.Trim(),
            .Username = User.Trim(),
            .Password = Pwd,
            .PublicBaseUrl = Pub.Trim()
        }
        Dim ValidErr As String = Nothing
        If Not CloudStorageCore.ValidateWebDavConfig(Cfg, ValidErr) Then
            Hint(ValidErr, HintType.Red)
            Return
        End If
        ModFriends.SetWebDavConfig(Cfg)
        If ModFriends.GetFileStorageMode() = "auto" Then ModFriends.SetFileStorageMode("webdav")
        RefreshCard()
        Hint("WebDAV 配置已通过 DPAPI 加密保存并设为当前存储后端！", HintType.Green)
    End Sub

    Private Sub ConfigureS3Dialog()
        Dim Existing = If(ModFriends.GetS3Config(), New CloudStorageCore.S3StorageConfig())
        If Not String.IsNullOrEmpty(Existing.EndpointUrl) Then
            Dim Act = MyMsgBox($"当前 S3 Endpoint：{Existing.EndpointUrl}{vbCrLf}Bucket：{Existing.Bucket} · Region：{Existing.Region}",
                               "S3 兼容存储配置", "重新填写配置", "清除现有配置", "取消")
            If Act = 2 Then
                ModFriends.SetS3Config(Nothing)
                RefreshCard()
                Hint("已清除 S3 存储配置。", HintType.Blue)
                Return
            ElseIf Act <> 1 Then
                Return
            End If
        End If
        Dim Ep = MyMsgBoxInput("S3 第 1/6 步：Endpoint URL", "输入 Cloudflare R2 / MinIO / 阿里云 OSS Endpoint（如 https://<accountid>.r2.cloudflarestorage.com）：", Existing.EndpointUrl, HintText:="https://…")
        If String.IsNullOrWhiteSpace(Ep) Then Return
        Dim Bucket = MyMsgBoxInput("S3 第 2/6 步：Bucket 存储桶名称", "输入目标 Bucket 名称：", Existing.Bucket)
        If String.IsNullOrWhiteSpace(Bucket) Then Return
        Dim Region = MyMsgBoxInput("S3 第 3/6 步：Region 区域", "Cloudflare R2 填 auto，阿里云/AWS 填对应区域（如 cn-hangzhou 或 us-east-1）：", If(String.IsNullOrWhiteSpace(Existing.Region), "auto", Existing.Region))
        If Region Is Nothing Then Return
        Dim Ak = MyMsgBoxInput("S3 第 4/6 步：AccessKey ID", "输入具有 PutObject/GetObject 权限的 AccessKey ID（DPAPI 本机加密）：", Existing.AccessKeyId)
        If String.IsNullOrWhiteSpace(Ak) Then Return
        Dim Sk = MyMsgBoxInput("S3 第 5/6 步：SecretAccessKey", "输入 SecretAccessKey（仅用于本机签名，不会发送给好友）：", Existing.SecretAccessKey)
        If String.IsNullOrWhiteSpace(Sk) Then Return
        Dim StyleChoice = MyMsgBox("第 6/6 步：请选择 URL 访问风格与预签名有效时长（最大 7 天）：", "S3 访问风格", "Path-Style（R2 / MinIO 推荐）", "Virtual-Host（OSS / AWS）", "取消")
        If StyleChoice = 0 OrElse StyleChoice = 3 Then Return

        Dim Cfg As New CloudStorageCore.S3StorageConfig With {
            .EndpointUrl = Ep.Trim(),
            .Bucket = Bucket.Trim(),
            .Region = If(String.IsNullOrWhiteSpace(Region), "auto", Region.Trim()),
            .AccessKeyId = Ak.Trim(),
            .SecretAccessKey = Sk.Trim(),
            .KeyPrefix = "pcllk/",
            .UsePathStyle = (StyleChoice = 1),
            .PresignDays = 7
        }
        Dim ValidErr As String = Nothing
        If Not CloudStorageCore.ValidateS3Config(Cfg, ValidErr) Then
            Hint(ValidErr, HintType.Red)
            Return
        End If
        ModFriends.SetS3Config(Cfg)
        If ModFriends.GetFileStorageMode() = "auto" Then ModFriends.SetFileStorageMode("s3")
        RefreshCard()
        Hint("S3 兼容存储配置已通过 DPAPI 加密保存（上传后将自动生成 7 天预签名下载直链）！", HintType.Green)
    End Sub

    Private Sub ShowTransferAndAuditDialog()
        Dim Sb As New Text.StringBuilder()
        Dim Transfers = ModFriends.GetTransferHistorySnapshot()
        Sb.AppendLine("【最近大文件传输任务（支持同路径断点续传）】")
        If Not Transfers.Any() Then
            Sb.AppendLine("• 本次运行暂无大文件传输记录。")
        Else
            For Each T In Transfers.Take(8)
                Dim Tag = If(T.Direction = "upload", "上传", "下载")
                Dim ErrInfo = If(String.IsNullOrWhiteSpace(T.ErrorText), "", $" ({T.ErrorText})")
                Sb.AppendLine($"• [{Tag}] {T.FileName} · {T.StatusText} ({T.CurrentChunk}/{T.TotalChunks}){ErrInfo}")
            Next
        End If
        Sb.AppendLine()
        Sb.AppendLine("【事件总线近期安全与状态审计（AppEventBus）】")
        Dim Audits = AppEventBus.GetRecentAuditEvents(10)
        If Not Audits.Any() Then
            Sb.AppendLine("• 暂无审计事件。")
        Else
            For Each A In Audits
                Sb.AppendLine($"• [{A.TimestampUtc.ToLocalTime():HH:mm:ss}] {A.Topic} <- {A.SourceModule}")
            Next
        End If

        Dim Choice = MyMsgBox(Sb.ToString().TrimEnd(), "大文件传输队列与事件总线审计", "清理缓存中的半截续传文件", "复制诊断记录", "关闭")
        If Choice = 1 Then
            Dim Cleaned As Integer = 0
            Try
                Dim CacheDir = IO.Path.Combine(PathUtils.RemoveExtendedPrefix(PathTemp), "Cache")
                If IO.Directory.Exists(CacheDir) Then
                    For Each F In IO.Directory.GetFiles(CacheDir, "*.pcllk.*")
                        Try
                            IO.File.Delete(F)
                            Cleaned += 1
                        Catch
                        End Try
                    Next
                End If
            Catch
            End Try
            Hint($"已清理 {Cleaned} 个内部暂存续传文件（若存在桌面或另存为目录的 .pcllk.part 可重新下载或删除）。", HintType.Green)
        ElseIf Choice = 2 Then
            ClipboardSet(SecurityGuard.RedactUserPathAndSecrets(Sb.ToString().TrimEnd()), False)
            Hint("已复制脱敏后的传输与审计记录！", HintType.Green)
        End If
    End Sub

#End Region

End Class
