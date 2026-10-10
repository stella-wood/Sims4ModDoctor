# Sims 4 Mod Sieve｜Agent 协作指南

正式产品名称为 `Sims 4 Mod Sieve`；工程目录、命名空间、程序集和可执行文件使用 `Sims4ModSieve`。

本文约束后续接手本项目的 Agent。目标不是让代码显得“完整”，而是在不破坏现有产品判断的前提下，持续交付可验证、可维护、不过度设计的功能。

## 1. 权威顺序与开始工作前的阅读

遇到冲突时，按以下顺序判断：

1. 用户当前明确表达的需求与纠正
2. 磁盘上正在运行的代码与测试
3. 本文件记录的稳定实现约束
4. `docs/product/产品需求文档.md`
5. `docs/architecture/架构方案-v3.md`
6. v2、调研稿与 spikes

开始修改前必须：

- 读取相关代码的当前版本，不凭旧截图或旧对话猜实现
- 检查工作树，保留用户和其他 Agent 的已有修改
- 先确定用户指出的具体对象、状态与影响范围
- 需求若只涉及一个位置，不擅自扩大为全局规则

### 1.1 工作范围与节省额度

- 一次只做用户指定的那一项任务。做完就停下汇报，不顺手做清单里的下一项，不顺手重构无关代码
- 先看下面第 2.1 节的项目地图，直接打开相关目录；用户已经给出文件或路径时，不再全仓库搜索
- 不为了「了解项目」通读整个仓库；只读本次任务涉及的文件，以及它们直接依赖的接口
- 改动前先用一两句话说明打算改哪些文件；范围明显超出任务时先问，不要先做
- 汇报写清：改了哪些文件、跑了哪些测试、结果如何、哪些没验证

## 2. 当前产品事实

这是 Windows 本地离线的《模拟人生 4》Mod 诊断工具，基于 .NET 8 与 WPF。

已经完成（重复文件检测，界面完整）：

- 多目录重复文件扫描
- 父目录与显式子目录的去重遍历
- 跳过 reparse point
- 按大小分桶与流式 SHA-256
- hash 前后稳定性校验与一次重试
- 稳定的重复组与建议保留规则
- WPF 首页、扫描范围、进度、取消、结果与选择交互
- 单文件、按组、一键建议、清空选择
- 全部展开/收起
- Windows 回收站删除、删除前复核、部分失败、最近一次撤回
- JSON 与 HTML 报告 CLI

已经完成（冲突检测底层，只有 Core / Packages，没有界面）：

- PR #1：只读 DBPF 索引读取与结构预检（`Sims4ModSieve.Packages`，见其 README）
- PR #2：同一 TGI 出现在多个 package 的候选冲突扫描（`Core/Conflicts/PackageConflictScanner`）
- PR #3：候选资源的受限读取、解压与内容哈希比较（`Core/Conflicts/ResourceContentComparer`、`Packages/DbpfResourceContentHasher`）；支持未压缩与 zlib，RefPack 返回结构化失败
- PR #4（审查中，合并前以磁盘代码为准）：`.ts4script` 脚本模块碰撞扫描（`Core/Scripts/ScriptModuleScanner`）

尚未完成：

- 冲突检测界面（首页卡片与导航仍为灰色「尚未开放」）
- 报错日志分析、二分排查、健康检查
- 缓存层
- 设置页（按钮在，内容空）
- 发布安装包 / 自动打包

不要把灰色入口、需求文档或 spike 写成已完成功能。

### 2.1 项目地图

```text
Sims4ModSieve.sln
├─ src/
│  ├─ Sims4ModSieve.Core/        业务逻辑，不依赖 WPF，也不引用任何第三方 package 库
│  │  ├─ Duplicates/              重复扫描、建议保留（DuplicateSelectionService）、来源发现与路径规则
│  │  ├─ Conflicts/               TGI 候选冲突扫描、资源内容比较、处理预算
│  │  ├─ Scripts/                 .ts4script 模块碰撞（PR #4）
│  │  ├─ Packages/                DBPF 预检与读取接口（IPackageIndexReader）
│  │  └─ Reporting/               JSON / HTML 报告
│  ├─ Sims4ModSieve.Packages/    唯一引用 LlamaLogic.Packages 的项目；索引读取 + 自写的资源内容读取
│  ├─ Sims4ModSieve.Desktop/     WPF 界面：MainWindow.xaml、ViewModels/、Services/（回收站、设置、日志）
│  └─ Sims4ModSieve.Cli/         命令行报告
├─ tests/
│  ├─ Sims4ModSieve.Core.Tests/      Core 单元测试（Linux 也能跑）
│  ├─ Sims4ModSieve.Packages.Tests/  DBPF 读取与真实样本测试
│  ├─ Sims4ModSieve.Desktop.Tests/   WPF 交互、命中区域、回收站（只能在 Windows 跑）
│  └─ fixtures/                       小型测试文件
├─ eng/dotnet.cmd                 固定 SDK 的 dotnet 包装
└─ .github/workflows/ci.yml       Windows CI
```

