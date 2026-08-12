# TransportHub v0.1.0（预览版）

这是首个 Windows 公开预览版。TransportHub 在 Syncthing 之上提供一个可置顶、可折叠的
文字和文件投递窗口。

## 本版内容

- 同步文字、链接、文件、文件夹和剪贴板图片
- 拖放发送；点击文字复制；点击附件在资源管理器中定位
- 始终置顶，折叠为屏幕边缘按钮，或隐藏到系统托盘
- 单一 Windows 安装程序，默认创建桌面和开始菜单快捷方式
- TransportHub 在用户登录 Windows 后自动启动
- 若电脑尚未安装 Syncthing，安装程序会通过 WinGet 安装并配置同步文件夹、90 天版本保留、登录自启动和防火墙

## 安装

1. 下载 `TransportHub-Setup-v0.1.0.exe`。
2. 双击安装；防火墙配置时 Windows 可能请求管理员确认。
3. 在每台电脑完成安装后，通过可信渠道交换 Syncthing Device ID，并共享 `transporthub-data`。

升级时直接运行新版安装程序，无需先卸载。

## 重要说明

- 这是预览版，只应连接由同一个人管理、彼此完全可信的设备。
- 安装器目前未做代码签名，Windows 可能显示 SmartScreen 或“未知发布者”。请仅从本仓库 Release 下载，并使用 `SHA256SUMS.txt` 核验。
- 安装 Syncthing 需要网络及 Windows App Installer/WinGet；Syncthing 二进制不包含在本安装器内。
- 卸载 TransportHub 会保留 Syncthing、设备身份、同步配置和同步数据，避免误删。同步不是备份。
- 仅支持 Windows 10/11；暂无自动更新与实时音视频。
