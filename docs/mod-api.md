# SharpFAI Mod API 1.0.0

SDK 的 NuGet 包名和 C# 命名空间都是 `SharpFAI.ModApi`，目标框架是 `net10.0`。本文以当前公开接口和桌面宿主行为为准。构建、安装、热重载和示例操作见 [Mod 开发指南](modding.md)。

## 最小可用 Mod

创建 `net10.0` 类库，设置程序集名为 `ChartTools`，编译时引用 `SharpFAI.ModApi` 1.0.0。使用 SDK 源码时，可参考示例项目的 `ProjectReference`。打包时包含入口程序集和私有依赖，SDK 由宿主提供。[示例项目](../examples/SharpFAI.ModExamples/SharpFAI.ModExamples.csproj)

```csharp
using System;
using SharpFAI.ModApi;

namespace Example;

public static class ChartTools
{
    [MenuItem("Tools/检查谱面", Shortcut = "Ctrl+Shift+F8", SnapshotChart = false)]
    private static void Inspect(CommandContext context)
    {
        if (context.Chart is { } chart)
            Console.WriteLine($"{chart.FileName}: {chart.LastFloor + 1} tiles");
    }
}
```

将以下清单保存为 `ChartTools.json`，和 `ChartTools.dll` 一起放到可执行文件旁的 `Mods/ChartTools/` 中。也可以把这两个文件压缩为 `ChartTools.sharpfaimod`，确保文件位于 ZIP 根目录。在编辑器的「设置 → Mod 管理」里启用并确认后，命令会出现在「工具」菜单。通过终端启动时可以看到命令打印的砖块数。

```json
{
  "id": "com.example.charttools",
  "name": "Example Chart Tools",
  "version": "1.0.0",
  "api": "[1.0.0,2.0.0)"
}
```

## 包和清单

```text
ChartTools.sharpfaimod
├── ChartTools.dll
├── ChartTools.json
├── PrivateDependency.dll
├── assets/
└── locales/
```

启动时会创建并扫描可执行文件旁的 `Mods`。压缩包和解压目录都支持，运行中新增文件可在 Mod 管理页刷新。`SHARPFAI_MODS_DIR` 可同时覆盖搜索目录和 Mod 数据目录。编辑器负责启用 Mod，并在编辑预览、播放和视频导出中调用相应的 Hook。

| 字段 | 约定 |
| --- | --- |
| `manifestVersion` | 当前格式为 `1`，可省略 |
| `id`、`name`、`version` | 小写反向域名 ID、显示名和 SemVer 版本 |
| `api` | SDK 版本范围。支持 `*`、区间如 `[1.0.0,2.0.0)`、精确匹配如 `[1.0.0]`。单写 `1.0.0` 表示最低版本 |
| `entryType` | 可选的 `ISharpFaiMod` 入口类型全名。只有一个具体实现时可自动识别；入口应提供公开无参构造函数。纯静态菜单 Mod 可以只用 `[MenuItem]` |
| `dependencies` | 必需依赖，例如 `[{"id":"com.example.base","version":"[1.0.0,2.0.0)"}]`。宿主选择兼容版本，并先激活依赖 |
| `platforms` | 可选的 .NET RID 列表，例如 `win-x64`。省略时不按 RID 筛选 |
| `translations` | 可选的语言到翻译键值表，也可随包提供 `locales/<culture>.json` |

宿主按同名 DLL 和 JSON 识别入口，每个包根目录只能有一对。私有程序集可放在入口旁。旧格式可用 `mod.json` 中的 `entryAssembly` 指定相对路径，旧清单中的 `hosts` 会被忽略。用户确认启用后，宿主校验包内容并加载其副本。热重载会重载受影响的依赖方；新版本激活失败时，已激活的版本继续运行。Mod 以宿主进程的权限执行。谱面依赖只用于提示或限制导出，不会自动安装或信任 Mod。

## 生命周期与服务

