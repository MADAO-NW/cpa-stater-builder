# CPA Windows 托盘构建器

CPA 是一个用于 CLIProxyAPI 的轻量级 Windows 托盘控制器。它可以启动和停止 `cli-proxy-api.exe`，通过彩色或灰色托盘图标显示运行或停止状态，并支持检查 GitHub Releases 中的更新。

本文件夹包含源代码和本地 Windows 构建脚本，方便用户自行编译 `CPA.exe`，无需信任预编译二进制文件。

## 文件说明

- `CPA-SingleFile.cs`：可审计的 C# WinForms 源码。
- `build-cpa.ps1`：适用于 Windows 10/11 的本地构建脚本。
- `assets/cpa.png`：托盘和应用图标源文件。
- `CPA.exe`：运行构建脚本后生成的构建产物。

## 环境要求

- Windows 10/11。
- 系统内置的 .NET Framework 4.x 编译器：
  - `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
  - 或 `C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe`

无需安装 Visual Studio，也无需使用第三方打包工具。

## 构建

在本文件夹中打开 PowerShell，然后运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-cpa.ps1
```

生成结果为：

```text
.\CPA.exe
```

构建脚本会在编译过程中于 `obj\` 目录下创建临时图标文件，并在退出前删除这些临时文件。

## 使用

将生成的 `CPA.exe` 放到 `cli-proxy-api.exe` 所在的同一目录，然后运行 `CPA.exe`。

托盘菜单为中文，包含：

- `启动 CPA`
- `停止 CPA`
- `重启 CPA`
- `检查更新`
- `更新到最新版本`
- `退出 CPA`

CPA 运行时，托盘图标为彩色；CPA 停止时，托盘图标为灰色。

## 注意事项

- 本项目不包含 `cli-proxy-api.exe`。
- 可从官方发布页面下载 CLIProxyAPI：https://github.com/router-for-me/CLIProxyAPI/releases
- 管理面板更新使用官方发布资产 `management.html`，来源为：https://github.com/router-for-me/Cli-Proxy-API-Management-Center/releases
