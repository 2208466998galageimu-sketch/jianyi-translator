# Thanslator 技术设计文档

> 2026-11-25 / 0.7.0 技术补齐：
> - LanguagePack 校验清单、双向闭合、目录边界及哈希；ModelFileDownload 校验并续传，暂存文件不成为当前模型。原生 LocalMtHost 为旧 Marian 和 M2M100 分别处理分词、语言 token、EOS、输出与保护规则；不支持的已存方向不会使 LLM 模式或启动崩溃。
> - OCR Profile（ocr.json formatVersion=1 / paddle-ctc）声明检测图、识别图、字符表，只允许包内相对路径；OcrHostClient 使用程序内可信运行组件加载外部模型，字符维度验证及测试图通过才保存。四档上游为 PP-OCRv6 Tiny/Small/Medium 和 PP-OCRv5 Mobile。推荐文件来源、大小和哈希位于 Settings/ocr-models.json。
> - ModelArchive 对实际解压流设上限；模型导入暂存失败清理，原 ZIP/模型不改动，旧配置不覆盖。助手输出只作为经白名单验证的文件映射，不能执行模型代码或远程改写权重。
> - LLM 分句翻译为一次 JSON 结构化请求，对每单元编号、占位符及段落空白严格校验。局部调整只提交命中单元，模型不采用所选译法则保留旧结果。相应模型/接口需支持 JSON 输出。嵌入提示词的 JSON 保留可读 Unicode；这与 HTTP 外层 JSON 的正常转义不同。
> - KnowledgeMatches 保留全部有效候选及当前组合 ID；组合 preferred 只引用方向、标准化原词、库 ID 与词条 ID，来源失效即回退。knowledge_requests 只存请求编号、库和日期，用事务去重累计候选命中。
> - translation_preferences 为独立版本化状态及本机去重密钥，不与术语数据或模型凭据混存。收集只保存规则、方向、场景、日期、HMAC 标识；候选确认后才复制为启用规则。API Key 继续只在 Windows 凭据管理器。
> - 远程导入助手和主动偏好示例发送前显示确切目标及数据预览；取消不发送。逐词/锁序路径不接上述模型和偏好。


> 2026-11-21 补充确认：个人知识库是长期保存、可持续修改的术语总库，可按领域分库。固定译法的写入与单条增删改完全并入知识库，不再制作独立功能、入口或另一套数据；旧 personal_terms 原样保留，后续仅允许显式迁入。调用领域翻译仍需已配置的 LLM。

> 2026-11-21：主动 OCR 大图在主程序等比缩小至 1600×1000 且不超过 150 万像素，兼容现有 OCR 组件；识别框按实际横纵比例映射回屏幕坐标。密码区域检查忽略屏幕外控件，不再仅因区域外密码框持有焦点而拒绝；未知位置仍拒绝。

> 取消按词频触发的主动义项提示及计数。旧数据库中的历史记录和个人术语原样保留。领域知识库将按语言对和领域检索，通用词典仍提供备选释义。

> LLM API 和本机 LLM 共用兼容聊天请求。API Key 写入 Windows 凭据管理器，按接口地址关联。本机端点仅允许回环地址。

> 固定译法存于现有 SQLite 数据库的 personal_terms 表，按语言对和区分大小写的查询词保存。下游查词优先显示个人译法，句段译文继续使用现有模型和术语规则。

> OCR 通序翻译在识别时根据行位置合并可信英文软换行。校对区仍使用原始识别文本。明确的软连字符可去掉，普通连字符保留，用户校对后的文本优先送译。

> 手动框选使用单显示器、目标窗口与工作区交集的物理像素坐标，松手关闭遮罩后复用本地 OCR。结果复用常驻浮层，以控制条为锚点。

> 2026-11-17 最新决策：移除全局热键注册及无选区指向识别入口；设置页继续使用 SQLite 的 ocrEnabled 和已有 OCR 组件路径。保持自动识图的选区证据、隐私门、取消与本地处理规则。下文热键设计为历史记录。

> 文档版本：v0.1（草案）
> 日期：2026-11-07
> 状态：待确认
> 关联文档：[product.md](product.md) · [agents.md](agents.md)
> 研究基准日：2026-11-07（免费额度、许可证、运行时版本均会变化，发布前需复核）

---

## 1. 决策摘要

| 决策 | 结论 |
|---|---|
| 技术栈 | **C# / .NET 10 / WPF（Windows 原生）**，单人可维护、低占用、透明浮层可靠 |
| 备选方案 | Electron 45 + TypeScript + selection-hook（若 WPF 取词验证不通过，按第 3.5 节切换） |
| 取词链路 | UI Automation TextPattern → IAccessible2 → 剪贴板回退（用户显式开启）→ 全局快捷键 |
| 触发方式 | 选中文本后，鼠标在选区内停留 400ms（可调 200–1000ms） |
| 结果容器 | 一个无激活、置顶、半透明浮层窗口，Tooltip / Panel 两种状态 |
| 生硬直译 | **不调用机器翻译**；分词 + 词典逐词释义，严格保持原文词序 |
| 词典 | 本地 ECDICT SQLite（仓库 MIT，数据许可待复核；770,611 条，实际构建约 96MB，含索引）+ 人工校订覆盖表 |
| 句子翻译 | 可选下载的本地 MT（默认不启用、零 Key）优先；官方免费额度作为可选高质量源；非官方免密钥接口仅作为显式开启的兜底 |
| 设置存储 | 本地 SQLite，key-value + JSON，write-through；API Key 存 Windows Credential Manager |
| 目标语言 | 首版 `zh-Hans`；数据结构与接口使用 BCP-47，不把中文写死 |

**一句话结论**：不存在「无限免费 + 权威」的翻译 API。可落地的最优组合是：**本地词典负责单词/短语与逐词直译，官方免费额度负责通顺句子翻译，非官方免密钥接口只做明确标注的可选兜底。**

---

## 2. 设计目标与约束

### 2.1 必须满足

1. 选中文本后悬停触发，不选中不触发；不抢焦点、不污染剪贴板（默认不用剪贴板）。
2. 单词/短语给「常见释义」，句子/段落给通顺翻译；生硬模式逐词保持原序。
3. Tooltip 与 Panel 都锚定在选区下方，半透明，尺寸受控，屏幕边缘自动翻转。
4. 所有开关、模式、语言、位置、来源选择持久化，重启恢复。
5. 结果模型语言中立，新增语言对不需要改 UI 与数据库 schema。
6. 翻译源可替换、可降级、可缓存；不内置共享 API Key。
7. 隐私底线：不记录选中文本，不采集密码框，远程请求有告知。

### 2.2 明确不承诺

- 不对所有 Windows 应用保证取词成功（自绘界面、游戏、扫描 PDF、提权窗口存在客观限制）。
- 不保证任何免费接口永久可用、永久免费或符合服务条款。
- 首版不提供 OCR、全文翻译、浏览器扩展、macOS 版本。
- 生硬模式输出**允许不通顺**，这是产品语义，不是缺陷。

---

## 3. 技术选型

### 3.1 候选对比

| 方案 | 优势 | 劣势 | 结论 |
|---|---|---|---|
| **C# WPF / .NET 10** | 原生 UIA、透明分层窗口可靠、无 WebView2 依赖、内存约 50–100MB、自包含目录约 170MB（框架依赖更小）、系统托盘/热键/剪贴板 API 直接可用 | 取词、浮层、动画都要自己实现；XAML UI 开发速度中等 | **首选** |
| Electron 45 + TypeScript | `selection-hook` 已封装 UIA/IA2/剪贴板；Chromium 透明窗口成熟；React/CSS 开发快；托盘、热键、更新齐全 | 约 90–120MB 安装包、150–300MB 内存；`selection-hook` 只有四角坐标，多行命中靠近似；剪贴板回退仍可能干扰 | 备选 |
| Tauri 2 + Rust | 安装包 5–10MB、内存低、原生 Rust 能力 | WebView2 154+ 存在透明背景回归（#5752，截至 2026-11-07 仍 open）；Rust UIA COM 工作量大；没有现成的 selection-hook | 暂不采用 |
| Python + PySide6 | 原型最快；`uiautomation` 可用；Qt 浮层成熟 | PyInstaller/Nuitka 打包 60–150MB、易触发 AV；GIL/COM 线程复杂；PySide6 LGPL 合规 | 仅原型 |

### 3.2 为什么首选 WPF

1. **产品是 Windows-only 的常驻小工具**：WPF 直接调用 Win32/UIA，不需要为跨平台付出 Electron 的体积代价。
2. **核心 UX 是透明浮层**：WPF 的 `AllowsTransparency` + 分层窗口在本机验证路径清晰；Electron 也可行，但 Tauri 当前受 WebView2 透明回归影响。
3. **内存目标**：PRD 目标 ≤150MB；WPF 方案通常 50–100MB，Electron 常见 150–300MB。
4. **取词可自建**：UIA TextPattern 提供逐行矩形（`GetBoundingRectangles`），比 `selection-hook` 的四角坐标更适合多行选区命中。
5. **分发简单**：自包含 .NET 安装包 + 代码签名即可，不依赖 Node/Chromium/WebView2 版本。

### 3.3 WPF 方案的风险与缓解

| 风险 | 缓解 |
|---|---|
| UIA 跨进程调用可能挂起 | UIA 调用放独立无窗口 MTA 工作者；有界队列与超时等待，不无限替换线程；必要时拆独立 `SelectionHelper.exe`（ADR-025） |
| `AllowsTransparency` 的性能/阴影限制 | 窗口只渲染小面板；避免实时模糊；用半透明纯色 + 细边框 |
| 多显示器混合 DPI 定位复杂 | 全链路使用物理像素 + `PerMonitorV2`；窗口定位用 `SetWindowPos` |
| 无现成 selection-hook | 按第 6 章实现 UIA/IA2/剪贴板三级链路；先做技术验证门 |
| 全局鼠标钩子可能被 AV 误报 | 不注入进程；签名；钩子回调极短；提供轮询兜底；提交 Defender 信誉 |

### 3.4 技术验证门（M1 必须通过）

在写业务功能前，用 1–2 天做一个最小 Spike，验证以下场景：

- [ ] Chrome / Edge 中选中单词/多行文本，UIA 能读到文本与逐行矩形；
- [ ] 记事本、VS Code、Word、Acrobat（有文本层 PDF）至少各有一个可用取词路径；
- [ ] 透明、无边框、置顶、不抢焦点的 WPF 窗口能在选区下方正确出现；
- [ ] Tooltip 点击穿透、Panel 关闭按钮可点击且不激活窗口；
- [ ] 100% / 150% / 200% 缩放与双屏混合 DPI 下位置正确；
- [ ] 关闭总开关后，鼠标钩子不产生任何查询。

**未通过的处理**：保留 `Core`、`Providers`、`Storage`、`Dictionary` 层，替换 UI/取词层为 Electron + `selection-hook`；不要同时维护两套。

### 3.5 备选方案（仅在验证门失败时启用）

- Electron 45.2.3 + TypeScript + React。
- `selection-hook` 2.1.2 放在 `utilityProcess`，不要放主进程。
- 一个预创建的透明 `BrowserWindow` 承载 Tooltip/Panel；Tooltip 用 `setIgnoreMouseEvents(true, { forward: true })`，Panel 关闭穿透。
- 设置仍用 SQLite；通过 `screen.screenToDipRect` 转换坐标。
- 该方案会超出 150MB 内存目标，需在 PRD 中重新确认。

---

## 4. 总体架构

### 4.1 模块图

```text
┌─────────────────────────────────────────────────────────────┐
│                        Thanslator.App                        │
│  WPF Shell / Tray / Popper / Settings / OverlayWindow        │
│  TriggerController（9 状态机）  ViewModels                    │
└───────────────┬─────────────────────────────┬───────────────┘
                │                             │
┌───────────────▼──────────────┐  ┌───────────▼───────────────┐
│      Thanslator.Capture      │  │   Thanslator.Translation   │
│  UIA / IA2 / Clipboard       │  │  Classifier / Glosser      │
│  InputPoll / Hotkey          │  │  Orchestrator / Cache      │
└───────────────┬──────────────┘  └───────────┬───────────────┘
                │                             │
┌───────────────▼──────────────┐  ┌───────────▼───────────────┐
│      Thanslator.Dictionary   │  │    Thanslator.Providers    │
│  ECDICT SQLite / Overrides   │  │  Azure / Google / Baidu    │
│  Tokenizer / Lemma / Phrase  │  │  Alibaba / Keyless / LocalMt│
└───────────────┬──────────────┘  └───────────┬───────────────┘
                │                             │
                │                  ┌──────────▼───────────────┐
                │                  │ Thanslator.LocalMtHost   │
                │                  │ C++ / CTranslate2        │
                │                  │ named pipe, on demand    │
                │                  └──────────────────────────┘
┌───────────────▼─────────────────────────────▼───────────────┐
│                     Thanslator.Storage                       │
│   SQLite: settings / cache / app_profiles / provider_accounts│
│   Credential Manager: API keys                               │
└──────────────────────────────────────────────────────────────┘
```

### 4.2 分层与依赖方向

```text
App ──> Capture ──> Core
 │        │
 ├──────> Translation ──> Dictionary, Providers
 │              │
 └──────> Storage
Core 不依赖 App / Capture / Providers；Providers 只依赖 Core 的接口与模型。
```

- `Core`：纯领域模型、枚举、接口，不引用 WPF/Win32。
- `Translation`：文本分类器、生硬直译器、Provider 编排、缓存协调。
- `Capture`：Windows 取词、鼠标输入轮询、热键、选区快照。
- `Dictionary`：本地词典查询、分词、词形还原、短语匹配、校订覆盖。
- `Providers`：HTTP 翻译适配器 + `LocalMtProvider`（通过命名管道调用 `LocalMtHost`）；每个适配器独立、可单测、可在 mock 下运行。
- `Storage`：SQLite、迁移、设置仓储、缓存、密钥封装。
- `App`：WPF 窗口、状态机、ViewModel、托盘、Popper、设置界面。

### 4.3 建议项目结构

```text
Thanslator.sln
src/
  Thanslator.App/                WPF 主程序、Tray、Popper、Overlay、Settings
  Thanslator.Core/               领域模型、接口、错误码
  Thanslator.Translation/        分类器、直译器、Provider 编排、缓存协调
  Thanslator.Capture/            UIA/IA2/剪贴板/钩子/热键
  Thanslator.Dictionary/         ECDICT 访问、分词、lemma、短语、覆盖表
  Thanslator.Providers/          Azure/Google/Baidu/Alibaba/Keyless/LocalMt/LLM
  Thanslator.Storage/            SQLite、迁移、设置、缓存、凭据
  Thanslator.Tests/              xUnit 单元测试与集成测试
tools/
  DictionaryBuilder/             从 ECDICT CSV 构建 SQLite 的脚本/工具
  LocalMtHost/                   C++/CTranslate2 本地 MT 辅助进程（可选组件）
  LocalMtBuilder/                模型下载、转换、量化、打包与 hash/许可清单
assets/
  dictionary/                    dict-en-zh.sqlite（构建产物，勿手改）
  overrides/                     function-words.json, polysemy.json, phrases.json
  licenses/                      THIRD_PARTY_NOTICES.md
docs/                            可选的长文档；根目录保留 product/design/agents
```

