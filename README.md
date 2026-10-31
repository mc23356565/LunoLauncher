# LunoLauncher

[![build](https://github.com/mc23356565/LunoLauncher/actions/workflows/build.yml/badge.svg)](https://github.com/mc23356565/LunoLauncher/actions/workflows/build.yml)

A free, open-source launcher for **Minecraft: Java Edition** on Windows 10/11.
Independent fork of OpenLauncher (GPL-3.0).

- **Website & download:** https://mc23356565.github.io/LunoLauncher/
- **Releases (exe + SHA-256):** https://github.com/mc23356565/LunoLauncher/releases
- **Privacy policy:** https://mc23356565.github.io/LunoLauncher/privacy.html
- **Terms of use:** https://mc23356565.github.io/LunoLauncher/terms.html
- **Contact:** DreemurrDawnlit@outlook.com
- **License:** GPL-3.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE)

## What it is

A desktop launcher that downloads and starts Minecraft: Java Edition, manages multiple
instances, installs mod loaders (Forge, NeoForge, Fabric, Quilt, OptiFine), searches and
installs mods and modpacks from Modrinth and CurseForge, and signs players in with their
own account.

## What it is not

- It is **not affiliated with, endorsed by, or sponsored by** Mojang Studios or Microsoft.
- It does **not** contain, host, mirror or redistribute Minecraft game files. Client jars,
  assets and libraries are downloaded from Mojang's official servers (or public mirrors of
  those same files) into the player's own folder, at the player's request.
- It does **not** bypass authentication, and it cannot create, crack or share accounts.
  Online play requires a genuine Microsoft account that owns Minecraft: Java Edition.

## Microsoft account sign-in

Sign-in uses the official Microsoft identity platform flow: OAuth 2.0 device
authorization grant (public client, tenant `consumers`, scope `XboxLive.signin`), then
Xbox Live, then XSTS, then:

- `POST https://api.minecraftservices.com/authentication/login_with_xbox` — to obtain the
  player's own Minecraft access token;
- `GET https://api.minecraftservices.com/minecraft/profile` — to confirm Java Edition
  ownership and read that player's own UUID, username and skin.

No other Minecraft Services endpoints are used. Access to those endpoints requires an
**approved application (client) ID**; details and the current status are documented in
（申请细节属于发行者内部流程，不在本仓库维护）。

## Account policy

Authentication is mandatory in this build, and this is enforced in code rather than by a
setting:

- **No offline, unauthenticated or "no-login" profiles.** The account selector only offers
  Microsoft sign-in. There is no build flag, command-line switch, environment variable or
  configuration key that enables an offline profile, and any offline account left over from
  an older installation is rejected before the game can start.
- **Ownership is verified before launch.** `GET /minecraft/profile` must succeed for the
  account, otherwise the installation cannot be launched.
- **Third-party sign-in (authlib-injector / Yggdrasil) is disabled as well.** It
  authenticates a third-party account but does not prove ownership of Java Edition.
- The launcher never bypasses, weakens or substitutes Mojang authentication, and contains
  no cheat, automation or account-sharing functionality.

Enforcement points, if you want to verify this by reading the code:

| Concern | File |
|---|---|
| Offline profiles cannot be created | `src/LunoLauncher/Accounts/OfflineAuth.cs` |
| Offline / third-party accounts are rejected at launch | `src/LunoLauncher/Accounts/AccountService.cs` |
| Both options are hidden from the account dialog | `src/LunoLauncher/UI/AccountDialogs.cs` |
| The switches themselves (both `false`) | `src/LunoLauncher/Branding.cs` |

## Privacy