在 `ISharpFaiMod.ActivateAsync(IModContext, CancellationToken)` 中注册需要实例状态或宿主服务的功能。贡献 ID 使用 `<mod-id>/<name>`；同一注册类型内应唯一。注册方法返回 `IDisposable`，可提前撤销，也会在 Mod 停用时由宿主清理。停用时取消生命周期令牌，待占用回调结束后调用 `DisposeAsync()`。Mod 自己启动的任务及持有的 I/O 资源应在停用时释放。注册在激活成功后对应用生效。

`IModContext` 提供 `Id`、`Version`、`Host`、`DataDirectory`、`OpenAsset(path)`、`Log(message)` 和 `Text(key, fallback)`。`Host` 固定为 `UiHost.Editor`；单次处理是编辑器预览还是离线导出，从 Hook 上下文读取。`OpenAsset` 的路径相对于包内 `assets/`，返回的流由调用方释放。`Text` 依次查询当前语言、父语言和英文，最后返回 fallback 或键。`DataDirectory` 可放置 Mod 自己的持久配置。

在 C# 中按接口类型获取服务。`GetRequiredService<T>()` 找不到服务时会抛出带服务名称的异常。若在 `ActivateAsync` 中发生，宿主会记录错误并取消这次启用。可选功能用 `GetService<T>()`，返回 `null` 时跳过相关逻辑。旧清单里的 `requiredCapabilities` 和 `optionalCapabilities` 会被忽略。

```csharp
var windows = context.GetRequiredService<IToolWindowRegistry>();

var audio = context.GetService<IAudioProcessorRegistry>();
if (audio != null)
{
    // 在这里用 audio 注册音频处理器。
}
```

| 服务接口 | 用途 |
| --- | --- |
| `IEditorCommands`、`IMenuRegistry` | 手动注册命令和菜单 |
| `IToolWindowRegistry`、`IUiDialogs`、`IMainWindowService` | 工具窗口、对话框和主窗口 |
| `IChartEdits`、`IInspectorRegistry` | 修改谱面、扩展检查器 |
| `IPlaybackEvents`、`IPlaybackControl` | 观察和控制播放 |
| `IChartPreprocessorRegistry`、`IAssetPreprocessorRegistry` | 预处理谱面和素材 |
| `ICustomEventRegistry` | 自定义事件与原版兼容转换 |
| `IRenderPassRegistry`、`IOverlayRegistry` | 渲染处理和文字叠加 |
| `IAudioProcessorRegistry` | 音频处理和实时 DSP |
| `IExportJobRegistry`、`IExportPostprocessorRegistry` | 自定义导出和视频后处理 |
| `IModServices` | 调用其他 Mod 提供的服务 |

`IModServices.Publish(id, handler)` 注册 JSON 消息端点。`InvokeAsync(id, request, token)` 返回 `JsonElement`，可调用本 Mod 已生效的端点和清单中直接声明的依赖端点。激活阶段可调用已就绪的依赖端点。端点处理应遵守请求取消令牌。

处理接口共用 `HookDescriptor(Id, Hosts = UiHost.All, Order = 0, After = null, Required = false)`。`After` 可指定同类 Hook 的先后关系；同时可执行时先按 `Order`，再按 ID 排序。各阶段有固定顺序。允许跳过的 Hook 失败会记入日志。加载和导出管线遇到按 `Required` 处理的失败时会终止对应操作；交互式渲染和实时播放优先维持运行。

## 命令、菜单与窗口

`[MenuItem("Tools/子菜单/命令")]` 会在激活时自动注册命令和菜单项。可以标记入口程序集中的静态方法，也可以标记 `ISharpFaiMod` 入口实例的方法。返回类型支持 `void`、`Task` 和 `ValueTask`，可声明零个参数、一个 `CommandContext` 或 `CancellationToken`，也可按该顺序接收两者。

路径用 `/` 划分。首段可以是内置菜单的中文或英文名称，其他名称会创建 Mod 自己的顶层菜单。单段路径会注册为菜单栏上的直接命令。路径段可用翻译键，例如示例的 `Tools/menu/open`。`Shortcut` 设置默认快捷键，`Order` 决定排序，`SnapshotChart` 控制是否为命令生成谱面快照。

