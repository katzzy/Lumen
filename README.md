# Lumen Terminal 开发报告（v1.1.12）

> 这份文档是给项目维护者的工程状态报告，不是公开发布宣传页。
> 记录当前实现、两轮审查后的修复、性能数据、验证结果和仍然存在的问题。

## 1. 当前状态

- 版本：`1.1.12`
- 目标：Windows 原生串口控制台，仅串口；不包含 SSH/Telnet/Web 技术
- 技术栈：C# / .NET 10 / WPF，终端自绘，串口直接调用 Win32 API
- 构建：0 错误，Analyzer 0 警告
- 自检：17 项全部通过
- 发布：只保留当前 v1.1.12 Release；历史 Release 已按维护要求清空
- 仓库：`main` 与 `origin/main` 同步，提交/tag 已签名

## 2. 主要修复记录

### P1：正确性与可靠性

| 项 | 状态 | 说明 |
|---|---|---|
| 窗口缩放丢内容 | 完成 | 可见屏幕 + 最近回滚立即重排；完整 scrollback 后台快照重排，版本校验失败会重试 |
| Dispose/连接竞态 | 完成 | `Dispose` 置 `IsConnected=false`；连接/发送/断开路径均有 `_disposed` 守卫 |
| 多实例覆盖会话 | 完成 | 命名 Mutex 单实例；第二次启动激活已有窗口 |
| 快捷命令发错会话 | 完成 | 确认前捕获目标 Tab，确认后只操作该会话 |
| 反色背景错误 | 完成 | 背景与文字统一走 `ResolveColors`，普通背景有快速路径 |

### P2：重要功能与性能

| 项 | 状态 | 说明 |
|---|---|---|
| 长粘贴无法取消 | 完成 | 粘贴中按 `Esc` 取消，状态栏显示完成/取消 |
| 模态窗口快捷键 | 完成 | Overlay 可见时屏蔽全局快捷键 |
| 标签溢出 | 完成 | 标签栏支持滚轮横向滚动 |
| 日志写盘阻塞 UI | 完成 | bounded channel + 后台 worker；Tab 关闭异步落盘，退出时等待 pending log |
| CJK/emoji 搜索 | 完成 | 按 cell 映射高亮，emoji 不被替换为空格 |
| FindNext O(n²) | 完成 | 匹配项按行建索引，渲染不再全表扫描 |
| 画刷缓存 | 完成 | 有上限，批量淘汰；`FormattedText` 复用 |
| 快速连接换波特率 | 完成 | 同端口已打开时提示先关闭，不再静默激活旧波特率 |
| 编辑已打开会话 | 完成 | 同步快捷命令、粘贴延迟、日志目录、初始化命令等 |
| README 日志目录 | 完成 | 工具栏“日志目录”直接打开 |
| 手动重连 | 完成 | 工具栏“重连”；自动重连按要求不做 |
| 蓝牙串口标记 | 完成 | 串口下拉显示 `BT`，提示中显示“蓝牙” |
| 串口写入 0 字节 | 完成 | 抛出 `SerialException` 并触发断开 |
| 测试 | 完成 | `tests/Lumen.Tests`，17 项自检 |
| UIAutomation | 完成 | Document + TextPattern，可读取终端文本；选区 API 仍有限 |
| GitHub Actions | 未做 | 按要求暂时忽略 |

### P3：质量与架构

- 死代码清理：移除未使用设置、事件、方法和浅色调色板
- 大文件拆分：
  - `MainWindow` → `Tabs` / `Sessions` / `Actions`
  - `TerminalView` → `Rendering` / `Selection`
- 关键失败路径写入 crash log（终端解析、日志启停、会话/设置读写）
- `.editorconfig` 处理 Intentional 规则，Analyzer 0 警告
- 全项目 `dotnet format`
- MIT LICENSE 已加入并打包
- 首次启动会话列表为空，不再绑定 COM3/COM4
- Auto 会话不再默认带 Cisco 快捷命令
- README 增加日志敏感信息提醒

## 3. 性能专项

### 已完成

