# 简译：翻译体验审查与领域知识扩展建议

审查日期：2026-11-10。本文是审查建议，不代表产品需求已经变更。

**实施更新**：第一批已获批准并落地，见[0.6.5 交付记录](first-batch-2026-11-10.md)。下文保留修改前的审查依据；领域知识包仍为后续方案。

用户随后补充“代码等内容识别不到，希望各种情况都有取词入口，先给方案”。已补充[取词覆盖与代码场景方案](capture-plan-2026-11-10.md)。最新建议是先并行解决取词可靠性与本报告中的翻译链缺陷，再做领域包；这不代表承诺任何场景 100% 识别。

本轮阅读了 product.md、design.md、AGENTS.md，以及分类、分词、词典查询、逐词直译、术语保护、本地 MT、浮层呈现、请求生命周期和相关测试。实际只读查询了本地 ECDICT，并调用现有 Host 与已安装模型做了诊断。没有修改产品源码、词典或用户设置。

## 1. 结论与实施顺序

当前体验问题同时来自输入分类、占位符保护、输出校验、术语作用域和模型能力。单纯换更大词库，或者直接增加 RAG，不能解决这些问题。

建议顺序：

1. **先稳定翻译链路**：Unicode 空白分类、符号/专名保护、长文本切分、局部失败、真正取消请求。
2. **再补有来源的专业术语包**：通用词典保持兜底，领域包显式选择，专业词义不能污染普通阅读。
3. **最后增加按需的知识解释**：只在常驻面板中显式触发，走检索与支持指令的模型，和锁序直译分开。

产品定位建议保持为“Windows 英文技术阅读助手”：就近出现、离线基本可用、原序对照、专业术语可追溯。不要优先扩展成 OCR、写作、聊天、全平台、多服务大集合。定位建议是本次判断，不是已经证明的市场优势。

## 2. 本轮验证范围

- SDK：本机 .NET 10.0.403。
- `dotnet build Thanslator.slnx -c Debug --no-restore`：成功，0 警告、0 错误。
- `dotnet test Thanslator.slnx -c Debug --no-restore`：73 通过，0 失败、0 跳过。
- 模型诊断：仓库现有 `build/LocalMtHostRuy/Thanslator.LocalMtHost.exe`，配置中指向的 `D:\Jianyi\data\models\opus-mt-en-zh-int8`，4 线程，独立命名管道；没有启动或操作用户安装的 WPF 主程序。
- 诊断程序：[Program.cs](../artifacts/translation-audit/Program.cs)，结果：[results.jsonl](../artifacts/translation-audit/results.jsonl)。`artifacts/` 被 Git 忽略；该诊断固定了本机模型路径，不是发行功能。
- 测试通过只证明现有测试的断言成立。现有测试没有覆盖下述真实模型失败。

## 3. 已确认的翻译问题

### 3.1 PDF 常见空白会使整句只查词典

位置：`src/Thanslator.Core/TextClassifier.cs:44`，调用方 `App.xaml.cs:568`、`DictionaryService.cs:70`。

`CountWords` 只按 ASCII 空格和制表符拆分。文本中的 U+00A0 不换行空格没有被识别为词边界。

实测：`The\u00A0system\u00A0handles\u00A0incoming\u00A0requests.` 被分类成 `Word`，词典查询返回“本地词典未收录”。即使句末有句号，也先命中 `tokens <= 1`，因此没有进入 MT。

这是可以复现的“整句不翻译”路径。它可能解释用户在 PDF/网页中的部分体验，但没有用户原始失败句，不能把所有情况都归到这里。

建议：共享分类和查词的输入规范化；NFC、Unicode 空白、软连字符和 PDF 断行分别处理，保留原文用于展示。不要无差别删除符号，尤其不能改坏公式、代码、单位和路径；跨行去连字符须谨慎确认。增加 NBSP、窄不换行空格、弯引号、软连字符、混合换行的测试。

### 3.2 保护占位符本身被模型破坏