- 需要动态决定按钮状态或手动注册时，用 `IEditorCommands.Register(descriptor, handler)`。`CommandDescriptor.State` 返回 `CommandState(Enabled, Checked, Visible)`；频繁调用的状态计算应轻量。`IPlayerCommands` 保留为兼容接口。用户可在 Mod 管理中重绑快捷键。命令的 `SnapshotChart` 默认启用。仅即时读取时可设为 `false`，此时应在回调返回前完成谱面视图的读取。
- `IMenuRegistry.RegisterMenuBarMenu` 注册顶层菜单，`RegisterMenuBarCommand` 将命令直接放入菜单栏。两者用 `MenuBarSlot.Extensions`。`RegisterItem` 的目标为 `MenuTarget.ModMenu(id)` 或 `MenuTarget.BuiltIn(slot)`，可用 `MenuContribution.Submenu` 指定子菜单层级。菜单和条目引用的命令应由当前 Mod 注册。
- 编辑器提供 `File`、`Edit`、`View`、`Chart`、`Render`、`Tools`、`Help` 菜单槽，以及 `TileContext`、`DecorationContext`、`TimelineContext` 上下文槽；在对应区域用 Shift + 右键打开。
- `IInspectorRegistry.Register(descriptor, fields)` 在编辑器检查器中添加 `InspectorRow(Label, Value, CommandId)`。可绑定当前 Mod 的命令。
- `IToolWindowRegistry.Register(descriptor, create)` 注册窗口工厂。`ToolWindowDescriptor` 可选 `Dockable`、`Floating`、`Modal`，作用域为 `Application` 或 `Document`；可设默认停靠位置、最小尺寸和窗口菜单可见性。注册后可从命令中调用 `OpenAsync(id, token)`，并用 `GetState(id)` 或 `CloseAsync(id, token)` 管理当前 Mod 的窗口。文档窗口随所属谱面关闭。
- `IToolWindow.Build(in ToolWindowContext)` 每帧返回控件树；`HandleAsync(UiAction, token)` 接收交互；`DisposeAsync()` 释放窗口资源。频繁执行的 `Build` 应只读当前状态，将修改放在交互处理回调中。
- `IUiDialogs.ConfirmAsync(title, message, token)` 返回用户是否确认。`OpenFileAsync(description, extensions, token)` 的扩展名用逗号分隔，例如 `"png,jpg"`，取消时返回 `null`。
- `IMainWindowService.State` 提供窗口尺寸、帧缓冲像素尺寸、缩放、焦点和全屏状态。`Subscribe(callback)` 订阅状态变化，`RequestFullscreenAsync(fullscreen, token)` 请求切换。

| `UiNode` | 用法及 `UiAction.Value` |
| --- | --- |
| `UiText` | 普通或弱化文字 |
| `UiButton` | 按钮，可禁用；动作不带值 |
| `UiToggle` | 复选框；动作值为 JSON 布尔值 |
| `UiNumber` | 带范围的滑块；动作值为 JSON 数字 |
| `UiChoice` | 下拉选项；动作值为从 0 开始的索引 |
| `UiProgress` | `0–1` 进度 |
| `UiGroup` | 可折叠的子控件组 |
| `UiTable` | 字符串表格，支持 1–64 列 |

相关签名见 [ModApi.cs](../SharpFAI.ModApi/ModApi.cs)。

## 读取谱面、编辑和播放

`CommandContext.Chart` 在没有谱面时可以是 `null`。`ChartView` 提供文件名、当前选中砖块、末尾砖块索引和 `ChartRevision(Document, Version)`。首块索引为 0。`ReadAngle(floor)` 返回计算后的入射角；第 0 块和无效索引返回 `null`。`ReadSetting(key)` 返回可空的 JSON 值。`ReadEvents(floor)` 返回该砖块的普通谱面事件。

