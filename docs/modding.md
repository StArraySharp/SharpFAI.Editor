# SharpFAI Mod 开发与调试

## 安装示例

构建示例项目会生成基础包和工具包。工具包依赖基础包，并用 `IModServices` 调用其中的服务。

```powershell
dotnet build .\examples\SharpFAI.ModExamples -c Release
```

将构建目录 `examples/SharpFAI.ModExamples/bin/Release/net10.0/mods/` 下的 `Example.Base` 和 `Example.Tools` 文件夹复制到可执行文件旁的 `Mods`。编辑器会自动发现并启用它们。也可以到「设置 → Mod 管理」选择构建生成的 `.sharpfaimod` 文件安装，再启用工具包，基础包会随之加载。

从「谱面工具 → 打开谱面分析」或 `Ctrl+Shift+F9` 打开窗口。窗口可以切换深度着色与历史帧残影、调整实时总线增益，并显示谱面预处理执行次数。示例缓存只读 BPM，修改歌曲作者等字段会复用结果。播放带音乐的谱面时，增益对之后排队的音频块生效。

编辑器可在砖块属性的「Mod 事件」添加「暖色脉冲」。该事件随谱面保存，并按谱面时间在预览、播放和离线导出中求值。「文件 → 导出原版兼容副本」会把它近似转换为原版 `Flash`，在保存前列出近似转换及省略的项目。副本移除 SharpFAI 扩展字段，原谱继续保留可编辑的 Mod 事件。「渲染 → 导出谱面报告」可输出一个 JSON 文件。

## 开发版和热重载

「添加开发版」接收包根目录中与入口 DLL 同名的 JSON。示例构建生成 `bin/Release/net10.0/mods/Example.Base/Example.Base.json` 和 `bin/Release/net10.0/mods/Example.Tools/Example.Tools.json`。修改源码并重新构建后，在设置页点击「热重载」即可切换代码。重载基础包时，工具包也会按依赖顺序重新激活。新版本激活失败时，当前运行版本继续提供服务。

停用基础包会先关闭依赖它的工具包。仅因依赖而启用的包在失去最后一个依赖者后卸载。手动启用的包保留其启用状态。移除开发版只删除登记，源目录继续保留。

搜索目录默认为可执行文件旁的 `Mods`，启动时会自动创建。复制解压后的 Mod 文件夹会触发首次启用，手动停用的 Mod 会记住状态。`.sharpfaimod` 是安装包，在「设置 → Mod 管理」安装后会解压到 `Mods/packages/`。托管 Mod 与应用共享进程权限，请把可信的文件夹放入 `Mods`。可用 `SHARPFAI_MODS_DIR` 同时覆盖搜索目录和数据目录。Mod 统一在编辑器中启用，处理 Hook 也可参与编辑器的视频导出。用 `--no-mods` 启动可进入安全模式。

## SDK

接口签名、能力声明和处理阶段见 [Mod API 参考](mod-api.md)。

本机 MCP 接入和抓帧工具见 [SharpFAI MCP Mod](mcp-mod.md)。

在浏览器中查看和操作编辑器见 [SharpFAI Web 镜像 Mod](web-mirror.md)。

用 `[MenuItem("Tools/我的命令")]` 标记静态方法就能注册菜单。需要其他宿主服务时，实现 `ISharpFaiMod`，在 `ActivateAsync` 中用 `GetRequiredService<T>()` 获取必需服务，或用 `GetService<T>()` 检查可选服务。以示例项目为模板，编译时引用 `SharpFAI.ModApi` 1.0.0。[示例项目](../examples/SharpFAI.ModExamples/SharpFAI.ModExamples.csproj)

新包使用下面的布局。把目录内容压缩成 ZIP，再使用 `.sharpfaimod` 扩展名。入口 DLL 由同名 JSON 推断，依赖 DLL 放在同一目录。`entryType` 在入口程序集只有一个 Mod 类型时可省略。翻译可写入清单的 `translations`，包内素材可放在 `assets/`。原有 `mod.json`、`entryAssembly` 和 `assemblies/` 布局仍可读取。

```text
Example.Base.sharpfaimod
├── Example.Base.dll
├── Example.Base.json
└── SharpFAI.ModExamples.Shared.dll
```

生成本地 SDK 包的命令如下。

```powershell
dotnet pack .\SharpFAI.ModApi -c Release
```

| 接入点 | 当前支持 |
| --- | --- |
| 编辑器 | 只读谱面视图和可撤销的批量编辑，砖块检查器扩展，自定义事件字段和版本迁移 |
| 界面 | 顶层菜单、直接菜单命令、原有菜单插槽、Shift + 右键上下文菜单、可重绑快捷键、停靠 / 浮动 / 模态窗口、宿主对话框和窗口模式 |
| 播放 | 状态订阅、手打判定订阅、播放 / 暂停 / 定位请求，以及交互式文字叠加 |
| 谱面和素材 | 源谱面校验、播放副本变换、绑定后准备数据、按声明字段的增量缓存、按内容复用图片和歌曲处理结果 |
| 渲染 | `PrepareFrame`、`BackgroundAfterNative`、`SceneAfterNative` 三个阶段，颜色处理、帧内辅助纹理、场景深度和前一采样的历史纹理 |
| 音频和导出 | 实时音乐、打击音效和总线 DSP，同规则离线混音、自定义格式、MP4 临时产物后处理、原版兼容谱面副本 |

用 `ChartPreprocessorReads` 声明读取的根字段及设置键即可启用缓存。处理器只收到这些字段。缓存区分宿主、Mod 加载代数、时序、依赖输出和可选的 `CacheVersion`。外部配置变化时更新 `CacheVersion`。只读校验、绑定后准备和自定义事件可与既有增量预览并行，运行时改写原始谱面结构的处理器仍通过后台快照重建。

着色器使用 `vec4 modEffect(vec2 uv, vec4 color)` 作为入口，提供 `readColor(uv)`、`_ModTime`、`_ModParameters`、`_ScreenParams` 和 `_ModInput0` 至 `_ModInput3`。在场景后处理声明 `UsesSceneDepth` 后，`readDepth(uv)` 返回砖块、星球和有效世界装饰的设备深度，空背景为 1。声明 `UsesHistory` 后，`readHistory(uv)` 读取同一 pass 的上一采样输出，首帧回退到当前颜色。暂停重复绘制沿用同一份历史，跳转、重播、尺寸或投影切换及热重载会清空历史。附件和颜色输出使用相同像素尺寸。

实时音频使用 48 kHz 双声道、20 ms 为一个处理块。通过 `RegisterRealtime` 注册音乐、打击音效或混合总线处理器，宿主在音频工作线程创建实例，跳转时从新采样位置重建。每个 DSP 回调应只处理给定数据并避免阻塞。离线混音使用同一处理顺序，只有显式启用导出的扩展进入成片。