---

## 5. 核心数据模型

所有语言使用 BCP-47 标签；首版规范化为 `zh-Hans`，同时接受 `zh-CN` 作为别名映射到提供商参数。**任何模型都不得把中文作为唯一字段。**

```csharp
public enum TextKind { Word, Phrase, Sentence, Paragraph }
public enum TranslationMode { Fluent, Literal }
public enum SurfaceMode { Tooltip, Panel }

public sealed record LanguageTag(string Value); // "en", "zh-Hans", "ja", "fr"

public sealed record TextSpan(int Start, int Length)
public sealed record RectD(double Left, double Top, double Width, double Height); // physical px

public sealed record SelectionSnapshot(
    string Text,
    string? DetectedLanguage,
    IReadOnlyList<RectD> LineRects,
    nint ForegroundHwnd,
    string ProcessName,
    SelectionCaptureMethod Method,
    long CapturedAtMs);

public enum SelectionCaptureMethod { UiaTextPattern, IAccessible2, Clipboard, Hotkey }

public sealed record TranslationRequest(
    string Text,
    string SourceLang,          // "auto" 或 BCP-47
    string TargetLang,          // 默认 "zh-Hans"
    TranslationMode Mode,
    bool IncludePhrases,
    string? ProviderId,
    string? Model,
    string GlossaryVersion);

public sealed record GlossUnit(
    string SourceToken,
    string Gloss,
    string? PartOfSpeech,
    bool IsUnknown,
    bool IsPunctuation);

public sealed record Sense(
    string PartOfSpeech,
    string? Domain,
    IReadOnlyList<string> Glosses,
    int Rank,
    string Source);             // "ecdict" / "curated" / "kaikki"

public sealed record TranslationResult(
    int SchemaVersion,
    string Mode,                // "dictionary" / "fluent" / "literal"
    LanguageInfo Source,
    LanguageInfo Target,
    IReadOnlyList<Sense> Senses,
    IReadOnlyList<GlossUnit> Units,
    string? Text,
    ProviderInfo Provider,
    long LatencyMs,
    bool Cached);

public sealed record LanguageInfo(string Lang, string Dir); // Dir: ltr/rtl
public sealed record ProviderInfo(string Id, string DisplayName, bool IsOfficial);
```

**约束**：

- `TranslationResult` 必须同时能表达「多义项词典结果」「逐词直译结果」「整句译文」。
- `Sense.Glosses` 是目标语言的字符串列表；新增目标语言只增加数据与语言包，不改结构。
- `Units` 保留原文 token 与释义的对应关系，供生硬模式渲染与复制。
- `SchemaVersion` 用于缓存兼容；升级时旧缓存失效。

---

## 6. 取词与触发

### 6.1 取词链路

```text
鼠标抬起（拖动 > 4–6px）或空闲 250–300ms
        │
        ▼
┌───────────────────────┐
│ 1. UI Automation      │  TextPattern.GetSelection
│    TextPattern        │  TextRange.GetText / GetBoundingRectangles
└──────────┬────────────┘
           │ 不支持/为空
┌──────────▼────────────┐
│ 2. IAccessible2       │  IAccessibleText.get_selection/get_text
│    IAccessibleText    │  get_characterExtents
└──────────┬────────────┘
           │ 不支持
┌──────────▼────────────┐
│ 3. 剪贴板回退（可选）  │  Ctrl+Insert → Ctrl+C；备份/恢复；仅用户开启
└──────────┬────────────┘
           │ 失败
┌──────────▼────────────┐
│ 4. 全局快捷键         │  Ctrl+Shift+D：立即取当前选区并翻译
└───────────────────────┘
```

### 6.2 UIA 细节

- 在独立无窗口 MTA 工作者执行 UIA，不在主 UI 线程读取；剪贴板另用 STA。依据[微软 UIA 线程建议](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)（ADR-025，取代旧 STA 决策）。
- `AutomationElement.FromHandle(GetForegroundWindow())` 或 `ElementFromPoint(cursor)`。
- 取 `TextPattern`：先检查 `SupportedTextSelection`；`GetSelection()` 返回多个不连续 range；每个 range `GetText(-1)`、`GetBoundingRectangles()`。
- 密码字段：检查 `AutomationElement.IsPassword`（`UIA_IsPasswordPropertyId = 30019`）及其祖先；为真则跳过，不读取、不缓存。
- Chromium 首次 UIA 请求可能因无障碍桥接激活而失败：100–300ms 后重试一次。
- 缓存 `SelectionSnapshot`，以 `HWND + 文本 + 矩形` 为指纹；滚动/窗口移动/缩放/前台变化时失效。
- 不认识的元素/没有 TextPattern：返回 `Unsupported`；`NoSelection`、`Forbidden`、`Timeout`、`Canceled` 不进入剪贴板。聚焦/光标及祖先优先，命中即返回；后备遍历最多访问 96 节点/700ms，独立工作者等待上限 2 秒、队列容量 1。单次 COM 调用仍可能挂起，等待超时不等于中断，不创建无限替代线程。
- 密码/前台/桌面/提权与内置 denylist 检查先于读取；无法确认隐私边界时拒绝回退。自动触发以真实行矩形及 8px 容差判断；无可靠几何只允许热键显示。一次 120ms 延迟重试仅适用于不支持且无选区证据。

### 6.3 IAccessible2 回退

以下为后续设计，2026-11-10 第一批没有实现 IA2；当前可执行链为 UIA → 用户已启用的受控剪贴板/主动热键。

- `AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, IID_IAccessible)` → `QueryInterface(IAccessibleText)`。
- `get_nSelections` / `get_selection` / `get_text`；`get_characterExtents(IA2_COORDTYPE_SCREEN_RELATIVE)` 获取逐字矩形。
- 性能低于 UIA，只在前者失败时使用；Office 一般不走 IA2，走剪贴板或 COM。

### 6.4 剪贴板回退（默认关闭，用户显式开启）

**为什么默认关闭**：模拟 `Ctrl+C` 会进入剪贴板历史、可能与剪贴板管理器冲突，且恢复只能是「尽力而为」，无法保证零干扰。

流程：

1. 共享隐私门允许、前台未变、UIA 未明确报告无选区，且用户开启回退；自动触发还需选区证据，热键可以主动尝试。
2. 在独立 STA 工作者枚举并预先复制可安全保存的 HGLOBAL 格式：文本、HTML/RTF、DIB/DIBV5、文件列表及部分图片编码；遇到未知/OLE/CF_BITMAP 句柄格式、读取失败或总量超过 32MiB，停止，不发送复制。
3. 等待修饰键松开；普通窗口 `Ctrl+C` 最多 800ms，再尝试一次 `Ctrl+Insert` 最多 700ms；已知终端仅用 `Ctrl+Insert`。每步复查前台及取消。
4. 只有剪贴板序号变化且所有者属于目标进程时读取，结果只驻内存；不能仅凭剪贴板有文字就认为成功。
5. 恢复前在打开的剪贴板锁下再次比较序号与所有者；不同则不清空用户的新内容。恢复失败显示固定提示，仍属尽力而为，不能消除剪贴板历史/管理器记录，也无法区分同一目标进程的同时复制。
6. 记录 `SelectionCaptureMethod.Clipboard`，不记录原文；复制结果无精确几何时仅热键显示。总等待上限 3 秒，底层操作取消不保证能撤回已发送的按键。

**禁止**：密码管理器、银行客户端、提权窗口、安全桌面、远程桌面凭据界面。当前实现内置常见密码管理器/系统凭据进程 denylist；用户编辑 denylist 的设置界面尚未实现。不能据此声称覆盖所有银行或自绘密码界面。

### 6.5 鼠标输入监听与热键

**2026-11-08 起改为轮询（ADR-014）**：`WH_MOUSE_LL` 在本项目的实际运行环境里出现过「钩子安装成功（`SetWindowsHookEx` 返回非零、`err=0`）、消息循环正常、但回调一次都不触发」的情况，且 Windows 对超时回调是**静默摘除**、不通知安装者，属于不可观测失败，不适合作为唯一触发源。

- 轮询：专用后台线程每 **8ms** 调用 `GetCursorPos` + `GetAsyncKeyState(VK_LBUTTON)`，采样交给纯逻辑类 `MouseInputTracker` 判定 Move / LeftDown / LeftUp。
- 坐标是屏幕物理像素；移动事件按 `MoveThrottleMs = 16` 节流，避免把每个采样都推给 UI 线程。
- 首个采样只建立基线，不产生伪 `LeftDown`；同一采样既移动又按键时先发 Move 再发按键；`Reset()` 丢弃基线（关闭/重开总开关、切换触发器时调用）。
- 静止开销：每 8ms 两次轻量系统调用（无锁、无分配、无 UI 交互）；`Wheel` 事件仅为接口稳定保留，轮询路径不会产生。
- 失败可观测：线程记录 `input poll heartbeat`（每 500 次采样一条，仅在诊断模式下写日志）、`poll failed err=`、`poll crashed`，并由状态栏提示「鼠标监听启动失败；Ctrl+Shift+D 仍可用」。
- 历史实现（保留供参考）：`WH_MOUSE_LL` 装在带消息循环的专用线程，回调只入队、立即 `CallNextHookEx`。若将来需要滚轮或极低延迟，可在**确认钩子可用**的环境重新启用，并与轮询做能力探测后二选一。
- 全局热键 `Ctrl+Shift+D`：`RegisterHotKey`，用于无法自动取词的应用；`WM_HOTKEY` 到专用线程。F12 保留，不占用。

**0.6.7 故障修订（ADR-029）**：实际日志出现 `GetCursorPos` 失败 `err=5` 后采样永久停止。暂不可访问改为每 250ms 等待重试，只在进入暂停时发一次失效事件，取消选区/翻译并重置鼠标与 Esc 基线；恢复后重新建立基线，不合成跨桌面的点击，不切换到安全桌面。正常轮询仍为 8ms，关闭总开关/退出会中断等待。热键注册增加 `MOD_NOREPEAT`，避免长按重复触发后把刚显示的浮层关闭；显式失败与忙碌使用已有 toast，注册失败不被启动就绪文案覆盖。仅诊断模式记录接收、状态、是否有选区几何及 generation，不记录文字。UIA 的祖先检查/候选祖先链统一最多 128 层，避免 16/12 层误拒绝深层控件；跨进程调用仍受原有 2s 等待和有界工作队列约束。修订验证边界见 [故障记录](docs/capture-fix-2026-11-10.md)。
- 2026-11-10：`CaptureInvalidationMonitor` 用 WinEvent 监听前台/桌面/窗口位置/选区/滚动开始事件；Raw Input 仅检查鼠标垂直/水平滚轮标志以补充轮询，失效时取消旧请求。Esc 只读取按下沿，不记录全局键盘字符。相关事件由应用提供，未暴露事件的自绘界面仍需兼容性验证。[微软 Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input)
- UIPI：提权窗口前台时，低权限进程的钩子/`SendInput`/UIA 都可能失效；这是系统限制，不做绕过，UI 明确提示「当前窗口不支持」。

### 6.6 悬停状态机

状态：

```text
Disabled ──enable──> Idle ──selection captured──> Armed
Armed ──cursor inside selection──> Dwelling
Dwelling ──dwell elapsed──> Loading ──result──> VisibleTooltip | VisiblePanel
Visible* ──dismiss──> Cooldown ──cooldown elapsed──> Idle
Loading/Visible* ──error──> Error ──dismiss/retry──> Idle
```

关键时序（默认值，均可配置）：

| 参数 | 默认 | 说明 |
|---|---|---|
| 选区捕获防抖 | 120–150ms | 鼠标抬起后延迟捕获，避免选区尚未稳定 |
| 拖动阈值 | 4–6px | 小于阈值视为点击，不取词 |
| 命中容差 | 4 DIP | 鼠标在选区矩形外少量范围仍算命中 |
| 停留触发 | 400ms | 可调 200–1000ms |
| 移动重置 | 8 DIP | 鼠标在原地抖动超过 8 DIP 重置 dwell |
| Tooltip/Panel 间桥接 | 250ms | 从选区移向容器时保持可见 |
| 本地词典加载态最短显示 | 200ms | 避免快速命中造成闪烁 |
| 关闭后冷却 | 600–800ms | 防止同位置反复弹出 |
| 同选区重触发 | 需离开再进入 | 指纹抑制；Esc 关闭后必须离开 |
| Tooltip 离开后隐藏 | 400ms | 指针离开「选区 + 容器」后 |
| Panel 自动关闭 | 0（关闭） | 默认不自动关闭；可配置 5–60s；计时从最后交互开始；悬停暂停；最后 5s 可显示倒计时 |

事件与规则：

- 鼠标在选区内移动：不重新请求；只有锚点矩形变化才重新定位。
- 鼠标从选区移向容器：250ms 桥接，期间不关闭；容器是可交互 Panel 时同样适用。
- `Esc`、点击其他位置、滚动、选区变化、前台窗口变化、锁屏：立即关闭。
- `Esc` 关闭后记录指纹，除非离开选区/选区变化，否则不再自动弹出。
- 加载中选区变化：递增 generation，丢弃旧响应；不得让旧结果覆盖新选区。
- 关闭总开关：清空缓存快照、取消进行中的请求、停止钩子/轮询。

### 6.7 DPI 与多显示器

- 进程清单声明 `PerMonitorV2`；`SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` 包住 UIA 调用。
- UIA `GetBoundingRectangles` 实测按物理像素返回；`GetCursorPos`/窗口 API 也统一按物理像素处理，最后只在 WPF 布局处换算 DIP。
- 使用 `MonitorFromRect` + `GetMonitorInfoW().rcWork`，不是整个屏幕；任务栏、负坐标显示器都要正确。
- 窗口定位优先直接用 Win32 `SetWindowPos`（物理像素）；WPF 内部布局按 DIP，通过每显示器 DPI 换算。
- 处理 `WM_DPICHANGED`；窗口跨屏时重新计算锚点与尺寸。

---

### 6.8 本地屏幕 OCR（0.6.6）

