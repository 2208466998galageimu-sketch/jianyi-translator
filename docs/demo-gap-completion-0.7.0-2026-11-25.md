# 简译 0.7.0 补齐记录

日期：2026-11-25。对应 docs/demo-gap-audit-0.6.16-2026-11-25.md；保留原报告作为旧版事实，以下是本轮新实现。

| 项目 | 当前状态 | 证据 |
| --- | --- | --- |
| 本地语言核心 | 中英双向及七语言 42 个方向真实运行通过；只登记已选择和试译成功方向 | build/model-research/m2m100-matrix.json |
| 下载配置和增量启用 | 双向启用、反向切换、启用日语复用核心、拒绝未选择韩语通过实际窗口检查 | build/gap-real-package-qa2/result.json（49 项） |
| 四组 OCR | 已核实上游 SHA-256；打包后的 worker 四组真实识别中英文通过 | build/model-research/ocr-profile-results.json |
| 默认 OCR 回归 | 三组图片各五次，与旧默认逐行/坐标/分数一致；本机热运行中位数约 55ms | build/gap-ocr-default-qa/comparison.json |
| 自备 OCR 导入 | 标准 ONNX ZIP 试识别后自动配置；错字符表拒绝；旧配置和原文件保留，失败暂存清除 | build/gap-native-ocr-qa2/native-import-passed.txt |
| 助手返回 | 独立配置、返回原 OCR 导入区及四套真实推荐按钮通过 | build/gap-extended-qa/result.json |
| 译文调整 | 候选默认收起、同译法来源合并、仅本次、撤销、过期结果丢弃、悬注无按钮通过真实 WPF 检查 | build/gap-native-ocr-qa2/overlay-passed.txt |
| 实际 LLM | 官方 Qwen2.5-1.5B Q4_K_M，本机 llama.cpp；实际“sensor”从传感器改为感应器，另一句逐字符不变 | build/model-research/llm-evidence-checked-results.json |
| 偏好及统计 | 默认不收集；方向与场景隔离；HMAC 去重、跨日/不同原文阈值、候选确认；请求频率去重有单元检查 | tests/Thanslator.Tests/TranslationPreferenceTests.cs、KnowledgeAdjustmentTests.cs |

## 明确的支持边界和未通过的范围

- 本轮没有制作任意 Transformers/Safetensors 到 CT2/GGUF 的自动权重转换器；导入助手只整理支持的布局/封装。不同架构、分片 GGUF、没有往返方向或哈希不符的包应返回失败，不能伪装成功。
- 同一个 M2M100 核心不能物理拆成单语言增量权重；第一次中英也下载约 473 MiB，随后其他语言复用核心。界面的勾选对应启用和方向检查，不代表切割权重。
- 42 方向检查证明可运行，不是翻译质量评测。测试已观察 application 多义词误译。专业内容仍需核对；LLM/知识库也不保证必然正确。
- 1.5B 小模型可以完成结构化分句和明确术语调整，但示例提取曾给出无依据的泛化规则及改写的引用；现在原样依据不匹配会过滤。最近真实检查返回空候选，不能据此宣称风格推断质量达标。人工确认不可省略。
- 结构化 LLM 请求要求接口支持标准 JSON 输出；未对所有自备 API、四个 LLM 规模逐个真实下载运行。
- 真实跨应用取词、各种显示缩放/多屏、真实科研截图、长期运行和用户实际语言质量仍需要现场验收；合成截图与窗口工作流不能代替这些。
- 新旧许可说明一并保留；原 ECDICT 数据来源的分发许可问题未由本轮解决，未发布 GitHub 或宣称已可商业分发。

安装及最终发布校验结果另记本目录的发布报告。