`IChartEdits.CommitAsync(expected, edit, token)` 在编辑器编辑状态提交一个 `ChartEditRequest(Description, Operations)`。操作按顺序应用，成功的一批修改合并为一次撤销。传入读谱时的 `ChartRevision` 检查并发修改。返回值为 `Applied`、`Invalid` 或 `RevisionConflict`；修订冲突时应重新读取并计算操作。

| `ChartOperation` | 语义 |
| --- | --- |
| `InsertAngles(AfterFloor, Angles)` | 在指定砖块后插入有限角度值 |
| `DeleteAngles(FirstFloor, Count)` | 删除连续砖块，起始索引须大于 0 |
| `SetChartSetting(Key, Value)` | 写入 JSON 设置值，`bpm` 和 `pitch` 必须为正数 |
| `AddChartEvent(Floor, Type, Fields)` | 添加普通事件，`Fields` 为 JSON 对象，宿主写入 `floor` 和 `eventType` |
| `UpdateChartEvent(Floor, Index, Fields)` | 按 `ReadEvents` 的零基索引更新事件字段，砖块和事件类型由原事件决定 |
| `RemoveChartEvent(Floor, Index)` | 删除指定普通事件 |

`IPlaybackEvents.Subscribe(callback)` 观察交互式 `PlaybackState`，包含 `Mode`、`Time`、`Duration`、`Floor` 和 `Playing`。时间以秒计，连续时间通知会限频。`SubscribeJudgments(callback)` 接收手打判定，包括时间、砖块、角度偏差、判定名称、是否接受和是否失败。`IPlaybackControl.RequestAsync(action, time, token)` 可请求 `Play`、`Pause` 或 `Seek`，跳转参数为有限秒数。手打、设置窗口或导出等状态可能拒绝请求，调用方应检查返回值。

## 谱面预处理

`IChartPreprocessorRegistry.Register(descriptor, stage, process)` 注册一个从 `ChartPreparationContext` 返回 `ChartPreparationResult` 的异步处理器。也可在回调前传入 `ChartPreprocessorReads`，启用按输入复用的缓存。

| `ChartPreparationStage` | 输入及结果 |
| --- | --- |
| `ValidateSource` | 读取源谱面，返回 `Diagnostics`，可附砖块编号 |
| `PrepareChart` | 修改用于播放或导出的副本。`Operations` 支持 `SetChartSetting`、`AddChartEvent`。多个 Hook 修改同一个设置时，后写入者须用 `After` 声明顺序 |
| `PrepareBoundData` | `EntryTimes` 提供每块砖的进入时间（秒）。返回的 `Data` 以 Hook ID 为键，供后续同阶段处理器和自定义事件读取 |

`ChartPreprocessorReads.RootFields` 声明 JSON 根字段。若包含 `"settings"`，可以用 `SettingKeys` 进一步指定键；例如 `new ChartPreprocessorReads(["settings"], ["bpm"])`。宿主只传递声明范围内的 JSON。缓存还区分宿主、Mod 加载代数、时序和前置输出。若结果还依赖 Mod 的其他配置，应通过 `CacheVersion` 返回其版本标识。使用无读取声明的重载可获取完整快照。只读校验、绑定后数据和自定义事件支持编辑器增量预览；修改播放副本的 Hook 由后台重建。

## 素材预处理

`IAssetPreprocessorRegistry.Register(descriptor, kind, process)` 支持 `AssetKind.Music` 和 `AssetKind.Image`。`AssetPreparationContext` 提供原始文件名和 `OpenRead()`，后者打开当前处理链输入，流由回调释放。返回 `null` 表示沿用输入；返回 `PreparedAsset(Extension, Content)` 则对该会话使用新内容。扩展名去掉前导点后须为 1–8 个 ASCII 字母或数字，内容须非空且不超过 128 MiB。结果按源文件内容和 Hook 复用；保持同一输入的输出一致。

## 自定义事件和原版兼容副本

