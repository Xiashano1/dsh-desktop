@echo off
REM ============================================
REM  DshDesktop 一键重新构建
REM  需要：.NET SDK 7（dotnet --version 检查）
REM  产物：publish-v3\DshDesktop.exe（单文件）
REM ============================================
cd /d %~dp0
dotnet publish -c Release -o .\publish-v3
if errorlevel 1 (
    echo.
    echo [构建失败] 请确认已安装 .NET SDK 7，且网络可用（首次会下载 NuGet 包）。
    pause
    exit /b 1
)
echo.
echo [构建完成] publish-v3\DshDesktop.exe
pause