位置：`LocalMtProvider.cs:75`、`TextProtector.cs:68`、`TranslationGlossary.cs:75`。

当前将 URL、路径、代码、数字等变成 `⟦0⟧`，将部分专名变成 `⟦T0⟧`，翻译后按字符串/正则还原。

通过实际 C# Provider → Host → 模型的结果：

| 原句 | 当前结果摘要 | 当前输出校验 |
|---|---|---|
| `I use GitHub and Docker to build the application.` | `我用“T0”和“T1”来构建应用程序。` | 放行 |
| `Visit https://example.com and read the documentation before starting the server.` | `在启动服务器之前访问 ⟦0 并读取文档。` | 放行 |
| `If x ≤ 5, the system returns a result within 3.5 ms.` | 开头少量中文，随后大量 `>>cmn_Hans<<`，完整输出 1475 字符 | 放行 |
| 在普通英文句中插入 `☃` | 此样本正常翻译且保留 `☃` | 放行 |

因此，不能说“任何未知符号都会使模型失败”；失败取决于输入、保护方式和模型。直接绕过保护向同一 Host 发送含 GitHub/Docker 的原句时，专名正常保留；这说明保护机制在该样本中反而制造了问题，但不证明所有 URL/数字都可以裸传。

建议：

- 先缩小无差别保护范围，尤其不要默认把所有普通数字和模型原本能处理的专名都掩码。每次调整都要验证数值、单位、专名不丢失。
- 以“可逆且校验通过”为保护成功条件。校验每个占位符的身份和出现次数，发现损坏再进行有界回退；不能把缺失内容随便追加到句末。
- 不要只把 `⟦…⟧` 换成另一组字符就宣布修好；新的写法也必须通过真实模型测试。
- 公式、代码、路径等按完整片段处理；不能把数学关系符当垃圾删除。
- 单句仍失败时保留原句并标明未译；其他句子照常呈现。通序路径不得用逐词拼接冒充通顺译文。

### 3.3 输出校验既漏检，也会整段拒绝

位置：`TextProtector.cs:92`、`LocalMtGuard.cs:62`、`tools/LocalMtHost/main.cpp:83`、`:171`。

- `TextProtector.IsComplete` 没有接入生产翻译链。
- 它只检查 `Contains`，两次相同的 `42` 丢掉一次仍会被判断完整；本轮自检已验证。
- C# 校验只覆盖空、替换字符、无字母数字、整句原样返回，不能识别上述占位符损坏和语言标记重复。
- Host 在 beam/greedy 都没有通过 `IsSaneOutput` 时，仍可能返回 `first_decoded`，使前面的质量检查失去兜底约束。
- C# 遇到任意 `�` 会拒绝整条译文；当前没有逐句结果和局部错误结构。

建议：传回结构化错误与警告；同时检查残留标记、异常重复、受保护内容数量、明显截断。规则只能识别部分损坏，不能当成语义质量评分。把错误限制到句子/片段，不能因为一个失败句把整段可用结果丢掉。

### 3.4 段落整块送模型，存在截断与错误扩散

位置：`LocalMtProvider.cs:77`、`tools/LocalMtHost/main.cpp:132`、`:180`。设计文档要求按句处理，当前没有实现。