No telemetry, no analytics, no account backend. The only account data stored is the
refresh token, kept on the user's machine and encrypted with Windows DPAPI under the
current Windows user. Full text: [privacy policy](https://mc23356565.github.io/LunoLauncher/privacy.html).

## Build

Requires the .NET SDK 8 or newer (no Visual Studio needed):

```powershell
.\build.ps1        # outputs dist\LunoLauncher.exe
```

Every push to `main` is built automatically by GitHub Actions, and a tag `v*` publishes
the executable to Releases with its SHA-256 in the release notes. Releases, update-manifest
signing, key rotation and community hand-over are documented in
（发版与接手流程属于维护者内部流程，不在本仓库维护）。

## Fork and credits

This is an independent fork of **OpenLauncher** (decentralized Minecraft launcher,
GPL-3.0). The build this fork is based on came from a public community download page, not
from the copyright holder. Original copyright notices and the GPL-3.0
license are retained. Changes in this fork: rebranding, an independent update channel and
signing key, and an independent website and release process. See [NOTICE](NOTICE).

Minecraft is a trademark of Mojang Synergies AB.

---

# LunoLauncher（中文）

轻量的 Minecraft Java 版启动器，免费、开源，**OpenLauncher 的独立分支**（GPL-3.0）。

- 下载页面：https://mc23356565.github.io/LunoLauncher/
- 发布页（exe + SHA-256）：https://github.com/mc23356565/LunoLauncher/releases
- 联系邮箱：DreemurrDawnlit@outlook.com

## 功能

| 模块 | 功能 |
|---|---|
| 启动 | 一键启动；自动补全缺失文件；自动选择 / 下载合适的 Java；自动分配内存；版本隔离；自定义窗口标题、窗口大小、全屏；启动后自动进服；启动后最小化 / 隐藏 / 关闭；实时游戏日志；导出启动脚本 |
| 账号 | 微软正版（设备代码登录）；皮肤头像；换皮肤（上传 64×64 / 64×32 的 PNG，经典或细手臂，可恢复默认）|
| 联机 | 局域网世界 + 虚拟局域网直连：创建 / 加入房间、房间成员与连接状态、从日志自动读端口、一键复制邀请、启动即进服；**EasyTier 已内置**，把 exe 发给对方就能用 |
| 下载游戏 | 正式版 / 快照 / 远古版 / 愚人节版；Forge、NeoForge、Fabric（可同时装 Fabric API）、Quilt；自己执行 Forge 安装器的处理器，下载可走镜像 |
| 社区资源 | Modrinth + CurseForge 双源搜索 Mod / 整合包 / 资源包 / 光影包 / 数据包；常见中文名搜索；自动补齐前置 Mod；按版本和加载器筛选 |
| 整合包 | 导入 Modrinth（.mrpack）和 CurseForge（.zip）整合包；导出为 .mrpack |
| 管理 | 多个 .minecraft 目录；版本重命名 / 删除（进回收站）/ 描述；Mod 启用禁用、批量检查更新、一键更新；资源包 / 光影 / 存档 / 截图管理，支持拖放安装 |
| 崩溃分析 | 识别内存不足、Java 版本不对、缺前置、重复 Mod、Mixin 冲突、显卡驱动等常见原因并给建议；一键导出错误报告 |
| 种子查询 | 群系地图（随窗口自适应、可无限拖动、滚轮以鼠标为锚点缩放）；Beta 1.7 → 26.3 共 35 个版本（名单来自 cubiomes.dll）；维度 主世界 / 下界 / 末地；1.18+ 三层（地表 / 地下 / 深层）；史莱姆区块；28 种结构标记（含要塞）+ 悬停弹框 / 复制 TP；半径内结构查找（列表 + 双击定位）；群系与结构清单带搜索 |
| 下载 | 多线程、断点换源、全部文件校验；BMCLAPI / MCIM 镜像与官方源错峰竞速，自动避开连不上的源；可自定义镜像；限速 |
| 个性化 | 主题色、背景图 |
| 更新 | 多更新源并行拉取签名清单；可添加信任的发布者公钥；镜像表可随签名清单下发 |

## 下载使用

从 https://mc23356565.github.io/LunoLauncher/ 下载 `LunoLauncher.exe`，放进一个空文件夹双击即可
（Windows 10 / 11 自带 .NET Framework 4.8，无需安装任何东西）。
游戏文件默认放在 exe 旁边的 `.minecraft`，启动器配置在 `LunoLauncher` 文件夹里——整个文件夹拷走就能在别的电脑上用。
联机用的 EasyTier 已经打包在 exe 里（约 13 MB），第一次点「联机」时自动解压到 `LunoLauncher\tunnel\bin`，不需要另外下载。

也可以在 **版本选择 → 添加已有的游戏目录** 里直接使用官方启动器、PCL、HMCL 的 `.minecraft`。

## 项目结构

```
src/LunoLauncher/
  Core/       JSON、HTTP、镜像表、多线程下载器、设置、任务
  Game/       版本 JSON 合并、安装器（原版 / Fabric / Quilt / Forge / NeoForge）、启动参数、Java 管理、崩溃分析
  Accounts/   微软正版登录、皮肤（离线与外置登录在本发行版中已关闭）
  Content/    Modrinth / CurseForge、本地 Mod 管理、整合包导入导出
  Update/     签名清单与自更新
  UI/         WPF 界面
tools/Release/  发布工具：生成密钥、签名清单、校验
docs/           维护文档
```

## 声明

本项目与 Mojang、Microsoft 无关，Minecraft 是 Mojang Synergies AB 的商标。
本项目是 OpenLauncher 的独立分支，原作者版权声明与 GPL-3.0 许可均予保留，详见 [NOTICE](NOTICE)。
镜像服务由 bangbang93（BMCLAPI）、MCIM 等社区成员公益提供，请合理使用。

## 发行版策略（重要）

本发行版**只支持微软正版登录**：账号类型只有「微软正版」，没有「离线」，也没有「外置登录（authlib-injector）」。

原因（对应官方审核原文）：*"Applications must verify the user's game ownership and require
authentication before enabling gameplay or access to servers or sites. Applications that
bypass or disable authentication, ownership or license checks, or other safety features will
not be approved. This requirement applies regardless of server configuration."*

- 离线账号：不需要认证就能进入游戏，不满足上述要求
- 外置登录：验证的是第三方皮肤站账号，不能证明玩家拥有 Java 版

两项分别由 `src/LunoLauncher/Branding.cs` 的 `AllowOfflineAccount` 与 `AllowThirdPartyLogin`
控制，默认均为 `false`；旧配置里残留的离线 / 外置登录账号会被拒绝启动，且没有开关可以打开。

## 内置 Client ID 与发布方式（维护者）

官方发布的 exe 自带一个**已通过 Mojang 审核**的 Client ID，所以下载下来就能登录微软账号。
这个值**不在源码里、也不在仓库里**，三种注入方式：

| 来源 | 用法 |
|---|---|
| 构建参数 | `.\build.ps1 -MsClientId 00000000-0000-0000-0000-000000000000` |
| 本地文件 | 在仓库根放 `clientid.txt`（只写一行 GUID，已在 `.gitignore` 里），`build.ps1` 自动读 |
| CI | 仓库 Settings → Secrets and variables → Actions 新建 `MS_CLIENT_ID`，工作流会 `-p:MsClientId=` 传给编译器 |

取到的值由 MSBuild 生成到 `obj\BuiltInClientId.g.cs`，编译成 `Branding.DefaultMicrosoftClientId`。
什么都没提供时是空字符串，行为与以前一致：提示用户自己去申请。

**为什么用注入而不是混淆**：设备代码流属于 OAuth 2.0 的公共客户端（public client），
Client ID 按设计就是公开的 —— 它会出现在请求体、浏览器地址栏和 exe 里的字符串常量中，
任何人都能取出来；真正需要保密的是**客户端密钥 client secret**，而这条流程根本不用它。
把 Client ID 排除在源码外能达到的真实目的是：**别人 fork 你的仓库后不能直接编译出一个能登录的版本**，
必须自己走一遍申请流程（`https://aka.ms/mce-reviewappid`）。混淆改变不了这一点。

自己 fork 后要发布，请自己申请 Client ID；不要复用别人的。
