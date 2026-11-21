Imports System.Windows.Input

Public Class PageToolsHotkeys
    Implements IRefreshable

    Private Bindings As New List(Of HotkeyBinding)
    Private IsCapturing As Boolean = False
    Private CapturedModifiers As ModifierKeys = ModifierKeys.None
    Private CapturedKey As Key = Key.None
    Private IsInitializedUi As Boolean = False

    Private Sub PageToolsHotkeys_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If Not IsInitializedUi Then
            IsInitializedUi = True
            InitActionCombo()
        End If
        RefreshList()
    End Sub

    Private Sub PageToolsHotkeys_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        StopCapture()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshList()
    End Sub

    Private Sub InitActionCombo()
        ComboAction.Items.Clear()
        For Each Act As HotkeyActionId In [Enum].GetValues(GetType(HotkeyActionId))
            ComboAction.Items.Add(New MyComboBoxItem With {
                .Content = FormatActionDisplay(Act),
                .Tag = CInt(Act)
            })
        Next
        If ComboAction.Items.Count > 0 Then ComboAction.SelectedIndex = 0
    End Sub

    Private Sub RefreshList()
        Bindings = LoadHotkeyBindings()
        ApplyHotkeys(Bindings)
        RenderBindings()
    End Sub

    Private Sub RenderBindings()
        PanBindingsList.Children.Clear()
        CardBindingsList.Title = $"已绑定快捷键 ({Bindings.Count})"
        HintNoBindings.Visibility = If(Bindings.Any(), Visibility.Collapsed, Visibility.Visible)

        For Each Item In Bindings.ToList()
            Dim LocalItem = Item
            Dim KeyText = FormatKeyDisplay(CType(LocalItem.Modifiers, ModifierKeys), CType(LocalItem.KeyCode, Key))
            Dim ScopeText = If(LocalItem.OnlyWhenFocused, "仅窗口聚焦时", "系统全局")
            Dim StateText = If(Not LocalItem.Enabled, "已禁用", If(LocalItem.RegisterOk, "生效中", "注册冲突（可能已被其他软件占用）"))

            Dim Row As New MyListItem With {
                .Title = $"{KeyText}   →   {FormatActionDisplay(LocalItem.Action)}",
                .Info = $"触发范围：{ScopeText} · 状态：{StateText}",
                .Height = 44,
                .Type = MyListItem.CheckType.Clickable,
                .Logo = If(LocalItem.Enabled AndAlso LocalItem.RegisterOk,
                           "pack://application:,,,/images/Blocks/RedstoneLampOn.png",
                           "pack://application:,,,/images/Blocks/RedstoneLampOff.png")
            }

            Dim BtnTest As New MyIconButton With {.ToolTip = "立即测试执行该功能", .Logo = Logo.IconButtonRefresh}
            AddHandler BtnTest.Click, Sub() ExecuteHotkeyAction(LocalItem.Action)

            Dim BtnToggle As New MyIconButton With {
                .ToolTip = If(LocalItem.Enabled, "停用此快捷键", "启用此快捷键"),
                .Logo = If(LocalItem.Enabled, Logo.IconButtonCross, Logo.IconButtonOpen)
            }
            AddHandler BtnToggle.Click,
            Sub()
                LocalItem.Enabled = Not LocalItem.Enabled
                SaveHotkeyBindings(Bindings)
                ApplyHotkeys(Bindings)
                RenderBindings()
            End Sub

            Dim BtnDelete As New MyIconButton With {.ToolTip = "删除绑定", .Logo = Logo.IconButtonDelete}
            AddHandler BtnDelete.Click,
            Sub()
                Bindings.RemoveAll(Function(b) b.Id = LocalItem.Id)
                SaveHotkeyBindings(Bindings)
                ApplyHotkeys(Bindings)
                RenderBindings()
                Hint($"已移除快捷键 {KeyText}", HintType.Green)
            End Sub

            Row.Buttons = {BtnTest, BtnToggle, BtnDelete}
            PanBindingsList.Children.Add(Row)
        Next
    End Sub

    Private Sub StopCapture()
        If Not IsCapturing Then Return
        IsCapturing = False
        ModHotkeys.IsCaptureSuspended = False
        If FrmMain IsNot Nothing Then
            RemoveHandler FrmMain.PreviewKeyDown, AddressOf OnGlobalPreviewKeyDown
        End If
        BtnCaptureKey.ColorType = MyButton.ColorState.Normal
        If CapturedKey = Key.None Then
            BtnCaptureKey.Text = "点击录制快捷键"
        Else
            BtnCaptureKey.Text = FormatKeyDisplay(CapturedModifiers, CapturedKey)
        End If
    End Sub

    Private Sub BtnCaptureKey_Click() Handles BtnCaptureKey.Click
        If IsCapturing Then
            StopCapture()
            LabCaptureHint.Text = "已取消录制"
            Return
        End If
        IsCapturing = True
        ModHotkeys.IsCaptureSuspended = True
        BtnCaptureKey.ColorType = MyButton.ColorState.Highlight
        BtnCaptureKey.Text = "请按下按键组合..."
        LabCaptureHint.Text = "按 Esc 取消"
        If FrmMain IsNot Nothing Then
            AddHandler FrmMain.PreviewKeyDown, AddressOf OnGlobalPreviewKeyDown
        End If
    End Sub

    Private Shared Function IsModifierOnlyKey(K As Key) As Boolean
        Return K = Key.LeftCtrl OrElse K = Key.RightCtrl OrElse
               K = Key.LeftAlt OrElse K = Key.RightAlt OrElse
               K = Key.LeftShift OrElse K = Key.RightShift OrElse
               K = Key.LWin OrElse K = Key.RWin OrElse
               K = Key.System
    End Function

    Private Sub OnGlobalPreviewKeyDown(sender As Object, e As KeyEventArgs)
        If Not IsCapturing Then Return
        Dim ActualKey As Key = If(e.Key = Key.System, e.SystemKey, If(e.Key = Key.ImeProcessed, e.ImeProcessedKey, e.Key))
        If ActualKey = Key.None OrElse IsModifierOnlyKey(ActualKey) Then Return

        e.Handled = True
        If ActualKey = Key.Escape AndAlso Keyboard.Modifiers = ModifierKeys.None Then
            StopCapture()
            LabCaptureHint.Text = "已取消录制"
            Return
        End If

        CapturedModifiers = Keyboard.Modifiers
        CapturedKey = ActualKey
        StopCapture()
        LabCaptureHint.Text = $"已录制：{FormatKeyDisplay(CapturedModifiers, CapturedKey)}"
    End Sub

    Private Sub BtnAddBinding_Click() Handles BtnAddBinding.Click
        StopCapture()
        If CapturedKey = Key.None Then
            Hint("请先点击「点击录制快捷键」录制一个按键组合！", HintType.Red)
            Return
        End If
        If Not ChkOnlyFocused.Checked AndAlso CapturedModifiers = ModifierKeys.None AndAlso
           (CapturedKey < Key.F1 OrElse CapturedKey > Key.F24) Then
            Hint("全局热键请搭配 Ctrl/Alt/Shift 等修饰键或使用 F1~F24，避免拦截系统普通打字按键！", HintType.Red)
            Return
        End If
        Dim Selected = TryCast(ComboAction.SelectedItem, MyComboBoxItem)
        If Selected Is Nothing Then Return
        Dim Action As HotkeyActionId = CType(Val(Selected.Tag), HotkeyActionId)

        Dim ExistsSame = Bindings.FirstOrDefault(Function(b) b.Modifiers = CInt(CapturedModifiers) AndAlso b.KeyCode = CInt(CapturedKey))
        If ExistsSame IsNot Nothing Then
            If MyMsgBox($"快捷键「{FormatKeyDisplay(CapturedModifiers, CapturedKey)}」已绑定功能「{FormatActionDisplay(ExistsSame.Action)}」，是否替换为新功能？", "快捷键冲突", "替换", "取消", IsWarn:=True) <> 1 Then Return
            Bindings.Remove(ExistsSame)
        End If

        Dim NewBinding As New HotkeyBinding With {
            .Modifiers = CInt(CapturedModifiers),
            .KeyCode = CInt(CapturedKey),
            .Action = Action,
            .Enabled = True,
            .OnlyWhenFocused = ChkOnlyFocused.Checked
        }
        Bindings.Add(NewBinding)
        SaveHotkeyBindings(Bindings)
        ApplyHotkeys(Bindings)
        RenderBindings()
        If NewBinding.RegisterOk Then
            Hint($"已绑定 {FormatKeyDisplay(CapturedModifiers, CapturedKey)} → {FormatActionDisplay(Action)}", HintType.Green)
        Else
            Hint("绑定已保存，但向系统注册热键失败（可能与其他软件热键冲突）！", HintType.Red)
        End If
    End Sub

    Private Sub BtnClearAllBindings_Click() Handles BtnClearAllBindings.Click
        If Not Bindings.Any() Then Return
        If MyMsgBox("确定要清空所有已添加的快捷键绑定吗？", "清空绑定", "确定清空", "取消", IsWarn:=True) <> 1 Then Return
        Bindings.Clear()
        SaveHotkeyBindings(Bindings)
        ApplyHotkeys(Bindings)
        RenderBindings()
        Hint("已清空全部快捷键绑定。", HintType.Green)
    End Sub

End Class
