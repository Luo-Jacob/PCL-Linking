''' <summary>
''' PCL Linking：原本挂在「百宝箱」「更多」页面上的工具函数。
''' 这些页面已被移除，但启动流程、日志、自定义事件仍在调用，所以集中放在这个模块里。
''' </summary>
Public Module ModExtra

    ''' <summary>
    ''' 启动器是否已经是最新版？PCL Linking 不联网检查更新，恒为 True。
    ''' </summary>
    Public Function IsLauncherNewest() As Boolean?
        Return True
    End Function

    ''' <summary>
    ''' 内存优化（PCL Linking 未实现，仅保留调用点）。
    ''' </summary>
    Public Sub MemoryOptimize(ShowHint As Boolean)
    End Sub

    Public Sub MemoryOptimizeInternal(ShowHint As Boolean)
    End Sub

    Public Sub Jrrp()
        Hint("PCL Linking 不包含这个功能。")
    End Sub

    Public Sub RubbishClear()
        Hint("PCL Linking 不包含这个功能。")
    End Sub

    Public Sub StartCustomDownload(Url As String, FileName As String, Optional Folder As String = Nothing)
        Hint("PCL Linking 不包含内置下载工具，请手动下载：" & FileName)
    End Sub

    ''' <summary>
    ''' 启动中的随机提示。
    ''' </summary>
    Private ReadOnly Hints As String() = {
        "你知道吗：本分支为 Plain Craft Launcher (PCL Linking 联机修改版)，由 PCL 2.14.0.0 开源版二次开发而来",
        "你知道吗：上游原版 PCL 作者为龙腾猫跃 (LTCat)，日常使用官方正版功能也推荐使用原版 PCL",
        "你知道吗：本修改版开源仓库为 https://github.com/Luo-Jacob/PCL-Linking",
        "你可以：在「版本设置 → Mod 管理」点「从文件安装」或「下载新 Mod」添加 Mod，也可以直接把 .jar 放进 mods 文件夹",
        "你可以：在「版本设置 → 版本概览」点「补全文件」修复损坏或缺失文件的版本，游戏没了音效也可以试试它",
        "你可以：在「版本设置 → 版本概览」修改版本名与版本描述，给整合包写点备注",
        "你可以：在「下载」页安装原版、Forge、Fabric 或整合包，启动器会自动补齐依赖文件",
        "你可以：在「联机」页创建房间，把邀请码和验证码一起发给朋友，对方就能加入你的局域网游戏",
        "你可以：在「设置 → 个性化」切换浅色 / 深色 / 跟随系统与自定义背景图片",
        "你可以：在「设置 → 联机设置」填自定义节点，公共节点失效时照样能连",
        "你可以：把官方 EasyTier 压缩包（EasyTier.zip）放在启动器同级目录；没有的话首次联机时会自动从官方 GitHub 下载并校验 SHA-256",
        "改坏了设置？删掉 PCL Linking 文件夹就是全新安装"
    }
    Public Function GetRandomHint() As String
        Return Hints(New System.Random().Next(0, Hints.Length))
    End Function

    Public Function GetRandomCave() As String
        Return GetRandomHint()
    End Function

    ''' <summary>
    ''' 获取当前设置的内存值，单位为 GB。
    ''' </summary>
    Public Function GetRam(Instance As McInstance, UseVersionJavaSetup As Boolean) As Double
        Dim RamGive As Double
        If Settings.Get(Of Integer)("LaunchRamType") = 0 Then
            '自动配置
            Dim RamAvailable As Double = Math.Round(My.Computer.Info.AvailablePhysicalMemory / 1024 / 1024 / 1024 * 10) / 10
            '确定需求的内存值
            Dim RamMininum As Double '无论如何也需要保证的最低限度内存
            Dim RamTarget1 As Double '估计能勉强带动了的内存
            Dim RamTarget2 As Double '估计没啥问题了的内存
            Dim RamTarget3 As Double '放一百万个材质和 Mod 和光影需要的内存
            If Instance IsNot Nothing AndAlso Not Instance.IsLoaded Then Instance.Load()
            If Instance IsNot Nothing AndAlso Instance.Modable Then
                '可安装 Mod 的版本
                Dim ModDir = DirectoryUtils.GetInfo(Instance.PathIndie & "mods\")
                Dim ModCount As Integer = If(ModDir.Exists, ModDir.GetFiles.Count(Function(f) {".jar", ".zip", ".litemod"}.Contains(f.Extension.Lower)), 0)
                RamMininum = 0.5 + ModCount / 150
                RamTarget1 = 1.5 + ModCount / 90
                RamTarget2 = 2.7 + ModCount / 50
                RamTarget3 = 4.5 + ModCount / 25
            ElseIf Instance IsNot Nothing AndAlso Instance.Version.HasOptiFine Then
                'OptiFine 版本
                RamMininum = 0.5
                RamTarget1 = 1.5
                RamTarget2 = 3
                RamTarget3 = 5
            Else
                '普通版本
                RamMininum = 0.5
                RamTarget1 = 1.5
                RamTarget2 = 2.5
                RamTarget3 = 4
            End If
            Dim RamDelta As Double
            '预分配内存，阶段一，0 ~ T1，100%
            RamDelta = RamTarget1
            RamGive += Math.Min(RamAvailable, RamDelta)
            RamAvailable -= RamDelta
            If RamAvailable < 0.1 Then GoTo PreFin
            '预分配内存，阶段二，T1 ~ T2，70%
            RamDelta = RamTarget2 - RamTarget1
            RamGive += Math.Min(RamAvailable * 0.7, RamDelta)
            RamAvailable -= RamDelta / 0.7
            If RamAvailable < 0.1 Then GoTo PreFin
            '预分配内存，阶段三，T2 ~ T3，40%
            RamDelta = RamTarget3 - RamTarget2
            RamGive += Math.Min(RamAvailable * 0.4, RamDelta)
            RamAvailable -= RamDelta / 0.4
            If RamAvailable < 0.1 Then GoTo PreFin
            '预分配内存，阶段四，T3 ~ T3 * 2，15%
            RamDelta = RamTarget3
            RamGive += Math.Min(RamAvailable * 0.15, RamDelta)
            RamAvailable -= RamDelta / 0.15
            If RamAvailable < 0.1 Then GoTo PreFin
PreFin:
            '不低于最低值
            RamGive = Math.Round(Math.Max(RamGive, RamMininum), 1)
        Else
            '手动配置
            Dim Value = Settings.Get(Of Integer)("LaunchRamCustom")
            If Value <= 12 Then
                RamGive = Value * 0.1 + 0.3
            ElseIf Value <= 25 Then
                RamGive = (Value - 12) * 0.5 + 1.5
            ElseIf Value <= 33 Then
                RamGive = (Value - 25) * 1 + 8
            Else
                RamGive = (Value - 33) * 2 + 16
            End If
        End If
        Return RamGive
    End Function

End Module
