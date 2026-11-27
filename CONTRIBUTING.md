# 参与开发

建议先创建 Issue 描述具体场景，再提交范围清楚的改动。修复应说明触发条件、修改后的行为和检查结果；保持现有代码风格，不顺带重写无关模块。

## 开发环境

Windows 10/11 x64，使用 `global.json` 指定的 .NET SDK。先按 README 完成恢复、构建和测试；C++ MT 与 Python OCR 为独立的可选组件。

```powershell
dotnet restore Thanslator.slnx --locked-mode
dotnet build Thanslator.slnx -c Release --no-restore
dotnet test tests/Thanslator.Tests -c Release --no-build
```

完整测试包含需要可交互 Windows 桌面的 UIA 用例。无桌面环境可使用 README 中与 CI 一致的过滤条件，但应在 PR 中说明没有覆盖哪些桌面行为。

修改依赖后，重新运行 `dotnet restore --use-lock-file`，同时提交相应 `packages.lock.json`。不要提交 NuGet 缓存、模型权重、生成词典、安装包或 `data/`。

## 不可破坏的行为

- 自动触发仍要求真实选区；不恢复普通悬停识别或已删除的全局快捷键。
- 密码区域、安全桌面与不可访问窗口不采集；原文和截图不写普通日志。
- API Key 只存凭据管理器。样例、日志和截图中使用公开合成文本，不带真实密钥或第三方私人资料。
- 锁序直译保持原词序，不用整句 MT/LLM 润色。
- 导入、升级与失败回退保留用户数据和旧可用配置；不静默改写原模型。
- 第三方代码保留原版权、许可与可核对来源，不把附加条款误标为单纯 Apache/MIT。

更多约定见 `agents.md`、`product.md` 和 `design.md`。简译自有源码采用 MIT 许可证。
