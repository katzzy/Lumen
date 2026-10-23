# Lumen Terminal 1.1.0

面向网络运维的 Windows 原生串口控制台，SecureCRT 的轻量替代品。**不使用任何 Web 技术**：界面为 WPF + XAML，终端为 DirectWrite 自绘，串口直接调用 Win32 API。

![Lumen Terminal](screenshot.png)

## 为什么是 Lumen

- **串口优先**：COM 口枚举（含 USB 描述）、波特率/数据位/校验/停止位/流控、DTR/RTS、**Send Break**（交换机进 ROMMON / 密码恢复）。
- **粘贴不丢字符**：每行可配置延迟（默认 100ms），9600 波特率下粘贴整段配置也不会丢；危险命令（`erase` / `reload` / `write erase` 等）二次确认。
- **日志留痕**：连接即自动记录（可关闭），可选每行时间戳，按会话与时间命名；一键打开日志目录。
- **运维细节**：连接后自动发送命令（如 `terminal length 0`）、快捷命令按钮、按站点/机房分组、搜索回滚、查找高亮、GBK/UTF-8 编码。
- **现代界面**：深色 Fluent 风格、自定义标题栏、标签页、自绘终端网格、等宽字体、清晰的中文回退。
- **原生且轻**：单文件夹、免安装运行时（self-contained），可放在 U 盘里带走。

## 厂商适配

内置 13 家厂商模板，新建/编辑会话时选择厂商即可带出默认波特率、编码、分页命令和快捷命令；也可以在连接后根据设备启动信息自动识别。

| 厂商 | 默认串口 | 自动关闭分页 | 编码 |
|---|---|---|---|
| Cisco IOS / IOS-XE | 9600 8N1 | `terminal length 0` | UTF-8 |
| 华为 VRP | 9600 8N1 | `screen-length 0 temporary` | GBK |
| H3C Comware | 9600 8N1 | `screen-length disable` | GBK |
| 中兴 ZXR10 | 9600 8N1 | `terminal length 0` | GBK |
| 锐捷 RGOS | 9600 8N1 | `terminal length 0` | GBK |
| 迈普 MyPower | 9600 8N1 | `terminal length 0` | GBK |
| TP-Link JetStream | 9600 8N1 | `terminal length 0` | GBK |
| Juniper Junos | 9600 8N1 | `set cli screen-length 0` | UTF-8 |
| Arista EOS | 9600 8N1 | `terminal length 0` | UTF-8 |
| Aruba / HPE | 9600 8N1 | `no page` | UTF-8 |
| Dell OS10 / OS9 | 9600 8N1 | `terminal length 0` | UTF-8 |
| Extreme EXOS | 9600 8N1 | `disable clipaging` | UTF-8 |
| MikroTik RouterOS | 115200 8N1 | 不发送 | UTF-8 |

- **自动识别**：连接后读取设备 Banner，识别到厂商后自动切换编码、应用快捷命令，并在首次出现登录提示符后发送分页命令。
- **手动选择**：如果设备启动时没有 Banner（例如已经登录），在会话编辑器的「厂商」里选择对应模板，点「套用厂商模板」即可。
- **通用 VT100**：不属于以上厂商时选「通用」，不会自动发送任何命令。
- 分页命令与中文编码以设备手册和实际输出为准；国产设备中文乱码时优先切 GBK。


## 快速开始

1. 解压 `Lumen-1.1.0-win-x64.zip` 到任意**可写目录**（例如 `D:\Tools\Lumen`）。
2. 双击 `Lumen.exe`。
3. 在左侧「快速连接」选择 COM 口与波特率，点击「连接控制台」。
4. 没有设备也可以点「打开演示会话」体验完整界面。

> 首次运行会在程序目录生成 `data` 与 `logs`（便携模式）。如果希望把配置放到 `%APPDATA%\Lumen`，删除程序目录下的 `portable.flag` 即可。

## 常用快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+Shift+T` | 新建会话 |
| `Ctrl+Shift+W` | 关闭当前会话 |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 切换标签 |
| `Ctrl+1` … `Ctrl+9` | 跳到指定标签 |
| `Ctrl+F` | 在当前会话中查找 |
| `Ctrl+Shift+A` | 全选屏幕内容 |
| `Ctrl+Shift+C` / `Ctrl+C` | 复制（有选区时复制，否则发送 Ctrl+C） |
| `Ctrl+V` / `Ctrl+Shift+V` | 粘贴（带节流） |
| `Ctrl++` / `Ctrl+-` / `Ctrl+0` | 放大 / 缩小 / 重置字号 |
| `Shift+PageUp` / `Shift+PageDown` | 翻看回滚缓冲 |
| `Shift+Home` / `Shift+End` | 跳到回滚顶部 / 底部 |
| `F11` | 最大化 / 还原 |
| `Ctrl+Break` | 发送串口 Break（也可点工具栏 Break） |

## 数据与日志

- 便携模式（默认）：`程序目录\data\sessions.json`、`settings.json`、`程序目录\logs\*.log`
- 非便携模式：`%APPDATA%\Lumen\`、`文档\Lumen Logs\`
- 会话编辑器中可覆盖单个会话的日志目录。
- 日志为 UTF-8 纯文本，自动去除 ANSI 控制序列；可选行首时间戳。

## 当前版本边界

1.1 专注串口，以下功能**尚未包含**（架构已预留）：

- SSH / Telnet / SFTP、端口转发、跳板机
- XMODEM / YMODEM / ZMODEM 文件传输
- 自动重连、批量群发、分屏
- 浅色主题、脚本引擎、插件
- 自绘终端中的中文输入法合成预览（中文显示与粘贴正常，CLI 输入以英文为主）

## 从源码构建

需要 **.NET 10 SDK**（Windows）：

```powershell
dotnet build .\src\Lumen\Lumen.csproj -c Debug
dotnet publish .\src\Lumen\Lumen.csproj -c Release -r win-x64 --self-contained true -o .\dist\Lumen
```

或直接运行：

```powershell
.\build.ps1
```

产物在 `dist\Lumen`，首次运行前会自动生成配置。发布包为 self-contained，目标机器无需安装 .NET。

## 目录结构

```
src/Lumen/
  Controls/TerminalView.cs     终端渲染、选区、查找、输入
  Terminal/                    VT 解析、屏幕缓冲、编码、按键编码
  Serial/                      Win32 串口、端口枚举、演示会话
  Services/                    会话存储、日志、设置
  Views/                       会话编辑器、设置、确认对话框
  Themes/                      深色主题与控件样式
  MainWindow.xaml(.cs)         主窗口、标签、会话树
```

## 已知说明

- 串口层直接使用 `CreateFile` / `SetCommState` / `EscapeCommFunction`，不依赖 `System.IO.Ports`，因此支持 Break 与 DTR/RTS，且拔插时不会卡死。
- 终端滚动缓冲默认 10,000 行，可在设置中调整。
- 日志按会话名称与开始时间命名，同名会话不会覆盖。
- 本版本未做代码签名，企业分发前建议使用内部证书签名，避免 SmartScreen 提示。

---

Lumen Terminal 1.1.0 · 为网络运维打造的串口控制台
