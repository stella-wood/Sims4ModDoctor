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
| `SixLabors.ImageSharp` | 3.1.11 | Six Labors Split License 1.0（见下） |
| `System.Collections.Immutable` | 8.0.0 | MIT |
| `YamlDotNet` | 16.3.0 | MIT |

### SixLabors.ImageSharp 3.1.11 — Six Labors Split License 1.0

该包的 nuspec 未声明 SPDX 许可证表达式，只带一个已弃用的 `licenseUrl`；
Six Labors 自 2.0 起使用自有的分离式许可证，按使用形态在
Apache License 2.0 与商业许可之间二选一。

许可证原文：https://github.com/SixLabors/ImageSharp/blob/main/LICENSE

按其条款，满足以下任一条即适用 **Apache License 2.0**：

- 用于以开源或源码可得许可证发布的软件；
- **作为传递依赖被引入**；
- 作为直接依赖，且使用方是年营收低于 100 万美元的营利主体；
- 作为直接依赖，且使用方是非营利组织或注册慈善机构。

本项目同时满足前两条：仓库以 MIT 发布，且 ImageSharp 是经由
`LlamaLogic.Packages` 引入的传递依赖，不是直接依赖。因此按当前形态适用
Apache License 2.0，不构成许可证阻塞。

⚠️ 以下变化会使这一结论失效，届时需要重新评估：

- 本项目改为闭源或源码不可得；
- ImageSharp 变成直接依赖（例如为了图像解码而显式引用它）。

## Test-only dependencies

- `MSTest` 4.3.3 — MIT — https://github.com/microsoft/testfx
- `Microsoft.NET.Test.Sdk` 18.9.0 — MIT — https://github.com/microsoft/vstest

These packages are used only to build and run the automated test projects; they are not runtime dependencies.
