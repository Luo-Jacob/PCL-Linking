# 第三方开源组件与双协议许可声明（Third-Party & Dual-License Notices）

本仓库为基于开源启动器 **PCL 2.14.0.0** 二次开发的第三方修改版本，命名为 **PCL Linking（PCL Linking 修改版 / `PCLLK`）**（仓库地址：<https://github.com/Luo-Jacob/PCL-Linking>）。

---

## 1. 上游核心项目与限制性许可（`LICENCE`）

### Plain Craft Launcher 2 (PCL 2.14.0.0 开源版)
- **原作者**：龙腾猫跃 (LTCat)
- **上游仓库**：<https://github.com/Meloong-Git/PCL>
- **作者赞助链接**：<https://meloong.com/afd/a/LTCat>
- **许可协议**：遵循仓库根目录 [LICENCE](./LICENCE) 中的《PCL 分发有限许可》与《PCL 存储库合理使用指南》。
  - 除下方第 2 节明确列出的独立核心文件外，本仓库中的 PCL 核心逻辑、UI 界面控件及与 PCL 私有类库耦合的功能页面（包含衍生修改后的界面/启动器集成代码）整体分发时仍须严格遵守 [LICENCE](./LICENCE) 中关于署名、名称、主色调及赞助劝导的相关条款。

### MeloongCore
- **来源**：<https://github.com/Meloong-Git/MeloongCore>
- **许可协议**：Apache License 2.0（完整文本见 [MeloongCore/LICENSE.txt](./MeloongCore/LICENSE.txt)）

---

## 2. 可独立复用的原创底层核心（MIT License）

以下完全原创、且**不依赖原版 PCL 私有类库或 UI 控件**的独立底层核心模块与构建脚本已抽离至 [`Plain Craft Launcher 2/MitCore/`](./Plain%20Craft%20Launcher%202/MitCore/) 目录（及根目录 [`build.cmd`](./build.cmd)），均带有 `SPDX-License-Identifier: MIT` 文件头，遵循仓库根目录 [LICENSE-MIT](./LICENSE-MIT) 单独开源，可脱离本启动器直接复用：

| 文件 | 独立核心能力 | 许可协议 |
| :--- | :--- | :--- |
| `Plain Craft Launcher 2/MitCore/EasyTierNetworkCore.vb` | EasyTier 按架构下载 URL / SHA-256 校验、节点 URI 安全校验与并发 TCP 测速、延迟与抖动统计、剪贴板邀请码提取、MC 局域网 4445 组播扫描 | MIT |
| `Plain Craft Launcher 2/MitCore/ModNbt.vb` | 纯 .NET NBT 二进制序列化解析与 Minecraft `servers.dat` 读写工具 | MIT |
| `Plain Craft Launcher 2/MitCore/FriendsCryptoProtocol.vb` | `lml-v2-` / `lml-grp-v2-` 域分离密钥派生、HMAC-SHA256 固定时间校验、AES-256 + SHA-256 加密中继文件上传与同源校验下载 | MIT |
| `Plain Craft Launcher 2/MitCore/SlpProbeEngine.vb` | Minecraft Server List Ping (SLP 1.7+) 纯 TCP 探测引擎（RTT 延迟、版本、人数、MOTD，支持 IPv4 / IPv6） | MIT |
| `Plain Craft Launcher 2/MitCore/ZipSafetyCore.vb` | 压缩包解压 ZipSlip 路径穿越防护与解压体积/条目数上限保护 | MIT |
| `Plain Craft Launcher 2/MitCore/InstanceDiffCore.vb` | 游戏实例 Mods / Config 文件夹递归 SHA-1 差异对比 (Diff) 与可疑崩溃日志特征诊断解析 | MIT |
| `Plain Craft Launcher 2/MitCore/SecurityAndWin32Core.vb` | Windows DPAPI 当前用户范围凭据保护封装、Win32 无边框全屏窗口状态切换 API 封装 | MIT |
| `build.cmd` | 自动化定位 MSBuild 并构建发布单文件产物的独立命令行构建脚本 | MIT |

---

## 3. 多人联机组件（按需从官方源下载）

本修改版不在 Git 源码与官方 GitHub Release 产物内直接捆绑 EasyTier 可执行文件，而是在用户首次使用联机功能时从 EasyTier 官方 GitHub Releases 下载并校验 SHA-256：

- **EasyTier**（`easytier-core.exe` / `easytier-cli.exe`）
  - 上游仓库：<https://github.com/EasyTier/EasyTier>
  - 许可协议：LGPL-3.0
- **WinDivert**（`WinDivert64.sys`，随 EasyTier Windows 官方发布包提供）
  - 上游仓库：<https://github.com/basil00/WinDivert>
  - 许可协议：LGPL-3.0 / GPL-2.0（双许可）
- **Wintun**（`wintun.dll`，随 EasyTier Windows 官方发布包提供）
  - 上游站点：<https://www.wintun.net/>
  - 许可协议：GPL-2.0（带 Prebuilt Binary Exemption 说明）

---

## 4. 内嵌 .NET 依赖与辅助 JAR / 代码

| 组件 | 用途说明 | 许可协议 | 项目 / 说明 |
| :--- | :--- | :--- | :--- |
| **Newtonsoft.Json** (`13.0.3`) | JSON 解析与序列化 | MIT | <https://github.com/JamesNK/Newtonsoft.Json> |
| **NAudio** (`1.7.3`) | 启动器背景音乐播放 | Ms-PL / MIT（1.7 时代为 Ms-PL） | <https://github.com/naudio/NAudio> |
| **Ookii.Dialogs.Wpf** (`5.0.1`) | 原生文件夹选择对话框 | BSD-3-Clause | <https://github.com/ookii-dialogs/ookii-dialogs-wpf> |
| **Imazen.WebP** (`10.0.1`) & **libwebp** | WebP 图片编解码 | MIT / Apache-2.0 & BSD-3-Clause | <https://github.com/imazen/libwebp-net> / WebM libwebp |
| **ThrottleDebounce** (`2.0.2`) | 输入防抖与节流控制 | Apache-2.0 | <https://github.com/Aldaviva/ThrottleDebounce> |
| **Microsoft.Win32.Registry** (`5.0.0`) | 系统深色模式/注册表读取 | MIT | .NET Runtime |
| **PolySharp** (`1.15.0`) | C# 编译期 Polyfill | MIT | <https://github.com/Sergio0694/PolySharp> |
| **JavaWrapper.jar** | Minecraft Java 启动包装 | 随 PCL / Meloong-Git | <https://github.com/00ll00/java_launch_wrapper> |
| **LwjglUnsafeAgent.jar** | 独立 `-javaagent` 进程加载 | GPL-3.0（作为独立外部进程参数运行） | <https://github.com/HMCL-dev/lwjgl-unsafe-agent> |
| **ClipboardInterceptor.vb** | WPF 剪贴板/输入控制辅助 | Apache-2.0（经 `uye` 授权用于 PCL） | 见源码文件头部许可声明 |