Host 将整个输入一次编码、一次推理；输出上限设为 512 token。所用 CTranslate2 4.8.5 的 `TranslationOptions` 输入默认截断上限为 1024 token，Host 没有覆盖这个设置。长文本存在静默丢尾和输出不完整风险；本轮没有对超长输入做端到端截断测量。[CTranslate2 4.8.5 源码](https://github.com/OpenNMT/CTranslate2/blob/v4.8.5/include/ctranslate2/translation.h)

建议：保留段落边界 → 按句切分 → 超长句按从句边界及模型 token 预算再切分。不能直接按每个句点拆，必须覆盖 `U.S.`、版本号、小数、URL、代码和引号。候选切分策略可参考 Argos 的段落/句子处理，不需要为此引入它的整个 Python 运行环境。[Argos translate.py](https://github.com/argosopentech/argos-translate/blob/master/argostranslate/translate.py)

### 3.5 丢弃旧响应不等于取消旧推理

位置：`App.xaml.cs:660`、`:847`、`LocalMtProvider.cs:65`。

关闭/新选区会增加 generation，但没有取消正在运行的查询；MT 只有独立的 30 秒超时。Provider 通过 SemaphoreSlim 串行处理，新查询可能等待已经不需要的旧请求。

建议：每次选择持有 CTS，关闭、关闭总开关、选区变更、前台切换时主动取消，所有词典/MT 调用传递同一个取消令牌。原生推理没有现成取消协议，仅关闭管道不保证停止计算；需要明确采用取消协议或终止本应用拥有的 Host，并处理重连。不得误杀借用连接的其他 Host。

### 3.6 两词短语和带括号单词容易形成死路

位置：`TextClassifier.cs:51`、`DictionaryService.cs:338`、`App.xaml.cs:590`。

- 短语未命中回退要求至少 3 词，`attention mechanism` 这类两词术语未收录就不会进入通序 MT。
- `(kernel)` 查不到，`kernel` 可以查到。规范化没有清理成对外围括号/引号。
- 直接查词与分类使用的规范化规则不一致。

建议：在通序模式中，两词短语未命中允许 MT；锁序模式保留现有“不擅自调用 MT”的规则。前者属于产品行为变化，应先同步 product.md/design.md；锁序两词可提示开启短语拆分，不能偷偷改变语义。外围标点处理需覆盖 C++、C#、.NET 等反例。

## 4. “词库少”的实际组成

本轮只读 SQLite 实际统计：770,611 条 entries，其中 366,599 条含空格，373 条释义为空。不能把所有多词条目都当成高质量习语。当前人工表规模：功能词 113、首选义项 5、词典补充 9、MT 术语条目 21。

真正不足的是现代术语覆盖、领域词义、别名/大小写处理和义项质量，不是总条目数。

| 实测查询 | 当前情况 | 应补什么 |
|---|---|---|
| `embedding` | 只有埋置、包埋、植入等旧领域释义 | 机器学习义项、领域来源 |
| `RAG` | 按 `rag` 查到碎布、抹布等 | 大写缩略语和领域义项 |
| `reranker` / `embeddings` | 基础库没有对应精确词条 | 新词、别名、词形映射 |
| `attention mechanism` | 未收录，且两词没有 MT 回退 | 多词术语、路由改进 |
| `gate driver` | 有“门驱动器” | 电子领域审校与替代义项 |
| `C++` / `node.js` | ECDICT 原库没有，但应用通过补充表查到 | 证明小型覆盖表已经有效 |

`DictionaryService.ParseTranslation` 会删除 `[计]`、`[医]` 等标签，再把相同词性的义项合并、截断；这使应用无法按领域排序。`WordNormalizer` 统一小写，无法保留 `RAG/rag` 的区别。`LiteralGlosser` 主要依赖首选表或第一可用释义，不具备上下文词义消歧。

另一处已验证的问题是全局 MT 纠错：

```text
源句：The client paid the worker.
候选译文：客户向工人付款。
当前 Apply 后：客户端向工作进程付款。
```

这条自检直接调用现有 `TranslationGlossary.Apply`，证明的是纠错逻辑会污染普通语境，不是声称模型在该句中一定生成该候选译文。

建议优先补领域审校表，并保留词性、领域、来源。不要自动把机器生成的解释加入基础词典。语境不明时展示多个词义，不编造置信度。锁序仍保持一 token 一单元，不因专业短语匹配把多个单词合并；多词术语可以作为旁注另行展示。

## 5. 同类产品与值得复用的部分

截至本次检索，以下比较基于官方仓库/文档及部分源码阅读，没有逐个安装做体验测试。

| 项目 | 已核对的能力/实现 | 对简译的价值与取舍 |
|---|---|---|
| [STranslate](https://github.com/STranslate/STranslate) | WPF；`ITranslatePlugin.cs` 区分翻译、词典、LLM 请求与结果，异步接口带取消；LICENSE 为 MIT | 技术栈最接近。优先参考请求/结果建模、服务适配与取消，不先复制整套插件宿主。见 [接口源码](https://github.com/STranslate/STranslate/blob/main/src/STranslate.Plugin/ITranslatePlugin.cs)、[LICENSE](https://github.com/STranslate/STranslate/blob/main/LICENSE) |
| [GoldenDict-NG](https://github.com/xiaoyifang/goldendict-ng) | 多词典格式、词典分组；源码许可证 GPLv3+ | 参考“分组激活词典”的用户模型，映射成领域包。不要为了导入术语先重做完整 MDict/StarDict 阅读器。见 [分组文档](https://xiaoyifang.github.io/goldendict-ng/manage_groups/)、[README](https://github.com/xiaoyifang/goldendict-ng/blob/master/README.md) |
| [Pot](https://github.com/pot-app/pot-desktop) | 多接口、插件、OCR、生词本；检索页面显示仓库已于 2026-09-07 归档 | 可参考服务管理设计；不要把它当作持续维护的核心依赖，也不建议追着补齐所有功能 |
| [NextAI Translator](https://github.com/nextai-translator/nextai-translator) | 翻译、润色、总结、流式；原 OpenAI Translator 已改名 | 参考按用途选择动作，不能让“解释”混进“翻译”。README 徽章/表述与实际 LICENSE 存在不一致，本轮读到 LICENSE 是 AGPLv3，复用前以选定版本实际文件为准。见 [README](https://github.com/nextai-translator/nextai-translator/blob/main/README.md)、[LICENSE](https://github.com/nextai-translator/nextai-translator/blob/main/LICENSE) |
| [Argos Translate](https://github.com/argosopentech/argos-translate) | CTranslate2 管线按段落与句子处理，句界检测独立 | 直接参考分段和拼回思路；当前模型质量仍需单独比较，不能认为换成 Argos 就必然更好。见 [源码](https://github.com/argosopentech/argos-translate/blob/master/argostranslate/translate.py) |

简译值得保留的组合是“选区附近、低打扰、离线、锁序对照”。这些是产品聚焦方向，不代表竞争产品没有类似能力。若未来想做通用翻译工具，先评估基于现有 WPF 项目改造是否更省成本，再决定持续自建。

## 6. 专业知识库的大概方案

### 6.1 将三种资产分开

| 资产 | 解决的问题 | 首期建议 |
|---|---|---|
| 术语库 | 某个单词/缩略语/短语在此领域叫什么 | 立即规划，先做一个领域 |
| 经审校的双语例句/翻译记忆 | 固定表达、领域用法、翻译参考 | 术语包之后；参考例句不能当成当前句译文 |
| 文档知识库 | 概念是什么、为什么这样理解、依据在哪里 | 后续显式触发解释时检索 |

不建议把所有资料切块向量化后，期待向量检索直接变成高质量英汉词典。短词和缩略语需要精确、大小写和领域控制；正文解释才更适合全文/语义检索。

### 6.2 首期领域包

先选一个最常用领域，例如软件/AI，或者电子电路；不要同时铺多个无人审校的领域。建议第一包约 200–500 个人工确认术语、50 条代表性句子；这是范围建议，不是现有覆盖数据。

存储在便携目录 `data/knowledge-packs/<pack-id>/`，与设置 DB、基础 ECDICT 分离：

```text
manifest.json       包 ID、版本、语言对、领域、来源、许可、hash、审校日期
terms.json          首期术语和别名，现有 JSON 机制可复用
references.sqlite   后续有文档检索需求再加；不要第一期先搭服务器
```

术语至少包含：源词、目标词、语言对、领域、词性/义项、别名、大小写规则、原文保留规则、来源 URL/文件页码、来源版本、审校状态。不要把大写缩略语与普通小写单词合并。

查询优先级：当前领域内的用户审校 > 当前领域包 > 通用审校 > ECDICT。多包冲突不得按目录顺序随便覆盖；首期仅一个活动领域，冲突显示备选。专业包是“追加/排序义项”，只有用户明确选择强制术语时才做强制替换。

沿用 `DictionaryService`、`TranslationGlossary`、`PrimaryMeaningTable` 的已有载入机制；增加领域范围与来源信息，不要先建另一套词典服务。JSON 足够时用 JSON，词条规模和查询需求扩大后再做独立 SQLite。

知识来源优先：项目官方双语文档、厂商手册、允许使用的公开术语表、用户有权使用的资料；机器抽取的术语进入待审区。比如 [TensorFlow docs-l10n](https://github.com/tensorflow/docs-l10n) 可作为官方组织下双语资料的候选，不应直接当作完整、权威、版本一致的术语数据库。每个资料文件的许可、版本及译文准确性都要单独确认。

### 6.3 区分领域和用途

“领域”决定词义优先级，例如电子、软件/AI。“用途”决定输出动作，例如快速翻译、锁序对照、概念解释。两者不要合成几十个模式按钮。

- Popper 保持当前主要开关；领域设置放托盘/设置页，常驻面板标明当前领域。
- 单词：先给领域义项，再列通用义项及来源。
- 锁序：仍逐 token、保持顺序、不调用 MT/LLM；可额外列出命中的专业术语，不替换整个 token 序列。
- 通序：术语用于约束译法；不把整段资料直接塞进 OPUS-MT。
- 解释：用户在常驻面板中主动触发，结果引用资料并标“解释”；不能伪装为原文翻译。
- 悬注维持只读、点击穿透，不为了加按钮破坏现有呈现规则。

### 6.4 文档检索与模型如何连接

现有 OPUS-MT 没有“接受背景资料并遵循解释指令”的接口。将检索片段拼到输入中，会让它把资料也当待译文本；RAG 不能直接成为现有本地 MT 的质量开关。

建议先做两条独立路径：

```text
词/术语查询 → 当前领域术语包 → 通用词典 → 释义及来源

主动解释 → 当前领域过滤 → 关键词检索 → 必要时语义检索
         → 少量相关资料与术语 → 用户配置的 LLM → 解释及引用
```

文档量较小时先用 SQLite FTS5/BM25，不先引入独立向量数据库服务；FTS5 官方支持全文检索与 bm25 排序。[SQLite 官方文档](https://www.sqlite.org/fts5.html)

若评测发现关键词检索漏掉同义表达，再加 embedding，与关键词候选融合；缩略语、型号仍保留精确路径。中英混合检索需实际验证分词与召回，不能把默认全文分词器当成中文语义检索。

入库保留文件、页码/章节、版本、许可；引用回到这些位置。检索到的资料只能作为证据，不能作为新的系统指令；没有相关证据时提示缺乏依据。默认本地检索；云模型调用必须由用户配置并明确哪些原文/资料会发送。默认不记录用户选择内容，知识包更新不修改设置 DB。未来如有缓存，加入领域、包版本、模型、提示词版本等全部输出参数。

可拆解的 .NET 实例是微软 [Semantic Kernel VectorStoreRAG](https://github.com/microsoft/semantic-kernel/tree/main/dotnet/samples/Demos/VectorStoreRAG)：[DataLoader.cs](https://github.com/microsoft/semantic-kernel/blob/main/dotnet/samples/Demos/VectorStoreRAG/DataLoader.cs) 将文本片段与文件/页码引用关联。可参考这种来源建模及入库分工，后续再选最少的组件；不要把示例的所有向量服务、OCR 和云依赖一起搬进桌面程序。

## 7. 模型升级与 UI 改进

### 模型

先用同一测试集对当前 OPUS-MT、候选官方服务和可选本地指令模型做对比，再决定换模型。记录漏译、符号/数字完整性、术语、否定、条件、长句结构、延迟、内存和分发体积。不能靠“参数更多”或几个漂亮例句决策。当前文档里的远程 Provider 链与接口大多还是设计，源码没有通用翻译编排器，不能将其报告成现成功能。

普通模式需要高质量时，可先实现一个用户配置的官方 Provider；若要原生 glossary，必须先验证对应语言对和产品接口确实支持。LLM/RAG 作为独立可选能力，不替换锁序路径。模型与数据的商用、再分发许可分别确认，不依据代码仓库 LICENSE 推断所有模型可分发。

### UI

- 常驻面板当前仍有 6000 字符截断，原文 4000；悬注 700/420。常驻应支持完整滚动与完整复制，显示截断不能等于丢失结果；当前源码没有复制按钮。
- 锁序常驻每个 token 都换行，阅读和比较较慢；可先改成紧凑的原文/释义对照块，保持原序与未知词标记，布局不需要重设计 Popper。
- 未知词状态放面板内，避免每次弹 toast 打断阅读；显示真实计数，不制造置信度。
- 清楚区分“未取到选区”“词典未收录”“模型未启用”“此句未译”“部分完成”；MT 错误不应都显示“本地词典”。
- 长文本先显示逐句进度和已完成结果；Esc/关闭后立刻取消。不要为了容器交互破坏 Tooltip 点击穿透和不抢焦点。

## 8. 最小落地批次与验收

| 批次 | 修改范围 | 验证目标 |
|---|---|---|
| A：输入/符号修复 | `TextClassifier`、规范化、`TextProtector`、`TranslationGlossary`、`LocalMtProvider`、`LocalMtGuard`、Host；对应测试 | NBSP 长句正确路由；上述模型回归句不泄漏占位符或语言标记；重复数字不因重复值而漏检 |
| B：长文/取消 | 翻译编排、`App.xaml.cs` 的调用/CTS、Provider/Host 生命周期、分句结果与 Overlay | 超长输入不静默截断；某句失败保留其他结果；新选区不等待无用旧任务；关闭不再后台推理 |
| C：一个领域包 | 现有三类词表载入、通用语言模型、设置存储、常驻来源显示、包构建/校验 | 至少 50 常用词 + 5 短语 + 2 句人工检查；大小写、别名、词义冲突、领域切换、重启恢复及基础词典回退通过 |
| D：按需解释 | 文档导入、检索、可选 LLM Provider、引用显示 | 每条引用能定位；无证据不捏造；结果与翻译分开；离线模式不出网 |

批次 A/B 不应等待领域包建好。实施时优先复用已存在的函数和管线，只把要改的业务从 code-behind 提取出来，不进行全仓库 MVVM/插件化重写。新增领域/用途设置、两词通序回退、部分结果等语义要同步 product.md/design.md 与 ADR；每个批次单独可构建、测试与人工验收。

建议准备 100 条固定回归输入：普通句、两词术语、多义词、PDF 空白、软连字符、URL/路径/代码、公式/数字/单位、缩略语、长段落、负面/条件表达。确定性规则使用单元测试；真实模型使用单独离线集成评估，不把某一条精确译文写死成唯一正确答案。质量和延迟分别记，P50/P95 必须多次测量。本轮少量耗时不是性能基准。

## 9. 尚未检查与剩余风险

没有用户原始失败句，不能唯一归因其全部“不翻译”问题；未做完整 Windows 应用/双屏 DPI/交互兼容矩阵，未逐个安装竞品，未完成候选模型质量排名、专业术语人工审校或数据再分发许可结论。

本轮仅增加审查报告与 Git 忽略的诊断材料；产品缺陷仍在，尚未修复。新增报告前的 Git 状态仅有既存 `.claude/` 未跟踪项，本轮没有处理它。

## 翻译功能候选

- 已取消按查询次数推荐固定释义的方案。保留用户主动编辑的固定译法，领域释义与参考资料纳入商业化路线图。
- 句段翻译增强。下一阶段比较用户配置的 LLM API 与自选的轻量本地模型。先用统一英中样本对照现有 OPUS-MT，记录术语、漏译、数字和代码完整性、等待时间和资源占用。本地运行时可参考 [llama.cpp](https://github.com/ggml-org/llama.cpp) 或 [Ollama 的兼容接口](https://docs.ollama.com/api/openai-compatibility)。模型、授权和分发范围待样本比较后确定。


## 翻译模型设置方案

在现有「本地翻译模型」设置窗口内统一管理句段翻译。入口改名「翻译模型」。默认选择本地翻译模型（OPUS-MT），复用现有安装和导入流程。选择 LLM 模式后再选择接入方式。

| 选项 | 设置项 | 请求发送到 |
| --- | --- | --- |
| 本地翻译模型（默认） | 已有模型目录与线程选项 | 本机 OPUS-MT |
| LLM · API 接入 | HTTPS 接口地址、模型名、API Key、测试连接 | 用户配置的接口 |
| LLM · 本机模型 | 本机服务地址、模型名、测试连接 | 仅回环地址上的本机模型服务 |

配置完毕后显式启用对应模式，下一次通序句段翻译使用该模式。选中 API 模式时提示句段文字会发往配置的接口，API Key 写入 Windows Credential Manager。测试连接发送固定样例。接口不可用时在结果中显示原因和翻译来源，用户可切回本地翻译模型。单词、短语、固定译法和锁序直译继续使用已有规则。

两种 LLM 接入先共用 OpenAI 兼容聊天请求和译文解析。现有 OPUS-MT 继续走本机翻译进程。首版调用用户已经启动的本机服务。选哪种轻量模型以及是否随下载站提供，等统一样本对比后再决定。 [Ollama 兼容接口](https://docs.ollama.com/api/openai-compatibility) · [llama.cpp](https://github.com/ggml-org/llama.cpp)

## 0.6.14 代码与发布体积优化（2026-11-25）

- 删除已无入口的 KnowledgeWindow 和 LocalMtSettingsWindow 两套旧窗口；源文件已备份至 backups/source-before-0.6.14-cleanup。正式入口统一为 MoreSettingsWindow，运行数据读取与旧数据迁移保留。
- 清理未使用字段与原型残留初始化；修复测试连接后未保存输入丢失，连接草稿仅在页面内存保留，换接口时清空输入中的 Key。
- 发布副本移除 WinForms 设计器、未使用的 WebView2 WinForms 控件和 XML 开发文档。OCR 副本移除未使用的 AVIF 解码/测试扩展；仅当哈希一致时移除平铺 DLL 的重复副本。原始 OCR 构建、共享环境、模型和许可证保留。
- tools/Optimize-Release.ps1 已接入 tools/publish.ps1，不使用裁剪 WPF 类型或模型量化来换取体积。
- 本轮安装包 149161556 → 145742750 字节，减少 3418806 字节（2.29%）；程序与 OCR 的解压文件合计减少 38683017 字节。压缩器原先已共享重复数据块，因此安装包减少量小于解压文件减少量。
- 222 项自动测试通过。裁剪后的自包含运行文件通过设置入口、图标键位、组合保存、回收恢复、历史回退、模型配置与下载位置检查。OCR 3 组样图、各 5 轮及反向顺序复查结果相同；复查首次调用精简/原版约 741/748ms，热调用中位数约 61/64ms。属于本机合成样本对照，不代表完整跨设备性能基准。
- 本机 D:/Jianyi 已更新至 0.6.14.0，startup: ready，词典和托盘均就绪。安装前备份完整旧版本；安装过程数据库文件哈希不变，启动后原设置及旧词条/知识库表内容摘要全部一致。OCR 模型和字符表哈希相同。
- 安装包：dist/Jianyi-Setup-0.6.14-compact.exe。SHA-256：2CBF7D78093403354C567512F83736933B7FF92E3C16B4565C43E99BD486C033。