`ICustomEventRegistry.Register(descriptor, compile, evaluate, schema)` 用 `descriptor.Id` 作为事件类型。提供 `CustomEventSchema(TitleKey, Version, Fields, Migrate)` 后，编辑器会在砖块的「Mod 事件」面板显示该事件。每个 `CustomEventField` 都要有唯一的 `Key` 和 JSON `Default`。`Number` 使用 `Minimum` 和 `Maximum`，`Toggle` 保存布尔值，`Text` 保存字符串，`Choice` 保存 `Choices` 中选中的字符串。保存的数据包含类型、字段版本、砖块锚点、角度偏移和 JSON 参数，位于 `sharpFaiMods.events`。`Version` 从 1 开始。处理旧字段时，`Migrate(oldVersion, payload)` 返回当前版本的参数。

`compile` 接收 `CustomChartEvent`，包含锚点砖块、计算后的事件时间（秒）和绑定阶段的 `PreparedData`。返回 `null` 可跳过此事件，或返回时间有效的 `TimedChartEvent(Start, End, Payload)`。`evaluate` 只在时间区间内被采样，返回 `FrameEffect`。按传入的时间求值可让定位、重播和离线逐帧渲染得到一致的事件结果。

`RegisterCompatibilityConverter(descriptor, convert)` 使用同一事件类型 ID，并让 `descriptor.Hosts` 包含 `UiHost.Editor`。「文件 → 导出原版兼容副本」向转换器传入 `CompatibilityEvent`，包含 `Floor`、`AngleOffsetDegrees`、`SchemaVersion`、`Payload` 和锚点 `Bpm`。转换器自行识别该版本的参数。

- 返回 `CompatibilityConversion(Actions, Decorations, Note)` 追加原版事件和装饰。装饰列表接受 `AddDecoration`、`AddText`、`AddObject` 和 `AddParticle`。
- 返回 `null` 或空结果时，该事件会在副本中省略，并列入导出提示。`Note` 可说明近似转换或省略原因。
- 只在副本写入这些原版事件。副本移除根级 `sharpFai*` 扩展；源谱保留 Mod 事件。

## 渲染与叠加

`IRenderPassRegistry.Register(descriptor, stage, sample)` 返回 `FrameEffect(Multiply, Add)`，按通道执行 `color * Multiply + Add`，无变化时返回 `FrameEffect.Identity`。`RenderFrame` 提供谱面时间（秒）、像素宽高、`Scene3D` 和目标宿主。

| `RenderStage` | 执行时机 |
| --- | --- |
| `PrepareFrame` | 清屏后，绘制背景前。可为后续阶段生成辅助纹理 |
| `BackgroundAfterNative` | 画完背景及其原生滤镜后，绘制主场景前 |
| `SceneAfterNative` | 主场景及其原生滤镜完成后。随后求值当前时间的自定义事件 |

`RegisterShader(descriptor, stage, shader, parameters, enabled)` 运行全分辨率全屏 GLSL。`RenderShader.Source` 长度为 1–65,536 个字符，只需定义 `modEffect`；宿主提供 GLSL 410 声明和 `main`。例如设置 `UsesHistory = true` 后可用以下代码制作轻微残影。

```glsl
vec4 modEffect(vec2 uv, vec4 color) {
    return mix(color, readHistory(uv), 0.35);
}
```

| GLSL 接口 | 内容 |
| --- | --- |
| `readColor(uv)` / `_MainTex` | 当前 pass 的输入颜色 |
| `_ModTime` / `_ModParameters` | 谱面时间（秒）和 `parameters` 返回的 `vec4`，参数默认全 0 |
| `_ScreenParams` / `_MainTex_TexelSize` | 分辨率和输入纹素尺寸。`_ScreenParams.xy` 为宽高，`_MainTex_TexelSize.xy` 为宽高的倒数 |
| `_ModInput0` 至 `_ModInput3` | `RenderShader.Inputs` 引用的当前帧辅助纹理，按列表顺序绑定。生产者须声明 `ProducesAuxiliary = true`，并先于引用方执行 |
| `readDepth(uv)` | `SceneAfterNative` 中声明 `UsesSceneDepth` 后可用。返回砖块、星球和有效世界装饰的设备深度，空背景为 `1`，不包含屏幕 UI 或拖尾 |
| `readHistory(uv)` | 声明 `UsesHistory` 后，读取该 pass 上一采样的输出。历史不可用时返回当前输入颜色 |

