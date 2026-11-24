# 构建 SharpFAI.Editor

## 环境

- Windows x64 和支持 OpenGL 4.1 的显卡驱动。
- Git 与能构建 `net10.0` 项目的 .NET SDK，例如 .NET 10 SDK。
- 准备原版滤镜资源时，需要本机安装的《冰与火之舞》（ADOFAI）和游戏的 `*_Data` 目录。
- 导出 MP4 时需要 FFmpeg。将含有 `ffmpeg.exe` 的目录加入 `PATH`，重新打开终端后可用 `ffmpeg -version` 检查。

## 1. 获取源码

```powershell
git clone --recurse-submodules https://github.com/StArraySharp/SharpFAI.Editor.git
cd SharpFAI.Editor
```

如果已经克隆，可在仓库根目录执行 `git submodule update --init --recursive` 补齐 SharpFAI 和 Thrust。

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

## 3. 编译与运行

```powershell
dotnet build .\SharpFAI.Editor.slnx -c Release
dotnet run --project .\SharpFAI.Editor -c Release --no-build
```

也可直接用独立播放器打开谱面。

```powershell
dotnet run --project .\SharpFAI.Player -c Release --no-build -- --open "D:\Charts\example.adofai"
```

## 4. 测试和打包

```powershell
dotnet test .\SharpFAI.Editor.slnx -c Release
```

需要桌面 OpenGL 的集成测试可单独选择运行。涉及 MP4 编码的测试还需要 FFmpeg。

在仓库根目录用 PowerShell 7 生成自包含的 Windows x64 单文件，输出为 `publish/SharpFAI.Editor.exe` 和 `publish/PrepareGameAssets.exe`。

```powershell
pwsh -NoProfile -File .\publish.ps1
```

已生成资源准备工具时，可在源码工作区直接运行 `.\publish\PrepareGameAssets.exe`，或传入 `--game-dir` 指定游戏安装目录。工具将资源写入工作区的 `GameFiles`，供下一次构建使用。
