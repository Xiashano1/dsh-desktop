# DshDesktop v9 — DeepSeek Harness WebUI 桌面壳（原生窗口 + 托盘 + 提问面板）

把 DeepSeek Harness WebUI（`dsh web`）封装成 Windows 桌面应用的轻量壳。

- 技术栈：**.NET 7 WinForms + WebView2**
- 发布形态：**自包含单文件**（`publish-v3\DshDesktop.exe`，68.5 MB，含 .NET 运行时，拷到任何 Win10/11 机器双击即用）
- 行为：启动时检测目标地址是否已有 `dsh web` 在运行 → 没有则自动拉起 `dsh web --port <端口>` → 就绪后加载页面

## 界面（v10 最终版）

- **标准 Windows 窗口**：系统原生标题栏（官方图标/标题/最小化/最大化/关闭、双击最大化、贴靠、系统菜单），标题栏在客户区外不占页面
- **官方图标**：从 DSH 官方 `favicon.svg` 渲染生成的 `app.ico`（16–256 共 9 尺寸）嵌入 exe
- **深灰标题栏**：Win11 DWM `DWMWA_CAPTION_COLOR` = `#3C3C3C`（深色模式白色文字）+ 同色边框；Win10 自动跳过
- **提问面板（注入页面右侧）**：
  - 右侧一个 **◀ 按钮**，点击面板**向左平行滑出**（动画），显示**全部用户提问**（单行 + 省略号、悬停微亮）
  - **点击某行 → 平滑跳转到该消息并自动收回**
  - **自动加载完整历史**：自动点击前端"加载更早"逐页拉取，覆盖从第一条到最新的全部提问；切换对话自动重载
  - 每 2.5s 同步，新提问实时出现
- **系统托盘**（滴答清单深色风）：
  - 托盘图标存在 = DSH 服务运行中
  - **点窗口 X / Alt+F4 → 静默收进托盘**（无通知气泡，服务继续运行）
  - **左键单击托盘图标 → 直接打开主窗口**
  - **右键菜单**（纯平深底 `#26262A` + 圆角 + 圆角悬停高亮，与标题栏同族）：
    - **打开桌面版** — 显示主窗口
    - **打开 WEB 版** — 系统浏览器打开 `http://127.0.0.1:3080`
    - **关闭所有 DSH** — 关闭本应用拉起的 + 外部启动的全部 dsh web 服务，并完全退出
  - 新图标默认在"显示隐藏的图标"里，拖出即可固定
- **悬浮气泡**：连接中/失败时底部中央显示（含原因 + 重试），连上自动消失
- **DPI**：SystemAware（适配 ToDesk 虚拟显示器等会话环境）
- 全程日志 `%TEMP%\DshDesktop.log`；dsh 自身输出 `%TEMP%\DshDesktop-dsh.log`

## 使用

```powershell
.\publish-v3\DshDesktop.exe                          # 默认 http://127.0.0.1:3080
.\publish-v3\DshDesktop.exe --url http://127.0.0.1:7456
```

## 修复记录

1. **系统代理导致连接失败**：Watt Toolkit (Steam++) 系统代理（127.0.0.1:26561）会吞掉 localhost
   请求 → 探测强制 `UseProxy = false`。
2. **黑屏卡"等待 dsh 启动"**：旧副本无代理绕行所致；状态以悬浮气泡显示真实原因。
3. **拖动时 WebView2 内容不跟随**：窗口移动/缩放后反射调用 `NotifyParentWindowPositionChanged`。
4. **无边框/自绘标题栏 → 回归原生窗口**：标题栏在客户区外，不占页面空间。
5. **DPI 虚拟化**：SystemAware + 窗口尺寸自适应工作区，适配 ToDesk 会话。
6. **对话历史分页**：前端"加载更早"按钮逐页拉取，确保提问面板覆盖完整历史。
7. **托盘**：X 收托盘常驻；托盘退出关闭全部 dsh 服务（netstat 定位监听进程）。

## 重新构建

双击 **`build.bat`** 一键构建；或手动执行：

```powershell
dotnet publish .\DshDesktop.csproj -c Release -o .\publish-v3
```