- 使用现有 MinerU 4.0.5 快照的 PP-OCRv6 ONNX 推理子集；对同图实测后选定该后端，不同时引入多个引擎。独立可选 `Jianyi.OcrHost` 包含运行时/模型/字符表，发行版不依赖 RAG 绝对路径。默认关闭，不自动下载/启用。
- `SelectionCaptureService` 产出原文或失败状态/选区证据，Core `OcrCapturePolicy` 决定是否允许；App 保持 dwell/热键与 generation。`ScreenOcrCapture` 只做有限截图和行块选择，不调用翻译。UIA 可读原文优先，禁止/超时/取消/忙碌不回退。无选区仅主动热键允许，不能从普通悬停进入。
- 截图按物理像素限制在前台客户区与当前显示器工作区，最多 1200×700；可信选区增加白边并只保留选区行内像素。截图前后校验前台、桌面、提权及遮挡；其他应用遮挡拒绝，简译自身窗口区域遮罩。GDI 区域截图不是原子窗口隔离，快速变化画面仍需实机验证，不宣称完全消除竞态。
- 光标须命中检测行，按固定种子行约束同栏相邻行，遇空行间隔停止，指向入口最多 16 行；不采用引擎的跨栏全图顺序。低分行不丢弃；空识别行保留替代占位并提示。不能检测出来的文字无法凭空补全，代码符号必须校对。
- 主进程与自建隐藏子进程使用 UTF-8 JSON lines/继承管道传递内存 PNG，限定像素、字节、行数、文本长度、分数和坐标；无监听端口，无截图文件/原文日志。CPU 4 线程，关闭 ORT 遥测，禁止 Python 网络连接；请求超时 30s，取消终止本实例子进程，空闲 5 分钟回收。原文编辑只在常驻面板主动点击时激活，翻译沿用原模式。
- 本机组件约 231MiB，单独模型约 22.94MB；冷启动/端到端样本并不等于模型热推理耗时，详细数据与验证边界见 [第二批记录](docs/second-batch-2026-11-10.md)。模型卡/许可证见 `tools/OcrHost/README.md`；MinerU 附加条款与第三方声明随组件保留。

## 7. 文本分类与路由

### 7.1 分类规则

优先使用 UAX #29 分词（.NET 可用 ICU4N；MVP 英文也可用正则 + 连字符/撇号规则）。分类器输出 `TextKind` + 置信度 + 原因，阈值放在版本化 JSON 中，便于按语言调整。

**拉丁文字默认**：

| 类型 | 规则 |
|---|---|
| Word | 1 个 word-like token |
| Phrase | 2–8 个 token，≤64 字符，无句末标点（`. ! ?` 等） |
| Sentence | 9–60 个 token，或含句末标点，≤400 字符，无换行 |
| Paragraph | 含换行，或 >400 字符，或 >60 token |

**CJK 默认**：

| 类型 | 规则 |
|---|---|
| Word | ≤4 个汉字/字符 |
| Phrase | 5–12 个字符 |
| Sentence | 13–100 个字符，或含句末标点 |
| Paragraph | 含换行，或 >100 个字符 |

- 模糊时默认 `TextKind=Sentence`；翻译模式仍按用户选择（`literalMode=true` 时仍走逐词），不能因分类失败把模式改成 Fluent。
- P1 允许用户在 Panel 中「按单词/短语/句子重新处理」。
- 分类失败不得阻塞：默认 `TextKind=Sentence`，模式不变。

### 7.2 模式路由矩阵

| 选区类型 | 普通模式（Fluent） | 生硬模式（Literal） |
|---|---|---|
| 单词 | 本地词典：常见义项，按词性分组 | 同左；不调用 MT |
| 短语 | 短语词典/习语优先；无结果则 MT | `includePhrases=true`：逐词直译；`false`：只查短语词典/习语，未收录则提示，**不调用 MT** |
| 句子 | MT，语序/语法/逻辑通顺 | 逐词直译，严格原序，不重排 |
| 段落 | 按句切分后 MT，保留段落结构 | 逐词直译，保留换行/标点 |

> **需求解读**：本设计把「短语」定义为 2–8 个单词、但不构成完整句子的组合（超过按句子处理）。生硬模式下是否把短语也逐词处理，由下拉菜单控制。
>
> **生硬模式的无 MT 边界**：逐词路径绝不调用机器翻译。`includePhrases=false` 且选区为短语时，走的是「短语词典查询」这一显式例外，不是逐词路径；词典未收录就提示切换普通模式或开启「包含短语」，绝不回退 MT。若产品本意不同，只需改路由矩阵，不改底层管线。

---

## 8. 翻译与词典管线

### 8.1 普通模式

```text
SelectionSnapshot
   │
   ▼
TextClassifier ──Word/Phrase──> DictionaryService ──> DictionaryResult
   │                                   │ 无结果
   │                                   ▼
   └──Sentence/Paragraph──> TranslationOrchestrator
                                   │ localMtEnabled && model ready?
                                   ├── yes ──> LocalMtProvider ──> LocalMtHost (pipe)
                                   └── no  ──> ProviderChain (remote, 若已配置)
                                                   │
                                                   ▼
                                            Cache (hit?) ──> Result
```

- 单词：只查词典，不把 MT 当词典；词典无结果时可显示「未收录」，P1 可回退单次 MT。
- 短语：精确匹配短语表（最长 5–6 token，词形还原后匹配）；优先短、小写、无领域标签的条目；习语/短语动词可用带领域标签的条目，但降权显示。
- 句子/段落：若本地 MT 已启用且模型就绪，优先本地 MT；否则使用已配置的远程源；两者都不可用则提示下载本地模型/配置官方源，不自动调用非官方接口。
- 本地 MT 与远程 MT 都按句切分、保留段落换行、按原顺序拼接。
- 普通模式绝不输出逐词拼接结果。

### 8.2 生硬直译模式

**核心原则：逐词路径不调用任何机器翻译。** 机器翻译 API 都会重排语序、补全省略、做形态变化，无法满足「按原文词序逐词对应」。

算法：

```text
1. 规范化：NFC、去零宽字符、保留原始空白/换行/标点
2. 分词：UAX #29 word boundaries
   - 合并无空格连接的连字符复合词（state-of-the-art）
   - 去掉所有格 's / ’s 后查询
   - URL、文件路径、代码标识符、版本号作为不可译 token
3. 逐 token：
   a. 标点/空白 → 作为 token 保留位置；显示层可按目标语言映射句末标点（如 `.`→`。`）
   b. 人工校订功能词表（a/an/the/of/to/and/is/...）→ 固定释义
   c. 人工校订多义词覆盖表 → 首选义项
   d. ECDICT 词形还原（exchange 字段）→ 词典义项
   e. 仍无 → 保留原词，标记 unknown（虚线下划线/灰色）
4. 按原 token 顺序输出 GlossUnit 列表
5. 不合并习语、不调整语序、不补主语、不润色
```

**短语语义**：

- 用户选中短语且 `includePhrases=true`：同样逐 token 处理，例如 `break the ice` → `break(打破) the(这) ice(冰)`，而不是「打破僵局」。
- 用户选中短语且 `includePhrases=false`：只走短语词典/习语，例如「打破僵局」；**词典未收录则提示未收录，不调用 MT**（可提示切换普通模式或开启「包含短语」）。
- 句子/段落：始终逐 token；不因 `includePhrases` 而把习语合并成一个整体。该选项只影响「被选中的短语」这一入口。
- 因此，`includePhrases=false` 的短语是「短语词典查询」的显式例外，不属于逐词路径；两条路径都不调用 MT。

**未知词处理**：保留原文并标记，不编造释义；可选调用在线词典 API（如 Azure Dictionary Lookup）补单词释义，但**不得**调用句子 MT。

**输出形态**：

- Tooltip：原文小号灰字一行 + 逐词释义序列，按原文顺序；过长时截断并显示省略号，提示「切换 Panel 模式查看完整结果」；**不提供点击展开**（Tooltip 点击穿透）。
- Panel：两列表格「原文 token | 释义」，标点单独成列或附在相邻 token；未知词虚线；支持复制为 `token（释义）` 文本。
- 标点位置不丢失；句末标点可按目标语言本地化（如 `.`→`。`），但原文 token 保留，复制时可选择保留原文标点。
- 复制时保留原文顺序与换行；不得输出「润色后」的句子。

**示例**：

```text
原文: The quick brown fox jumps over the lazy dog.
释义: The(这) quick(快速的) brown(棕色的) fox(狐狸) jumps(跳) over(越过) the(这) lazy(懒惰的) dog(狗).
```

不要求「这快速的棕色狐狸跳过了懒狗」这样的通顺结果——那是普通模式的职责。

### 8.3 词典查询

**数据源**：ECDICT 精简 SQLite（见第 9.5 节）。

查找顺序：

1. 规范化 key：去首尾空白、统一弯引号、保留大小写用于专有名词判断；
2. 单词：精确匹配 → 去所有格 → 词形还原索引（`exchange` 字段）→ 规则还原（-s/-es/-ed/-ing/-er/-est、不规则表）；
3. 短语：精确匹配 → 词形还原后最长匹配（≤6 token）→ 中断于标点；
4. 覆盖表：先查人工校订（功能词、top 多义词、短语动词/习语），再查 ECDICT；
5. 义项解析：ECDICT `translation` 行按 `\n` 分行，按 `n.`/`vt.`/`vi.`/`a.`/`adv.` 等前缀分词性；去掉 `[医]`/`[化]`/`[计]` 等领域标签（或折叠为小标签）；拆逗号/分号；去重；跳过「X 的过去式/过去分词/复数」等词形注释；
6. 排序：人工覆盖 > POS 上下文（若可用）> ECDICT 原始顺序 + 词频；Tooltip 最多 3 组，Panel 最多 6 组；
7. 每词性组显示 1–3 个近义中文，用「；」分隔；音标可选。

**已知数据问题**：ECDICT 的首个释义不一定是现代常用义（如 `fine` 先给「罚款」、`set` 先给「日落」、`take off` 先给「拿掉」）。因此必须有：

- 约 100 个功能词人工表（a/the/of/to/is/are/was/were/can/may/will/...）；
- 约 300–500 个高频多义词覆盖表；
- 约 500–1000 个短语动词/习语覆盖表；
- 这些覆盖表是**产品资产**，需要中文编辑复核，不能自动生成。

**词性上下文**：MVP 不做完整 POS tagger；按 ECDICT 前缀分组展示 + 覆盖表优先。P1 可引入轻量 POS 标注（如 ML.NET/规则），用于选取更可能的词性组。

### 8.4 分词与词形

- 英文：UAX #29；合并连字符复合词；保留 `don't`/`dog's` 的撇号；数字+单位（`3.5GHz`）作为不译 token；URL/路径/代码标识符跳过。
- 中文（未来源语言）：ICU 字典分词或 jieba-rs；不要用纯空格切分。
- 词形：优先 ECDICT `exchange`（构建后约 5.9 万条有效 form）；精确词条只有“X的过去式/复数”等语法注释时回退到 lemma；所有格 `'s` 先剥离；不要使用许可不明的 `lemma.en.txt`。
- 短语：ECDICT 有 36.6 万多词条目；精确选择匹配可全表查，句子内联匹配只用人工校订的常见习语表（避免技术名词误匹配）。

### 8.5 Provider 编排

```csharp
public interface ITranslationProvider
{
    string Id { get; }
    ProviderCapabilities Capabilities { get; }
    Task<ProviderTranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct);
}

public interface IDictionaryProvider
{
    string Id { get; }
    Task<DictionaryLookupResult> LookupAsync(
        DictionaryLookupRequest request, CancellationToken ct);
}
```

编排规则：

- **不是每个请求都走 MT**：单词/短语先词典；生硬模式只走词典/直译器。
- 本地 MT 优先：`localMtEnabled=true` 且模型 `ready` 时，句子/段落先走 `LocalMtProvider`；失败后再按用户配置的远程链降级。
- 远程 Provider 链：用户配置的主源 → 备用源 → 错误；`fallbackProviderIds` 为空时，自动使用其他已配置且支持该语言对的官方源，不尝试未配置 Key 的源；每步有超时（连接 3s，总 8s，可按源调整）、取消、一次重试（仅网络/5xx）；不重试 4xx/额度错误。
- 熔断：连续失败 3 次，暂停该源 60s；恢复后试探。
- 缓存：普通模式的远程译文与本地 MT 译文可缓存；生硬模式与本地 ECDICT 查询结果不写缓存（直译结果含原文 token，且本地重算足够快）；远程 30 天、本地 MT 7 天；失败不写负缓存（或短 TTL 30s）。
- 结果标注：`ProviderInfo.IsOfficial` 与 `DisplayName` 必须进入结果，UI 显示「官方 / 非官方 / 本地」。
- 不内置共享 Key；Key 不存在时引导配置；本地 MT 未安装且无远程源时，句子翻译提示「下载本地模型 / 配置官方源」，不自动走非官方接口。

### 8.6 缓存键

```text
cache_key = SHA-256(canonical_json{
  text, source_lang, target_lang, mode, include_phrases,
  provider_id, model, prompt_version, glossary_version, schema_version
})
```

- 任何影响输出的参数都必须进入 key（包括 `includePhrases`）；否则模式切换会命中错误缓存。
- 在线词典结果 TTL 90 天；MT 结果 30 天；可配置；支持 stale-while-revalidate。
- **生硬模式结果不缓存**：`GlossUnit.SourceToken` 含原文 token，写缓存等于保存选中文本；本地重算足够快。
- 仅缓存远程 Fluent 译文与在线词典结果；`result_json` 默认不含原文；历史记录另设且默认关闭（见第 12 章）。

---

## 9. 翻译与词典来源

### 9.1 结论：没有「无限免费 + 权威」

截至 2026-11-07 的核实结果：

- 没有任何提供商提供**无限、免费、权威**的翻译 API。
- 官方免费额度是存在的，但都需要注册/密钥，且有月度上限；部分需要实名或信用卡验证。
- 免密钥的 Google/Edge/MyMemory 等接口要么是**非官方**（违反 ToS、可能被封、无 SLA），要么额度极小。
- 「权威性」在词典层面靠**可追溯的词典数据 + 人工校订**；在句子层面靠**官方 API**；非官方网页接口不能宣称权威。
- 本地 MT 可以做到零 Key、离线，但质量低于云端，且有模型体积/硬件成本；它解决「免费/隐私」，不解决「权威」。

### 9.2 推荐组合（按优先级）

| 场景 | 首选 | 备用 | 说明 |
|---|---|---|---|
| 单词/短语释义 | 本地 ECDICT + 校订表 | 在线词典（可选） | 离线、低延迟、可解释 |
| 生硬逐词直译 | 本地词典 + 功能词表 | 在线词典补 OOV | **不使用 MT** |
| 普通句子/段落 | **本地 MT（可选下载，默认不启用）** | 官方 API（用户配置：Azure/Google/百度/阿里） | 零 Key、离线；质量低于云端；官方源作为高质量备选 |
| 中国大陆网络 | 百度高级版 / 阿里云 | Azure/Google（如可访问） | 国内访问稳定，需实名 |
| 免密钥试用 | Google gtx / Edge（非官方）、MyMemory（官方小额度） | 无 | 有本地 MT 后不再作为默认兜底；仅显式开启 |
| 隐私/离线 | 本地词典 + 本地 MT（已下载） | — | 完全不出本机；未下载模型时句子翻译不可用 |
| LLM 增强（可选） | Groq 免费层 / Gemini / Ollama | — | 适合长句润色/上下文，不宣称权威 |

