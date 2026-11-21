Public Class PageSetupAbout

    Private Sub PageSetupAbout_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        LabVersion.Text = VersionBaseName
        LabFooterVersion.Text = $"{VersionDisplay} · 基于 PCL 2.14.0.0 开源版二次开发"
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

End Class
