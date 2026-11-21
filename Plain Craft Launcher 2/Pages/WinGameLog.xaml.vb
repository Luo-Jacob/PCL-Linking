Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Windows.Input

Public Class WinGameLog

    Public Class LogLineItem
        Public Property Text As String
        Public Property ForegroundBrush As Brush
    End Class

    Private Shared _CurrentWindow As WinGameLog = Nothing
    Private ReadOnly LineBuffer As New List(Of String)(2048)
    Private ReadOnly FilteredItems As New ObservableCollection(Of LogLineItem)
    Private ActivePid As Integer = 0
    Private Const MaxViewLines As Integer = 2000

    Private Shared ReadOnly BrushError As Brush = CreateFrozenBrush(&HF8, &H71, &H71)
    Private Shared ReadOnly BrushWarn As Brush = CreateFrozenBrush(&HFB, &HBF, &H24)
    Private Shared ReadOnly BrushMixin As Brush = CreateFrozenBrush(&H38, &HBD, &HF8)
    Private Shared ReadOnly BrushNormal As Brush = CreateFrozenBrush(&HE2, &HE8, &HF0)

    Private Shared Function CreateFrozenBrush(r As Byte, g As Byte, b As Byte) As Brush
        Dim Br As New SolidColorBrush(Color.FromRgb(r, g, b))
        Br.Freeze()
        Return Br
    End Function

    Public Shared Sub ShowOrActivate()
        RunInUi(
        Sub()
            If _CurrentWindow IsNot Nothing AndAlso _CurrentWindow.IsLoaded Then
                If _CurrentWindow.WindowState = WindowState.Minimized Then
                    _CurrentWindow.WindowState = WindowState.Normal
                End If
                _CurrentWindow.Activate()
                _CurrentWindow.RefreshWatcherList()
            Else
                _CurrentWindow = New WinGameLog()
                _CurrentWindow.Show()
            End If
        End Sub)
    End Sub

    Private Sub WinGameLog_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ListLogLines.ItemsSource = FilteredItems
        InitContextMenu()
        AddHandler ModWatcher.LiveLogAppended, AddressOf OnLiveLogAppended
        RefreshWatcherList()
    End Sub

    Private Sub InitContextMenu()
        Dim Menu As New ContextMenu()
        Dim MiCopySel As New MenuItem With {.Header = "复制选中日志行 (Ctrl+C)"}
        AddHandler MiCopySel.Click, Sub() CopySelectedLines()
        Dim MiSelectAll As New MenuItem With {.Header = "全选"}
        AddHandler MiSelectAll.Click, Sub() ListLogLines.SelectAll()
        Dim MiDiag As New MenuItem With {.Header = "一键智能诊断报错"}
        AddHandler MiDiag.Click, Sub() BtnDiagnoseCrash_Click(Nothing, Nothing)
        Menu.Items.Add(MiCopySel)
        Menu.Items.Add(MiSelectAll)
        Menu.Items.Add(New Separator())
        Menu.Items.Add(MiDiag)
        ListLogLines.ContextMenu = Menu
    End Sub

    Private Sub WinGameLog_Closed(sender As Object, e As EventArgs) Handles Me.Closed
        RemoveHandler ModWatcher.LiveLogAppended, AddressOf OnLiveLogAppended
        If Object.ReferenceEquals(_CurrentWindow, Me) Then _CurrentWindow = Nothing
    End Sub

    Private Function GetActiveTargetInstance() As McInstance
        Dim TargetWatcher = ModWatcher.GetWatchersSnapshot().FirstOrDefault(
            Function(w)
                Try
                    Return w.GameProcess IsNot Nothing AndAlso w.GameProcess.Id = ActivePid
                Catch
                    Return False
                End Try
            End Function)
        Return If(TargetWatcher?.Instance, McInstanceSelected)
    End Function

    Public Sub RefreshWatcherList()
        Dim ListCopy = ModWatcher.GetWatchersSnapshot()
        Dim PrevPid As Integer = ActivePid
        ComboProcess.Items.Clear()
        For Each W In ListCopy
            Dim Pid As Integer = 0
            Try
                If W.GameProcess IsNot Nothing Then Pid = W.GameProcess.Id
            Catch
            End Try
            Dim StateText As String = If(W.State = ModWatcher.Watcher.MinecraftState.Running, "运行中", If(W.State = ModWatcher.Watcher.MinecraftState.Loading, "启动中", "已结束"))
            Dim Item As New ComboBoxItem With {
                .Content = $"{If(W.Instance?.Name, "Minecraft")} (PID {Pid} · {StateText})",
                .Tag = Pid
            }
            ComboProcess.Items.Add(Item)
        Next
        If ComboProcess.Items.Count > 0 Then
            Dim Match = ComboProcess.Items.OfType(Of ComboBoxItem)().FirstOrDefault(Function(x) CInt(x.Tag) = PrevPid)
            ComboProcess.SelectedItem = If(Match, ComboProcess.Items(ComboProcess.Items.Count - 1))
        Else
            ActivePid = 0
            LabStatus.Text = "当前暂无运行中的 Minecraft 进程"
        End If
    End Sub

    Private Sub ComboProcess_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboProcess.SelectionChanged
        Dim Sel = TryCast(ComboProcess.SelectedItem, ComboBoxItem)
        If Sel Is Nothing Then Return
        ActivePid = CInt(Sel.Tag)
        LoadInitialLinesForActivePid()
    End Sub

    Private Sub LoadInitialLinesForActivePid()
        LineBuffer.Clear()
        Dim TargetWatcher = ModWatcher.GetWatchersSnapshot().FirstOrDefault(
            Function(w)
                Try
                    Return w.GameProcess IsNot Nothing AndAlso w.GameProcess.Id = ActivePid
                Catch
                    Return False
                End Try
            End Function)
        If TargetWatcher IsNot Nothing Then
            For Each Line In TargetWatcher.LatestLog.ToArray()
                If Line IsNot Nothing Then LineBuffer.Add(Line)
            Next
            TrimBuffer()
        End If
        RebuildFilteredView()
    End Sub

    Private Sub OnLiveLogAppended(Pid As Integer, InstanceName As String, Line As String)
        If String.IsNullOrEmpty(Line) Then Return
        RunInUi(
        Sub()
            If Not IsLoaded OrElse ChkPauseStream.IsChecked = True Then Return
            If ActivePid = 0 Then
                RefreshWatcherList()
                If ActivePid <> 0 AndAlso LineBuffer.Count > 0 AndAlso String.Equals(LineBuffer(LineBuffer.Count - 1), Line, StringComparison.Ordinal) Then Return
            End If
            If ActivePid <> 0 AndAlso Pid <> ActivePid Then Return
            LineBuffer.Add(Line)
            TrimBuffer()
            If MatchesFilter(Line) Then
                If FilteredItems.Count >= MaxViewLines Then FilteredItems.RemoveAt(0)
                FilteredItems.Add(CreateLogLineItem(Line))
                If ChkAutoScroll.IsChecked = True AndAlso FilteredItems.Count > 0 Then
                    ListLogLines.ScrollIntoView(FilteredItems(FilteredItems.Count - 1))
                End If
                UpdateStatusText(FilteredItems.Count)
            End If
        End Sub)
    End Sub

    Private Shared Function CreateLogLineItem(Line As String) As LogLineItem
        Dim Brush As Brush = BrushNormal
        If Line.Contains("/ERROR") OrElse Line.Contains("/FATAL") OrElse Line.Contains("Exception") OrElse Line.Contains("Caused by:") OrElse Line.Contains("Error:") Then
            Brush = BrushError
        ElseIf Line.Contains("/WARN") Then
            Brush = BrushWarn
        ElseIf Line.IndexOf("mixin", StringComparison.OrdinalIgnoreCase) >= 0 OrElse Line.Contains("ModLoading") OrElse Line.Contains("ModResolution") Then
            Brush = BrushMixin
        End If
        Return New LogLineItem With {.Text = Line, .ForegroundBrush = Brush}
    End Function

    Private Sub TrimBuffer()
        If LineBuffer.Count > MaxViewLines Then
            LineBuffer.RemoveRange(0, LineBuffer.Count - MaxViewLines)
        End If
    End Sub

    Private Function MatchesFilter(Line As String) As Boolean
        If String.IsNullOrEmpty(Line) Then Return False
        Dim ModeItem = TryCast(ComboLevelFilter.SelectedItem, ComboBoxItem)
        Dim Mode = If(ModeItem?.Tag?.ToString(), "ALL")
        If Mode = "WARN" Then
            If Not (Line.Contains("/WARN") OrElse Line.Contains("/ERROR") OrElse Line.Contains("/FATAL") OrElse Line.Contains("Exception") OrElse Line.Contains("Caused by:")) Then Return False
        ElseIf Mode = "ERROR" Then
            If Not (Line.Contains("/ERROR") OrElse Line.Contains("/FATAL") OrElse Line.Contains("Exception") OrElse Line.Contains("Error:") OrElse Line.Contains("Caused by:")) Then Return False
        ElseIf Mode = "MIXIN" Then
            If Not (Line.IndexOf("mixin", StringComparison.OrdinalIgnoreCase) >= 0 OrElse Line.Contains("ModLoading") OrElse Line.Contains("ModResolution") OrElse Line.Contains("/ERROR") OrElse Line.Contains("Exception")) Then Return False
        End If
        Dim Kw = If(TextSearch.Text, "").Trim()
        If Kw.Length > 0 Then
            If ChkRegexSearch.IsChecked = True Then
                Try
                    If Not Regex.IsMatch(Line, Kw, RegexOptions.IgnoreCase) Then Return False
                Catch
                    If Line.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) < 0 Then Return False
                End Try
            Else
                If Line.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) < 0 Then Return False
            End If
        End If
        Return True
    End Function

    Private Sub RebuildFilteredView()
        FilteredItems.Clear()
        For Each Line In LineBuffer
            If MatchesFilter(Line) Then
                FilteredItems.Add(CreateLogLineItem(Line))
            End If
        Next
        If ChkAutoScroll.IsChecked = True AndAlso FilteredItems.Count > 0 Then
            ListLogLines.ScrollIntoView(FilteredItems(FilteredItems.Count - 1))
        End If
        UpdateStatusText(FilteredItems.Count)
    End Sub

    Private Sub UpdateStatusText(Optional MatchedCount As Integer = -1)
        If MatchedCount >= 0 Then
            LabStatus.Text = $"PID {ActivePid} · 缓存 {LineBuffer.Count} 行 · 匹配显示 {MatchedCount} 行"
        Else
            LabStatus.Text = $"PID {ActivePid} · 缓存 {LineBuffer.Count} 行"
        End If
    End Sub

    Private Sub ComboLevelFilter_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboLevelFilter.SelectionChanged
        If Not IsLoaded Then Return
        RebuildFilteredView()
    End Sub

    Private Sub TextSearch_TextChanged(sender As Object, e As TextChangedEventArgs) Handles TextSearch.TextChanged
        If Not IsLoaded Then Return
        RebuildFilteredView()
    End Sub

    Private Sub ChkRegexSearch_Changed(sender As Object, e As RoutedEventArgs) Handles ChkRegexSearch.Checked, ChkRegexSearch.Unchecked
        If Not IsLoaded Then Return
        RebuildFilteredView()
    End Sub

    Private Sub ListLogLines_PreviewKeyDown(sender As Object, e As KeyEventArgs) Handles ListLogLines.PreviewKeyDown
        If (Keyboard.Modifiers And ModifierKeys.Control) <> 0 AndAlso e.Key = Key.C Then
            e.Handled = True
            CopySelectedLines()
        End If
    End Sub

    Private Sub CopySelectedLines()
        Dim Sel = ListLogLines.SelectedItems.OfType(Of LogLineItem)().Select(Function(x) x.Text).ToList()
        If Not Sel.Any() Then
            Hint("请先点击选中需要复制的日志行！", HintType.Blue)
            Return
        End If
        ClipboardSet(String.Join(Environment.NewLine, Sel), False)
        Hint($"已复制 {Sel.Count} 行日志到剪贴板！", HintType.Green)
    End Sub

    Private Sub BtnRefreshProcs_Click(sender As Object, e As RoutedEventArgs) Handles BtnRefreshProcs.Click
        RefreshWatcherList()
    End Sub

    Private Function GetFilteredText() As String
        Return String.Join(Environment.NewLine, FilteredItems.Select(Function(x) x.Text))
    End Function

    Private Sub BtnCopyLog_Click(sender As Object, e As RoutedEventArgs) Handles BtnCopyLog.Click
        If Not FilteredItems.Any() Then
            Hint("当前日志视图为空！", HintType.Blue)
            Return
        End If
        ClipboardSet(GetFilteredText(), False)
        Hint($"已复制全部 {FilteredItems.Count} 行过滤日志到剪贴板！", HintType.Green)
    End Sub

    Private Sub BtnExportLog_Click(sender As Object, e As RoutedEventArgs) Handles BtnExportLog.Click
        Dim OutPath = Dialogs.SaveFile("导出游戏实时日志", $"minecraft-live-{DateTime.Now:yyyyMMdd-HHmmss}.log", filter:={("log", "日志文件 (*.log)")})
        If String.IsNullOrEmpty(OutPath) Then Return
        Try
            File.WriteAllText(OutPath, GetFilteredText(), New UTF8Encoding(False))
            Hint("已导出日志到 " & Path.GetFileName(OutPath), HintType.Green)
        Catch ex As Exception
            Hint("导出日志失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub BtnClearLog_Click(sender As Object, e As RoutedEventArgs) Handles BtnClearLog.Click
        LineBuffer.Clear()
        FilteredItems.Clear()
        UpdateStatusText(0)
    End Sub

    Private Sub BtnDiagnoseCrash_Click(sender As Object, e As RoutedEventArgs) Handles BtnDiagnoseCrash.Click
        Dim LinesSnap = LineBuffer.ToList()
        Dim InstSnap = GetActiveTargetInstance()
        If Not LinesSnap.Any() AndAlso InstSnap IsNot Nothing Then
            Dim LatestFile = Path.Combine(InstSnap.PathIndie.TrimEnd("\"c), "logs", "latest.log")
            If File.Exists(LatestFile) Then
                Try
                    LinesSnap = File.ReadAllLines(LatestFile, Encoding.UTF8).ToList()
                Catch
                End Try
            End If
        End If
        If Not LinesSnap.Any() Then
            Hint("当前无可用日志内容，请先运行游戏或等待日志输出。", HintType.Blue)
            Return
        End If
        BtnDiagnoseCrash.IsEnabled = False
        RunInNewThread(
        Sub()
            Try
                Dim Analyzer As New CrashAnalyzer(InstSnap)
                Dim Diag = Analyzer.AnalyzeLiveLines(LinesSnap)
                RunInUi(
                Sub()
                    BtnDiagnoseCrash.IsEnabled = True
                    Dim Choice = MyMsgBox(Diag, "Minecraft 实时日志智能诊断", "复制诊断结论", "关闭")
                    If Choice = 1 Then
                        ClipboardSet(Diag, False)
                        Hint("已复制诊断结论到剪贴板！", HintType.Green)
                    End If
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    BtnDiagnoseCrash.IsEnabled = True
                    Hint($"智能诊断失败：{ex.Message}", HintType.Red)
                End Sub)
            End Try
        End Sub, "Live Log Crash Diagnose")
    End Sub

    Private Sub BtnOpenLatestLog_Click(sender As Object, e As RoutedEventArgs) Handles BtnOpenLatestLog.Click
        Dim TargetInst = GetActiveTargetInstance()
        If TargetInst Is Nothing Then Return
        Dim LogsDir = TargetInst.PathIndie & "logs\"
        Directory.CreateDirectory(LogsDir)
        OpenExplorer(LogsDir)
    End Sub

End Class
