using OpenTK.Graphics.OpenGL4;
using SharpFAI.Player.Engine.ShaderGen;
using System;
using System.Collections.Generic;
using System.IO;

namespace SharpFAI.Player.Engine;

/// <summary>
/// 把游戏自带的 hlslcc GLSL 批量转换成引擎用的 <c>.vert</c>/<c>.frag</c>，并用真实 GL 上下文
/// 编译验证。调用前需要当前线程已经 MakeCurrent 一个 GL 上下文。
/// Converts the game's hlslcc GLSL into the engine's <c>.vert</c>/<c>.frag</c> files and verifies each
/// pair with a real GL compile. Requires a current GL context on the calling thread.
/// </summary>
public static class ShaderConverter
{
    /// <summary>转换游戏目录里的全部 shader 并校验编译。</summary>
    /// <param name="shaderRoot">游戏 <c>Assets/Shader</c> 目录。</param>
    /// <param name="outputDirectory">输出目录。</param>
    public static ConversionReport ConvertGameShaders(string shaderRoot, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!Directory.Exists(shaderRoot))
        {
            throw new DirectoryNotFoundException($"找不到 shader 目录 / Shader root not found: {shaderRoot}");
        }

        Directory.CreateDirectory(outputDirectory);

        string[] files = Directory.GetFiles(shaderRoot, "*.glsl", SearchOption.AllDirectories);
        List<ConversionEntry> entries = new(files.Length);

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(shaderRoot, file);
            string shaderName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "shader";
            string stageName = Path.GetFileNameWithoutExtension(
                file.Replace(".orig", string.Empty, StringComparison.Ordinal));

            try
            {
                UnityShaderAdapter.AdapterResult result = ConvertFile(
                    file,
                    Path.Combine(outputDirectory, shaderName),
                    stageName);

                (bool ok, string? error) = VerifyCompile(result.VertexSource, result.FragmentSource);

                entries.Add(new ConversionEntry(relative, ok, error, result.Warnings.Count));
            }
            catch (Exception ex)
            {
                entries.Add(new ConversionEntry(relative, false, $"{ex.GetType().Name}: {ex.Message}", 0));
            }
        }

        return new ConversionReport(entries);
    }

    /// <summary>转换一个 glsl 文件并写出两个阶段。</summary>
    /// <param name="glslPath">hlslcc 输出的单文件（<c>N_glsl.orig.glsl</c>）。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="baseName">输出文件名（不含扩展名）。</param>
    /// <param name="perInstanceUniforms">要改成逐顶点属性的 uniform。</param>
    public static UnityShaderAdapter.AdapterResult ConvertFile(
        string glslPath,
        string outputDirectory,
        string baseName,
        params string[] perInstanceUniforms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(glslPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        string source = File.ReadAllText(glslPath);

        UnityShaderAdapter.AdapterResult result =
            UnityShaderAdapter.Convert(source, perInstanceUniforms);

        Directory.CreateDirectory(outputDirectory);

        File.WriteAllText(Path.Combine(outputDirectory, baseName + ".vert"), result.VertexSource);
        File.WriteAllText(Path.Combine(outputDirectory, baseName + ".frag"), result.FragmentSource);

        return result;
    }

    /// <summary>
    /// 编译并校验输出目录里的每一对 <c>.vert</c>/<c>.frag</c>，用于确认转换出来的官方着色器
    /// 能被引擎真正编译。
    /// Compiles and verifies every <c>.vert</c>/<c>.frag</c> pair under a directory, so a converted
    /// game shader can be proven to build with the engine.
    /// </summary>
    /// <param name="shaderDirectory">含 <c>.vert</c>/<c>.frag</c> 的目录。</param>
    public static ConversionReport VerifyDirectory(string shaderDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderDirectory);

        if (!Directory.Exists(shaderDirectory))
        {
            throw new DirectoryNotFoundException($"找不到着色器目录 / Shader directory not found: {shaderDirectory}");
        }

        string[] vertices = Directory.GetFiles(shaderDirectory, "*.vert", SearchOption.AllDirectories);
        List<ConversionEntry> entries = new(vertices.Length);

        foreach (string vertexPath in vertices)
        {
            string fragmentPath = Path.ChangeExtension(vertexPath, ".frag");
            string relative = Path.GetRelativePath(shaderDirectory, vertexPath);

            if (!File.Exists(fragmentPath))
            {
                entries.Add(new ConversionEntry(relative, false, "missing fragment stage", 0));
                continue;
            }

            (bool ok, string? error) = VerifyCompile(
                File.ReadAllText(vertexPath),
                File.ReadAllText(fragmentPath));

            entries.Add(new ConversionEntry(relative, ok, error, 0));
        }

        return new ConversionReport(entries);
    }

    /// <summary>编译并链接一对阶段，返回是否成功及错误日志。</summary>
    public static (bool Ok, string? Error) VerifyCompile(string vertexSource, string fragmentSource)
    {
        int vertex = 0;        int fragment = 0;
        int program = 0;

        try
        {
            (vertex, string? vertexError) = CompileStage(ShaderType.VertexShader, vertexSource);

            if (vertexError is not null)
            {
                return (false, "vertex: " + vertexError);
            }

            (fragment, string? fragmentError) = CompileStage(ShaderType.FragmentShader, fragmentSource);

            if (fragmentError is not null)
            {
                return (false, "fragment: " + fragmentError);
            }

            program = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);

            return status != 0 ? (true, null) : (false, "link: " + GL.GetProgramInfoLog(program));
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (program != 0)
            {
                GL.DeleteProgram(program);
            }

            if (vertex != 0)
            {
                GL.DeleteShader(vertex);
            }

            if (fragment != 0)
            {
                GL.DeleteShader(fragment);
            }
        }
    }

    private static (int Handle, string? Error) CompileStage(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);

        // 长度必须是 UTF-8 字节数：OpenTK 把字符串按 UTF-8 传给驱动，单参数重载却传字符数，
        // 含中文注释的着色器会被截断成 "unexpected $end"。
        // The length must be the UTF-8 byte count: OpenTK marshals the string as UTF-8 while the
        // single-argument overload passes the character count, truncating shaders with non-ASCII
        // comments into "unexpected $end".
        GL.ShaderSource(shader, 1, [source], [System.Text.Encoding.UTF8.GetByteCount(source)]);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);

        if (status != 0)
        {
            return (shader, null);
        }

        string log = GL.GetShaderInfoLog(shader);
        GL.DeleteShader(shader);
        return (0, log);
    }
}

/// <summary>单个 shader 的转换结果。</summary>
public readonly record struct ConversionEntry(string File, bool Compiled, string? Error, int Warnings);

/// <summary>批量转换报告。</summary>
public sealed class ConversionReport(IReadOnlyList<ConversionEntry> entries)
{
    /// <summary>每个 shader 的结果。</summary>
    public IReadOnlyList<ConversionEntry> Entries { get; } = entries;

    /// <summary>成功编译的数量。</summary>
    public int SuccessCount
    {
        get
        {
            int count = 0;

            foreach (ConversionEntry entry in Entries)
            {
                if (entry.Compiled)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>失败数量。</summary>
    public int FailureCount => Entries.Count - SuccessCount;
}
