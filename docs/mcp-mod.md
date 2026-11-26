# SharpFAI MCP Mod

## 安装和连接

MultiPlatforms 发布包的 `Mods/` 已包含 MCP。使用单独下载的编辑器时，将发布附件 `SharpFAI.Mcp.sharpfaimod` 放入编辑器可执行文件旁的 `Mods`，或在「设置 → Mod 管理」安装。从源码单独构建 Mod 可在仓库根目录执行以下命令。

```powershell
dotnet build mods/SharpFAI.Mcp/SharpFAI.Mcp.csproj -c Release
```

启用 Mod 后，打开「工具 → MCP」查看地址、配置文件路径和运行状态，也可以在这里停止或重新启动服务。监听端口和自动启动可在「设置 → Mod 管理 → SharpFAI MCP → 设置」调整。端口在下次启动监听时生效。

地址默认是 `http://127.0.0.1:37181/mcp`。使用支持 Streamable HTTP 的客户端，将 `SharpFAI.Mcp.Settings.json` 中的 `Token` 值填入请求头。令牌允许客户端读取当前谱面和修改编辑缓冲区，请保存在本机。

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

批量修改在编辑模式下执行，并计为一次撤销。索引以执行到该操作时的谱面为准。保存仍由编辑器执行。

抓帧使用独立渲染器并遵循当前画质设置。依赖前一帧的特效可传入 `prerollSeconds` 和 `prerollFps` 预渲染一段画面，最长 10 秒。默认不预滚，`includeImage: false` 可只取帧状态。首次抓帧需要准备当前修订的离屏场景，大谱面的读取和渲染耗时取决于资源规模。