**默认策略（2026-11-07 确认）**：单词/短语默认走本地 ECDICT + 校订表；应用**不预配置远程翻译源**；首次需要句子/段落翻译时，优先引导下载轻量本地 MT（约 100–300MB，零 Key），也可选择配置官方免费 Key 或继续仅本地；非官方免密钥源必须用户显式开启。默认状态下不发任何网络翻译请求，也不自动下载模型。

### 9.3 官方免费额度（发布前必须复核）

| 提供商 | 免费额度（2026-11 研究） | 是否需要 Key/验证 | 中文 | 备注 |
|---|---|---|---|---|
| **Azure Translator F0** | **200 万字符/月**；200 万/小时；单请求 5 万字符 | 需要 Azure 订阅 + Key + Region；注册通常要信用卡验证（不扣 F0 费用，来源有冲突） | `zh-Hans` 支持 | **最推荐的「免费 + 官方」选项**；有 Dictionary Lookup 与 alignment |
| **Google Cloud Translation v2** | 每月 $10 赠金，约 **50 万 NMT 字符/月**（$20/百万）；超出计费 | 需要 GCP 项目 + 计费账户 + API Key | `zh-CN` 支持 | 质量高；无硬性消费上限，Key 泄露有账单风险 |
| **百度翻译高级版** | **100 万字符/月**（标准版 5 万/月） | 需要 AppID/密钥；高级版需中国个人实名 | 英中质量强 | 国内访问稳定；超额 ¥49/百万字符 |
| **阿里云机器翻译** | **100 万字符/月** | 需要 AccessKey；需实名 | 英中质量强 | 国内访问稳定；签名较复杂 |
| DeepL API | 新用户 Developer 为 **100 万字符一次性**，不再重置；旧 Free 50 万/月已停售 | 需要账号/Key | `zh-Hans` 支持 | 欧洲语言强，英中相对一般；不适合作为长期免费主力 |
| 腾讯云 TMT | 历史 500 万/月，2026-10-01 起停止发放新免费包，文本翻译逐步下线 | — | — | **不要新接入** |
| 有道 | 新账号 ¥50 体验金，非循环免费额度 | 需要 Key | 中文好 | 不适合作为长期免费承诺 |

> 额度、计划名、注册要求变化频繁；上表是设计依据，不是对用户的永久承诺。产品内应显示「额度以服务商为准」，并提供一键测试/切换。

### 9.4 免密钥接口（官方小额度 + 第三方非官方，仅显式开启）

| 接口 | 现状 | 风险 | 用途 |
|---|---|---|---|
| Google `translate_a/single?client=gtx` | 2026-11-07 实测可用；支持 `dt=bd` 词性义项；0.7–2.4s | 违反 Google ToS；可能 429/403/IP 封禁；响应结构可能变 | 无 Key 试用的主兜底 |
| Microsoft Edge `translatetext` | 2026-11-07 实测可用；旧 auth 端点已失效 | 未文档化、易变、可能被封锁 | 第二兜底 |
| MyMemory | **官方免费 API（Translated 运营）**；匿名 5,000 字符/天，带邮箱 50,000/天；单请求 500 字节 | 额度小、延迟可达 10s；ToS 禁止爬取/转售/多段请求；**截至 2026-11-07 的条款未要求署名或商业付费计划，但以官网为准** | 短文本第三兜底 |
| LibreTranslate 公共实例 | 2026 年大多已失效；官方托管需 Key | 公共实例不可靠 | 仅自托管 |
| Lingva / Yandex 免密钥 / DeepLX | 多数已失效或需自托管/Key | 不稳定、ToS/商标风险 | 不作为默认 |
| 其他中文网页接口（腾讯 Transmart、火山、阿里网页、有道、金山） | 部分可用 | 签名/加密/CSRF，周级变化；ToS 风险 | 不纳入首版 |

**策略**：

- 首次启动向导中，免密钥模式（无论接口是否官方）必须由用户**主动勾选**；第三方非官方接口显示「非官方、可能失效、可能违反服务条款」。
- 免密钥源默认关闭；不作为唯一来源；请求限流（≥500ms 间隔）、重试 1 次、缓存优先。
- UI 结果页标注「官方 / 第三方非官方」；第三方接口不宣称权威。
- 公开发布时，建议默认提供本地词典 + 可选本地 MT + 官方源；非官方源作为用户自担风险的实验选项。

### 9.5 词典数据

| 数据 | 方向 | 规模 | 许可证 | 结论 |
|---|---|---|---|---|
| **ECDICT** | EN→ZH | 770,611 条；CSV 66MB；实际构建 SQLite 约 96MB（含 `word_lower`/forms 索引） | 仓库 MIT（Copyright 2025 Linwei）；**数据无单独许可证**；含 cdict（曾标 GPL）与来源不明的《屌丝字典》 | **M2 已采用**；保留 MIT 声明；商业分发前做法律复核，或替换高风险来源 |
| **Kaikki / Wiktionary 中文释义** | EN→ZH | 49,491 个英文词，10.4MB 抽取；覆盖 top-5k 的 86.9% | CC BY-SA 4.0 + GFDL 1.1+ | v0.2 可选数据包；独立文件，保留署名与 ShareAlike |
| CC-CEDICT | ZH→EN | 125,369 条 | CC BY-SA 4.0 | 未来 ZH→EN 模式；不是 EN→ZH 主源 |
| Princeton WordNet 3.0 | EN | 117,659 synset | WordNet license（宽松，可商用） | 可用于英语词性/词义先验；无中文 |
| Chinese Open Wordnet | EN↔ZH | 42,300 synset | WordNet-style | 覆盖稀疏，不作为主源 |
| OpenHowNet / BabelNet | — | — | 非商业/不清晰 | **不打包** |
| Free Dictionary API / StarDict 数据 | EN | — | 代码 GPL，数据来源不明/各词典单独许可 | **不打包** |

**ECDICT 使用注意**：

- 当前 CSV 的 `pos` 列**全为空**；词性需从 `translation` 行前缀解析，不能依赖 `pos` 字段。
- 音标覆盖：全库 28.3%，top-5k 约 98.5%；适合显示常见词音标。
- 短语 36.6 万条，质量参差；精确选区匹配可用全表，句子内联匹配只用人工表。
- `collins` / `oxford` 字段只用于内部排序，**不要在 UI 显示 Collins 星标/Oxford 徽章**（品牌与数据库权利风险）。
- 许可证风险：仓库 LICENSE 是 MIT，但 README 未单独声明数据许可；cdict 旧页面标 GPL，屌丝字典来源不明。个人使用风险低；闭源商业分发需法律意见或替换数据。
- 若未来公开到 GitHub：代码可独立选择 MIT/Apache-2.0；**不要把 ECDICT CSV 或来源不明的词典数据直接提交到仓库**，优先提供 `DictionaryBuilder` + 下载/hash 校验脚本；若在 Release 分发词典包，附 `LICENSE`/`THIRD_PARTY_NOTICES` 并说明数据许可风险。

### 9.6 词典包构建

```text
ECDICT CSV
  │  tools/DictionaryBuilder
  ├─ 规范化 word / phonetic / translation / bnc / frq / exchange
  ├─ 解析 POS 前缀、领域标签、词形注释
  ├─ 构建 form → lemma 索引
  ├─ 构建 phrase 表（多词条目）
  ├─ 合并 assets/overrides/*.json（人工校订优先）
  └─ 输出 assets/dictionary/dict-en-zh.sqlite（只读，带 version/hash）
```

- 构建产物带 `schema_version`、`source_hash`、`built_at`；应用启动校验。
- 覆盖表独立于 ECDICT，便于更新与审计；每条覆盖记录 `source="curated"` 和编辑日期。
- 词典 DB 与用户设置 DB 分离；升级词典只替换只读文件，不动用户设置。
- `THIRD_PARTY_NOTICES.md` 与应用内「许可证」页列出 ECDICT/Kaikki/CC-CEDICT 等署名。

### 9.7 生硬模式为什么不用 LLM

- LLM 可以按提示逐词输出，但可能合并 token、改写标点、漏词、产生幻觉；不可复现，且免费层有配额/隐私问题。
- 本地词典 + 规则是确定性的，符合「按原文顺序逐词对应」的产品定义。
- LLM 可作为 v0.2 的「增强直译」可选实验，但必须与默认直译路径隔离，并在 UI 标注。

### 9.8 本地离线 MT（A 方案）

目标：不配置 API Key、不联网，也能翻译普通句子/段落；模型**默认不下载**，用户显式安装后才启用。

**实现状态（2026-11-07）**：Host 已编译并通过命名管道验证；WPF 已通过 `LocalMtProvider` 接入，普通句子可显示译文；托盘「本地 MT 设置」可选择模型目录、导入模型 ZIP、删除模型。已加入源词条件术语表（`translation-glossary.json`），修正 race conditions、legacy、architecture、token、rate limit 等高频错译。模型包尚无默认公开托管地址，一键 URL 下载接口已实现（URL + SHA-256），200–500 条质量评估与安装包仍未完成。

**首选模型**

| 组件 | 选择 | 许可 | 说明 |
|---|---|---|---|
| 翻译模型 | OPUS-MT en→zh（Marian） | 原始模型 README 标 CC-BY 4.0，需复核并署名；不是 Apache-2.0 | 质量中等，适合阅读辅助；技术术语可能不准 |
| 推理运行时 | CTranslate2（int8 量化） | MIT | CPU 可跑；需在项目启动前验证 OPUS-MT 转换链路 |
| 分词 | SentencePiece | Apache-2.0 | 随模型提供 |
| 备选 | Argos Translate en→zh | 代码 MIT/CC0；模型包许可需逐个确认 | 体积约 100MB/语言对；质量略低，但集成简单 |

**不采用**：NLLB-200（CC BY-NC，非商用）、来源不明的“英汉大词典”模型、把 LLM 作为默认翻译路径。

**进程架构**

```text
Thanslator.App (WPF)
   │  named pipe: \\.\pipe\Thanslator.LocalMt
   ▼
Thanslator.LocalMtHost.exe (C++ / CTranslate2 + SentencePiece)
   │
   ├─ 按需加载模型（首次请求约 1–3s）
   ├─ 句子/段落翻译（int8, CPU；GPU 可选但不默认）
   └─ 空闲 5 分钟退出，释放内存
```

- 主程序不直接引用 CTranslate2，避免原生库崩溃拖垮 UI；Host 崩溃后自动重启一次。
- 通信使用命名管道 + JSON lines（请求：text、sourceLang、targetLang、modelId；响应：translation、latency、error），不监听公网端口，不触发防火墙。
- Host 与模型均放在便携数据目录 `data\`（默认 `<仓库根>\data` 或 `<exe 目录>\data`，可用 `THANSLATOR_HOME` 覆盖）；模型目录包含 `model.bin`、`config.json`、`LICENSE`、`SHA256SUMS`。
- 主安装包可包含原生 Host；实测 CPU-only CTranslate2 + Ruy 后端：`ctranslate2.dll` 约 7.2MB、Host 约 448KB，合计约 7.7MB；模型（int8）约 81–86MB。早期 wheel 里 61MB 的 DLL 不适用于自建 CPU-only 版本。
- 当前原生构建使用 Ruy 后端，`ComputeType` 用 `FLOAT32` 加载 int8 权重（该 CPU-only 构建不支持 `INT8_FLOAT32` 计算类型）；实测短句约 275–422ms（32 线程 CPU、4 线程配置），后续可调优。
- Spike 确认 `ct2-opus-mt-converter` 可从 OPUS-MT 原始发布包转换，HF 快照缺少 `decoder.yml`；语言标记 `>>cmn_Hans<<` 在 SentencePiece 之后加入；beam 4 + 多候选 + CJK 过滤可修复乱码候选。

**下载与安装**

1. 设置页显示「下载本地翻译模型（约 100–300MB，零 Key）」；
2. 用户点击后才开始下载；显示进度、速度、剩余时间；可取消；
3. 下载完成校验 SHA-256 与签名清单；失败可重试/删除；
4. 安装后 `localMtModelStatus=ready`，默认仍不自动启用，用户打开 `localMtEnabled`；
5. 删除模型时停止 Host、删除目录、恢复 `not_installed`。

**质量与性能预期**

- 现代 4 核 CPU：短句约 0.5–3s，长段落按句切分后逐句翻译；首次加载模型额外 1–3s。
- 质量低于 Azure/Google，可能直译、术语不准、长句结构生硬；UI 标注「本地 MT」，不宣称权威。
- 支持语言对以模型为准；无对应模型的语言对提示不可用，不回退非官方接口。

**失败与降级**

- 模型未安装：`localmt.not_installed` → 提示下载或配置官方源；
- Host 启动/推理失败：`localmt.inference_failed` → 重启一次；仍失败则提示；
- 本地 MT 失败且用户配置了官方源：按 provider 链降级到官方；**不自动降级到非官方接口**；
- 本地 MT 成功时不发任何网络请求；缓存规则见 8.6/12.3。

**许可与分发**

- OPUS-MT/CTranslate2/SentencePiece 的许可证与 NOTICE 随模型包和应用内许可证页提供。
- 模型文件不提交到 Git 仓库；GitHub Release 可作为可选下载资产，附 `LICENSE`/`SHA256SUMS`。
- 若改用 Argos，必须逐个确认模型包许可，不能只依赖代码 MIT/CC0。

---

## 10. 结果容器与交互

### 10.1 两种表面

> 用户可见名称：**悬注**（内部 `SurfaceMode.Tooltip`）/ **常驻**（内部 `SurfaceMode.Panel`）。

| 特性 | Tooltip | Panel |
|---|---|---|
| 用途 | 快速看单词/短语/短句 | 长句、段落、逐词对照 |
| 交互 | 只读、点击穿透、无按钮 | 可点击、关闭叉号、复制、滚动 |
| 焦点 | 永不获取 | 默认不获取；用户点击按钮时可获取 |
| 窗口样式 | `WS_EX_NOACTIVATE + TOOLWINDOW + TOPMOST + TRANSPARENT` | 去掉 `TRANSPARENT`，其余相同 |
| 关闭 | 指针离开 400ms / Esc / 滚动 | 关闭叉号 / Esc / 点击外部 / 选区变化 / 可选自动关闭 |
| 自动关闭 | 离开后 400ms；最长可见 10s | 默认不自动关闭；可选 5–60s，悬停暂停 |
| 尺寸 | 最大 360×240 DIP | 宽 420–480 DIP，高 ≤ 工作区 60% |
| 内容 | 最多 3 组释义 / 短译文 | 最多 6 组释义 / 全文滚动 / 双列直译 |

> 注意：`WS_EX_TRANSPARENT` 的窗口收不到鼠标事件，因此 Tooltip **不能**同时做「点击固定/点击展开」。如果以后要点击固定，改为点击 Popper 或全局热键，或者去掉穿透并接受鼠标事件。

### 10.2 几何与外观

- 锚点：选区所有行矩形的并集；水平居中于并集；下方 8 DIP 间距。
- 下方空间不足 `容器高度 + 16 DIP` → 翻转到上方；两侧都不够 → 限制高度并滚动，不画出屏幕。
- 限制在 `MonitorFromRect` 的 `rcWork` 内，留 8 DIP 边距；不要用全屏 bounds（会盖任务栏）。
- 背景：深色品牌渐变卡片 `#282A36` 92% → `#1A1C26` 94%（浅色主题镜像为白色渐变）；文字对比度 ≥ 4.5:1；高对比度主题改为不透明系统色 + 2px 边框。
- 圆角 10–12 DIP；1px 半透明边框；轻阴影；不要实时模糊（分层窗口性能差）。
- 入场：120ms 淡入 + 4–8px 上移；尊重系统「减少动画」设置。
- 内容宽度变化时重新测量并重新锚定；不覆盖选区。
- 多显示器：使用物理像素定位；窗口跨屏时按目标屏 DPI 重新布局。

