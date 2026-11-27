# 第一批交付：简译 0.6.5

日期：2026-11-10。依据用户已批准的[翻译审查](translation-review-2026-11-10.md)与[取词方案](capture-plan-2026-11-10.md)实施。保留 WPF 现有视觉，不新增框选/OCR/知识库设置，不调整 Popper 尺寸、颜色、字体或分段控件。

## 已实现

| 范围 | 改变 | 验证入口 |
|---|---|---|
| 输入与分类 | Unicode 空白、成对外括号/引号；`.NET`/`C++`/`C#` 保留；通序两词未收录可转句子，锁序仍 ≥3 词 | `TextClassifierTests`、实际词典诊断 |
| 长文 | 按句/换行/384 UTF-8 字节保守预算切分，代码整体保留，不拆代理对；逐片段进度与原顺序拼回 | `TranslationDocumentTests`、12 句真实模型样本 |
| 局部失败 | 失败单元显示「〔未译〕原文」，邻近单元继续；拒绝空译文、异常重复、内部标记及关键内容缺失 | `LocalMtProviderTests`、`LocalMtGuardTests` |
| 符号保护 | URL、代码、数学比较式等保护；ASCII 线标记严格校验一次出现；数字允许模型理解，但校验数值与重复次数；损坏最多重试一次原句 | 协议 fixture 与真实 OPUS-MT |
| 术语 | 歧义替换增加 contextTerms，日常 client/worker 不强制技术释义；同源用户项优先 | `TranslationGlossaryTests` |
| 取消 | 关闭/Esc/新选区/前台切换取消并丢弃旧结果；每实例独立管道，取消和超时只终止自己启动的 Host | 协议取消测试、真实模型取消后恢复 |
| 取词 | 独立 MTA UIA、独立 STA 剪贴板、有界队列与等待；就近优先，后备最多 96 节点/700ms；真实行矩形命中 | `CaptureTests`；跨应用手工矩阵仍待补 |
| 隐私与复制 | 先检查前台/安全桌面/提权/内置 denylist/密码；失败状态分离，禁止/明确无选区/超时不复制；安全格式预备份、序号与所有权校验 | 策略与失败分支测试；真实多格式恢复仍待人工验收 |
| 浮层 | 沿用现有资源和布局；状态栏显示进度/未译数；常驻保留完整长文，悬注仍紧凑预览 | 实际 OverlayWindow 渲染与完整文本/不抢焦点检查 |

主要代码：`src/Thanslator.Capture/SelectionCaptureService.cs`、`UiaSelectionReader.cs`、`ClipboardSelectionReader.cs`、`CaptureInvalidationMonitor.cs`；`src/Thanslator.Translation/LocalMtProvider.cs`、`TranslationDocument.cs`、`TextProtector.cs`；`tools/LocalMtHost/main.cpp`；`src/Thanslator.App/App.xaml.cs`、`OverlayWindow.xaml.cs`。未引入第三方依赖。

## 验证记录

Release 解决方案构建成功，0 警告、0 错误；Release 自动测试 **113 通过、0 失败、0 跳过**。最后补充格式枚举失败时拒绝复制，受影响的 9 项 Capture 测试再次通过，并重新发布。新原生 Host 编译成功，已有第三方 CTranslate2 头文件出现一处 C4244 转换警告，未引入新的原生依赖。`git diff --check` 无空白错误。测试均使用合成输入，没有用户原文/密钥 fixture，不请求远程翻译。真实模型只读复用本机已有 `D:\Jianyi\data\models\opus-mt-en-zh-int8`，没有自动下载或修改模型，也没有改动 `D:\RAG\ocr`。

实际新编译的 CPU Host + 本机 OPUS-MT 样本：

