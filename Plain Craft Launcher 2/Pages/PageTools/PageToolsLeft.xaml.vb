Public Class PageToolsLeft
    Implements IRefreshable

    Public PageID As FormMain.PageSubType = FormMain.PageSubType.ToolsToolbox

    Private Sub PageCheck(sender As FrameworkElement, e As RouteEventArgs) Handles ItemToolbox.Check, ItemHotkeys.Check, ItemServer.Check, ItemSkin3D.Check, ItemResourceMerge.Check, ItemServerPing.Check, ItemHelp.Check
        If sender.Tag IsNot Nothing Then PageChange(Val(sender.Tag))
    End Sub

    Public Function PageGet(Optional ID As FormMain.PageSubType = -1)
        If ID = -1 Then ID = PageID
        Select Case ID
            Case FormMain.PageSubType.ToolsToolbox
                If FrmToolsToolbox Is Nothing Then FrmToolsToolbox = New PageToolsToolbox
                Return FrmToolsToolbox
            Case FormMain.PageSubType.ToolsHotkeys
                If FrmToolsHotkeys Is Nothing Then FrmToolsHotkeys = New PageToolsHotkeys
                Return FrmToolsHotkeys
            Case FormMain.PageSubType.ToolsServer
                If FrmToolsServer Is Nothing Then FrmToolsServer = New PageToolsServer
                Return FrmToolsServer
            Case FormMain.PageSubType.ToolsSkin3D
                If FrmToolsSkin3D Is Nothing Then FrmToolsSkin3D = New PageToolsSkin3D
                Return FrmToolsSkin3D
            Case FormMain.PageSubType.ToolsResourceMerge
                If FrmToolsResourceMerge Is Nothing Then FrmToolsResourceMerge = New PageToolsResourceMerge
                Return FrmToolsResourceMerge
            Case FormMain.PageSubType.ToolsServerPing
                If FrmToolsServerPing Is Nothing Then FrmToolsServerPing = New PageToolsServerPing
                Return FrmToolsServerPing
            Case FormMain.PageSubType.ToolsHelp
                If FrmHelp Is Nothing Then FrmHelp = New PageHelp
                Return FrmHelp
            Case Else
                If FrmToolsToolbox Is Nothing Then FrmToolsToolbox = New PageToolsToolbox
                Return FrmToolsToolbox
        End Select
    End Function

    Public Sub PageChange(ID As FormMain.PageSubType)
        If PageID = ID Then Return
        AniControlEnabled += 1
        Try
            PageChangeRun(PageGet(ID))
            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换百宝箱分页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub

    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight")
        If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
        FrmMain.PageRight = Target
        CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnExit()
        AniStart({
            AaCode(Sub()
                       CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnForceExit()
                       FrmMain.PanMainRight.Child = FrmMain.PageRight
                       FrmMain.PageRight.Opacity = 0
                   End Sub, 130),
            AaCode(Sub()
                       FrmMain.PageRight.Opacity = 1
                       FrmMain.PageRight.PageOnEnter()
                   End Sub, 30, True)
        }, "PageLeft PageChange")
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        Refresh(FrmMain.PageCurrentSub)
    End Sub

    Public Sub Refresh(SubType As FormMain.PageSubType)
        Select Case SubType
            Case FormMain.PageSubType.ToolsToolbox
                FrmToolsToolbox?.Refresh()
                ItemToolbox.Checked = True
            Case FormMain.PageSubType.ToolsSkin3D
                FrmToolsSkin3D?.Refresh()
                ItemSkin3D.Checked = True
            Case FormMain.PageSubType.ToolsServer
                ItemServer.Checked = True
            Case FormMain.PageSubType.ToolsHotkeys
                FrmToolsHotkeys?.Refresh()
                ItemHotkeys.Checked = True
            Case FormMain.PageSubType.ToolsResourceMerge
                ItemResourceMerge.Checked = True
            Case FormMain.PageSubType.ToolsServerPing
                FrmToolsServerPing?.Refresh()
                ItemServerPing.Checked = True
            Case FormMain.PageSubType.ToolsHelp
                FrmHelp?.Refresh()
                ItemHelp.Checked = True
        End Select
    End Sub

    Public Sub RefreshHelpButton_Click(sender As Object, e As EventArgs)
        If FrmHelp Is Nothing Then FrmHelp = New PageHelp
        FrmHelp.Refresh()
        ItemHelp.Checked = True
    End Sub

    Private Sub ItemFeedback_Click(sender As Object, e As MouseButtonEventArgs) Handles ItemFeedback.Click
        Select Case MyMsgBox("反馈问题前建议附带运行日志 Log1.txt（切勿包含个人密钥与密码）。",
                             "反馈与日志", "打开 GitHub Issues", "打开日志文件夹", "取消")
            Case 1
                OpenWebsite("https://github.com/Luo-Jacob/PCL-Linking/issues")
            Case 2
                FeedbackInfo()
                OpenExplorer(Paths.Base & "PCL Linking\Log1.txt")
        End Select
    End Sub

End Class
