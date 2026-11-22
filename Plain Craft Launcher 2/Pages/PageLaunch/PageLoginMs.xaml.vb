Public Class PageLoginMs

    ''' <summary>
    ''' 刷新页面显示的所有信息。
    ''' </summary>
    Public Sub Reload(KeepInput As Boolean)
        Dim IndexBefore = ComboAccounts.SelectedIndex
        '刷新下拉框列表
        ComboAccounts.Items.Clear()
        ComboAccounts.Items.Add(New MyComboBoxItem With {.Content = "添加新账号"})
        Try
            Dim MsJson As JObject = Settings.Get(Of String)("LoginMsJson").DeserializeJson()
            For Each Account In MsJson
                Dim Item As MyListItem = CType(FindResource("ComboBoxItemTemplateWithDelete"), DataTemplate).LoadContent()
                Item.Tag = Account.Value.ToString
                Item.Title = Account.Key
                CType(Item.Buttons(0), MyIconButton).Tag = Account.Key
                ComboAccounts.Items.Add(Item)
            Next
        Catch ex As Exception
            Logger.Error(ex, $"微软登录信息出错，登录信息已被重置", LogBehavior.Toast)
            Settings.Set("LoginMsJson", "{}")
        End Try
        '如果不保留输入，刷新列表后自动选择第一项
        ComboAccounts.SelectedIndex = If(KeepInput AndAlso IndexBefore >= 0 AndAlso IndexBefore < ComboAccounts.Items.Count, IndexBefore, 0)
        UpdateAuthModeUi()
    End Sub

    Public Sub UpdateAuthModeUi()
        If BtnLogin Is Nothing OrElse BtnOfficialPcl Is Nothing OrElse BtnConfigClientId Is Nothing Then Return
        Dim HasClientId As Boolean = HasEffectiveMsOAuthClientId()
        Dim DetectedPcl As String = ResolveOfficialPclPath(AllowAutoDetect:=True)
        If HasClientId Then
            BtnLogin.ColorType = MyButton.ColorState.Highlight
            BtnOfficialPcl.ColorType = MyButton.ColorState.Normal
            BtnConfigClientId.Text = "更改 Client ID"
        Else
            BtnLogin.ColorType = MyButton.ColorState.Normal
            BtnOfficialPcl.ColorType = MyButton.ColorState.Highlight
            BtnConfigClientId.Text = "配置 Client ID"
        End If
        BtnOfficialPcl.Text = If(Not String.IsNullOrEmpty(DetectedPcl), "官方 PCL 代启动", "官方 PCL 代验证")
    End Sub

    Private Sub PageLoginMs_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        UpdateAuthModeUi()
    End Sub

    Private Sub ComboAccounts_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboAccounts.SelectionChanged
        If AniControlEnabled <> 0 OrElse ComboAccounts.SelectedItem Is Nothing OrElse ComboAccounts.ContentPresenter Is Nothing Then Return
        If TypeOf ComboAccounts.SelectedItem Is MyListItem Then
            ComboAccounts.ContentPresenter.Content = CType(ComboAccounts.SelectedItem, MyListItem).Title
        ElseIf TypeOf ComboAccounts.SelectedItem Is MyComboBoxItem Then
            ComboAccounts.ContentPresenter.Content = CType(ComboAccounts.SelectedItem, MyComboBoxItem).Content
        End If
    End Sub

    ''' <summary>
    ''' 获取当前页面的登录信息。
    ''' </summary>
    Public Shared Function GetLoginData() As McLoginMs
        If FrmLoginMs Is Nothing Then Return New McLoginMs With {.OAuthRefreshToken = Settings.Get(Of String)("CacheMsV2OAuthRefresh"), .UserName = Settings.Get(Of String)("CacheMsV2Name")}
        Dim Result As McLoginMs = Nothing
        RunInUiWait(
        Sub()
            If FrmLoginMs.ComboAccounts.SelectedIndex <= 0 Then
                Result = New McLoginMs
            Else
                Dim Item As MyListItem = CType(FrmLoginMs.ComboAccounts.SelectedItem, MyListItem)
                Result = New McLoginMs With {.OAuthRefreshToken = If(Item.Tag, "").ToString(), .UserName = Item.Title}
            End If
        End Sub)
        Return Result
    End Function
    ''' <summary>
    ''' 当前页面的登录信息是否有效。
    ''' </summary>
    Public Shared Function IsVaild(LoginData As McLoginMs) As String
        If LoginData Is Nothing OrElse LoginData.OAuthRefreshToken = "" Then
            Return "请在登录账号后再启动游戏！"
        Else
            Return ""
        End If
    End Function
    Public Function IsVaild() As String
        Return IsVaild(GetLoginData())
    End Function

    Private Sub BtnLogin_Click(sender As Object, e As EventArgs) Handles BtnLogin.Click
        If Not HasEffectiveMsOAuthClientId() Then
            PromptConfigureMsAuthFallback("当前构建未配置微软 OAuth Client ID，PCLLK 无法直接向微软发起登录请求。")
            UpdateAuthModeUi()
            Return
        End If
        ComboAccounts.IsEnabled = False
        BtnLogin.IsEnabled = False
        BtnOfficialPcl.IsEnabled = False
        BtnConfigClientId.IsEnabled = False
        BtnLogin.Text = "0%"
        RunInNewThread(
        Sub()
            Try
                McLoginMsLoader.Start(GetLoginData(), IsForceRestart:=True)
                Do While McLoginMsLoader.State = LoadState.Loading
                    RunInUi(Sub() BtnLogin.Text = Math.Round(McLoginMsLoader.Progress * 100) & "%")
                    Thread.Sleep(50)
                Loop
                If McLoginMsLoader.State = LoadState.Finished Then
                    RunInUi(Sub() FrmLaunchLeft.RefreshPage(False, True))
                ElseIf McLoginMsLoader.State = LoadState.Canceled Then
                    Throw New OperationCanceledException
                ElseIf McLoginMsLoader.Error Is Nothing Then
                    Throw New Exception("未知错误！")
                Else
                    Throw New Exception(McLoginMsLoader.Error.Message, McLoginMsLoader.Error)
                End If
            Catch ex As Exception
                If ex.IsCanceled Then
                ElseIf IsMsAuthClientRejectedException(ex) Then
                    RunInUi(
                    Sub()
                        PromptConfigureMsAuthFallback(
                            "PCLLK 直连微软验证被拒绝（所用 Client ID 未获 Minecraft API 授权或配置无效）。" & vbCrLf &
                            "详细：" & ex.Message.TrimStart("$"c))
                    End Sub)
                ElseIf ex.Message.StartsWithF("$") Then
                    Hint(ex.Message.TrimStart("$"c), HintType.Red)
                ElseIf TypeOf ex Is Security.Authentication.AuthenticationException AndAlso ex.Message.Contains("SSL/TLS") Then
                    Logger.Error(ex, $"正版登录验证失败，请考虑在 [设置 → 启动 → 下载选项] 中勾选 [关闭 SSL 证书校验]，然后再试。{vbCrLf}{vbCrLf}原始错误信息：", LogBehavior.Alert)
                Else
                    Logger.Error(ex, "正版登录尝试失败", LogBehavior.Alert)
                End If
            Finally
                RunInUi(
                Sub()
                    ComboAccounts.IsEnabled = True
                    BtnLogin.IsEnabled = True
                    BtnOfficialPcl.IsEnabled = True
                    BtnConfigClientId.IsEnabled = True
                    BtnLogin.Text = "登录"
                    UpdateAuthModeUi()
                End Sub)
            End Try
        End Sub, "Ms Login")
    End Sub

    Private Sub BtnOfficialPcl_Click(sender As Object, e As EventArgs) Handles BtnOfficialPcl.Click
        TryLaunchOfficialPcl(AskConfirmIfAutoDetected:=True)
        UpdateAuthModeUi()
    End Sub

    Private Sub BtnConfigClientId_Click(sender As Object, e As EventArgs) Handles BtnConfigClientId.Click
        ConfigureMsClientIdInteractive()
        UpdateAuthModeUi()
    End Sub

End Class