### 10.3 内容布局

**单词（Tooltip/Panel）**：

```text
┌──────────────────────────────┐
│ run                    /rʌn/ │
│ v.  跑；运行；经营            │
│ n.  奔跑；一段路程            │
│ 本地词典 · ECDICT            │
└──────────────────────────────┘
```

**短语（普通模式）**：

```text
break the ice
打破僵局；打破沉默
本地短语词典 · 非直译
```

**句子（普通模式）**：

```text
敏捷的棕色狐狸跳过了那只懒狗。
[官方源] Azure · 320ms
```

**生硬直译（Panel）**：

```text
The     这
quick   快速的
brown   棕色的
fox     狐狸
jumps   跳
over    越过
the     这
lazy    懒惰的
dog     狗
.       。
[生硬直译] 本地词典 · 未做语序调整
```

- 生硬模式 Tooltip 只显示一行原文 + 一行释义序列；放不下时截断并显示省略号，提示「切换 Panel 模式查看完整结果」；**不提供点击展开**（Tooltip 点击穿透）。
- 未知词：灰色 + 虚线下划线；不显示伪造释义。
- 标点：单独显示或附在相邻词；位置不丢失；句末标点可按目标语言本地化（如 `.`→`。`），原文 token 保留。
- Panel 底部显示来源、模式、延迟、缓存标记；非官方源用醒目但克制的标签。
- 复制：Panel 提供「复制译文」；生硬模式复制 `token（释义）` 按行排列，保留顺序。

### 10.4 加载、错误与空状态

| 状态 | 表现 |
|---|---|
| 加载中 | 80–120ms 后显示骨架；本地词典命中至少显示 200ms，避免闪烁 |
| 词典未收录 | 「词典未收录」+ 原文；可在 Panel 中查询在线词典（若配置） |
| 语言不支持 | 「暂不支持 en → xx」+ 设置入口 |
| 网络失败 | 先自动切换备用源；全部失败显示「翻译服务暂不可用」+ 重试 |
| 额度/限流 | 「当前来源额度不足/被限流，已切到备用源」；不显示原始 HTTP 错误 |
| 取词失败 | 默认不弹；调试模式显示「当前应用不支持自动取词，可尝试 Ctrl+Shift+D」 |
| 数据库错误 | 启动提示；内存默认值；不阻塞取词/翻译 |

### 10.5 可访问性

- `Esc` 全局关闭（Panel 内也可用）。
- 浮层在 UIA 树中暴露为 `UIA_IsPeripheralPropertyId = TRUE` 的 Pane/ToolTip；用 `UiaRaiseNotificationEvent` 通知 Narrator/NVDA，不移动焦点。
- Tooltip 只读、不可聚焦；Panel 的关闭/复制按钮可键盘访问（当用户主动点击进入时）。
- 高对比度模式：不透明背景、系统色、2px 边框；不依赖颜色区分状态。
- 字号随系统缩放；避免固定像素字体。

---

## 11. 常驻入口与设置

### 11.1 Popper 小图标

- 无边框、置顶、不抢焦点；默认位于主屏右上角。
- 支持自由拖动：按住非控件区域（标题/空白/文字）即可拖动。
- 屏幕边缘抽屉：拖到工作区边缘 24 DIP 内或越过边缘时收起为 28×64 DIP 边条；点击边条从侧边滑出；顶部「方框 + 右箭头」按钮可主动贴边收起。
- 完全隐藏：`—` 按钮隐藏整个 Popper（包括边条）；右下角显示无焦点、点击穿透的小 toast（约 2.2 秒自动淡出）；单击系统托盘图标重新显示并展开。
- 停靠边、停靠偏移、抽屉开关写入 SQLite，重启恢复。
- 展开态：**310 DIP 宽**，高度自适应（锁序直译约 132 DIP、通序翻译约 112 DIP，含页脚），内边距 12/7 DIP。
- 外观（2026-11-08 起，见 `preview-balanced (1).html`）：深色品牌渐变卡片 `#282A36` → `#1A1C26`、12 DIP 圆角、1px `#14FFFFFF` 边框、内高光 + 双层投影；强调色为品牌蓝紫渐变 `#4F7CFF → #6B8CFF`。
- 顶部布局：
  - 20×20 品牌砖（`#1D1D1F`，圆角 6）+ 新 logo（两个并排窗口 + 中间箭头）+ 品牌文案 **简译**（12.5 DIP SemiBold）；
  - 右侧依次为「方框 + 右箭头」抽屉图标按钮、`—` 隐藏按钮、总开关（32×16 渐变胶囊，旋钮 12）；
  - 不显示 **开启 / 关闭** 小字；不显示 `M1` / `暂停`。
- 模式行：分段控件（segmented），**通序翻译 / 锁序直译** 各占一半，选中段为浅色高亮胶囊；
  - 点击分段切换 `LiteralMode`；
  - 锁序直译时显示 **短语拆分** 行（品牌蓝圆点 + 标签 + 28×15 DIP 开关）；通序翻译时整行隐藏；
  - UI 不显示「含短语 / 不含短语」文字；开关状态写入 `IncludePhrases`。
- 呈现方式行：**悬注 / 常驻** 分段控件；内部枚举仍为 `SurfaceMode.Tooltip / Panel`。
- 页脚：1px 分隔线 + 状态文案（默认「选中文本后悬停，或按 Ctrl+Shift+D」），9.5 DIP 弱化色。
- 开关变更立即写 SQLite；失败回滚 UI 并提示。
- 小图标不参与悬停触发；鼠标移入 Popper 不弹翻译。
- 位置持久化；多屏时若原位置不在任何工作区，回到默认位置。

### 11.2 托盘

- 单击托盘图标：显示并展开 Popper（若处于抽屉状态则展开抽屉）；
- 显示/隐藏 Popper；
- 启用/暂停（与总开关同步）；
- 打开设置；
- 当前翻译源/额度状态（只读）；
- 退出。
- 关闭主窗口不退出；退出必须从托盘或设置。

### 11.3 设置项

| Key | 类型 | 默认 | 说明 |
|---|---|---|---|
| `enabled` | bool | true | 总开关 |
| `literalMode` | bool | false | 生硬直译 |
| `includePhrases` | bool | false | 生硬模式是否包含短语 |
| `surfaceMode` | enum | tooltip | tooltip / panel |
| `dwellMs` | int | 400 | 200–1000 |
| `panelAutoCloseMs` | int | 0 | 0=不自动关闭（默认）；可选 5000–60000 |
| `sourceLang` | string | auto | BCP-47 或 auto |
| `targetLang` | string | zh-Hans | 可扩展 |
| `providerId` | string? | null | 首次向导选择 |
| `fallbackProviderIds` | string[] | [] | 备用源顺序；为空时自动使用其他已配置且支持该语言对的官方源 |
| `allowUnofficialProviders` | bool | false | 非官方源总开关 |
| `offlineMode` | bool | false | 仅本地词典/本地 MT；禁止远程请求 |
| `localMtEnabled` | bool | false | 启用本地 MT（需模型已安装） |
| `localMtModelId` | string? | null | 已安装模型 ID，如 `opus-mt-en-zh-int8` |
| `localMtModelStatus` | enum | not_installed | not_installed / downloading / ready / error |
| `clipboardFallback` | bool | false | 剪贴板取词回退 |
| `ocrEnabled` | bool | false | 显式启用本地屏幕 OCR，不从普通悬停截图 |
| `ocrComponentPath` | string? | null | 组件目录；空时为 exe 旁 OcrHost |
| `hotkey` | string | Ctrl+Shift+D | 全局快捷键 |
| `theme` | enum | system | system/light/dark |
| `launchAtStartup` | bool | false | 开机自启 |
| `historyEnabled` | bool | false | 历史记录（P2；v1 不建表） |
| `popperPosition` | object | 默认位置 | 物理像素 + 显示器 ID |
| `uiLocale` | string | zh-Hans | 界面语言 |
| `appDenylist` | string[] | 常见密码管理器 | 进程排除 |

> 设置采用 key-value + JSON，新增开关不需要迁移；但 `Settings` 结构体要有默认值与枚举校验。

---

## 12. 持久化与缓存

### 12.1 SQLite 配置

- 路径：便携数据目录 `data\thanslator.db`（解析顺序：`THANSLATOR_HOME` → 仓库根 `data\` → exe 旁 `data\`；见 `Thanslator.Core.AppPaths`）
- `PRAGMA journal_mode=WAL;`
- `PRAGMA synchronous=NORMAL;`
- `PRAGMA busy_timeout=5000;`
- `PRAGMA foreign_keys=ON;`
- 单写连接 + 读连接池；设置写入用事务。
- 迁移：`PRAGMA user_version`，append-only，每个迁移在事务中执行；升级前备份 DB；测试全新安装与旧版本升级。

### 12.2 Schema（v1）

```sql
CREATE TABLE settings (
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at_ms INTEGER NOT NULL
);

CREATE TABLE app_profiles (
  process_name TEXT PRIMARY KEY,
  enabled INTEGER NOT NULL DEFAULT 1,
  mode_override TEXT,
  surface_override TEXT
);

CREATE TABLE translation_cache (
  cache_key TEXT PRIMARY KEY,
  source_lang TEXT NOT NULL,
  target_lang TEXT NOT NULL,
  mode TEXT NOT NULL,
  provider TEXT NOT NULL,
  model TEXT,
  params_hash TEXT NOT NULL,
  result_json TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  last_used_at_ms INTEGER NOT NULL,
  expires_at_ms INTEGER NOT NULL
);
CREATE INDEX idx_cache_expiry ON translation_cache(expires_at_ms);
CREATE INDEX idx_cache_lru ON translation_cache(last_used_at_ms);

CREATE TABLE provider_accounts (
  id TEXT PRIMARY KEY,
  provider TEXT NOT NULL,
  label TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  last_tested_at_ms INTEGER
);

CREATE TABLE provider_capabilities (
  provider TEXT PRIMARY KEY,
  fetched_at_ms INTEGER NOT NULL,
  json TEXT NOT NULL
);

