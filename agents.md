# Thanslator 编码代理指南（AGENTS.md）

> 0.8.1：安装、升级和卸载按清单校验程序文件归属，不运行旧卸载器。备份恢复和迁移成功后，以已提交快照刷新运行状态，不再回写整份设置。旧版升级和数据变更回归只使用隔离夹具。

> 2026-11-26 / 0.8.0：按 docs/release-0.8.0.md 维护最新交互。场景旧规则不得静默全局化；模型配置与凭据不进入用户数据备份；迁移保留原文件，禁止直接删除外部/共享权重；只读悬注保持无按钮。

> 2026-11-17 最新约定：Ctrl+Shift+D 已移除，自动 OCR 只能在真实且可信的选区触发；开关位于控制条设置页。不要恢复无选区指向识别入口。下文早期版本的热键与 OCR 例外仅为历史记录。

> 面向在本仓库工作的 AI 编码代理与新加入的开发者。
> 最后更新：2026-11-10
> **开始任何任务前，先读 [product.md](product.md) 和 [design.md](design.md)。** 本文件只规定「怎么做」，三者冲突时以 product.md 的产品语义 + design.md 的技术决策为准，并在本文件记录变更。

- **2026-11-10 / 0.6.8**：安装器原目录卸载升级，保留 data 和已装 OCR；点击安装前不卸载，失败停止。回归脚本 `tools/test-installer.ps1` 只使用随机隔离目录/注册表键/假程序名，不得拿真实安装做破坏性测试。

- **2026-11-10 / 0.6.7**：鼠标暂不可访问时暂停并恢复，取消旧请求且不切换桌面；祖先检查支持更深的 UIA 树；热键失败/忙碌/OCR 关闭使用现有小提示，注册时禁自动重复。实际 VS Code 对话区仍需现场验收，见 `docs/capture-fix-2026-11-10.md`。

---

## 1. 当前仓库状态

- **2026-11-10 第二批（0.6.6）**：可选免框选 OCR：原右键菜单/热键、可信选区 dwell 回退、本地独立 CPU 组件、可见区域与遮挡限制、行块选择、常驻原文校对。默认关闭，不自动下载；原文/截图不落盘。代码/小字/复杂版面需核对，跨窗口手工矩阵仍未验收；详见 `docs/second-batch-2026-11-10.md`。

- **2026-11-10 第一批（0.6.5）**：有界 UIA/受控剪贴板、共享隐私门、滚动/前台/选区失效取消、逐段本地 MT/符号完整性、Unicode 输入及常驻长文显示；Release 113 项测试通过。跨应用/DPI/真实剪贴板手工矩阵未完成，OCR/IA2/领域包未实现，详见 `docs/first-batch-2026-11-10.md`。

