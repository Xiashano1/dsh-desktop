# DshDesktop

> DeepSeek Harness WebUI（`dsh web`）的 Windows 桌面客户端 —— 原生窗口 · 系统托盘 · 提问历史面板

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/release/Xiashano1/dsh-desktop)](https://github.com/Xiashano1/dsh-desktop/releases)

DshDesktop 把 DeepSeek Harness WebUI 封装成 Windows 桌面应用：自动拉起本地 `dsh web` 服务，以原生窗口承载页面，并额外提供提问历史面板与系统托盘常驻等桌面体验。

## ✨ 特性

- **原生 Windows 窗口** — 系统标题栏（深灰配色）+ 官方图标，标题栏不占页面空间
- **提问面板** — 一键滑出全部提问历史，点击任意提问即可跳转定位
- **系统托盘常驻** — 关闭窗口自动收进托盘，服务在后台继续运行
- **自动拉起服务** — 检测 `dsh web` 未运行则自动启动，就绪后加载页面
- **连接状态提示** — 悬浮气泡显示连接中/失败原因，支持一键重试
- **跨 DPI 适配** — SystemAware，兼容远程桌面 / 虚拟显示器等会话环境

## 📦 下载

从 [Releases](https://github.com/Xiashano1/dsh-desktop/releases) 下载最新版 `DshDesktop.exe`。

自包含单文件（含 .NET 运行时），Win10/11 x64 双击即用，无需安装。

## 🚀 使用

```powershell
# 默认连接 http://127.0.0.1:3080
DshDesktop.exe

# 指定端口
DshDesktop.exe --url http://127.0.0.1:7456
```

> 前置要求：已安装 DeepSeek Harness（`dsh web`）。应用会自动拉起服务，也可先手动启动。

## 🔨 构建

需要 .NET 7 SDK：

```powershell
dotnet publish .\DshDesktop.csproj -c Release -o .\publish-v3
```

或直接双击根目录下的 `build.bat`。

## 🧱 技术栈

- .NET 7 · WinForms · WebView2

## 📝 日志

- 应用日志：`%TEMP%\DshDesktop.log`
- dsh 服务输出：`%TEMP%\DshDesktop-dsh.log`

## 📄 License

[MIT](LICENSE) © 2026 Xiashan