-- 历史记录为 P2：v1 不建表；届时通过迁移新增 history(...) 并默认关闭。
```

**隐私取舍**：`translation_cache` 默认不保存原文，只保存 `result_json` 与哈希元数据；生硬模式结果因含 `SourceToken` 不写缓存。历史记录为 P2，届时新增独立表并默认关闭；缓存内容仍可能含敏感译文，支持一键清空与按应用排除。

### 12.3 缓存策略

- 在线词典：TTL 90 天；远程 MT：30 天；本地 MT：7 天；负结果：30 秒。
- 上限：50,000 行或 100MB，LRU 淘汰；启动清理过期项。
- `cache_key` 见 8.6；模式/短语/来源/模型/提示词版本变化必须产生新 key。
- 本地词典结果可直接重算，不缓存；生硬模式不缓存；缓存主要用于普通模式的本地/远程 MT 与在线词典。
- 缓存行不保存原始 `source_text`；仅保留哈希 key、`result_json` 与元数据。

### 12.4 密钥

- API Key 存 Windows Credential Manager，target 命名 `Thanslator/provider/{providerId}`。
- 非敏感元数据（provider、label、创建时间、最后测试时间）存 SQLite `provider_accounts`。
- 绝不写入 SQLite、JSON 配置、日志、崩溃报告、遥测。
- 提供「测试连接」「删除密钥」「更换密钥」；UI 只显示末四位。
- Credential Manager 单条 blob 上限约 2560 字节（UTF-16 约 1280 字符），API Key 足够；更长内容用 DPAPI。

---

## 13. 多语言扩展

### 13.1 语言标签

- 全链路 BCP-47：`en`, `zh-Hans`, `zh-Hant`, `ja`, `ko`, `fr`, `de`, `ar`, `he`, `fa`, `ur`。
- 规范化：小写语言、脚本首字母大写、地区大写；`zh-CN` 映射为 `zh-Hans` 用于内部，调用提供商时再映射（Google 用 `zh-CN`，Azure 用 `zh-Hans`）。
- 显示名从系统/ICU 获取，不把显示名当 key。

### 13.2 语言包

- 词典按语言对打包：`dict-{src}-{tgt}.sqlite`，带版本、来源、许可证、覆盖率元数据。
- 首版只随包 `dict-en-zh.sqlite`；其他语言对可按需下载或后续内置。
- 语言包与 UI 语言独立：目标语言是数据，界面语言是资源。
- 分词器注册表：按脚本选择 UAX #29/ICU/jieba；短语匹配按语言配置。
- 提供商能力矩阵缓存：`provider_capabilities(provider, fetched_at_ms, json)`；不支持的语言对在设置中禁用，不等到请求时失败。

### 13.3 结果与 UI

- 结果模型已语言中立（第 5 章）。
- UI 文案用 `.resx`（首版 zh-Hans；预留 en）；错误码由后端返回，前端本地化，不拼接翻译片段。
- RTL：`dir` 字段、逻辑 CSS/布局属性、必要时 FSI/PDI；首版不承诺阿拉伯语/希伯来语 UI，但数据结构支持。
- 字体：依赖 Windows 字体回退（Segoe UI、Microsoft YaHei UI、Yu Gothic UI、Malgun Gothic）；不打包大字体。

---

## 14. 隐私与安全

### 14.1 威胁模型

选中的文本可能是密码、Token、合同、源代码、私人消息。数据流必须是：

```text
本地取词 → 本地分类 → 用户悬停触发 → 仅发送到用户选择的提供商 → 本地缓存
```

禁止：

- 后台持续监听剪贴板；
- 预取用户未触发的选区；
- 把选中文本写入普通日志/崩溃报告/遥测；
- 在密码框、安全桌面、提权窗口取词；
- 把 API Key 写入 SQLite/配置/日志。

### 14.2 措施

| 风险 | 措施 |
|---|---|
| 密码/敏感字段 | UIA `IsPassword` 检查（元素及祖先）；进程 denylist；安全桌面跳过 |
| 剪贴板回退泄露到历史 | 默认关闭；显式开启；提示剪贴板管理器可能记录 |
| 远程翻译数据外发 | 首次使用告知；结果显示提供商；可切换离线/本地；非官方源单独确认 |
| 日志泄露 | 默认不记录文本；调试日志脱敏；日志级别可配置 |
| 提权窗口 | 明确不支持；不尝试绕过 UIPI；不默认管理员运行 |
| API Key 泄露 | Credential Manager；不内置共享 Key；支持撤销/更换 |
| 恶意更新 | 安装包/更新包签名；HTTPS；校验哈希 |
| AV 误报 | 不注入进程；钩子回调短；代码签名；提交 Defender/SmartScreen；提供轮询兜底 |

### 14.3 首次运行同意

首次需要句子/段落翻译时，优先提供本地选项：

> 下载本地翻译模型（约 100–300MB，零 Key、不联网），或配置官方免费额度，或继续仅使用本地词典。模型只在点击下载后获取，不会自动安装。

只有用户选择远程源时，才显示数据外发提示：

> 选中文本将发送至「Azure Translator」用于翻译。Thanslator 默认不保存选中文本；若你主动开启历史记录，原文仅保存在本机并在 7 天后删除。

用户可选择：**下载本地 MT（零 Key）** / 仅本地词典 / 配置官方 Key / 使用非官方免 Key（风险提示）。选择写入设置，后续不再重复弹窗（可随时在设置中更改）。

---

## 15. 性能与可靠性

| 指标 | 目标 |
|---|---|
| 冷启动到 Popper 可见 | ≤2s |
| 空闲内存 | ≤150MB（WPF 目标 50–100MB） |
| 空闲 CPU | <1% |
| 本地词典查询 | p95 ≤50ms |
| 结果容器出现 | dwell 结束后 ≤100ms 显示骨架 |
| 本地 MT | 模型加载 1–3s；短句 p50 ≤3s、p95 ≤8s（CPU int8，句长/硬件而定） |
| 本地 MT 内存 | 运行时额外数百 MB；空闲 5 分钟卸载 Host |
| 远程翻译 | p50 ≤1.5s，p95 ≤4s（网络/来源而定；用户配置后才统计） |
| 缓存命中 | ≤100ms |
| 同选区重复触发 | 0（必须离开再进入） |

可靠性：

- UIA 调用独立 MTA 工作者：超过 2s 停止等待并丢弃过期结果，队列有界；无法强制中断挂起 COM，持续挂起需重启应用。需要可杀死的故障隔离时再升级独立 helper 进程，不无限创建线程（ADR-025）。
- 提供商超时/取消/熔断；旧请求用 generation 丢弃。
- LocalMtHost 崩溃自动重启一次；连续失败提示用户；空闲 5 分钟退出并释放模型内存。
- 数据库写入失败回滚 UI 状态；DB 损坏时启动恢复/重建缓存，不丢用户可重建的数据。
- 钩子线程不执行任何阻塞操作；回调 <1ms。
- 退出时注销钩子/热键、关闭 DB、取消请求；避免残留进程。

---

## 16. 错误处理约定

- 内部用错误码（`capture.unsupported`, `provider.not_configured`, `provider.quota`, `provider.timeout`, `localmt.not_installed`, `localmt.download_failed`, `localmt.inference_failed`, `dictionary.not_found`, `db.write_failed`），UI 本地化。
- 不把原始异常/HTTP body 直接显示给用户；调试日志记录脱敏后的错误码与状态码。
- 本地 MT 未安装返回 `localmt.not_installed`；远程源未配置返回 `provider.not_configured`；两者都不自动尝试非官方源；单词/短语仍走本地词典。
- 降级顺序：官方主源 → 官方备用 → 非官方（若启用）→ 错误；词典失败不影响 MT，MT 失败不影响词典。
- 每次降级在结果元数据中记录 `attempts[]`，UI 只显示最终来源与简短原因。

---

## 17. 测试与兼容性

### 17.1 单元测试

- `TextClassifier`：单词/短语/句子/段落边界；中英文；标点；换行；模糊输入。
- `LiteralGlosser`：原序、标点保留、功能词、未知词、连字符、所有格、URL/代码、includePhrases 路由。
- `DictionaryService`：ECDICT 解析、POS 前缀、领域标签、词形还原、短语最长匹配、覆盖表优先级。
- `CacheKey`：模式/短语/来源/模型变化必须产生不同 key。
- `SettingsRepository`：默认值、写入、重启恢复、迁移、损坏恢复。
- Provider 解析器：用固定 JSON fixture 测 Azure/Google/Baidu/Alibaba/Keyless 响应；错误码映射。
- `LocalMtProvider` / Host 协议：模型未安装、ready、Host 崩溃重启、超时、空输出、错误码映射。
- 模型安装：SHA-256 校验、取消下载、损坏包、删除模型、磁盘空间不足。
- Trigger 状态机：dwell、移动重置、Esc、冷却、同选区抑制、generation。

### 17.2 集成/手工测试矩阵

| 应用 | 取词方式 | 选区矩形 | 悬停触发 | 备注 |
|---|---|---|---|---|
| Chrome | UIA TextPattern | 逐行 | 待验证 | 首次 UIA 可能需重试 |
| Edge | 同上 | 逐行 | 待验证 | Chromium 同源 |
| Firefox | UIA（143+）/IA2 | 待验证 | 待验证 | 旧版走 IA2 |
| Notepad | UIA | 待验证 | 待验证 | RichEditD2DPT1，按 class 查找 |
| Notepad++ | UIA/IA2 | 待验证 | 待验证 | Scintilla 控件，需实测 |
| VS Code | UIA | 待验证 | 待验证 | Electron/Chromium |
| Word | UIA/COM/剪贴板 | 待验证 | 待验证 | UIA 可能不稳定 |
| PowerPoint | UIA/剪贴板 | 待验证 | 待验证 | 文本框选择行为需实测 |
| Acrobat（文本层 PDF） | MSAA/剪贴板 | 待验证 | 待验证 | 可能需要延迟读取 |
| 浏览器内置 PDF（Chrome/Edge） | UIA/剪贴板 | 待验证 | 待验证 | PDF 查看器差异大 |
| 终端 | 剪贴板 Ctrl+Insert | 无矩形 | 热键 | 默认不进终端 |
| 提权窗口 | 不支持 | — | — | UIPI 限制 |

- DPI：100/125/150/200%，双屏混合缩放，负坐标显示器。
- 主题：浅色/深色/高对比度。
- 语言：英文→中文；开发环境验证另一语言对的结构路径。
- 本地 MT：模型下载/安装/删除；断网可用；Host 重启；20 句技术文档抽样检查（不要求逐句完美，记录明显错误）。
- 隐私：密码框不取词；剪贴板恢复；日志无文本；本地 MT 不产生网络请求。

### 17.3 发布前检查

- 所有免费额度/ToS 重新核对（设计基准日 2026-11-07 会过期）。
- ECDICT/Kaikki 许可证与署名检查；商业分发法律复核。
- 本地 MT 模型/CTranslate2/SentencePiece 许可证、模型 hash、下载源可用性检查。
- 安装包签名、SmartScreen、Defender 误报测试。
- 数据库迁移 fresh install + upgrade。
- 崩溃/强杀后设置恢复。

---

## 18. 打包与发布

- 目标：Windows 10 22H2 / Windows 11，x64；ARM64 可选。
- .NET 10 LTS，WPF；`PublishSingleFile` 可选；自包含发布目录约 170MB，加 ECDICT 词典约 96MB（合计约 270MB）；内置原生 Host + CPU-only CTranslate2 约 7.7MB；安装包压缩体积待实测；本地 MT 模型不计入主发布包。
- 安装器：NSIS（复用 Tauri 自带）或 MSIX；运行期数据写入便携 `data\` 目录（安装版可映射到安装目录旁或 `%LOCALAPPDATA%`，由打包器决定）；卸载保留/清除用户数据由用户选择。
- 发行范围（2026-11-07 确认）：**个人使用优先**，可暂不签名；后续上传 GitHub 时，公开 Release 建议签名（OV/EV 或 Azure Trusted Signing），并评估 SmartScreen/AV 阻力。
- GitHub 公开前：添加代码 LICENSE、`THIRD_PARTY_NOTICES.md`、构建说明；不要把 ECDICT CSV 或来源不明的词典数据直接提交到仓库。优先提供 `tools/DictionaryBuilder` + 下载/hash 校验脚本，让用户自行构建；或在 Release 中单独分发带许可证说明的词典包。
- 本地 MT：主安装包可包含 `Thanslator.LocalMtHost.exe`（约 448KB）+ CPU-only `ctranslate2.dll`（约 7.2MB）；模型（int8 约 81–86MB）作为可选下载，存 `data\models\`，附 `LICENSE`/`SHA256SUMS`。不自动下载；卸载时询问是否删除模型。
- 本地 MT 模型包与 Host 可随 GitHub Release 分发；不要把模型文件提交到 Git 仓库。
- 开机自启：注册表 Run 或启动文件夹；默认关闭，用户开启。
- 自动更新：P1（Velopack 或自建签名清单）；MVP 可手动下载覆盖安装。

**0.6.8 升级安装（ADR-030）**：读取当前用户 `Software\Jianyi\InstallDir`，缺失时读取卸载项 `InstallLocation`；只处理该产品已登记且含程序或卸载器的安装，不扫描磁盘。升级沿用原目录并跳过改目录页，欢迎页显示旧版本、路径与卸载安排；`.onInit` 不结束程序或卸载。用户点击安装后，先检查目标不是驱动器根/系统/用户根目录且存在旧卸载器，复制卸载器到临时插件目录，以 `/S _?=<原目录>` 调用并 `ExecWait` 等待，检查退出码及程序/组件残留，失败停止。关闭主程序及其子进程以释放 MT/OCR 文件；旧卸载器保留 `data\`，不迁移或删除设置与模型。带 OCR 包会选中并锁定旧安装已有的 OCR 组件；不含 OCR 的包遇到已有 OCR 时在卸载前拒绝升级，避免无意丢失功能。首次安装 OCR 仍默认不选、不启用。隔离验证命令与结果见 [安装升级记录](docs/installer-upgrade-2026-11-10.md)。
- 日志：`data\logs`，滚动、脱敏、默认不含文本；用户可导出诊断包（默认不含选中文本）。
- 词典构建在 CI 中执行，产物带 hash；发布清单记录词典版本与来源。

---

## 19. 里程碑

| 阶段 | 内容 | 退出标准 |
|---|---|---|
| M0 | 文档与决策（本文件） | 技术栈/来源/短语语义确认 |
| M1 | Spike：UIA 取词 + 透明浮层 + Popper + SQLite | 6.1/3.4 验证门通过；否则切换 Electron |
| M2 | 词典 + 分类 + 触发状态机 + Tooltip/Panel | 单词/短语离线可用；三应用通过 |
| M3 | 本地 MT Host + 模型下载/校验 + 缓存 + 设置向导 | 零 Key、断网可翻句子；模型安装/删除可用 |
| M4 | 生硬直译 + 短语开关 + 覆盖表 | 原序/标点/未知词验收通过 |
| M5 | 官方 MT（可选）+ 隐私/安全/DPI/兼容性/错误处理 | 测试矩阵通过 |
| M6 | 安装包/自启/许可证页；签名在公开分发时再做 | 可日常安装使用 |

---

## 20. 风险与开放问题

| 风险 | 影响 | 应对 |
|---|---|---|
| ECDICT 数据许可不清晰 | 商业分发风险 | 个人/开源低风险；商业前法律复核或替换 cdict 来源；保留 MIT 与 NOTICE |
| 免费额度/接口变化 | 翻译失败 | Provider 抽象 + 降级 + 缓存 + 发布前复核；不承诺永久免费 |
| 非官方接口 ToS | 账号/IP 被封、法律风险 | 默认关闭；显式同意；不内置 Key；公开版可只保留本地 MT + 官方源 |
| UIA 跨应用不稳定 | 部分应用无法取词 | 三级回退 + 热键 + 兼容矩阵；不承诺 100% |
| UIA 调用挂起 | 取词卡死 | 独立线程 + 看门狗；必要时独立 helper 进程 |
| 剪贴板回退干扰 | 剪贴板历史/管理器冲突 | 默认关闭；显式开启；备份/恢复；限流 |
| WPF 透明窗口/点击穿透 | 关闭按钮不可点或抢焦点 | 两种窗口样式；Spike 验证；Panel 不用 `WS_EX_TRANSPARENT` |
| 提权窗口 | 无法取词 | 明确限制，不绕过 UIPI |
| 生硬模式质量 | 用户误认为 bug | UI 标注「生硬直译」；原文对照；一键切普通模式 |
| 短语语义理解偏差 | 功能不符预期 | 设置中用示例说明；必要时改路由矩阵 |
| 长段落翻译 | 提供商限制/超时 | 按句切分、批量、缓存、Panel 滚动 |
| 词典包体积 | 安装包变大 | 可提供 top-30k 精简包；完整包按需下载 |
| 本地 MT 质量/延迟 | 低于云端、CPU 慢、术语不准 | UI 标注「本地 MT」；可选官方源；抽样质量测试；不承诺权威 |
| 本地 MT 模型许可/体积 | 不能随意分发、下载 100–300MB | 优先 OPUS-MT/CTranslate2；模型单独下载并附许可；不提交 Git |
| OPUS-MT→CTranslate2 转换链 | 技术验证失败则本地 MT 无法交付 | M3 前做转换/推理 Spike；失败时退回 Argos 或只保留本地词典 + 官方源 |
| 模型下载供应链 | 被篡改/损坏 | HTTPS + SHA-256 + 签名清单；失败可重试/删除 |

### 已确认（2026-11-07）

1. **短语语义**：短语 = 2–8 个单词；`includePhrases=false` 只查短语词典，不调用 MT；`true` 逐词直译。
2. **默认来源**：单词/短语本地 ECDICT + 校订表；应用不预配置远程源；句子/段落优先使用可选下载的本地 MT，官方源作为可选高质量备选。
3. **Panel 自动关闭**：默认不自动关闭；保留关闭叉号；自动关闭可选。
4. **发行范围**：个人使用优先；后续可能上传 GitHub；公开前做许可证/签名/词典分发评估。
5. **本地 MT**：默认不下载；用户显式安装后启用；零 Key、不联网。

### 仍待确认

1. **本地 MT 模型选型**：OPUS-MT + CTranslate2 int8 优先，Argos 备选；需实测 en→zh 质量、CPU 延迟、模型体积与转换链。
2. **历史/生词本（P2）**：默认关闭；是否进入下一版本。
3. **GitHub 开源策略**：代码许可证；ECDICT/本地 MT 模型随 Release 分发、用户自建，还是仅提供构建脚本。

---

## 21. 附录

### 21.1 关键 API 端点

| 提供商 | 端点/认证 | 备注 |
|---|---|---|
| Azure Translator | `POST https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&from=en&to=zh-Hans`；`Ocp-Apim-Subscription-Key` + `Ocp-Apim-Subscription-Region` | body 为 `[{"Text":"..."}]`；F0 限额见 9.3 |
| Google Cloud v2 | `POST https://translation.googleapis.com/language/translate/v2?key=...` | body `{q, target, source, format}`；API Key 可限制到该 API |
| 百度 | `https://fanyi-api.baidu.com/api/trans/vip/translate`；`appid+q+salt+sign` | sign = MD5(appid+q+salt+secret) |
| 阿里云 | `mt.aliyuncs.com`；AccessKey 签名 | 签名较复杂；需实名 |
| DeepL | `POST https://api-free.deepl.com/v2/translate`；`DeepL-Auth-Key` | Developer 1M 一次性；Endpoint 以发 Key 时为准 |
| Google 非官方 | `GET https://translate.googleapis.com/translate_a/single?client=gtx&sl=..&tl=..&dt=t&q=..` | 非官方；`dt=bd` 可返回词性义项 |
| Edge 非官方 | `POST https://edge.microsoft.com/translate/translatetext?from=..&to=..&isEnterpriseClient=false` | 非官方；可能失效 |
| MyMemory | `GET https://api.mymemory.translated.net/get?q=..&langpair=en%7Czh-CN` | 5k/天匿名；50k/天带邮箱；短文本；URL 中的竖线编码为 `%7C` |
| Groq | `https://api.groq.com/openai/v1/chat/completions` | 免费层约 30 RPM/1k RPD/200k TPD；可选 |
| Ollama | `http://localhost:11434/v1` | 本地；模型需用户安装；P2 |
| **本地 MT** | 命名管道 `\\.\pipe\Thanslator.LocalMt`；JSON lines；`Thanslator.LocalMtHost.exe`（CTranslate2） | 不联网、无 Key；模型可选下载；默认不启用 |