辅助 pass 把产物交给当前帧的其他 pass。引用方启用时应保证生产者也启用。历史纹理在暂停的同一时间点重复绘制时不会累积；跳转、重播、尺寸或 2D/3D 投影切换及热重载会重置历史。

`IOverlayRegistry.Register(descriptor, sample)` 返回交互式文字叠加。`OverlayText.NormalizedPosition` 的范围按视图归一化，`(0,0)` 为左上角。文字颜色用 ImGui 的 RGBA 打包格式，默认不透明白色。视频画面效果应使用渲染 Hook；交互式文字叠加由界面绘制。

## 音频处理

| 接口 | 数据 |
| --- | --- |
| `IAudioProcessorRegistry.Register(descriptor, bus, process)` | 按源 `AudioFormat` 修改交错的 `Span<short>`。音乐、打击音效在加载阶段处理；`AudioBus.Mix` 用于离线混音结果 |
| `IAudioProcessorRegistry.RegisterRealtime(descriptor, bus, create)` | 为 `Music`、`Hitsounds` 或 `Mix` 注册 `IRealtimeAudioProcessor` 工厂。实时播放和离线导出共用总线顺序 |

实时实例先收到 `Reset(format, firstFrame)`，再收到连续的 `Process(in block, samples)`，结束时调用 `Dispose()`。当前格式为 48 kHz 双声道交错浮点 PCM，每块最多 960 帧（20 ms）；离线末块可短于此长度。`RealtimeAudioBlock.FirstFrame` 是当前块的起始采样帧，`Offline` 区分播放和导出。跳转时会重建实例并从新的帧位置开始。

`Music` 和 `Hitsounds` 先分别处理，再应用用户音量并相加，最后运行 `Mix`。最终输出把有限值限制在 `[-1, 1]` 后量化为 16 位 PCM。就地处理回调提供的缓冲区，避免阻塞、频繁分配或跨回调保存 Span。

## 自定义导出与视频后处理

- `IExportJobRegistry.Register(descriptor, run)` 注册交互式导出项。`ExportJobDescriptor` 包含 `Hook`、`TitleKey` 和 `Extension`，扩展名使用 1–12 个 ASCII 字母或数字。`ExportJobContext` 提供 `Chart`、`TemporaryPath` 和 `Progress`。向临时路径写入完整文件，用 `Progress.Report(0…1)` 报告进度，并响应取消令牌。成功后宿主把文件移到用户选择的位置。
- `IExportPostprocessorRegistry.Register(descriptor, process)` 用于原生视频导出后的产物处理。该 Hook 的目标应包含 `UiHost.Export`。`ExportArtifact` 提供 `TemporaryPath` 及最终文件名 `OutputFileName`。宿主为每个处理器提供临时副本；回调成功且产物非空时才采用该副本。
- 编辑器的视频导出使用 `UiHost.Export` 筛选谱面预处理、自定义事件、渲染和音频 Hook。原版兼容谱面副本使用前述 `RegisterCompatibilityConverter`。

完整处理签名见 [Processing.cs](../SharpFAI.ModApi/Processing.cs)，可运行的注册示例见 [ExampleMods.cs](../examples/SharpFAI.ModExamples/ExampleMods.cs)。

## 实时查询与离屏抓帧

`IEditorInspection` 读取包含未保存改动的编辑文档。先用 `GetChartAsync` 获取修订号，后续的砖块、事件、装饰、定位和诊断查询都可以指定该修订。返回数据可以在后台线程读取，事件和装饰以分页结果返回。

`IEditorFrameCapture` 从指定修订准备独立渲染器，按砖块或音乐时间返回摄像机和滤镜状态，可选 PNG。抓帧通过编辑器的渲染线程执行，并接受取消信号。每次只运行一个离屏抓帧任务。

