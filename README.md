# SharpFAI.Editor

SharpFAI.Editor 是《冰与火之舞》（A Dance of Fire and Ice，ADOFAI）的非官方谱面编辑器。仓库同时提供独立的 SharpFAI Player，用于播放谱面和离线渲染视频。

## 功能

- 编辑轨道、事件和装饰，支持打开 `.adofai` 文件和导入 `.zip` 谱面包。
- 在编辑器内预览或播放谱面，也可以用独立播放器查看效果。
- 按指定分辨率和帧率导出视频。导出 MP4 需要 FFmpeg，缺少 FFmpeg 时可输出 PNG 帧序列。

## 从源码运行

使用 Windows x64、支持 OpenGL 4.1 的显卡驱动，以及 Git 和 .NET 10 SDK。克隆时一并获取 SharpFAI 和 Thrust 子模块。

```powershell
git clone --recurse-submodules https://github.com/StArraySharp/SharpFAI.Editor.git
cd SharpFAI.Editor
dotnet build .\SharpFAI.Editor.slnx -c Release
dotnet run --project .\SharpFAI.Editor -c Release
```

启动后从「文件 → 打开」选择谱面。也可以从命令行用独立播放器打开谱面。

```powershell
dotnet run --project .\SharpFAI.Player -c Release -- --open ".\example.adofai"
```

原版滤镜使用本机 ADOFAI 安装中的构建输入。完整步骤见 [构建指南](docs/build.md)。

## 打包与测试

在仓库根目录通过 PowerShell 7 运行发布脚本，可在 `publish/` 生成自包含的 Windows x64 编辑器和资源准备工具。 [publish.ps1](publish.ps1)

```powershell
pwsh -NoProfile -File .\publish.ps1
dotnet test .\SharpFAI.Editor.slnx -c Release
```

桌面 GPU 集成测试单独运行，需要 OpenGL 环境。涉及视频编码的测试还需要 FFmpeg。