- 已有文档 + **M3 工程**：`Thanslator.slnx`、`Thanslator.App`（WPF）、`Thanslator.Core`、`Thanslator.Storage`、`Thanslator.Capture`、`Thanslator.Dictionary`、`Thanslator.Translation`、`Thanslator.Tests`。
- M3 已实现：Popper、SQLite 设置、UIA TextPattern 取词、鼠标悬停 dwell、`Ctrl+Shift+D`、透明 Tooltip/Panel、本地 ECDICT 单词/短语释义、人工校订表、**生硬逐词直译与短语开关**、词形还原/所有格处理、Popper 自由拖动/屏幕边缘抽屉/完全隐藏到托盘+toast、本地 MT 术语表、托盘、自包含发布；33 个测试通过。
- 路径 2 原生 MT：已编译 `tools/LocalMtHost`（C++ / CPU-only CTranslate2 + Ruy + SentencePiece）；WPF 已通过 `LocalMtProvider` 连接命名管道，普通句子可显示 OPUS-MT 译文；托盘「本地 MT 设置」可选择模型目录、导入模型 ZIP、删除模型。
- 未实现：模型包公开托管/一键下载、质量评估/术语表、安装包/签名、完整兼容矩阵。不要把这些当成已完成。
- 词典数据：`assets/dictionary/dict-en-zh.sqlite`（本地生成，`.gitignore` 忽略）；构建脚本在 `tools/DictionaryBuilder/`；许可证见 `assets/dictionary/README.md`。
- 运行期数据全部在**便携目录** `data\`（设置库 `data\thanslator.db`、日志 `data\logs\`、本地 MT 模型 `data\models\`）；解析顺序 `THANSLATOR_HOME` → 仓库根 `data\` → exe 旁 `data\`，见 `src/Thanslator.Core/AppPaths.cs`。不要新增写 `%LOCALAPPDATA%` 的代码。
- .NET 10 SDK 安装在 `C:\Users\lenovo\.dotnet`；解决方案使用 `.slnx` 格式。运行/发布说明见 `README.md`。
- 技术栈已定：**.NET 10 / C# / WPF（Windows 10/11）**。Electron 只是 design.md 3.5 的备选，不要在未确认的情况下同时实现两套。

---

## 2. 不可破坏的产品不变量

以下规则是产品的「宪法」。任何实现、重构、优化都不能违反；如果确实需要改，先改 product.md/design.md 并说明理由。

### 2.1 触发

1. **自动触发只有「已选中文本 + 鼠标在选区内停留达到阈值」。** 普通悬停文字不触发。2026-11-10 用户确认的 0.6.6 例外：显式启用 OCR 后，主动热键可识别当前窗口内光标所指文字块；没有选区不能从普通悬停截图。
2. 总开关关闭时，不取词、不请求、不弹容器、不留后台查询。
3. 同一次选区关闭后，必须「离开选区再进入」才允许再次弹出；`Esc` 关闭后尤其如此。
4. 滚动、点击其他位置、前台窗口切换、选区变化、锁屏都必须关闭容器并取消请求。
5. 不得在鼠标移动事件里同步调用 UIA/网络/磁盘；采样线程只做入队。

### 2.2 取词与隐私

6. 默认**不使用剪贴板回退**。剪贴板回退必须用户显式开启，并且备份/恢复是尽力而为，UI 必须如实说明。未开启时首次 UIA 失败可提示一次入口；UIA 返回的选区必须靠近光标（鼠标触发路径）才采信，否则交给剪贴板回退。
7. 密码框（UIA `IsPassword`）、安全桌面、提权窗口、denylist 进程：不读取、不缓存、不发送。
8. 默认不记录选中文本；日志、崩溃报告、遥测都不得包含原文。调试日志必须脱敏。
9. 不注入其他进程，不关闭 UIPI，不默认以管理员运行，不安装全局键盘记录器。
10. API Key 只存 Windows Credential Manager；绝不写入 SQLite、JSON、日志、源码、测试 fixture。

### 2.3 翻译语义

11. **生硬模式的逐词路径绝对不调用机器翻译或 LLM 做整句改写。** 必须：分词 → 逐 token 查词典/规则 → 原序输出；不重排、不补主语、不润色。
12. 生硬模式遇到未知词：保留原文并标记 unknown，不编造释义。可选在线词典补单词，但不得调用 MT。
13. `includePhrases` 只影响「用户选中的短语」这一入口：
    - `true`：短语也逐词；
    - `false`：只查短语词典/习语，未收录则提示，**不调用 MT**（这是显式例外，不是逐词路径）。
    - 句子/段落永远逐词，不因该开关把习语合并。
    - **2026-11-10 补充**：短语词典未命中时，锁序仍要求 ≥3 词才按句子逐词；通序 ≥2 词可按句子走本地 MT。命中的短语仍按开关。原因：短句/从句被判为短语时，不回退会直接显示「未收录」。
14. 普通模式（Fluent）必须语序通顺；不能输出逐词拼接结果。
15. 单词/短语优先给「常见释义」的义项列表，不把 MT 的单个结果冒充词典释义。
16. 结果模型必须包含 `sourceLang`、`targetLang`、`senses`/`units`、`provider`；**不得新增只适用于中文的字段或分支**。
17. 新增语言对时，优先新增词典包/Provider 能力，不改 UI 主流程与数据库 schema。

### 2.4 呈现

18. Tooltip 和 Panel 都必须出现在**选区下方**，空间不足才翻转到上方；不得遮挡选区，必须限制在显示器工作区。
19. Tooltip 只读、点击穿透、无按钮；Panel 可交互、有关闭叉号、**默认不自动关闭**（自动关闭为可选项）。
20. 浮层出现/消失不得抢焦点；Panel 只在用户点击其控件时才允许获取焦点。
21. 半透明背景必须满足文字对比度 ≥4.5:1；高对比度主题改为不透明系统色。
22. 多显示器/混合 DPI 必须用物理像素计算，再换算 DIP；禁止用主屏缩放处理所有屏幕。

**UI 文案固定，代码内部枚举不变：**

- 用户可见 **悬注** = `SurfaceMode.Tooltip`；**常驻** = `SurfaceMode.Panel`。
- 用户可见 **锁序直译** = `LiteralMode=true`；**通序翻译** = `LiteralMode=false`。
- 用户可见 **短语拆分** 开关 = `IncludePhrases`；UI 不显示「含短语 / 不含短语」文字。
- UI 品牌为 **简译**（英文 JIANYI，2026-11-08 起；新视觉见 `brand-guidelines (1).html`）；代码命名空间/仓库仍为 `Thanslator`。
- Popper 宽度 **310 DIP**（紧凑版，见 `preview-balanced (1).html`）；标题行右侧依次为抽屉按钮、`—` 隐藏按钮、总开关（32×16 渐变胶囊），**不显示开启/关闭小字**；不显示 M1/暂停；模式与呈现方式使用分段控件（segmented）；短语开关仅锁序模式出现。
- 完全隐藏到托盘时显示 2.2 秒小 toast，不弹系统大通知；单击托盘图标恢复并展开。

### 2.5 设置与数据

23. 所有开关（总开关、生硬模式、短语、呈现方式、语言、来源、位置）变更即写 SQLite；重启必须恢复。
24. 设置键使用 key-value + JSON，新增开关不要求 schema 迁移；但 `Settings` 结构体必须有默认值、枚举校验、损坏回退。
25. 用户设置数据库与词典数据库分离；升级词典不得改动用户设置。
26. 缓存 key 必须包含所有影响输出的参数：文本、源/目标语言、模式、`includePhrases`、provider、model、prompt/glossary 版本、schema 版本。否则模式切换会串结果。
27. 缓存默认不保存原文；生硬模式结果（含原文 token）不写缓存，本地词典结果不缓存，仅普通模式远程结果可缓存。历史记录为 P2；若加入，默认关闭、开启后 7 天保留并支持一键清空。
28. **本地词典优先，默认不配置远程源**：单词/短语不依赖网络；句子/段落未安装本地 MT 且未配置远程源时，返回 `localmt.not_installed` / `provider.not_configured` 并引导下载模型或设置官方源；不得自动调用非官方接口。
29. **本地 MT 默认不下载**：只有用户显式点击下载并安装模型后才启用；不得自动下载、自动启用或把模型文件提交到 Git；本地 MT 不联网。

---

## 3. 架构规则

### 3.1 依赖方向

```text
Thanslator.App ──> Thanslator.Capture ──> Thanslator.Core
      │                  │
      ├──────────────> Thanslator.Translation ──> Thanslator.Dictionary
      │                                          └> Thanslator.Providers
      └──────────────> Thanslator.Storage
