'由于包含加解密等安全信息，本文件中的部分代码已被删除

Friend Module ModSecret

    '标注 PCL Linking 的不同分支，仅用于替换标记
    Public Const VersionBranchMain As String = "OpenSource"
    '在开源版的注册表与常规版的注册表隔离，以防数据冲突
    Public Const RegFolder As String = "PCL Linking"
    '微软 OAuth Client ID（环境变量兜底，可在设置中配置自定义 Client ID）
    Public OAuthClientId As String = If(Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID"), "")
    'CurseForge API Key（环境变量兜底，可在设置中配置自定义 Key）
    Public CurseForgeAPIKey As String = If(Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY"), "")

#Region "微软正版 OAuth 与官方 PCL 协同"

    ''' <summary>
    ''' 获取当前生效的微软 OAuth Client ID：优先读取用户在设置中填写的 Client ID，其次读取环境变量 PCL_MS_CLIENT_ID。
    ''' </summary>
    Friend Function GetEffectiveMsOAuthClientId() As String
        Try
            Dim CustomId As String = Settings.Get(Of String)("ToolMsOAuthClientId")
            If Not String.IsNullOrWhiteSpace(CustomId) Then Return CustomId.Trim()
        Catch
        End Try
        Return If(OAuthClientId, "").Trim()
    End Function

    ''' <summary>
    ''' 当前是否已配置可用的微软 OAuth Client ID。
    ''' </summary>
    Friend Function HasEffectiveMsOAuthClientId() As Boolean
        Return Not String.IsNullOrEmpty(GetEffectiveMsOAuthClientId())
    End Function

    ''' <summary>
    ''' 判断异常是否属于微软/Mojang 拒绝未授权或无效的 OAuth Client ID。
    ''' </summary>
    Friend Function IsMsAuthClientRejectedException(ex As Exception) As Boolean
        Dim Cur As Exception = ex
        While Cur IsNot Nothing
            Dim Msg As String = If(Cur.Message, "")
            If Msg.Contains("invalid_client") OrElse Msg.Contains("unauthorized_client") OrElse
               Msg.Contains("Invalid app registration") OrElse Msg.Contains("AADSTS700016") OrElse
               Msg.Contains("未获微软/Mojang Minecraft API 授权") OrElse Msg.Contains("Client ID 无效") Then
                Return True
            End If
            If TypeOf Cur Is HttpRequestCodeException Then
                Dim HttpEx = CType(Cur, HttpRequestCodeException)
                Dim Resp As String = If(HttpEx.Response, "")
                If Resp.Contains("invalid_client") OrElse Resp.Contains("unauthorized_client") OrElse
                   Resp.Contains("Invalid app registration") OrElse Resp.Contains("AADSTS700016") Then
                    Return True
                End If
            End If
            Cur = Cur.InnerException
        End While
        Return False
    End Function

    Private Function NormalizeExeFullPath(FilePath As String) As String
        Try
            If String.IsNullOrWhiteSpace(FilePath) Then Return ""
            Return IO.Path.GetFullPath(FilePath.Trim().Trim(""""c))
        Catch
            Return If(FilePath, "").Trim()
        End Try
    End Function

    Private Function IsValidOfficialPclCandidate(CandidatePath As String) As Boolean
        If String.IsNullOrWhiteSpace(CandidatePath) Then Return False
        Dim Normalized As String = NormalizeExeFullPath(CandidatePath)
        If String.IsNullOrEmpty(Normalized) OrElse Not IO.File.Exists(Normalized) Then Return False
        If Not Normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) Then Return False
        Dim SelfPath As String = NormalizeExeFullPath(PathExe)
        If String.Equals(Normalized, SelfPath, StringComparison.OrdinalIgnoreCase) Then Return False
        Dim FileName As String = IO.Path.GetFileName(Normalized)
        If FileName.StartsWith("PCLLK", StringComparison.OrdinalIgnoreCase) OrElse
           FileName.IndexOf("Linking", StringComparison.OrdinalIgnoreCase) >= 0 Then
            Return False
        End If
        Return True
    End Function

    ''' <summary>
    ''' 扫描常见目录下可能安装的官方 PCL 可执行文件（不读取第三方软件的私有注册表凭证）。
    ''' </summary>
    Friend Function FindOfficialPclExecutables() As List(Of String)
        Dim Results As New List(Of String)
        Dim Seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim CandidateDirs As New List(Of String) From {
            Paths.Base,
            IO.Path.GetDirectoryName(Paths.Base.TrimEnd("\"c)),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        }
        Try
            If Not String.IsNullOrWhiteSpace(McFolderSelected) Then
                CandidateDirs.Add(McFolderSelected)
                CandidateDirs.Add(IO.Path.GetDirectoryName(McFolderSelected.TrimEnd("\"c)))
            End If
        Catch
        End Try

        For Each DirPath In CandidateDirs
            If String.IsNullOrWhiteSpace(DirPath) OrElse Not IO.Directory.Exists(DirPath) Then Continue For
            Try
                For Each ExeFile In IO.Directory.GetFiles(DirPath, "*.exe", IO.SearchOption.TopDirectoryOnly)
                    Dim Name As String = IO.Path.GetFileName(ExeFile)
                    Dim MatchesPcl As Boolean =
                        Name.Equals("PCL.exe", StringComparison.OrdinalIgnoreCase) OrElse
                        Name.Equals("PCL2.exe", StringComparison.OrdinalIgnoreCase) OrElse
                        Name.StartsWith("Plain Craft Launcher", StringComparison.OrdinalIgnoreCase) OrElse
                        (Name.StartsWith("PCL", StringComparison.OrdinalIgnoreCase) AndAlso Not Name.StartsWith("PCLLK", StringComparison.OrdinalIgnoreCase))
                    If Not MatchesPcl Then Continue For
                    Dim Normalized As String = NormalizeExeFullPath(ExeFile)
                    If IsValidOfficialPclCandidate(Normalized) AndAlso Seen.Add(Normalized) Then
                        Results.Add(Normalized)
                    End If
                Next
            Catch
            End Try
        Next
        Return Results
    End Function

    ''' <summary>
    ''' 解析官方 PCL 路径：优先读取用户保存的路径，否则按需在常见目录自动查找。
    ''' </summary>
    Friend Function ResolveOfficialPclPath(Optional AllowAutoDetect As Boolean = True) As String
        Try
            Dim Configured As String = Settings.Get(Of String)("ToolOfficialPclPath")
            If IsValidOfficialPclCandidate(Configured) Then
                Return NormalizeExeFullPath(Configured)
            End If
        Catch
        End Try
        If Not AllowAutoDetect Then Return ""
        Return FindOfficialPclExecutables().FirstOrDefault()
    End Function

    ''' <summary>
    ''' 弹窗让用户手动选择本地官方 PCL 可执行文件并保存配置。
    ''' </summary>
    Friend Function SelectOfficialPclExecutable() As String
        Dim Chosen As String = Nothing
        RunInUiWait(
        Sub()
            Chosen = Dialogs.SelectFile("选择本地官方 PCL 可执行文件（如 Plain Craft Launcher 2.exe / PCL2.exe）", False, filter:="PCL 可执行文件 (*.exe)|*.exe").FirstOrDefault()
        End Sub)
        If String.IsNullOrWhiteSpace(Chosen) Then Return ""
        Dim Normalized As String = NormalizeExeFullPath(Chosen)
        If String.Equals(Normalized, NormalizeExeFullPath(PathExe), StringComparison.OrdinalIgnoreCase) OrElse
           IO.Path.GetFileName(Normalized).StartsWith("PCLLK", StringComparison.OrdinalIgnoreCase) Then
            Hint("不能选择 PCL Linking 自身，请选择原版官方 PCL2 可执行文件！", HintType.Red)
            Return ""
        End If
        If Not IO.File.Exists(Normalized) Then
            Hint("所选文件不存在！", HintType.Red)
            Return ""
        End If
        Settings.Set("ToolOfficialPclPath", Normalized)
        RunInUi(
        Sub()
            If FrmSetupLaunch IsNot Nothing AndAlso FrmSetupLaunch.TextOfficialPclPath IsNot Nothing Then
                FrmSetupLaunch.TextOfficialPclPath.Text = Normalized
            End If
        End Sub)
        Hint($"已保存官方 PCL 路径：{IO.Path.GetFileName(Normalized)}", HintType.Green)
        Return Normalized
    End Function

    ''' <summary>
    ''' 启动本地官方 PCL 进行正版验证与游戏启动（PCL Linking 保持后台运行提供联机与工具功能）。
    ''' </summary>
    Friend Function TryLaunchOfficialPcl(Optional AskConfirmIfAutoDetected As Boolean = False) As Boolean
        Dim Configured As String = ""
        Try
            Configured = Settings.Get(Of String)("ToolOfficialPclPath")
        Catch
        End Try
        Dim ExplicitValid As Boolean = IsValidOfficialPclCandidate(Configured)
        Dim TargetPath As String = If(ExplicitValid, NormalizeExeFullPath(Configured), FindOfficialPclExecutables().FirstOrDefault())

        If String.IsNullOrEmpty(TargetPath) Then
            Select Case MyMsgBox("未检测到本地官方 PCL 可执行文件。" & vbCrLf &
                                  "请选择你电脑上已下载的官方 PCL（例如 Plain Craft Launcher 2.exe / PCL2.exe），之后即可一键代为启动正版验证。",
                                  "选择官方 PCL", "浏览选择 PCL.exe", "前往 PCL 开源主页", "取消")
                Case 1
                    TargetPath = SelectOfficialPclExecutable()
                    If String.IsNullOrEmpty(TargetPath) Then Return False
                Case 2
                    OpenWebsite("https://github.com/Meloong-Git/PCL")
                    Return False
                Case Else
                    Return False
            End Select
        ElseIf Not ExplicitValid AndAlso AskConfirmIfAutoDetected Then
            Select Case MyMsgBox($"检测到本地疑似官方 PCL 程序：{vbCrLf}{TargetPath}{vbCrLf}{vbCrLf}是否将该路径保存并立即打开它完成正版验证？",
                                  "官方 PCL 协同验证", "使用此官方 PCL 启动", "重新选择 PCL.exe", "取消")
                Case 1
                    Settings.Set("ToolOfficialPclPath", TargetPath)
                    RunInUi(Sub() If FrmSetupLaunch IsNot Nothing AndAlso FrmSetupLaunch.TextOfficialPclPath IsNot Nothing Then FrmSetupLaunch.TextOfficialPclPath.Text = TargetPath)
                Case 2
                    TargetPath = SelectOfficialPclExecutable()
                    If String.IsNullOrEmpty(TargetPath) Then Return False
                Case Else
                    Return False
            End Select
        End If

        Try
            Dim Info As New Diagnostics.ProcessStartInfo With {
                .FileName = TargetPath,
                .WorkingDirectory = IO.Path.GetDirectoryName(TargetPath),
                .UseShellExecute = True
            }
            Diagnostics.Process.Start(Info)
            Hint($"已打开官方 PCL（{IO.Path.GetFileName(TargetPath)}），请在其中完成正版登录并启动游戏；PCL Linking 将继续为你提供联机、好友与百宝箱！", HintType.Green)
            Logger.Info($"已拉起官方 PCL 协同验证：{TargetPath}")
            Return True
        Catch ex As Exception
            Logger.Error(ex, $"启动官方 PCL 失败：{TargetPath}", LogBehavior.Alert)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 弹窗快速输入并保存自定义微软 OAuth Client ID。
    ''' </summary>
    Friend Function ConfigureMsClientIdInteractive() As Boolean
        Dim CurrentId As String = GetEffectiveMsOAuthClientId()
        Dim RuleList As New ObjectModel.Collection(Of Validate) From {New ValidateNullable}
        Dim InputVal As String = MyMsgBoxInput(
            "配置微软 OAuth Client ID",
            "请填写你自己的、且已通过 Mojang Minecraft API 审核的 Microsoft Entra（Azure）应用 Client ID；留空可清除已保存配置。" & vbCrLf &
            "提示：也可通过系统/启动环境变量 PCL_MS_CLIENT_ID 提供，或在「设置 → 启动 → 正版登录与官方 PCL 协同」中详细管理。",
            CurrentId,
            RuleList,
            HintText:="例如 00000000-0000-0000-0000-000000000000",
            Button1:="保存",
            Button2:="取消")
        If InputVal Is Nothing Then Return False
        Dim Trimmed As String = InputVal.Trim()
        Settings.Set("ToolMsOAuthClientId", Trimmed)
        RunInUi(
        Sub()
            If FrmSetupLaunch IsNot Nothing AndAlso FrmSetupLaunch.TextMsOAuthClientId IsNot Nothing Then
                FrmSetupLaunch.TextMsOAuthClientId.Text = Trimmed
            End If
            If FrmLoginMs IsNot Nothing Then FrmLoginMs.UpdateAuthModeUi()
        End Sub)
        If String.IsNullOrEmpty(Trimmed) Then
            Hint("已清空自定义微软 Client ID。", HintType.Blue)
        Else
            Hint("已保存自定义微软 Client ID（DPAPI 加密存储），现在可直接点击「登录」测试正版验证。", HintType.Green)
        End If
        Return True
    End Function

    ''' <summary>
    ''' 当 PCLLK 无法直接执行微软正版验证时，统一弹出选择引导（官方 PCL 协同 / 填写 Client ID / 取消）。
    ''' </summary>
    Friend Sub PromptConfigureMsAuthFallback(Optional ContextMessage As String = Nothing)
        Dim Prefix As String = If(String.IsNullOrWhiteSpace(ContextMessage),
            "当前无法直接通过 PCL Linking 完成微软正版验证（开源版本未内置受限的微软 Client ID）。",
            ContextMessage.Trim())
        Dim Choice As Integer = MyMsgBox(
            Prefix & vbCrLf & vbCrLf &
            "你可以选择以下任一方式继续：" & vbCrLf &
            "1. 官方 PCL 代验证：一键打开本地官方 PCL 完成正版验证并启动游戏（PCL Linking 可继续在后台为你提供 EasyTier 联机、好友与百宝箱）。" & vbCrLf &
            "2. 配置微软 Client ID：填入已通过 Mojang 审核的微软应用 Client ID，之后由 PCL Linking 直接完成登录。",
            "正版登录方式引导", "官方 PCL 代启动", "配置 Client ID", "取消")
        Select Case Choice
            Case 1
                TryLaunchOfficialPcl(AskConfirmIfAutoDetected:=True)
            Case 2
                ConfigureMsClientIdInteractive()
        End Select
    End Sub

#End Region

    ''' <summary>
    ''' 获取当前生效的 CurseForge API Key：优先读取用户在设置中填写的 Key，其次读取环境变量。
    ''' </summary>
    Friend Function GetEffectiveCurseForgeApiKey() As String
        Try
            Dim CustomKey As String = Settings.Get(Of String)("ToolCurseForgeApiKey")
            If Not String.IsNullOrWhiteSpace(CustomKey) Then Return CustomKey.Trim()
        Catch
        End Try
        Return If(CurseForgeAPIKey, "").Trim()
    End Function

    Private Function IsOfficialCurseForgeHost(Url As String) As Boolean
        Dim Parsed As Uri = Nothing
        If Not Uri.TryCreate(Url, UriKind.Absolute, Parsed) Then Return False
        Dim Host As String = Parsed.Host.ToLowerInvariant()
        Return Host = "api.curseforge.com" OrElse Host.EndsWithF(".curseforge.com") OrElse
               Host = "forgecdn.net" OrElse Host.EndsWithF(".forgecdn.net") OrElse
               Host.EndsWithF(".overwolf.wtf")
    End Function

#Region "网络鉴权"

    Friend Function SecretCdnSign(UrlWithMark As String) As String
        If Not UrlWithMark.EndsWithF("{CDN}") Then Return UrlWithMark
        Return UrlWithMark.Replace("{CDN}", "").Replace(" ", "%20")
    End Function
    ''' <summary>
    ''' 设置 Headers 的 UA、Referer。
    ''' </summary>
    Friend Sub SecretHeadersSign(Url As String, ByRef Req As HttpRequestMessage, Optional SimulateBrowserHeaders As Boolean = False)
        If Not Req.Headers.UserAgent.Any Then
            Dim CleanVer As String = VersionBaseName.Replace(" ", "")
            If Url.Contains("baidupcs.com") OrElse Url.Contains("baidu.com") Then
                Req.Headers.Add("User-Agent", "LogStatistic")  '#4951
            ElseIf SimulateBrowserHeaders Then
                Req.Headers.Add("User-Agent", $"PCL-Linking-Fork/{CleanVer} Mozilla/5.0 AppleWebKit/537.36 Chrome/63.0.3239.132 Safari/537.36")
            Else
                Req.Headers.Add("User-Agent", $"PCL-Linking-Fork/{CleanVer} (+https://github.com/Luo-Jacob/PCL-Linking)")
            End If
        End If
        If IsOfficialCurseForgeHost(Url) Then
            Dim ApiKey As String = GetEffectiveCurseForgeApiKey()
            If Not String.IsNullOrEmpty(ApiKey) Then Req.Headers.Add("x-api-key", ApiKey)
        End If
    End Sub

#End Region

#Region "主题"

    Public Color1 As New MyColor(52, 61, 74)
    Public Color2 As New MyColor(11, 91, 203)
    Public Color3 As New MyColor(19, 112, 243)
    Public Color4 As New MyColor(72, 144, 245)
    Public Color5 As New MyColor(150, 192, 249)
    Public Color6 As New MyColor(213, 230, 253)
    Public Color7 As New MyColor(222, 236, 253)
    Public Color8 As New MyColor(234, 242, 254)
    Public ColorBg0 As New MyColor(150, 192, 249)
    Public ColorBg1 As New MyColor(190, Color7)
    Public ColorGray1 As New MyColor(64, 64, 64)
    Public ColorGray2 As New MyColor(115, 115, 115)
    Public ColorGray3 As New MyColor(140, 140, 140)
    Public ColorGray4 As New MyColor(166, 166, 166)
    Public ColorGray5 As New MyColor(204, 204, 204)
    Public ColorGray6 As New MyColor(235, 235, 235)
    Public ColorGray7 As New MyColor(240, 240, 240)
    Public ColorGray8 As New MyColor(245, 245, 245)
    Public ColorSemiTransparent As New MyColor(1, Color8)

    Public ThemeNow As Integer = -1
    Public ColorHue As Integer = 210, ColorSat As Integer = 85, ColorLightAdjust As Integer = 0, ColorHueTopbarDelta As OneOf(Of Integer, Integer()) = 0
    Public ThemeDontClick As Integer = 0

#Region "深浅色"

    ''' <summary>
    ''' 当前是否使用深色模式。0 浅色、1 深色、2 跟随系统。
    ''' 颜色模式的概念与明度映射参考 PCL 社区版（PCL.Core/UI/Theme/ToneProfile）。
    ''' </summary>
    Public Function IsThemeDark() As Boolean
        Select Case Settings.Get(Of Integer)("UiColorMode")
            Case 1
                Return True
            Case 2
                Return SystemIsDarkMode()
            Case Else
                Return False
        End Select
    End Function

    ''' <summary>
    ''' 读取系统是否处于深色模式（注册表 AppsUseLightTheme）。
    ''' </summary>
    Public Function SystemIsDarkMode() As Boolean
        Try
            Dim Value = Microsoft.Win32.Registry.GetValue(
                "HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1)
            Return CInt(Value) = 0
        Catch ex As Exception
            Logger.Warn(ex, "读取系统深色模式失败")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 「颜色模式」设置变化时刷新主题（供 Settings.OnChanged 调用）。
    ''' </summary>
    Public Sub UiColorModeChanged()
        ThemeRefresh()
    End Sub

#End Region

    ''' <summary>
    ''' PCL Linking 主题预设表：每项为 {色相, 饱和度, 亮度微调, 顶栏色相差}。
    ''' </summary>
    Public ReadOnly ThemePresets As Integer()() = {
        New Integer() {210, 88, 0, 0},   '0 龙猫蓝
        New Integer() {172, 52, 2, 12},  '1 甜柠青
        New Integer() {126, 48, 0, 10},  '2 小草绿
        New Integer() {44, 82, 4, 12},   '3 菠萝黄
        New Integer() {26, 42, -4, 8},   '4 橡木棕
        New Integer() {210, 6, -12, 0},  '5 玄素黑
        New Integer() {338, 62, 0, 10},  '6 铁杆粉
        New Integer() {268, 52, -2, 10}, '7 神秘紫
        New Integer() {36, 62, -2, 8},   '8 秋仪金
        New Integer() {22, 88, 2, 12},   '9 活跃橙
        New Integer() {355, 74, 0, 10},  '10 跳票红
        New Integer() {196, 92, -2, 8},  '11 极客蓝
        New Integer() {210, 92, 4, 30},  '12 滑稽彩
        New Integer() {210, 90, 0, 20},  '13 欧皇彩
        New Integer() {210, 85, 0, 0}}   '14 自定义

    ''' <summary>
    ''' 合规锁定：按《PCL 存储库合理使用指南》第 5 条，Windows 端固定为 PCL 官方蓝色主色相。
    ''' </summary>
    Friend Sub ApplyThemePreset(Id As Integer)
        Dim BluePreset As Integer() = ThemePresets(0)
        ColorHue = BluePreset(0)
        ColorSat = BluePreset(1)
        ColorLightAdjust = BluePreset(2)
        ColorHueTopbarDelta = BluePreset(3)
    End Sub

    Public Sub ThemeRefresh(Optional NewTheme As Integer = -1)
        Try
            If ThemeNow = NewTheme AndAlso NewTheme >= 0 Then Return
            If NewTheme >= 0 Then ThemeNow = NewTheme
            ApplyThemePreset(ThemeNow)

            If IsThemeDark() Then
                '深色：明度映射参考 PCL 社区版 ToneProfile.DefaultDark
                'Color1 是文字与深色前景（浅色模式下最暗），深色下反转为近白
                Color1 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.15, 94)
                Color2 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.75, 76)
                Color3 = New MyColor().FromHSL2(ColorHue, ColorSat, 62)
                Color4 = New MyColor().FromHSL2(ColorHue, ColorSat, 66)
                Color5 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.55, 44)
                Color6 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.35, 27)
                Color7 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.25, 23)
                Color8 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.2, 20)
                '中性灰同步反转：Gray1 原本是深色文字，现在变为浅色文字
                ColorGray1 = New MyColor(232, 232, 232)
                ColorGray2 = New MyColor(200, 200, 200)
                ColorGray3 = New MyColor(172, 172, 172)
                ColorGray4 = New MyColor(140, 140, 140)
                ColorGray5 = New MyColor(104, 104, 104)
                ColorGray6 = New MyColor(68, 68, 68)
                ColorGray7 = New MyColor(50, 50, 52)
                ColorGray8 = New MyColor(38, 38, 40)
            Else
                Color1 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.2, 25 + ColorLightAdjust * 0.3)
                Color2 = New MyColor().FromHSL2(ColorHue, ColorSat, 45 + ColorLightAdjust)
                Color3 = New MyColor().FromHSL2(ColorHue, ColorSat, 55 + ColorLightAdjust)
                Color4 = New MyColor().FromHSL2(ColorHue, ColorSat, 65 + ColorLightAdjust)
                Color5 = New MyColor().FromHSL2(ColorHue, ColorSat, 80 + ColorLightAdjust * 0.4)
                Color6 = New MyColor().FromHSL2(ColorHue, ColorSat, 91 + ColorLightAdjust * 0.1)
                Color7 = New MyColor().FromHSL2(ColorHue, ColorSat, 95)
                Color8 = New MyColor().FromHSL2(ColorHue, ColorSat, 97)
                ColorGray1 = New MyColor(64, 64, 64)
                ColorGray2 = New MyColor(115, 115, 115)
                ColorGray3 = New MyColor(140, 140, 140)
                ColorGray4 = New MyColor(166, 166, 166)
                ColorGray5 = New MyColor(204, 204, 204)
                ColorGray6 = New MyColor(235, 235, 235)
                ColorGray7 = New MyColor(240, 240, 240)
                ColorGray8 = New MyColor(245, 245, 245)
            End If
            ColorBg0 = Color4 * 0.4 + Color5 * 0.4 + ColorGray4 * 0.2
            ColorBg1 = New MyColor(190, Color7)

            ColorSemiTransparent = New MyColor(1, Color8)
            'PCL 2.14 里大量使用 StaticResource，直接替换资源字典项不会让这些引用跟着变。
            '所以统一就地修改同一个 SolidColorBrush 实例，深浅色切换才能覆盖整个界面。
            Dim MainColors As MyColor() = {Color1, Color2, Color3, Color4, Color5, Color6, Color7, Color8}
            Dim GrayColors As MyColor() = {ColorGray1, ColorGray2, ColorGray3, ColorGray4,
                                           ColorGray5, ColorGray6, ColorGray7, ColorGray8}
            For i = 0 To 7
                SetThemeBrush($"ColorBrush{i + 1}", CType(MainColors(i), Color))
                Application.Current.Resources($"ColorObject{i + 1}") = CType(MainColors(i), Color)
                SetThemeBrush($"ColorBrushGray{i + 1}", CType(GrayColors(i), Color))
                Application.Current.Resources($"ColorObjectGray{i + 1}") = CType(GrayColors(i), Color)
            Next
            SetThemeBrush("ColorBrushBg0", CType(ColorBg0, Color))
            SetThemeBrush("ColorBrushBg1", CType(ColorBg1, Color))
            Application.Current.Resources("ColorObjectBg0") = CType(ColorBg0, Color)
            Application.Current.Resources("ColorObjectBg1") = CType(ColorBg1, Color)
            If IsThemeDark() Then
                '深色表面：中性冷灰，避免主题色大面积糊在背景上
                SetThemeBrush("ColorBrushCard", Color.FromArgb(245, 31, 33, 38))
                SetThemeBrush("ColorBrushSurface", Color.FromRgb(38, 40, 45))
                SetThemeBrush("ColorBrushHalfWhite", Color.FromRgb(45, 48, 54))
                SetThemeBrush("ColorBrushSemiWhite", Color.FromArgb(187, 31, 33, 38))
                SetThemeBrush("ColorBrushWhite", Color.FromRgb(38, 40, 45))
                SetThemeBrush("ColorBrushBackgroundTransparentSidebar", Color.FromArgb(241, 26, 28, 32))
                SetThemeBrush("ColorBrushRedBack", Color.FromArgb(90, 150, 55, 55))
                SetThemeBrush("ColorBrushSemiTransparent", Color.FromArgb(1, 0, 0, 0))
                '卡片投影与下拉阴影在深色下必须变暗，否则会浮出一圈白光
                Application.Current.Resources("ColorObject1") = Color.FromRgb(0, 0, 0)
                Application.Current.Resources("ColorObject4") = Color.FromRgb(12, 13, 16)
                Application.Current.Resources("ColorObjectGray2") = Color.FromRgb(0, 0, 0)
            Else
                SetThemeBrush("ColorBrushCard", Color.FromArgb(245, 255, 255, 255))
                SetThemeBrush("ColorBrushSurface", Color.FromRgb(255, 255, 255))
                SetThemeBrush("ColorBrushHalfWhite", Color.FromArgb(85, 255, 255, 255))
                SetThemeBrush("ColorBrushSemiWhite", Color.FromArgb(187, 255, 255, 255))
                SetThemeBrush("ColorBrushWhite", Color.FromRgb(255, 255, 255))
                SetThemeBrush("ColorBrushBackgroundTransparentSidebar", Color.FromArgb(241, 255, 255, 255))
                SetThemeBrush("ColorBrushRedBack", Color.FromArgb(128, 251, 221, 221))
                SetThemeBrush("ColorBrushSemiTransparent", Color.FromArgb(1, 234, 242, 254))
            End If
            ThemeRefreshMain()
        Catch ex As Exception
            Logger.Error(ex, "刷新主题颜色失败", LogBehavior.Toast)
        End Try
    End Sub

    ''' <summary>
    ''' 就地更新全局画刷；若画刷已被冻结则退回替换资源项。
    ''' </summary>
    Private Sub SetThemeBrush(Key As String, Value As Color)
        Dim Brush = TryCast(Application.Current.Resources(Key), SolidColorBrush)
        If Brush IsNot Nothing AndAlso Not Brush.IsFrozen Then
            Brush.Color = Value
        Else
            Application.Current.Resources(Key) = New SolidColorBrush(Value)
        End If
    End Sub
    Public Sub ThemeRefreshMain()
        RunInUi(
        Sub()
            If Not FrmMain.IsLoaded Then Return
            Dim Dark As Boolean = IsThemeDark()
            '顶部条背景
            Dim Brush = New LinearGradientBrush With {.EndPoint = New Point(1, 0), .StartPoint = New Point(0, 0)}
            Dim Deltas = ColorHueTopbarDelta.Switch(Function(d) New Integer() {-d, 0, d}, Function(d) d)
            Dim TitleLight As Integer = If(Dark, 24, 48), TitleLightMid As Integer = If(Dark, 29, 54)
            Dim TitleSat As Integer = If(Dark, CInt(ColorSat * 0.55), ColorSat)
            Brush.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor().FromHSL2(ColorHue + Deltas(0), TitleSat, TitleLight + ColorLightAdjust)})
            Brush.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor().FromHSL2(ColorHue + Deltas(1), TitleSat, TitleLightMid + ColorLightAdjust)})
            Brush.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor().FromHSL2(ColorHue + Deltas(2), TitleSat, TitleLight + ColorLightAdjust)})
            FrmMain.PanTitle.Background = Brush
            FrmMain.PanTitle.Background.Freeze()
            '主页面背景
            If Settings.Get(Of Boolean)("UiBackgroundColorful") Then
                Brush = New LinearGradientBrush With {.EndPoint = New Point(0.1, 1), .StartPoint = New Point(0.9, 0)}
                Dim BgLight As Integer = If(Dark, 14, 91)
                Dim BgSat As Integer = If(Dark, CInt(ColorSat * 0.35), CInt(ColorSat * 0.8))
                Brush.GradientStops.Add(New GradientStop With {.Offset = -0.1, .Color = New MyColor().FromHSL2(ColorHue - 15, BgSat, BgLight)})
                Brush.GradientStops.Add(New GradientStop With {.Offset = 0.4, .Color = New MyColor().FromHSL2(ColorHue, BgSat, BgLight)})
                Brush.GradientStops.Add(New GradientStop With {.Offset = 1.1, .Color = New MyColor().FromHSL2(ColorHue + 15, BgSat, BgLight)})
                FrmMain.PanForm.Background = Brush
            ElseIf Dark Then
                FrmMain.PanForm.Background = New MyColor(20, 21, 24)
            Else
                FrmMain.PanForm.Background = New MyColor(245, 245, 245)
            End If
            FrmMain.PanForm.Background.Freeze()
        End Sub)
    End Sub
    ''' <summary>
    ''' 遵循《PCL 存储库合理使用指南》第 5 条：Windows 端界面主色相固定为蓝色（支持浅色/深色/跟随系统）。
    ''' </summary>
    Friend Sub ThemeCheckAll(EffectSetup As Boolean)
        Try
            ThemeNow = 0
            ApplyThemePreset(0)
        Catch ex As Exception
            Logger.Error(ex, "初始化主题失败")
        End Try
    End Sub
    Friend Function ThemeCheckOne(Id As Integer) As Boolean
        Return Id = 0
    End Function
    Friend Function ThemeUnlock(Id As Integer, Optional ShowDoubleHint As Boolean = True, Optional UnlockHint As String = Nothing) As Boolean
        Return Id = 0
    End Function

#End Region

#Region "更新"

    Friend Sub UpdateCheckByButton()
        Hint("请前往 GitHub Releases（https://github.com/Luo-Jacob/PCL-Linking/releases）获取最新版本。")
    End Sub

    Friend IsUpdateWaitingRestart As Boolean = False
    Public Sub UpdateRestart(TriggerRestartAndByEnd As Boolean)
    End Sub

#End Region

#Region "联网配置"

    ''' <summary>
    ''' 联网获取的配置信息。
    ''' 若获取失败或仍在获取中，可能为 Nothing。
    ''' </summary>
    Public ServerConfig As JObject

    Public ServerLoader As New LoaderTask(Of Integer, Integer)("PCL Linking 配置更新", Sub() Logger.Info("该版本中不包含更新通知功能……"), Priority:=ThreadPriority.BelowNormal) With
        {.ReloadTimeout = 1000 * 60 * 60} '超时 1 小时

#End Region

#Region "赞助等级"

    Public ReadOnly Property CurrentRank As DonationRank
        Get
            Return DonationRank.None
        End Get
    End Property

    ''' <summary>
    ''' 获取设备识别码。
    ''' </summary>
    Friend Function GetIdentify() As String
        Return "0000-0000-0000-0000"
    End Function

#End Region

End Module
