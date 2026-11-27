# 简译 · Jianyi

Windows 划词翻译工具。选中文字并停留，就在原文附近显示释义或译文，减少复制、切换窗口和粘贴的步骤。

当前版本：**0.8.2 技术预览**。支持 Windows 10/11 x64，主要使用 .NET 10、WPF 和 CTranslate2。

## 能做什么

- 英中词典释义、词形还原与锁序逐词直译；锁序路径不调用整句 MT 或 LLM。
- 本地中、英、日、韩、法、德、西句段翻译。按需启用语言，默认选中中英双向；第一次需下载约 473 MiB 共享核心，新增支持语言复用核心。多语言句段翻译不等于已有多语言词典。
- 接入本机 LLM 或兼容聊天接口的远程 LLM；自动语言识别、分句和知识库相关任务需要 JSON 输出支持。
- 主动框选 OCR，或在可信选区读取失败时使用可关闭的自动 OCR 回退。四组本地 OCR 推荐可下载、试识别并配置。
- 仅在 LLM 模式下使用领域术语库、知识库组合、版本恢复与已删除内容。
- 在常驻译文浮窗点击“调整翻译”后选择真实知识库候选，默认仅本次使用，可保存为此组合首选，并撤销本次显示调整。
- 独立的个人翻译偏好。手动规则或从示例提取的候选需确认后生效；本地反馈收集默认关闭。
- 模型编辑和检测、词条批量管理、可编辑导入预览、备份恢复、下载与存储统一放在“更多设置”。

## 数据和使用边界

运行数据默认在程序旁的 `data/`，下载位置可以修改。API Key 通过 Windows 凭据管理器保存，不写入仓库或普通配置文件。原文与截图不作为默认日志保存；远程翻译会把本次内容发送给用户配置的服务。

截图 OCR 在本地运行；远程导入助手发送资料前提供内容与目标预览。模型导入先直接检查，再允许助手整理已支持的文件布局。**不提供任意 Transformers/Safetensors 权重自动转换，也不把改文件名当作转换。**

七语言 42 个方向已做本机运行检查，但不是翻译质量认证。OCR、专业术语和 LLM 输出仍需核对；跨应用、不同缩放和多屏兼容性仍在完善。

## 从源码构建

准备 Windows x64 和 `global.json` 指定的 .NET 10 SDK。以下命令只构建托管程序，不下载词典、模型或 OCR 组件：

```powershell
dotnet restore Thanslator.slnx --locked-mode
dotnet build Thanslator.slnx -c Release --no-restore
dotnet test tests/Thanslator.Tests -c Release --no-build --filter "FullyQualifiedName!~Thanslator.Tests.UiaSelectionReaderTests"
dotnet run --project src/Thanslator.App
```

有可交互 Windows 桌面时，可以去掉测试过滤条件运行完整测试。CI 不把桌面 UIA 测试或真实模型验收冒充纯单元测试。

源码仓库不包含生成的词典数据库、模型权重、安装包、用户数据和本机备份。缺少词典数据时无法获得完整离线查词功能；句段模型需要另行配置。词典构建入口为 `tools/DictionaryBuilder/build_ecdict.py`，运行前先阅读[词典来源与许可说明](assets/dictionary/README.md)。

可选原生组件：

- [本地 MT 宿主](tools/LocalMtHost/README.md)：需要 MSVC、CMake、CPU 版 CTranslate2 和 SentencePiece。
- [OCR 组件](tools/OcrHost/README.md)：需要独立 Python 构建环境、已授权的模型与依赖；不属于默认托管构建。
- [模型导入规范](docs/model-import-format-0.7.md)。

## 开发与发布

- [产品需求](product.md) · [技术设计](design.md)
- [参与开发](CONTRIBUTING.md) · [安全问题](SECURITY.md)
- [GitHub 上传和仓库配置](docs/github-publishing.md)
- [第三方许可边界](docs/third-party-licensing.md)
- [0.8.0 发布记录](docs/release-0.8.0.md)

公开安装包包含 Windows 程序及本地句段翻译宿主。完整版离线词典和 OCR 需要另外准备词典数据或 OCR 组件。安装包通过 GitHub Releases 发布。

## 许可证

简译自有源码采用 [MIT 许可证](LICENSE)。第三方代码、模型和数据继续遵守各自许可，详见[许可边界](docs/third-party-licensing.md)。
