Public Class PageLoginAuth
    Private IsFirstLoad As Boolean = True
    ''' <summary>
    ''' 刷新页面显示的所有信息。
    ''' </summary>
    Public Sub Reload(KeepInput As Boolean)
        '记住密码
        CheckRemember.Checked = Settings.Get(Of Boolean)("LoginRemember")
        Dim EmailRaw As String = Settings.Get(Of String)("LoginAuthEmail")
        Dim Emails As String() = If(String.IsNullOrEmpty(EmailRaw), Array.Empty(Of String)(), EmailRaw.Split({"¨"c}, StringSplitOptions.RemoveEmptyEntries))
        If KeepInput AndAlso Not IsFirstLoad Then '避免第一次就以 KeepInput 的方式加载，导致文本框里没东西
            '保留输入，只刷新下拉框列表
            Dim Input As String = ComboName.Text
            ComboName.ItemsSource = If(Emails.Length = 0, Nothing, Emails)
            ComboName.Text = Input
        Else
            '不保留输入，刷新列表后自动选择第一项
            If Emails.Length = 0 Then
                ComboName.ItemsSource = Nothing
            Else
                ComboName.ItemsSource = Emails
                ComboName.Text = Emails(0)
                If Settings.Get(Of Boolean)("LoginRemember") Then TextPass.Password = Settings.Get(Of String)("LoginAuthPass").BeforeFirst("¨").Trim
            End If
        End If
        ReloadRegisterButton()
        IsFirstLoad = False
    End Sub
    ''' <summary>
    ''' 获取当前页面的登录信息。
    ''' </summary>
    Public Shared Function GetLoginData() As McLoginServer
        Dim RawServer As String = If(IsNothing(McInstanceSelected), Settings.Get(Of String)("CacheAuthServerServer"), Settings.Get(Of String)("VersionServerAuthServer", Instance:=McInstanceSelected)).Trim().TrimEnd("/"c)
        Dim Server As String = If(RawServer = "", "", RawServer & "/authserver")
        If FrmLoginAuth Is Nothing Then
            Return New McLoginServer(McLoginType.Auth) With {.Token = "Auth", .BaseUrl = Server, .UserName = "", .Password = "", .Description = "Authlib-Injector", .Type = McLoginType.Auth}
        Else
            Return New McLoginServer(McLoginType.Auth) With {.Token = "Auth", .BaseUrl = Server, .UserName = FrmLoginAuth.ComboName.Text.Replace("¨", "").Trim, .Password = FrmLoginAuth.TextPass.Password.Replace("¨", "").Trim, .Description = "Authlib-Injector", .Type = McLoginType.Auth}
        End If
    End Function
    ''' <summary>
    ''' 当前页面的登录信息是否有效。
    ''' </summary>
    Public Shared Function IsVaild(LoginData As McLoginServer) As String
        Dim RawServer As String = If(LoginData.BaseUrl, "").BeforeLast("/authserver").Trim()
        If RawServer = "" OrElse Not String.IsNullOrEmpty(New ValidateHttp().Validate(RawServer)) Then Return "请先在「版本设置 → 服务器与外置登录」中填写正确的 Yggdrasil API 地址！"
        If LoginData.UserName = "" Then Return "账号不能为空！"
        Dim HasValidCache As Boolean =
            Settings.Get(Of String)("CacheAuthAccess") <> "" AndAlso
            Settings.Get(Of String)("CacheAuthUuid") <> "" AndAlso
            String.Equals(Settings.Get(Of String)("CacheAuthUsername"), LoginData.UserName, StringComparison.OrdinalIgnoreCase) AndAlso
            String.Equals(Settings.Get(Of String)("CacheAuthServerServer").Trim().TrimEnd("/"c), RawServer.TrimEnd("/"c), StringComparison.OrdinalIgnoreCase)
        If LoginData.Password = "" AndAlso Not HasValidCache Then Return "密码不能为空！"
        Return ""
    End Function
    Public Function IsVaild() As String
        Return IsVaild(GetLoginData())
    End Function

    '保存输入信息
    Private Sub ComboName_TextChanged(sender As Object, e As TextChangedEventArgs) Handles ComboName.TextChanged
        If sender.Text = "" Then TextPass.Password = ""
        If AniControlEnabled = 0 Then Settings.Set("CacheAuthAccess", "") '迫使其不进行 Validate
    End Sub
    Private Sub TextPass_PasswordChanged(sender As Object, e As RoutedEventArgs) Handles TextPass.PasswordChanged
        If AniControlEnabled = 0 Then Settings.Set("CacheAuthAccess", "")
    End Sub
    Private Sub ComboName_SelectionChanged(sender As MyComboBox, e As SelectionChangedEventArgs) Handles ComboName.SelectionChanged
        If sender.SelectedIndex = -1 OrElse Not Settings.Get(Of Boolean)("LoginRemember") Then
            TextPass.Password = ""
        Else
            Dim Passes = Settings.Get(Of String)("LoginAuthPass").ToString.Split("¨"c)
            TextPass.Password = If(sender.SelectedIndex < Passes.Length, Passes(sender.SelectedIndex).Trim, "")
        End If
    End Sub
    Private Sub CheckBoxChange(sender As MyCheckBox, e As Object) Handles CheckRemember.Change
        If AniControlEnabled = 0 Then Settings.Set(sender.Tag, sender.Checked)
    End Sub

    '链接处理
    Private Sub ComboName_TextChanged_Link() Handles ComboName.TextChanged
        BtnLink.Content = If(ComboName.Text = "", "注册账号", "找回密码")
    End Sub
    Private Sub Btn_Click(sender As Object, e As EventArgs) Handles BtnLink.Click
        Dim Website As String = If(McInstanceSelected IsNot Nothing, Settings.Get(Of String)("VersionServerAuthRegister", Instance:=McInstanceSelected), Settings.Get(Of String)("CacheAuthServerRegister"))
        If BtnLink.Content = "注册账号" Then
            OpenWebsite(Website)
        Else
            OpenWebsite(Website.Replace("/auth/register", "/auth/forgot"))
        End If
    End Sub
    '切换注册按钮可见性
    Private Sub ReloadRegisterButton() Handles Me.Loaded
        Dim Address As String = If(McInstanceSelected IsNot Nothing, Settings.Get(Of String)("VersionServerAuthRegister", Instance:=McInstanceSelected), Settings.Get(Of String)("CacheAuthServerRegister"))
        BtnLink.Visibility = If(Not String.IsNullOrWhiteSpace(Address) AndAlso String.IsNullOrEmpty(New ValidateHttp().Validate(Address)), Visibility.Visible, Visibility.Collapsed)
    End Sub

End Class
