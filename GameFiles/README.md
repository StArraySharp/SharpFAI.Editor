# ADOFAI 本地构建输入

## 从游戏安装目录准备资源

在 Windows 上用 .NET 10 SDK 运行 C# 项目。直接运行会打开目录选择器。自动化可通过 `--game-dir` 传入安装目录或 `*_Data` 目录。

```console
dotnet run --project build/PrepareGameAssets -c Release
# 或指定目录
dotnet run --project build/PrepareGameAssets -c Release -- --game-dir "E:\SteamLibrary\steamapps\common\A Dance of Fire and Ice"
```

工具会复用 `tools` 缓存，缺少所需版本时从 GitHub 下载 Windows x64 发布包并验证 SHA-256。AssetRipperPatcher 1.0.3 或更新版本从安装目录导出纹理、D3D11 字节码和用于解析属性的 ShaderLab。准备工具在运行时加载 AssetRipper 库来读取 Unity 的 pass 表，再用 HLSLcc 转译配对后的着色器阶段到 GLSL 330，并移除未使用的常量成员。ShaderLab 属性和纹理导入参数供构建时生成默认值。

准备成功后，资源保存在 `GameFiles/Prepared` 下，`Prepared.current` 指向当前结果。重复执行会复用匹配的结果。普通构建会离线复制纹理和 GLSL 到输出目录，同时将它们嵌入播放器 DLL。游戏程序集只供代码生成使用。源 DXBC、ShaderLab、`.meta` 以及以 `_0` 结尾的资源文件都排除在运行时产物之外。内容相同的 `_0` 纹理使用别名。

```powershell
dotnet build .\SharpFAI.Player\SharpFAI.Player.csproj
```

## 手动提供旧版构建输入

从同一版本的 ADOFAI 安装目录下的 `*_Data\Managed` 文件夹，将 DLL 放到本目录。请直接放在 `GameFiles` 下，文件包括：

- `Assembly-CSharp.dll`
- `Assembly-CSharp-firstpass.dll`
- 解析程序集引用所需的依赖 DLL。可将同一个 `Managed` 文件夹中的 DLL 一起复制过来。

在仓库根目录构建 Player，或执行发布脚本。构建会自动读取这里的 DLL 并生成滤镜控制代码。

```powershell
dotnet build .\SharpFAI.Player\SharpFAI.Player.csproj
```

没有 `Prepared.current` 时，如果放入了两份主 DLL 中的任意一份，构建会从这里读取，并要求两份都齐全。两份都未放入时，可通过本地文件 `SharpFAI.Player\Adofai.props.user` 配置 `AdofaiGameDir`。着色器和纹理可通过 `AdofaiFilterAssetsDir`、`AdofaiUnityAssetsDir` 和 `AdofaiFilterTexturesDir` 配置。

此目录仅跟踪本说明。请使用有权使用的本地游戏文件。

单独下载或更新工具可使用 `PrepareGameAssets --download-tools`。命令和完整的构建、测试及打包步骤见 [构建指南](../docs/build.md)。
