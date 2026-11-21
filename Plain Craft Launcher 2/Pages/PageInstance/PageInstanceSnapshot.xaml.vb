Imports System.IO

Public Class PageInstanceSnapshot
    Implements IRefreshable

    Private Snapshots As New List(Of ModSnapshot.SnapshotMeta)
    Private IsBusy As Boolean = False

    Private _SyncingAutoBackupOnExit As Boolean = False

    Private Sub PageInstanceSnapshot_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If String.IsNullOrWhiteSpace(TextSnapshotName.Text) Then
            TextSnapshotName.Text = $"快照 {DateTime.Now:yyyy-MM-dd HH-mm}"
        End If
        SyncAutoBackupOnExitUi()
        RefreshList()
    End Sub

    Private Sub PageInstanceSnapshot_IsVisibleChanged(sender As Object, e As DependencyPropertyChangedEventArgs) Handles Me.IsVisibleChanged
        If CBool(e.NewValue) Then SyncAutoBackupOnExitUi()
    End Sub

    Private Sub SyncAutoBackupOnExitUi()
        Dim Instance = PageInstanceLeft.Instance
        _SyncingAutoBackupOnExit = True
        Try
            ChkAutoBackupOnExit.IsEnabled = (Instance IsNot Nothing)
            ChkAutoBackupOnExit.Checked = (Instance IsNot Nothing AndAlso Settings.Get(Of Boolean)("VersionAdvanceAutoBackupOnExit", Instance:=Instance))
        Finally
            _SyncingAutoBackupOnExit = False
        End Try
    End Sub

    Private Sub ChkAutoBackupOnExit_Change() Handles ChkAutoBackupOnExit.Change
        If _SyncingAutoBackupOnExit Then Return
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Settings.Set("VersionAdvanceAutoBackupOnExit", ChkAutoBackupOnExit.Checked, Instance:=Instance)
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshList()
    End Sub

    Private Shared Function FormatSize(Bytes As Long) As String
        If Bytes < 1024 Then Return $"{Bytes} B"
        If Bytes < 1024 * 1024 Then Return $"{Bytes / 1024.0:F1} KB"
        If Bytes < 1024L * 1024L * 1024L Then Return $"{Bytes / (1024.0 * 1024.0):F1} MB"
        Return $"{Bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    End Function

    Private Function GetCheckedScopes() As List(Of String)
        Dim Scopes As New List(Of String)
        If ChkScopeMods.Checked Then Scopes.Add("mods")
        If ChkScopeConfig.Checked Then Scopes.Add("config")
        If ChkScopeResourcePacks.Checked Then Scopes.Add("resourcepacks")
        If ChkScopeShaderPacks.Checked Then Scopes.Add("shaderpacks")
        If ChkScopeOptions.Checked Then Scopes.Add("options")
        If ChkScopeSaves.Checked Then Scopes.Add("saves")
        Return Scopes
    End Function

    Private Sub RefreshList()
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Snapshots = ModSnapshot.ListSnapshots(Instance)
        RenderSnapshots()
    End Sub

    Private Sub RenderSnapshots()
        PanSnapshotList.Children.Clear()
        CardSnapshotList.Title = $"历史快照列表 ({Snapshots.Count})"
        HintNoSnapshots.Visibility = If(Snapshots.Any(), Visibility.Collapsed, Visibility.Visible)

        Dim Instance = PageInstanceLeft.Instance
        Dim SnapDir = If(Instance IsNot Nothing, ModSnapshot.GetSnapshotsDir(Instance), "")

        For Each Snap In Snapshots
            Dim LocalSnap = Snap
            Dim ScopeText = If(LocalSnap.Scopes IsNot Nothing AndAlso LocalSnap.Scopes.Any(),
                               String.Join("、", LocalSnap.Scopes.Select(AddressOf ModSnapshot.FormatScopeName)),
                               "空")
            Dim NotePart = If(String.IsNullOrWhiteSpace(LocalSnap.Note), "", $" · {LocalSnap.Note}")
            Dim InfoStr = $"{LocalSnap.CreatedAt:yyyy-MM-dd HH:mm:ss} · 范围: {ScopeText} · {LocalSnap.FileCount} 个文件 · {FormatSize(LocalSnap.SizeBytes)}{NotePart}"

            Dim Row As New MyListItem With {
                .Title = LocalSnap.Name,
                .Info = InfoStr,
                .Height = 46,
                .Type = MyListItem.CheckType.Clickable,
                .Logo = "pack://application:,,,/images/Blocks/CommandBlock.png"
            }

            Dim BtnRestore As New MyIconButton With {.ToolTip = "回滚到此快照", .Logo = Logo.IconButtonRefresh}
            AddHandler BtnRestore.Click, Sub() StartRollback(LocalSnap)

            Dim BtnExport As New MyIconButton With {.ToolTip = "导出快照压缩包", .Logo = Logo.IconButtonSave}
            AddHandler BtnExport.Click,
            Sub()
                Dim ZipPath = Path.Combine(SnapDir, LocalSnap.ZipFileName)
                If Not File.Exists(ZipPath) Then
                    Hint("快照压缩包不存在！", HintType.Red)
                    Return
                End If
                Dim OutPath = Dialogs.SaveFile("导出实例快照", $"{LocalSnap.Name}.zip", filter:={("zip", "ZIP 快照文件 (*.zip)")})
                If String.IsNullOrEmpty(OutPath) Then Return
                Try
                    File.Copy(ZipPath, OutPath, True)
                    Hint($"已导出快照到 {Path.GetFileName(OutPath)}", HintType.Green)
                Catch ex As Exception
                    Hint($"导出失败：{ex.Message}", HintType.Red)
                End Try
            End Sub

            Dim BtnDelete As New MyIconButton With {.ToolTip = "删除此快照", .Logo = Logo.IconButtonDelete}
            AddHandler BtnDelete.Click,
            Sub()
                If MyMsgBox($"确定要删除快照「{LocalSnap.Name}」吗？该操作不可撤销。", "删除快照", "删除", "取消", IsWarn:=True) <> 1 Then Return
                Try
                    ModSnapshot.DeleteSnapshotFiles(Instance, LocalSnap)
                    Hint($"已删除快照「{LocalSnap.Name}」", HintType.Green)
                    RefreshList()
                Catch ex As Exception
                    Hint($"删除快照失败：{ex.Message}", HintType.Red)
                End Try
            End Sub

            Row.Buttons = {BtnRestore, BtnExport, BtnDelete}
            AddHandler Row.Click, Sub() StartRollback(LocalSnap)
            PanSnapshotList.Children.Add(Row)
        Next
    End Sub

    Private Sub BtnOpenSnapshotsDir_Click() Handles BtnOpenSnapshotsDir.Click
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim SnapDir = ModSnapshot.GetSnapshotsDir(Instance)
        Directory.CreateDirectory(SnapDir)
        OpenExplorer(SnapDir)
    End Sub

    Private Sub BtnCreateSnapshot_Click() Handles BtnCreateSnapshot.Click
        If IsBusy Then Return
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        Dim Scopes = GetCheckedScopes()
        If Not Scopes.Any() Then
            Hint("请至少勾选一项要包含在快照中的内容范围！", HintType.Red)
            Return
        End If
        Dim Name = TextSnapshotName.Text.Trim()
        If String.IsNullOrEmpty(Name) Then Name = $"快照 {DateTime.Now:yyyy-MM-dd HH-mm}"
        Dim Note = TextSnapshotNote.Text.Trim()

        IsBusy = True
        BtnCreateSnapshot.IsEnabled = False
        LabSnapshotStatus.Text = "正在打包创建快照..."

        RunInNewThread(
        Sub()
            Try
                Dim Meta = ModSnapshot.CreateSnapshotCore(Instance, Name, Note, Scopes)
                RunInUi(
                Sub()
                    LabSnapshotStatus.Text = $"已创建快照「{Meta.Name}」（{Meta.FileCount} 个文件，{FormatSize(Meta.SizeBytes)}）"
                    TextSnapshotName.Text = $"快照 {DateTime.Now:yyyy-MM-dd HH-mm}"
                    TextSnapshotNote.Text = ""
                    Hint($"实例快照「{Meta.Name}」创建成功！", HintType.Green)
                    RefreshList()
                End Sub)
            Catch ex As Exception
                Logger.Error(ex, "创建实例快照失败")
                RunInUi(
                Sub()
                    LabSnapshotStatus.Text = $"创建失败：{ex.Message}"
                    Hint($"创建快照失败：{ex.Message}", HintType.Red)
                End Sub)
            Finally
                RunInUi(
                Sub()
                    IsBusy = False
                    BtnCreateSnapshot.IsEnabled = True
                End Sub)
            End Try
        End Sub, "Create Instance Snapshot")
    End Sub

    Private Sub StartRollback(Snap As ModSnapshot.SnapshotMeta)
        If IsBusy Then
            Hint("当前有快照任务正在执行，请稍候！", HintType.Red)
            Return
        End If
        Dim Instance = PageInstanceLeft.Instance
        If Instance Is Nothing Then Return
        If McInstanceLock.IsLocked(Instance) Then
            Hint("该实例当前被锁定，无法执行回滚！", HintType.Red)
            Return
        End If

        Dim ScopeDesc = If(Snap.Scopes IsNot Nothing AndAlso Snap.Scopes.Any(),
                           String.Join("、", Snap.Scopes.Select(AddressOf ModSnapshot.FormatScopeName)),
                           "无")
        Dim Choice = MyMsgBox(
            $"即将把实例「{Instance.Name}」回滚到快照「{Snap.Name}」（{Snap.CreatedAt:yyyy-MM-dd HH:mm}）。" & vbCrLf &
            $"受影响目录范围：{ScopeDesc}。" & vbCrLf &
            $"这些目录当前的改动将被快照内容替换，是否先创建当前状态的安全备份再回滚？",
            "确认回滚实例快照",
            "先备份当前再回滚",
            "直接覆盖回滚",
            "取消",
            IsWarn:=True)
        If Choice <> 1 AndAlso Choice <> 2 Then Return
        Dim BackupFirst As Boolean = (Choice = 1)

        Dim SnapDir = ModSnapshot.GetSnapshotsDir(Instance)
        Dim ZipPath = Path.Combine(SnapDir, Snap.ZipFileName)
        If Not File.Exists(ZipPath) Then
            Hint("快照压缩包已丢失，无法回滚！", HintType.Red)
            Return
        End If

        IsBusy = True
        BtnCreateSnapshot.IsEnabled = False
        LabSnapshotStatus.Text = "正在执行快照回滚..."

        RunInNewThread(
        Sub()
            Try
                If BackupFirst AndAlso Snap.Scopes IsNot Nothing AndAlso Snap.Scopes.Any() Then
                    RunInUi(Sub() LabSnapshotStatus.Text = "正在备份当前实例状态...")
                    ModSnapshot.CreateSnapshotCore(Instance, $"回滚前备份 ({DateTime.Now:MM-dd HH-mm})", $"回滚至「{Snap.Name}」前自动创建", Snap.Scopes)
                End If

                Dim RestoredCount = ModSnapshot.RestoreSnapshotCore(Instance, Snap, Sub(Msg) RunInUi(Sub() LabSnapshotStatus.Text = Msg))

                RunInUi(
                Sub()
                    LabSnapshotStatus.Text = $"已成功回滚至「{Snap.Name}」（恢复 {RestoredCount} 个文件）"
                    Hint($"实例已成功回滚至快照「{Snap.Name}」！", HintType.Green)
                    RefreshList()
                End Sub)
            Catch ex As Exception
                Logger.Error(ex, "实例快照回滚失败")
                RunInUi(
                Sub()
                    LabSnapshotStatus.Text = $"回滚失败：{ex.Message}"
                    Hint($"回滚失败：{ex.Message}", HintType.Red)
                End Sub)
            Finally
                RunInUi(
                Sub()
                    IsBusy = False
                    BtnCreateSnapshot.IsEnabled = True
                End Sub)
            End Try
        End Sub, "Rollback Instance Snapshot")
    End Sub

End Class
