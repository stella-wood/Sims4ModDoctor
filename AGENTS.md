# Sims 4 Mod Doctor · Agent 指南

Windows 本地离线的《模拟人生 4》Mod 整理工具，.NET 8 + WPF。计划改名为 Sims4ModSieve（尚未执行）。

## 1. 以什么为准

冲突时按顺序：

1. 用户当前的要求和纠正
2. 仓库里的代码与测试
3. 本文件
4. `docs/product/产品需求文档.md`
5. `docs/architecture/架构方案-v3.md`、`docs/design/UI设计风格规范-v1.md`
6. `docs/archive/`、`docs/research/`

## 2. 工作方式

- 一次只做指定的一项任务，做完停下汇报。不顺手做下一项，不顺手重构。
- 动手前用一两句话说明要改哪些文件、大概多少改动；明显超出任务时先问。
- 用户给了文件或路径，就直接打开，不全仓库搜索。不为「了解项目」通读仓库。
- 开始前看工作树，保留用户和其他 Agent 未提交的修改。
- 用户只说了一处，就只改那一处，不自行推广成全局规则。
- 汇报写：改了哪些文件、跑了哪些测试、结果、哪些没验证。

## 3. 写代码

- 改动尽量小，只改任务需要的地方。跟着周围代码的命名、写法和注释密度走。
- 防御只放在真正不可信的边界：读文件、解析 package／zip／日志、用户输入。内部调用之间不为不可能发生的情况加检查。
- 不新建抽象、接口或工具类，除非同样的代码已经出现三次。
- 不留注释掉的代码，不写复述代码本身的注释。注释只写「为什么」。
- 改了行为，同一个提交里更新相关文档（README、`docs/`、各项目 README）。

## 4. 写文档

文档写给开发者看：

- 写这个功能做什么、规则是什么、现在做到哪。
- 不写全文摘要、不写给文档自己的验收标准、不写替自己免责的句子。
- 同一条规则只在一个地方写，其他地方链接过去。
- 链接用仓库内相对路径，不写本机绝对路径。

## 5. 当前状态

- 重复文件检测：界面完整（扫描、选择、回收站删除、撤回、CLI 报告）。
- 冲突检测：底层完成，没有界面。
  - `Packages`：只读 DBPF 索引与结构预检
  - `Core/Conflicts`：同一 TGI 的候选冲突，资源内容读取、解压与哈希比较（未压缩、zlib；RefPack 返回结构化失败）
  - `Core/Scripts`：`.ts4script` 模块同名碰撞
- 未做：冲突检测界面、报错诊断、二分排查、禁用、设置页内容、打包发布、改名。

首页和导航里灰色的「尚未开放」不是已完成的功能。

## 6. 项目地图

```text
src/
├─ Sims4ModDoctor.Core/        业务逻辑，不依赖 WPF；第三方只用 SharpZipLib
│  ├─ Duplicates/              重复扫描、建议保留、来源发现与路径规则
│  ├─ Conflicts/               TGI 候选冲突、资源内容比较、处理预算
│  ├─ Scripts/                 .ts4script 模块碰撞
│  ├─ Packages/                DBPF 预检与读取接口
│  └─ Reporting/               JSON / HTML 报告
├─ Sims4ModDoctor.Packages/    唯一引用 LlamaLogic.Packages；索引读取 + 自写资源内容读取
├─ Sims4ModDoctor.Desktop/     WPF：MainWindow.xaml、ViewModels/、Services/
└─ Sims4ModDoctor.Cli/         命令行报告
tests/
├─ Sims4ModDoctor.Core.Tests/      Linux 也能跑
├─ Sims4ModDoctor.Packages.Tests/  含真实样本测试
├─ Sims4ModDoctor.Desktop.Tests/   只能在 Windows 跑（STA）
└─ fixtures/
```

真实样本测试默认跳过。设置 `SIMS4_MOD_DOCTOR_REAL_MODS=<Mods 路径>` 后用 `--filter TestCategory=RealCorpus` 运行。

## 7. 不能改动的业务规则

### 7.1 建议保留

`Core/Duplicates/DuplicateSelectionService` 是唯一权威：Mods 内优先 → 目录更深优先 → 稳定路径顺序。一键勾选和组勾选都调用它，不在 ViewModel、code-behind 或删除服务里另写判断。