- **VT 解析移出 UI 线程**：串口读取线程直接解码/解析，UI 只收合并后的 `Updated`
- **逐行渲染缓存**：按行缓存 `DrawingGroup`，内容/选择/搜索/光标哈希不变时复用；光标闪烁只重建光标行
- **零拷贝串口读取**：`DataReceived` 使用 `ReadOnlyMemory<byte>`，读取线程复用 8KB buffer
- **搜索**：150ms debounce，每 256 行让出 dispatcher，可取消
- **Scrollback**：固定容量环形缓冲，追加/裁剪 O(1)，不再 `List.RemoveAt(0)`
- **完整 reflow**：后台快照重排 + 版本校验，UI 不阻塞
- **日志**：bounded channel（8192，DropOldest），后台 worker
- **UIA**：TextPattern 快照缓存 250ms
- **串口写**：`ArrayPool<byte>` 复用
- **UI 同步**：`ScreenBuffer.SyncRoot` 保护解析与渲染/搜索

### 微基准（当前机器，Release，单次采样）

| 场景 | 结果 |
|---|---:|
| Scrollback 追加 20k 行（上限 10k） | 175–183 ms |
| Scrollback 追加 40k 行（上限 10k） | 221–274 ms |
| 完整重排 10k 行 / 120→80 列 | 66–86 ms（后台执行） |
| 搜索 10k 行 | 50–60 ms（分块，不阻塞单帧） |

## 4. 验证

```powershell
dotnet build .\src\Lumen\Lumen.csproj -c Debug
dotnet run --project .\tests\Lumen.Tests\Lumen.Tests.csproj -c Release
```

已验证：

- 17 项自检全部通过
- Analyzer 0 警告，构建 0 错误
- 后台解析 + Cisco Banner 自动识别 + 自动分页正常
- 窗口缩放后内容保留，后台完整回滚重排
- 异步搜索高亮 5 处 `Switch`，计数正确
- 单实例第二次启动激活已有窗口，进程数保持 1
- UIA `DocumentRange.GetText` 可读取完整终端文本
- 1.1.12 打包版启动与版本号检查通过

## 5. 已知限制 / 未完成

这些不是当前热路径的性能问题，但仍是明确限制：

- **自动重连**：按之前约定未实现，仅保留手动“重连”
- **GitHub Actions**：按要求暂时忽略
- **UIA 选区**：`SupportedTextSelection.None`，TextPattern 可读但不可选；`GetEnclosingElement` 仍返回 null
- **画刷缓存**：是批量淘汰，不是严格 LRU
- **文件拆分**：`VtParser` / `ScreenBuffer` 仍是较大单文件
- **日志 ANSI 剥离**：仍逐字符，但在后台 worker 执行
- **IME 候选框**：已接入 IMM32 定位，但当前环境没有中文输入法做人工位置验证
- **代码签名**：未做，企业分发前可自签
- **敏感日志**：可能包含设备配置/SNMP community/接口地址，分享前需脱敏

## 6. 目录结构

```text
src/Lumen/
  Controls/       TerminalView（Rendering/Selection）、UIA、IME
  Terminal/       VT 解析、ScreenBuffer、ScrollbackBuffer、编码、按键
  Serial/         Win32 串口、端口枚举、演示会话
  Services/       会话存储、日志、设置
  Views/          会话编辑器、设置、确认对话框
  Themes/         深色主题与控件样式
  MainWindow.*    主窗口（Tabs/Sessions/Actions 分部类）
tests/Lumen.Tests/  17 项自检
```

## 7. 运行与打包

```powershell
# 构建
dotnet build .\src\Lumen\Lumen.csproj -c Debug

# 打包（self-contained，win-x64）
.\build.ps1
```

产物：`dist\Lumen`，压缩包 `dist\Lumen-1.1.12-win-x64.zip`。首次运行生成 `data` 与 `logs`（便携模式）；删除 `portable.flag` 可改用 `%APPDATA%\Lumen`。

## 8. 数据与日志

- 便携：`程序目录\data`、`程序目录\logs`
- 非便携：`%APPDATA%\Lumen`、`文档\Lumen Logs`
- 日志 UTF-8，自动去 ANSI；可选时间戳
- 日志可能包含敏感信息，需限制目录权限并在分享前脱敏

## 9. License

MIT，详见 [LICENSE](LICENSE)。SecureCRT 是 VanDyke Software 的商标；Lumen 与其无关联。

---

维护者报告 · Lumen Terminal 1.1.12
