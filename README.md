# SharpFAI.Editor

SharpFAI.Editor 是《冰与火之舞》（A Dance of Fire and Ice，ADOFAI）的非官方谱面编辑器。仓库同时提供独立的 SharpFAI Player，用于播放谱面和离线渲染视频。

## 功能

- 编辑轨道、事件和装饰，支持打开 `.adofai` 文件和导入 `.zip` 谱面包。
- 在编辑器内预览或播放谱面，也可以用独立播放器查看效果。
- 按指定分辨率和帧率导出视频。FFmpeg 编码 H.264 和 AAC，SharpMP4 封装最终文件。缺少 FFmpeg 时可输出 PNG 帧序列。
- 播放和导出铺面指定的背景视频。SharpMP4 读取 MP4、M4V 和 MOV 的视频信息，FFmpeg 解码画面。其他容器需要 `ffprobe` 读取视频信息。

## 从源码运行

使用 Windows x64、支持 OpenGL 4.1 的显卡驱动，以及 Git 和 .NET 10 SDK。克隆时一并获取子模块。

```powershell
git clone --recurse-submodules https://github.com/StArraySharp/SharpFAI.Editor.git
cd SharpFAI.Editor
dotnet build .\SharpFAI.Editor.slnx -c Release
dotnet run --project .\SharpFAI.Editor -c Release
```

启动后从「文件 → 打开」选择谱面。加上 `--player` 可从同一入口启动播放器模式。

```powershell
dotnet run --project .\SharpFAI.Editor -c Release -- --player --open ".\example.adofai"
```

原版滤镜使用本机 ADOFAI 安装中的构建输入。完整步骤见 [构建指南](docs/build.md)。

## 打包与测试

在仓库根目录通过 PowerShell 7 运行打包脚本，可在 `publish/` 生成自包含的 Windows x64、Linux x64、macOS x64 和 macOS arm64 编辑器。文件名包含 RID 和版本号，四个可执行文件和字体许可证也会打包到 `SharpFAI.Editor-MultiPlatforms-<版本>.zip`。 [publish.ps1](publish.ps1)

```powershell
pwsh -NoProfile -File .\publish.ps1
pwsh -NoProfile -File .\publish.ps1 -Runtime linux-x64
dotnet test .\SharpFAI.Editor.slnx -c Release
```

桌面 GPU 集成测试单独运行，需要 OpenGL 环境。涉及视频编码的测试还需要 FFmpeg。

## 第三方库与工具

| 用途 | 项目与 NuGet 包 |
| --- | --- |
| 窗口、输入及 OpenGL / OpenAL 绑定 | [OpenTK](https://www.nuget.org/packages/OpenTK) |
| 编辑器和播放器界面 | [Dear ImGui](https://github.com/ocornut/imgui)、[ImGui4NET](https://www.nuget.org/packages/ImGui4NET)、[ImGui4NET.Backend.OpenGL3](https://www.nuget.org/packages/ImGui4NET.Backend.OpenGL3) |
| 原生文件对话框 | [NativeFileDialog](https://www.nuget.org/packages/NativeFileDialog) |
| 音频播放与 MP3、Ogg Vorbis 解码 | [OpenAL Soft](https://github.com/kcat/openal-soft)（[OpenAL.Soft](https://www.nuget.org/packages/OpenAL.Soft)）、[MP3Sharp](https://www.nuget.org/packages/MP3Sharp)、[NVorbis](https://www.nuget.org/packages/NVorbis) |
| 图像处理、字体排版和文字绘制 | [SixLabors.ImageSharp](https://www.nuget.org/packages/SixLabors.ImageSharp)、[SixLabors.ImageSharp.Drawing](https://www.nuget.org/packages/SixLabors.ImageSharp.Drawing)、[SixLabors.Fonts](https://www.nuget.org/packages/SixLabors.Fonts) |
| 内置中日韩字体 | [Noto Sans CJK](https://github.com/notofonts/noto-cjk)、[字体许可证](SharpFAI.Player/Resources/Fonts/NotoSansCJK-LICENSE.txt) |
| MP4 系列容器解析和封装 | [SharpMP4 源码](https://github.com/jimm98y/SharpMP4) · [NuGet](https://www.nuget.org/packages/SharpMP4) |
| 视频背景解码、媒体信息探测及导出编码 | [FFmpeg](https://ffmpeg.org/download.html)（`ffmpeg`、`ffprobe`） |
| 谱面 JSON 读写和归档校验 | [Newtonsoft.Json](https://www.nuget.org/packages/Newtonsoft.Json)、[System.IO.Hashing](https://www.nuget.org/packages/System.IO.Hashing) |
| GLSL 语法解析 | [Antlr4.Runtime.Standard](https://www.nuget.org/packages/Antlr4.Runtime.Standard) |
| 构建时提取游戏资源和转换着色器 | [AssetRipperPatcher](https://github.com/StArraySharp/AssetRipperPatcher)、[HLSLcc](https://github.com/StArraySharp/HLSLcc) |
| 滤镜绑定的源码生成 | [Microsoft.CodeAnalysis.CSharp](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp)、[AsmResolver.DotNet](https://www.nuget.org/packages/AsmResolver.DotNet)、[ICSharpCode.Decompiler](https://www.nuget.org/packages/ICSharpCode.Decompiler) |
| 测试、分析器及覆盖率 | [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk)、[NUnit](https://www.nuget.org/packages/NUnit)、[NUnit3TestAdapter](https://www.nuget.org/packages/NUnit3TestAdapter)、[NUnit.Analyzers](https://www.nuget.org/packages/NUnit.Analyzers)、[coverlet.collector](https://www.nuget.org/packages/coverlet.collector) |
