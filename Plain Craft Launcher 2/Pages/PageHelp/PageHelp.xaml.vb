Public Class PageHelp
    Implements IRefreshable

    Private Class HelpTopic
        Public Property Category As String
        Public Property Title As String
        Public Property Badge As String
        Public Property Summary As String
        Public Property Steps As String()
        Public Property Tip As String
        Public Property ActionText As String
        Public Property ActionCallback As Action
        Public Property SecondaryActionText As String = ""
        Public Property SecondaryCallback As Action = Nothing
    End Class

    Private CurrentCategory As String = "ALL"
    Private IsInitializedUi As Boolean = False
    Private Topics As List(Of HelpTopic) = Nothing

    Private Sub PageHelp_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        If Not IsInitializedUi Then
            IsInitializedUi = True
            InitTopics()
            For Each Rb In PanHelpCategories.Children.OfType(Of MyRadioButton)()
                Rb.LabText.Margin = New Thickness(-2, 0, 8, 0)
            Next
        End If
        RenderHelpCards()
    End Sub

    Public Sub Refresh() Implements IRefreshable.Refresh
        SearchBox.Text = ""
        CurrentCategory = "ALL"
        BtnCatAll.Checked = True
        RenderHelpCards()
        Hint("已刷新帮助指南页面", Log:=False)
    End Sub

    Private Shared Sub OpenSelectedInstanceSubPage(SubPage As FormMain.PageSubType)
        If McInstanceSelected Is Nothing Then
            Hint("请先在「版本选择」中选中一个游戏实例！", HintType.Blue)
            FrmMain.PageChange(FormMain.PageType.InstanceSelect)
            Return
        End If
        If McInstanceLock.IsLocked(McInstanceSelected) Then
            Hint("当前实例正在被修改或运行中，请稍后再试！", HintType.Red)
            Return
        End If
        PageInstanceLeft.Instance = McInstanceSelected
        FrmMain.PageChange(FormMain.PageType.InstanceSetup, SubPage)
    End Sub

    Private Sub InitTopics()
        Topics = New List(Of HelpTopic) From {
            New HelpTopic With {
                .Category = "安装启动",
                .Title = "1. 下载游戏本体、Mod 加载器与整合包安装",
                .Badge = "原版 / 加载器 / 整合包",
                .Summary = "可以在「下载 → 原版游戏」里选定 Minecraft 版本，顺带把 Fabric、Forge、NeoForge、OptiFine 或 LiteLoader 一起装好，也可以直接拖入整合包压缩包安装。",
                .Steps = {
                    "① 打开顶部「下载 → 原版游戏」，选一个 MC 版本后在右侧点选需要的 Mod 加载器或 OptiFine，点「开始安装」就会自动补全依赖和库文件。",
                    "② 安装第三方整合包时，可以把 .zip 或 .mrpack 压缩包直接拖进启动器窗口，或者去「下载 → 整合包」在线搜索安装。",
                    "③ 如果想给已经装好的原版实例后补加载器，在左侧打开该实例的「版本设置 → 修改」就能追加或升级。"
                },
                .Tip = "下载速度慢或卡校验时，去「设置 → 全局设置」把下载源和线程数调一调通常会快很多。",
                .ActionText = "去安装游戏",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall),
                .SecondaryActionText = "查看下载设置",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch)
            },
            New HelpTopic With {
                .Category = "安装启动",
                .Title = "2. 账号管理、离线皮肤与外置登录",
                .Badge = "离线 / 外置登录",
                .Summary = "支持保存多个离线昵称快速切换，也支持配置基于 Authlib-Injector 的皮肤站外置登录。",
                .Steps = {
                    "① 离线登录：在启动页直接输入或下拉添加常用角色名。下方的皮肤设置可以选择默认模板（Steve / Alex / 随机），或者指定本地 64×64 自定义皮肤文件（CustomSkin.png）。",
                    "② 外置登录：在启动页切到外置登录填写皮肤站首页或 Yggdrasil API 地址，也可以把皮肤站提供的 Authlib-Injector 链接直接拖到启动器里。",
                    "③ 版本独立覆盖：如果某个整合包服务器需要单独的皮肤站，在对应实例的「版本设置 → 设置」里可以单独指定登录方式，不用反复改全局。"
                },
                .Tip = "配置多个离线账号后，还能在「工具 → 全局快捷键」里录一个快捷键用来轮换账号。",
                .ActionText = "回到启动页",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Launch),
                .SecondaryActionText = "设置自定义皮肤",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch)
            },
            New HelpTopic With {
                .Category = "安装启动",
                .Title = "3. 多游戏目录、版本隔离与桌面快捷方式",
                .Badge = "版本列表 / 快捷启动",
                .Summary = "支持管理多个 .minecraft 文件夹、按规则隔离不同版本的存档与 Mod，并可以为常用实例或指定单机世界生成桌面快捷方式。",
                .Steps = {
                    "① 打开启动页左下角「版本选择」，左侧可以切换、添加或扫描不同路径下的 .minecraft 文件夹，也能直接新建目录。",
                    "② 对着版本卡片进「版本设置 → 概览」，可以改名、改图标、加星标收藏，或者把它从列表隐藏（在版本列表页按 F11 能临时显示隐藏的版本）。",
                    "③ 在「版本总览 → 高级管理」可以点「创建桌面快捷方式」或「导出启动脚本」；在「世界存档」页选定具体存档，还能生成跳过主界面直接进对应世界的快捷方式。"
                },
                .Tip = "建议保持「隔离可安装 Mod 的版本」开启，这样不同整合包之间的 mods 和 config 不会混在一起。",
                .ActionText = "打开版本选择",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.InstanceSelect),
                .SecondaryActionText = "当前实例总览",
                .SecondaryCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceOverall)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "4. EasyTier 多人联机、节点测速与延迟监控",
                .Badge = "免管理员 / 折线监控",
                .Summary = "基于 EasyTier 用户态组网（--no-tun），不用安装虚拟网卡也不用管理员权限就能和朋友直连，房间内会显示实时延迟折线、抖动和 NAT 状态。",
                .Steps = {
                    "① 房主在游戏里点「对局域网开放」记下端口（或者直接点下方「扫描局域网存档」自动抓端口），进「联机 → 创建房间」设好联机密码后就能把邀请码发给朋友或直接群发。",
                    "② 好友进「联机 → 加入房间」（或者复制朋友消息后点「剪贴板一键进房」）连上房间，下方卡片会显示最近 30 秒的延迟走势和成员进出提醒。",
                    "③ 如果觉得当前节点有点卡，点等待页下面的「节点测速与择优」，测完会自动应用最快的前 3 个节点。",
                    "④ 加入房间后点「写入服务器列表」或「启动游戏并直连」，就能直接把房主地址写进 servers.dat 置顶或直接拉起游戏。"
                },
                .Tip = "房主和加入者如果手动改了自定义节点或刚测速换过节点，双方最好保持同一批节点，更容易直连成功。",
                .ActionText = "前往「联机」页面",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "5. 局域网自动扫描、剪贴板进房与固定房间码",
                .Badge = "组局提速",
                .Summary = "省掉来回切屏记端口号、手敲邀请码和验证码的麻烦，常一起玩的朋友还能开启固定房间码。",
                .Steps = {
                    "① 扫描建房：游戏内开启局域网或本地开服运行起来后，在联机等待页点「扫描局域网存档」，会自动捕捉本机 4445 广播和本地服务端端口直接建房。",
                    "② 剪贴板进房：复制朋友发来的整段邀请消息，回到「联机」页点「剪贴板一键进房」，或者打开加房弹窗整段粘贴进去，系统会自动拆出邀请码和验证码。",
                    "③ 固定房间码：在「设置 → 联机设置」打开「固定房主房间码」，以后每次用同一端口开房，生成的邀请码和密码都不会变，朋友在历史列表里标个星标就能长期直接点加入。"
                },
                .Tip = "如果要换一串新的固定邀请码或密码，在「设置 → 联机设置」点一下「重置固定码」就行。",
                .ActionText = "前往「联机」页面",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain),
                .SecondaryActionText = "打开「联机设置」",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLink)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "6. 语音 Mod 端口穿透、断线重连与连通性自检",
                .Badge = "额外端口 / 掉线重连",
                .Summary = "支持 Simple Voice Chat 等近距离语音或网页地图的额外端口一起走联机隧道，自带意外掉线 3 秒自动重连和一键连通性排查。",
                .Steps = {
                    "① 额外端口穿透：进入「设置 → 联机设置 → 高级设置」，点「填入语音端口 24454」（或自己写端口，多个用逗号隔开，如网页地图 8100）。房主和朋友两边填成一样的，开房时会自动附带穿透 TCP+UDP。",
                    "② 断线保护：默认勾选「联机意外断开时自动重连」，遇到节点短暂抽风断开后会在 3 秒后自动重连（最多 3 次）。",
                    "③ 连通性自检：进房间后万一游戏多人列表刷不出来，点网络监控卡片上的「连通性自检」，能顺着 NAT、虚网链路和本地映射端口测一遍，复制结果发给房主看很快就能定位问题。"
                },
                .Tip = "客户端收到局域网广播时名字前会带出房主昵称（例如「PCL Linking 联机 · 房主名」），在多人游戏最底部一眼就能认出来。",
                .ActionText = "打开「联机设置」",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLink),
                .SecondaryActionText = "前往「联机」页面",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "7. 好友添加、私聊握手校验与 Mod 一键补齐",
                .Badge = "端到端加密",
                .Summary = "单窗口即可加好友、建群或入群，支持剪贴板名片自动识别和静默握手校验；发送联机邀请或单独分享 Mod 时，对方可以一键启用本地禁用 Mod 或自动下载缺失项。",
                .Steps = {
                    "① 复制自己的好友名片发给朋友，点右上角「+ 添加」呼出窗口（已复制对方名片时会自动填好），添加后后台会自动发一次握手校验，双方互通后列表头像旁会打上「✓」。",
                    "② 单条消息最多支持 2000 字，Shift+Enter 换行、Enter 直接发送；切换会话会自动拉取最新消息，发送失败的消息旁会有红色「⚠ 重发」按钮可以直接重发。",
                    "③ 建好联机房间后点「发联机邀请」，或者随时点「同步 Mod」发给当前好友/群；收到方点卡片上的「一键补齐 / 启用差异 Mod」，会优先把本地 .disabled 重命名恢复，缺的再从 Modrinth/CurseForge 拉取补齐。"
                },
                .Tip = "点邀请卡片的「加入并直连游戏」时，如果两边 Mod 不一致也会先弹提示让你核对。",
                .ActionText = "前往「好友」页面",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Friends)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "8. 加密群聊、群名片邀请与文件 / 截图收发",
                .Badge = "多人群聊 / 10 MB 文件",
                .Summary = "不需要自己搭服务器，建好群后把群名片或拉好友卡片发出去就能组群聊天，还能端到端加密互传文件和剪贴板截图。",
                .Steps = {
                    "① 建群与拉人：点「好友 → + 添加」，切到「创建群聊」生成 G 开头的群号和 24 位密钥；进群后点上方「群名片」复制发送，或者点「拉好友」直接向现有好友发入群卡片。",
                    "② 发送文件与截图：点输入框左边的「📎 文件」、把文件直接拖进聊天框，或者在输入框按 Ctrl+V 粘贴截图/文件，都会先在本地加密并算好 SHA-256 再发出去（单文件最大 10 MB）。",
                    "③ 解密保存：收到文件卡片后点「解密并下载保存」，校验通过后就能另存到本地。注意公共中继的附件一般暂存约 3 小时，过期让对方重发一份即可。"
                },
                .Tip = "在左上角「我的名片」可以改自己的展示昵称，群聊里会直接显示这个名字；屏蔽某个好友后，他在群里的发言也会同步隐藏。",
                .ActionText = "前往「好友与群聊」",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Friends)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "9. 好友实例同步（启动自动检查 / 随时手动同步）",
                .Badge = "发布与订阅",
                .Summary = "适合固定一起玩的整合包小队：一方把本地实例设为发布源后，其他人订阅绑定到自己的本地实例，以后每次打开 PCL Linking 自动比对更新，也能随时手动触发。",
                .Steps = {
                    "① 发布端：选中对应好友或群聊，点聊天顶部的「实例同步 → 将当前选中实例设为发布源并立即推送修订版」，可勾选是否顺带打包加密 config（8 MB 以内）和启动时自动检测变化推送。",
                    "② 接收端：在聊天框看到同步邀请卡片后点「绑定当前实例并自动同步」，选好是否把本地多出来的 Mod 自动改名禁用（.disabled）。",
                    "③ 自动与手动对齐：之后每次启动启动器都会在后台检查新版本并自动同步有变动的实例（覆盖 config 前会自动备份最近 3 份旧配置）；也可以点「实例同步 → 管理全部同步绑定」随时手工点立即同步。"
                },
                .Tip = "如果是单人测试或游戏刚好正在运行/锁定中，同步会自动跳过，等关闭游戏再手动点一下同步就行。",
                .ActionText = "管理实例同步",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Friends)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "10. 历史联机收藏夹与群发邀请",
                .Badge = "常用房间",
                .Summary = "每次连过的房间都会记在「联机」页下方，常去的房间标星重命名之后，下次点一下就能重连。",
                .Steps = {
                    "① 加入过的房间和密码会自动存到「联机」页底部的历史列表里（最多保留最近 20 条普通记录，收藏的不限条数）。",
                    "② 常用房间可以点「收藏」置顶，再点「改名」标个好认的名字，以后不用再翻聊天记录找邀请码。",
                    "③ 房主建完房间后，在完成页右上方点「群发全员」（或者回好友页点「群发邀请」），勾选好要邀请的人和是否附带 Mod 清单就能一次全发出去了。"
                },
                .Tip = "搭配「设置 → 联机设置」里的「开启联机页自动加入上次房间」更省事。",
                .ActionText = "前往「联机」页面",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain),
                .SecondaryActionText = "前往「好友」页面",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Friends)
            },
            New HelpTopic With {
                .Category = "联机社交",
                .Title = "11. 游戏内轻量消息浮窗与无边框全屏切换",
                .Badge = "免切屏回复 / 无边框",
                .Summary = "玩游戏的时候不用 Alt+Tab 切出来看消息：有新私信、群消息或联机邀请时右上角会自动弹出半透明浮窗，也能直接回复和切换无边框全屏。",
                .Steps = {
                    "① 收到新消息时浮窗自动出现（8 秒不操作会自动收起），顶栏会顺便显示当前联机人数/延迟和游戏实时内存占用。",
                    "② 用顶部的 ‹ / › 可以在好友和群聊之间切换，在底部输入框按 Enter 就能直接发消息；遇到联机邀请卡片直接点按钮就能接。",
                    "③ 点右上角「无边框」按钮可以把运行中的 Minecraft 窗口铺满当前显示器并且去掉标题栏，再点一次恢复原窗口位置和大小。"
                },
                .Tip = "建议在「工具 → 全局快捷键」绑个顺手的键来随时呼出/收起浮窗或切无边框，配合游戏设置里的「窗口化」切屏完全不黑屏。",
                .ActionText = "呼出浮层看看",
                .ActionCallback = Sub() WinInGameOverlay.ToggleOverlay(),
                .SecondaryActionText = "去绑快捷键",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsHotkeys)
            },
            New HelpTopic With {
                .Category = "开服运维",
                .Title = "12. 本地一键开服并与联机房间直接对接",
                .Badge = "6 种核心",
                .Summary = "自带 Paper、Purpur、Vanilla、Fabric、NeoForge、Forge 一键建服和管理，不需要改路由器端口映射，开好服直接一键拉起 EasyTier 房间就能让朋友进。",
                .Steps = {
                    "① 打开「工具 → 服务器生成/管理」，目录留空的话点生成会自动建在同级的 PCL Linking\\Servers\\核心-版本\\ 下；该目录下已有的服务端也会自动扫描放进上方下拉列表。",
                    "② 配置好核心、MC 版本、Java，下面填好端口（默认 25565）、最大人数、MOTD、游戏模式、正版验证开关和分配内存。",
                    "③ 先点生成再点「启动服务端」，控制台跑起来之后点「一键共享到联机房间」，就会自动拿当前服务端端口建好房间。"
                },
                .Tip = "纯离线或朋友外置/离线混合联机记得把「开启正版验证 (online-mode)」保持在关闭状态。",
                .ActionText = "前往本地开服",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsServer)
            },
            New HelpTopic With {
                .Category = "开服运维",
                .Title = "13. 服务端守护重启、定时备份、装插件与 Mod 互转",
                .Badge = "运维管理",
                .Summary = "本地开服页同时带崩溃自动拉起、定时备份地图、OP/白名单图形化管理、在线插件下载，以及客户端和服务端 Mod/存档互相导入导出。",
                .Steps = {
                    "① 崩溃保护与备份：勾上「崩溃自动重启（上限 3 次）」后若异常退出会在 5 秒后自动重启并存一份 pcl-linking-crash 日志；勾「定时自动备份世界」会定期把存档压缩到 backups\\ 并保留最新 10 份。",
                    "② 控制台与玩家管理：上方有内存/PID 仪表盘和快捷指令栏（say/kick/ban），下方也能直接图形化添删白名单和 OP 列表。",
                    "③ 下插件/Mod：点「下载插件 / Mod」跳转资源页后，点版本旁边的安装按钮能直接下到当前服务端的 plugins\\ 或 mods\\。",
                    "④ 客户端与服务端互通：可以点「从当前实例同步 Mod 到服务端」，PCL Linking 会自动过滤掉光影、小地图、HUD 这类纯客户端模组，省得服务端启动报错；也能把单机存档一键导进服务端 world。"
                },
                .Tip = "互转 Mod 和导存档之前记得先把服务端安全 stop 停稳。",
                .ActionText = "前往本地开服",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsServer),
                .SecondaryActionText = "去下服务端插件",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadPlugin)
            },
            New HelpTopic With {
                .Category = "开服运维",
                .Title = "14. Minecraft 服务器状态探测与关注巡检",
                .Badge = "SLP 1.7+",
                .Summary = "不进游戏就能用原生 TCP 直连测任何服务器的在线人数、延迟 RTT、服务端版本、MOTD 和在线玩家样本，支持本地开服和当前 EasyTier 地址快速填入。",
                .Steps = {
                    "① 进入「工具 → 服务器状态探测」，手动填 IP:端口，或者直接点快捷按钮填入本地开服地址 / 当前已加入的 EasyTier 虚网联机地址。",
                    "② 点「立即探测」看延迟和玩家数；常用服务器可以「加入关注列表」，以后进页面点一下「全部刷新」就能挨个巡检一遍。",
                    "③ 测通之后可以直接点「写入当前实例服务器列表 (servers.dat)」或者「用当前实例直连启动」进服。"
                },
                .Tip = "探测朋友开的 EasyTier 联机地址前，需先在「联机」页加进他的房间。",
                .ActionText = "打开服务器探测",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsServerPing),
                .SecondaryActionText = "前往本地开服",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsServer)
            },
            New HelpTopic With {
                .Category = "Mod排雷",
                .Title = "15. 下载 Mod / 整合包 / 光影与「常用模组清单」",
                .Badge = "双平台搜索",
                .Summary = "整合了 Modrinth 与 CurseForge 资源搜索、版本筛选与本地服务端安装，并可以把整套装机必备 Mod（如优化、汉化、小地图等）存成「常用模组清单」随时复用。",
                .Steps = {
                    "① 在顶部「下载」页按类别（Mod、整合包、数据包、资源包、光影、插件）检索资源，点具体版本可直接装进当前选中的游戏实例。",
                    "② 进入「下载 → 常用模组清单」可以新建或导入清单分享码，之后选定任意目标实例，点一键部署会自动筛选适配的加载器/MC 版本并把缺失的前置一并下载。"
                },
                .Tip = "不想一个个加清单的话，去具体实例的「Mod 管理」点「提取为常用清单」，直接把现有 Mod 导出一套就行了。",
                .ActionText = "管理常用模组清单",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadPresets),
                .SecondaryActionText = "去搜 Mod",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadMod)
            },
            New HelpTopic With {
                .Category = "Mod排雷",
                .Title = "16. 实例 Mod 管理、启动前冲突体检与跨版本预检",
                .Badge = "防崩检查",
                .Summary = "管理已启用/已禁用的 Mod、一键升级新版，并在启动游戏前静态扫描 .jar 里的元信息，把常见冲突、错放加载器和缺前置提前拦下来。",
                .Steps = {
                    "① 在「版本设置 → Mod 管理」默认勾着「启动前自动体检冲突」，平时直接点启动就会顺手查一遍；也可以随时点「冲突与依赖体检」主动全量排查。",
                    "② 体检能抓出重复 Mod ID、Fabric/Forge 放错加载器、纯服务端 Mod、显式声明冲突、缺前置库和 MC 版本不匹配等问题，弹窗里可以直接点一键禁用把冲突 Mod 改成 .disabled。",
                    "③ 想把旧整合包往高版本升级时，可以先点「跨版本兼容预检」，提前看清楚目标 MC 版本下有哪些 Mod 已经出新版、哪些还没跟进。"
                },
                .Tip = "如果用了 Sinytra Connector 之类允许跨加载器的转接模组，可以临时关掉启动前自动体检或忽略相应误报。",
                .ActionText = "打开当前实例 Mod 管理",
                .ActionCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceMod)
            },
            New HelpTopic With {
                .Category = "Mod排雷",
                .Title = "17. 实时日志监控窗口与报错一键诊断",
                .Badge = "分级过滤 / 正则",
                .Summary = "游戏运行时可以实时查看客户端输出流，按日志等级高亮并支持关键字和正则过滤，遇到闪退直接点一下就能做规则分析。",
                .Steps = {
                    "① 点标题栏右侧扩展日志按钮，或者在「版本总览 → 打开实时日志」调出独立窗口（在全局设置里也可以勾选每次启动自动打开）。",
                    "② 支持切换多个正在运行的游戏进程，用上方的级别下拉（全部 / WARN / ERROR / Mixin 与 Mod 报错）搭配「正则」复选框快速定位报错行。",
                    "③ 出问题时点工具栏「一键智能诊断报错」，会结合当前日志和最新 crash-reports 给出常见原因和排查方向；右键或 Ctrl+C 可以复制或导出整份 .log。"
                },
                .Tip = "把别人发给你的 crash-report .txt 直接拖进 PCL Linking 主窗口也能自动做一遍崩溃分析。",
                .ActionText = "打开实时日志",
                .ActionCallback = Sub() WinGameLog.ShowOrActivate()
            },
            New HelpTopic With {
                .Category = "实例定制",
                .Title = "18. 修改实例加载器与导出整合包",
                .Badge = "加装加载器 / 打包",
                .Summary = "不用删掉重下：已经创建好的实例可以单独升级、卸载或补装加载器，改好后也能打包导出成标准整合包或 .mrpack。",
                .Steps = {
                    "① 加载器修改：进「版本设置 → 修改」，可以给已有实例安装、切换或卸载 Forge、NeoForge、Fabric 和 OptiFine。",
                    "② 导出整合包：进「版本设置 → 导出」，选好要打进压缩包的目录和配置文件，支持附带启动器程序；如果不带启动器程序还能选择导出为 .mrpack 格式。"
                },
                .Tip = "如果整合包体积比较大，通常不要勾选直接打包所有外部下载不到的大资源，除非网络环境很难连接下载源。",
                .ActionText = "打开实例「修改」",
                .ActionCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceModify),
                .SecondaryActionText = "打开实例「导出」",
                .SecondaryCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceExport)
            },
            New HelpTopic With {
                .Category = "实例定制",
                .Title = "19. 实例对比 (Diff)、一键瘦身与资源更新",
                .Badge = "排异 / 清理",
                .Summary = "两台电脑或两个版本之间不知差了哪些 Mod/配置时用 Diff 一眼比完；同时提供清理旧日志缓存和材质包/光影包检查更新。",
                .Steps = {
                    "① 实例对比 (Diff)：进「版本总览 → 高级管理 → 实例对比 (Diff)」，挑另一个对比实例后就会列出双方独有 Mod 和 config 差异，可以直接勾选补齐缺掉的 Mod。",
                    "② 一键瘦身清理：同样在高级管理里，点「一键瘦身清理」能扫描旧 .log.gz 归档、崩溃报告、.mixin.out 和残留 .tmp / .lmltmp 文件批量腾空间。",
                    "③ 资源与光影：进「版本设置 → 资源与光影」可以直接检查当前实例里装的材质包和光影包有没有新版本，有的话点一下即可更新。"
                },
                .Tip = "朋友联机缺单个 Mod 或版本对不上号的时候，拿 Diff 工具比对一遍特别直观。",
                .ActionText = "打开「版本总览」",
                .ActionCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceOverall),
                .SecondaryActionText = "查看资源与光影",
                .SecondaryCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceAssets)
            },
            New HelpTopic With {
                .Category = "实例定制",
                .Title = "20. 世界存档管理、地区文件清理与退出自动存档",
                .Badge = "存档清理 / 自动备份",
                .Summary = "免打开游戏就能查各个单机世界的游戏模式、种子与游玩时长、直进存档、清除玩家未停留过的废区块，还能在退出游戏时自动备份改动过的 saves。",
                .Steps = {
                    "① 在「版本设置 → 世界存档」选一个存档，点「直连该世界启动」会用 QuickPlay 直接读进这个存档（1.20+ 原生支持）；也能在这里做桌面直达快捷方式或安全删除存档。",
                    "② 存档太膨胀时可以用下方的区块清理工具，按停留时间（例如仅清理 <= 0 秒从未久留过的区块）剪裁 region/*.mca，默认会先自动备份原 .mca 到实例的 PCL Linking/RegionBackups。",
                    "③ 在「实例设置」或「实例快照」页勾上「退出游戏时若存档有变动自动备份 saves」，以后正常退出 Minecraft 只要检测到存档有新修改就会后台补一份轻快照。"
                },
                .Tip = "在启动页玩一圈主界面但没进存档直接退出，系统不会做多余的空备份。",
                .ActionText = "打开「世界存档」",
                .ActionCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceSaves),
                .SecondaryActionText = "打开「实例设置」",
                .SecondaryCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceSetup)
            },
            New HelpTopic With {
                .Category = "实例定制",
                .Title = "21. 实例增量快照与崩溃一键回滚",
                .Badge = "快照还原",
                .Summary = "折腾大改整合包配置、加删重型 Mod 之前随手打个快照（可选 mods / config / saves / options.txt），搞炸了哪怕崩溃弹框里都能直接点回滚。",
                .Steps = {
                    "① 进入「版本设置 → 实例快照」，勾选想保护的目录点创建快照（支持自写备注）；页内也能勾上启动游戏前自动备份。",
                    "② 真的把存档或配置调乱了，点对应快照上的「恢复」就能还原；就算进游戏直接崩溃，崩溃诊断弹窗里也会给出回滚最近快照的选项。"
                },
                .Tip = "恢复快照会先在后台校验并完整解压到临时区再替换目标文件，不怕压缩包损坏导致两头落空。",
                .ActionText = "打开「实例快照」",
                .ActionCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceSnapshot)
            },
            New HelpTopic With {
                .Category = "实例定制",
                .Title = "22. 实例游玩时长统计与场景模式一键切换",
                .Badge = "时长排行 / 场景预设",
                .Summary = "自动记下每个实例的总游玩时长与启动次数，且能在「单人画质向」和「多人高帧联机向」之间一键切好 JVM、视距、帧率和光影 Mod 开关。",
                .Steps = {
                    "① 时长统计：每次启动退出游戏后都会把有效时长保存到 PCL Linking\\Playtime.json，版本总览顶部随时查看，点右侧「全部实例统计排行」可以看所有实例榜单。",
                    "② 场景模式切换：打开「版本设置 → 设置」最上方的场景切换栏，「单人画质向」会启用高画质视距、调优 G1GC 并尝试开启 Iris/Oculus 光影模组；「多人高帧联机向」会调低视距并把纯客户端光影 Mod 临时改成 .disabled。",
                    "③ 自定义方案：如果自己调好了一套趁手的配置，点「将当前配置存为自定义预设」存下来就能一直重复套用。"
                },
                .Tip = "内置场景不会改动配方或玩法类的核心 Mod，切换完成下方各项设置会自动刷新。",
                .ActionText = "打开「实例设置」",
                .ActionCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceSetup),
                .SecondaryActionText = "查看游玩统计",
                .SecondaryCallback = Sub() OpenSelectedInstanceSubPage(FormMain.PageSubType.InstanceOverall)
            },
            New HelpTopic With {
                .Category = "实例定制",
                .Title = "23. 3D 皮肤预览器与多资源包无冲突合并",
                .Badge = "实用工具",
                .Summary = "工具页内置支持滚轮缩放旋转的 3D 皮肤/披风预览器，以及可以按顺序合并多个材质包并排查文件覆盖冲突的合并工具。",
                .Steps = {
                    "① 3D 皮肤预览器：在「工具 → 皮肤 3D 预览」直接读取本地 PNG、当前账号皮肤或按正版玩家名在线拉取，支持切细臂 (Alex) 模型、看披风/鞘翅和导出 PNG。",
                    "② 多资源包合并：在「工具 → 资源包合并」往里加多个 .zip 或文件夹，排好上下叠加顺序；如果有同名贴图/模型冲突，列表里可直接点选保留哪一份，最后一键合成单个新资源包。"
                },
                .Tip = "合并好后可直接放进游戏实例的 resourcepacks 文件夹生效。",
                .ActionText = "打开「皮肤 3D 预览」",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsSkin3D),
                .SecondaryActionText = "打开「资源包合并」",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsResourceMerge)
            },
            New HelpTopic With {
                .Category = "常见排查",
                .Title = "24. 全局设置（Java / 内存 / 个性化）与数据共存",
                .Badge = "便携目录 / 外观",
                .Summary = "支持自动查找与下载对应版本 Java、动态内存分配，以及浅色/深色主题、背景图、音乐、自定义主页与本地便携存储。",
                .Steps = {
                    "① Java 与内存：在「设置 → 全局设置」默认自动按游戏版本匹配本机或自动下载合适 Java；自己装了特定 JDK 的可以在 Java 列表里导入或让系统搜索。",
                    "② 外观与主页：在「设置 → 个性化」可切换浅色 / 深色 / 跟随 Windows 系统深色模式、窗口透明度、背景图片/背景音乐文件夹以及预设或本地 Custom.xaml 主页。",
                    "③ 配置完全便携：所有设置、账号、好友与游玩统计都放在启动器同级的 PCL Linking\\ 文件夹里（敏感凭据有 Windows DPAPI 本地加密保护），跟原版 PCL 放一块互不干扰。"
                },
                .Tip = "万一哪天把界面或设置改坏了，闭着眼删掉同级的 PCL Linking 文件夹再打开就是全新默认状态。",
                .ActionText = "前往「全局设置」",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupLaunch),
                .SecondaryActionText = "前往「个性化」",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupUI)
            },
            New HelpTopic With {
                .Category = "常见排查",
                .Title = "25. 全局快捷键录制与常见问题速查",
                .Badge = "17 种动作 / 排错",
                .Summary = "整理了 17 个全局快捷键绑定动作，以及遇到联机慢、黑屏进不去游戏、加载报错时的常见处理顺序。",
                .Steps = {
                    "① 全局快捷键：打开「工具 → 全局快捷键」，可给「快速启动选中实例」「打开最新日志」「打开游戏目录」「轮换离线账号」「显示/最小化启动器」「切换游戏内消息浮窗」「切换运行中游戏无边框」「直跳各大功能页」共 17 个动作录制组合键（默认全空不抢热键）。",
                    "② 联机老走中继或高延迟：房主和好友都点一遍「联机 → 节点测速与择优」挑同样的低延迟前 3 节点，顺手让 Windows 防火墙放行 PCLLK.exe。",
                    "③ 启动秒退或报错：先点 Mod 管理「冲突与依赖体检」清掉错放/缺前置项；再看实时日志「一键智能诊断」提示；改动前有打快照的话可以直接回滚。"
                },
                .Tip = "如果需要按窗口聚焦限制热键，可以在「工具 → 全局快捷键」勾上「仅在 PCL Linking 窗口聚焦时响应」。",
                .ActionText = "配置「全局快捷键」",
                .ActionCallback = Sub() FrmMain.PageChange(FormMain.PageType.Tools, FormMain.PageSubType.ToolsHotkeys),
                .SecondaryActionText = "打开「关于」查看说明",
                .SecondaryCallback = Sub() FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupAbout)
            }
        }
    End Sub

    Private Sub Category_Changed(sender As MyRadioButton, raiseByMouse As Boolean) Handles BtnCatAll.Check, BtnCatLaunch.Check, BtnCatMulti.Check, BtnCatServer.Check, BtnCatModDiag.Check, BtnCatInstance.Check, BtnCatFaq.Check
        If Not IsInitializedUi Then Return
        CurrentCategory = If(sender.Tag?.ToString(), "ALL")
        RenderHelpCards()
    End Sub

    Private Sub SearchBox_TextChanged() Handles SearchBox.TextChanged
        If Not IsInitializedUi Then Return
        RenderHelpCards()
    End Sub

    Private Sub RenderHelpCards()
        If Topics Is Nothing OrElse PanHelpCards Is Nothing Then Return
        Dim Kw = If(SearchBox.Text, "").Trim()
        Dim Matched = Topics.Where(
        Function(t)
            If CurrentCategory <> "ALL" AndAlso Not String.Equals(t.Category, CurrentCategory, StringComparison.OrdinalIgnoreCase) Then
                Return False
            End If
            If Kw.Length = 0 Then Return True
            Return t.Title.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   t.Summary.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   t.Tip.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   t.Badge.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   t.Steps.Any(Function(s) s.IndexOf(Kw, StringComparison.OrdinalIgnoreCase) >= 0)
        End Function).ToList()

        PanHelpCards.Children.Clear()
        LabHelpCount.Text = If(Kw = "" AndAlso CurrentCategory = "ALL", $"共 {Matched.Count} 项功能指南", $"匹配显示 {Matched.Count} 项功能指南")
        CardHelpEmpty.Visibility = If(Matched.Any(), Visibility.Collapsed, Visibility.Visible)

        For Each Item In Matched
            PanHelpCards.Children.Add(BuildHelpCard(Item))
        Next
    End Sub

    Private Function BuildHelpCard(Topic As HelpTopic) As MyCard
        Dim Card As New MyCard With {
            .Title = Topic.Title,
            .Margin = New Thickness(0, 0, 0, 14),
            .CornerRadius = New CornerRadius(6)
        }
        Dim Body As New StackPanel With {.Margin = New Thickness(25, 38, 25, 16)}

        '顶部简介与标签行
        Dim TagRow As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 0, 0, 8)}
        Dim TagBorder As New Border With {
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(7, 2, 7, 2),
            .Margin = New Thickness(0, 0, 8, 0),
            .VerticalAlignment = VerticalAlignment.Center
        }
        TagBorder.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
        TagBorder.Child = New TextBlock With {
            .Text = Topic.Category,
            .Foreground = Brushes.White,
            .FontSize = 11,
            .FontWeight = FontWeights.Bold
        }
        TagRow.Children.Add(TagBorder)

        Dim BadgeBorder As New Border With {
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(7, 2, 7, 2),
            .VerticalAlignment = VerticalAlignment.Center
        }
        BadgeBorder.SetResourceReference(Border.BackgroundProperty, "ColorBrush7")
        Dim BadgeLab As New TextBlock With {
            .Text = Topic.Badge,
            .FontSize = 11,
            .FontWeight = FontWeights.SemiBold
        }
        BadgeLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2")
        BadgeBorder.Child = BadgeLab
        TagRow.Children.Add(BadgeBorder)
        Body.Children.Add(TagRow)

        Dim SumTb As New TextBlock With {
            .Text = Topic.Summary,
            .TextWrapping = TextWrapping.Wrap,
            .FontSize = 13,
            .LineHeight = 19,
            .Margin = New Thickness(0, 0, 0, 10)
        }
        SumTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Body.Children.Add(SumTb)

        '操作步骤区
        Dim StepsBox As New Border With {
            .CornerRadius = New CornerRadius(6),
            .Padding = New Thickness(13, 10, 13, 10),
            .Margin = New Thickness(0, 0, 0, 10)
        }
        StepsBox.SetResourceReference(Border.BackgroundProperty, "ColorBrushSurface")
        Dim StepsStack As New StackPanel()
        For i As Integer = 0 To Topic.Steps.Length - 1
            Dim StepTb As New TextBlock With {
                .Text = Topic.Steps(i),
                .TextWrapping = TextWrapping.Wrap,
                .FontSize = 12.5,
                .LineHeight = 19,
                .Margin = If(i < Topic.Steps.Length - 1, New Thickness(0, 0, 0, 5), New Thickness(0))
            }
            StepTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray1")
            StepsStack.Children.Add(StepTb)
        Next
        StepsBox.Child = StepsStack
        Body.Children.Add(StepsBox)

        '底栏：小贴士 + 一键跳转按钮
        Dim FooterGrid As New Grid()
        FooterGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        FooterGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim TipTb As New TextBlock With {
            .Text = "提示：" & Topic.Tip,
            .TextWrapping = TextWrapping.Wrap,
            .FontSize = 12,
            .VerticalAlignment = VerticalAlignment.Center,
            .Margin = New Thickness(0, 0, 14, 0)
        }
        TipTb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Grid.SetColumn(TipTb, 0)
        FooterGrid.Children.Add(TipTb)

        Dim BtnPanel As New StackPanel With {.Orientation = Orientation.Horizontal, .VerticalAlignment = VerticalAlignment.Center}
        If Topic.ActionCallback IsNot Nothing Then
            Dim CapturedMain = Topic.ActionCallback
            Dim BtnMain As New MyButton With {
                .Text = Topic.ActionText,
                .Height = 28,
                .Padding = New Thickness(12, 0, 12, 0),
                .ColorType = MyButton.ColorState.Highlight
            }
            AddHandler BtnMain.Click, Sub() CapturedMain()
            BtnPanel.Children.Add(BtnMain)
        End If
        If Topic.SecondaryCallback IsNot Nothing AndAlso Not String.IsNullOrEmpty(Topic.SecondaryActionText) Then
            Dim CapturedSec = Topic.SecondaryCallback
            Dim BtnSec As New MyButton With {
                .Text = Topic.SecondaryActionText,
                .Height = 28,
                .Padding = New Thickness(12, 0, 12, 0),
                .Margin = New Thickness(8, 0, 0, 0)
            }
            AddHandler BtnSec.Click, Sub() CapturedSec()
            BtnPanel.Children.Add(BtnSec)
        End If
        Grid.SetColumn(BtnPanel, 1)
        FooterGrid.Children.Add(BtnPanel)

        Body.Children.Add(FooterGrid)
        Card.Children.Add(Body)
        Return Card
    End Function

End Class
