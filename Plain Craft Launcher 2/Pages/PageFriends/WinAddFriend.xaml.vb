Public Class WinAddFriend

    Public Property ResultTargetId As String = ""
    Public Property ResultIsGroup As Boolean = False

    Private IsReadyUi As Boolean = False

    Private Sub WinAddFriend_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        For Each Rb In {RbModeAddFriend, RbModeJoinGroup, RbModeCreateGroup}
            Rb.LabText.Margin = New Thickness(-2, 0, 8, 0)
        Next
        IsReadyUi = True
        TryPrefillFromClipboard(SilentIfNotFound:=True)
        UpdateTabVisibility()
    End Sub

    Private Sub Mode_Changed() Handles RbModeAddFriend.Check, RbModeJoinGroup.Check, RbModeCreateGroup.Check
        If Not IsReadyUi Then Return
        UpdateTabVisibility()
    End Sub

    Private Sub UpdateTabVisibility()
        PanAddFriend.Visibility = If(RbModeAddFriend.Checked, Visibility.Visible, Visibility.Collapsed)
        PanJoinGroup.Visibility = If(RbModeJoinGroup.Checked, Visibility.Visible, Visibility.Collapsed)
        PanCreateGroup.Visibility = If(RbModeCreateGroup.Checked, Visibility.Visible, Visibility.Collapsed)
    End Sub

    Private Function TryPrefillFromClipboard(SilentIfNotFound As Boolean) As Boolean
        Dim Clip As String = ""
        Try
            If Clipboard.ContainsText() Then Clip = Clipboard.GetText()
        Catch
            Return False
        End Try
        If String.IsNullOrWhiteSpace(Clip) Then
            If Not SilentIfNotFound Then Hint("剪贴板为空！", HintType.Red)
            Return False
        End If

        Dim FId As String = "", FPwd As String = ""
        If ModFriends.TryParseCard(Clip, FId, FPwd) Then
            RbModeAddFriend.Checked = True
            TextFriendId.Text = FId
            TextFriendPwd.Text = FPwd
            HintClipboard.Text = $"已从剪贴板自动识别好友名片：ID {FId}"
            HintClipboard.Visibility = Visibility.Visible
            UpdateTabVisibility()
            Return True
        End If

        Dim GId As String = "", GSec As String = "", GName As String = ""
        If ModFriends.TryParseGroupCard(Clip, GId, GSec, GName) Then
            RbModeJoinGroup.Checked = True
            TextJoinGroupId.Text = GId
            TextJoinGroupSecret.Text = GSec
            If Not String.IsNullOrWhiteSpace(GName) Then TextJoinGroupName.Text = GName
            HintClipboard.Text = $"已从剪贴板自动识别群聊名片：{GId}{If(GName <> "", "（" & GName & "）", "")}"
            HintClipboard.Visibility = Visibility.Visible
            UpdateTabVisibility()
            Return True
        End If

        If Not SilentIfNotFound Then
            HintClipboard.Theme = MyHint.Themes.Red
            HintClipboard.Text = "剪贴板中未识别到合法的好友名片或群名片。"
            HintClipboard.Visibility = Visibility.Visible
        End If
        Return False
    End Function

    Private Sub ShowDialogError(Msg As String)
        HintClipboard.Theme = MyHint.Themes.Red
        HintClipboard.Text = Msg
        HintClipboard.Visibility = Visibility.Visible
    End Sub

    Private Sub BtnPasteCard_Click() Handles BtnPasteCard.Click
        TryPrefillFromClipboard(SilentIfNotFound:=False)
    End Sub

    Private Sub BtnPasteGroupCard_Click() Handles BtnPasteGroupCard.Click
        TryPrefillFromClipboard(SilentIfNotFound:=False)
    End Sub

    Private Sub BtnCancel_Click() Handles BtnCancel.Click
        DialogResult = False
        Close()
    End Sub

    Private Sub BtnConfirm_Click() Handles BtnConfirm.Click
        If RbModeAddFriend.Checked Then
            Dim Id = If(TextFriendId.Text, "").Trim()
            Dim Pwd = If(TextFriendPwd.Text, "").Trim()
            Dim Remark = If(TextFriendRemark.Text, "").Trim()
            Dim Err = ModFriends.AddFriend(Id, Pwd, Remark)
            If Err IsNot Nothing Then
                ShowDialogError(Err)
                Return
            End If
            ResultTargetId = Id
            ResultIsGroup = False
            RunInNewThread(Sub() ModFriends.SendHandshakePing(Id), "Friend Handshake Ping")
            ModFriends.TriggerImmediatePoll()
            Hint("已添加好友并发送双向握手校验包！", HintType.Green)
            DialogResult = True
            Close()

        ElseIf RbModeJoinGroup.Checked Then
            Dim GId = If(TextJoinGroupId.Text, "").Trim().ToUpperInvariant()
            Dim Sec = If(TextJoinGroupSecret.Text, "").Trim()
            Dim GName = If(TextJoinGroupName.Text, "").Trim()
            Dim Err = ModFriends.JoinGroup(GId, Sec, GName)
            If Err IsNot Nothing Then
                ShowDialogError(Err)
                Return
            End If
            ResultTargetId = GId
            ResultIsGroup = True
            Hint($"已加入群聊 {GId}！", HintType.Green)
            DialogResult = True
            Close()

        ElseIf RbModeCreateGroup.Checked Then
            Dim GName = If(TextCreateGroupName.Text, "").Trim()
            Dim Created = ModFriends.CreateGroup(GName)
            ClipboardSet(ModFriends.FormatGroupCard(Created), False)
            ResultTargetId = Created.Id
            ResultIsGroup = True
            Hint($"已创建群聊「{Created.Name}」({Created.Id})，并已将群名片复制到剪贴板！", HintType.Green)
            DialogResult = True
            Close()
        End If
    End Sub

End Class
