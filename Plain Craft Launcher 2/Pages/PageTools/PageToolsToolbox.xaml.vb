Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.Net.Http
Imports System.Threading
Imports System.Windows.Media.Imaging

Public Class PageToolsToolbox
    Implements IRefreshable

    Private DownloadCts As CancellationTokenSource = Nothing
    Private IsDownloading As Boolean = False
    Private CustomAchIconPath As String = Nothing
    Private LastAchievementBytes As Byte() = Nothing

    Private Sub PageToolsToolbox_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If String.IsNullOrWhiteSpace(TextDownloadFolder.Text) Then
            TextDownloadFolder.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        End If
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        LabQuickStatus.Text = "百宝箱状态已刷新。"
    End Sub

#Region "快捷功能"

    Private Sub BtnClearRubbish_Click() Handles BtnClearRubbish.Click
        BtnClearRubbish.IsEnabled = False
        LabQuickStatus.Text = "正在扫描临时文件、缓存与旧日志..."
        RunInNewThread(
        Sub()
            Try
                Dim Summary = ModExtra.RubbishClearInteractive()
                RunInUi(Sub() LabQuickStatus.Text = Summary)
            Finally
                RunInUi(Sub() BtnClearRubbish.IsEnabled = True)
            End Try
        End Sub, "ToolboxRubbishClear")
    End Sub

    Private Sub BtnJrrp_Click() Handles BtnJrrp.Click
        Dim Luck = ModExtra.ComputeDailyLuck()
        LabQuickStatus.Text = $"今日人品：{Luck.Score} 分（{Luck.Label}）— {Luck.Advice}"
        Hint($"今日人品：{Luck.Score} —— {Luck.Label}", If(Luck.Score >= 60, HintType.Green, HintType.Blue))
    End Sub

    Private Sub BtnCreateShortcut_Click() Handles BtnCreateShortcut.Click
        Try
            If McInstanceSelected IsNot Nothing Then
                Dim Choice = MyMsgBox($"要创建哪种桌面快捷方式？{vbCrLf}· 直接启动选中实例「{McInstanceSelected.Name}」{vbCrLf}· 仅创建 PCL Linking 启动器桌面快捷方式",
                                      "创建桌面快捷方式", $"启动 {McInstanceSelected.Name}", "仅启动器", "取消")
                If Choice = 1 Then
                    Dim Args As String = $"--launch ""{McInstanceSelected.Name}"" --folder ""{McFolderSelected.TrimEnd("\"c)}"""
                    Dim Lnk = CreateDesktopShortcut("启动 " & McInstanceSelected.Name, Args, $"使用 PCL Linking 启动 {McInstanceSelected.Name}")
                    LabQuickStatus.Text = $"已创建快捷方式：{Path.GetFileName(Lnk)}"
                    Hint($"已在桌面创建快捷方式：{Path.GetFileName(Lnk)}", HintType.Green)
                    Return
                ElseIf Choice <> 2 Then
                    Return
                End If
            End If
            Dim PlainLnk = CreateDesktopShortcut("PCL Linking", "", "Plain Craft Launcher (PCL Linking 修改版)")
            LabQuickStatus.Text = $"已创建快捷方式：{Path.GetFileName(PlainLnk)}"
            Hint($"已在桌面创建快捷方式：{Path.GetFileName(PlainLnk)}", HintType.Green)
        Catch ex As Exception
            Hint($"创建快捷方式失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnOpenDataDir_Click() Handles BtnOpenDataDir.Click
        Dim DirPath = Path.Combine(Paths.Base, "PCL Linking")
        Directory.CreateDirectory(DirPath)
        OpenExplorer(DirPath)
    End Sub

#End Region

#Region "自定义下载"

    Public Sub PrefillCustomDownload(Url As String, FileName As String, Folder As String)
        If Not String.IsNullOrWhiteSpace(Url) Then TextDownloadUrl.Text = Url
        If Not String.IsNullOrWhiteSpace(FileName) Then TextDownloadName.Text = FileName
        If Not String.IsNullOrWhiteSpace(Folder) Then TextDownloadFolder.Text = Folder
    End Sub

    Private Sub BtnDownloadPickFolder_Click() Handles BtnDownloadPickFolder.Click
        Dim DefaultDir = If(String.IsNullOrWhiteSpace(TextDownloadFolder.Text),
                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                            TextDownloadFolder.Text.Trim())
        Dim Picked = Dialogs.SelectFolder("选择文件保存目录", False, defaultDirectory:=If(Directory.Exists(DefaultDir), DefaultDir, Nothing))
        If Picked IsNot Nothing AndAlso Picked.Any() Then
            TextDownloadFolder.Text = Picked(0)
        End If
    End Sub

    Private Sub BtnDownloadOpenDir_Click() Handles BtnDownloadOpenDir.Click
        Dim TargetDir = If(String.IsNullOrWhiteSpace(TextDownloadFolder.Text),
                           Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                           TextDownloadFolder.Text.Trim())
        If Directory.Exists(TargetDir) Then
            OpenExplorer(TargetDir)
        Else
            Hint("目标目录尚不存在！", HintType.Red)
        End If
    End Sub

    Private Sub BtnDownloadStart_Click() Handles BtnDownloadStart.Click
        If IsDownloading Then
            DownloadCts?.Cancel()
            BtnDownloadStart.IsEnabled = False
            LabDownloadProgress.Text = "正在取消下载..."
            Return
        End If

        Dim RawUrl = If(TextDownloadUrl.Text, "").Trim()
        Dim ParsedUri As Uri = Nothing
        If Not Uri.TryCreate(RawUrl, UriKind.Absolute, ParsedUri) OrElse
           (ParsedUri.Scheme <> Uri.UriSchemeHttp AndAlso ParsedUri.Scheme <> Uri.UriSchemeHttps) Then
            Hint("请输入有效的 http:// 或 https:// 下载地址！", HintType.Red)
            Return
        End If

        Dim FolderPath = If(TextDownloadFolder.Text, "").Trim()
        If String.IsNullOrWhiteSpace(FolderPath) Then
            FolderPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            TextDownloadFolder.Text = FolderPath
        End If

        Dim FileName = If(TextDownloadName.Text, "").Trim()
        If String.IsNullOrWhiteSpace(FileName) Then
            Try
                FileName = Path.GetFileName(Uri.UnescapeDataString(ParsedUri.AbsolutePath))
            Catch
                FileName = ""
            End Try
            If String.IsNullOrWhiteSpace(FileName) Then FileName = "download.bin"
        End If
        FileName = Path.GetFileName(FileName)
        For Each Bad In Path.GetInvalidFileNameChars()
            FileName = FileName.Replace(Bad, "_"c)
        Next
        If String.IsNullOrWhiteSpace(FileName) Then FileName = "download.bin"
        TextDownloadName.Text = FileName

        Dim DestPath = Path.GetFullPath(Path.Combine(FolderPath, FileName))
        Dim FolderFull = Path.GetFullPath(FolderPath).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        If Not DestPath.StartsWith(FolderFull, StringComparison.OrdinalIgnoreCase) Then
            Hint("目标文件路径无效！", HintType.Red)
            Return
        End If
        If File.Exists(DestPath) Then
            If MyMsgBox($"目标文件 {FileName} 已存在，是否覆盖？", "覆盖确认", "覆盖下载", "取消") <> 1 Then Return
        End If

        IsDownloading = True
        DownloadCts = New CancellationTokenSource()
        Dim LocalToken = DownloadCts.Token
        BtnDownloadStart.Text = "取消下载"
        BtnDownloadStart.ColorType = MyButton.ColorState.Red
        LabDownloadProgress.Text = "正在连接服务器..."

        RunInNewThread(
        Sub()
            Dim TmpPath = DestPath & ".pcltmp"
            Try
                Directory.CreateDirectory(FolderPath)
                Using Client As New HttpClient() With {.Timeout = TimeSpan.FromMinutes(30)},
                      Req As New HttpRequestMessage(HttpMethod.Get, ParsedUri)
                    SecretHeadersSign(RawUrl, Req, False)
                    Using Resp = Client.SendAsync(Req, HttpCompletionOption.ResponseHeadersRead, LocalToken).GetAwaiter().GetResult()
                        Resp.EnsureSuccessStatusCode()
                        Dim TotalLen = Resp.Content.Headers.ContentLength
                        Using NetStream = Resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult(),
                              Fs As New FileStream(TmpPath, FileMode.Create, FileAccess.Write, FileShare.None)
                            Dim Buf(32767) As Byte
                            Dim Received As Long = 0
                            Dim LastUiTick As Long = 0
                            Do
                                LocalToken.ThrowIfCancellationRequested()
                                Dim n = NetStream.Read(Buf, 0, Buf.Length)
                                If n <= 0 Then Exit Do
                                Fs.Write(Buf, 0, n)
                                Received += n
                                Dim NowMs = Environment.TickCount
                                If NowMs - LastUiTick >= 150 Then
                                    LastUiTick = NowMs
                                    Dim CapturedRecv = Received
                                    Dim TextMsg = If(TotalLen.HasValue AndAlso TotalLen.Value > 0,
                                        $"已下载 {CapturedRecv / 1048576.0:0.2} / {TotalLen.Value / 1048576.0:0.2} MB ({Math.Min(100, CInt(CapturedRecv * 100.0 / TotalLen.Value))}%)",
                                        $"已下载 {CapturedRecv / 1048576.0:0.2} MB")
                                    RunInUi(Sub() LabDownloadProgress.Text = TextMsg)
                                End If
                            Loop
                        End Using
                    End Using
                End Using
                If File.Exists(DestPath) Then File.Delete(DestPath)
                File.Move(TmpPath, DestPath)
                RunInUi(
                Sub()
                    LabDownloadProgress.Text = $"下载完成：{FileName}"
                    Hint($"文件已下载至：{DestPath}", HintType.Green)
                End Sub)
            Catch ex As OperationCanceledException
                Try : If File.Exists(TmpPath) Then File.Delete(TmpPath)
                Catch : End Try
                RunInUi(
                Sub()
                    LabDownloadProgress.Text = "已取消下载"
                    Hint("已取消自定义下载", HintType.Blue)
                End Sub)
            Catch ex As Exception
                Try : If File.Exists(TmpPath) Then File.Delete(TmpPath)
                Catch : End Try
                Logger.Warn(ex, "自定义下载失败")
                RunInUi(
                Sub()
                    LabDownloadProgress.Text = $"下载失败：{ex.Message}"
                    Hint($"下载失败：{ex.Message}", HintType.Red)
                End Sub)
            Finally
                RunInUi(
                Sub()
                    IsDownloading = False
                    BtnDownloadStart.IsEnabled = True
                    BtnDownloadStart.Text = "开始下载"
                    BtnDownloadStart.ColorType = MyButton.ColorState.Highlight
                    DownloadCts?.Dispose()
                    DownloadCts = Nothing
                End Sub)
            End Try
        End Sub, "ToolboxCustomDownload")
    End Sub

#End Region

#Region "成就图标生成"

    Private Sub BtnAchPickIcon_Click() Handles BtnAchPickIcon.Click
        Dim Files = Dialogs.SelectFile("选择成就图标图片 (.png / .jpg)", False, filter:="图片文件 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg")
        If Files Is Nothing OrElse Not Files.Any() Then Return
        CustomAchIconPath = Files(0)
        ComboAchIcon.SelectedIndex = ComboAchIcon.Items.Count - 1
        RenderAchievementPreview()
    End Sub

    Private Sub ComboAchIcon_SelectionChanged() Handles ComboAchIcon.SelectionChanged
        If Not IsLoaded Then Return
        Dim Item = TryCast(ComboAchIcon.SelectedItem, MyComboBoxItem)
        If Item IsNot Nothing AndAlso Equals(Item.Tag, "__custom__") AndAlso String.IsNullOrWhiteSpace(CustomAchIconPath) Then
            Return
        End If
        RenderAchievementPreview()
    End Sub

    Private Sub BtnAchPreview_Click() Handles BtnAchPreview.Click
        RenderAchievementPreview()
    End Sub

    Private Sub BtnAchSave_Click() Handles BtnAchSave.Click
        If LastAchievementBytes Is Nothing Then RenderAchievementPreview()
        If LastAchievementBytes Is Nothing Then Return
        Dim OutPath = Dialogs.SaveFile("保存成就图片", $"achievement-{DateTime.Now:yyyyMMdd-HHmmss}.png", filter:={("png", "PNG 图片 (*.png)")})
        If String.IsNullOrEmpty(OutPath) Then Return
        Try
            File.WriteAllBytes(OutPath, LastAchievementBytes)
            Hint($"已保存成就图片：{Path.GetFileName(OutPath)}", HintType.Green)
        Catch ex As Exception
            Hint($"保存失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Function LoadSelectedIconBitmap() As Bitmap
        Dim Item = TryCast(ComboAchIcon.SelectedItem, MyComboBoxItem)
        Dim TagName = If(Item?.Tag?.ToString(), "Grass.png")
        If TagName = "__custom__" AndAlso Not String.IsNullOrWhiteSpace(CustomAchIconPath) AndAlso File.Exists(CustomAchIconPath) Then
            Return New Bitmap(CustomAchIconPath)
        End If
        Dim ResUri As New Uri($"pack://application:,,,/Images/Blocks/{If(TagName = "__custom__", "Grass.png", TagName)}", UriKind.Absolute)
        Dim Info = Windows.Application.GetResourceStream(ResUri)
        If Info IsNot Nothing Then
            Using Stream = Info.Stream
                Return New Bitmap(Stream)
            End Using
        End If
        Return Nothing
    End Function

    Private Sub RenderAchievementPreview()
        Try
            Dim Title = If(TextAchTitle.Text, "").Trim()
            Dim Line1 = If(TextAchLine1.Text, "").Trim()
            Dim Line2 = If(TextAchLine2.Text, "").Trim()
            If String.IsNullOrWhiteSpace(Title) Then Title = "获得进度！"
            If String.IsNullOrWhiteSpace(Line1) Then Line1 = "探索 PCL Linking 百宝箱"

            Const Scale As Integer = 2
            Const W As Integer = 320 * Scale
            Const H As Integer = 64 * Scale
            Using Bmp As New System.Drawing.Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format32bppArgb),
                  G As System.Drawing.Graphics = System.Drawing.Graphics.FromImage(Bmp)
                G.SmoothingMode = SmoothingMode.AntiAlias
                G.InterpolationMode = InterpolationMode.NearestNeighbor
                G.PixelOffsetMode = PixelOffsetMode.Half
                G.Clear(System.Drawing.Color.Transparent)

                '经典暗灰成就底板 + 双层像素风边框
                Using OuterBrush As New System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 22, 22, 22)),
                      InnerBrush As New System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 36, 36, 38)),
                      BorderPen As New System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 92, 92, 98), 2 * Scale),
                      PathBox = CreateRoundedPath(New System.Drawing.RectangleF(2, 2, W - 4, H - 4), 10 * Scale),
                      InnerPath = CreateRoundedPath(New System.Drawing.RectangleF(5 * Scale, 5 * Scale, W - 10 * Scale, H - 10 * Scale), 7 * Scale)
                    G.FillPath(OuterBrush, PathBox)
                    G.FillPath(InnerBrush, InnerPath)
                    G.DrawPath(BorderPen, InnerPath)
                End Using

                '左侧 32x32 图标（放大 2x = 64x64）
                Using IconBmp = LoadSelectedIconBitmap()
                    If IconBmp IsNot Nothing Then
                        G.DrawImage(IconBmp, New System.Drawing.Rectangle(16 * Scale, 16 * Scale, 32 * Scale, 32 * Scale))
                    End If
                End Using

                '右侧文字排版
                Dim HasLine2 = Not String.IsNullOrWhiteSpace(Line2)
                Using FontTitle As New System.Drawing.Font("Microsoft YaHei UI", 10.5F * Scale, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel),
                      FontBody As New System.Drawing.Font("Microsoft YaHei UI", 10.0F * Scale, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel),
                      FontSub As New System.Drawing.Font("Microsoft YaHei UI", 8.5F * Scale, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel),
                      BrushTitle As New System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 255, 232, 66)),
                      BrushBody As New System.Drawing.SolidBrush(System.Drawing.Color.White),
                      BrushSub As New System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 178, 184, 194)),
                      Fmt As New System.Drawing.StringFormat(System.Drawing.StringFormatFlags.NoWrap) With {.Trimming = System.Drawing.StringTrimming.EllipsisCharacter}
                    Dim TextX As Single = 60.0F * Scale
                    Dim TextW As Single = W - TextX - 12.0F * Scale
                    If HasLine2 Then
                        G.DrawString(Title, FontTitle, BrushTitle, New System.Drawing.RectangleF(TextX, 9.0F * Scale, TextW, 18 * Scale), Fmt)
                        G.DrawString(Line1, FontBody, BrushBody, New System.Drawing.RectangleF(TextX, 27.0F * Scale, TextW, 17 * Scale), Fmt)
                        G.DrawString(Line2, FontSub, BrushSub, New System.Drawing.RectangleF(TextX, 44.0F * Scale, TextW, 15 * Scale), Fmt)
                    Else
                        G.DrawString(Title, FontTitle, BrushTitle, New System.Drawing.RectangleF(TextX, 13.0F * Scale, TextW, 20 * Scale), Fmt)
                        G.DrawString(Line1, FontBody, BrushBody, New System.Drawing.RectangleF(TextX, 34.0F * Scale, TextW, 20 * Scale), Fmt)
                    End If
                End Using

                Using Ms As New MemoryStream()
                    Bmp.Save(Ms, System.Drawing.Imaging.ImageFormat.Png)
                    LastAchievementBytes = Ms.ToArray()
                End Using
            End Using

            Using ViewMs As New MemoryStream(LastAchievementBytes)
                Dim Bi As New BitmapImage()
                Bi.BeginInit()
                Bi.CacheOption = BitmapCacheOption.OnLoad
                Bi.StreamSource = ViewMs
                Bi.EndInit()
                Bi.Freeze()
                ImgAchPreview.Source = Bi
                BorderAchPreview.Visibility = Visibility.Visible
            End Using
        Catch ex As Exception
            Hint($"生成成就预览失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Shared Function CreateRoundedPath(Rect As RectangleF, Radius As Single) As GraphicsPath
        Dim Path As New GraphicsPath()
        Dim D As Single = Radius * 2
        Path.AddArc(Rect.X, Rect.Y, D, D, 180, 90)
        Path.AddArc(Rect.Right - D, Rect.Y, D, D, 270, 90)
        Path.AddArc(Rect.Right - D, Rect.Bottom - D, D, D, 0, 90)
        Path.AddArc(Rect.X, Rect.Bottom - D, D, D, 90, 90)
        Path.CloseFigure()
        Return Path
    End Function

#End Region

End Class