真实样本测试默认跳过，设置环境变量 `SIMS4_MOD_SIEVE_REAL_MODS=<Mods 路径>` 后运行（`--filter TestCategory=RealCorpus`）。
需求文档与架构方案在作者本机 `docs/`，未进仓库；仓库里没有时以本文件和磁盘代码为准。

## 3. 不可复制的业务规则

### 3.1 重复副本建议

`Sims4ModSieve.Core.Duplicates.DuplicateSelectionService` 是建议保留/删除策略的唯一权威：

1. Mods 内部优先保留
2. 同一范围内目录层级更深者优先
3. 仍并列时按稳定路径顺序选择

全局“一键勾选”和组级勾选必须调用同一份建议结果。不要在 ViewModel、XAML code-behind 或删除服务里重写判断。

UI 中建议保留项排在组内第一项展示，但不得改变 Core 的文件身份或 hash 结果。

### 3.2 组级选择状态

组复选框为选中状态的两种合法情况：

- 当前选择与 Core 的建议删除集合完全一致
- 用户手动选中了该组全部文件

不要因为用户选择“全部删除”就自动取消组复选框。用户可以覆盖建议。

### 3.3 删除与撤回

删除链路必须保持：

```text
ViewModel
  → IFileActionService
    → IRecycleBinAdapter
      → Windows IFileOperation
```

- UI 事件处理器不得直接调用文件删除 API
- 只处理用户当前明确勾选的真实文件
- 执行前再次核对长度与最后修改时间
- 只支持可靠的 Windows 回收站语义；不得退化为永久删除
- 部分成功必须与部分失败分别报告
- 撤回只覆盖最近一次批量删除
- 恢复时原路径已有同名文件，绝不覆盖
- 删除成功后重建当前重复组，建议策略仍调用 `DuplicateSelectionService`

删除确认窗是意图确认，不是说明页。当前批准的交互为：

- 原生标题栏不显示文字
- 正文只显示 `确定删除所选文件？`
- 按钮只显示 `否` 与 `是`
- 弹窗保持紧凑

文件数量、大小、完整路径、回收站说明、撤回说明和整组警告不得擅自重新塞回确认窗。预检仍在后台执行；只有失败时才告诉用户具体问题。

## 4. 结果会话与联动边界

扫描范围、扫描结果、选择状态、删除提示和撤回记录属于同一条结果会话，但“联动”不等于任何变化都清空一切。

当前批准的规则只有：

- 左侧来源列表变为空：清空右侧结果、选择、扫描状态、删除提示与撤回记录
- 用户开始新扫描：建立新结果会话，并替换旧会话
- 新增目录：保留现有结果
- 移除部分目录：保留现有结果
- 启用或停用目录：保留现有结果
- 扫描进行中：锁定来源复选框，避免扫描输入与页面状态错位

不要把新增、部分移除或启停目录擅自解释为“旧结果必须立刻失效”。用户可以先调整范围，再决定何时重扫。

新增任何带状态的功能前，先列出它与下列事件的关系：

- 首次进入页面
- 开始、取消、失败、完成扫描
- 来源新增、移除、清空、启停
- 单项、整组与批量选择
- 删除成功、部分失败、撤回成功、部分恢复
- 返回首页再进入

状态应接入现有生命周期入口，不要在不同按钮旁各养一套互不相识的布尔变量。

## 5. 界面与文字尺度

设计目标是：克制、温暖、精准、有工具感，但不冷。

稳定视觉基线：

- 页面底色：`#F6F5F2`
- 卡片：白色
- 主强调紫：`#6F5A91`
- 展开文件区：`#F3F1ED`
- 当前 Mods 提示区：`#EAE7E2`
- 分组虚线应可感知但不抢眼
- 大面积避免纯冷白、默认蓝和过度圆角

字体角色不能混成一种：

- 品牌：Bahnschrift
- 展示标题：等线
- 功能标题与按钮：Microsoft YaHei UI
- 数据、路径与轻量统计：Segoe UI Variable Text / 等线

文字规则：

- 标签和说明不写需求文档式长句
- 能用准确短语解决，就不补一段防御性解释
- 按钮说明通常不加句号
- 不重复表达页面标题、按钮动作与安全提示
- 不用“为了让用户放心”作为增加信息的默认理由
- 完整文件路径必须包含文件名；可以视觉截断，但 Tooltip 保留全文