Mod 可以实现 `IModActivationObserver`，在新实例发布且旧实例释放后启动独占资源，例如本地端口。仍在 `ActivateAsync` 内注册菜单、窗口和其他贡献，并在 `DisposeAsync` 中释放资源。

接口见 [EditorInspection.cs](../SharpFAI.ModApi/EditorInspection.cs)，使用示例见 [SharpFAI MCP Mod](mcp-mod.md)。

## 窗口画面与输入

`IEditorWindowStream.TryOpen()` 打开一个独占会话，用于读取带界面的编辑器窗口画面和发送输入。按需调用 `CaptureAsync`，每次最多保留一个请求，可选 RGBA8 或 JPEG。鼠标坐标按客户区归一化至 0–1，按键使用物理键名，例如 `KeyA`。客户端失去焦点时可以发送 `Reset`，断开连接时释放会话，编辑器会松开远程按住的按键和鼠标。接口见 [EditorWindowStream.cs](../SharpFAI.ModApi/EditorWindowStream.cs)。

## 声明式 Mod 设置

入口类在实现 `ISharpFaiMod` 之外再实现 `IModSettings`，即可声明实例字段或属性。宿主会在 `ActivateAsync` 前载入保存值。默认读取入口对象上的成员，也可以实现 `object Settings => _settings`，返回单独的设置对象。

```csharp
public sealed class ExampleMod : ISharpFaiMod, IModSettings
{
    [ModSettingLabel("自动启动"), ModSettingTooltip("在启用此 Mod 时启动")]
    private bool _autoStart = true;

    [ModSettingLabel("帧率"), ModSettingRange(1, 60), ModSettingShowIf(nameof(_autoStart))]
    private int _frameRate = 24;

    public ValueTask ActivateAsync(IModContext context, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

任意 `ModSetting` 系列标注都会将成员纳入设置。布尔值显示为复选框，枚举显示为下拉框，标注范围的 `int` 和 `float` 显示为滑块。其他数值及字符串可直接输入，数组和对象用多行 JSON 编辑。`ModSettingJson(Lines = 6)` 可给存储 JSON 的字符串使用多行编辑和语法校验。还支持 `ModSettingHeader`（分组）、`ModSettingOrder`（排序）、`ModSettingShowIf`（条件禁用）、`ModSettingReadOnly`（只读）、`ModSettingNoSave`（当前会话）和 `ModSettingIgnore`（排除）。标签、分组名和提示可对应清单中的翻译键。

在「设置 → Mod 管理 → 对应 Mod → 设置」编辑后点击「应用」。宿主保存成功后在编辑器线程调用 `OnSettingsChanged()`，Mod 可在此更新正在运行的功能。JSON 键使用 C# 成员名。首次保存前使用声明的默认值。

`IModSettings` 实现可在 `ActivateAsync` 中通过 `context.GetRequiredService<IModSettingsStore>()` 获取配置文件路径，并用 `Save()` 保存代码修改的值。`Save()` 只写入已声明持久化的成员。用 `[ModSettingHidden]` 保存由代码管理的值，例如令牌，同时从设置界面隐藏该成员。

- 对于放在搜索目录的 `Example.sharpfaimod` 文件或目录，配置是同级的 `Example.Settings.json`。直接添加的开发目录也按目录名保存。
- 从界面安装的 Mod 按版本存放在 `Mods/packages/<Mod ID>/` 下，配置用入口 DLL 的基本名命名，与各版本目录同级。
- 停用、热重载和移除会保留同级文件。再次安装同一个 Mod 可继续使用原来的设置。

接口见 [ModSettings.cs](../SharpFAI.ModApi/ModSettings.cs)，实际用法见 [WebMirrorMod.cs](../mods/SharpFAI.WebMirror/WebMirrorMod.cs)。字段声明参考 [StArray.ModManager Inspector](https://github.com/StArraySharp/StArray.ModManager/tree/main/StArray.ModManager/Inspector)。
