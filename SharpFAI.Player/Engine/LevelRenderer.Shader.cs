namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{

// 着色器热重载。
// Shader hot reload.

    /// <summary>
    /// Set up shader hot reload: when Resources/Shaders exists next to the executable, watch it and
    /// recompile on change so shaders can be tuned without restarting the editor.
    /// </summary>
    private void InitializeShaderHotReload()
    {
        if (_shaderWatcher is not null)
        {
            return;
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "Resources", "Shaders");

        if (!Directory.Exists(directory))
        {
            _shaderStatus = "着色器: 内嵌（未找到磁盘目录，热重载不可用）";
            return;
        }

        ShaderLibrary.DiskDirectory = directory;
        ShaderLibrary.InvalidateCache();

        _shaderWatcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };

        _shaderWatcher.Changed += OnShaderFileChanged;
        _shaderWatcher.Created += OnShaderFileChanged;
        _shaderWatcher.Renamed += OnShaderFileChanged;
        _shaderWatcher.EnableRaisingEvents = true;

        _shaderStatus = "着色器: 热重载已开启";
    }

    private void OnShaderFileChanged(object sender, FileSystemEventArgs e)
    {
        string extension = Path.GetExtension(e.FullPath);

        if (!extension.Equals(".vert", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".frag", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The event can fire repeatedly; let the next frame coalesce them
        _shaderReloadPending = true;
    }

    /// <summary>Perform one shader recompile.</summary>
    private void ApplyShaderReload()
    {
        try
        {
            ShaderLibrary.InvalidateCache();
            _officialRenderer?.ReloadShader();
            if (_spriteRenderer is not null && !_spriteRenderer.ReloadShaders(out string? error))
            {
                throw new InvalidOperationException(error);
            }

            _shaderStatus = "着色器: 已重载";
        }
        catch (Exception ex)
        {
            _shaderStatus = $"着色器重载失败: {ex.Message}";
        }
    }
}