组复选框显示选中的两种情况：当前选择等于建议删除集合；或用户手动选中了整组。用户选中整组时不要自动取消组复选框。

### 7.2 删除与撤回

```text
ViewModel → IFileActionService → IRecycleBinAdapter → Windows IFileOperation
```

- UI 事件里不直接调用删除 API；只删用户当前勾选的真实文件。
- 执行前再核对长度和修改时间；不支持回收站时不退化为永久删除。
- 部分成功和部分失败分别报告。
- 撤回只针对最近一次批量删除；原路径已有同名文件时绝不覆盖。
- 删除后重建当前重复组，建议仍由 `DuplicateSelectionService` 给出。

确认窗：标题栏无文字，正文只有 `确定删除所选文件？`，按钮只有 `否`、`是`。不要把数量、大小、路径、回收站或撤回说明加回去；预检在后台做，失败了再说明。

### 7.3 结果会话

| 事件 | 结果 |
|---|---|
| 来源列表清空 | 清空结果、选择、扫描状态、删除提示、撤回记录 |
| 开始新扫描 | 新建会话替换旧会话 |
| 新增、移除部分、启停来源 | 保留现有结果 |
| 扫描进行中 | 锁定来源复选框 |
| 返回首页再进入 | 保留现有结果 |

新增带状态的功能，接入现有会话生命周期，不在各个按钮旁边各养一套布尔变量。

## 8. 界面

视觉、尺寸、颜色、字体见 `docs/design/UI设计风格规范-v1.md`。最常被改坏的几条：

- 气质：克制、温暖、清楚，有工具感。不用默认蓝、大圆角、大面积渐变或玻璃效果。
- 文案用短语，按钮不加句号，不写防御性说明，不把标题、按钮和提示重复说三遍。
- 完整路径必须含文件名，可以截断显示，Tooltip 给全文。
- 首页随窗口舒展，字号和图标不放大；不用 Viewbox 整页缩放。
- 图标自绘矢量，不用 emoji 或 Unicode 方块。图标视觉尺寸和点击热区分开：图标 `IsHitTestVisible=false`，矩形宿主负责命中（批量展开按钮：图标 16×16，热区 24×24）。改命中后运行 `BareIconButtonHitTests`。

## 9. WPF 已知陷阱

1. 改相似 Style 时补丁要带准确的 `x:Key`。曾经改到了 `HeaderNavButtonStyle`，要改的 `BareIconButtonStyle` 没变。
2. 无背景的 `ContentPresenter` 只让 Path 参与命中，热区要用明确的矩形宿主。
3. `Run.Text` 绑定只读属性必须 `Mode=OneWay`，否则确认窗布局时抛 `InvalidOperationException`。
4. WPF 测试要 STA；一个进程里不要反复创建 `Application`。
5. 运行中的应用会锁 EXE／DLL，先关掉再构建。

## 10. 完成标准

- 改在正确的控件和代码路径上，没有扩大范围。
- 编译 0 警告 0 错误，相关测试通过。
- 高风险操作只用临时文件验证，不碰真实 Mods。
- 界面改动要实际启动看过。点击区域、渲染、删除与撤回要有行为证据，编译通过不算完成。

## 11. 常用命令

```powershell
.\eng\dotnet.cmd build .\Sims4ModDoctor.sln -c Release --no-restore --nologo
.\eng\dotnet.cmd test  .\Sims4ModDoctor.sln -c Release --no-restore --nologo
.\eng\dotnet.cmd run --project .\src\Sims4ModDoctor.Desktop\Sims4ModDoctor.Desktop.csproj -c Release
```

真实回收站往返测试见 README。

## 12. 代码来源

- 第三方代码只按许可证使用，来源写进 `THIRD-PARTY-NOTICES.md`。
- 格式规则以源码或规范为依据。真实样本只能证伪：样本里没见过某个值，不代表它不存在。

## 13. 与用户协作

- 用户说「这个颜色」「这个位置」，以截图标注和最近一句为准，先弄清是背景、文件区、按钮还是文字。
- 用户纠正后立刻放弃旧方案，不替它辩护，也不跳到另一个极端。
- 可以提判断，但不用架构或规范压过用户明确的产品决定。
- 不让用户反复点击、截图来定位本该由自动测试发现的问题。
- 没验证过的，不报告成已完成。