### 21.2 研究来源（节选）

- Microsoft UIA：`IUIAutomationTextPattern::GetSelection`、`IUIAutomationTextRange::GetBoundingRectangles`、`RangeFromPoint`、`CompareEndpoints`；UIA event/property IDs。
- Microsoft 窗口样式：`WS_EX_NOACTIVATE`、`WS_EX_TOOLWINDOW`、`WS_EX_TOPMOST`、`WS_EX_TRANSPARENT`；`WM_MOUSEACTIVATE`。
- WebView2Feedback #5752（透明背景回归，open，2026-11-07）。
- DeepL API plans（Free 停售、Developer 1M 一次性）。
- Google Cloud Translation pricing / v2 REST。
- Azure Translator pricing / service limits / v3 REST。
- 百度翻译开放平台产品页；阿里云机器翻译定价；腾讯云公告 2448/2482。
- MyMemory usage limits / terms。
- ECDICT LICENSE / README / CSV 统计；CC-CEDICT MDBG；Wiktionary/Kaikki；Princeton WordNet；OpenHowNet/BabelNet 许可页。
- OPUS-MT en→zh（原始模型 README 标 CC-BY 4.0，需复核/署名）、CTranslate2（MIT）、SentencePiece（Apache-2.0）、Argos Translate（代码 MIT/CC0，模型许可逐个确认）。
- Chrome UIA 支持公告、Firefox 143 UIA、selection-hook 2.1.2 文档。
- W3C WCAG 1.4.13、WAI-ARIA APG Tooltip/Dialog 模式。

> 研究代理的原始 JSON 与来源清单保存在会话临时目录；本文件只保留影响决策的结论。发布前请按 17.3 重新核对。

### 21.3 决策记录（ADR 摘要）

| 编号 | 决策 | 日期 | 原因 |
|---|---|---|---|
| ADR-001 | 首版 Windows-only | 2026-11-07 | 取词/浮层高度依赖 Windows API；跨平台会稀释核心体验 |
| ADR-002 | WPF 优先，Electron 备选 | 2026-11-07 | 低占用、透明浮层可靠、原生 UIA；Electron 作为验证门失败的回退 |
| ADR-003 | 生硬模式不使用 MT/LLM | 2026-11-07 | 只有逐词词典+规则能保证原序、确定性、可解释 |
| ADR-004 | 本地 ECDICT + 人工覆盖表 | 2026-11-07 | 免费、离线、覆盖广；数据质量问题用校订表解决 |
| ADR-005 | 远程源官方优先，非官方仅兜底 | 2026-11-07 | 「权威性」与稳定性；避免 ToS/封禁风险 |
| ADR-006 | 设置用 SQLite key-value + JSON | 2026-11-07 | 开关频繁增加，避免每次迁移；类型在应用层校验 |
| ADR-007 | 缓存不默认保存原文 | 2026-11-07 | 选中文本可能敏感；历史另设且默认关闭 |
| ADR-008 | 本地词典优先，默认不配置远程源 | 2026-11-07 | 用户确认本地优先；句子翻译用可选本地 MT 或官方源 |
| ADR-009 | Panel 默认不自动关闭 | 2026-11-07 | 用户确认；保留关闭叉号，自动关闭改为可选项 |
| ADR-010 | 个人使用优先，GitHub 后续 | 2026-11-07 | 先保证自用；公开前再做许可证、签名与词典分发策略 |
| ADR-011 | 可选本地 MT（OPUS-MT + CTranslate2） | 2026-11-07 | 用户选择零 Key 离线句子翻译；模型默认不下载，避免主安装包膨胀 |
| ADR-012 | 品牌改为「简译」+ Popper 紧凑版 310 DIP | 2026-11-08 | 用户提供 `brand-guidelines (1).html` 与 `preview-balanced (1).html`，指定以新版尺寸为准；代码命名空间/DB/管道不变，避免迁移与重编 Host |
| ADR-013 | 界面字体显式指定「Segoe UI, Microsoft YaHei UI, Microsoft YaHei」+ 灰度抗锯齿 | 2026-11-08 | 原用 Segoe UI 让中文走 WPF 逐字形回退，字重/字宽不一致且半透明窗口上的 ClearType 出现彩边；改为显式回退链 + `TextOptions.TextRenderingMode="Grayscale"`，次要文字色上调一档（#9AA0B5→#B3B9CA、#6E7387→#8B91A4）提升深色卡片可读性 |
| ADR-014 | 鼠标输入触发由 `WH_MOUSE_LL` 改为 8ms 轮询 | 2026-11-08 | 实测本机环境钩子可安装（`SetWindowsHookEx` 非零、`err=0`、消息循环正常）但回调一次都不触发，且系统对超时回调是静默摘除、无通知，属不可观测失败；轮询用 `GetCursorPos`+`GetAsyncKeyState`，10ms 级精度足够 400ms dwell，失败可观测（心跳/错误码/状态栏提示）。代价：失去滚轮事件（`Wheel` 仅保留接口）与极低延迟，滚动收起浮层改为点击/失焦路径 |
| ADR-015 | 发布产物用本机自签名证书签名（**已撤销，见 ADR-018**） | 2026-11-08 | 未签名的自包含 exe 每次重新发布都会触发 Microsoft Defender SmartScreen「Windows 已保护你的电脑 / 无法识别的应用」（发行者未知），用户没点「仍要运行」时进程根本没启动，表现为「托盘没有图标、取词没反应」。生成 `CN=Jianyi Code Signing` 自签名证书（10 年），`.cer` 导入 `CurrentUser\Root` + `CurrentUser\TrustedPublisher`，`tools/sign-release.ps1` 在每次 publish 后签名并校验；仅本机信任，公开分发仍需正式证书 |
| ADR-016 | 托盘不可用时禁止隐藏到托盘并显式提示 | 2026-11-08 | `Shell_NotifyIcon(NIM_ADD)` 返回 `ERROR_ACCESS_DENIED(5)` 的实际原因是进程完整性级别为 **Low**（AI 编码工具的沙箱、受保护模式会给工作区打 Low 标签并以低完整性启动子进程），UIPI 禁止低完整性进程向 Medium 的 Explorer 发送托盘消息。旧实现仍会把控制条隐藏并提示「点击托盘图标」，而图标根本不存在。改为 `CreateTray()` 返回可用性、`PopperWindow.CanHideToTray` 守卫「—」与右键隐藏，失败时状态栏说明并保持控制条可见；启动日志把完整性级别打印为 `Low/Medium/High`。正常双击 exe（Explorer 启动、Medium）不受影响 |
| ADR-017 | 自签名证书不能让 SmartScreen 放行；本机用 `Unblock-File` 消除 MOTW | 2026-11-08 | 实测已签名 exe 仍弹「Windows 已保护你的电脑」，发行者显示 `CN=Jianyi Code Signing`，说明签名有效、链路可信，但 SmartScreen 不认可自签名证书的声誉。该弹窗由 **Mark of the Web**（`Zone.Identifier`）触发，与签名是否有效无关：SmartScreen 开启时显示蓝色拦截框，关闭时退回「打开文件 - 安全警告」。本机使用先解除锁定（属性→解除锁定，或 `Get-ChildItem dist\M5-folder -Recurse -File \| Unblock-File`）；公开分发需 OV/EV 证书或 Azure Trusted Signing 建立声誉。ADR-015 中「签名即可避免 SmartScreen 拦截」的说法据此更正 |
| ADR-018 | 撤销自签名方案，发布产物保持未签名 | 2026-11-08 | 自签名既不能让 SmartScreen 放行，又要求每个用户导入证书，无法用于分发。已删除 `tools/sign-release.ps1`、`data/signing/`（PFX/口令/CER）和当前 exe 的嵌入签名（PE 安全目录清零、签名 blob 截断），发布流程不再包含签名步骤。本机证书存储中的 `CN=Jianyi Code Signing`（My/Root/TrustedPublisher）保留待手动清理（命令见交付说明），不影响未签名程序运行。后续对外分发在「正式代码签名（OV/EV 或 Azure Trusted Signing）」与「Microsoft Store MSIX」之间决策，未决定前不引入自签名 |
| ADR-019 | 分发采用 NSIS 用户目录安装包（方案 C，复用 Tauri 自带的 NSIS） | 2026-11-08 | 直接发便携目录会让用户每次启动新版本都遇到 SmartScreen，而正式证书/商店暂不投入。本机已由 Tauri 构建下载了完整 NSIS（`%LOCALAPPDATA%\tauri\NSIS`，含 MUI2、简体中文语言包和插件），因此不再引入 Inno Setup/额外下载。安装到 `%LOCALAPPDATA%\Programs\Jianyi`，`RequestExecutionLevel user` 不触发 UAC；安装目录用户可写，所以 `AppPaths` 的「exe 旁 data\」不变；卸载保留 `data\`（设置/日志/本地 MT 模型）。用户只在**首次运行安装包**时遇到一次 SmartScreen，安装后启动不再提示。构建入口 `tools/publish.ps1`（测试→publish→整理→Unblock→makensis），版本号由 csproj `<Version>` 经 FileVersion 读取。**AI 沙箱会给工作区打 Low 完整性标签，新生成文件会继承**；带 Low 标签的安装包双击后会以低完整性运行，写 `%LOCALAPPDATA%` 时报「无法打开要写入的文件」，因此脚本在发布目录和安装包生成后统一 `icacls /setintegritylevel Medium`。以后换正式证书只需给安装包签名，应用代码不用改 |
| ADR-020 | 翻译保护层：受保护片段 + 专名/术语占位符 + MT 输入输出校验 | 2026-11-08 | 本地 MT 会改写或丢掉 URL/邮箱/路径/代码/数字/emoji，也会把专名按普通词翻译。送 MT 前用 `⟦n⟧` 保护非翻译片段、用 `⟦Tn⟧` 保护术语表 `targetTerm` 条目，翻完按序还原（容忍空格和【】）。术语表支持 `caseSensitive`/`wholeWord`，用户表 `data\overrides\translation-glossary.json` 与内置表合并且用户优先。生硬模式把受保护片段当 Opaque；未收录词保留原文并加下划线，右下角 Toast 提示「存在未收录内容，已用下划线标注」。`LocalMtGuard` 调用前拦截 CJK/空/纯符号输入，调用后拒绝空译文、乱码、原样返回。58 项测试通过 |
| ADR-021 | 锁序直译优先取本义：跳过「其他」义项 + 可维护的优先释义表 | 2026-11-08 | ECDICT 的第一义项有时是专业/补充注释（如 windows 的操作系统全称、set 的 DOS 命令说明），不是单词本义。`LiteralGlosser` 现在优先选非「其他」词性的第一义项，再按 `primary-meanings.json`（内置 + 用户 `data\overrides`，用户优先）用原词或词形还原后的 headword 覆盖指定词的优先释义；查不到的词仍走词典第一义项。新增 2 个测试，共 61 项通过 |
| ADR-022 | 通用取词回退：剪贴板 + UIA 候选改进 | 2026-11-09 | ChatGPT（MSIX/Chromium）与 PDF 容器能枚举到文本元素，但 `TextPattern.GetSelection()` 返回空，表现为「点击有反应、没有译文」。新增 `ClipboardSelectionReader`：用户显式开启后，UIA 失败时向前台窗口发送 Ctrl+C，读取 `CF_UNICODETEXT`，并尽力恢复原文本（文本格式）；浮层锚点用光标位置。UIA 侧增加光标祖先链、优先 `Document`/`Edit` 元素、候选上限 24。剪贴板回退默认关闭，符合隐私不变量；提权窗口受 UIPI 限制 |
| ADR-024 | 通用取词管线加固：UIA 校验 + 剪贴板重试 + 一次提示 | 2026-11-09 | 逐应用补 UIA 无法通用。`CaptureSelection` 统一编排：UIA 优先，鼠标路径要求选区靠近光标（96px，防止 WPS/Office 返回面板/工具栏里的错误选区），否则交给剪贴板；剪贴板依次尝试 Ctrl+C（800ms）→ Ctrl+C 重试（500ms）→ Ctrl+Insert（700ms），轮询 `CF_UNICODETEXT`/`CF_TEXT`，最后恢复原文本；未开启剪贴板时首次失败弹一次 toast 提示入口。扫描版 PDF/图片没有可复制文本，OCR 框选作为下一层（未实现）。 |
| ADR-023 | 短语未命中按句子回退 + 带符号词整体识别 | 2026-11-09 | 5 词左右的 PDF/网页选区被 `TextClassifier` 判为 `Phrase`；短语拆分关闭时只查整条短语，未命中就显示「本地词典未收录」而不给出翻译。新增 `TextClassifier.ShouldFallbackToSentence`：短语未命中且 ≥3 词时按句子处理（锁序逐词、通序本地 MT），命中的短语仍受开关控制。`TextTokenizer` 把含 `.` `_` `&` `'` `+` `#` 的词当作整体（AT&T/node.js/C++/C#/snake_case），不再拆成字母；新增 `dictionary-overrides.json` 补充 U.S./C++/node.js 等符号词，查不到仍保留原文并标未收录。 |

