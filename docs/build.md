# 构建 SharpFAI.Editor

## 环境

- Windows x64 和支持 OpenGL 4.1 的显卡驱动。
- Git 与能构建 `net10.0` 项目的 .NET SDK，例如 .NET 10 SDK。
- 准备原版滤镜资源时，需要本机安装的《冰与火之舞》（ADOFAI）和游戏的 `*_Data` 目录。
- 导出 MP4 和播放背景视频需要 `ffmpeg`。SharpMP4 随程序构建，负责 MP4 系列容器的解析和导出封装。`ffprobe` 用于其他容器，以及 SharpMP4 无法读取的文件。可将 FFmpeg 工具放在程序旁边或加入 `PATH`。

## 1. 获取源码

```powershell
git clone --recurse-submodules https://github.com/adofaiex/SharpFAI.Editor.git
cd SharpFAI.Editor
```

如果已经克隆，可在仓库根目录执行 `git submodule update --init --recursive` 补齐子模块。

## 2. 准备原版滤镜资源

构建基础编辑器和播放器可跳过此步。要启用从 ADOFAI 导入的滤镜和教程背景平铺贴图，在仓库根目录运行下列命令，选择游戏安装目录或它的 `*_Data` 目录。

```powershell
dotnet run --project .\build\PrepareGameAssets -c Release
```

命令行或自动化构建可直接传路径。

```powershell
dotnet run --project .\build\PrepareGameAssets -c Release -- --game-dir "D:\Games\A Dance of Fire and Ice"
```

工具会复用已缓存的 AssetRipperPatcher 和 HLSLcc。缺少所需版本时，它会从 GitHub 下载 Windows x64 发布包并校验 SHA-256。若只想下载工具或检查正式版本更新，可运行下列命令。

```powershell
dotnet run --project .\build\PrepareGameAssets -c Release -- --download-tools
```

控制台会显示各阶段的进度。游戏资源准备结束后，`GameFiles/Prepared.current` 指向当前资源包，后续构建会自动使用它。只要工具和游戏文件的内容相同，重复准备会复用已有结果。旧版手动配置仍可参考 [GameFiles 说明](../GameFiles/README.md)。

编辑器的「位置轨道」连接线也使用准备好的原版资源。新版工具会导入 `ADOFAI/ScrollingSprite` 和编辑器使用的 `dotted-line.png`。基础构建使用内置虚线。

可以把当前已准备的资源导出成 ZIP，再在另一份源码工作区离线导入。导入会校验并解压资源，然后更新 `GameFiles/Prepared.current`，无需安装游戏或下载提取工具。

```powershell
dotnet run --project .\build\PrepareGameAssets -c Release -- --export-zip ".\adofai-assets.zip"
dotnet run --project .\build\PrepareGameAssets -c Release -- --import-zip "D:\Archives\adofai-assets.zip"
```

省略路径会打开文件选择窗口。ZIP 包含构建用的纹理、GLSL、资源元数据和游戏程序集。

## 3. 编译与运行

```powershell
dotnet build .\SharpFAI.Editor.slnx -c Release
dotnet run --project .\SharpFAI.Editor -c Release --no-build
```

同一个编辑器程序可以用 `--player` 启动播放器模式。

```powershell
dotnet run --project .\SharpFAI.Editor -c Release --no-build -- --player --open "D:\Charts\example.adofai"
```

Windows 发布包可在可执行文件名后加 `--player` 启动播放器。`--player --help` 会列出截图、视频导出和参数覆盖选项。

## 4. 测试和打包

```powershell
dotnet test .\SharpFAI.Editor.slnx -c Release
```

需要桌面 OpenGL 的集成测试可单独选择运行。涉及 MP4 编码的测试还需要 FFmpeg。

在仓库根目录用 PowerShell 7 生成自包含单文件。默认打包 Windows、Linux 和 macOS。

| 文件 | 说明 |
| --- | --- |
| `publish/SharpFAI.Editor-win-x64-<版本>.exe` | Windows x64 |
| `publish/SharpFAI.Editor-linux-x64-<版本>` | Linux x64 |
| `publish/SharpFAI.Editor-osx-x64-<版本>` | macOS x64 |
| `publish/SharpFAI.Editor-osx-arm64-<版本>` | macOS arm64 |
| `publish/Mods/SharpFAI.Mcp.sharpfaimod` | MCP Mod |
| `publish/Mods/SharpFAI.WebMirror.sharpfaimod` | Web 镜像 Mod |
| `publish/SharpFAI.Editor-MultiPlatforms-<版本>.zip` | 所选平台的可执行文件、字体许可证和 `Mods/` 下的两个 Mod |

```powershell
pwsh -NoProfile -File .\publish.ps1
```

用 `-Runtime` 打包指定平台，可选 `win-x64`、`linux-x64`、`osx-x64`、`osx-arm64`。

```powershell
pwsh -NoProfile -File .\publish.ps1 -Runtime linux-x64,osx-arm64
```

维护者可使用本地 `release.ps1` 发布。先提交并推送对应版本的源码。脚本从 Git 凭据助手读取 GitHub 登录凭据，从本地脚本配置读取 ATL 登录信息。它调用一次 `publish.ps1`，将各平台可执行文件和两个 Mod 包上传到 GitHub Releases，将 MultiPlatforms ZIP 上传到 ATL。需要预览操作时加 `-WhatIf`。

```powershell
pwsh -NoProfile -File .\release.ps1 -Tag stable -Changelog "中文更新说明"
```
