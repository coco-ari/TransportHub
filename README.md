# TransportHub

TransportHub 是一个面向多台自有 Windows 电脑的轻量悬浮投递窗。它使用
[Syncthing](https://syncthing.net/) 负责局域网发现、公网连接、加密传输、断点续传和中继，
自身只提供统一的文字、链接、图片、文件及文件夹时间线。

> 当前处于早期预览阶段，仅适合加入由同一个人管理、彼此完全可信的设备。

## 功能

- 始终置顶的紧凑 Windows 窗口，可折叠成屏幕边缘按钮或隐藏到托盘。
- 发送文字与 HTTP(S) 链接；点击文字即可复制。
- 通过回形针、拖放或剪贴板发送文件、文件夹和图片。
- 点击附件卡片，在资源管理器中打开所在目录并选中文件。
- 显示传输进度、在线设备数量和消息送达状态。
- 在悬浮窗内复制连接码、发起连接并确认新电脑，无需打开 Syncthing 网页。
- 支持 Syncthing 的局域网直连、公网 NAT 穿透及端到端加密中继。
- TransportHub 与 Syncthing 均可在 Windows 登录后自动启动；卸载桌面窗不会删除 Syncthing 或同步数据。

TransportHub 不实现独立的聊天服务器，也不会替代 Syncthing。实时音视频通话目前不在范围内。

## 工作方式

```text
TransportHub 悬浮窗
        │
        ├─ 文字、附件与回执元数据
        ▼
%USERPROFILE%\TransportHub
        │
        ▼
Syncthing ── 局域网直连 / 公网直连 / 加密中继 ── 其他电脑
```

桌面端不会把 Syncthing API 密钥写入仓库或命令行。它只在本机读取 Syncthing 配置，
并通过本机 GUI/API 获取状态。每台电脑必须保留自己独立的 Syncthing Device ID、证书和密钥。

## 一键安装（推荐）

打开 [GitHub Releases](https://github.com/coco-ari/TransportHub/releases)，下载并双击
`TransportHub-Setup-v<版本>.exe`。只需要运行这一个安装程序，无需先安装 Syncthing，
也无需下载源码或 Visual Studio。

安装程序会自动完成：

- 将 TransportHub 安装到 `%LOCALAPPDATA%\Programs\TransportHub`；
- 创建桌面图标和开始菜单快捷方式；
- 让 TransportHub 在登录 Windows 后自动启动，并在安装完成后立即启动；
- 如果电脑没有 Syncthing，通过 WinGet 在线安装开源的 Syncthing Windows Setup；
- 创建 `%USERPROFILE%\TransportHub` 和 `transporthub-data` 同步文件夹；
- 配置 90 天阶梯版本控制和 Windows 登录自启动，并尝试创建防火墙规则；
- 保留电脑上已有的 Syncthing 设备身份、配对关系及其他文件夹配置。

首次安装需要联网并具备 Windows App Installer/`winget`。配置防火墙时 Windows 可能弹出一次
管理员确认。安装器目前尚未代码签名，因此可能显示 SmartScreen 或“未知发布者”；请只从本仓库
Release 下载，并使用随附的 `SHA256SUMS.txt` 核验文件。

安装完成后，双击桌面的 TransportHub 图标，或点击屏幕边缘的紫色 `T` 按钮。安装器会把
TransportHub 与 Syncthing 一次装好；电脑之间的首次互信在 TransportHub 窗口内完成。

升级时直接运行新版安装程序，不需要先卸载。设备身份、同步配置、消息和文件都会保留。
如果 Syncthing 安装、网络配置或防火墙配置失败，安装器会中止并保留当前版本，
不会留下无法使用的半安装状态。

## 系统要求

运行环境：

- Windows 10 或 Windows 11
- PowerShell 5.1 或更新版本
- Windows App Installer（提供 `winget`）
- .NET Framework 4.8

从源码构建还需要：

- Visual Studio 2022 或更高版本的 Build Tools（含 Roslyn C# 编译器）
- .NET Framework 4.8 Developer Pack

## 添加其他电脑

推荐流程：

1. 在主电脑打开 TransportHub，点击标题下方的“点击这里连接电脑”。
2. 点击“复制”本机连接码，通过可信渠道把它发到新电脑。
3. 新电脑只需运行同一个一键安装包；启动后点击同一位置，粘贴连接码并点“连接”。
4. 主电脑会显示“新电脑请求连接”，核对电脑名称和连接码后点“接受”。
5. 状态变成在线后，文字和文件会自动同步；局域网和公网使用同一套步骤。

连接码包含 Syncthing Device ID 和电脑显示名称，不包含证书、私钥或 API Key。连接码本身
不是密码；TransportHub 仍要求已连接一方明确接受新设备，防止陌生电脑直接加入资料空间。

如需兼容旧版本，也可以直接粘贴完整的 Syncthing Device ID。手动管理可打开 Syncthing Web GUI：

```text
http://127.0.0.1:8384/
```

从源码仓库操作时，也可以在双方分别运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\add-peer.ps1 `
  -DeviceId '<对方完整的 Syncthing Device ID>' `
  -DeviceName '<对方电脑名称>'
```

推荐使用一台长期在线电脑作为中心节点：中心添加所有普通节点，普通节点只添加中心。

## 从源码构建（开发者）

先安装并配置 Syncthing：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\bootstrap.ps1
```

如需使用其他数据盘，可传入 `-FolderPath 'D:\TransportHub'`。脚本可重复运行，并会保留已有
设备身份及其他 Syncthing 配置。

然后构建并安装桌面端：

```powershell
& .\scripts\build-desktop.ps1 -Configuration Release
& .\artifacts\TransportHub.Desktop\TransportHub.exe --self-test
& .\scripts\install-desktop.ps1
```

源码安装脚本同样会创建桌面图标、开始菜单快捷方式和当前用户登录启动项，并默认完成
Syncthing 安装、配置与登录自启动；重复运行可安全升级。

卸载桌面窗：

```powershell
& .\scripts\uninstall-desktop.ps1
```

卸载脚本不会删除 Syncthing、Syncthing 配置或 `%USERPROFILE%\TransportHub` 中的数据。

验证：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

完成设备配对后可启用严格连接检查：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 `
  -RequireConnectedPeer
```

## 使用

- 输入文字后按 Enter 发送，Shift+Enter 换行。
- 点击文字卡片复制全文。
- 点击回形针选择文件或文件夹。
- 将文件或文件夹拖到主窗口或折叠按钮即可发送。
- 在窗口中按 Ctrl+V 可发送剪贴板图片、文件或单独的 HTTP(S) 链接。
- 点击已到达的附件卡片会打开资源管理器并定位文件。
- 点击标题栏 `—` 折叠到屏幕边缘，点击 `×` 隐藏到系统托盘。
- 点击标题下方的连接状态可随时查看本机连接码、连接新电脑或接受连接请求。

## 卸载

在“Windows 设置 → 应用 → 已安装的应用”中卸载 TransportHub。卸载会移除 TransportHub、
桌面/开始菜单快捷方式及 TransportHub 自启动项，但会保留：

- Syncthing 及其登录自启动任务；
- `%LOCALAPPDATA%\Syncthing` 中的设备身份与配置；
- `%USERPROFILE%\TransportHub` 中的消息和文件。

这是为了避免误删或失去同步资料。如确实要彻底移除 Syncthing，请另行从 Windows 设置中卸载；
同步数据目录只能在确认已有备份后手动删除。

## 仓库结构

```text
apps/TransportHub.Desktop/   Windows 桌面端、时间线协议与自测
scripts/                     Syncthing 部署、配对、验证及桌面端安装脚本
design/                      产品与协议设计说明
prototype/                   早期交互原型
```

## 安全边界

- 同一共享文件夹中的可写设备可以写入消息和回执元数据。不要加入不可信设备。
- 当前消息协议没有独立的设备公钥签名，信任边界等同于 Syncthing 文件夹成员。
- 不要复制或提交 Syncthing 的 `config.xml`、`cert.pem`、`key.pem` 或 API 密钥。
- 保持 Web GUI 监听在 `127.0.0.1`，不要把 8384 端口直接暴露到公网。
- TransportHub 会校验附件路径并拒绝路径穿越、ADS、重解析点及同步冲突元数据，
  但仍不会自动执行收到的文件。

发现安全问题时，请使用 GitHub 仓库的私密安全报告功能，不要在公开 Issue 中提交密钥、
设备 ID、个人路径或可利用细节。

## 数据安全

同步不是备份。误删、覆盖、损坏和勒索软件加密都可能传播到其他在线设备。

- 为长期在线节点配置独立、不可变或离线备份。
- 不要直接同步正在运行的数据库、虚拟机磁盘或 Outlook PST。
- 先在测试目录验证小文件、离线补齐、大文件续传、SHA-256 一致性、版本恢复和冲突处理。
- 公共中继能看到连接 IP、Device ID、流量大小和时间等元数据，但不能读取端到端加密内容。

## 已知限制

- 当前仅支持 Windows。
- 所有共享成员必须彼此可信。
- 文字与附件依赖 Syncthing 最终一致性，不是实时聊天服务。
- 预览版安装器尚未代码签名，也没有自动更新。
- 实时音视频通话尚未实现。

## 开发与测试

构建脚本会调用 Visual Studio 2022 或更高版本的 Roslyn 编译器，并将输出写入 `artifacts/`：

```powershell
& .\scripts\build-desktop.ps1 -Configuration Debug
& .\scripts\build-desktop.ps1 -Configuration Release
& .\artifacts\TransportHub.Desktop\TransportHub.exe --self-test
```

自测覆盖时间线消息、附件、送达回执、畸形文件隔离、文件/目录传输、哈希和路径安全。

## 许可证

[MIT](LICENSE)

Syncthing 是独立项目，使用其自己的许可证。TransportHub 不包含 Syncthing 二进制文件。
一键安装器会在安装时通过 WinGet 获取开源的 Bill Stewart Syncthing Windows Setup。
