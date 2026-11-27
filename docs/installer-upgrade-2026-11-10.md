# 0.6.8：安装前自动卸载旧版

## 行为

- 沿用 NSIS 和现有安装向导风格。检测当前用户登记的简译安装，欢迎页显示旧版和目录；只有点击安装才关闭程序/子进程并卸载。取消向导不会提前卸载。
- 升级锁定原目录，保留 `data` 内设置、日志和本地模型；基础词典由新包重新安装。不新增数据迁移或删除用户数据的选项。
- 复制旧卸载器到临时目录，以 `/S _?=<原目录>` 调用并等待完成。卸载器缺失、无法启动、非零退出或程序文件残留时停止，避免混装；不猜测、扫描或删除其他程序目录。
- 已安装 OCR 的升级会选中并锁定 OCR 安装项。不含 OCR 的安装包遇到已有 OCR 时会在卸载前停止，提示使用带组件的包。新安装 OCR 仍默认不选，用户开关值不变。
- 升级时关闭简译及其自建子进程，释放本地 MT/OCR 文件。没有执行当前真实安装的卸载或升级。

## 验证

`powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-installer.ps1`

脚本将真实安装器流程重命名到随机测试产品/注册表键/程序名，并去掉与测试无关的桌面和开始菜单快捷方式；测试目标强制限定在本次 `artifacts/installer-qa/<随机标识>` 内，不能结束真实简译。旧版 fixture 使用实际 NSIS 卸载器，有延迟完成、报错和假成功三种情况。测试数据为合成文件，不含真实设置或文本。

旧逻辑测试先观察到 `Old version was not uninstalled` 失败；新逻辑最终 **7 个场景全部通过**：首次安装、旧版卸载升级（含等待/残留清理/原路径/data/OCR）、同版重装、卸载失败、假成功、卸载器缺失、无 OCR 包保护。原始结果：`artifacts/installer-qa/f076a82ca2df4fdcbd45155efdbef8eb/results.json`。

解决方案 Release 构建 0 警告、0 错误；全量测试 125 通过、0 失败、0 跳过。NSIS 编译沿用英文 LegalCopyright 元数据的 9100 警告，不影响安装器行为。没有新增依赖或插件。

限制：不是 MSI/其他用户/未登记便携版的卸载管理，不自动迁移安装目录；没有验证真实用户安装和交互式向导全流程。个人试用安装包未签名。

## 交付

- `dist/Jianyi-Setup-0.6.8-ocr.exe`，文件版本 0.6.8.0，148,769,122 字节；安装包已设 Medium 完整性级别。包含原可选 OCR 组件，首次安装不自动勾选。
- SHA-256：`0D5D92A4E77DA554869AA3187A368C612B4FADBF9EC52C390ED2A0F396F17120`。
- 自包含目录 `dist/Upgrade-0.6.8`，含本地 MT Host、不含用户 `data`。未覆盖旧安装包，未执行真实用户的安装/卸载。

## 参考

- [NSIS 官方源码的已安装处理](https://github.com/NSIS-Dev/nsis/blob/master/Examples/makensis.nsi)。本次复用已有 NSIS，不引入安装器框架或新插件。
- [NSIS 官方命令行说明](https://nsis.sourceforge.io/Docs/Chapter3.html#installerusage)：`_?=` 必须最后且不加引号，配合 ExecWait 等待卸载器；仅 ExecWait 不能解决卸载器自复制后提前返回的问题。