| ADR-025 | 有界取词与共享隐私边界 | 2026-11-10 | 取代 ADR-022/024 的无差别回退：UIA 无窗口 MTA，剪贴板独立 STA；状态区分成功/无选区/不支持/超时/禁止/忙碌/取消。安全门先于读取，禁止/明确无选区/超时不复制；自动触发按实际行矩形。剪贴板预先复制安全 HGLOBAL 格式，未知格式拒绝，恢复前锁内校验序号和所有者，已知终端不发 Ctrl+C。UIA 单调用不能强制取消，2 秒超时只停止等待；持续挂起需重启或后续 helper 隔离，不无限创建线程。 |
| ADR-026 | 按片段翻译、严格保护与取消 | 2026-11-10 | 细化 ADR-020：本机 OPUS-MT 会损坏 Unicode 占位符，线协议改用避开原文冲突的 ASCII 标记并严格校验身份/次数；普通数字不掩码但校验数值及重复数。损坏时原句重试一次，仍失败保留该单元原文。按句/换行及 384 UTF-8 字节保守预算切分，代码整体保留；Host 输入 token 超过 512 拒绝、禁用隐式截断，输出达到 512 上限拒绝，过滤坏候选后不再返回第一个坏结果。逐段进度、完整常驻内容、请求取消与实例独立 Host 管道；取消/超时终止本实例自建 Host。未换模型，无远程请求或新增依赖。 |
| ADR-027 | Unicode 输入与术语语境 | 2026-11-10 | Unicode 空白参与词数分类，成对外括号/引号不妨碍查词且保留 C++/C#/.NET。通序两词未收录可转句子，锁序 ≥3 词规则保持。歧义术语替换要求 contextTerms 语境匹配，同源用户术语覆盖内置，避免把日常 client/worker 强制改成客户端/工作线程；领域包后续单独设计。 |

| ADR-028 | 可选免框选本地 OCR | 2026-11-10 | 复用 MinerU 的 PP-OCRv6 ONNX 子集，默认关闭，主动热键或可信选区 dwell 回退；有限可见区域、隐私与遮挡检查、内存管道及常驻原文校对，见 §6.8。 |
| ADR-029 | 采样恢复与可见的热键反馈 | 2026-11-10 | 暂时无法采样时暂停、取消旧请求并等待恢复，不永久停止、不切换安全桌面；深层 UIA 祖先统一有界检查，热键禁重复并显示明确失败状态。未宣称已通过 VS Code 对话区现场验收。 |
| ADR-030 | 安装前卸载已登记旧版 | 2026-11-10 | 点击安装才卸载并等待完成，失败停止；沿用原路径保留便携 data，保留已装 OCR，避免覆盖遗留文件和升级丢组件。 |


## 领域术语知识库实施约束（2026-11-21）

以 product.md 的“领域术语知识库第一版”为当前产品定义，替代早期固定译法优先查询的重做方向。

- Core：领域库、术语条目、命中记录使用独立模型；匹配器为可单元测试的纯逻辑，处理语言对、英文边界、较长短语优先、多库优先级及去重。不引入向量数据库。
- Storage：在现有 SQLite 中新增库与条目表，外键和库内唯一约束明确；导入使用事务。所选库 ID 和顺序通过既有 settings 存储。personal_terms 不删除、不隐式迁移。
- App：新增独立知识库窗口，负责库/条目 CRUD、导入预览、优先级和冲突查看；设置页复用既有模型配置入口并提供快捷选择。通过当前保存的 API/本机连接配置调用，不擅自新增模型档案系统。
- Translation：扩展现有 LlmTranslationClient，传入当前原文的命中术语快照；请求含结构化术语数据及其仅为参考的说明。限制命中项总量和总字符数，超限显示明确提示，不静默漏用。API Key 继续使用 Windows 凭据管理器。
- 路由：接入位置不仅是 TryShowSentenceTranslationAsync；先检查 LLM/通顺模式及领域命中，保证命中的单词、短语也能进入领域翻译，避免先被通用词典短路。未命中、LocalMt 和 LiteralMode 保持既有路由。
- 展示：命中详情与请求 generation 绑定；ShowLookup/ShowTranslation/HideOverlay 均重置旧详情，避免跨结果残留。使用 WPF 文本控件，不渲染术语里的 HTML 或指令。
- 安全与错误：保存和导入失败不丢失编辑数据；库失效时明确提示。不记录原文、术语正文或凭据到日志。若提供模型使用情况，只说明“已提供规则”，不能未经检查声称模型严格执行。
- 验证：真实外部 API 与本机模型的翻译质量须现场验证；模拟 HTTP 的协议与路由测试不能冒充模型质量验证。

## 文档转换与知识生成分离（2026-11-21）

智能导入的 LLM 职责限定为结构整理、字段映射和已有内容转换。术语原文与对应译法必须有输入资料依据；缺字段或对应关系不确定时输出待处理项，不生成译法补齐。输出经程序校验与用户预览确认后才可持久化。优先复用确定性文件解析，必要时再调用 LLM 整理，避免把纯格式转换变成内容生成。

知识库生成是未来单独能力，不通过转换提示词隐式开启，当前不实现。

## 智能整理导入实现（2026-11-21）

KnowledgeDocument 使用 .NET 内置 UTF-8、ZIP、XML 能力读取限定格式的文字：DOCX 主文档段落、XLSX 共享/内联字符串及缓存单元格值。此为文本抽取，非版面还原或 OCR；不执行宏、公式，不访问外部 XML 实体。文件、相关 XML 解压量和提取字符数均有上限。

KnowledgeConversionClient 复用 LLM 端点规则、凭据管理及 API/本机 HTTP 客户端，以独立的整理请求获取 terms/unresolved JSON。没有加入生成词义能力。返回上限 1 MB、整理超时 90 秒，取消传播到请求。KnowledgeConversion 核对候选原词、译法及原文片段，缺依据、重复、超量与待处理项阻止整批确认；不能用字符串存在性检查代替语义审核。

KnowledgeWindow 复用预览与草稿确认；读取与请求期间禁用编辑和库切换，保存、删除和新建操作受忙碌保护。关闭页面或取消可终止请求。未确认的结果不写库，来源片段作为备注保留。


## ADR：更多设置与持久化接入（0.6.14 / 2026-11-24）

- 继续使用 WPF。MoreSettingsWindow 按需承载 Microsoft WebView2，复用已确认页面结构；这是现有窗口内的设置视图，不引入 Electron 或第二套桌面主程序。
- 页面作为嵌入资源随应用发布，运行时映射到固定本机虚拟地址。顶层导航、弹出窗口、网页下载、权限被限制；页面 CSP 禁止自行联网。模型及数据库操作通过有限的原生消息动作完成。API Key 不返回页面、不进入 JSON/SQLite。
- MoreSettingsRepository 在既有 SQLite 中保存带 revision 的完整知识库状态，以及独立模型档案与按日期/语言对统计的命中次数。版本检查保证旧窗口不能覆盖新提交。每次提交同时保存条目、版本、组合与回收状态，避免半批更新。
- 初次读取仅从 knowledge_libraries/knowledge_terms 迁移，按旧 KnowledgeLibraryIds 创建默认组合；新页面之后只修改新状态。旧表和 personal_terms 保留，以便升级前数据仍可核对。旧管理窗口不再作为入口。
- 翻译请求根据已保存的当前组合投影为既有 KnowledgeLibrary/KnowledgeTerm，再进入 KnowledgeRules 与既有 LLM 请求。禁用或已删除库不参与；频率排序统计每次请求中实际含匹配词条的库，不记录原文。同一请求内重复出现不重复累计同一库。auto 源语言仍沿用已确认的英文库限制；中、日、韩显式源语言允许无空格句中的术语匹配。
- 组合未保存编辑只在页面草稿中。词条历史保存快照，恢复也追加快照。回收时保留引用，彻底删除时清除所有组合引用。
- 导入助手档案独立；凭据按数据目录和接口地址分开写 Windows 凭据管理器，避免更换接口时复用旧 Key。生成 JSON 需要方向、长度和结构校验，不能直接入库。已有双语文档整理复用 KnowledgeDocument/KnowledgeConversionClient 及原文依据核对，当前仅开放本机执行。
- AppSettings 新增 ModelDownloadDirectory、OllamaRuntimePath、ManagedLlmStorageRoot。默认下载位置通过 AppPaths.ModelsDirectory 解析，未选择绝对路径时随安装目录保持便携。新位置由原生文件夹选择器取得并校验可写，保存失败不改变内存配置。
- ManagedOllama 只管理自身启动的进程，在回环地址的独立端口提供服务，子进程 OLLAMA_MODELS 指向所选目录的 llm 子目录。组件包、临时下载、运行组件与该进程私有数据也在所选目录。已有外部服务不被重启或改写环境变量。应用退出只结束自己持有的进程。
- 运行组件仅在用户点击下载后从 ollama/ollama 官方稳定 Release 获取 Windows x64 ZIP，校验 GitHub 发布的 SHA-256，解压检查路径与体积。模型下载完成还需出现在本机列表并通过试译，之后才提交当前模型设置。
- 参考：Microsoft 官方 WebView2Samples（https://github.com/MicrosoftEdge/WebView2Samples）；Ollama Windows 独立组件（https://github.com/ollama/ollama/blob/main/docs/windows.mdx）；服务存储目录（https://github.com/ollama/ollama/blob/main/docs/faq.mdx）。
- 验证工具 tools/SettingsSmoke 使用隔离数据库、真实 WPF 控件和真实 WebView2，通过仅监听回环的协议夹具检查连接、下载配置与文件落盘。夹具不代表真实模型质量，不下载大型权重。检查入口、键位、组合草稿保存、回收恢复、历史回退、助手隔离及默认/自选下载位置。
- 本版未扩展本地 MT 推理架构：现有英语到简体中文 CT2 引擎继续工作，多语言离线包和多套 OCR 分发仍待验证；不把原型样例视作实际支持。更多设置需要系统 Microsoft Edge WebView2 运行时。

## 更多设置窗口边界（0.6.15 / 2026-11-25）
MoreSettingsWindow 使用 WPF WindowChrome 原生圆角区域，无透明分层窗口。WebView2 视口与 main 均填满客户区，侧栏和内容分别滚动。品牌及顶部空白通过 WebView2 原生 app-region 拖动；窗口控制消息独立于模型操作队列，八个边缘命中区转交系统缩放。SettingsSmoke 检查完整视口、原生圆角裁切及最小化/最大化/还原。

## 更多设置整页拖动实现（0.6.16 / 2026-11-25）
替代 0.6.15 只覆盖顶部的 CSS 拖动区。主文档及模型 iframe 共用指针手势：记录按下位置，超过 6 CSS px 后捕获指针，逐帧发送移动，松开或取消清理，并拦截这次拖动产生的 click。编辑控件和边缘缩放区域不参与拖动。
原生窗口消息保存按下时的客户区逻辑坐标，移动时使用当前物理光标位置减去按窗口 DPI 换算的抓取偏移，SetWindowPos 不改变尺寸、层级或激活状态。不能在异步消息到达时才读取鼠标位置作为起点，否则短拖动的按下和移动可能读到同一个终点，导致窗口不动。窗口操作仍绕过模型任务队列，不记录用户内容。
实测：正文拖动 (80,80)→(140,120)；侧栏按钮拖动→(180,150)，未误切页；模型配置内页拖动→(150,130)。输入框内拖选保留文字选中且窗口位置不变。真实设置工作流 38 项通过。

## 2026-11-26 翻译偏好交互调整（已确认，待实施）

以 [交互审查第一节](docs/ux-review-2026-11-26.md#1-已同意翻译偏好调整) 为本次确认范围。旧带场景规则不可静默扩大范围，候选建议与生效规则分开，按实际语言选择偏好；结果浮窗保留调整入口。复用现有 WPF/WebView2、SQLite、凭据和翻译编排，不新增场景管理系统。其余模块审查建议尚未批准，本记录不改变当前安装版或冻结行为。
## 推荐下载与启用

推荐目录和体积由原生端提供。打开页面只读检查本机文件，不下载组件。首次安装使用随简译发布的 Ollama 官方版本信息，主动更新时才查询最新发布。离线包、OCR 和运行组件共用文件体积检查、SHA-256 校验、停滞超时和续传。下载进度前进后重新计算连续失败次数，连续三次没有新增进度时结束本次尝试。下载前保存官方发布条目的地址、大小和摘要，重启后可按同一条目续传。ZIP 校验通过后先核对解压空间，再在临时目录解压。Ollama 权重由其服务下载，先查本机列表再决定是否拉取。

新模型试译或试识别通过后，模型档案和当前选择在同一个 SQLite 事务提交。失败时保留下好的文件，重试可以从校验或试运行继续。

## 0.8.1 数据保存与安装文件保护

恢复预览记录会覆盖的基础设置原值，以及知识库和偏好的版本。确认时在同一个 SQLite 写事务里比较并提交，再以已提交的设置快照刷新运行状态。导出跳过损坏的基础设置，备份和词表在同目录完整写入后才替换原文件。当前词条和历史版本共用字段校验。

迁移在目标盘暂存并校验文件，原目录一直保留。模型档案与相关设置路径在一个事务内比较并提交。复制完成但未提交时，可重新选择相同的已校验副本继续。

从 0.8.1 起，升级和卸载按文件清单及 SHA-256 校验文件归属。内置清单只覆盖已取得可靠安装包的 0.7.0 和 0.8.0。无法识别的旧版和存在同名冲突的目录会停止升级。旧卸载器不会运行。常规卸载保留 data，清洁安装仍需明确确认。

## 控制条翻译选择

WPF 语言弹出层与引擎菜单共用更多设置的模型清单、语言方向验证、连接检测和 SQLite 设置写入。模型菜单读取已配置档案时只访问本机。确认切换后可按已有连接检测流程检查 AI 服务，检测仅使用固定的测试句。保存成功后更新控制条并同步打开的更多设置。

## 0.8.0 UX 调整实施（2026-11-26）

本次确认的方案已实现，替代上一条“待实施”状态。继续使用 WPF/WebView2、SQLite、凭据管理和现有模型宿主。模型页抽为同源离线资源；原生操作按偏好、模型、数据/存储分部维护。页面使用草稿和离开确认，模型应用以实际检查为前置条件，使用中与上次检测状态分开。

用户数据备份使用受限字段 JSON，不含模型、连接/凭据和采集样本；按知识库和偏好 revision 进行事务恢复，事先生成恢复点。模型迁移校验路径边界和文件摘要后提交引用，保留原文件；目标目录残留不擅自递归清除。临时打包 RID 锁记录与源码依赖锁分离，版本和内容哈希核对后恢复原锁文件。

详见 [发布记录](docs/release-0.8.0.md) 和 [交互审查](docs/ux-review-2026-11-26.md)。真实云端质量及本机安装升级未在本轮执行；回环协议、隔离桌面操作与真实模型质量分别报告。
