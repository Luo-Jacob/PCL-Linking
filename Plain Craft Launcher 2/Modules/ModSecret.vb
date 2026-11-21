'由于包含加解密等安全信息，本文件中的部分代码已被删除

Friend Module ModSecret

    '标注 PCL Linking 的不同分支，仅用于替换标记
    Public Const VersionBranchMain As String = "OpenSource"
    '在开源版的注册表与常规版的注册表隔离，以防数据冲突
    Public Const RegFolder As String = "PCL Linking"
    'CurseForge API Key（环境变量兜底，可在设置中配置自定义 Key）
    Public CurseForgeAPIKey As String = If(Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY"), "")

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
