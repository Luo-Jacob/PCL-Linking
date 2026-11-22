Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Text
Imports System.Windows.Input
Imports System.Windows.Threading
Imports Newtonsoft.Json.Linq

Public Class PageToolsServer

    Private IsFirstLoad As Boolean = False
    Private IsDirManuallyEdited As Boolean = False
    Private IsUpdatingUi As Boolean = False
    Private IsBusyGenerating As Boolean = False

    Private Shared RunningServerProc As Process = Nothing
    Private Shared RunningServerDir As String = ""
    Private Shared AutoBackupTimer As Threading.Timer = Nothing
    Private Shared ReadOnly ProcLock As New Object()
    Private Shared IsIntentionalServerStop As Boolean = False
    Private Shared ConsecutiveCrashCount As Integer = 0
    Private Shared LastServerStartUtc As DateTime = DateTime.MinValue
    Private Shared LastAutoBackupUtc As DateTime = DateTime.UtcNow
    Private Shared ActiveAutoRestartEnabled As Boolean = True
    Private Shared ActiveAutoBackupEnabled As Boolean = False
    Private Shared ActiveBackupIntervalMinutes As Integer = 60
    Private Shared IsBackingUpWorld As Integer = 0
    Private MonitorTimer As DispatcherTimer = Nothing
    Private ReadOnly ConsoleLines As New Queue(Of String)
    Private Const MaxConsoleLines As Integer = 500

    Public Shared Sub CleanupRunningServer()
        StopSharedAutoBackupTimer()
        SyncLock ProcLock
            If RunningServerProc Is Nothing Then
                RunningServerDir = ""
                Return
            End If
            Try
                IsIntentionalServerStop = True
                If Not RunningServerProc.HasExited Then
                    Try
                        RunningServerProc.StandardInput.WriteLine("stop")
                        RunningServerProc.StandardInput.Flush()
                    Catch
                    End Try
                    If Not RunningServerProc.WaitForExit(3500) Then
                        RunningServerProc.Kill()
                    End If
                End If
            Catch
            Finally
                Try
                    RunningServerProc.Dispose()
                Catch
                End Try
                RunningServerProc = Nothing
                RunningServerDir = ""
            End Try
        End SyncLock
    End Sub

    Private Shared Sub StartSharedAutoBackupTimer()
        SyncLock ProcLock
            If AutoBackupTimer IsNot Nothing Then Return
            AutoBackupTimer = New Threading.Timer(AddressOf SharedAutoBackupTick, Nothing, 30000, 30000)
        End SyncLock
    End Sub

    Private Shared Sub StopSharedAutoBackupTimer()
        SyncLock ProcLock
            If AutoBackupTimer IsNot Nothing Then
                Try
                    AutoBackupTimer.Dispose()
                Catch
                End Try
                AutoBackupTimer = Nothing
            End If
        End SyncLock
    End Sub

    Private Shared Sub SharedAutoBackupTick(State As Object)
        If Not ActiveAutoBackupEnabled OrElse Threading.Volatile.Read(IsBackingUpWorld) = 1 Then Return
        Dim DirSnap As String = ""
        SyncLock ProcLock
            If RunningServerProc Is Nothing Then Return
            Try
                If RunningServerProc.HasExited Then Return
            Catch
                Return
            End Try
            DirSnap = RunningServerDir
        End SyncLock
        If String.IsNullOrWhiteSpace(DirSnap) OrElse Not Directory.Exists(DirSnap) Then Return
        If (DateTime.UtcNow - LastAutoBackupUtc).TotalMinutes >= ActiveBackupIntervalMinutes Then
            BackupServerWorldCore(DirSnap, IsAuto:=True, FrmToolsServer)
        End If
    End Sub

    Public Shared Function GetSavedServerDirConfigPath() As String
        Return GetPortableDataPath("ServerDir.txt")
    End Function

    Public Shared Function LoadSavedServerDir(Optional CustomConfigPath As String = Nothing) As String
        Try
            Dim PathStr = If(String.IsNullOrWhiteSpace(CustomConfigPath), GetSavedServerDirConfigPath(), CustomConfigPath)
            If Not File.Exists(PathStr) Then Return ""
            Return File.ReadAllText(PathStr, Encoding.UTF8).Trim()
        Catch ex As Exception
            Logger.Warn(ex, "读取保存的服务端目录失败")
            Return ""
        End Try
    End Function

    Public Shared Sub SaveServerDir(DirPath As String, Optional CustomConfigPath As String = Nothing)
        Try
            Dim PathStr = If(String.IsNullOrWhiteSpace(CustomConfigPath), GetSavedServerDirConfigPath(), CustomConfigPath)
            Dim Clean = If(DirPath, "").Trim()
            If Clean = "" Then
                If File.Exists(PathStr) Then File.Delete(PathStr)
                Return
            End If
            Dim Parent = Path.GetDirectoryName(PathStr)
            If Not String.IsNullOrEmpty(Parent) Then Directory.CreateDirectory(Parent)
            File.WriteAllText(PathStr, Clean, New UTF8Encoding(False))
        Catch ex As Exception
            Logger.Warn(ex, "保存服务端目录配置失败")
        End Try
    End Sub

    Public Class ServerProfile
        Public Property Name As String = ""
        Public Property ServerDir As String = ""
        Public Property CoreType As String = "Paper"
        Public Property McVersion As String = "1.21.1"
        Public Property Port As String = "25565"
        Public Property JvmXms As String = "1024M"
        Public Property JvmXmx As String = "4096M"
    End Class

    Public Shared Function GetSavedProfilesConfigPath() As String
        Return GetPortableDataPath("ServerProfiles.json")
    End Function

    Public Shared Function LoadServerProfiles(Optional CustomConfigPath As String = Nothing) As List(Of ServerProfile)
        Dim Result As New List(Of ServerProfile)()
        Try
            Dim PathStr = If(String.IsNullOrWhiteSpace(CustomConfigPath), GetSavedProfilesConfigPath(), CustomConfigPath)
            If Not File.Exists(PathStr) Then Return Result
            Dim Raw = File.ReadAllText(PathStr, Encoding.UTF8).Trim()
            If String.IsNullOrEmpty(Raw) Then Return Result
            Dim Arr = JArray.Parse(Raw)
            For Each Token In Arr
                Dim Obj = TryCast(Token, JObject)
                If Obj Is Nothing Then Continue For
                Dim Dir = If(Obj("ServerDir")?.ToString(), "").Trim()
                If String.IsNullOrEmpty(Dir) Then Continue For
                Dim ProfileName = If(Obj("Name")?.ToString(), "").Trim()
                If String.IsNullOrEmpty(ProfileName) Then ProfileName = Path.GetFileName(Dir.TrimEnd("\"c))
                Result.Add(New ServerProfile With {
                    .Name = ProfileName,
                    .ServerDir = Dir,
                    .CoreType = If(Obj("CoreType")?.ToString(), "Paper").Trim(),
                    .McVersion = If(Obj("McVersion")?.ToString(), "1.21.1").Trim(),
                    .Port = If(Obj("Port")?.ToString(), "25565").Trim(),
                    .JvmXms = If(Obj("JvmXms")?.ToString(), "1024M").Trim(),
                    .JvmXmx = If(Obj("JvmXmx")?.ToString(), "4096M").Trim()
                })
            Next
        Catch ex As Exception
            Logger.Warn(ex, "读取服务端档案配置失败")
        End Try
        Return Result
    End Function

    Public Shared Sub SaveServerProfiles(Profiles As IEnumerable(Of ServerProfile), Optional CustomConfigPath As String = Nothing)
        Try
            Dim PathStr = If(String.IsNullOrWhiteSpace(CustomConfigPath), GetSavedProfilesConfigPath(), CustomConfigPath)
            Dim Parent = Path.GetDirectoryName(PathStr)
            If Not String.IsNullOrEmpty(Parent) Then Directory.CreateDirectory(Parent)
            Dim Arr As New JArray()
            If Profiles IsNot Nothing Then
                For Each P In Profiles
                    If P Is Nothing OrElse String.IsNullOrWhiteSpace(P.ServerDir) Then Continue For
                    Arr.Add(New JObject From {
                        {"Name", If(P.Name, "").Trim()},
                        {"ServerDir", P.ServerDir.Trim()},
                        {"CoreType", If(P.CoreType, "Paper").Trim()},
                        {"McVersion", If(P.McVersion, "1.21.1").Trim()},
                        {"Port", If(P.Port, "25565").Trim()},
                        {"JvmXms", If(P.JvmXms, "1024M").Trim()},
                        {"JvmXmx", If(P.JvmXmx, "4096M").Trim()}
                    })
                Next
            End If
            Dim TmpPath = PathStr & ".tmp"
            File.WriteAllText(TmpPath, Arr.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))
            If File.Exists(PathStr) Then
                File.Replace(TmpPath, PathStr, Nothing, True)
            Else
                File.Move(TmpPath, PathStr)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "保存服务端档案配置失败")
        End Try
    End Sub

    Public Shared Function UpsertServerProfile(Profile As ServerProfile, Optional CustomConfigPath As String = Nothing) As List(Of ServerProfile)
        Dim List = LoadServerProfiles(CustomConfigPath)
        If Profile Is Nothing OrElse String.IsNullOrWhiteSpace(Profile.ServerDir) Then Return List
        Dim CleanDir = Profile.ServerDir.Trim().TrimEnd("\"c)
        Dim Existing = List.FirstOrDefault(Function(x) x.ServerDir.Trim().TrimEnd("\"c).Equals(CleanDir, StringComparison.OrdinalIgnoreCase) OrElse
                                                       (Not String.IsNullOrWhiteSpace(Profile.Name) AndAlso x.Name.Equals(Profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        If Existing IsNot Nothing Then
            Existing.Name = If(String.IsNullOrWhiteSpace(Profile.Name), Existing.Name, Profile.Name.Trim())
            Existing.ServerDir = Profile.ServerDir.Trim()
            Existing.CoreType = If(String.IsNullOrWhiteSpace(Profile.CoreType), Existing.CoreType, Profile.CoreType.Trim())
            Existing.McVersion = If(String.IsNullOrWhiteSpace(Profile.McVersion), Existing.McVersion, Profile.McVersion.Trim())
            Existing.Port = If(String.IsNullOrWhiteSpace(Profile.Port), Existing.Port, Profile.Port.Trim())
            Existing.JvmXms = If(String.IsNullOrWhiteSpace(Profile.JvmXms), Existing.JvmXms, Profile.JvmXms.Trim())
            Existing.JvmXmx = If(String.IsNullOrWhiteSpace(Profile.JvmXmx), Existing.JvmXmx, Profile.JvmXmx.Trim())
        Else
            If String.IsNullOrWhiteSpace(Profile.Name) Then Profile.Name = Path.GetFileName(CleanDir)
            List.Add(Profile)
        End If
        SaveServerProfiles(List, CustomConfigPath)
        Return List
    End Function

    Public Shared Function RemoveServerProfile(ProfileNameOrDir As String, Optional CustomConfigPath As String = Nothing) As List(Of ServerProfile)
        Dim List = LoadServerProfiles(CustomConfigPath)
        Dim Key = If(ProfileNameOrDir, "").Trim().TrimEnd("\"c)
        If Key = "" Then Return List
        List = List.Where(Function(x) Not x.Name.Equals(Key, StringComparison.OrdinalIgnoreCase) AndAlso
                                      Not x.ServerDir.Trim().TrimEnd("\"c).Equals(Key, StringComparison.OrdinalIgnoreCase)).ToList()
        SaveServerProfiles(List, CustomConfigPath)
        Return List
    End Function

    Private Shared Sub DiscoverExistingServerProfiles()
        Try
            Dim ServersRoot = Path.Combine(Paths.Base, "PCL Linking", "Servers")
            If Not Directory.Exists(ServersRoot) Then Return
            Dim Existing = LoadServerProfiles()
            Dim KnownCores As String() = {"Paper", "Purpur", "Vanilla", "Fabric", "NeoForge", "Forge"}
            For Each FolderPath In Directory.GetDirectories(ServersRoot)
                Dim CleanPath = FolderPath.TrimEnd("\"c) & "\"
                If Existing.Any(Function(p) p.ServerDir.TrimEnd("\"c).Equals(FolderPath.TrimEnd("\"c), StringComparison.OrdinalIgnoreCase)) Then Continue For
                Dim HasServerFiles = File.Exists(Path.Combine(FolderPath, "server.properties")) OrElse
                                     File.Exists(Path.Combine(FolderPath, "eula.txt")) OrElse
                                     File.Exists(Path.Combine(FolderPath, "start.bat")) OrElse
                                     Directory.GetFiles(FolderPath, "*.jar", SearchOption.TopDirectoryOnly).Any()
                If Not HasServerFiles Then Continue For

                Dim FolderName = Path.GetFileName(FolderPath.TrimEnd("\"c))
                Dim CoreGuess As String = "Paper"
                Dim VerGuess As String = "1.21.1"
                Dim DashIdx = FolderName.IndexOf("-"c)
                If DashIdx > 0 Then
                    Dim CandidateCore = FolderName.Substring(0, DashIdx)
                    Dim MatchedCore = KnownCores.FirstOrDefault(Function(c) c.Equals(CandidateCore, StringComparison.OrdinalIgnoreCase))
                    If MatchedCore IsNot Nothing Then
                        CoreGuess = MatchedCore
                        VerGuess = FolderName.Substring(DashIdx + 1)
                    End If
                End If
                UpsertServerProfile(New ServerProfile With {
                    .Name = FolderName,
                    .ServerDir = CleanPath,
                    .CoreType = CoreGuess,
                    .McVersion = VerGuess
                })
            Next
        Catch ex As Exception
            Logger.Warn(ex, "扫描本地已有服务端目录失败")
        End Try
    End Sub

    Private Sub RefreshServerProfilesCombo(Optional SelectDir As String = Nothing)
        IsUpdatingUi = True
        Try
            ComboServerProfiles.Items.Clear()
            Dim Profiles = LoadServerProfiles()
            If Profiles.Count = 0 Then
                ComboServerProfiles.Items.Add(New MyComboBoxItem With {.Content = "（暂无已保存档案）", .Tag = Nothing})
                ComboServerProfiles.SelectedIndex = 0
                BtnDeleteServerProfile.IsEnabled = False
                Return
            End If
            Dim TargetDir = If(String.IsNullOrWhiteSpace(SelectDir), TextServerDir.Text.Trim(), SelectDir).TrimEnd("\"c)
            If TargetDir = "" Then
                ComboServerProfiles.Items.Add(New MyComboBoxItem With {.Content = "（请选择已有档案，或保持目录留空以新建）", .Tag = Nothing})
            End If
            Dim MatchIndex As Integer = -1
            For i = 0 To Profiles.Count - 1
                Dim P = Profiles(i)
                Dim Label = $"{P.Name} [{P.CoreType} {P.McVersion}]"
                ComboServerProfiles.Items.Add(New MyComboBoxItem With {.Content = Label, .Tag = P})
                If MatchIndex < 0 AndAlso TargetDir <> "" AndAlso P.ServerDir.Trim().TrimEnd("\"c).Equals(TargetDir, StringComparison.OrdinalIgnoreCase) Then
                    MatchIndex = ComboServerProfiles.Items.Count - 1
                End If
            Next
            If TargetDir = "" Then
                ComboServerProfiles.SelectedIndex = 0
                BtnDeleteServerProfile.IsEnabled = False
            Else
                ComboServerProfiles.SelectedIndex = If(MatchIndex >= 0, MatchIndex, 0)
                Dim Sel = TryCast(ComboServerProfiles.SelectedItem, MyComboBoxItem)
                BtnDeleteServerProfile.IsEnabled = (Sel?.Tag IsNot Nothing)
            End If
        Finally
            IsUpdatingUi = False
        End Try
    End Sub

    Private Sub ApplyServerProfile(P As ServerProfile)
        If P Is Nothing Then Return
        IsUpdatingUi = True
        Try
            IsDirManuallyEdited = True
            Dim CleanDir = P.ServerDir.TrimEnd("\"c) & "\"
            TextServerDir.Text = CleanDir
            SaveServerDir(CleanDir)
            If Not String.IsNullOrWhiteSpace(P.CoreType) Then
                For Each Item As MyComboBoxItem In ComboServerType.Items
                    If String.Equals(Item.Tag?.ToString(), P.CoreType, StringComparison.OrdinalIgnoreCase) Then
                        ComboServerType.SelectedItem = Item
                        Exit For
                    End If
                Next
            End If
            If Not String.IsNullOrWhiteSpace(P.McVersion) Then ComboServerMcVer.Text = P.McVersion
            If Not String.IsNullOrWhiteSpace(P.Port) Then TextPropPort.Text = P.Port
            If Not String.IsNullOrWhiteSpace(P.JvmXms) Then TextJvmXms.Text = P.JvmXms
            If Not String.IsNullOrWhiteSpace(P.JvmXmx) Then TextJvmXmx.Text = P.JvmXmx
        Finally
            IsUpdatingUi = False
        End Try
        LoadExistingProperties(P.ServerDir, ShowHint:=False)
        RefreshPlayerJsonLists(P.ServerDir)
        RefreshServerPluginsList(P.ServerDir)
        LoadPerformanceConfigToUi(P.ServerDir)
    End Sub

    Private Sub ComboServerProfiles_SelectionChanged() Handles ComboServerProfiles.SelectionChanged
        If Not IsFirstLoad OrElse IsUpdatingUi Then Return
        Dim Item = TryCast(ComboServerProfiles.SelectedItem, MyComboBoxItem)
        Dim P = TryCast(Item?.Tag, ServerProfile)
        BtnDeleteServerProfile.IsEnabled = (P IsNot Nothing)
        If P IsNot Nothing Then ApplyServerProfile(P)
    End Sub

    Private Sub BtnSaveServerProfile_Click() Handles BtnSaveServerProfile.Click
        Dim CurDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(CurDir) Then
            Hint("请先设置服务端目录！", HintType.Red)
            Return
        End If
        Dim FolderName = Path.GetFileName(CurDir.TrimEnd("\"c))
        Dim DefaultName = If(String.IsNullOrWhiteSpace(FolderName), $"{GetSelectedServerType()}-{GetTargetMcVersion()}", FolderName)
        Dim InputName = MyMsgBoxInput("保存服务端档案", "请输入要保存的服务端档案名称：", DefaultName)
        If InputName Is Nothing OrElse String.IsNullOrWhiteSpace(InputName) Then Return
        Dim Profile As New ServerProfile With {
            .Name = InputName.Trim(),
            .ServerDir = CurDir.TrimEnd("\"c) & "\",
            .CoreType = GetSelectedServerType(),
            .McVersion = GetTargetMcVersion(),
            .Port = If(String.IsNullOrWhiteSpace(TextPropPort.Text), "25565", TextPropPort.Text.Trim()),
            .JvmXms = If(String.IsNullOrWhiteSpace(TextJvmXms.Text), "1024M", TextJvmXms.Text.Trim()),
            .JvmXmx = If(String.IsNullOrWhiteSpace(TextJvmXmx.Text), "4096M", TextJvmXmx.Text.Trim())
        }
        UpsertServerProfile(Profile)
        SaveServerDir(Profile.ServerDir)
        RefreshServerProfilesCombo(Profile.ServerDir)
        Hint($"已保存服务端档案「{Profile.Name}」！", HintType.Green)
    End Sub

    Private Sub BtnDeleteServerProfile_Click() Handles BtnDeleteServerProfile.Click
        Dim Item = TryCast(ComboServerProfiles.SelectedItem, MyComboBoxItem)
        Dim P = TryCast(Item?.Tag, ServerProfile)
        If P Is Nothing Then Return
        If MyMsgBox($"确定要从档案列表移除「{P.Name}」吗？（不会删除磁盘上的服务端文件）", "删除服务端档案", "移除", "取消", IsWarn:=True) <> 1 Then Return
        RemoveServerProfile(P.Name)
        RefreshServerProfilesCombo()
        Hint($"已移除服务端档案「{P.Name}」！", HintType.Green)
    End Sub

    Private Sub PageToolsServer_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If Not IsFirstLoad Then
            IsFirstLoad = True
            RefreshJavaCombo()
            DiscoverExistingServerProfiles()
            Dim SavedDir = LoadSavedServerDir()
            If Not String.IsNullOrWhiteSpace(SavedDir) Then
                IsDirManuallyEdited = True
                IsUpdatingUi = True
                TextServerDir.Text = SavedDir
                IsUpdatingUi = False
                LoadExistingProperties(SavedDir, ShowHint:=False)
                RefreshPlayerJsonLists(SavedDir)
            Else
                IsDirManuallyEdited = False
                IsUpdatingUi = True
                TextServerDir.Text = ""
                IsUpdatingUi = False
            End If
            RefreshServerProfilesCombo(SavedDir)
            RefreshServerPluginsList(SavedDir)
            LoadPerformanceConfigToUi(SavedDir)
            InitMcVersionsAsync()
        Else
            Dim CurDir = TextServerDir.Text.Trim()
            If Directory.Exists(CurDir) Then
                RefreshPlayerJsonLists(CurDir)
                RefreshServerPluginsList(CurDir)
                LoadPerformanceConfigToUi(CurDir)
            End If
        End If
        SyncGuardBackupConfigFromUi()
        If MonitorTimer Is Nothing Then
            MonitorTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(2)}
            AddHandler MonitorTimer.Tick, AddressOf MonitorTimer_Tick
        End If
        MonitorTimer.Start()
        RefreshServerMonitorPanel()
        RefreshProcessButtons()
    End Sub

    Private Sub PageToolsServer_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        MonitorTimer?.Stop()
    End Sub

    Private Function GetSelectedBackupIntervalMinutes() As Integer
        Dim Item = TryCast(ComboBackupInterval?.SelectedItem, MyComboBoxItem)
        Dim Parsed As Integer = 60
        If Item?.Tag IsNot Nothing Then Integer.TryParse(Item.Tag.ToString(), Parsed)
        Return Math.Max(5, Parsed)
    End Function

    Private Sub SyncGuardBackupConfigFromUi()
        If ChkAutoRestartOnCrash IsNot Nothing Then ActiveAutoRestartEnabled = ChkAutoRestartOnCrash.Checked
        If ChkAutoBackupWorld IsNot Nothing Then ActiveAutoBackupEnabled = ChkAutoBackupWorld.Checked
        ActiveBackupIntervalMinutes = GetSelectedBackupIntervalMinutes()
    End Sub

    Private Sub GuardBackupSetting_Changed() Handles ChkAutoRestartOnCrash.Change, ChkAutoBackupWorld.Change, ComboBackupInterval.SelectionChanged
        If Not IsFirstLoad OrElse IsUpdatingUi Then Return
        SyncGuardBackupConfigFromUi()
        If ChkAutoBackupWorld.Checked Then LastAutoBackupUtc = DateTime.UtcNow
    End Sub

    Private Function GetSelectedServerType() As String
        Dim Item = TryCast(ComboServerType.SelectedItem, MyComboBoxItem)
        Return If(Item?.Tag?.ToString(), "Paper")
    End Function

    Private Function GetTargetMcVersion() As String
        Return ComboServerMcVer.Text.Trim()
    End Function

    Private Sub UpdateDefaultServerDir()
        If IsDirManuallyEdited OrElse String.IsNullOrWhiteSpace(TextServerDir.Text) Then Return
        Dim Core = GetSelectedServerType()
        Dim Ver = GetTargetMcVersion()
        If String.IsNullOrWhiteSpace(Ver) Then Ver = "1.21.1"
        IsUpdatingUi = True
        TextServerDir.Text = Path.Combine(Paths.Base, "PCL Linking", "Servers", $"{Core}-{Ver}") & "\"
        IsUpdatingUi = False
    End Sub

    Private Sub ComboServer_SelectionChanged() Handles ComboServerType.SelectionChanged, ComboServerMcVer.SelectionChanged
        If Not IsFirstLoad OrElse IsUpdatingUi Then Return
        UpdateDefaultServerDir()
    End Sub

    Private Sub RefreshJavaCombo()
        ComboServerJava.Items.Clear()
        Dim List = Configs.JavaList.Get()
        If List IsNot Nothing Then
            For Each J In List
                ComboServerJava.Items.Add(New MyComboBoxItem With {
                    .Content = J.ToString(),
                    .Tag = J.JavaExePath
                })
            Next
        End If
        If ComboServerJava.Items.Count > 0 Then
            ComboServerJava.SelectedIndex = 0
        Else
            ComboServerJava.Items.Add(New MyComboBoxItem With {.Content = "系统默认 PATH (java)", .Tag = "java"})
            ComboServerJava.SelectedIndex = 0
        End If
    End Sub

    Private Sub InitMcVersionsAsync()
        RunInNewThread(
        Sub()
            Try
                If DlClientListLoader.State <> LoadState.Finished Then DlClientListLoader.WaitForExit()
                If DlClientListLoader.Output.Value Is Nothing Then Return
                Dim Releases = CType(DlClientListLoader.Output.Value("versions"), JArray).
                    Where(Function(v) v("type")?.ToString() = "release").
                    Select(Function(v) v("id").ToString()).
                    Take(60).ToList()
                RunInUi(
                Sub()
                    IsUpdatingUi = True
                    Dim OldText = ComboServerMcVer.Text
                    ComboServerMcVer.Items.Clear()
                    For Each Ver In Releases
                        ComboServerMcVer.Items.Add(New MyComboBoxItem With {.Content = Ver})
                    Next
                    If Not String.IsNullOrWhiteSpace(OldText) Then
                        ComboServerMcVer.Text = OldText
                    ElseIf ComboServerMcVer.Items.Count > 0 Then
                        ComboServerMcVer.SelectedIndex = 0
                    End If
                    IsUpdatingUi = False
                    UpdateDefaultServerDir()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "服务端生成器加载 MC 版本列表失败")
            End Try
        End Sub, "Server Load McVersions")
    End Sub

    Private Sub AppendConsole(Line As String)
        If Line Is Nothing Then Return
        RunInUi(
        Sub()
            SyncLock ConsoleLines
                ConsoleLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {Line}")
                While ConsoleLines.Count > MaxConsoleLines
                    ConsoleLines.Dequeue()
                End While
                TextServerConsole.Text = String.Join(Environment.NewLine, ConsoleLines)
            End SyncLock
            TextServerConsole.ScrollToEnd()
        End Sub)
    End Sub

    Private Sub RefreshProcessButtons()
        Dim Running As Boolean = False
        SyncLock ProcLock
            Running = (RunningServerProc IsNot Nothing AndAlso Not RunningServerProc.HasExited)
        End SyncLock
        BtnStartServer.IsEnabled = (Not Running AndAlso Not IsBusyGenerating)
        BtnGenerateServer.IsEnabled = (Not Running AndAlso Not IsBusyGenerating)
        BtnStopServer.IsEnabled = Running
        BtnKillServer.IsEnabled = Running
    End Sub

    Private Sub TextServerDir_TextChanged() Handles TextServerDir.TextChanged
        If Not IsFirstLoad OrElse IsUpdatingUi Then Return
        Dim Val = TextServerDir.Text.Trim()
        If Val = "" Then
            IsDirManuallyEdited = False
            SaveServerDir("")
        Else
            IsDirManuallyEdited = True
            SaveServerDir(Val)
        End If
    End Sub

    Private Sub BtnBrowseServerDir_Click() Handles BtnBrowseServerDir.Click
        Dim Folders = Dialogs.SelectFolder("选择服务端存放根目录", False)
        If Folders Is Nothing OrElse Not Folders.Any() Then Return
        IsDirManuallyEdited = True
        Dim Chosen = Folders(0).TrimEnd("\"c) & "\"
        IsUpdatingUi = True
        TextServerDir.Text = Chosen
        IsUpdatingUi = False
        SaveServerDir(Chosen)
        LoadExistingProperties(Chosen)
        RefreshPlayerJsonLists(Chosen)
        RefreshServerPluginsList(Chosen)
        LoadPerformanceConfigToUi(Chosen)
        RefreshServerProfilesCombo(Chosen)
        RefreshServerMonitorPanel()
    End Sub

    Private Sub BtnOpenServerDir_Click() Handles BtnOpenServerDir.Click
        Dim DirPath = TextServerDir.Text.Trim()
        If String.IsNullOrEmpty(DirPath) Then Return
        Try
            Directory.CreateDirectory(DirPath)
            OpenExplorer(DirPath)
        Catch ex As Exception
            Hint($"无法打开服务端目录：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub LoadExistingProperties(ServerDir As String, Optional ShowHint As Boolean = True)
        Dim PropFile = Path.Combine(ServerDir, "server.properties")
        If Not File.Exists(PropFile) Then Return
        IsUpdatingUi = True
        Try
            For Each Raw In File.ReadAllLines(PropFile, Encoding.UTF8)
                Dim Line = Raw.Trim()
                If Line = "" OrElse Line.StartsWith("#") OrElse Not Line.Contains("=") Then Continue For
                Dim Eq = Line.IndexOf("="c)
                Dim Key = Line.Substring(0, Eq).Trim().ToLowerInvariant()
                Dim Val = Line.Substring(Eq + 1).Trim()
                Select Case Key
                    Case "server-port" : TextPropPort.Text = Val
                    Case "max-players" : TextPropMaxPlayers.Text = Val
                    Case "view-distance" : TextPropViewDistance.Text = Val
                    Case "motd" : TextPropMotd.Text = Val
                    Case "online-mode" : ChkPropOnlineMode.Checked = Val.Equals("true", StringComparison.OrdinalIgnoreCase)
                    Case "pvp" : ChkPropPvp.Checked = Val.Equals("true", StringComparison.OrdinalIgnoreCase)
                    Case "white-list" : ChkPropWhitelist.Checked = Val.Equals("true", StringComparison.OrdinalIgnoreCase)
                    Case "gamemode"
                        For i As Integer = 0 To ComboPropGamemode.Items.Count - 1
                            If CStr(CType(ComboPropGamemode.Items(i), MyComboBoxItem).Tag).Equals(Val, StringComparison.OrdinalIgnoreCase) Then
                                ComboPropGamemode.SelectedIndex = i
                            End If
                        Next
                    Case "difficulty"
                        For i As Integer = 0 To ComboPropDifficulty.Items.Count - 1
                            If CStr(CType(ComboPropDifficulty.Items(i), MyComboBoxItem).Tag).Equals(Val, StringComparison.OrdinalIgnoreCase) Then
                                ComboPropDifficulty.SelectedIndex = i
                            End If
                        Next
                End Select
            Next
            If ShowHint Then Hint("已读取该目录现有的 server.properties 配置", HintType.Blue)
        Catch ex As Exception
            Logger.Warn(ex, "读取现有 server.properties 失败")
        Finally
            IsUpdatingUi = False
        End Try
    End Sub

    Private Sub WriteEulaFile(ServerDir As String)
        Dim EulaPath = Path.Combine(ServerDir, "eula.txt")
        Dim TmpPath = EulaPath & ".tmp"
        File.WriteAllText(
            TmpPath,
            $"# By changing the setting below to TRUE you are indicating your agreement to Mojang EULA.{Environment.NewLine}" &
            $"# Generated by PCL Linking on {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}eula=true{Environment.NewLine}",
            Encoding.ASCII)
        If File.Exists(EulaPath) Then
            File.Replace(TmpPath, EulaPath, Nothing, True)
        Else
            File.Move(TmpPath, EulaPath)
        End If
    End Sub

    Private Function EnsureServerEulaAccepted(ServerDir As String) As Boolean
        If ChkAcceptEula.Checked Then
            WriteEulaFile(ServerDir)
            Return True
        End If
        Dim EulaPath = Path.Combine(ServerDir, "eula.txt")
        If File.Exists(EulaPath) Then
            Try
                If File.ReadAllLines(EulaPath, Encoding.UTF8).Any(Function(l) l.Trim().Equals("eula=true", StringComparison.OrdinalIgnoreCase)) Then
                    ChkAcceptEula.Checked = True
                    Return True
                End If
            Catch
            End Try
        End If
        If MyMsgBox("运行 Minecraft 服务端需要同意 Mojang 官方 EULA (https://aka.ms/MinecraftEULA)。是否现在同意并写入 eula=true？",
                    "确认 Mojang EULA", "我同意 EULA", "取消") = 1 Then
            ChkAcceptEula.Checked = True
            WriteEulaFile(ServerDir)
            Return True
        End If
        Return False
    End Function

    Private Sub WriteServerProperties(ServerDir As String)
        Dim PortNum As Integer = 25565
        If Not Integer.TryParse(If(TextPropPort.Text, "").Trim(), PortNum) OrElse PortNum < 1 OrElse PortNum > 65535 Then PortNum = 25565
        Dim MaxPlayersNum As Integer = 20
        If Not Integer.TryParse(If(TextPropMaxPlayers.Text, "").Trim(), MaxPlayersNum) OrElse MaxPlayersNum < 1 OrElse MaxPlayersNum > 10000 Then MaxPlayersNum = 20
        Dim ViewDistNum As Integer = 10
        If Not Integer.TryParse(If(TextPropViewDistance.Text, "").Trim(), ViewDistNum) OrElse ViewDistNum < 2 OrElse ViewDistNum > 32 Then ViewDistNum = 10
        Dim SafeMotd = If(TextPropMotd.Text, "").Replace(vbCr, " ").Replace(vbLf, " ").Trim()
        If String.IsNullOrWhiteSpace(SafeMotd) Then SafeMotd = "A PCL Linking Local Minecraft Server"

        Dim PropPath = Path.Combine(ServerDir, "server.properties")
        Dim Existing As New List(Of String)
        Dim UpdatedKeys As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"server-port", PortNum.ToString()},
            {"max-players", MaxPlayersNum.ToString()},
            {"view-distance", ViewDistNum.ToString()},
            {"gamemode", If(TryCast(ComboPropGamemode.SelectedItem, MyComboBoxItem)?.Tag?.ToString(), "survival")},
            {"difficulty", If(TryCast(ComboPropDifficulty.SelectedItem, MyComboBoxItem)?.Tag?.ToString(), "normal")},
            {"online-mode", If(ChkPropOnlineMode.Checked, "true", "false")},
            {"pvp", If(ChkPropPvp.Checked, "true", "false")},
            {"white-list", If(ChkPropWhitelist.Checked, "true", "false")},
            {"motd", SafeMotd}
        }
        Dim WrittenKeys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If File.Exists(PropPath) Then
            For Each Raw In File.ReadAllLines(PropPath, Encoding.UTF8)
                Dim Eq = Raw.IndexOf("="c)
                If Eq > 0 AndAlso Not Raw.TrimStart().StartsWith("#") Then
                    Dim Key = Raw.Substring(0, Eq).Trim()
                    If UpdatedKeys.ContainsKey(Key) Then
                        Existing.Add($"{Key}={UpdatedKeys(Key)}")
                        WrittenKeys.Add(Key)
                        Continue For
                    End If
                End If
                Existing.Add(Raw)
            Next
        Else
            Existing.Add("# Minecraft server properties (generated by PCL Linking)")
        End If
        For Each Kv In UpdatedKeys
            If Not WrittenKeys.Contains(Kv.Key) Then Existing.Add($"{Kv.Key}={Kv.Value}")
        Next
        Dim TmpPath = PropPath & ".tmp"
        File.WriteAllLines(TmpPath, Existing, New UTF8Encoding(False))
        If File.Exists(PropPath) Then
            File.Replace(TmpPath, PropPath, Nothing, True)
        Else
            File.Move(TmpPath, PropPath)
        End If
    End Sub

    Private Function GetSelectedJavaExe() As String
        Dim Item = TryCast(ComboServerJava.SelectedItem, MyComboBoxItem)
        Dim JavaExe = If(Item?.Tag?.ToString(), "java")
        If JavaExe <> "java" AndAlso Not File.Exists(JavaExe) Then Return "java"
        Return JavaExe
    End Function

    Private Sub BtnGenerateServer_Click() Handles BtnGenerateServer.Click
        If IsBusyGenerating Then Return
        Dim McVer = GetTargetMcVersion()
        Dim Core = GetSelectedServerType()
        If String.IsNullOrWhiteSpace(McVer) Then
            Hint("请填写目标 MC 版本！", HintType.Red)
            Return
        End If
        Dim ServerDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(ServerDir) Then
            ServerDir = Path.Combine(Paths.Base, "PCL Linking", "Servers", $"{Core}-{McVer}") & "\"
            IsUpdatingUi = True
            TextServerDir.Text = ServerDir
            IsUpdatingUi = False
        End If
        If Not ChkAcceptEula.Checked Then
            If MyMsgBox("创建并运行 Minecraft 服务端需要同意 Mojang 官方 EULA (https://aka.ms/MinecraftEULA)。是否现在同意并写入 eula=true？", "确认 Mojang EULA", "我同意 EULA", "取消") <> 1 Then Return
            ChkAcceptEula.Checked = True
        End If

        Dim JavaExe = GetSelectedJavaExe()
        Dim Xms = If(String.IsNullOrWhiteSpace(TextJvmXms.Text), "1024M", TextJvmXms.Text.Trim())
        Dim Xmx = If(String.IsNullOrWhiteSpace(TextJvmXmx.Text), "4096M", TextJvmXmx.Text.Trim())
        If Not System.Text.RegularExpressions.Regex.IsMatch(Xms, "^\d+[KMGkmg]$") OrElse
           Not System.Text.RegularExpressions.Regex.IsMatch(Xmx, "^\d+[KMGkmg]$") Then
            Hint("JVM 内存参数格式无效，请使用如 1024M / 4G 的格式！", HintType.Red)
            Return
        End If
        Dim NoguiArg = If(ChkServerNogui.Checked, " nogui", "")
        SaveServerDir(ServerDir)
        IsBusyGenerating = True
        RefreshProcessButtons()
        LabServerStatus.Text = $"正在下载并生成 {Core} {McVer} 服务端..."
        AppendConsole($"=== 开始生成 {Core} {McVer} 服务端 -> {ServerDir} ===")

        RunInNewThread(
        Sub()
            Try
                Directory.CreateDirectory(ServerDir)
                Using Http As New HttpClient() With {.Timeout = TimeSpan.FromMinutes(3)}
                    Http.DefaultRequestHeaders.UserAgent.ParseAdd($"PCL-Linking-Fork/{VersionBaseName.Replace(" ", "")} (+https://github.com/Luo-Jacob/PCL-Linking)")
                    Select Case Core
                        Case "Vanilla"
                            GenerateVanillaServer(Http, McVer, ServerDir)
                        Case "Paper"
                            GeneratePaperServer(Http, McVer, ServerDir)
                        Case "Purpur"
                            GeneratePurpurServer(Http, McVer, ServerDir)
                        Case "Fabric"
                            GenerateFabricServer(Http, McVer, ServerDir)
                        Case "NeoForge"
                            GenerateNeoForgeServer(Http, McVer, ServerDir, JavaExe)
                        Case "Forge"
                            GenerateForgeServer(Http, McVer, ServerDir, JavaExe)
                        Case Else
                            Throw New Exception($"不支持的服务端核心：{Core}")
                    End Select
                End Using

                RunInUiWait(
                Sub()
                    WriteEulaFile(ServerDir)
                    WriteServerProperties(ServerDir)
                End Sub)

                '写入便捷 start.bat
                Dim WinArgs = FindModLoaderWinArgsFile(ServerDir)
                Dim BatCmd As String = If(Not String.IsNullOrEmpty(WinArgs),
                    $"""{JavaExe}"" -Xms{Xms} -Xmx{Xmx} @""{WinArgs}""{NoguiArg}",
                    $"""{JavaExe}"" -Xms{Xms} -Xmx{Xmx} -jar ""{FindPrimaryServerJar(ServerDir)}""{NoguiArg}")
                File.WriteAllText(Path.Combine(ServerDir, "start.bat"), $"@echo off{Environment.NewLine}{BatCmd}{Environment.NewLine}pause{Environment.NewLine}", Encoding.Default)

                AppendConsole("=== 服务端生成与 EULA / server.properties 配置完成！可直接点击「启动服务端」。 ===")
                Dim PortStr = "25565"
                RunInUiWait(
                Sub()
                    If Not String.IsNullOrWhiteSpace(TextPropPort.Text) Then PortStr = TextPropPort.Text.Trim()
                    UpsertServerProfile(New ServerProfile With {
                        .Name = Path.GetFileName(ServerDir.TrimEnd("\"c)),
                        .ServerDir = ServerDir.TrimEnd("\"c) & "\",
                        .CoreType = Core,
                        .McVersion = McVer,
                        .Port = PortStr,
                        .JvmXms = Xms,
                        .JvmXmx = Xmx
                    })
                    RefreshServerProfilesCombo(ServerDir)
                    RefreshServerPluginsList(ServerDir)
                    LoadPerformanceConfigToUi(ServerDir)
                    LabServerStatus.Text = $"{Core} {McVer} 服务端已就绪！"
                    Hint($"服务端 {Core} {McVer} 生成成功！", HintType.Green)
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "生成服务端失败")
                AppendConsole($"[错误] 生成失败：{ex.Message}")
                RunInUi(
                Sub()
                    LabServerStatus.Text = $"生成失败：{ex.Message}"
                    Hint($"服务端生成失败：{ex.Message}", HintType.Red)
                End Sub)
            Finally
                RunInUi(
                Sub()
                    IsBusyGenerating = False
                    RefreshProcessButtons()
                End Sub)
            End Try
        End Sub, "Generate Minecraft Server")
    End Sub

    Private Shared Sub DownloadToFile(Http As HttpClient, Url As String, DestPath As String)
        Dim TempPath = DestPath & ".tmp"
        Using Resp = Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
            Resp.EnsureSuccessStatusCode()
            Using NetStream = Resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult(), Fs As New FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None)
                NetStream.CopyTo(Fs)
            End Using
        End Using
        If File.Exists(DestPath) Then File.Delete(DestPath)
        File.Move(TempPath, DestPath)
    End Sub

    Private Shared Sub DownloadServerFile(Http As HttpClient, Urls As IEnumerable(Of String), DestPath As String)
        Dim UrlList = Urls.Where(Function(u) Not String.IsNullOrWhiteSpace(u)).Distinct().ToList()
        If Not UrlList.Any() Then Throw New Exception("未找到有效的服务端下载地址")
        Dim FullDest = Path.GetFullPath(DestPath)
        Try
            Dim Dl As New LoaderDownload("下载服务端核心", New List(Of NetFile) From {
                New NetFile(UrlList, FullDest, New FileChecker With {.MinSize = 10240})
            })
            Dl.WaitForExit()
            If Dl.State = LoadState.Finished AndAlso File.Exists(FullDest) Then Return
        Catch ex As Exception
            Logger.Warn(ex, "分片引擎下载服务端失败，回退直连重试")
        End Try
        Dim LastEx As Exception = Nothing
        For Each U In UrlList
            Try
                DownloadToFile(Http, U, FullDest)
                Return
            Catch ex As Exception
                LastEx = ex
            End Try
        Next
        If LastEx IsNot Nothing Then Throw LastEx
    End Sub

    Private Sub GenerateVanillaServer(Http As HttpClient, McVer As String, ServerDir As String)
        If DlClientListLoader.State <> LoadState.Finished Then DlClientListLoader.WaitForExit()
        Dim Versions = TryCast(DlClientListLoader.Output.Value?("versions"), JArray)
        Dim Entry = Versions?.FirstOrDefault(Function(v) v("id")?.ToString() = McVer)
        If Entry Is Nothing Then Throw New Exception($"未在原版清单中找到 Minecraft {McVer}")
        AppendConsole("正在读取原版服务端元数据（优先使用已配置的下载源）...")
        Dim JsonText As String = Nothing
        Dim LastMetaEx As Exception = Nothing
        For Each MetaUrl In DlSourceLauncherOrMetaGet(Entry("url").ToString())
            Try
                JsonText = Http.GetStringAsync(MetaUrl).GetAwaiter().GetResult()
                Exit For
            Catch ex As Exception
                LastMetaEx = ex
            End Try
        Next
        If String.IsNullOrEmpty(JsonText) Then
            If LastMetaEx IsNot Nothing Then Throw LastMetaEx
            Throw New Exception($"获取 Minecraft {McVer} 元数据失败")
        End If
        Dim VerJson = JObject.Parse(JsonText)
        Dim ServerUrl = VerJson("downloads")?("server")?("url")?.ToString()
        If String.IsNullOrEmpty(ServerUrl) Then Throw New Exception($"Minecraft {McVer} 未提供服务端下载包")
        Dim CandidateUrls = DlSourceLauncherOrMetaGet(ServerUrl).ToList()
        AppendConsole($"正在使用多线程分片引擎下载 server.jar（候选源 {CandidateUrls.Count} 个）...")
        DownloadServerFile(Http, CandidateUrls, Path.Combine(ServerDir, "server.jar"))
    End Sub

    Private Sub GeneratePaperServer(Http As HttpClient, McVer As String, ServerDir As String)
        AppendConsole($"正在通过 PaperMC Fill v3 查询 Paper {McVer} 最新构建...")
        Dim EscapedVer = Uri.EscapeDataString(McVer)
        Dim BuildJson As JObject = Nothing
        Try
            Dim RespText = Http.GetStringAsync($"https://fill.papermc.io/v3/projects/paper/versions/{EscapedVer}/builds/latest").GetAwaiter().GetResult()
            BuildJson = JObject.Parse(RespText)
        Catch ex As HttpRequestException
            '若 latest 不可用则回退到 builds 列表获取最新可用构建
            Dim ArrResp = Http.GetStringAsync($"https://fill.papermc.io/v3/projects/paper/versions/{EscapedVer}/builds").GetAwaiter().GetResult()
            Dim BuildsArr = JArray.Parse(ArrResp)
            If BuildsArr Is Nothing OrElse Not BuildsArr.Any() Then
                Throw New Exception($"Paper 暂不支持 Minecraft {McVer}")
            End If
            BuildJson = CType(BuildsArr.First, JObject)
        End Try

        Dim DownloadNode = BuildJson("downloads")?("server:default")
        If DownloadNode Is Nothing Then
            Dim DownloadsMap = TryCast(BuildJson("downloads"), JObject)
            If DownloadsMap IsNot Nothing AndAlso DownloadsMap.Properties().Any() Then
                DownloadNode = DownloadsMap.Properties().First().Value
            End If
        End If
        If DownloadNode Is Nothing Then Throw New Exception($"Paper {McVer} 未提供可下载的服务端文件")

        Dim BuildNum = If(BuildJson("id"), BuildJson("build"))?.ToString()
        Dim FileName = If(DownloadNode("name")?.ToString(), $"paper-{McVer}-{BuildNum}.jar")
        Dim PrimaryUrl = DownloadNode("url")?.ToString()
        Dim FallbackUrl = $"https://fill-data.papermc.io/v3/projects/paper/versions/{EscapedVer}/builds/{BuildNum}/downloads/{FileName}"
        AppendConsole($"正在下载 Paper #{BuildNum} ({FileName})...")
        DownloadServerFile(Http, {PrimaryUrl, FallbackUrl}, Path.Combine(ServerDir, "server.jar"))
    End Sub

    Private Sub GeneratePurpurServer(Http As HttpClient, McVer As String, ServerDir As String)
        Dim Url = $"https://api.purpurmc.org/v2/purpur/{Uri.EscapeDataString(McVer)}/latest/download"
        AppendConsole($"正在下载 Purpur {McVer} 最新构建 ({Url})...")
        DownloadServerFile(Http, {Url}, Path.Combine(ServerDir, "server.jar"))
    End Sub

    Private Sub GenerateFabricServer(Http As HttpClient, McVer As String, ServerDir As String)
        AppendConsole($"正在查询 Fabric Loader 与安装器版本 ({McVer})...")
        Dim LoadersArr = JArray.Parse(Http.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(McVer)}").GetAwaiter().GetResult())
        If Not LoadersArr.Any() Then Throw New Exception($"Fabric 暂不支持 Minecraft {McVer}")
        Dim LoaderVer = LoadersArr(0)("loader")("version").ToString()
        Dim InstallersArr = JArray.Parse(Http.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer").GetAwaiter().GetResult())
        Dim StableInst = InstallersArr.FirstOrDefault(Function(x) CBool(x("stable")))
        Dim InstVer = If(StableInst, InstallersArr(0))("version").ToString()
        Dim Url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(McVer)}/{Uri.EscapeDataString(LoaderVer)}/{Uri.EscapeDataString(InstVer)}/server/jar"
        AppendConsole($"正在下载 Fabric 服务端启动核心 (Loader {LoaderVer})...")
        DownloadServerFile(Http, {Url}, Path.Combine(ServerDir, "server.jar"))
    End Sub

    Private Sub GenerateNeoForgeServer(Http As HttpClient, McVer As String, ServerDir As String, JavaExe As String)
        AppendConsole($"正在查找 NeoForge 匹配版本 ({McVer})...")
        If DlNeoForgeListLoader.State <> LoadState.Finished Then DlNeoForgeListLoader.WaitForExit()
        Dim List = DlNeoForgeListLoader.Output.Value
        Dim Matches = List.Where(Function(n) n.Inherit = McVer).ToList()
        If Not Matches.Any() Then Throw New Exception($"未找到适配 Minecraft {McVer} 的 NeoForge 版本")
        Dim Entry = If(Matches.FirstOrDefault(Function(n) Not n.IsBeta), Matches(0))
        Dim InstallerPath = Path.Combine(ServerDir, $"neoforge-{Entry.ApiName}-installer.jar")
        DownloadServerFile(Http, {$"https://bmclapi2.bangbang93.com/neoforge/version/{Entry.ApiName}/download/installer.jar", $"{Entry.UrlBase}-installer.jar"}, InstallerPath)
        RunInstallerServer(JavaExe, InstallerPath, ServerDir)
    End Sub

    Private Sub GenerateForgeServer(Http As HttpClient, McVer As String, ServerDir As String, JavaExe As String)
        AppendConsole($"正在查找 Forge 匹配版本 ({McVer})...")
        Dim ForgeLoader As New LoaderTask(Of String, List(Of DlForgeVersionEntry))("Server Forge Lookup", AddressOf DlForgeVersionMain)
        ForgeLoader.WaitForExit(McVer)
        Dim Matches = ForgeLoader.Output
        If Matches Is Nothing OrElse Not Matches.Any() Then Throw New Exception($"未找到适配 Minecraft {McVer} 的 Forge 版本")
        Dim Entry = If(Matches.FirstOrDefault(Function(f) f.IsRecommended AndAlso f.Category = "installer"),
                       Matches.FirstOrDefault(Function(f) f.Category = "installer"))
        If Entry Is Nothing Then Entry = Matches(0)
        Dim InstallerPath = Path.Combine(ServerDir, $"forge-{Entry.Inherit}-{Entry.FileVersion}-installer.jar")
        DownloadServerFile(Http, {
            $"https://bmclapi2.bangbang93.com/forge/download?mcversion={Entry.Inherit}&version={Entry.VersionName}&category=installer&format=jar",
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{Entry.Inherit}-{Entry.FileVersion}/forge-{Entry.Inherit}-{Entry.FileVersion}-installer.jar"
        }, InstallerPath)
        RunInstallerServer(JavaExe, InstallerPath, ServerDir)
    End Sub

    Private Sub RunInstallerServer(JavaExe As String, InstallerPath As String, ServerDir As String)
        AppendConsole($"正在执行服务端安装器：{Path.GetFileName(InstallerPath)} --installServer（首次需联网下载依赖库，请稍候）...")
        Dim Psi As New ProcessStartInfo With {
            .FileName = JavaExe,
            .Arguments = $"-jar ""{InstallerPath}"" --installServer",
            .WorkingDirectory = ServerDir,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }
        Using Proc = Process.Start(Psi)
            AddHandler Proc.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then AppendConsole(e.Data)
            AddHandler Proc.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then AppendConsole("[ERR] " & e.Data)
            Proc.BeginOutputReadLine()
            Proc.BeginErrorReadLine()
            Proc.WaitForExit()
            If Proc.ExitCode <> 0 Then Throw New Exception($"安装器退出码异常 ({Proc.ExitCode})")
        End Using
    End Sub

    Private Shared Function FindModLoaderWinArgsFile(ServerDir As String) As String
        Dim LibDir = Path.Combine(ServerDir, "libraries")
        If Not Directory.Exists(LibDir) Then Return Nothing
        Return Directory.EnumerateFiles(LibDir, "win_args.txt", SearchOption.AllDirectories).FirstOrDefault()
    End Function

    Private Shared Function FindPrimaryServerJar(ServerDir As String) As String
        Dim Direct = Path.Combine(ServerDir, "server.jar")
        If File.Exists(Direct) Then Return Direct
        Dim Jars = Directory.GetFiles(ServerDir, "*.jar").
            Where(Function(j) Not Path.GetFileName(j).Contains("installer")).
            OrderByDescending(Function(j) New FileInfo(j).Length).
            ToList()
        Return Jars.FirstOrDefault()
    End Function

    Private Sub BtnStartServer_Click() Handles BtnStartServer.Click
        StartServerCore(IsAutoRestart:=False)
    End Sub

    Private Sub StartServerCore(IsAutoRestart As Boolean)
        SyncLock ProcLock
            If RunningServerProc IsNot Nothing AndAlso Not RunningServerProc.HasExited Then Return
        End SyncLock

        Dim ServerDir = TextServerDir.Text.Trim()
        If Not Directory.Exists(ServerDir) Then
            Hint("服务端目录不存在，请先一键生成服务端或选择已有服务端目录！", HintType.Red)
            Return
        End If

        Dim WinArgs = FindModLoaderWinArgsFile(ServerDir)
        Dim ServerJar = FindPrimaryServerJar(ServerDir)
        If String.IsNullOrEmpty(WinArgs) AndAlso String.IsNullOrEmpty(ServerJar) Then
            Hint("未在目录中找到 server.jar 或 Forge/NeoForge win_args.txt！", HintType.Red)
            Return
        End If

        Dim Xms = If(String.IsNullOrWhiteSpace(TextJvmXms.Text), "1024M", TextJvmXms.Text.Trim())
        Dim Xmx = If(String.IsNullOrWhiteSpace(TextJvmXmx.Text), "4096M", TextJvmXmx.Text.Trim())
        If Not System.Text.RegularExpressions.Regex.IsMatch(Xms, "^\d+[KMGkmg]$") OrElse
           Not System.Text.RegularExpressions.Regex.IsMatch(Xmx, "^\d+[KMGkmg]$") Then
            Hint("JVM 内存参数格式无效，请使用如 1024M / 4G 的格式！", HintType.Red)
            Return
        End If
        If Not EnsureServerEulaAccepted(ServerDir) Then Return

        If Not IsAutoRestart Then ConsecutiveCrashCount = 0
        SyncGuardBackupConfigFromUi()
        WriteServerProperties(ServerDir)
        SaveServerDir(ServerDir)

        Dim JavaExe = GetSelectedJavaExe()
        Dim JavaMajor = GetSelectedJavaMajor()
        Dim JvmPresetTag = GetSelectedJvmPresetTag()
        Dim XmxMb = ServerConfigAndPluginCore.ParseMemoryToMb(Xmx)
        Dim ExtraJvmFlags = ServerConfigAndPluginCore.BuildJvmPresetFlags(JvmPresetTag, XmxMb, JavaMajor)
        Dim JvmPrefix = If(String.IsNullOrWhiteSpace(ExtraJvmFlags), "", " " & ExtraJvmFlags)
        Dim NoguiArg = If(ChkServerNogui.Checked, " nogui", "")
        Dim Args As String = If(Not String.IsNullOrEmpty(WinArgs),
            $"-Xms{Xms} -Xmx{Xmx}{JvmPrefix} @""{WinArgs}""{NoguiArg}",
            $"-Xms{Xms} -Xmx{Xmx}{JvmPrefix} -jar ""{ServerJar}""{NoguiArg}")

        AppendConsole($"=== {If(IsAutoRestart, $"[自动重启 #{ConsecutiveCrashCount}] ", "")}正在启动服务端：{JavaExe} {Args} ===")
        IsIntentionalServerStop = False
        LastServerStartUtc = DateTime.UtcNow
        LastAutoBackupUtc = DateTime.UtcNow
        Dim Proc As Process = Nothing
        Try
            Dim Psi As New ProcessStartInfo With {
                .FileName = JavaExe,
                .Arguments = Args,
                .WorkingDirectory = ServerDir,
                .UseShellExecute = False,
                .RedirectStandardInput = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }
            Proc = New Process With {.StartInfo = Psi, .EnableRaisingEvents = True}
            AddHandler Proc.OutputDataReceived, Sub(s, e) If e.Data IsNot Nothing Then AppendConsole(e.Data)
            AddHandler Proc.ErrorDataReceived, Sub(s, e) If e.Data IsNot Nothing Then AppendConsole("[ERR] " & e.Data)
            AddHandler Proc.Exited,
            Sub(s, e)
                Dim Code As Integer = -1
                Try
                    Code = Proc.ExitCode
                Catch
                End Try
                SyncLock ProcLock
                    If RunningServerProc Is Proc Then
                        RunningServerProc = Nothing
                        RunningServerDir = ""
                    End If
                End SyncLock
                Try
                    Proc.Dispose()
                Catch
                End Try
                If Not IsServerRunning() Then StopSharedAutoBackupTimer()
                Dim ExpectedStop = IsIntentionalServerStop
                AppendConsole($"=== 服务端进程已退出 (ExitCode={Code}) ===")
                RunInUi(
                Sub()
                    LabServerStatus.Text = If(Code <> 0 AndAlso Not ExpectedStop, $"状态：服务端异常退出 (ExitCode={Code})", "状态：服务端已停止")
                    RefreshProcessButtons()
                    RefreshServerMonitorPanel()
                    RefreshPlayerJsonLists(ServerDir)
                End Sub)
                If Code <> 0 AndAlso Not ExpectedStop Then
                    SaveCrashLogSnapshot(ServerDir, Code)
                    If (DateTime.UtcNow - LastServerStartUtc).TotalMinutes >= 3 Then ConsecutiveCrashCount = 0
                    If ActiveAutoRestartEnabled AndAlso ConsecutiveCrashCount < 3 Then
                        ConsecutiveCrashCount += 1
                        Dim AttemptNo = ConsecutiveCrashCount
                        AppendConsole($"[守护] 服务端意外崩溃，5 秒后将自动执行第 {AttemptNo}/3 次重启……")
                        RunInNewThread(
                        Sub()
                            Thread.Sleep(5000)
                            If IsIntentionalServerStop Then Return
                            If IsServerRunning() Then Return
                            RunInUi(Sub() StartServerCore(IsAutoRestart:=True))
                        End Sub, "Server Crash AutoRestart")
                    Else
                        AnalyzeServerCrashAsync(ServerDir, Code)
                    End If
                End If
            End Sub

            Proc.Start()
            Dim StartedPid As Integer = Proc.Id
            SyncLock ProcLock
                RunningServerProc = Proc
                RunningServerDir = ServerDir
            End SyncLock
            Proc.BeginOutputReadLine()
            Proc.BeginErrorReadLine()
            StartSharedAutoBackupTimer()
            LabServerStatus.Text = $"状态：服务端运行中 (PID {StartedPid})，地址 localhost:{If(String.IsNullOrWhiteSpace(TextPropPort.Text), "25565", TextPropPort.Text.Trim())}"
            RefreshProcessButtons()
            RefreshServerMonitorPanel()
        Catch ex As Exception
            SyncLock ProcLock
                If RunningServerProc Is Proc Then
                    RunningServerProc = Nothing
                    RunningServerDir = ""
                End If
            End SyncLock
            Try
                Proc?.Dispose()
            Catch
            End Try
            Hint($"启动服务端失败：{ex.Message}", HintType.Red)
            AppendConsole($"[错误] 启动失败：{ex.Message}")
            RefreshProcessButtons()
        End Try
    End Sub

    Private Sub SaveCrashLogSnapshot(ServerDir As String, ExitCode As Integer)
        Try
            Dim LogsDir = Path.Combine(ServerDir, "logs")
            Directory.CreateDirectory(LogsDir)
            Dim SnapText As String
            SyncLock ConsoleLines
                SnapText = String.Join(Environment.NewLine, ConsoleLines)
            End SyncLock
            File.WriteAllText(Path.Combine(LogsDir, $"pcl-linking-crash-{DateTime.Now:yyyyMMdd-HHmmss}-code{ExitCode}.log"), SnapText, New UTF8Encoding(False))
        Catch
        End Try
    End Sub

    Private Sub AnalyzeServerCrashAsync(ServerDir As String, ExitCode As Integer)
        Dim PortText As String = ""
        RunInUiWait(Sub() PortText = If(String.IsNullOrWhiteSpace(TextPropPort?.Text), "25565", TextPropPort.Text.Trim()))
        RunInNewThread(
        Sub()
            Try
                Dim LogText As New StringBuilder()
                Dim LatestLog = Path.Combine(ServerDir, "logs", "latest.log")
                If File.Exists(LatestLog) Then
                    Dim Lines = File.ReadAllLines(LatestLog, Encoding.UTF8)
                    For Each Line In Lines.Skip(Math.Max(0, Lines.Length - 180))
                        LogText.AppendLine(Line)
                    Next
                End If
                Dim CrashDir = Path.Combine(ServerDir, "crash-reports")
                If Directory.Exists(CrashDir) Then
                    Dim LatestCrash = Directory.GetFiles(CrashDir, "*.txt").OrderByDescending(Function(f) File.GetLastWriteTimeUtc(f)).FirstOrDefault()
                    If LatestCrash IsNot Nothing AndAlso (DateTime.UtcNow - File.GetLastWriteTimeUtc(LatestCrash)).TotalMinutes < 3 Then
                        LogText.AppendLine("--- CRASH REPORT ---")
                        LogText.AppendLine(File.ReadAllText(LatestCrash, Encoding.UTF8))
                    End If
                End If
                SyncLock ConsoleLines
                    For Each L In ConsoleLines
                        LogText.AppendLine(L)
                    Next
                End SyncLock

                Dim Body = LogText.ToString()
                Dim Reasons As New List(Of String)
                If Body.Contains("UnsupportedClassVersionError") OrElse Body.Contains("compiled by a more recent version of the Java Runtime") Then
                    Reasons.Add("• 当前选择的 Java 版本过低，请切换为 Java 17 或 Java 21。")
                End If
                If Body.Contains("FAILED TO BIND TO PORT") OrElse Body.Contains("Address already in use") Then
                    Reasons.Add($"• 端口 {PortText} 已被其他程序占用（可能上次服务端进程未完全关闭），请更换端口或结束残留进程。")
                End If
                If Body.Contains("agree to the EULA") Then
                    Reasons.Add("• 未同意 Mojang EULA 协议，请确认服务端目录中的 eula.txt 为 eula=true。")
                End If
                If Body.Contains("OutOfMemoryError") OrElse Body.Contains("Java heap space") Then
                    Reasons.Add("• 服务端分配内存 (-Xmx) 不足，请调大「最大内存」设置。")
                End If
                If Body.Contains("Missing or unsupported mandatory dependencies") OrElse Body.Contains("Could not find required mod") OrElse Body.Contains("ModLoadingException") Then
                    Reasons.Add("• 存在 Mod 缺少前置依赖或版本不兼容，请检查 mods 目录及日志中提示的模组名。")
                End If
                If Body.Contains("Unsupported API version") OrElse Body.Contains("Plugin") AndAlso Body.Contains("Error occurred while enabling") Then
                    Reasons.Add("• 部分服务端插件与当前服务端核心或 MC 版本不兼容，请检查 plugins 目录。")
                End If
                If Not Reasons.Any() Then
                    Reasons.Add($"• 服务端非正常退出（退出码 {ExitCode}），可查看上方控制台或 logs/latest.log 定位具体异常。")
                End If

                Dim DiagMsg = $"服务端意外崩溃（ExitCode={ExitCode}）：" & vbCrLf & String.Join(vbCrLf, Reasons)
                AppendConsole("[诊断] " & String.Join(" | ", Reasons))
                RunInUi(
                Sub()
                    If MyMsgBox(DiagMsg, "服务端异常退出诊断", "打开日志目录", "确定", IsWarn:=True) = 1 Then
                        Dim LogDir = Path.Combine(ServerDir, "logs")
                        OpenExplorer(If(Directory.Exists(LogDir), LogDir, ServerDir))
                    End If
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "分析服务端崩溃日志失败")
            End Try
        End Sub, "Analyze Server Crash")
    End Sub

    Private Shared Function SendServerCommandCore(Cmd As String, Optional Owner As PageToolsServer = Nothing, Optional WarnIfStopped As Boolean = True) As Boolean
        If String.IsNullOrWhiteSpace(Cmd) Then Return False
        SyncLock ProcLock
            If RunningServerProc Is Nothing OrElse RunningServerProc.HasExited Then
                If WarnIfStopped Then Hint("服务端当前未在运行！", HintType.Red)
                Return False
            End If
            Try
                RunningServerProc.StandardInput.WriteLine(Cmd)
                RunningServerProc.StandardInput.Flush()
                Owner?.AppendConsole($"> {Cmd}")
                Return True
            Catch ex As Exception
                Owner?.AppendConsole($"[发送指令失败] {ex.Message}")
                Return False
            End Try
        End SyncLock
    End Function

    Private Sub SendServerCommand(Cmd As String)
        SendServerCommandCore(Cmd, Me, WarnIfStopped:=True)
    End Sub

    Private Sub BtnStopServer_Click() Handles BtnStopServer.Click
        IsIntentionalServerStop = True
        LabServerStatus.Text = "状态：正在发送 stop 指令保存并停服..."
        SendServerCommand("stop")
    End Sub

    Private Sub BtnKillServer_Click() Handles BtnKillServer.Click
        If MyMsgBox("强制结束服务端进程可能导致尚未保存的区块损坏，建议优先使用「安全停服 (stop)」。确定仍要强制结束吗？", "强制结束服务端", "强制结束", "取消", IsWarn:=True) <> 1 Then Return
        IsIntentionalServerStop = True
        SyncLock ProcLock
            If RunningServerProc IsNot Nothing AndAlso Not RunningServerProc.HasExited Then
                RunningServerProc.Kill()
            End If
        End SyncLock
        RefreshProcessButtons()
    End Sub

    Private Sub BtnSendCommand_Click() Handles BtnSendCommand.Click
        Dim Cmd = TextServerCommand.Text.Trim()
        If String.IsNullOrEmpty(Cmd) Then Return
        TextServerCommand.Text = ""
        SendServerCommand(Cmd)
    End Sub

    Private Sub TextServerCommand_KeyDown(sender As Object, e As KeyEventArgs) Handles TextServerCommand.KeyDown
        If e.Key = Key.Enter Then
            e.Handled = True
            BtnSendCommand_Click()
        End If
    End Sub

    Private Sub BtnClearConsole_Click() Handles BtnClearConsole.Click
        SyncLock ConsoleLines
            ConsoleLines.Clear()
        End SyncLock
        TextServerConsole.Text = ""
    End Sub

#Region "快捷指令、servers.dat、存档互转与 OP/白名单管理"

    Private Sub BtnQuickSaveAll_Click() Handles BtnQuickSaveAll.Click
        SendServerCommand("save-all")
    End Sub
    Private Sub BtnQuickList_Click() Handles BtnQuickList.Click
        SendServerCommand("list")
    End Sub
    Private Sub BtnQuickDay_Click() Handles BtnQuickDay.Click
        SendServerCommand("time set day")
    End Sub
    Private Sub BtnQuickWeatherClear_Click() Handles BtnQuickWeatherClear.Click
        SendServerCommand("weather clear")
    End Sub
    Private Sub BtnQuickKeepInv_Click() Handles BtnQuickKeepInv.Click
        SendServerCommand("gamerule keepInventory true")
    End Sub
    Private Sub BtnQuickTps_Click() Handles BtnQuickTps.Click
        SendServerCommand("tps")
    End Sub
    Private Sub BtnQuickBroadcast_Click() Handles BtnQuickBroadcast.Click
        Dim Msg = MyMsgBoxInput("发送全服公告", "请输入要通过 say 指令广播给全服玩家的消息：", HintText:="公告内容")
        If Not String.IsNullOrWhiteSpace(Msg) Then SendServerCommand("say " & Msg.Trim())
    End Sub
    Private Sub BtnQuickKick_Click() Handles BtnQuickKick.Click
        Dim Player = MyMsgBoxInput("踢出在线玩家", "请输入要踢出服务器的玩家名：", HintText:="玩家 ID")
        If Not String.IsNullOrWhiteSpace(Player) Then SendServerCommand("kick " & Player.Trim())
    End Sub
    Private Sub BtnQuickBan_Click() Handles BtnQuickBan.Click
        Dim Player = MyMsgBoxInput("封禁玩家", "请输入要封禁的玩家名（ban）：", HintText:="玩家 ID")
        If Not String.IsNullOrWhiteSpace(Player) Then SendServerCommand("ban " & Player.Trim())
    End Sub

    Private Sub MonitorTimer_Tick(sender As Object, e As EventArgs)
        RefreshServerMonitorPanel()
    End Sub

    Private Sub RefreshServerMonitorPanel()
        If LabMonState Is Nothing Then Return
        Dim ProcSnap As Process = Nothing
        SyncLock ProcLock
            If RunningServerProc IsNot Nothing AndAlso Not RunningServerProc.HasExited Then ProcSnap = RunningServerProc
        End SyncLock
        If ProcSnap IsNot Nothing Then
            Try
                ProcSnap.Refresh()
                Dim Mb = ProcSnap.WorkingSet64 / 1024.0 / 1024.0
                Dim Up = DateTime.Now - ProcSnap.StartTime
                LabMonState.Text = $"状态：运行中 (PID {ProcSnap.Id})"
                LabMonRam.Text = $"内存：{Mb:0} MB"
                LabMonUptime.Text = $"运行：{Math.Floor(Up.TotalHours):00}:{Up.Minutes:00}:{Up.Seconds:00} · 线程 {ProcSnap.Threads.Count}"
            Catch
                LabMonState.Text = "状态：运行中"
            End Try
        Else
            LabMonState.Text = "状态：未运行"
            LabMonRam.Text = "内存：--"
            LabMonUptime.Text = "运行时长：--"
        End If
        Dim CurDir = TextServerDir.Text.Trim()
        If Directory.Exists(CurDir) Then
            Dim PlugDir = Path.Combine(CurDir, "plugins")
            Dim ModsDir = Path.Combine(CurDir, "mods")
            Dim PlugCnt = If(Directory.Exists(PlugDir), Directory.GetFiles(PlugDir, "*.jar").Length, 0)
            Dim ModsCnt = If(Directory.Exists(ModsDir), Directory.GetFiles(ModsDir, "*.jar").Length, 0)
            LabMonPlugins.Text = $"插件/Mod：{PlugCnt} 插件 / {ModsCnt} Mod"
        Else
            LabMonPlugins.Text = "插件/Mod：--"
        End If
    End Sub

    Private Sub BtnShareServerToLink_Click() Handles BtnShareServerToLink.Click
        Dim ServerDir = TextServerDir.Text.Trim()
        If Not String.IsNullOrWhiteSpace(ServerDir) Then SaveServerDir(ServerDir)
        Dim PortVal As Integer = 25565
        If Not Integer.TryParse(If(TextPropPort.Text, "").Trim(), PortVal) OrElse PortVal < 1 OrElse PortVal > 65535 Then
            PortVal = 25565
        End If
        If PageLinkMain.LinkState = PageLinkMain.LinkStates.Finished Then
            Dim Inv = PageLinkMain.GetCurrentInvite()
            If Inv IsNot Nothing Then
                Dim Choice = MyMsgBox($"当前已存在活跃的联机房间。{vbCrLf}邀请码：{Inv(0)}{vbCrLf}验证码：{Inv(1)}", "联机房间已就绪", "发给好友", "复制邀请码", "前往联机页")
                If Choice = 1 Then
                    FrmMain.PageChange(FormMain.PageType.Friends)
                ElseIf Choice = 2 Then
                    ClipboardSet($"在 PCL Linking 启动器中输入邀请码【{Inv(0)}】，再输入验证码【{Inv(1)}】，即可加入联机房间！", False)
                    Hint("已复制邀请码！", HintType.Green)
                ElseIf Choice = 3 Then
                    FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
                End If
                Return
            End If
        End If
        PageLinkMain.CreateRoomFromLocalServer(PortVal)
    End Sub

    Private Sub BtnBackupWorldNow_Click() Handles BtnBackupWorldNow.Click
        Dim ServerDir = TextServerDir.Text.Trim()
        If Not Directory.Exists(ServerDir) Then
            Hint("请先设置有效的服务端目录！", HintType.Red)
            Return
        End If
        BackupServerWorldAsync(ServerDir, IsAuto:=False)
    End Sub

    Private Sub BtnOpenBackupsDir_Click() Handles BtnOpenBackupsDir.Click
        Dim ServerDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(ServerDir) Then Return
        Try
            Dim BackupsDir = Path.Combine(ServerDir, "backups")
            Directory.CreateDirectory(BackupsDir)
            OpenExplorer(BackupsDir)
        Catch ex As Exception
            Hint($"无法打开备份目录：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BackupServerWorldAsync(ServerDir As String, IsAuto As Boolean)
        BackupServerWorldCore(ServerDir, IsAuto, Me)
    End Sub

    Private Shared Sub BackupServerWorldCore(ServerDir As String, IsAuto As Boolean, Owner As PageToolsServer)
        If Threading.Interlocked.CompareExchange(IsBackingUpWorld, 1, 0) = 1 Then
            If Not IsAuto Then Hint("当前正在执行世界备份，请稍候！", HintType.Blue)
            Return
        End If
        LastAutoBackupUtc = DateTime.UtcNow
        Dim WasRunning = IsServerRunning()
        If WasRunning Then
            SendServerCommandCore("save-off", Owner, WarnIfStopped:=False)
            SendServerCommandCore("save-all flush", Owner, WarnIfStopped:=False)
        End If
        If Not IsAuto Then Hint("正在打包备份服务端世界存档……", HintType.Blue)
        RunInNewThread(
        Sub()
            Dim TempPath As String = ""
            Try
                If WasRunning Then Thread.Sleep(2000)
                Dim WorldName = GetServerWorldNameStatic(ServerDir)
                Dim CandidateNames = {WorldName, WorldName & "_nether", WorldName & "_the_end"}
                Dim ExistingDirs = CandidateNames.Select(Function(n) (FolderName:=n, FullPath:=Path.Combine(ServerDir, n))).Where(Function(x) Directory.Exists(x.FullPath)).ToList()
                If Not ExistingDirs.Any() Then
                    If Not IsAuto Then RunInUi(Sub() Hint($"未找到世界目录：{WorldName}", HintType.Red))
                    Return
                End If
                Dim BackupsDir = Path.Combine(ServerDir, "backups")
                Directory.CreateDirectory(BackupsDir)
                Dim ZipName = $"{WorldName}_{If(IsAuto, "auto_", "")}{DateTime.Now:yyyyMMdd_HHmmss}.zip"
                Dim FinalPath = Path.Combine(BackupsDir, ZipName)
                TempPath = FinalPath & ".tmp"
                Dim FileCount As Integer = 0
                Using Archive = ZipFile.Open(TempPath, ZipArchiveMode.Create)
                    For Each DimInfo In ExistingDirs
                        For Each SrcFile In Directory.GetFiles(DimInfo.FullPath, "*", SearchOption.AllDirectories)
                            If Path.GetFileName(SrcFile).Equals("session.lock", StringComparison.OrdinalIgnoreCase) Then Continue For
                            Dim Rel = SrcFile.Substring(DimInfo.FullPath.TrimEnd("\"c).Length).TrimStart("\"c).Replace("\"c, "/"c)
                            Archive.CreateEntryFromFile(SrcFile, DimInfo.FolderName & "/" & Rel, CompressionLevel.Optimal)
                            FileCount += 1
                        Next
                    Next
                End Using
                If File.Exists(FinalPath) Then File.Delete(FinalPath)
                File.Move(TempPath, FinalPath)
                TempPath = ""

                '仅保留最近 10 份自动/手动备份，避免占用过多磁盘
                For Each OldZip In New DirectoryInfo(BackupsDir).GetFiles("*.zip").OrderByDescending(Function(f) f.LastWriteTimeUtc).Skip(10)
                    Try
                        OldZip.Delete()
                    Catch
                    End Try
                Next
                Owner?.AppendConsole($"[备份] 世界「{WorldName}」（含 {ExistingDirs.Count} 个维度目录）备份完成（{FileCount} 个文件）-> {ZipName}")
                If Not IsAuto Then
                    RunInUi(Sub() Hint($"服务端世界备份完成：{ZipName}", HintType.Green))
                End If
            Catch ex As Exception
                Logger.Warn(ex, "服务端世界备份失败")
                Owner?.AppendConsole($"[备份失败] {ex.Message}")
                If Not IsAuto Then RunInUi(Sub() Hint($"世界备份失败：{ex.Message}", HintType.Red))
            Finally
                If Not String.IsNullOrEmpty(TempPath) AndAlso File.Exists(TempPath) Then
                    Try
                        File.Delete(TempPath)
                    Catch
                    End Try
                End If
                If WasRunning AndAlso IsServerRunning() Then
                    SendServerCommandCore("save-on", Owner, WarnIfStopped:=False)
                End If
                Threading.Interlocked.Exchange(IsBackingUpWorld, 0)
            End Try
        End Sub, "Backup Server World")
    End Sub

    Private Sub BtnDownloadServerPlugin_Click() Handles BtnDownloadServerPlugin.Click
        Dim CurDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(CurDir) Then
            Hint("请先设置服务端目录！", HintType.Red)
            Return
        End If
        Directory.CreateDirectory(CurDir)
        SaveServerDir(CurDir)
        Dim Core = GetSelectedServerType()
        If Core = "Paper" OrElse Core = "Purpur" Then
            FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadPlugin)
            Hint("已关联当前服务端目录：进入插件详情后点击版本右侧图标即可装入 plugins\\ 目录！", HintType.Green)
        ElseIf Core = "Fabric" OrElse Core = "NeoForge" OrElse Core = "Forge" Then
            FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadMod)
            Hint("已关联当前服务端目录：进入 Mod 详情后点击版本右侧图标即可装入 mods\\ 目录！", HintType.Green)
        Else
            Hint("原版 (Vanilla) 服务端不支持安装插件或 Mod，请先切换为 Paper/Purpur 或 Fabric/NeoForge/Forge。", HintType.Blue)
        End If
    End Sub

    Private Sub BtnOpenServerPluginsDir_Click() Handles BtnOpenServerPluginsDir.Click
        Dim CurDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(CurDir) Then Return
        Try
            Dim Core = GetSelectedServerType()
            Dim SubName = If(Core = "Fabric" OrElse Core = "NeoForge" OrElse Core = "Forge", "mods", "plugins")
            Dim TargetDir = Path.Combine(CurDir, SubName)
            Directory.CreateDirectory(TargetDir)
            OpenExplorer(TargetDir)
        Catch ex As Exception
            Hint($"无法打开插件/Mod 目录：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnSyncClientModsToServer_Click() Handles BtnSyncClientModsToServer.Click
        If IsServerRunning() Then
            Hint("请先安全停止服务端后再同步 Mod！", HintType.Red)
            Return
        End If
        Dim Instance = McInstanceSelected
        If Instance Is Nothing Then
            Hint("请先在启动页面选择一个客户端游戏实例！", HintType.Red)
            Return
        End If
        Dim ServerDir = TextServerDir.Text.Trim()
        If Not Directory.Exists(ServerDir) Then
            Hint("请先设置有效的服务端目录！", HintType.Red)
            Return
        End If
        Dim ClientModsDir = Path.Combine(Instance.PathIndie.TrimEnd("\"c), "mods")
        If Not Directory.Exists(ClientModsDir) Then
            Hint($"实例「{Instance.Name}」的 mods 目录不存在！", HintType.Red)
            Return
        End If

        BtnSyncClientModsToServer.IsEnabled = False
        Hint("正在分析客户端 Mod（自动识别并剔除纯客户端模组）……", HintType.Blue)
        RunInNewThread(
        Sub()
            Try
                Dim AllJars = Directory.GetFiles(ClientModsDir, "*.jar", SearchOption.TopDirectoryOnly)
                Dim ServerJars As New List(Of String)
                Dim ClientOnlyNames As New List(Of String)
                For Each J In AllJars
                    Dim Check = ModPrecheck.IsClientOnlyModJar(J)
                    If Check.IsClientOnly Then
                        ClientOnlyNames.Add($"{Path.GetFileName(J)}（{Check.Reason}）")
                    Else
                        ServerJars.Add(J)
                    End If
                Next

                Dim Sb As New StringBuilder()
                Sb.AppendLine($"源客户端实例：{Instance.Name}")
                Sb.AppendLine($"目标服务端目录：{ServerDir}")
                Sb.AppendLine($"共扫描启用 Mod {AllJars.Length} 个：可上服 {ServerJars.Count} 个，已自动剔除纯客户端 Mod {ClientOnlyNames.Count} 个。")
                If ClientOnlyNames.Any() Then
                    Sb.AppendLine()
                    Sb.AppendLine("【以下纯客户端 Mod 将被跳过（防止服务端启动崩溃）：】")
                    For Each NameLine In ClientOnlyNames.Take(10)
                        Sb.AppendLine("• " & NameLine)
                    Next
                    If ClientOnlyNames.Count > 10 Then Sb.AppendLine($"……其余 {ClientOnlyNames.Count - 10} 个略")
                End If

                RunInUi(
                Sub()
                    BtnSyncClientModsToServer.IsEnabled = True
                    If Not ServerJars.Any() Then
                        MyMsgBox(Sb.ToString(), "客户端 → 服务端 Mod 同步分析", "关闭")
                        Return
                    End If
                    Dim Choice = MyMsgBox(Sb.ToString(), "同步 Mod 到服务端", "同步 Mod 与 config 配置", "仅同步 Mod", "取消")
                    If Choice <> 1 AndAlso Choice <> 2 Then Return
                    Dim IncludeConfig = (Choice = 1)
                    RunInNewThread(
                    Sub()
                        Try
                            Dim DestModsDir = Path.Combine(ServerDir, "mods")
                            Directory.CreateDirectory(DestModsDir)
                            Dim CopiedMods As Integer = 0
                            For Each SrcJar In ServerJars
                                File.Copy(SrcJar, Path.Combine(DestModsDir, Path.GetFileName(SrcJar)), True)
                                CopiedMods += 1
                            Next
                            Dim CopiedCfg As Integer = 0
                            If IncludeConfig Then
                                Dim SrcCfgDir = Path.Combine(Instance.PathIndie.TrimEnd("\"c), "config")
                                If Directory.Exists(SrcCfgDir) Then
                                    Dim DestCfgDir = Path.Combine(ServerDir, "config")
                                    CopyDirectoryRecursive(SrcCfgDir, DestCfgDir)
                                    CopiedCfg = Directory.GetFiles(SrcCfgDir, "*", SearchOption.AllDirectories).Length
                                End If
                            End If
                            AppendConsole($"[Mod 互转] 已从「{Instance.Name}」向服务端同步 {CopiedMods} 个 Mod（跳过 {ClientOnlyNames.Count} 个纯客户端 Mod），同步配置文件 {CopiedCfg} 个")
                            RunInUi(
                            Sub()
                                RefreshServerMonitorPanel()
                                Hint($"已向服务端同步 {CopiedMods} 个 Mod（已自动剔除 {ClientOnlyNames.Count} 个客户端 Mod）！", HintType.Green)
                            End Sub)
                        Catch ex2 As Exception
                            RunInUi(Sub() Hint($"同步失败：{ex2.Message}", HintType.Red))
                        End Try
                    End Sub, "Copy Client Mods To Server")
                End Sub)
            Catch ex As Exception
                RunInUi(
                Sub()
                    BtnSyncClientModsToServer.IsEnabled = True
                    Hint($"分析客户端 Mod 失败：{ex.Message}", HintType.Red)
                End Sub)
            End Try
        End Sub, "Inspect Client Mods For Server")
    End Sub

    Private Sub BtnSyncServerModsToClient_Click() Handles BtnSyncServerModsToClient.Click
        Dim Instance = McInstanceSelected
        If Instance Is Nothing Then
            Hint("请先在启动页面选择一个客户端游戏实例！", HintType.Red)
            Return
        End If
        Dim ServerDir = TextServerDir.Text.Trim()
        Dim ServerModsDir = Path.Combine(ServerDir, "mods")
        If Not Directory.Exists(ServerModsDir) Then
            Hint("当前服务端目录下不存在 mods 文件夹！", HintType.Red)
            Return
        End If
        Dim ClientModsDir = Path.Combine(Instance.PathIndie.TrimEnd("\"c), "mods")
        Directory.CreateDirectory(ClientModsDir)
        Dim SrvJars = Directory.GetFiles(ServerModsDir, "*.jar", SearchOption.TopDirectoryOnly)
        Dim MissingJars = SrvJars.Where(Function(j) Not File.Exists(Path.Combine(ClientModsDir, Path.GetFileName(j)))).ToList()
        If Not MissingJars.Any() Then
            Hint($"实例「{Instance.Name}」已包含服务端全部 {SrvJars.Length} 个 Mod！", HintType.Green)
            Return
        End If
        If MyMsgBox($"检测到服务端 mods 目录中有 {MissingJars.Count} 个 Mod 是客户端实例「{Instance.Name}」缺少的，是否立即复制补齐？",
                    "从服务端同步 Mod 到客户端", "立即同步", "取消") <> 1 Then Return
        Try
            For Each J In MissingJars
                File.Copy(J, Path.Combine(ClientModsDir, Path.GetFileName(J)), False)
            Next
            Hint($"已从服务端同步 {MissingJars.Count} 个 Mod 到实例「{Instance.Name}」！", HintType.Green)
        Catch ex As Exception
            Hint($"同步失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Shared Function IsServerRunning() As Boolean
        SyncLock ProcLock
            Return RunningServerProc IsNot Nothing AndAlso Not RunningServerProc.HasExited
        End SyncLock
    End Function

    Private Shared Function GetServerWorldNameStatic(ServerDir As String) As String
        Dim PropFile = Path.Combine(ServerDir, "server.properties")
        If File.Exists(PropFile) Then
            Try
                For Each Raw In File.ReadAllLines(PropFile, Encoding.UTF8)
                    Dim Line = Raw.Trim()
                    If Line.StartsWith("level-name=", StringComparison.OrdinalIgnoreCase) Then
                        Dim Val = Line.Substring("level-name=".Length).Trim()
                        If Not String.IsNullOrWhiteSpace(Val) Then Return Val
                    End If
                Next
            Catch
            End Try
        End If
        Return "world"
    End Function

    Private Function GetServerWorldName(ServerDir As String) As String
        Return GetServerWorldNameStatic(ServerDir)
    End Function

    Private Shared Sub CopyDirectoryRecursive(SrcDir As String, DestDir As String)
        Directory.CreateDirectory(DestDir)
        For Each SubDir In Directory.GetDirectories(SrcDir, "*", SearchOption.AllDirectories)
            Dim Rel = SubDir.Substring(SrcDir.TrimEnd("\"c).Length).TrimStart("\"c)
            Directory.CreateDirectory(Path.Combine(DestDir, Rel))
        Next
        For Each SrcFile In Directory.GetFiles(SrcDir, "*", SearchOption.AllDirectories)
            If Path.GetFileName(SrcFile).Equals("session.lock", StringComparison.OrdinalIgnoreCase) Then Continue For
            Dim Rel = SrcFile.Substring(SrcDir.TrimEnd("\"c).Length).TrimStart("\"c)
            Dim DestFile = Path.Combine(DestDir, Rel)
            Directory.CreateDirectory(Path.GetDirectoryName(DestFile))
            File.Copy(SrcFile, DestFile, True)
        Next
    End Sub

    Private Sub BtnAddToClientServers_Click() Handles BtnAddToClientServers.Click
        Dim Instance = McInstanceSelected
        If Instance Is Nothing Then
            Hint("请先选择一个客户端游戏实例！", HintType.Red)
            Return
        End If
        Dim Port = If(String.IsNullOrWhiteSpace(TextPropPort.Text), "25565", TextPropPort.Text.Trim())
        Dim Name = If(String.IsNullOrWhiteSpace(TextPropMotd.Text), "PCL Linking 本地服务端", TextPropMotd.Text.Trim())
        Dim ServersDat = Path.Combine(Instance.PathIndie, "servers.dat")
        Try
            Dim Added = ModNbt.EnsureServerInServersDat(ServersDat, Name, $"127.0.0.1:{Port}")
            Hint(If(Added, $"已添加 127.0.0.1:{Port} 到实例「{Instance.Name}」的多人服务器列表！", $"已更新实例「{Instance.Name}」中的服务器配置 127.0.0.1:{Port}"), HintType.Green)
        Catch ex As Exception
            Hint($"写入 servers.dat 失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnCreateServerShortcut_Click() Handles BtnCreateServerShortcut.Click
        Dim Instance = McInstanceSelected
        If Instance Is Nothing Then
            Hint("请先选择一个客户端游戏实例！", HintType.Red)
            Return
        End If
        Dim Port = If(String.IsNullOrWhiteSpace(TextPropPort.Text), "25565", TextPropPort.Text.Trim())
        Dim CurDir = TextServerDir.Text.Trim().TrimEnd("\"c)
        Dim Label = If(String.IsNullOrWhiteSpace(CurDir), Port, Path.GetFileName(CurDir))
        Dim FolderArg = If(McFolderSelected, "").TrimEnd("\"c)
        Try
            Dim Args = If(String.IsNullOrEmpty(FolderArg),
                $"--launch ""{Instance.Name}"" --server ""127.0.0.1:{Port}""",
                $"--launch ""{Instance.Name}"" --server ""127.0.0.1:{Port}"" --folder ""{FolderArg}""")
            Dim Lnk = CreateDesktopShortcut($"直连 {Label}", Args, $"通过 PCL Linking 使用实例 {Instance.Name} 直连 127.0.0.1:{Port}")
            Hint($"已在桌面创建快捷方式：{Path.GetFileName(Lnk)}", HintType.Green)
        Catch ex As Exception
            Hint($"创建快捷方式失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnImportWorldFromSave_Click() Handles BtnImportWorldFromSave.Click
        If IsServerRunning() Then
            Hint("请先安全停止服务端，再导入单机存档！", HintType.Red)
            Return
        End If
        Dim ServerDir = TextServerDir.Text.Trim()
        If Not Directory.Exists(ServerDir) Then
            Hint("请先设置有效的服务端目录！", HintType.Red)
            Return
        End If
        Dim SavesRoot = If(McInstanceSelected IsNot Nothing, Path.Combine(McInstanceSelected.PathIndie, "saves"), "")
        Dim Picked = Dialogs.SelectFolder("选择要导入服务端的单机存档文件夹", False, If(Directory.Exists(SavesRoot), SavesRoot, Nothing))
        If Picked Is Nothing OrElse Not Picked.Any() Then Return
        Dim SrcWorld = Picked(0).TrimEnd("\"c)
        If Not File.Exists(Path.Combine(SrcWorld, "level.dat")) Then
            Hint("所选文件夹不是有效的 Minecraft 世界存档（缺少 level.dat）！", HintType.Red)
            Return
        End If
        Dim WorldName = GetServerWorldName(ServerDir)
        Dim DestWorld = Path.Combine(ServerDir, WorldName)
        If Directory.Exists(DestWorld) Then
            If MyMsgBox($"服务端已存在世界目录「{WorldName}」，导入单机存档「{Path.GetFileName(SrcWorld)}」前会自动将旧世界备份为 {WorldName}_bak_*，是否继续？", "确认导入存档到服务端", "开始导入", "取消", IsWarn:=True) <> 1 Then Return
        End If
        RunInNewThread(
        Sub()
            Try
                If Directory.Exists(DestWorld) Then
                    Dim BakDir = Path.Combine(ServerDir, $"{WorldName}_bak_{DateTime.Now:yyyyMMdd_HHmmss}")
                    Directory.Move(DestWorld, BakDir)
                End If
                CopyDirectoryRecursive(SrcWorld, DestWorld)
                RunInUi(Sub() Hint($"已将单机存档「{Path.GetFileName(SrcWorld)}」导入服务端世界「{WorldName}」！", HintType.Green))
                AppendConsole($"=== 已从 {SrcWorld} 导入世界到 {DestWorld} ===")
            Catch ex As Exception
                RunInUi(Sub() Hint($"导入存档失败：{ex.Message}", HintType.Red))
            End Try
        End Sub, "Import Save To Server")
    End Sub

    Private Sub BtnExportWorldToSave_Click() Handles BtnExportWorldToSave.Click
        If IsServerRunning() Then
            Hint("请先安全停止服务端，再导出世界存档！", HintType.Red)
            Return
        End If
        Dim Instance = McInstanceSelected
        If Instance Is Nothing Then
            Hint("请先选择一个客户端游戏实例！", HintType.Red)
            Return
        End If
        Dim ServerDir = TextServerDir.Text.Trim()
        Dim WorldName = GetServerWorldName(ServerDir)
        Dim SrcWorld = Path.Combine(ServerDir, WorldName)
        If Not Directory.Exists(SrcWorld) OrElse Not File.Exists(Path.Combine(SrcWorld, "level.dat")) Then
            Hint($"服务端目录中未找到有效世界「{WorldName}」！", HintType.Red)
            Return
        End If
        Dim SavesDir = Path.Combine(Instance.PathIndie, "saves")
        Directory.CreateDirectory(SavesDir)
        Dim TargetName = $"{WorldName}_srv_{DateTime.Now:MMdd_HHmm}"
        Dim DestWorld = Path.Combine(SavesDir, TargetName)
        RunInNewThread(
        Sub()
            Try
                CopyDirectoryRecursive(SrcWorld, DestWorld)
                RunInUi(Sub() Hint($"已将服务端世界导出到实例「{Instance.Name}」的存档：{TargetName}", HintType.Green))
            Catch ex As Exception
                RunInUi(Sub() Hint($"导出世界失败：{ex.Message}", HintType.Red))
            End Try
        End Sub, "Export Server World")
    End Sub

    Private Shared Function ComputeOfflineUuid(PlayerName As String) As String
        Using Md5 = System.Security.Cryptography.MD5.Create()
            Dim Hash = Md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" & PlayerName))
            Hash(6) = CByte((Hash(6) And &HF) Or &H30)
            Hash(8) = CByte((Hash(8) And &H3F) Or &H80)
            Dim Hex = BitConverter.ToString(Hash).Replace("-", "").ToLowerInvariant()
            Return $"{Hex.Substring(0, 8)}-{Hex.Substring(8, 4)}-{Hex.Substring(12, 4)}-{Hex.Substring(16, 4)}-{Hex.Substring(20, 12)}"
        End Using
    End Function

    Private Function LoadJsonArrayFile(FilePath As String) As JArray
        If Not File.Exists(FilePath) Then Return New JArray()
        Try
            Dim Raw = File.ReadAllText(FilePath, Encoding.UTF8).Trim()
            If String.IsNullOrEmpty(Raw) Then Return New JArray()
            Return JArray.Parse(Raw)
        Catch
            Return New JArray()
        End Try
    End Function

    Private Sub PopulatePlayerListPanel(
        ServerDir As String,
        FileName As String,
        PanTarget As StackPanel,
        TitleLabel As TextBlock,
        TitleFormat As String,
        InfoProvider As Func(Of JObject, String),
        IconPath As String,
        DeleteTooltip As String,
        LiveRemoveCmdPrefix As String,
        RemovedToastFormat As String)

        Dim JsonPath = Path.Combine(ServerDir, FileName)
        Dim Entries = LoadJsonArrayFile(JsonPath)
        TitleLabel.Text = String.Format(TitleFormat, Entries.Count)
        For Each Entry In Entries.OfType(Of JObject)()
            Dim Name = If(Entry("name")?.ToString(), "")
            If Name = "" Then Continue For
            Dim Row As New MyListItem With {.Title = Name, .Info = InfoProvider(Entry), .Height = 36, .Logo = IconPath}
            Dim BtnDel As New MyIconButton With {.Logo = Logo.IconButtonDelete, .ToolTip = DeleteTooltip}
            Dim Captured = Name
            AddHandler BtnDel.Click,
            Sub()
                If IsServerRunning() Then
                    SendServerCommand($"{LiveRemoveCmdPrefix} {Captured}")
                    Hint($"已向运行中的服务端发送指令：{LiveRemoveCmdPrefix} {Captured}", HintType.Green)
                    RunInNewThread(
                        Sub()
                            Thread.Sleep(500)
                            RunInUi(Sub() RefreshPlayerJsonLists(ServerDir))
                        End Sub, "Refresh Player Json After Live Remove")
                    Return
                End If
                Try
                    Dim CurArr = LoadJsonArrayFile(JsonPath)
                    Dim Filtered = New JArray(CurArr.OfType(Of JObject)().Where(Function(o) Not String.Equals(o("name")?.ToString(), Captured, StringComparison.OrdinalIgnoreCase)))
                    Dim TmpPath = JsonPath & ".tmp"
                    File.WriteAllText(TmpPath, Filtered.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))
                    If File.Exists(JsonPath) Then
                        File.Replace(TmpPath, JsonPath, Nothing, True)
                    Else
                        File.Move(TmpPath, JsonPath)
                    End If
                    RefreshPlayerJsonLists(ServerDir)
                    Hint(String.Format(RemovedToastFormat, Captured), HintType.Green)
                Catch ex As Exception
                    Hint($"更新 {FileName} 失败：{ex.Message}", HintType.Red)
                End Try
            End Sub
            Row.Buttons = {BtnDel}
            PanTarget.Children.Add(Row)
        Next
    End Sub

    Private Sub RefreshPlayerJsonLists(ServerDir As String)
        If PanOpList Is Nothing OrElse PanWhitelistList Is Nothing Then Return
        PanOpList.Children.Clear()
        PanWhitelistList.Children.Clear()
        If String.IsNullOrWhiteSpace(ServerDir) OrElse Not Directory.Exists(ServerDir) Then
            LabOpsTitle.Text = "管理员列表 (OP · 0 人)"
            LabWhitelistTitle.Text = "白名单列表 (0 人)"
            Return
        End If

        PopulatePlayerListPanel(
            ServerDir, "ops.json", PanOpList, LabOpsTitle, "管理员列表 (OP · {0} 人)",
            Function(e) $"权限等级 {If(e("level")?.ToObject(Of Integer)(), 4)}",
            "pack://application:,,,/images/Blocks/CommandBlock.png",
            "移除 OP 权限", "deop", "已移除 {0} 的 OP 权限")

        PopulatePlayerListPanel(
            ServerDir, "whitelist.json", PanWhitelistList, LabWhitelistTitle, "白名单列表 ({0} 人)",
            Function(e) "白名单成员",
            "pack://application:,,,/images/Blocks/Grass.png",
            "移出白名单", "whitelist remove", "已将 {0} 移出白名单")
    End Sub

    Private Sub ChkPropWhitelist_Changed() Handles ChkPropWhitelist.Change
        If IsUpdatingUi Then Return
        Dim ServerDir = TextServerDir.Text.Trim()
        If IsServerRunning() Then
            SendServerCommand(If(ChkPropWhitelist.Checked, "whitelist on", "whitelist off"))
        End If
        If Directory.Exists(ServerDir) Then WriteServerProperties(ServerDir)
    End Sub

    Private Sub BtnRefreshPlayerLists_Click() Handles BtnRefreshPlayerLists.Click
        RefreshPlayerJsonLists(TextServerDir.Text.Trim())
    End Sub

    Private Sub AddPlayerJsonEntry(
        FileName As String,
        LiveAddCmdPrefix As String,
        EntryBuilder As Func(Of String, JObject),
        AddedToastFormat As String)

        Dim Name = TextPlayerAdminName.Text.Trim()
        If String.IsNullOrEmpty(Name) Then
            Hint("请先输入玩家名！", HintType.Red)
            Return
        End If
        If Name.Any(Function(c) Char.IsWhiteSpace(c)) Then
            Hint("玩家名不能包含空格或换行符！", HintType.Red)
            Return
        End If
        Dim ServerDir = TextServerDir.Text.Trim()
        If Not Directory.Exists(ServerDir) Then
            Hint("服务端目录不存在！", HintType.Red)
            Return
        End If
        If IsServerRunning() Then
            SendServerCommand($"{LiveAddCmdPrefix} {Name}")
            TextPlayerAdminName.Text = ""
            Hint(String.Format(AddedToastFormat, Name) & "（已由服务端实时处理）", HintType.Green)
            RunInNewThread(
                Sub()
                    Thread.Sleep(500)
                    RunInUi(Sub() RefreshPlayerJsonLists(ServerDir))
                End Sub, "Refresh Player Json After Live Add")
            Return
        End If
        Try
            Dim JsonPath = Path.Combine(ServerDir, FileName)
            Dim Arr = LoadJsonArrayFile(JsonPath)
            If Not Arr.OfType(Of JObject)().Any(Function(o) String.Equals(o("name")?.ToString(), Name, StringComparison.OrdinalIgnoreCase)) Then
                Arr.Add(EntryBuilder(Name))
                Dim TmpPath = JsonPath & ".tmp"
                File.WriteAllText(TmpPath, Arr.ToString(Newtonsoft.Json.Formatting.Indented), New UTF8Encoding(False))
                If File.Exists(JsonPath) Then
                    File.Replace(TmpPath, JsonPath, Nothing, True)
                Else
                    File.Move(TmpPath, JsonPath)
                End If
            End If
            TextPlayerAdminName.Text = ""
            RefreshPlayerJsonLists(ServerDir)
            Hint(String.Format(AddedToastFormat, Name), HintType.Green)
        Catch ex As Exception
            Hint($"写入 {FileName} 失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub BtnAddOp_Click() Handles BtnAddOp.Click
        AddPlayerJsonEntry(
            "ops.json", "op",
            Function(Name) New JObject From {
                {"uuid", ComputeOfflineUuid(Name)},
                {"name", Name},
                {"level", 4},
                {"bypassesPlayerLimit", False}
            },
            "已将 {0} 设为 OP")
    End Sub

    Private Sub BtnAddWhitelist_Click() Handles BtnAddWhitelist.Click
        AddPlayerJsonEntry(
            "whitelist.json", "whitelist add",
            Function(Name) New JObject From {
                {"uuid", ComputeOfflineUuid(Name)},
                {"name", Name}
            },
            "已将 {0} 加入白名单")
    End Sub

#End Region

#Region "服务端插件管理、更新与高级性能配置"

    Private LastScannedPlugins As New List(Of ServerConfigAndPluginCore.ServerPluginInfo)

    Private Function GetSelectedJavaMajor() As Integer
        Dim Item = TryCast(ComboServerJava?.SelectedItem, MyComboBoxItem)
        Dim Content = If(Item?.Content?.ToString(), "")
        Dim M = System.Text.RegularExpressions.Regex.Match(Content, "Java\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        Dim MajorVal As Integer = 21
        If M.Success AndAlso Integer.TryParse(M.Groups(1).Value, MajorVal) Then Return MajorVal
        Return 21
    End Function

    Private Function GetSelectedJvmPresetTag() As String
        Dim Item = TryCast(ComboServerJvmPreset?.SelectedItem, MyComboBoxItem)
        Return If(Item?.Tag?.ToString(), "Standard")
    End Function

    Private Sub RefreshServerPluginsList(ServerDir As String)
        If PanServerPluginList Is Nothing OrElse LabServerPluginsSummary Is Nothing Then Return
        PanServerPluginList.Children.Clear()
        HintServerPluginDeps.Visibility = Visibility.Collapsed
        LastScannedPlugins.Clear()

        If String.IsNullOrWhiteSpace(ServerDir) OrElse Not Directory.Exists(ServerDir) Then
            LabServerPluginsSummary.Text = "请先生成或选择服务端目录，然后查看 plugins 目录下的插件。"
            Return
        End If

        Dim Plugins = ServerConfigAndPluginCore.InspectServerPluginsDir(ServerDir)
        LastScannedPlugins = Plugins
        Dim EnabledCount = Plugins.Where(Function(p) p.IsEnabled).Count()
        Dim DisabledCount = Plugins.Count - EnabledCount
        If Plugins.Count = 0 Then
            LabServerPluginsSummary.Text = "plugins 目录中暂无插件 (.jar)，可点击下方「下载新插件」快速安装。"
            Return
        End If
        LabServerPluginsSummary.Text = $"已扫描 {Plugins.Count} 个插件（已启用 {EnabledCount} 个，已禁用 {DisabledCount} 个）"

        Dim MissingDeps = ServerConfigAndPluginCore.FindMissingPluginDependencies(Plugins)
        If MissingDeps.Any() Then
            HintServerPluginDeps.Text = $"检测到 {MissingDeps.Count} 项前置插件缺失：" & String.Join("；", MissingDeps.Take(4)) & If(MissingDeps.Count > 4, "……", "")
            HintServerPluginDeps.Visibility = Visibility.Visible
        End If

        For Each P In Plugins
            Dim InfoParts As New List(Of String)
            InfoParts.Add(If(P.IsEnabled, $"已启用 · {P.PlatformKind}", "已禁用"))
            If Not String.IsNullOrWhiteSpace(P.Version) Then InfoParts.Add("v" & P.Version)
            If P.Depends.Any() Then InfoParts.Add($"前置：{String.Join(", ", P.Depends)}")
            InfoParts.Add(P.FileName)

            Dim Row As New MyListItem With {
                .Title = P.Name,
                .Info = String.Join("  |  ", InfoParts),
                .Height = 40,
                .Logo = If(P.IsEnabled, "pack://application:,,,/images/Blocks/CommandBlock.png", "pack://application:,,,/images/Blocks/CobbleStone.png")
            }
            Dim Captured = P
            Dim BtnToggle As New MyIconButton With {
                .Logo = If(P.IsEnabled, Logo.IconButtonStop, Logo.IconButtonCheck),
                .ToolTip = If(P.IsEnabled, "禁用插件（改为 .jar.disabled）", "启用插件")
            }
            AddHandler BtnToggle.Click,
            Sub()
                If IsServerRunning() Then
                    Hint("服务端正在运行，为防止插件文件被占用锁定，请先安全停服再启用/禁用插件！", HintType.Red)
                    Return
                End If
                Try
                    Dim Src = Captured.FilePath
                    Dim Dst = If(Captured.IsEnabled,
                        Src & ".disabled",
                        Src.Substring(0, Src.Length - ".disabled".Length))
                    If File.Exists(Dst) Then
                        Hint($"目标文件已存在，无法切换：{Path.GetFileName(Dst)}", HintType.Red)
                        Return
                    End If
                    File.Move(Src, Dst)
                    RefreshServerPluginsList(ServerDir)
                    RefreshServerMonitorPanel()
                    Hint($"{If(Captured.IsEnabled, "已禁用", "已启用")}插件「{Captured.Name}」", HintType.Green)
                Catch ex As Exception
                    Hint($"切换插件状态失败：{ex.Message}", HintType.Red)
                End Try
            End Sub

            Dim BtnDelete As New MyIconButton With {
                .Logo = Logo.IconButtonDelete,
                .ToolTip = "删除插件文件"
            }
            AddHandler BtnDelete.Click,
            Sub()
                If IsServerRunning() Then
                    Hint("服务端正在运行，请先安全停服再删除插件！", HintType.Red)
                    Return
                End If
                If MyMsgBox($"确定要永久删除插件「{Captured.Name}」({Captured.FileName}) 吗？", "删除服务端插件", "确认删除", "取消", IsWarn:=True) <> 1 Then Return
                Try
                    If File.Exists(Captured.FilePath) Then File.Delete(Captured.FilePath)
                    RefreshServerPluginsList(ServerDir)
                    RefreshServerMonitorPanel()
                    Hint($"已删除插件「{Captured.Name}」", HintType.Green)
                Catch ex As Exception
                    Hint($"删除插件失败：{ex.Message}", HintType.Red)
                End Try
            End Sub

            Row.Buttons = {BtnToggle, BtnDelete}
            PanServerPluginList.Children.Add(Row)
        Next
    End Sub

    Private Sub BtnRefreshServerPlugins_Click() Handles BtnRefreshServerPlugins.Click
        RefreshServerPluginsList(TextServerDir.Text.Trim())
    End Sub

    Private Sub BtnInstallMorePlugins_Click() Handles BtnInstallMorePlugins.Click
        FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadPlugin)
    End Sub

    Private Sub BtnCheckPluginDeps_Click() Handles BtnCheckPluginDeps.Click
        Dim ServerDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(ServerDir) OrElse Not Directory.Exists(ServerDir) Then
            Hint("请先设置有效的服务端目录！", HintType.Red)
            Return
        End If
        Dim Plugins = ServerConfigAndPluginCore.InspectServerPluginsDir(ServerDir)
        Dim Missing = ServerConfigAndPluginCore.FindMissingPluginDependencies(Plugins)
        If Not Plugins.Any() Then
            Hint("plugins 目录中尚未安装任何插件！", HintType.Blue)
        ElseIf Not Missing.Any() Then
            MyMsgBox($"已扫描 {Plugins.Where(Function(p) p.IsEnabled).Count()} 个已启用插件，所有 plugin.yml / paper-plugin.yml 声明的必需前置插件均已满足。", "服务端插件依赖体检通过", "太棒了")
        Else
            MyMsgBox("检测到以下前置插件缺失（可能会导致相应插件无法加载）：" & vbCrLf & vbCrLf & String.Join(vbCrLf, Missing.Select(Function(s) "• " & s)),
                     "服务端插件依赖体检", "我知道了", IsWarn:=True)
        End If
    End Sub

    Private Shared Function NormalizePluginVerText(Raw As String) As String
        Dim S = If(Raw, "").Trim()
        If S.StartsWith("v", StringComparison.OrdinalIgnoreCase) AndAlso S.Length > 1 AndAlso Char.IsDigit(S(1)) Then
            S = S.Substring(1)
        End If
        Return S
    End Function

    Private Sub BtnCheckPluginUpdates_Click() Handles BtnCheckPluginUpdates.Click
        Dim ServerDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(ServerDir) OrElse Not Directory.Exists(ServerDir) Then
            Hint("请先设置有效的服务端目录！", HintType.Red)
            Return
        End If
        Dim EnabledPlugins = ServerConfigAndPluginCore.InspectServerPluginsDir(ServerDir).Where(Function(p) p.IsEnabled).ToList()
        If Not EnabledPlugins.Any() Then
            Hint("plugins 目录中没有可检查更新的已启用插件！", HintType.Blue)
            Return
        End If

        BtnCheckPluginUpdates.IsEnabled = False
        Dim TargetMcVer = GetTargetMcVersion()
        Hint($"正在通过 Modrinth / Hangar 为 {EnabledPlugins.Count} 个服务端插件检查更新……", HintType.Blue)

        RunInNewThread(
        Sub()
            Try
                Dim HashToPlugin As New Dictionary(Of String, ServerConfigAndPluginCore.ServerPluginInfo)(StringComparer.OrdinalIgnoreCase)
                For Each P In EnabledPlugins
                    Try
                        P.Sha1 = ServerConfigAndPluginCore.ComputeFileSha1Lower(P.FilePath)
                        If Not String.IsNullOrEmpty(P.Sha1) AndAlso Not HashToPlugin.ContainsKey(P.Sha1) Then
                            HashToPlugin(P.Sha1) = P
                        End If
                    Catch
                    End Try
                Next

                Dim Updates As New List(Of (Installed As ServerConfigAndPluginCore.ServerPluginInfo, NewVer As ResourceVersion, SourceLabel As String))
                Dim MatchedPlugins As New HashSet(Of ServerConfigAndPluginCore.ServerPluginInfo)

                '1. Modrinth SHA-1 批量检查更新
                If HashToPlugin.Any() Then
                    Try
                        Dim ReqBody As New JObject From {
                            {"hashes", New JArray(HashToPlugin.Keys)},
                            {"algorithm", "sha1"},
                            {"loaders", New JArray("paper", "purpur", "spigot", "bukkit", "velocity")}
                        }
                        If Not String.IsNullOrWhiteSpace(TargetMcVer) Then
                            ReqBody("game_versions") = New JArray(TargetMcVer)
                        End If
                        Dim RespObj = TryCast(DlModRequest("https://api.modrinth.com/v2/version_files/update", HttpMethod.Post, ReqBody.ToString(Newtonsoft.Json.Formatting.None), "application/json"), JObject)
                        If RespObj IsNot Nothing Then
                            For Each Prop In RespObj.Properties()
                                Dim OldHash = Prop.Name
                                Dim Installed As ServerConfigAndPluginCore.ServerPluginInfo = Nothing
                                If Not HashToPlugin.TryGetValue(OldHash, Installed) Then Continue For
                                Dim VerObj = TryCast(Prop.Value, JObject)
                                If VerObj Is Nothing Then Continue For
                                MatchedPlugins.Add(Installed)
                                Dim ParsedVer = ResourceVersion.FromPlatformJson(VerObj, ResourceTypes.Plugin)
                                If ParsedVer IsNot Nothing AndAlso ParsedVer.DownloadAvailable AndAlso
                                   Not String.IsNullOrEmpty(ParsedVer.Hash) AndAlso
                                   Not ParsedVer.Hash.Equals(OldHash, StringComparison.OrdinalIgnoreCase) Then
                                    Updates.Add((Installed, ParsedVer, "Modrinth"))
                                End If
                            Next
                        End If
                    Catch ex As Exception
                        Logger.Warn(ex, "Modrinth 插件批量检查更新失败，将继续尝试 Hangar")
                    End Try
                End If

                '2. 未命中 Modrinth 的插件按名称精确查询 Hangar
                For Each P In EnabledPlugins
                    If MatchedPlugins.Contains(P) OrElse String.IsNullOrWhiteSpace(P.Name) Then Continue For
                    Try
                        Dim SearchUrl = $"https://hangar.papermc.io/api/v1/projects?q={StringUtils.UrlEscape(P.Name)}&limit=5"
                        Dim HangarSearch = TryCast(DlModRequest(SearchUrl), JObject)
                        Dim Hits = TryCast(HangarSearch?("result"), JArray)
                        If Hits Is Nothing OrElse Hits.Count = 0 Then Continue For
                        Dim ExactHit = Hits.OfType(Of JObject)().FirstOrDefault(Function(h) String.Equals(h("name")?.ToString(), P.Name, StringComparison.OrdinalIgnoreCase))
                        If ExactHit Is Nothing Then Continue For
                        Dim Owner = ExactHit("namespace")?("owner")?.ToString()
                        Dim Slug = ExactHit("namespace")?("slug")?.ToString()
                        If String.IsNullOrWhiteSpace(Owner) OrElse String.IsNullOrWhiteSpace(Slug) Then Continue For
                        Dim Versions = ResourceVersion.FromProjectId($"{Owner}/{Slug}", ResourcePlatforms.Hangar, LoadDependencies:=False)
                        If Versions Is Nothing OrElse Not Versions.Any() Then Continue For
                        Dim Cand = Versions.Where(Function(v) v.DownloadAvailable AndAlso Not String.IsNullOrEmpty(v.DownloadUrls?.FirstOrDefault())).
                            OrderByDescending(Function(v) v.ReleaseDate).FirstOrDefault()
                        If Cand Is Nothing Then Continue For
                        Dim CurV = NormalizePluginVerText(P.Version)
                        Dim NewV = NormalizePluginVerText(Cand.Version)
                        If CurV <> "" AndAlso NewV <> "" AndAlso McUtils.CompareVersion(NewV, CurV) > 0 Then
                            Updates.Add((P, Cand, "Hangar"))
                        End If
                    Catch
                    End Try
                Next

                RunInUi(
                Sub()
                    BtnCheckPluginUpdates.IsEnabled = True
                    If Not Updates.Any() Then
                        MyMsgBox($"已完成 {EnabledPlugins.Count} 个插件的更新检查，当前全部为最新适配版本（或作者未发布到 Modrinth/Hangar 哈希库）。", "服务端插件检查更新")
                        Return
                    End If

                    Dim Sb As New StringBuilder()
                    Sb.AppendLine($"发现 {Updates.Count} 个可升级的服务端插件：")
                    Sb.AppendLine()
                    For Each U In Updates.Take(10)
                        Sb.AppendLine($"• {U.Installed.Name}：v{U.Installed.Version} → v{If(U.NewVer.Version, U.NewVer.Display)} [{U.SourceLabel}]")
                    Next
                    If Updates.Count > 10 Then Sb.AppendLine($"……等共 {Updates.Count} 个")
                    If MyMsgBox(Sb.ToString().TrimEnd(), "发现服务端插件更新", "一键下载更新", "取消") <> 1 Then Return

                    If IsServerRunning() Then
                        Hint("服务端正在运行，为防止旧插件被占用，请先安全停服后再执行插件更新！", HintType.Red)
                        Return
                    End If

                    Dim PluginsDir = Path.Combine(ServerDir, "plugins")
                    Directory.CreateDirectory(PluginsDir)
                    Dim NetFiles As New List(Of NetFile)
                    Dim OldFilesToRemove As New List(Of String)
                    For Each U In Updates
                        Dim TargetPath = Path.Combine(PluginsDir, U.NewVer.FileName)
                        NetFiles.Add(U.NewVer.ToNetFile(TargetPath, ResourceVersion.DownloadReason.Update))
                        If Not U.Installed.FilePath.Equals(TargetPath, StringComparison.OrdinalIgnoreCase) Then
                            OldFilesToRemove.Add(U.Installed.FilePath)
                        End If
                    Next
                    Dim Loaders As New List(Of LoaderBase) From {
                        New LoaderDownload("下载新版插件", NetFiles) With {.ProgressWeight = 8, .Block = True},
                        New LoaderTask(Of Integer, Integer)("清理旧版插件",
                        Sub()
                            For Each OldPath In OldFilesToRemove
                                Try
                                    If File.Exists(OldPath) Then File.Delete(OldPath)
                                Catch
                                End Try
                            Next
                        End Sub) With {.ProgressWeight = 0.5}
                    }
                    Dim UpdLoader As New LoaderCombo(Of Integer)($"服务端插件更新（{Updates.Count} 个） ", Loaders) With {.OnStateChanged =
                    Sub(MyLoader)
                        LoaderStateChangedHintOnly(MyLoader)
                        If MyLoader.State = LoadState.Finished Then
                            RunInUi(
                            Sub()
                                RefreshServerPluginsList(ServerDir)
                                RefreshServerMonitorPanel()
                                Hint($"已完成 {Updates.Count} 个服务端插件更新！", HintType.Green)
                            End Sub)
                        End If
                    End Sub}
                    UpdLoader.Start(1)
                    LoaderTaskbarAdd(UpdLoader)
                    FrmMain.BtnExtraDownload.ShowRefresh()
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "检查服务端插件更新失败")
                RunInUi(
                Sub()
                    BtnCheckPluginUpdates.IsEnabled = True
                    Hint($"检查插件更新失败：{ex.Message}", HintType.Red)
                End Sub)
            End Try
        End Sub, "Server Plugin Update Check")
    End Sub

    Private Sub BindPerformanceConfigFields(Cfg As ServerConfigAndPluginCore.ServerPerformanceConfig, Optional UpdateViewDistance As Boolean = False)
        If TextPerfSimDist Is Nothing OrElse Cfg Is Nothing Then Return
        IsUpdatingUi = True
        Try
            If UpdateViewDistance Then TextPropViewDistance.Text = Cfg.ViewDistance.ToString()
            TextPerfSimDist.Text = Cfg.SimulationDistance.ToString()
            TextPerfSpawnProt.Text = Cfg.SpawnProtection.ToString()
            TextPerfNetCompress.Text = Cfg.NetworkCompressionThreshold.ToString()
            TextPerfMaxTick.Text = Cfg.MaxTickTime.ToString()
            TextPerfMonsters.Text = Cfg.MonsterSpawnLimit.ToString()
            TextPerfAnimals.Text = Cfg.AnimalSpawnLimit.ToString()
            TextPerfAutoSaveChunks.Text = Cfg.PaperMaxAutoSaveChunks.ToString()
            ChkPerfAllowFlight.Checked = Cfg.AllowFlight
            ChkPerfCommandBlock.Checked = Cfg.EnableCommandBlock
            ChkPerfSyncChunk.Checked = Cfg.SyncChunkWrites
            ChkPerfOptimizeExp.Checked = Cfg.PaperOptimizeExplosions
            ChkPerfPreventUnloaded.Checked = Cfg.PaperPreventUnloadedChunks
            For i As Integer = 0 To ComboServerJvmPreset.Items.Count - 1
                Dim Item = TryCast(ComboServerJvmPreset.Items(i), MyComboBoxItem)
                If String.Equals(Item?.Tag?.ToString(), Cfg.JvmPreset, StringComparison.OrdinalIgnoreCase) Then
                    ComboServerJvmPreset.SelectedIndex = i
                    Exit For
                End If
            Next
        Finally
            IsUpdatingUi = False
        End Try
    End Sub

    Private Sub LoadPerformanceConfigToUi(ServerDir As String)
        If TextPerfSimDist Is Nothing Then Return
        BindPerformanceConfigFields(ServerConfigAndPluginCore.LoadPerformanceConfig(ServerDir))
    End Sub

    Private Function ReadPerformanceConfigFromUi() As ServerConfigAndPluginCore.ServerPerformanceConfig
        Dim ParseIntOrDefault =
        Function(TextVal As String, DefaultVal As Integer) As Integer
            Dim V As Integer
            If Integer.TryParse(If(TextVal, "").Trim(), V) Then Return V
            Return DefaultVal
        End Function
        Return New ServerConfigAndPluginCore.ServerPerformanceConfig With {
            .ViewDistance = ParseIntOrDefault(TextPropViewDistance.Text, 10),
            .SimulationDistance = ParseIntOrDefault(TextPerfSimDist.Text, 10),
            .SpawnProtection = ParseIntOrDefault(TextPerfSpawnProt.Text, 16),
            .NetworkCompressionThreshold = ParseIntOrDefault(TextPerfNetCompress.Text, 256),
            .MaxTickTime = ParseIntOrDefault(TextPerfMaxTick.Text, 60000),
            .MonsterSpawnLimit = ParseIntOrDefault(TextPerfMonsters.Text, 70),
            .AnimalSpawnLimit = ParseIntOrDefault(TextPerfAnimals.Text, 10),
            .PaperMaxAutoSaveChunks = ParseIntOrDefault(TextPerfAutoSaveChunks.Text, 24),
            .AllowFlight = ChkPerfAllowFlight.Checked,
            .EnableCommandBlock = ChkPerfCommandBlock.Checked,
            .SyncChunkWrites = ChkPerfSyncChunk.Checked,
            .PaperOptimizeExplosions = ChkPerfOptimizeExp.Checked,
            .PaperPreventUnloadedChunks = ChkPerfPreventUnloaded.Checked,
            .JvmPreset = GetSelectedJvmPresetTag()
        }
    End Function

    Private Sub ApplyOptimizationPresetToUi(PresetName As String, DisplayTitle As String)
        BindPerformanceConfigFields(ServerConfigAndPluginCore.BuildOptimizationPreset(PresetName), UpdateViewDistance:=True)
        Hint($"已填入「{DisplayTitle}」参数预设，请点击「保存高级配置」写入服务端文件！", HintType.Blue)
    End Sub

    Private Sub BtnPresetSmoothCoop_Click() Handles BtnPresetSmoothCoop.Click
        ApplyOptimizationPresetToUi("SmoothCoop", "联机流畅")
    End Sub

    Private Sub BtnPresetLowSpec_Click() Handles BtnPresetLowSpec.Click
        ApplyOptimizationPresetToUi("LowSpec", "低配轻量")
    End Sub

    Private Sub BtnPresetVanilla_Click() Handles BtnPresetVanilla.Click
        ApplyOptimizationPresetToUi("Vanilla", "原版默认")
    End Sub

    Private Sub BtnReloadPerfConfig_Click() Handles BtnReloadPerfConfig.Click
        Dim ServerDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(ServerDir) OrElse Not Directory.Exists(ServerDir) Then
            Hint("当前服务端目录不存在，无法读取！", HintType.Red)
            Return
        End If
        LoadExistingProperties(ServerDir, ShowHint:=False)
        LoadPerformanceConfigToUi(ServerDir)
        Hint("已重新读取 server.properties / bukkit.yml / paper 配置！", HintType.Blue)
    End Sub

    Private Sub BtnSavePerfConfig_Click() Handles BtnSavePerfConfig.Click
        Dim ServerDir = TextServerDir.Text.Trim()
        If String.IsNullOrWhiteSpace(ServerDir) Then
            Hint("请先设置服务端目录！", HintType.Red)
            Return
        End If
        Try
            Dim Cfg = ReadPerformanceConfigFromUi()
            Dim Core = GetSelectedServerType()
            Dim IsPluginCore = Core.Equals("Paper", StringComparison.OrdinalIgnoreCase) OrElse Core.Equals("Purpur", StringComparison.OrdinalIgnoreCase)
            ServerConfigAndPluginCore.SavePerformanceConfig(ServerDir, Cfg, AllowCreatePluginYaml:=IsPluginCore)
            SaveServerDir(ServerDir)
            Hint("已保存高级性能配置与 JVM 预设！" & If(IsServerRunning(), "部分参数与 JVM 参数将在重启服务端后生效。", ""), HintType.Green)
        Catch ex As Exception
            Hint($"保存高级配置失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

End Class
