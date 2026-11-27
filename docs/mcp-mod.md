# SharpFAI MCP Mod

## 安装和连接

MultiPlatforms 发布包的 `Mods/` 已包含解压后的 MCP。MCP Mod 1.1.0 的新工具需要 SharpFAI Editor 1.0.5 或更高版本。使用单独下载的编辑器时，可在「设置 → Mod 管理」安装发布附件 `SharpFAI.Mcp.sharpfaimod`，或将解压后的文件夹复制到 `Mods`。从源码单独构建 Mod 可在仓库根目录执行以下命令。

```powershell
dotnet build mods/SharpFAI.Mcp/SharpFAI.Mcp.csproj -c Release
```

启用 Mod 后，打开「工具 → MCP」查看地址、配置文件路径和运行状态，也可以在这里停止或重新启动服务。监听端口和自动启动可在「设置 → Mod 管理 → SharpFAI MCP → 设置」调整。端口在下次启动监听时生效。

地址默认是 `http://127.0.0.1:37181/mcp`。使用支持 Streamable HTTP 的客户端，将 `SharpFAI.Mcp.Settings.json` 中的 `Token` 值填入请求头。持有令牌的客户端可以读取和修改谱面、按本地路径打开和保存谱面，以及导出视频。请将令牌保存在本机。

```text
URL: http://127.0.0.1:37181/mcp
Authorization: Bearer <Token 的值>
```

监听仅绑定 `127.0.0.1`。配置文件和 Mod 同级。首次加载会读取旧版 `settings.json` 和 `token.txt`，保留已有端口、自动启动选项和有效的令牌。热重载会中断正在进行的请求，客户端可用同一个令牌重连。随附的 MCP 服务使用官方 C# SDK。 [ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore/2.1.0)

## 工具

| 工具 | 用途 |
| --- | --- |
| `chart_context` | 读取当前谱面的修订号、选择、设置及播放状态 |
| `chart_locate` | 在砖块、音乐时间和四分音符位置之间换算，同时返回 BPM 和拍号 |
| `chart_floors` | 读取连续砖块的原始角度和时间 |
| `chart_events` | 按砖块范围、事件类型或标签分页查找事件 |
| `chart_decorations` | 按类型或标签分页读取装饰原始参数 |
| `chart_event_schema` | 查找编辑器事件或装饰的字段、类型、默认值和范围 |
| `chart_diagnostics` | 读取编辑器渲染器报告的加载错误和警告 |
| `chart_capture_frame` | 按砖块或秒数离屏渲染，返回摄像机、滤镜参数、绘制统计及可选 PNG |
| `chart_edit` | 批量修改砖块、事件、装饰和关卡设置 |
| `chart_transport` | 自动播放、暂停、跳转或返回编辑模式 |
| `chart_edit_preview` | 用与提交相同的校验预演操作，返回各操作修改前后的值和第一条无效操作的索引 |
| `chart_open`、`chart_save` | 通过绝对路径打开和保存 `.adofai` 或 `.json` |
| `chart_tempo`、`chart_convert_tempo` | 查询砖块设定与实时 BPM，或按目标实时 BPM 反算当前变速事件的字段和值 |
| `chart_selection` | 读取、设置、清除 A–B 选区，或开始和停止循环播放 |
| `chart_profile_playback` | 在编辑器窗口实际播放指定区间，返回帧间隔分位数和带阶段耗时的慢帧 |
| `chart_compare_frames` | 对比当前画面与预演修改后的画面或本地参考 PNG，返回差异图和参数 |
| `chart_export_video`、`chart_video_status` | 导出 A–B 选区到指定 MP4，查询进度或取消 |

砖块、事件和装饰的索引从 0 开始。时间单位是音乐文件的秒数。时间查询和诊断依赖编辑器当前的同步状态，遇到 `timingReady: false` 或 `rendererCurrent: false` 可以稍后重试。分页查询即使返回空页，也可能带有继续扫描的游标。

建议先调用 `chart_context` 获取 `revision`，再用 `chart_locate` 和 `chart_events` 检查目标段落。把同一个 `revision` 传给抓帧和修改请求。修改成功后重新读取修订号，再抓一张图比较。下面是一组 `chart_edit` 参数，其中 `revision` 应替换为实际值。

```json
{
  "revision": { "document": "11111111-1111-1111-1111-111111111111", "version": 4 },
  "description": "调整镜头和文字",
  "operations": [
    { "kind": "setSetting", "key": "zoom", "value": 125 },
    { "kind": "updateDecoration", "index": 0, "fields": { "text": "Intro" } }
  ]
}
```

批量修改在编辑模式下执行，并计为一次撤销。索引以执行到该操作时的谱面为准。可以先把同一组参数交给 `chart_edit_preview`，确认后调用 `chart_edit`，结果包含新修订号 `revision`。删砖块预览列出首 512 个原始角度和删除总数。

打开和保存都可以只传 `path`。可选的 `revision` 用于明确指定当前谱面版本。打开其他谱面遇到未保存修改会返回错误，除非显式传入 `discardUnsaved: true`。保存到不同且已存在的文件需要 `overwrite: true`，同路径保存使用编辑器的普通保存行为。

性能采样需要先暂停播放。传入音乐文件秒数 `start` 和 `end`，一次最多采样 60 秒。报告包含实际窗口帧间隔和纹理上传、贴图解码等待、粒子、滤镜的 CPU 阶段耗时。

画面对比使用相同的时间、尺寸和预滚。传入与抓帧尺寸一致的绝对 `referencePng` 路径，或传入一组 `operations` 预演修改。修改仅用于离屏对比。结果包括 RGB 差异图，预演对比还会返回两侧的摄像机和滤镜参数。

导出片段先用 `chart_selection` 的 `set` 动作设置 `start`、`end`，再用 `chart_export_video` 指定绝对 `outputPath`。编辑器需能找到 FFmpeg。使用返回的任务 ID 调用 `chart_video_status`，传入 `cancel: true` 可取消，编辑器保留最近一次 MCP 导出任务的结果。

抓帧使用独立渲染器并遵循当前画质设置。依赖前一帧的特效可传入 `prerollSeconds` 和 `prerollFps` 预渲染一段画面，最长 10 秒。默认不预滚，`includeImage: false` 可只取帧状态。首次抓帧需要准备当前修订的离屏场景，大谱面的读取和渲染耗时取决于资源规模。
