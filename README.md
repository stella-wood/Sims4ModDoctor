# Sims 4 Mod Doctor

[![CI](https://github.com/stella-wood/Sims4ModDoctor/actions/workflows/ci.yml/badge.svg)](https://github.com/stella-wood/Sims4ModDoctor/actions/workflows/ci.yml)

Sims 4 Mod Doctor 是一个面向 Windows 的本地离线《模拟人生 4》Mod 整理工具。

当前版本专注于可靠地发现并处理内容完全相同的重复文件。扫描和报告生成默认只读；只有用户明确勾选并确认后，桌面版才会把文件移入 Windows 回收站。

## 当前功能

- 扫描一个或多个目录中的 `.package` 和 `.ts4script` 文件
- 识别文件名不同但内容完全相同的副本
- 正确处理同时选择父目录和显式子目录的情况
- 跳过 reparse point，避免意外扩大扫描范围
- 先按文件大小筛选，再流式计算 SHA-256
- Hash 前后复核文件状态，避免使用扫描期间发生变化的内容
- 单个目录或文件读取失败时继续扫描其他内容
- 按稳定规则提供建议保留项和建议删除项
- 支持逐文件、按组和一键应用建议
- 将明确选择的文件移入 Windows 回收站
- 支持部分失败报告和最近一次删除撤回
- 通过 CLI 导出 JSON 和静态 HTML 报告

Mod 冲突检测入口目前尚未开放，当前版本不会分析 DBPF 资源覆盖关系。

## 系统要求

- Windows 10 或 Windows 11
- .NET SDK 8.0.424 或兼容的 .NET 8 SDK

仓库的 `global.json` 固定了开发所使用的 SDK 版本。`eng\dotnet.cmd` 会优先使用项目内的本地 SDK；本地 SDK 不存在时会自动使用系统 `dotnet`。

## 构建与测试

每次推送到 `main` 或提交 Pull Request 时，GitHub Actions 都会在 Windows 上执行锁定还原、Release 构建和普通测试。

```powershell
.\eng\dotnet.cmd restore .\Sims4ModDoctor.sln --locked-mode
.\eng\dotnet.cmd build .\Sims4ModDoctor.sln -c Release --no-restore --nologo
.\eng\dotnet.cmd test .\Sims4ModDoctor.sln -c Release --no-build --no-restore --nologo
```

真实 Windows 回收站往返测试默认跳过，因为它会对自动创建的临时文件执行一次回收站删除和恢复。明确需要验证时运行：

```powershell
$env:SIMS4_MOD_DOCTOR_RUN_RECYCLE_BIN_TEST='1'
.\eng\dotnet.cmd test .\tests\Sims4ModDoctor.Desktop.Tests\Sims4ModDoctor.Desktop.Tests.csproj `
  -c Release --no-build --no-restore --nologo `
  --filter 'FullyQualifiedName~UnicodeFileCanRoundTripThroughWindowsRecycleBin'
```

该测试只使用系统临时目录中的自动生成文件，不会访问真实 Mods 文件夹。

## 运行桌面版

```powershell
.\eng\dotnet.cmd run --project .\src\Sims4ModDoctor.Desktop\Sims4ModDoctor.Desktop.csproj -c Release
```

Release 构建输出位于：

```text
src\Sims4ModDoctor.Desktop\bin\Release\net8.0-windows\
```

上次使用的扫描来源保存在当前 Windows 用户的本地应用数据目录中，不会写入 Mods 文件夹。

## CLI

```powershell
.\eng\dotnet.cmd run --project .\src\Sims4ModDoctor.Cli\Sims4ModDoctor.Cli.csproj -c Release -- `
  scan-duplicates `
  --source "D:\Documents\Electronic Arts\The Sims 4\Mods" `
  --source "D:\Downloads\New Mods" `
  --mods-root "D:\Documents\Electronic Arts\The Sims 4\Mods" `
  --extension .package `
  --extension .ts4script `
  --json ".\artifacts\duplicate-report.json" `
  --html ".\artifacts\duplicate-report.html"
```

省略 `--extension` 时 CLI 会扫描全部文件。CLI 不执行删除或修改；存在未完成分析时仍会保存报告，并以退出码 `1` 结束。

## 项目结构

```text
src/
├─ Sims4ModDoctor.Core/          文件发现、Hash、重复分组和报告
├─ Sims4ModDoctor.Cli/           命令行扫描与报告入口
└─ Sims4ModDoctor.Desktop/       WPF 界面和 Windows 回收站适配

tests/
├─ Sims4ModDoctor.Core.Tests/    核心扫描与报告测试
└─ Sims4ModDoctor.Desktop.Tests/ ViewModel、文件动作和 WPF 交互测试

eng/
├─ dotnet.cmd                    统一 .NET 命令入口
└─ generate-brand-icon.ps1       应用图标生成脚本
```

## 安全与隐私

- 扫描、分析和报告生成全部在本机完成
- 不上传 Mod、文件路径或扫描报告
- 不执行 `.ts4script` 中的代码
- 不修改 package 内部内容
- 不提供永久删除降级路径
- HTML 报告会转义文件名、路径和错误文本
- 导出的报告可能包含本地完整路径，分享前请自行检查

## 第三方组件

依赖与许可证信息见 [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)。

## 许可证

本项目采用 [MIT License](./LICENSE)。