```

- `Core` 不引用 WPF、Win32、HTTP、SQLite；只放模型、接口、错误码。
- `Translation` 放分类器、直译器、编排器与缓存协调；可以依赖 `Dictionary`/`Providers`，不依赖 UI。
- `Providers` 只依赖 `Core` 接口；每个 provider 可独立 mock。
- `Capture` 不直接发网络请求；只产出 `SelectionSnapshot`。
- `LocalMtHost` 是 `Providers/LocalMt` 管理的独立进程，通过命名管道通信；不要把它链接进 WPF 进程。
- `App` 负责编排状态机与 UI；不要把业务规则塞进 code-behind。
- 禁止循环依赖；发现循环先拆接口/模型，不要用 `InternalsVisibleTo` 糊过去。

### 3.2 必须存在的抽象

- `ITranslationProvider`：`TranslateAsync(TranslationRequest, CancellationToken)`。
- `IDictionaryProvider`：`LookupAsync(DictionaryLookupRequest, CancellationToken)`。
- `ITokenizer` / `IGlosser`：按语言注册；生硬模式只依赖它们。
- `ISelectionCapture`：UIA/IA2/剪贴板/热键实现可替换。
- `ISettingsRepository`、`ITranslationCache`、`ISecretStore`：便于测试与替换。
- `ITranslationOrchestrator`：负责 provider 链、超时、降级、缓存、generation 丢弃。

不要在 UI 里直接 `new HttpClient()` 或直接读注册表；通过接口注入。

### 3.3 线程模型

- WPF UI 线程：只做 UI；不阻塞等待。
- UIA：独立无窗口 MTA 工作者，有界队列与超时等待；剪贴板使用独立 STA 工作者。超时不能强制中断挂起 COM，不得无限新建替代线程；依据微软线程建议与 design.md ADR-025。
- 鼠标输入：轮询线程每 8ms 采样 `GetCursorPos`/`GetAsyncKeyState`（2026-11-08 起替代 `WH_MOUSE_LL`，见 design.md §6.5 / ADR-014）；判定逻辑在 `MouseInputTracker`，不要在 UI 线程做采样或判定。
- 网络：`async/await` + `CancellationToken`；禁止 `.Result`/`.Wait()`。
- 数据库：单写连接；读可用连接池；不要跨线程共享 `SqliteConnection`。
- 所有跨线程 UI 更新走 `Dispatcher`；不要从后台线程直接改 `ObservableCollection`。

### 3.4 错误与取消

- 每个请求带 `CancellationToken`；新选区/关闭容器时取消旧请求。
- 用 generation/版本号丢弃过期响应；不能让旧结果覆盖新选区。
- Provider 错误分类：`network`、`timeout`、`quota`、`rate_limit`、`unsupported_language`、`auth`、`server`、`unknown`。
- 只有网络/5xx/超时可重试一次；401/403/额度/参数错误不重试。
- UI 显示本地化错误码，不显示原始异常/HTTP body。

---

## 4. 计划目录结构

```text
Thanslator.sln
src/
  Thanslator.App/
    App.xaml / App.xaml.cs
    Tray/                     NotifyIcon、菜单
    Popper/                   常驻小图标与控制条
    Overlay/                  Tooltip/Panel 窗口、定位、动画
    Settings/                 设置窗口与 ViewModel
    ViewModels/
    Interop/                  WPF 窗口样式、DPI、WM_ 消息
  Thanslator.Core/
    Models/                   LanguageTag, TranslationResult, GlossUnit, Sense...
    Abstractions/             ITranslationProvider, IDictionaryProvider, ITokenizer, IGlosser...
    Errors/
  Thanslator.Translation/
    Classification/
    Glossing/
    Orchestration/            Provider 链、超时、降级、generation
    Caching/
  Thanslator.Capture/
    Uia/                      TextPattern 取词
    Ia2/                      IAccessibleText 回退
    Clipboard/                备份/恢复/SendInput
    Hooks/                    鼠标输入轮询（MouseHook/MouseInputTracker）、热键
    SelectionSnapshotFactory
  Thanslator.Dictionary/
    Ecdict/                   SQLite 查询、translation 解析
    Tokenization/
    Lemmatization/
    Phrases/
    Overrides/
  Thanslator.Providers/
    Azure/
    GoogleCloud/
    Baidu/
    Alibaba/
    KeylessGoogle/
    KeylessEdge/
    MyMemory/
    LocalMt/                  LocalMtProvider、命名管道客户端、模型状态
    Llm/                      Groq/Gemini/Ollama（可选）
    Shared/                   限流、错误映射、HTTP 基础设施
  Thanslator.Storage/
    Sqlite/
    Migrations/
    SettingsRepository
    TranslationCache
    SecretStore               Credential Manager / DPAPI
  Thanslator.Tests/
    Unit/
    Integration/
    Fixtures/
