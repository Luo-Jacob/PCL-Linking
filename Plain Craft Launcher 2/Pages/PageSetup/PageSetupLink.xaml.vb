Public Class PageSetupLink

    Private Sub PageSetupLink_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded

        '重复加载部分
        PanBack.ScrollToHome()
        LabFooterVersion.Text = $"{VersionDisplay} · 基于 PCL 2.14.0.0 开源版二次开发"

        '非重复加载部分
        Static IsLoaded As Boolean = False
        If IsLoaded Then Return
        IsLoaded = True

        AniControlEnabled += 1
        Reload()
        AniControlEnabled -= 1

    End Sub
    Public Sub Reload()
        SettingService.RefreshSettings(Me)
    End Sub

    '初始化
    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Logger.Info("已初始化联机页设置")
            Hint("已初始化联机页设置！", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化联机页设置失败", LogBehavior.Alert)
        End Try
        Reload()
    End Sub

    Private Sub BtnResetStickyRoom_Click() Handles BtnResetStickyRoom.Click
        Settings.Set("LinkStickySuffix", "")
        Settings.Set("LinkStickySecret", "")
        Settings.Set("LinkStickyVerify", "")
        Hint("已清除已保存的固定房间码，下次创建房间时将重新生成全新邀请码。", HintType.Blue)
    End Sub

    Private Sub BtnAddVoiceChatPort_Click() Handles BtnAddVoiceChatPort.Click
        Dim Ports = ModLink.ParseExtraPorts(TextExtraPorts.Text, 0)
        If Not Ports.Contains(24454) Then
            Ports.Add(24454)
            TextExtraPorts.Text = String.Join(",", Ports)
            Hint("已添加 Simple Voice Chat 默认端口 24454（房主与好友均需填写相同端口）", HintType.Green)
        Else
            Hint("额外穿透端口列表中已包含 24454。", HintType.Blue)
        End If
    End Sub

End Class
