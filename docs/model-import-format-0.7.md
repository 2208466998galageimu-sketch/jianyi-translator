# 0.7 模型导入规范

从“更多设置 → 模型配置”选择对应类型，再点击“从资源管理器导入”。原文件不修改；成功前保留原配置。助手可整理支持的布局，不能改写权重、生成分词器、扩大模型原有语言能力或执行模型代码。

## 本地翻译语言包

使用 ZIP，根目录或唯一的一层子目录包含 `manifest.json`。清单的顶层字段为：

| 字段 | 要求 |
| --- | --- |
| `formatVersion` | `3` |
| `engine` | `ctranslate2` |
| `architecture` | `marian` 或 `m2m100` |
| `name`、`version` | 非空名称和版本 |
| `kind` | `bidirectional` 或 `multidirectional` |
| `source`、`license` | 来源与许可，不可留空 |
| `directions` | `sourceLanguage`、`targetLanguage`、包内 `path`；需要源语言前缀的 Marian 模型可声明 `sourcePrefix` |
| `files` | 每个文件的 `path`、实际字节 `size`、64 位十六进制 `sha256` |

每个语言对都需要往返方向。可以由两个独立单向模型组成双向包，或由同一多语言模型支持多个方向；不能给单向模型加反向声明来冒充双向。

`marian` 每个模型目录需要 `model.bin`、`config.json`、`shared_vocabulary.json`、`source.spm`、`target.spm`。`m2m100` 需要 `model.bin`、`config.json`、`shared_vocabulary.json`、`sentencepiece.bpe.model`。

路径只能指向包内文件，禁止绝对路径、上级路径、重复文件和符号链接。包总大小不超过 4 GiB；实际解压流、大小及哈希都检查。默认推荐包还包含模型卡与许可。仍兼容旧的英译中五文件 Marian 包。

当前可试译验证的语言代码为 `en`、`zh-Hans`、`ja`、`ko`、`fr`、`de`、`es`。其他方向不能仅凭清单登记为就绪。关于模型转换请参考 [CTranslate2 官方文档](https://opennmt.net/CTranslate2/guides/transformers.html)；本版本应用不代办 Transformers/Safetensors 权重转换。

## 本机 LLM

支持单个 `.gguf`，或恰好包含一个 GGUF 的 ZIP。检查 GGUF v2/v3 文件头后，交给本机服务实际加载和测试；仅扩展名正确不代表可运行。不支持分片 GGUF、嵌入/重排模型冒充聊天模型、任意模型架构的自动转换。

本机运行组件和模型服务为独立配置。导入助手的配置不替换当前翻译模型，也不自动获得翻译服务的 API Key。

## OCR

支持已兼容的完整 OCR 组件 ZIP，或 Paddle DB 检测 + CTC 识别 ONNX 的模型包。标准包可使用以下文件：

```text
det.onnx
rec.onnx
characters.txt
```

这组标准文件名可以直接检查。其他布局可显式提供 `ocr.json`：

```json
{
  "formatVersion": 1,
  "engine": "paddle-ctc",
  "detector": "det.onnx",
  "recognizer": "rec.onnx",
  "characters": "characters.txt"
}
```

字符表按 UTF-8 每行一个字符，必须与识别模型输出类别对应。每个文件不超过 1 GiB，OCR 包总解压不超过 4 GiB。只有布局和模型兼容、实际测试图识别成功后才切换；不能用另一份字符表猜测修补不兼容模型。

自备 OCR 权重复用程序内的可信运行组件，需要先安装含该组件的版本。四组推荐模型下载后自动校验、测试并配置；源码仓库本身不携带这些权重或运行组件二进制。

## 失败与重试

直接导入失败后保留当前窗口的文件上下文。“重新检查”再次直接验证；布局适合修正时提供助手入口，配置后可以返回原导入页面再试。无助手配置时提示前往配置。修正后仍不兼容就显示原因，不覆盖旧模型。

关闭窗口后需重新选择文件；未把用户原文件路径或权重写入公开配置或上传到云端。远程助手只接收本次确认的资料；模型布局任务发送文件名和允许的目标名，不发送模型权重。
