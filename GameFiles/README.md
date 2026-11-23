# ADOFAI 本地构建输入

从同一版本的 ADOFAI 安装目录下的 `*_Data\Managed` 文件夹，将 DLL 放到本目录。请直接放在 `GameFiles` 下，文件包括：

- `Assembly-CSharp.dll`
- `Assembly-CSharp-firstpass.dll`
- 解析程序集引用所需的依赖 DLL。可将同一个 `Managed` 文件夹中的 DLL 一起复制过来。

在仓库根目录构建 Player，或执行发布脚本。构建会自动读取这里的 DLL 并生成滤镜控制代码。

```powershell
dotnet build .\SharpFAI.Player\SharpFAI.Player.csproj
```

如果放入了两份主 DLL 中的任意一份，构建会优先从这里读取，并要求两份都齐全。两份都未放入时，可通过本地文件 `SharpFAI.Player\Adofai.props.user` 配置 `AdofaiGameDir`。着色器和纹理可通过 `AdofaiFilterAssetsDir`、`AdofaiUnityAssetsDir` 和 `AdofaiFilterTexturesDir` 配置。

此目录仅跟踪本说明。请使用有权使用的本地游戏文件。

## 下载资源处理工具

在仓库根目录用 PowerShell 7 运行：

```powershell
pwsh -NoProfile -File .\Download-GameTools.ps1
```

脚本获取 HLSLcc 和 AssetRipperPatcher 的最新正式 Windows x64 发布包，校验 GitHub 提供的 SHA-256 后解压到 `tools\<工具名>\<版本>`。同版本已解压时会跳过下载。工具目录不纳入 Git；脚本不会运行工具，也不会导入游戏资源。
