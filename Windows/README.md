# AirSend Windows 客户端

Windows 客户端使用 .NET 8 WPF 构建，支持 LocalSend 设备发现、文件双向传输、文字/图片剪贴板同步、系统托盘和开机启动。

## GitHub Actions 构建

`.github/workflows/windows.yml` 会在 GitHub 的 `windows-latest` runner 上生成自包含 ZIP。Windows 电脑不需要安装 .NET SDK、Visual Studio 或 Android 工具链。

## 首次运行

客户端接收端口为 TCP `53318`，设备发现使用 UDP `53317`，并使用 LocalSend 组播地址 `224.0.0.167`。如果 Windows 防火墙弹窗出现，请允许 AirSend 访问专用网络。

也可以用管理员 PowerShell 手动放行端口：

```powershell
New-NetFirewallRule -DisplayName "AirSend TCP 53318" -Direction Inbound -Protocol TCP -LocalPort 53318 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "AirSend UDP 53317" -Direction Inbound -Protocol UDP -LocalPort 53317 -Action Allow -Profile Private
```

## Android 联调

让 Android 与 Windows 连接同一个 Wi-Fi，启动客户端后点击“刷新设备”。先测试普通文件，再测试 `clipboard.txt` 文字剪贴板和 `AirSend-Clipboard-*.png` 图片剪贴板。校园网开启客户端隔离时，局域网发现和传输可能被路由器阻止。
