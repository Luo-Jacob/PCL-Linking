Imports System.Collections
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Controls.Primitives
Imports System.Windows.Documents
Imports System.Windows.Threading

Public Module LocalizationService

    Private Class TrackedPropState
        Public Property OriginalCn As String = ""
        Public Property LastTranslated As String = ""
    End Class

    Private ReadOnly TrackedMap As New ConditionalWeakTable(Of DependencyObject, Dictionary(Of String, TrackedPropState))()
    Private HooksInitialized As Integer = 0
    Public EverTranslated As Boolean = False

    Public Sub InitLocalizationHooks()
        If Interlocked.Exchange(HooksInitialized, 1) = 1 Then Return
        Try
            LocalizationCore.LoadEmbeddedTranslations(GetType(LocalizationService).Assembly, "en-US.json")
        Catch ex As Exception
            Logger.Warn(ex, "加载内嵌英文语言包失败")
        End Try

        Try
            Dim SavedLang = Settings.Get(Of Integer)("UiLanguage")
            LocalizationCore.CurrentLanguage = If(SavedLang = 1, LocalizationCore.LauncherLanguage.English, LocalizationCore.LauncherLanguage.ChineseSimplified)
        Catch
            LocalizationCore.CurrentLanguage = LocalizationCore.LauncherLanguage.ChineseSimplified
        End Try

        Try
            EventManager.RegisterClassHandler(GetType(MyPageRight), FrameworkElement.LoadedEvent, New RoutedEventHandler(AddressOf OnContainerElementLoaded))
            EventManager.RegisterClassHandler(GetType(MyPageLeft), FrameworkElement.LoadedEvent, New RoutedEventHandler(AddressOf OnContainerElementLoaded))
            EventManager.RegisterClassHandler(GetType(Window), FrameworkElement.LoadedEvent, New RoutedEventHandler(AddressOf OnContainerElementLoaded))
            EventManager.RegisterClassHandler(GetType(MyCard), FrameworkElement.LoadedEvent, New RoutedEventHandler(AddressOf OnLeafControlLoaded))
            EventManager.RegisterClassHandler(GetType(MyListItem), FrameworkElement.LoadedEvent, New RoutedEventHandler(AddressOf OnLeafControlLoaded))
            EventManager.RegisterClassHandler(GetType(MyResourceItem), FrameworkElement.LoadedEvent, New RoutedEventHandler(AddressOf OnLeafControlLoaded))
        Catch ex As Exception
            Logger.Warn(ex, "注册界面多语言事件钩子失败")
        End Try
    End Sub

    Private Sub OnContainerElementLoaded(sender As Object, e As RoutedEventArgs)
        If sender IsNot e.Source Then Return
        Dim Dep = TryCast(sender, DependencyObject)
        If Dep Is Nothing Then Return
        If Not LocalizationCore.IsEnglishActive() AndAlso Not HasTrackedChildren(Dep) Then Return

        TranslateElement(Dep)
        Dim Fe = TryCast(Dep, FrameworkElement)
        If Fe IsNot Nothing AndAlso Fe.Dispatcher IsNot Nothing Then
            Fe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, New Action(Sub() TranslateElement(Dep)))
        End If
    End Sub

    Private Sub OnLeafControlLoaded(sender As Object, e As RoutedEventArgs)
        If sender IsNot e.Source Then Return
        If Not LocalizationCore.IsEnglishActive() Then Return
        Dim Dep = TryCast(sender, DependencyObject)
        If Dep IsNot Nothing Then TranslateElement(Dep)
    End Sub

    Public Function HasTrackedChildren(Root As DependencyObject) As Boolean
        Dim Dummy As Dictionary(Of String, TrackedPropState) = Nothing
        Return TrackedMap.TryGetValue(Root, Dummy)
    End Function

    Public Sub ApplyLauncherLanguage()
        Try
            Dim SavedLang = Settings.Get(Of Integer)("UiLanguage")
            LocalizationCore.CurrentLanguage = If(SavedLang = 1, LocalizationCore.LauncherLanguage.English, LocalizationCore.LauncherLanguage.ChineseSimplified)
        Catch
            LocalizationCore.CurrentLanguage = LocalizationCore.LauncherLanguage.ChineseSimplified
        End Try

        RunInUi(
        Sub()
            Try
                Dim CultureName = If(LocalizationCore.IsEnglishActive(), "en-US", "zh-CN")
                Dim Culture = New CultureInfo(CultureName)
                Thread.CurrentThread.CurrentUICulture = Culture
                CultureInfo.DefaultThreadCurrentUICulture = Culture
            Catch
            End Try

            Try
                FrmMain?.PageNameRefresh()
            Catch
            End Try

            Dim Pages As DependencyObject() = {
                FrmMain,
                FrmLaunchLeft, FrmLaunchRight, FrmSelectLeft, FrmSelectRight, FrmSpeedLeft, FrmSpeedRight,
                FrmLinkMain, FrmFriends, FrmHelp,
                FrmDownloadLeft, FrmDownloadInstall, FrmDownloadMod, FrmDownloadPack, FrmDownloadDataPack,
                FrmDownloadShader, FrmDownloadResourcePack, FrmDownloadPlugin, FrmDownloadMap, FrmDownloadPresets,
                FrmDownloadModify, FrmDownloadResourceDetail,
                FrmSetupLeft, FrmSetupLaunch, FrmSetupLink, FrmSetupUI, FrmSetupAbout,
                FrmLoginMs, FrmLoginMsSkin, FrmLoginLegacy, FrmLoginAuth, FrmLoginAuthSkin,
                FrmInstanceLeft, FrmInstanceOverall, FrmInstanceMod, FrmInstanceModDisabled, FrmInstanceSetup,
                FrmInstanceExport, FrmInstanceSaves, FrmInstanceSnapshot, FrmInstanceAssets,
                FrmToolsLeft, FrmToolsToolbox, FrmToolsSkin3D, FrmToolsServer, FrmToolsHotkeys, FrmToolsResourceMerge, FrmToolsServerPing
            }
            For Each P In Pages
                If P IsNot Nothing Then
                    Try
                        TranslateElement(P)
                    Catch ex As Exception
                        Logger.Warn(ex, "切换语言翻译页面失败")
                    End Try
                End If
            Next

            If Application.Current IsNot Nothing Then
                For Each Win As Window In Application.Current.Windows.Cast(Of Window)().ToArray()
                    If Win IsNot Nothing AndAlso Win IsNot FrmMain Then
                        Try
                            TranslateElement(Win)
                        Catch
                        End Try
                    End If
                Next
            End If

            '同步动态构建文本的页面
            Try
                If FrmFriends IsNot Nothing AndAlso FrmFriends.IsLoaded Then FrmFriends.RefreshLanguageUi()
            Catch
            End Try
            Try
                If FrmLinkMain IsNot Nothing AndAlso FrmLinkMain.IsLoaded Then FrmLinkMain.RefreshLanguageUi()
            Catch
            End Try
            Try
                If FrmDownloadInstall IsNot Nothing AndAlso FrmDownloadInstall.IsLoaded Then FrmDownloadInstall.ReloadSelected()
            Catch
            End Try

            '刷新设置搜索框结果
            Try
                If FrmSetupLeft IsNot Nothing AndAlso FrmSetupLeft.SearchSetupBox IsNot Nothing AndAlso
                   Not String.IsNullOrWhiteSpace(FrmSetupLeft.SearchSetupBox.Text) Then
                    Dim Prev = FrmSetupLeft.SearchSetupBox.Text
                    FrmSetupLeft.SearchSetupBox.Text = ""
                    FrmSetupLeft.SearchSetupBox.Text = Prev
                End If
            Catch
            End Try
        End Sub)
    End Sub

    Public Sub TranslateElement(Root As DependencyObject)
        If Root Is Nothing Then Return
        If LocalizationCore.IsEnglishActive() Then EverTranslated = True
        Dim Visited As New HashSet(Of Object)()
        TranslateRecursive(Root, Visited)
    End Sub

    Private Sub TranslateRecursive(Node As DependencyObject, Visited As HashSet(Of Object))
        If Node Is Nothing OrElse Not Visited.Add(Node) Then Return

        Dim IsLeafComposite As Boolean = False

        If TypeOf Node Is FrameworkElement Then
            Dim Fe = DirectCast(Node, FrameworkElement)
            Dim TipStr = TryCast(Fe.ToolTip, String)
            If TipStr IsNot Nothing Then
                ApplyTrackedProp(Fe, "ToolTip", TipStr, Sub(v) Fe.ToolTip = v)
            End If
        End If

        If TypeOf Node Is Window Then
            Dim W = DirectCast(Node, Window)
            ApplyTrackedProp(W, "Title", W.Title, Sub(v) W.Title = v)
        ElseIf TypeOf Node Is MyCard Then
            Dim Card = DirectCast(Node, MyCard)
            ApplyTrackedProp(Card, "Title", Card.Title, Sub(v) Card.Title = v)
        ElseIf TypeOf Node Is MyListItem Then
            Dim Item = DirectCast(Node, MyListItem)
            ApplyTrackedProp(Item, "Title", Item.Title, Sub(v) Item.Title = v)
            ApplyTrackedProp(Item, "Info", Item.Info, Sub(v) Item.Info = v)
            If Item.Buttons IsNot Nothing Then
                For Each BtnObj In Item.Buttons.Cast(Of Object)().ToArray()
                    Dim DepBtn = TryCast(BtnObj, DependencyObject)
                    If DepBtn IsNot Nothing Then TranslateRecursive(DepBtn, Visited)
                Next
            End If
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyResourceItem Then
            Dim ResItem = DirectCast(Node, MyResourceItem)
            Dim Proj = TryCast(ResItem.Tag, ResourceProject)
            If Proj IsNot Nothing Then
                Dim Titles = Proj.GetControlTitle(True)
                ResItem.Title = Titles.Title
                If ResItem.LabTitleRaw IsNot Nothing AndAlso Titles.SubTitle <> "" Then
                    ResItem.LabTitleRaw.Text = Titles.SubTitle
                End If
                ResItem.Tags = Proj.Tags.Select(Function(t) LocalizationCore.TranslateText(t)).ToList()
                If ResItem.LabTime IsNot Nothing Then
                    ResItem.LabTime.Text = LocalizationCore.TranslateText(StringUtils.FormatTimeSpan(Proj.LastUpdate - Date.Now, True))
                End If
                If ResItem.LabDownload IsNot Nothing Then
                    ResItem.LabDownload.Text = ResourceProject.FormatCompactDownloadCount(Proj.DownloadCount)
                End If
                If ResItem.LabVersion IsNot Nothing AndAlso Not String.IsNullOrEmpty(ResItem.LabVersion.Text) Then
                    ApplyTrackedProp(ResItem.LabVersion, "Text", ResItem.LabVersion.Text, Sub(v) ResItem.LabVersion.Text = v)
                End If
            Else
                ApplyTrackedProp(ResItem, "Title", ResItem.Title, Sub(v) ResItem.Title = v)
                If ResItem.LabTitleRaw IsNot Nothing Then
                    ApplyTrackedProp(ResItem.LabTitleRaw, "Text", ResItem.LabTitleRaw.Text, Sub(v) ResItem.LabTitleRaw.Text = v)
                End If
                If ResItem.LabVersion IsNot Nothing Then
                    ApplyTrackedProp(ResItem.LabVersion, "Text", ResItem.LabVersion.Text, Sub(v) ResItem.LabVersion.Text = v)
                End If
                If ResItem.LabTime IsNot Nothing Then
                    ApplyTrackedProp(ResItem.LabTime, "Text", ResItem.LabTime.Text, Sub(v) ResItem.LabTime.Text = v)
                End If
            End If
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyButton Then
            Dim Ctrl = DirectCast(Node, MyButton)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyIconTextButton Then
            Dim Ctrl = DirectCast(Node, MyIconTextButton)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyExtraTextButton Then
            Dim Ctrl = DirectCast(Node, MyExtraTextButton)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyTextButton Then
            Dim Ctrl = DirectCast(Node, MyTextButton)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyRadioButton Then
            Dim Ctrl = DirectCast(Node, MyRadioButton)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyCheckBox Then
            Dim Ctrl = DirectCast(Node, MyCheckBox)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyRadioBox Then
            Dim Ctrl = DirectCast(Node, MyRadioBox)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyHint Then
            Dim Ctrl = DirectCast(Node, MyHint)
            ApplyTrackedProp(Ctrl, "Text", Ctrl.Text, Sub(v) Ctrl.Text = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyTextBox Then
            Dim Ctrl = DirectCast(Node, MyTextBox)
            ApplyTrackedProp(Ctrl, "HintText", Ctrl.HintText, Sub(v) Ctrl.HintText = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MySearchBox Then
            Dim Ctrl = DirectCast(Node, MySearchBox)
            ApplyTrackedProp(Ctrl, "HintText", Ctrl.HintText, Sub(v) Ctrl.HintText = v)
            IsLeafComposite = True
        ElseIf TypeOf Node Is MyComboBox Then
            Dim Cmb = DirectCast(Node, MyComboBox)
            ApplyTrackedProp(Cmb, "HintText", Cmb.HintText, Sub(v) Cmb.HintText = v)
            For Each SubItem In Cmb.Items.Cast(Of Object)().ToArray()
                Dim DepItem = TryCast(SubItem, DependencyObject)
                If DepItem IsNot Nothing Then TranslateRecursive(DepItem, Visited)
            Next
            Try
                If Cmb.Items.Count > 0 Then
                    AniControlEnabled += 1
                    Cmb.Items.Refresh()
                    AniControlEnabled -= 1
                End If
            Catch
                If AniControlEnabled > 0 Then AniControlEnabled -= 1
            End Try
            IsLeafComposite = True
        ElseIf TypeOf Node Is ComboBoxItem Then
            Dim Cbi = DirectCast(Node, ComboBoxItem)
            Dim ContentStr = TryCast(Cbi.Content, String)
            If ContentStr IsNot Nothing Then
                ApplyTrackedProp(Cbi, "Content", ContentStr, Sub(v) Cbi.Content = v)
            End If
            IsLeafComposite = True
        ElseIf TypeOf Node Is TabItem Then
            Dim Ti = DirectCast(Node, TabItem)
            Dim HdrStr = TryCast(Ti.Header, String)
            If HdrStr IsNot Nothing Then
                ApplyTrackedProp(Ti, "Header", HdrStr, Sub(v) Ti.Header = v)
            End If
        ElseIf TypeOf Node Is TextBlock Then
            Dim Tb = DirectCast(Node, TextBlock)
            If Tb.Inlines IsNot Nothing AndAlso Tb.Inlines.Count > 1 Then
                Dim InlinesCopy = Tb.Inlines.Cast(Of Inline)().ToArray()
                For Each Inl In InlinesCopy
                    If TypeOf Inl Is Run Then
                        Dim R = DirectCast(Inl, Run)
                        ApplyTrackedProp(R, "Text", R.Text, Sub(v) R.Text = v)
                    End If
                Next
            Else
                ApplyTrackedProp(Tb, "Text", Tb.Text, Sub(v) Tb.Text = v)
            End If
            IsLeafComposite = True
        ElseIf TypeOf Node Is Run Then
            Dim R = DirectCast(Node, Run)
            ApplyTrackedProp(R, "Text", R.Text, Sub(v) R.Text = v)
            IsLeafComposite = True
        End If

        If IsLeafComposite Then Return

        For Each Child In LogicalTreeHelper.GetChildren(Node).Cast(Of Object)().ToArray()
            Dim DepChild = TryCast(Child, DependencyObject)
            If DepChild IsNot Nothing Then TranslateRecursive(DepChild, Visited)
        Next

        If TypeOf Node Is ContentControl Then
            Dim Cc = DirectCast(Node, ContentControl)
            Dim DepContent = TryCast(Cc.Content, DependencyObject)
            If DepContent IsNot Nothing Then TranslateRecursive(DepContent, Visited)
        ElseIf TypeOf Node Is Decorator Then
            Dim Dec = DirectCast(Node, Decorator)
            If Dec.Child IsNot Nothing Then TranslateRecursive(Dec.Child, Visited)
        ElseIf TypeOf Node Is Panel Then
            Dim Pan = DirectCast(Node, Panel)
            For Each Child As UIElement In Pan.Children.Cast(Of UIElement)().ToArray()
                If Child IsNot Nothing Then TranslateRecursive(Child, Visited)
            Next
        ElseIf TypeOf Node Is ItemsControl Then
            Dim Ic = DirectCast(Node, ItemsControl)
            For Each Item In Ic.Items.Cast(Of Object)().ToArray()
                Dim DepItem = TryCast(Item, DependencyObject)
                If DepItem IsNot Nothing Then TranslateRecursive(DepItem, Visited)
            Next
        End If
    End Sub

    Private Sub ApplyTrackedProp(Target As DependencyObject, PropName As String, CurrentVal As String, Setter As Action(Of String))
        If Target Is Nothing OrElse String.IsNullOrEmpty(CurrentVal) Then Return

        Dim Dict As Dictionary(Of String, TrackedPropState) = Nothing
        If Not TrackedMap.TryGetValue(Target, Dict) Then
            Dict = New Dictionary(Of String, TrackedPropState)(StringComparer.Ordinal)
            TrackedMap.Add(Target, Dict)
        End If

        Dim State As TrackedPropState = Nothing
        If Not Dict.TryGetValue(PropName, State) Then
            State = New TrackedPropState With {.OriginalCn = CurrentVal, .LastTranslated = CurrentVal}
            Dict(PropName) = State
        ElseIf Not String.Equals(CurrentVal, State.OriginalCn, StringComparison.Ordinal) AndAlso
               Not String.Equals(CurrentVal, State.LastTranslated, StringComparison.Ordinal) AndAlso
               LocalizationCore.ContainsCjk(CurrentVal) Then
            State.OriginalCn = CurrentVal
        End If

        Dim Desired As String = If(LocalizationCore.IsEnglishActive(), LocalizationCore.TranslateText(State.OriginalCn), State.OriginalCn)
        If Not String.Equals(CurrentVal, Desired, StringComparison.Ordinal) Then
            State.LastTranslated = Desired
            Setter(Desired)
        End If
    End Sub

End Module
