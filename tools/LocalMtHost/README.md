# 简译原生本地翻译宿主

独立 Windows CPU 进程，通过命名管道服务 WPF 程序。0.7 支持旧 Marian 包和 M2M100 多语言包；主程序已接入生命周期、语言参数、取消和结果显示。模型权重不随源码提交。

已验证的运行组合为 CTranslate2 4.8.5（CPU / Ruy）及 SentencePiece 0.2.0。源代码本身不包含其 SDK、DLL 或模型。托管解决方案可以不构建这个可选组件。

## 构建

需要 Windows x64、Visual Studio 2022 C++ Build Tools、Windows SDK 和 CMake 3.20+。先按各上游项目的说明准备 CPU 依赖，保留许可：

- [CTranslate2 v4.8.5](https://github.com/OpenNMT/CTranslate2/tree/v4.8.5)
- [SentencePiece v0.2.0](https://github.com/google/sentencepiece/tree/v0.2.0)

依赖安装目录需要包含公开头文件、链接库、CTranslate2 的 CMake 导出配置及运行 DLL。将下面两个示例路径替换为自己的安装前缀：

```powershell
cmake -S tools/LocalMtHost -B build/LocalMtHostMultilingual -G "Visual Studio 17 2022" -A x64 -DCT2_INSTALL_ROOT=C:/deps/ctranslate2 -DSP_INSTALL_ROOT=C:/deps/sentencepiece
cmake --build build/LocalMtHostMultilingual --config Release
```

输出为 `build/LocalMtHostMultilingual/Release/Thanslator.LocalMtHost.exe` 和旁边的 `ctranslate2.dll`。部署到主程序的 `LocalMtHost/`；发布脚本使用这个目录，不再引用本机临时工具链。

当前 CI 只验证托管构建。依赖 SDK 的干净环境构建和自动安装包流水线尚未配置，不能将已有本机原生验证当成远程 CI 通过。

## 协议与模型

命名管道使用小端 `uint32` 长度加 UTF-8 内容；错误响应以前缀 `__ERROR__:` 表示。进程不提供网络服务，由启动它的主程序管理。

主程序根据语言包清单传入架构、模型目录、语言方向、必要的源语言前缀与 EOS 策略。M2M100 使用共享 SentencePiece 及语言 token；Marian 保留旧分词器路径。模型需提前是兼容的 CTranslate2 格式，不直接载入 PyTorch/Safetensors。

语言包文件、哈希、方向和导入限制见[模型导入规范](../../docs/model-import-format-0.7.md)。
