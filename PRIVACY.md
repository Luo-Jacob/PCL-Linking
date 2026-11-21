# 隐私政策与本地数据处理说明（PCL Linking 修改版）

最后更新日期：2026-11-21（对应版本 `v 1.0.1`）

**PCL Linking**（**Plain Craft Launcher - PCL Linking 修改版**，开源仓库：<https://github.com/Luo-Jacob/PCL-Linking>）为本地优先（Local-First）的桌面应用程序，**不包含任何内置遥测（Telemetry）、统计上报或广告 SDK**。仅在您主动使用对应联网功能时，才会与相关第三方或开源公共服务通信。

---

## 1. 本地存储与凭据保护

所有配置、缓存与聊天记录均保存在程序同级 `PCL Linking\` 便携目录中（或游戏实例目录下的 `PCL Linking\` 子目录），不会向 Windows 系统无关目录上传您的配置：

- **常规配置与游玩统计**：`PCL Linking\Setup.ini`、`PCL Linking\Global.ini`、`PCL Linking\Hotkeys.json`、`PCL Linking\Playtime.json`、`PCL Linking\SceneProfiles.json` 等文件以本地文本 / JSON 形式保存。
- **敏感设置保护（Windows DPAPI）**：`ToolCurseForgeApiKey`、`LoginAuthPass`、`CacheAuthAccess`、联机房间粘性密钥等敏感项优先通过 Windows 数据保护 API（`ProtectedData.Protect`，`DataProtectionScope.CurrentUser`）绑定当前 Windows 用户加密存储（前缀 `dpapi:`）。
  - > 注意：由于配置默认存于程序同级 `PCL Linking\` 目录，若您将整个 `PCL Linking\` 目录分享给他人，仍请先清理个人设置、日志与聊天记录文件。
- **诊断日志**：运行日志（`PCL Linking\Log1.txt` ~ `Log5.txt`）仅保存在本地用于排查问题，程序不会自动上传。提交 GitHub Issue 前请自行检查并遮盖个人用户名或敏感地址。

---

## 2. 好友聊天、加密文件与实例同步（仅在用户主动启用后）

- **端到端加密消息与群聊**：
  - 私聊与群聊仅存储于本地 `PCL Linking\friends.json`，默认每 30 秒通过公开或用户自定义的中继服务器（默认 `https://ntfy.sh`）轮询密文消息。
  - 消息正文使用 **AES-256-CBC + HMAC-SHA256** 进行客户端侧加密与完整性校验（采用域分离推导密钥与主题 ID `lml-v2-` / `lml-grp-v2-`），中继服务器无法读取明文。
  - **可被中继服务器观测的元数据**：您的出口 IP 地址、访问时间戳以及派生后的不可逆主题 ID。
- **加密文件与截图附件（<= 10 MB）**：
  - 仅在您点击发送、拖入聊天框或粘贴图片并确认后，文件才会在本地加密再上传至所配置的 `ntfy` 中继服务器。
  - 中继默认短暂缓存（通常约 3 小时）加密附件，接收端下载时限定同一中继源并强制校对 SHA-256 摘要，不会自动访问第三方外部链接。
- **好友实例同步（`PCL Linking\InstanceSync.json`）**：
  - 推送端仅在您手动配置发布源并确认后发送 Mod 列表摘要与可选加密 `config/`（上限 8 MB）。
  - 订阅端仅按本地绑定配置下载来自同源中继的加密配置并从 Modrinth 拉取公开 Mod 元数据与文件；覆盖本地 `config/` 前会自动在实例 `PCL Linking\ConfigSyncBackups\` 下滚动保留最近 3 份备份。

---

## 3. 多人联机（EasyTier P2P / 中继组网）

- **联机组件按需下载**：若程序同级未放置 `EasyTier.zip`，仅当您首次点击开启/加入联机房间时，程序才会从 **EasyTier 官方 GitHub Releases**（`https://github.com/EasyTier/EasyTier/releases`）下载压缩包并强制校验 SHA-256 哈希。
- **公共节点池查询**：启用「社区节点池」后，联机时会请求 `https://uptime.easytier.cn/api/nodes` 获取活跃公开中继节点列表，并向选中节点建立组网连接。
- **可被联机对端与中继看到的网络信息**：您的公网/中继观测 IP 地址、组网虚拟 IP（例如 `10.114.114.x`）、玩家在联机面板展示的游戏昵称以及房间开放端口。

---

## 4. Minecraft 启动与资源下载服务

- **离线登录与第三方外置登录（Authlib-Injector）**：仅与您配置的认证服务器或本地启动流程交互。本修改版已移除官方微软 OAuth 客户端实现；官方微软正版账号日常使用建议直接使用官方 [PCL](https://github.com/Meloong-Git/PCL)。
- **资源 / 服务端镜像源与探测器**：根据您触发的功能，可能请求 Mojang / BMCLAPI、Modrinth API、CurseForge API（仅向 CurseForge 官方域名发送您自行填写的 `x-api-key`）、Paper / Purpur / Fabric / NeoForge 官方接口，或在您触发服务器状态探测时向目标主机发起 TCP / SLP 握手。

---

## 5. 数据控制与删除（注销清理）

本程序无中心化云端账号体系。若您需要彻底删除所有本地个人配置、好友密文、实例同步记录与日志：
1. 关闭启动器与运行中的本地服务端；
2. 直接删除程序同级 `PCL Linking\` 目录及各版本目录下的 `PCL Linking\`、`ConfigSyncBackups\` 子文件夹；
3. 此前发布到公共 `ntfy` 中继的加密消息会在中继缓存到期后自动清理，建议同时通知好友/群员更换聊天密钥。
