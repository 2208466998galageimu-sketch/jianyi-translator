# 自有源码、第三方代码和数据的许可边界

核查日期：2026-11-26。这里只记录当前可核对的来源，不声称已完成全部分发授权审查。

| 内容 | 当前依据 | 发布边界 |
| --- | --- | --- |
| 简译自有 C#、C++、界面和脚本 | MIT，见仓库根目录的 `LICENSE` | 同步分发许可证和版权声明 |
| `tools/OcrHost/vendor/` | 源自 MinerU 4.0.5 本机快照；保留版权头、`tools/OcrHost/LICENSE-MinerU.md` 和 Apache 文本 | 该许可附有额外条件，不能改标为纯 Apache-2.0；上游精确提交和改动来源仍应补充记录 |
| CTranslate2 / SentencePiece | MIT / Apache-2.0，见 `assets/licenses/` | 保留完整许可及所用构建中的第三方声明；源码仓库不携带 DLL/SDK |
| RapidOCR 推荐模型 | 官方模型目录与发布方 SHA-256，Apache-2.0 来源说明 | 权重按用户选择另行下载；不放进 Git 历史 |
| M2M100 推荐翻译核心 | 模型卡 MIT，转换分发固定版本，见组件说明 | 不把它与自有源码当成同一份作品；保留来源与许可 |
| ECDICT 生成词典 | 仓库 MIT，但其描述的上游数据来源未由本项目完成授权核查 | CSV、生成数据库不进入源码快照；现有含此数据的安装包暂不公开发布 |
| 用户自备模型、知识库和凭据 | 用户提供 | 不属于源码仓库或公开样例，禁止放入提交、Issue 或公开报告 |

0.8.2 公开安装包包含应用和本地句段翻译宿主。ECDICT 生成词典及既有的预构建 OCR 组件仍需独立核清分发条件。

需要保留的区别：公开可见不等于已经获得开源授权；自有源码的许可证不自动覆盖第三方作品；上游仓库标注 MIT/Apache 也不能代替对其数据来源和附加条件的核对。

来源：

- [MIT 官方文本](https://opensource.org/license/mit)及 [Apache-2.0 官方文本](https://www.apache.org/licenses/LICENSE-2.0)
- [MinerU 当前许可](https://github.com/opendatalab/MinerU/blob/master/LICENSE.md)，本地实际使用的版本仍以随源码保留的许可为准
- [ECDICT 上游说明](https://github.com/skywind3000/ECDICT)及本仓库 `assets/dictionary/THIRD_PARTY_NOTICES.md`
- `assets/licenses/THIRD_PARTY_NOTICES.md`
