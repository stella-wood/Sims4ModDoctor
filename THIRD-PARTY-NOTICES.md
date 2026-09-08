# Third-party notices

## Runtime dependencies

`Sims4ModDoctor.Packages` 引用 `LlamaLogic.Packages` 作为只读 DBPF 索引底座。
该引用被限制在这一个项目内：`Sims4ModDoctor.Core`、`Sims4ModDoctor.Cli` 与
`Sims4ModDoctor.Desktop` 都不引用它，也不暴露它的任何类型。

### 直接依赖

- `LlamaLogic.Packages` 3.8.2 — MIT — https://github.com/Llama-Logic/LlamaLogic

### 传递依赖

`LlamaLogic.Packages` 自身带入以下包（版本取自
`src/Sims4ModDoctor.Packages/packages.lock.json`）：

| 包 | 版本 | 许可证 |
|---|---|---|
| `BCnEncoder.Net` | 2.2.0 | MIT OR Unlicense |
| `BCnEncoder.Net.ImageSharp` | 1.1.2 | MIT OR Unlicense |
| `CommunityToolkit.HighPerformance` | 8.4.0 | MIT |
| `Nito.AsyncEx` | 5.1.2 | MIT |
| `Nito.AsyncEx.Context` | 5.1.2 | MIT |
| `Nito.AsyncEx.Coordination` | 5.1.2 | MIT |
| `Nito.AsyncEx.Interop.WaitHandles` | 5.1.2 | MIT |
| `Nito.AsyncEx.Oop` | 5.1.2 | MIT |
| `Nito.AsyncEx.Tasks` | 5.1.2 | MIT |
| `Nito.Cancellation` | 1.1.2 | MIT |
| `Nito.Collections.Deque` | 1.1.1 | MIT |
| `Nito.Disposables` | 2.2.1 | MIT |
| `SharpZipLib` | 1.4.2 | MIT |
| `SixLabors.ImageSharp` | 3.1.11 | ⚠️ 见下方待确认项 |
| `System.Collections.Immutable` | 8.0.0 | MIT |
| `YamlDotNet` | 16.3.0 | MIT |

### ⚠️ 待确认：SixLabors.ImageSharp 3.1.11

该包的 nuspec 未声明 SPDX 许可证表达式，只带一个已弃用的 `licenseUrl`。
Six Labors 自 2.0 起改用自有的分离式许可条款，其中包含对商业使用的限制，
与其余依赖的 MIT 条款不同。

本项目**尚未**对该条款完成许可证决策。发布前需要确认：

1. 本项目的分发形态是否落在其免费许可范围内；
2. 若不在，是否可以裁掉这条依赖链
   （它来自 `LlamaLogic.Packages` 的图像解码能力，而本工具只读索引，不解码图像）。

在结论出来之前，不应据此对外发布二进制包。

## Test-only dependencies

- `MSTest` 4.3.3 — MIT — https://github.com/microsoft/testfx
- `Microsoft.NET.Test.Sdk` 18.9.0 — MIT — https://github.com/microsoft/vstest

These packages are used only to build and run the automated test projects; they are not runtime dependencies.
