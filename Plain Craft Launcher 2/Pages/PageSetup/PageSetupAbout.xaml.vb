Public Class PageSetupAbout

    Private Sub PageSetupAbout_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        LabVersion.Text = VersionBaseName
        LabFooterVersion.Text = $"{VersionDisplay} · 基于 PCL 2.14.0.0 开源版二次开发"
    End Sub

    Private Sub BtnCopyDevQQ_Click() Handles BtnCopyDevQQ.Click
        ClipboardSet("3992309256", False)
        Hint("已复制开发者 QQ：3992309256", HintType.Green)
    End Sub

    Private Sub BtnCopyQQ_Click() Handles BtnCopyQQ.Click
        ClipboardSet("870143562", False)
        Hint("已复制社区 QQ 群号：870143562", HintType.Green)
    End Sub

    Private Sub BtnSponsorLTCat_Click() Handles BtnSponsorLTCat.Click
        OpenWebsite("https://meloong.com/afd/a/LTCat")
    End Sub

    Private Sub BtnOpenPclRepo_Click() Handles BtnOpenPclRepo.Click
        OpenWebsite("https://github.com/Meloong-Git/PCL")
    End Sub

    Private Sub BtnOpenLmlRepo_Click() Handles BtnOpenLmlRepo.Click
        OpenWebsite("https://github.com/Luo-Jacob/PCL-Linking")
    End Sub

    Private Sub BtnOpenPrivacyDoc_Click() Handles BtnOpenPrivacyDoc.Click
        OpenWebsite("https://github.com/Luo-Jacob/PCL-Linking/blob/main/PRIVACY.md")
    End Sub

    Private Sub BtnOpenThirdPartyDoc_Click() Handles BtnOpenThirdPartyDoc.Click
        OpenWebsite("https://github.com/Luo-Jacob/PCL-Linking/blob/main/THIRD-PARTY-LICENSES.md")
    End Sub

    Private Sub BtnCheckUpdate_Click() Handles BtnCheckUpdate.Click
        CheckGitHubUpdateAsync(SilentIfLatest:=False, StatusControl:=LabUpdateStatus, ButtonControl:=BtnCheckUpdate)
    End Sub

    Public Shared Sub CheckGitHubUpdateAsync(Optional SilentIfLatest As Boolean = False,
                                             Optional StatusControl As TextBlock = Nothing,
                                             Optional ButtonControl As MyButton = Nothing)
        If ButtonControl IsNot Nothing Then ButtonControl.IsEnabled = False
        If StatusControl IsNot Nothing Then StatusControl.Text = LocalizationCore.TranslateText("正在从 GitHub Releases 检查最新版本…")
        If Not SilentIfLatest Then Hint("正在检查 GitHub 最新版本…", HintType.Blue)

        RunInNewThread(
        Sub()
            Try
                Dim Headers As String(,) = {{"User-Agent", $"PCL-Linking/{VersionBaseName.Replace(" ", "")}"}}
                Dim Json = ModNet.NetRequestByClient(
                    GitHubReleaseCheckCore.LatestReleaseApiUrl,
                    Accept:="application/vnd.github+json",
                    Timeout:=12000,
                    Headers:=Headers,
                    RequireJson:=True,
                    MakeLog:=Not SilentIfLatest)
                Dim Res = GitHubReleaseCheckCore.ParseLatestReleaseJson(Json, VersionBaseName)

                RunInUi(
                Sub()
                    If ButtonControl IsNot Nothing Then ButtonControl.IsEnabled = True
                    If Not Res.HasUpdate Then
                        If StatusControl IsNot Nothing Then
                            StatusControl.Text = LocalizationCore.TranslateText($"当前 {VersionBaseName} 已是最新正式版（GitHub 最新：{Res.LatestTag}）。")
                        End If
                        If Not SilentIfLatest Then
                            Hint($"当前 {VersionBaseName} 已是最新版本！", HintType.Green)
                        End If
                        Return
                    End If

                    If StatusControl IsNot Nothing Then
                        StatusControl.Text = LocalizationCore.TranslateText($"发现新版本：{Res.LatestTag}（当前 {VersionBaseName}），点击检查可打开下载页。")
                    End If

                    Dim NotesSnippet = If(Res.ReleaseNotes, "").Trim()
                    If NotesSnippet.Length > 700 Then NotesSnippet = NotesSnippet.Substring(0, 700).TrimEnd() & vbCrLf & "……"
                    Dim AssetInfo As New System.Text.StringBuilder()
                    AssetInfo.AppendLine($"发现新版本：{Res.ReleaseName}（当前：{VersionBaseName}）")
                    If Not String.IsNullOrEmpty(Res.PublishedAt) Then AssetInfo.AppendLine($"发布时间：{Res.PublishedAt}")
                    If Not String.IsNullOrEmpty(Res.AssetName) AndAlso Res.AssetSizeBytes > 0 Then
                        Dim SizeMb = Res.AssetSizeBytes / (1024.0 * 1024.0)
                        AssetInfo.AppendLine($"发布包：{Res.AssetName}（{SizeMb:0.00} MB）")
                    End If
                    If Not String.IsNullOrEmpty(Res.AssetSha256) Then AssetInfo.AppendLine($"SHA-256：{Res.AssetSha256}")
                    If Not String.IsNullOrEmpty(NotesSnippet) Then
                        AssetInfo.AppendLine()
                        AssetInfo.AppendLine("更新说明：")
                        AssetInfo.AppendLine(NotesSnippet)
                    End If

                    If SilentIfLatest Then
                        Hint($"发现新版本 {Res.LatestTag}！可前往「设置 → 关于」查看更新日志并下载。", HintType.Green)
                    Else
                        Dim HasDlUrl = Not String.IsNullOrEmpty(Res.AssetDownloadUrl)
                        Dim Choice = If(HasDlUrl,
                            MyMsgBox(AssetInfo.ToString().TrimEnd(), "发现 GitHub 新版本", "打开 Release 页面", "复制直链地址", "稍后"),
                            MyMsgBox(AssetInfo.ToString().TrimEnd(), "发现 GitHub 新版本", "打开 Release 页面", "稍后"))
                        If Choice = 1 Then
                            OpenWebsite(Res.HtmlUrl)
                        ElseIf HasDlUrl AndAlso Choice = 2 Then
                            ClipboardSet(Res.AssetDownloadUrl, False)
                            Hint("已复制新版本下载直链！", HintType.Green)
                        End If
                    End If
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "检查 GitHub Releases 更新失败")
                RunInUi(
                Sub()
                    If ButtonControl IsNot Nothing Then ButtonControl.IsEnabled = True
                    If StatusControl IsNot Nothing Then
                        StatusControl.Text = $"检查更新失败：{ex.Message}（可手动访问 github.com/Luo-Jacob/PCL-Linking/releases）"
                    End If
                    If Not SilentIfLatest Then
                        Hint($"检查新版本失败：{ex.Message}", HintType.Red)
                    End If
                End Sub)
            End Try
        End Sub, "GitHub Release Check")
    End Sub

End Class