主页响应规则：

- 内容区随窗口横向扩展
- 卡片宽高按既定比例变化
- 字号与图标尺寸保持不变
- 卡片内部使用比例分区，让图标、文字和按钮位置随卡片舒展
- 不使用 Viewbox 把整页当海报同比放大

结果页保持：

- 顶部导航稳定
- 左右工作区约 31:69
- 左侧只管理范围与扫描状态
- 右侧承担结果浏览与文件动作

## 6. 图标与点击区域

- 优先使用项目自绘的轻量矢量图标，不随意塞 emoji 或系统 Unicode 方块
- 批量展开/收起使用三条“手风琴线”，状态通过线距区分
- 图标视觉尺寸与点击热区分离
- 当前批量展开按钮：图标 `16×16`，命中区 `24×24`
- 图标层 `IsHitTestVisible=false`，完整矩形宿主负责命中
- 裸图标按钮的宿主背景与所在表面同色；视觉上无底色，但不能使用只有 Path 才命中的空模板

修改命中行为后，必须运行 `BareIconButtonHitTests`。不要只看 XAML 就宣布“整块可点”。

## 7. WPF 已知陷阱

1. 修改相似 Style 时，补丁必须带上准确的 `x:Key` 上下文。曾经因为通用模板片段命中了 `HeaderNavButtonStyle`，实际要改的 `BareIconButtonStyle` 完全没变。
2. 无背景的 `ContentPresenter` 可能只让可绘制 Path 参与命中。命中区域需要明确的矩形宿主。
3. `Run.Text` 绑定只读 ViewModel 属性时必须显式 `Mode=OneWay`。默认双向绑定曾导致确认窗口布局时抛出 `InvalidOperationException` 并终止应用。
4. WPF 测试需要 STA。一个测试进程中不要反复创建 `Application`；命中与弹窗渲染应放在同一离屏 WPF 测试中。
5. 正在运行的应用可能锁定 EXE/DLL。优先正常关闭；若无法关闭，可用项目内临时输出验证，但最终必须回写正式构建并清理临时目录。

## 8. 完成标准

“改完”至少意味着：

- 修改落在正确控件和正确代码路径
- 行为与用户描述一致，没有擅自扩大范围
- 编译 0 警告、0 错误
- 相关测试通过
- 高风险操作使用隔离临时文件验证，不碰真实 Mods
- GUI 修改实际启动后再交付

不要因为 `apply_patch` 成功或编译通过就宣布用户可见问题已经解决。点击区域、窗口渲染、删除与撤回都需要对应的行为证据。

## 9. 常用命令

```powershell
# 构建
.\eng\dotnet.cmd build .\Sims4ModSieve.sln -c Release --no-restore --nologo

# 完整测试
.\eng\dotnet.cmd test .\Sims4ModSieve.sln -c Release --no-restore --nologo

# 仅桌面交互测试
.\eng\dotnet.cmd test .\tests\Sims4ModSieve.Desktop.Tests\Sims4ModSieve.Desktop.Tests.csproj `
  -c Release --no-restore --nologo

# 明确授权后运行真实回收站往返测试；只使用自动创建的临时中文文件
$env:SIMS4_MOD_SIEVE_RUN_RECYCLE_BIN_TEST='1'
.\eng\dotnet.cmd test .\tests\Sims4ModSieve.Desktop.Tests\Sims4ModSieve.Desktop.Tests.csproj `
  -c Release --no-build --no-restore --nologo `
  --filter 'FullyQualifiedName~UnicodeFileCanRoundTripThroughWindowsRecycleBin'

# 运行桌面版
.\eng\dotnet.cmd run --project .\src\Sims4ModSieve.Desktop\Sims4ModSieve.Desktop.csproj -c Release
```

## 10. 代码来源

- 第三方代码只按其许可证使用；DBPF 格式参考 LlamaLogic（MIT），来源已写在 `THIRD-PARTY-NOTICES.md`
- 格式规则以源码或规范为依据；真实样本只能证伪，不能证明规则恒真（样本里没见过某种值，不代表不存在）

## 11. 与用户协作

- 用户说“这个颜色”“这个位置”时，先以截图标注和最近一句为准，明确是背景、文件区、按钮还是文字
- 用户纠正范围后立即放弃旧假设，不继续替旧方案辩护
- 少写“更安全、更完整”的自我解释；先交付她真正要求的尺度
- 不让用户反复承担点击、截图和定位本应由自动测试完成的问题
- 可以提出判断，但不得用架构或规范压过用户明确的产品决定
- 用户的苛刻不是噪音；真正需要避免的是 Agent 未验证便报告成功，以及从一次纠正跳到另一个极端
