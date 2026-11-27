# SharpFAI Web 镜像 Mod

Web 镜像在浏览器中显示编辑器窗口，支持鼠标、滚轮、快捷键和文本输入。

1. 解压 MultiPlatforms 发布包，或在「设置 → Mod 管理」安装发布附件 `SharpFAI.WebMirror.sharpfaimod`。也可将解压后的 Mod 文件夹复制到编辑器可执行文件旁的 `Mods`。从源码可用 `dotnet build mods/SharpFAI.WebMirror -c Release` 单独构建。
2. 在「设置 → Mod 管理」启用 Mod，打开「工具 → Web 镜像」，点击「在浏览器中打开」。该入口接替了原有的 `--web-ui` 启动参数。

镜像只监听本机 `127.0.0.1`，同一时间允许一个浏览器连接。打开时请使用完整链接，末尾的片段用于验证连接。停止、停用或热重载会关闭正在使用的连接。重启服务或热重载后，从菜单打开新的链接。

在「设置 → Mod 管理 → SharpFAI Web Mirror → 设置」可调整自动启动和最高帧率。最高帧率应用后立即生效，自动启动用于下一次启用或热重载。保存文件名为 `SharpFAI.WebMirror.Settings.json`。
