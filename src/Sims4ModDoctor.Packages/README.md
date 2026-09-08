# Sims4ModDoctor.Packages

只读 DBPF 索引底座。整个解决方案里**只有这个项目**引用
`LlamaLogic.Packages`；`Core`、`Cli`、`Desktop` 都够不到它。

对外只有一个入口：`Sims4ModDoctor.Core.Packages.IPackageIndexReader`。

## 现在支持什么

- 打开 DBPF **2.1**（Sims 4 的版本），只读模式；
- 列出全部资源键（Type / Group / Instance）与它们在索引中的序号；
- 交给第三方库之前先做结构预检，见下；
- 结构化失败：每一种拒绝都有稳定错误码，可用于报告与测试匹配；
- 取消可传播：`OperationCanceledException` 原样向上抛，不会被转成失败结果；
- 读取前后核对文件戳，读取期间文件被改动则整份结果作废；
- 单个文件失败不影响同批其它文件。

### 预检拦得住什么

在任何 seek、分配或第三方调用之前，只读 header 与索引头共 100 字节：

| 错误码 | 拦的是 |
|---|---|
| `package-too-small-for-header` | 文件短于 96 字节 |
| `package-too-large` | 超出配置的体积上限 |
| `package-magic-mismatch` | 开头不是 `DBPF` |
| `package-unsupported-version` | 不是 2.1（Sims 2 的 1.1 也在此拦下） |
| `package-unsupported-index-version` | 索引版本不是 3，或位域用了三个受支持位之外的位 |
| `package-resource-count-exceeds-limit` | 声明的资源数超过上限 |
| `package-index-out-of-bounds` | 索引位置越界、与文件长度矛盾、或 32 位相加回绕 |
| `package-index-size-mismatch` | 索引长度不在「资源数 × 每条可能长度」的区间内 |
| `package-duplicate-resource-keys` | 索引里有重复的资源键（见下） |

后两条依赖索引的真实布局，有两处不能想当然：

**一、indexType 位域只有三位有效**（`0x01` Type、`0x02` Group、`0x04` Instance 高位）。
置位的字段从每条记录里提出来，只在索引头里存一次。
社区文档描述了 8 个位，但依赖的第三方库只实现这三个——其余位置位时它会忽略 flag
并按错误布局逐条读取，读出一堆看起来正常的假资源键，所以预检直接拒绝。

**二、每条记录的长度本身是可变的。** 固定 7 个 DWORD，
外加**可选**的 4 字节扩展压缩信息（Size 字段最高位是它的标志）。
因此索引长度只能校验区间，不能校验相等。

⚠️ 一批真实样本里恰好全部带扩展字段，不等于它必须带。

### 重复资源键会被拒绝

第三方库用有序 HashSet 与以资源键为键的字典存索引，同一个 TGI 出现两次就只剩一条。
本实现在读取后比对键数量与 header 声明的条目数，不一致时整份作废。

一个专门用来找重复资源的工具，不能在地基上把重复吃掉还报成功。
⚠️ 这是「暂时不支持」，不是「这个文件坏了」——将来若要支持，需要绕开该库的索引集合。

## 现在不支持什么

本轮只读索引，以下都**没有**实现，也不应假装已实现：

- 读取或解压 payload（zlib、RefPack/QFS 一概没碰）；
- TGI 倒排表、payload hash、任何冲突结论；
- `.ts4script` 分析；
- 缓存、SQLite、报告、UI、文件操作；
- 判断哪个 package 最终在游戏里生效。

### 两项带兜底的元数据

`PackageResourceEntry.ContentSize` 与 `Compression` 取自第三方库。
在只读打开的方式下，这两个 API 都只从**已解析的索引项**里取字段，
不读取也不解压资源内容。

但它们仍可能抛异常——例如索引里的压缩类型不在库的枚举内，映射时直接抛。
所以这里各自兜了一层：取不到就是 `null` / `Unknown`，
原始取值保留在 `CompressionRaw` 里。一条元数据拿不到，
不应该毁掉整个文件的资源键索引。

⚠️ 压缩模式到 `PackageCompression` 的映射依据的是库的枚举命名，
**尚未与 s4pe 对同一批资源逐项对拍**。

## 验证到哪一步了

自动测试（`tests/Sims4ModDoctor.Packages.Tests`、
`tests/Sims4ModDoctor.Core.Tests/Packages`）覆盖：

- indexType 位域三个有效位的每一种组合，以及无效位必须被拒绝；
- 每条带 / 每条不带 / 混着带扩展压缩字段；
- 索引位置写在 64 位字段或老式 32 位字段；重复资源键；空路径；
- 太小、magic 错、版本不符、索引越界、整数回绕、截断索引、
  伪造的巨大资源数、空索引自相矛盾；
- 取消传播、读取中途文件改动、批量中的单点失败；
- Unicode 与超过 260 字符的路径。

测试用的 package 全部由 `DbpfFixtureBuilder` 在测试期间生成，
仓库里不含任何真实玩家 Mod。

### 本机真实语料

`RealCorpusTests` 默认跳过。指一个真实 Mods 目录给它就会运行：

```bash
SIMS4_MOD_DOCTOR_REAL_MODS=/path/to/Mods dotnet test tests/Sims4ModDoctor.Packages.Tests
```

它回答自造 fixture 回答不了的问题：预检会不会把玩家正常的 Mod 拦下来。

**已跑过的一次：95 个真实 package（约 784 MB），全部通过，零误杀。**
那批语料里 18 个使用了公共常量字段（15 个 `0x02`、3 个 `0x03`），
因此「记录长度可变」这条路径确实被真实文件走到过。

### ⚠️ 还没验的

- 与 s4pe 逐项对拍 TGI、offset、stored size、memory size、compression；
- RefPack/QFS 与混合压缩样本；
- 大型合并 package 的吞吐与峰值内存；
- Windows 上的长路径行为（本轮测试在 Linux 上跑的，CI 会在 windows-latest 上复核）。
