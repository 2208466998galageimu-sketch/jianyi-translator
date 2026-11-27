# 上传 GitHub 与后续配置

目标是可构建、可审查的源码仓库，以及许可边界清楚的 Windows 技术预览安装包。

## 建议的发布顺序

1. 新建名为 `jianyi-translator` 的 **Private** 仓库，说明填写“Windows 划词翻译：本地模型、OCR 与 LLM 领域术语库”。不要自动生成另一套 README、LICENSE 或 gitignore。
2. 使用本轮准备的源码快照作为首次提交；它不含 `.git` 历史、用户数据、模型、生成词典、安装包或内部商业草案。原工程和原提交历史仍保留。
3. 简译自有代码采用根目录 `LICENSE` 中的 MIT 许可。第三方源码继续遵守原条款。
4. 在 GitHub 运行一次 CI，确认 `Windows managed` 和 `Secret scan` 两个检查通过。首次远程运行前，不应把本机检查写成“GitHub Actions 已通过”。
5. 人工核对待上传文件和历史敏感资料问题，再决定改成 Public。自动密钥扫描不能识别所有私人文档、截图或业务信息。
6. 公开 Release 使用专门构建的安装包，附 SHA-256 和第三方许可声明。现有含词典和预构建 OCR 组件的本机安装包继续留作本地使用。

历史中若曾包含真实凭据或非公开资料，应使用已审查的当前源码建立新的远程历史，而不是单纯把旧仓库切为公开。旧仓库保留在本地或私下备份。

## 连接到新建仓库

在新的源码快照目录执行，仓库地址从你自己的 GitHub 仓库页面复制，不要把下面步骤直接用于另一份正在使用的工程。

```powershell
git init -b main
git status --short
# 先确认列表没有 data、模型、安装包或私人文件
git add .
git commit -m "Initial source release: Jianyi 0.8.2"
```

随后将真实仓库地址添加为 `origin` 并推送 `main`。可以使用 Git Credential Manager 的浏览器登录流程；不要把 Token 写进远程地址、脚本或聊天。

建议先在 GitHub 的 Settings → Emails 开启邮箱隐私，并将该页面显示的 `noreply` 地址设置为**这个仓库**的提交邮箱，不修改其他项目的全局设置。参见 [GitHub 提交邮箱说明](https://docs.github.com/en/account-and-profile/how-tos/email-preferences/setting-your-commit-email-address)。

## 上传后建议配置

| 位置 | 建议 | 原因 |
| --- | --- | --- |
| General → Default branch | `main` | 与准备的工作流一致 |
| General → Pull Requests | 仅启用 Squash merge；自动删除已合并分支 | 保持一个人维护时的历史简单 |
| Rules → Rulesets | 对默认分支禁止删除、禁止强推；要求 PR 和两个状态检查 | 阻止误覆盖及带失败检查的合并 |
| PR 审批数量 | 单人阶段设为 0；有第二位维护者后再改为 1 | 你不能审批自己的 PR，设 1 会阻塞自己的日常合并 |
| Actions → General | 默认 `Read repository contents`，关闭 Actions 创建和批准 PR 的权限 | 当前 CI 不需要写仓库或操作发布 |
| Security / Advanced Security | 开启 Dependency graph、Dependabot alerts、secret scanning、push protection | 检查依赖和密钥；可用性以账号和仓库类型为准 |
| Security → Advisories | 开启 Private vulnerability reporting | 让安全问题有私密渠道 |
| Issues | 开启，保留问题和功能模板 | 集中反馈 |
| Wiki / Discussions / Projects | 初期关闭 | 先把反馈集中在一个地方 |
| Releases | 第一版标记为预发布；后续用 `vX.Y.Z` 标签 | 区分技术预览和稳定版 |
| About | 填说明与 Topics：`translation`、`windows`、`wpf`、`ocr`、`llm`、`offline` | 帮用户了解范围；不写未经验证的“高精度”或“商业就绪” |

必须先让 CI 运行一次，再在 Ruleset 中选择实际出现的检查名：`Windows managed`、`Secret scan`。不要照抄不存在的检查名，否则可能一直无法合并。暂时不要求 CODEOWNERS 审批、签名提交或部署环境审批。

工作流只监听普通 `pull_request`，不使用 `pull_request_target` 执行投稿者代码；官方 Actions 固定到完整提交 SHA，`persist-credentials` 关闭。Dependabot 每周检查 NuGet 与 Actions，NuGet 兼容更新分组，普通版本更新使用 7 天冷却，不自动合并。

## CI 实际覆盖

托管构建使用 `global.json` 和 NuGet 锁文件。CI 的托管测试过滤了需要交互桌面的两条 UIA 用例；桌面全套、OCR 实际识别、模型推理、安装升级和多屏验收仍分别进行。CI 不下载模型，不打包词典，不生成公开安装包。

当前不加入自动发布安装包的工作流：原生组件的干净环境构建、数据授权与签名流程尚未成为已验证的公开流水线。解决这些条件后再加发布，不用一个看起来完整但实际不能重现的发布按钮。

## 官方依据

- [仓库安全和依赖设置](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-security-and-analysis-settings-for-your-repository)
- [分支 Ruleset](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)
- [Actions 安全用法](https://docs.github.com/en/actions/reference/security/secure-use)
- [Dependabot 选项](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference)
- [私密漏洞报告](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)
- [大文件和 Release 分发](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github)
