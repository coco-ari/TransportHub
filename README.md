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
- 支持 Syncthing 的局域网直连、公网 NAT 穿透及端到端加密中继。
- Windows 登录自启动；卸载桌面窗不会删除 Syncthing 或同步数据。

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

## 系统要求

运行环境：

- Windows 10 或 Windows 11
- PowerShell 5.1 或更新版本
- `winget`
- Syncthing（部署脚本可自动安装）

从源码构建还需要：

- Visual Studio 2022 或更高版本的 Build Tools（含 Roslyn C# 编译器）
- .NET Framework 4.8 Developer Pack

## 快速开始

### 1. 安装并配置 Syncthing

在 PowerShell 中运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\bootstrap.ps1
```

脚本默认执行以下操作：

- 以当前用户模式安装 Syncthing Windows Setup；
- 创建 `%USERPROFILE%\TransportHub`；
- 配置 Folder ID `transporthub-data`；
- 启用发送与接收及 90 天阶梯版本控制；
- 保留已有设备身份和其他 Syncthing 配置；
- 配置登录启动与 Windows 防火墙规则。

如需使用其他数据盘：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\bootstrap.ps1 `
  -FolderPath 'D:\TransportHub' `
  -FolderId 'transporthub-data'
```

脚本可重复运行；如果相同 Folder ID 已指向其他路径，会停止并要求人工确认。

### 2. 添加其他电脑

在每台电脑打开 Syncthing Web GUI：

```text
http://127.0.0.1:8384/
```

通过“操作 → 显示 ID”获取 Device ID，并通过可信渠道交换。双方都需要添加对方并共享
`transporthub-data`。也可以在双方分别运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\add-peer.ps1 `
  -DeviceId '<对方完整的 Syncthing Device ID>' `
  -DeviceName '<对方电脑名称>'
```

推荐使用一台长期在线电脑作为中心节点：中心添加所有普通节点，普通节点只添加中心。

### 3. 构建并安装悬浮窗

```powershell
& .\scripts\build-desktop.ps1 -Configuration Release
& .\artifacts\TransportHub.Desktop\TransportHub.exe --self-test
& .\scripts\install-desktop.ps1
```

安装位置为 `%LOCALAPPDATA%\Programs\TransportHub`。安装脚本会创建当前用户登录启动项和
开始菜单快捷方式，重复运行可安全升级。

卸载桌面窗：

```powershell
& .\scripts\uninstall-desktop.ps1
```

卸载脚本不会删除 Syncthing、Syncthing 配置或 `%USERPROFILE%\TransportHub` 中的数据。

### 4. 验证

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
- 尚无自动更新、代码签名或官方安装包。
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