assets/
  dictionary/                 dict-en-zh.sqlite（构建产物）
  overrides/
    function-words.json
    polysemy.json
    phrases.json
  licenses/THIRD_PARTY_NOTICES.md
tools/
  DictionaryBuilder/          从 ECDICT CSV 构建只读词典
  LocalMtHost/                C++/CTranslate2 辅助进程（可选组件）
  LocalMtBuilder/             模型下载、转换、量化、打包、hash/许可清单
  publish.ps1                 测试→publish→整理→Unblock→编译安装包
installer/
  Jianyi.nsi                  NSIS 安装包（按用户装到 %LOCALAPPDATA%\Programs\Jianyi）
docs/                         可选设计笔记；根目录保留三份主文档
```

**生成物与源码分离**：`assets/dictionary/*.sqlite` 由 `tools/DictionaryBuilder` 生成，不手改；源码中不提交 66MB CSV（除非构建流程明确要求，优先用下载脚本 + hash 校验）。

---

## 5. 命令

> 本机 SDK 在 `C:\Users\lenovo\.dotnet\dotnet.exe`（全局 `dotnet` 可能找不到 SDK）。解决方案是 `.slnx`。

```powershell
# 还原/构建
dotnet restore
dotnet build Thanslator.slnx -c Debug

# 运行 WPF 应用（开发期）
dotnet run --project src/Thanslator.App/Thanslator.App.csproj

# 测试
dotnet test Thanslator.slnx -c Debug
# 若 testhost 在沙箱里无法 OpenProcess 父进程（Win32Exception 5），需要自建 runner
# 加载测试程序集（旧 artifacts/xunit-shim 已随调试产物清理）。

# 发布（自包含 win-x64 + NSIS 安装包）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1
# 安装器隔离回归（首次安装、卸载升级、失败停止、data/OCR 保留）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-installer.ps1
# 已安装 OCR 的升级需带组件包；基础包会在卸载前停止，避免丢失 OCR
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -OcrComponentDirectory build/ocr-dist/Jianyi.OcrHost
# 沙箱里 testhost 起不来时加 -SkipTests
# 安装包产物：dist/Jianyi-Setup-<版本>.exe
# 可选 OCR 组件构建（隔离环境准备见 tools/OcrHost/README.md；只读复用已有模型）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/OcrHost/build.ps1
# 发布时将组件列为可选安装项，默认不勾选、不自动启用
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -OcrComponentDirectory build/ocr-dist/Jianyi.OcrHost
# NSIS 复用 Tauri 下载的 %LOCALAPPDATA%\tauri\NSIS\Bin\makensis.exe，不需要另外安装。
# 安装到 %LOCALAPPDATA%\Programs\Jianyi，按用户安装、无 UAC；data\ 保留在 exe 旁。

# 手动等价步骤（脚本内部执行）：
#   dotnet restore src/Thanslator.App/Thanslator.App.csproj -r win-x64
#   dotnet publish src/Thanslator.App/Thanslator.App.csproj `
#     -c Release -r win-x64 --self-contained true -o dist/M5-folder --no-restore
#   复制 build/LocalMtHostRuy/Thanslator.LocalMtHost.exe 和 ctranslate2.dll 到 dist/M5-folder/LocalMtHost/
#   删除 *.pdb、createdump.exe、非中文语言目录，Unblock-File，makensis installer/Jianyi.nsi
#   手工 makensis 后同样要 icacls <安装包> /setintegritylevel Medium（Low 标签会让静默安装静默失败）

# 单文件发布（可选；尚未完成启动验证，不要作为唯一发布物）
# dotnet publish ... -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 构建词典（M2 工具尚未创建；以下为目标命令）
dotnet run --project tools/DictionaryBuilder -- `
  --ecdict path/to/ecdict.csv `
  --overrides assets/overrides `
  --out assets/dictionary/dict-en-zh.sqlite

# 构建本地 MT Host（可选组件；以实际 CMake 配置为准）
cmake -S tools/LocalMtHost -B build/localmt -DCMAKE_BUILD_TYPE=Release
cmake --build build/localmt --config Release

# 准备本地 MT 模型包（示例；以实际工具参数为准）
python tools/LocalMtBuilder/prepare.py `
  --model opus-mt-en-zh --quantize int8 --out dist/localmt
```

规则：

- 提交前至少跑 `dotnet build` 和受影响范围的 `dotnet test`。
- 不在仓库里提交 `bin/`、`obj/`、用户数据库、API Key、日志、词典大 CSV、本地 MT 模型文件。
- 若新增命令，更新本节；不要只写在 PR 描述里。

---

## 6. 编码规范

### 6.1 C# / .NET

- .NET 10，C# 最新稳定版；`Nullable` 开启；`TreatWarningsAsErrors` 开启（或至少 CI 开启）。
- 文件级 namespace；`var` 仅在类型明显时使用；公共 API 写 XML 注释。
- 异步：`async Task`/`ValueTask`；后缀 `Async`；传 `CancellationToken`；禁止 `async void`（事件处理器除外）；禁止 `.Result`/`.Wait()`。
- 不可变模型优先 `record`/`init`；集合返回 `IReadOnlyList<T>`。
- P/Invoke：集中放 `Interop/`，优先 `[LibraryImport]` 源生成；结构体显式布局；检查返回值/Win32 错误。
- 资源：`using`/`await using`；窗口、轮询线程、句柄、DB 连接必须释放。
- 异常：只捕获能处理的；不要 `catch (Exception) { }`；错误转错误码，不把异常消息直接给 UI。
- 日志：结构化、无原文、无 Key；用 `ILogger`，不要 `Console.WriteLine` 进生产。
- UI：MVVM；code-behind 只做视图相关；`CommunityToolkit.Mvvm`（MIT）可选；命令用 `ICommand`/`RelayCommand`。
- 包管理：`Directory.Packages.props` 集中版本；新增依赖必须检查许可证（MIT/Apache-2.0/BSD 优先；GPL/AGPL/非商业需明确批准）。

### 6.2 命名

- 领域术语统一：`SelectionSnapshot`、`TranslationRequest`、`TranslationResult`、`GlossUnit`、`Sense`、`SurfaceMode`、`TranslationMode`。
- 不用 `HardTranslate`/`RawTranslate` 等多种叫法；产品对外叫「生硬翻译」，代码用 `Literal`。
- Provider ID 用稳定小写：`azure`、`google-cloud`、`baidu`、`alibaba`、`deepl`、`google-unofficial`、`edge-unofficial`、`mymemory`、`groq`、`gemini`、`ollama`。
- 语言标签统一 BCP-47 字符串；内部首选 `zh-Hans`，对提供商做参数映射。

### 6.3 注释与文档

- 注释解释「为什么」，不重复代码。
- 平台/API 的坑（UIA 挂起、DPI、剪贴板、WS_EX_*）必须写注释并链接设计文档章节。
- 修改产品语义/技术决策时，同步更新 product.md/design.md，并在 design.md 21.3 增加 ADR 行。
- 不把研究结论写成永久事实；额度/许可证标注「截至 2026-11，发布前复核」。

---

## 7. 关键实现指引

### 7.1 新增 Provider

1. 在 `Thanslator.Providers/<Name>/` 实现 `ITranslationProvider`（需要词典则同时实现 `IDictionaryProvider`）。
2. 声明 `ProviderCapabilities`：支持语言对、是否官方、是否支持词典/逐词/流式、最大请求长度、是否需要 Key。
3. 用 `HttpClient` + 超时 + `CancellationToken`；错误映射到 `ProviderError` 枚举；不要吞异常。
4. 单元测试用固定 JSON fixture；禁止在测试里打真实网络。
5. 在 `ProviderRegistry` 注册；在设置向导/设置页增加条目；更新 design.md 9.3/9.4。
6. UI 必须显示「官方/非官方」标签；非官方必须受 `allowUnofficialProviders` 控制。

### 7.2 新增语言对 / 词典包

1. 词典包命名 `dict-{src}-{tgt}.sqlite`，带 `schema_version`、`source_hash`、`built_at`、许可元数据。
2. 语言包只读；不要合并进用户设置 DB。
3. 注册 tokenizer/phrase matcher/lemma 策略；没有专用分词器时用 UAX #29 默认。
4. 在 `LanguageRegistry` 增加显示名/方向/提供商映射；UI 从 registry 读取，不硬编码中文。
5. 若数据是 CC BY-SA，保持独立文件并更新 `THIRD_PARTY_NOTICES.md` 与应用内许可证页；不要让 ShareAlike 污染代码库。
6. 验收：至少 50 个常见词 + 5 个短语 + 2 个句子的人工检查；记录覆盖率。

### 7.3 新增设置项

1. 在 `Settings` 结构体加属性 + 默认值 + 枚举校验。
2. 在 `SettingsRepository` 读写 JSON；不需要 schema 迁移。
3. 若影响翻译输出，必须加入缓存 key（design.md 8.6）。
4. 在设置 UI/Popper 中暴露；变更即写；失败回滚。
5. 更新 design.md 11.3 的表。

### 7.4 修改生硬模式

- 先写/改测试：原序、标点、功能词、未知词、连字符、所有格、URL/代码、短语开关。
- 不得为了「读起来更顺」调整 token 顺序；这是产品定义。
- 不得调用 MT；若引入在线词典补词，必须只影响单 token，不改变顺序。
- 覆盖表变更要有中文审校记录（谁改、为什么、示例）。

### 7.5 修改浮层

- 先确认 `WS_EX_TRANSPARENT` 与交互控件的取舍；Tooltip 不得同时可点击。
- 任何定位改动都要测：屏幕底部翻转、任务栏、双屏、负坐标、100/150/200% DPI。
- 不得让浮层出现时改变 `GetForegroundWindow()`；用 `ShowActivated=False` + `WS_EX_NOACTIVATE` + `WM_MOUSEACTIVATE -> MA_NOACTIVATE`。
- Panel 默认不自动关闭。若用户启用自动关闭，计时器在指针进入时暂停、离开后重置；最后 5 秒才显示倒计时。
- 复制按钮复制完整结果，不复制被截断的显示文本。

### 7.6 本地 MT（A 方案）

- `LocalMtHost.exe` 是独立进程（C++/CTranslate2），通过命名管道 `\\.\pipe\Thanslator.LocalMt` 与主程序通信；不要把 CTranslate2 直接链接进 WPF 进程。
- 协议用 JSON lines：请求含 `text`、`sourceLang`、`targetLang`、`modelId`；响应含 `translation`、`latencyMs`、`error`。Host 只监听命名管道，不监听网络端口。
- 模型默认不下载；用户点击「下载本地翻译模型」后才开始，显示进度、可取消，完成后校验 SHA-256 与签名清单。
- `localMtEnabled=true` 且 `localMtModelStatus=ready` 时才走本地 MT；否则返回 `localmt.not_installed`，提示下载/配置官方源。
- Provider 顺序：本地 MT（若就绪）→ 用户配置的官方主源 → 官方备用 →（非官方仅显式启用）→ 错误。不得绕过用户配置自动走非官方。
- Host 崩溃重启一次；空闲 5 分钟退出；模型文件不提交 Git；模型包必须附 `LICENSE`/`SHA256SUMS`。
- 质量标注「本地 MT」；不要声称与云端/权威词典等价。抽样测试记录明显错译，不要求逐句完美。
- 模型选择优先 OPUS-MT + CTranslate2（CTranslate2 MIT；OPUS-MT 原始模型 README 标 CC-BY 4.0，需复核/署名）；Argos 作为备选，模型许可逐个确认；不要使用 NLLB（非商用）。

---

## 8. 测试要求

### 8.1 必须有的测试

- `TextClassifier` 的边界用例（1 词、2 词、5 词、8 词、9 词、标点、换行、中英混排）。
- `LiteralGlosser` 的顺序/标点/未知词/短语开关/URL/代码/所有格。
- `DictionaryService` 的 ECDICT 解析、POS 前缀、领域标签、词形还原、短语最长匹配、覆盖表优先级。
- `CacheKey` 对 mode/includePhrases/provider/model 的敏感性。
- `SettingsRepository` 的默认值、写读、重启恢复、损坏回退、迁移。
- `TriggerController` 的 dwell、移动重置、Esc、冷却、同选区抑制、generation。
- Provider 响应解析与错误映射（fixture，不打真实网络）。
- `LocalMtProvider`/Host 协议：模型未安装、ready、Host 崩溃重启、超时、空输出、错误码。
- 模型安装：SHA-256、取消、损坏包、删除、磁盘不足。
- 数据库迁移：全新安装 + 从上一版本升级。

### 8.2 手工矩阵（每次 UI/取词改动）

- Chrome / Edge / Firefox；
- 记事本 / VS Code；
- Word / Acrobat（文本层 PDF）；
- 100% / 150% / 200% + 双屏混合 DPI；
- 浅色 / 深色 / 高对比度；
- 密码框不取词；
- 关总开关后无任何请求；
- 本地 MT：模型未安装→提示；下载/安装/删除；断网翻译；Host 崩溃重启；
- 强杀进程后设置恢复。

### 8.3 Definition of Done

一个任务完成必须满足：

- [ ] 行为符合 product.md 与 design.md；
- [ ] 新增/修改逻辑有单元测试；
- [ ] `dotnet build` 无警告（或已说明例外）；
- [ ] 相关测试通过；
- [ ] 涉及 UI/取词时，完成手工矩阵中受影响项；
- [ ] 无原文/Key 进入日志、缓存默认值或测试 fixture；
- [ ] 文档（尤其 design.md 的决策/设置表/Provider 表）同步更新；
- [ ] 没有引入未审查许可证的新依赖。

---

## 9. 隐私与安全检查清单

提交涉及取词、网络、存储、日志的 PR 前逐项确认：

- [ ] 没有在 UIA 读取前漏掉 `IsPassword` 检查；
- [ ] 没有在 denylist/安全桌面/提权窗口取词；
- [ ] 剪贴板回退仍然默认关闭，且备份/恢复失败有降级；
- [ ] 没有记录选中文本原文（日志、异常、遥测、崩溃报告）；
- [ ] API Key 只从 Credential Manager 读取，没有落盘；
- [ ] 远程请求只在用户触发后发生，没有预取/后台扫描；
- [ ] 本地 MT 不产生网络请求；模型只在用户点击下载后获取，且校验 SHA-256；
- [ ] 非官方 Provider 受 `allowUnofficialProviders` 控制并在 UI 标注；
- [ ] 缓存 key 包含所有影响输出的参数；
- [ ] 用户可清空缓存/密钥（若 P2 历史已启用，则一并清空）；
- [ ] 安装包/更新包签名与哈希校验（发布相关）。

---

## 10. 不要做的事

- 不要用 Electron/Tauri 再写一套「备用实现」，除非 design.md 3.4 的验证门失败并已更新决策。
- 不要把生硬模式实现成「MT 后逐词对齐」；MT 已经重排语序，无法还原。
- 不要为了省事把 `includePhrases` 用于句子内习语合并；它只控制选中短语的入口。
- 不要在 UI 线程调用 UIA、`SendInput`、SQLite 同步 IO 或网络。
- 不要把 `WS_EX_TRANSPARENT` 加到需要点击关闭/复制的 Panel 上。
- 不要用全屏 bounds 代替显示器 `rcWork` 做定位。
- 不要把 ECDICT 的 `collins`/`oxford` 字段做成品牌徽章。
- 不要内置任何开发者 API Key，也不要把 Key 写进示例配置。
- 不要承诺「永久免费」「无限翻译」「100% 取词成功」。
- 不要在没有复核的情况下把 2026-11 的额度/许可证写成永久事实。
- 不要手改 `assets/dictionary/*.sqlite`；改 `tools/DictionaryBuilder` 与覆盖表。
- 不要自动下载本地 MT 模型，或把模型文件提交到 Git；不要把 CTranslate2 直接链接进 WPF 进程。
- 不要在未确认产品语义时擅自改短语开关/自动关闭/呈现方式的行为。

---

## 11. 已确认与仍待确认

### 已确认（2026-11-07）

1. 短语 = 2–8 个单词；锁序 `includePhrases=false` 只查短语词典、不调用 MT；`true` 逐词直译。**2026-11-10 补充**：未命中时锁序 ≥3 词按句子逐词，通序 ≥2 词可按句子走本地 MT。
2. 单词/短语默认本地 ECDICT + 校订表；应用不预配置远程源。
3. Panel 默认不自动关闭，保留关闭叉号；自动关闭为可选项。
4. 发行范围：个人使用优先，后续可能上传 GitHub；公开前再做许可证/签名/词典分发评估。
5. 句子/段落翻译：可选下载的本地 MT（默认不下载、零 Key）优先；官方远程源作为可选高质量备选。

### 仍待确认（不要擅自决定）

1. 本地 MT 模型选型：OPUS-MT + CTranslate2 int8 优先，Argos 备选；需实测质量、CPU 延迟、模型体积与转换链。
2. 历史记录/生词本（P2）：默认关闭；是否进入下一版本。
3. GitHub 开源策略：代码许可证；ECDICT/本地 MT 模型随 Release 分发、用户自建，还是仅提供构建脚本。
4. 浏览器扩展 / macOS 是否进入下一版本；OCR 已经用户确认进入 0.6.6，可选且默认关闭。

如果任务触及以上事项，先在回复中提出，不要默默选一个实现。

---

## 12. 代理工作流建议

1. **读文档**：product.md → design.md → 本文件相关章节。
2. **确认现状**：`git status`、目录结构、现有测试；不要假设脚手架存在。
3. **写计划**：列出要改的文件、要加的测试、要更新的文档；大改动先说明取舍。
4. **小步实现**：先 Core/测试，再 Provider/Dictionary，最后 UI；每步可编译。
5. **验证**：跑测试 + 手工矩阵受影响项；UI 改动附截图/录屏（如环境允许）。
6. **更新文档**：设置/Provider/决策变化同步 design.md；命令/目录变化同步本文件。
7. **报告**：说明做了什么、没做什么、验证结果、剩余风险；不要虚报测试通过。

---

## 13. 相关文档

- [README.md](README.md)：M1 当前状态、运行/测试/发布命令、已知限制。
- [product.md](product.md)：产品语义、功能需求、验收标准、优先级。
- [design.md](design.md)：技术选型、架构、算法、来源、schema、测试、里程碑。
- `assets/licenses/THIRD_PARTY_NOTICES.md`（待创建）：ECDICT/Kaikki/CC-CEDICT 等署名与许可证。
- `docs/`（待创建）：可选的研究笔记；不要把临时研究 JSON 提交进仓库。

## 0.6.14（2026-11-25）
更多设置入口为 MoreSettingsWindow，两个旧设置窗口已移除。发布使用 tools/Optimize-Release.ps1，保留原 OCR 构建供对照。tools/SettingsSmoke 可运行隔离的真实窗口检查及 OCR 对照；不得拿用户数据作为测试夹具。默认下载位置为简译 data/models，可在模型配置页改为自选目录。

