# 0.6.7：悬停停止与热键无反馈修订

## 证据与范围

- 用户反馈：VS Code 对话区域选中后悬停没有译文，开启 OCR 后 Ctrl+Shift+D 仍无可见反馈。
- 当前安装 `D:\Jianyi` 为 0.6.6，OCR exe 和模型齐全，启动日志记录热键注册成功。首次读取设置 OCR 为 false；用户再次操作后已确认为 true，不能再把故障归因于未启用。
- 运行日志最后在 20:06:58.977 记录 `input poll failed err=5`，之后无采样心跳。`MouseHook.PollThread` 在一次 GetCursorPos 失败后直接 return，且 App 仍持有旧实例，无法自行恢复。这解释了之后悬停失效。未确定 err=5 的具体触发来源，不把安全桌面切换等可能原因当作已证实事实。
- 旧版没有热键接收/捕获状态日志，因此“注册成功”不能证明用户那次按键已收到，也不能确定无反馈是失败提示藏在控制条、取消还是热键投递问题。VS Code 的默认 Ctrl+Shift+D 为打开调试视图，但注册成功的全局热键通常会拦截；没有证据就不直接断言快捷键冲突。
- 另外通过 24 层合成 UIA 控件树复现：旧 16 层窗口归属检查误拒绝普通控件，旧 16 层密码祖先检查也漏掉更上层保护标记。该边界真实存在，但尚未证明它就是用户这张截图的直接原因。

## 修改与验证目标

1. 暂时不可访问时每 250ms 等待恢复，第一次失败发失效事件并取消旧请求；恢复时重置基线，不合成松键/点击。关闭或退出可立即停止等待。
2. 祖先检查及候选祖先链统一到最多 128 层，保留工作队列/超时与失败关闭策略；不让 OCR 绕过密码、权限检查、超时和忙碌。
3. 热键禁自动重复。显式失败、忙碌、总开关关闭、OCR 未开启等使用原有 toast 和状态栏；OCR 启动有提示。剪贴板恢复失败的警告保留，注册失败不再被通用就绪提示覆盖。
4. 诊断模式增加热键接收、捕获状态、选区证据、OCR 状态及取消 generation。无原文/截图写入日志，没有添加新依赖、远程请求或改变 OCR 默认关闭。

回归先在旧逻辑观察失败，再修复：采样失败后的恢复测试原先失败；深层窗口归属和密码祖先两个用例原先均失败。

## 本地验证

- 最终 `dotnet build Thanslator.slnx -c Release --no-restore`：0 警告、0 错误；`dotnet test Thanslator.slnx -c Release --no-build`：125 通过、0 失败、0 跳过。
- 新增四项回归：暂停后恢复、不合成跨暂停松键；暂停中关闭后不再采样；24 层普通控件仍属于原窗口；24 层外的密码祖先仍被检查。
- 原 WPF 渲染自检通过：长文完整显示、浮层不激活、OCR 原文与校对事件保持；不是实际 OS 按键/鼠标验收。
- 自包含发布目录 `dist/CaptureFix-0.6.7`，版本 0.6.7.0，包含原有 LocalMtHost；发布目录没有 `data` 用户数据。
- 未新增依赖；现有 OCR 组件与模型原样复用。`git diff --check` 未报告空白错误，Git 提示部分既有文件后续将转换 CRLF。

## 现场限制

Computer Use 本轮返回 `native pipe is unavailable ... os error 2`，无法完成真实 VS Code 对话区的鼠标/热键端到端验收。合成控件测试不能替代该场景验收。用户截图为中文，当前英→中本地模型不支持中文输入，但正确行为是明确显示提示，不应静默无反馈。

仅生成修订包，不自动停止正在运行的简译，也不改写 D 盘安装和设置数据。

## 交付

- 安装包 `dist/Jianyi-Setup-0.6.7-ocr.exe`，版本 0.6.7.0，148,771,788 字节；复用原可选 OCR 组件。退出旧版后覆盖安装到原目录，不需要卸载；包内不含用户 `data`。
- SHA-256：`A010F1337883B89AA32BD30D3104F7BD167CF107042F00BD5CE2E84CDFB2C98E`。
- NSIS 编译成功，沿用模板的英文 LegalCopyright 元数据 9100 警告；安装包完整性级别已设 Medium，个人试用包未签名。
- 更新后在 VS Code 对话区用一行英文复现，区分热键接收、捕获失败、取消、OCR 和翻译阶段。诊断记录位于原 `data/logs/trigger.log`（已有诊断模式开启时）。没有现场复测就不把整个热键问题标为解决。

## 来源

- [Microsoft GetCursorPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcursorpos)：访问与输入桌面条件；本次实现不调用 SetThreadDesktop。
- [Microsoft RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)：MOD_NOREPEAT。
- [Microsoft UIA TreeWalker](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/navigate-among-ui-automation-elements-with-treewalker)：沿父节点确认所属窗口。
- [VS Code 默认快捷键](https://code.visualstudio.com/docs/reference/default-keybindings)、[Webview 说明](https://github.com/microsoft/vscode-docs/blob/main/api/extension-guides/webview.md)。
- GitHub 参考 [Pot](https://github.com/pot-app/pot-desktop)：区分选区翻译与 OCR 入口；本次复用现有简译实现，没有引入另一套引擎/桌面框架。