| 输入场景 | 结果 |
|---|---|
| GitHub / Docker 专名 | 均完整保留，普通句成功 |
| URL 夹在句中 | 链接完整，前后文本翻译 |
| `If x ≤ 5, the system returns a result within 3.5 ms.` | 一整句翻译成功，保留 `x ≤ 5` 与 `3.5`；旧 Unicode 占位符在该模型会丢括号，直接输入 `≤` 会变成方框，因此选择实测通过的 ASCII 线标记 |
| ☃ 与反引号代码 | 受保护内容原样保留，周围文字翻译 |
| NBSP 空白 | 正常翻译 |
| `The client paid the worker.` | 日常客户/工人释义，不套技术替换 |
| 含不可用替换字符的失败片段 + 下一句 | 第一片段保留未译原文，下一句正常完成 |
| 12 句换行长文 | 12 句全部返回，11 个换行保留；本机单次约 4.5 秒，不是性能承诺 |
| 翻译中取消后再翻译 | 约 227ms 响应取消，下一请求成功恢复 |

模型样本的成功只说明这些输入通过，不代表翻译质量全面提升或任意符号均可恢复。ASCII 标记也可能被其他模型破坏，因此保留校验与原文回退。

浮层截图：`artifacts/wpf-first-batch-qa/overlay-first-batch.png`。使用真实 `App.xaml` 资源和 `OverlayWindow`，验证超过 6000 字的常驻结果不被显示逻辑截断，显示浮层前后前台窗口未改变。桌面控制原生通道不可用；合成跨窗口 UIA 测试因前台无法切到测试窗口返回 `Canceled`，没有把它计为成功，也没有为测试削弱前台检查。

## 已知边界与后续

交付目录 `dist/FirstBatch-0.6.5` 与安装包 `dist/Jianyi-Setup-0.6.5-first-batch.exe`；模型不随包下载。使用现有 NSIS 脚本，仅构建、不自动安装或停止当前应用。仍为未签名本地试用构建；NSIS 模板有原有英文版本信息缺少 LegalCopyright 的 9100 警告。组件哈希见 `artifacts/first-batch-qa/release-manifest.json`，发布 Host 与真实模型测试的新编译 Host 的 SHA-256 一致。标准 `tools/publish.ps1` 使用的本机 `build/LocalMtHostRuy` 已同步新 Host，旧生成的 Host 备份在 QA artifacts；旧 CMakeCache 指向历史拼写目录的问题未修改，重新原生配置使用了独立 `build/LocalMtHostFirstBatch`。

最终安装包已构建成功，SHA-256：`2142363F808794EB1ABB2126AA0AD775F973B4EEDEF078B46FC03E8469B6A76D`。未执行安装/升级验收；现有安装与设置未被覆盖。

- Chrome/Edge/Firefox、VS Code、Office/Acrobat、混合 DPI、锁屏、真实剪贴板图片/文件/富文本与终端仍需手工验收。本次不声称取词兼容矩阵通过。
- UIA 的单个跨进程 COM 调用无法强制中断；工作者超时仅停止等待，持续挂起时可能保持忙碌，需重启应用。出现实际高频挂起后再上独立 helper 进程。
- 不暴露选区或密码属性的自绘控件无法保证识别；受保护界面不会绕过权限。显式热键复制在不支持 UIA 的编辑器中仍可能受“无选区复制整行”的应用行为影响，无法凭复制内容证明选区。
- 剪贴板备份/恢复仍尽力而为；未知格式直接拒绝回退，可能比旧实现更保守。历史/剪贴板管理器无法撤回，异步复制晚于取消或同一进程并发复制也不能完全归因。
- 未实现 OCR、IA2、领域知识包、翻译历史、模型升级或云端翻译。本批先修可靠性；后续 OCR 按免框选入口方案单独接入，不直接把 MinerU 文档流水线塞进悬停热路径。

## 参考与复用判断

优先审查 [selection-hook](https://github.com/0xfullex/selection-hook) 的分层取词思路，保留现有 .NET/WPF，不新增 Node/Electron 依赖。实现依据：[微软 UIA 线程建议](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)、[剪贴板操作](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-operations)、[Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input)。本机 CTranslate2 已安装头文件用于确认输入/输出长度参数，实际编译与模型样本共同验证，未凭网页默认值推断运行结果。
