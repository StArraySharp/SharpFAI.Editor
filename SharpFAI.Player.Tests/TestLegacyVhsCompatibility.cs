using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine.ShaderGen;
using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

public sealed partial class TestLegacyVhsCompatibility
{
    private const int Width = 382, Height = 576, Channels = Width * Height * 4;
    private sealed record Case(float Seconds, float Tracking, bool NativeCoordinates = false, int Diagnostic = 0);
    private sealed record Frame(float[] Float, byte[] Unorm);
    private sealed record Metrics(double Mae, double Rmse, double Max, int X, int Y, int Channel, int NonFinite, int DifferentPixels);

    [Test, Explicit("Requires original game inputs, ShaderRipper, desktop GL and installed MSVC/Windows SDK; executes original Windows DXBC and independent equations on D3D11.")]
    [NonParallelizable, Apartment(ApartmentState.STA)]
    public void FixedSourceTexturesAndClockCharacterizeLegacy004() => RunControlledComparison(null);

    private static void RunControlledComparison(string? hlslccPath)
    {
        string? game = Environment.GetEnvironmentVariable("SHARPFAI_ADOFAI_GAME");
        string? ripper = Environment.GetEnvironmentVariable("SHARPFAI_SHADER_RIPPER");
        if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(ripper))
        {
            Assert.Ignore("Set SHARPFAI_ADOFAI_GAME and SHARPFAI_SHADER_RIPPER to run the original-game comparison.");
            return;
        }
        string root = FindRoot();
        string scratch = Path.Combine(root, "SharpFAI.Player.Tests", "obj", hlslccPath is null ? "legacy-vhs-controlled" : "legacy-vhs-hlslcc");
        Directory.CreateDirectory(scratch);
        string[] inputs = File.ReadAllLines(Path.Combine(root, "SharpFAI.Player", "obj", "Debug", "net10.0", "AdofaiFilters.inputs"));
        string Resolve(string resource) => inputs.Single(l => l.Split('|')[1] == resource).Split('|')[2];
        string glslPath = Resolve("Resources.Shaders.Filters/CameraFilterPackLegacy/Real_VHS.glsl");
        string[] atlasPaths = { Resolve("Resources.Filters.CameraFilterPack_VHS1.png"), Resolve("Resources.Filters.CameraFilterPack_VHS2.png") };
        string referencePath = Path.Combine(root, "SharpFAI.Player.Tests", "Shaders", "LegacyVhsReference.hlsl");
        var cases = (from seconds in new[] { 0f, 1.55f, 12.375f }
                     from tracking in new[] { 0f, 0.212f, -0.212f }
                     from native in new[] { false, true }
                     select new Case(seconds, tracking, native)).ToList();
        foreach (float seconds in new[] { 0f, 1.55f, 12.375f }) cases.Add(new Case(seconds, 0, Diagnostic: 1));
        cases.Add(new Case(0, 0, Diagnostic: 2));
        cases.Add(new Case(0, 0, true, 2));
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        int[] textures = new int[3];
        var programs = new List<int>();
        int vao = 0, fbo = 0, target = 0;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new Vector2i(Width, Height), StartVisible = false, StartFocused = false,
                APIVersion = new Version(3, 3), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            try
            {
                vao = GL.GenVertexArray(); GL.BindVertexArray(vao);
                GL.Disable(EnableCap.Dither); GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.FramebufferSrgb);
                byte[] source = new byte[Channels];
                for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int i = (y * Width + x) * 4;
                    source[i] = (byte)(x * 251 / (Width - 1));
                    source[i + 1] = (byte)(y * 239 / (Height - 1));
                    source[i + 2] = (byte)((x / 17 * 37 + y / 29 * 71) % 256);
                    source[i + 3] = 255;
                    if (x < 71 && y > 431) { source[i] = 255; source[i + 1] = 7; source[i + 2] = 103; }
                }
                textures[0] = ImageDecoder.Upload(source, Width, Height, generateMipmaps: false, repeat: false);
                GL.BindTexture(TextureTarget.Texture2D, textures[0]);
                // Unity's 382x576 temporary RT is trilinear, but has only level zero.
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
                for (int i = 0; i < 2; i++)
                {
                    string meta = File.ReadAllText(atlasPaths[i] + ".meta");
                    Assert.That(meta, Does.Contain("enableMipMap: 1"));
                    Assert.That(meta, Does.Contain("filterMode: 1"));
                    Assert.That(meta, Does.Contain("wrapU: 0"));
                    Assert.That(meta, Does.Contain("wrapV: 0"));
                    var image = ImageDecoder.DecodeFile(atlasPaths[i]);
                    ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
                    textures[i + 1] = ImageDecoder.Upload(image);
                    GL.BindTexture(TextureTarget.Texture2D, textures[i + 1]);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapNearest);
                }
                string inputPath = Path.Combine(scratch, "input.bin"), outputPath = Path.Combine(scratch, "output.bin");
                using (var writer = new BinaryWriter(File.Create(inputPath)))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        GL.BindTexture(TextureTarget.Texture2D, textures[i]);
                        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int w);
                        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int h);
                        int levels = i == 0 ? 1 : 1 + (int)Math.Floor(Math.Log2(Math.Max(w, h)));
                        writer.Write(w); writer.Write(h); writer.Write(levels);
                        for (int mip = 0; mip < levels; mip++)
                        {
                            byte[] pixels = new byte[Math.Max(1, w >> mip) * Math.Max(1, h >> mip) * 4];
                            GL.GetTexImage(TextureTarget.Texture2D, mip, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                            writer.Write(pixels);
                        }
                    }
                    writer.Write(cases.Count);
                    foreach (var c in cases) { writer.Write(c.Tracking); writer.Write(c.Seconds); writer.Write(c.NativeCoordinates ? 1f : 0f); writer.Write((float)c.Diagnostic); }
                }
                string nativeOutput = RunD3d(root, scratch, referencePath, inputPath, outputPath);
                string dxbcPath = Path.Combine(scratch, "original.dxbc"), originalOutputPath = Path.Combine(scratch, "original-output.bin");
                Run("pwsh", $"-NoProfile -File \"{Path.Combine(root, "SharpFAI.Player.Tests", "Native", "ExtractLegacyVhsDxbc.ps1")}\" -ShaderRipperDirectory \"{ripper}\" -ResourcesAsset \"{Path.Combine(game, "A Dance of Fire and Ice_Data", "resources.assets")}\" -OutputPath \"{dxbcPath}\"", root);
                Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dxbcPath))),
                    Is.EqualTo("33CF78551E79600EEB9DF378EECF02095F30C9E7019FDFBF4FAF6C9FA57C4C57"), "Use the audited Windows Legacy004 bytecode build.");
                string originalNativeOutput = Run(Path.Combine(scratch, "runner.exe"), $"\"{dxbcPath}\" \"{inputPath}\" \"{originalOutputPath}\"", root);
                string Instructions(string path) => string.Join('\n', File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)));
                string auditedAsmPath = Path.Combine(root, "tmp-compare", "legacy004-dxbc-20261121", "Assets", "Resources", "CameraFilterPackLegacy_Real_VHS", "3_ps.orig.asm");
                string executedAsm = Instructions(dxbcPath + ".asm");
                if (hlslccPath is null)
                {
                    string auditedAsm = Instructions(auditedAsmPath);
                    const string number = @"-?\d+\.\d+";
                    Assert.That(System.Text.RegularExpressions.Regex.Replace(executedAsm, number, "#"),
                        Is.EqualTo(System.Text.RegularExpressions.Regex.Replace(auditedAsm, number, "#")), "Original instruction topology must match the audited DXBC.");
                    double[] Literals(string text) => System.Text.RegularExpressions.Regex.Matches(text, number)
                        .Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    double[] executedLiterals = Literals(executedAsm), auditedLiterals = Literals(auditedAsm);
                    Assert.That(executedLiterals, Has.Length.EqualTo(auditedLiterals.Length));
                    Assert.That(executedLiterals.Zip(auditedLiterals, (a, b) => Math.Abs(a - b)).Max(), Is.LessThan(0.0000011),
                        "Disassemblers can round exact halfway literals such as 0.0078125 differently at six decimals.");
                }
                using var hlslcc = hlslccPath is null ? null : new HlslccExperiment();
                hlslcc?.Initialize(hlslccPath!, root, dxbcPath, executedAsm);
                string original = UnityShaderAdapter.ImportFullscreenFragment(File.ReadAllText(glslPath));
                string reference = ToGlsl(File.ReadAllText(referencePath));
                int exported = Program(original), equations = Program(reference);
                programs.Add(exported); programs.Add(equations);
                int main = original.IndexOf("void main()", StringComparison.Ordinal);
                int flipped = Program(original[..main] + original[main..].Replace("vs_TEXCOORD0",
                    "(vec2(vs_TEXCOORD0.x,1.0-vs_TEXCOORD0.y))", StringComparison.Ordinal));
                programs.Add(flipped);
                fbo = GL.GenFramebuffer(); target = GL.GenTexture();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                GL.Viewport(0, 0, Width, Height);
                Frame Render(int program, Case c, int misboundTexture = -1)
                {
                    GL.UseProgram(program);
                    bool translated = hlslcc is not null && program == hlslcc.LinkedProgram;
                    for (int i = 0; i < 3; i++)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, textures[i]);
                        if (!translated) GL.Uniform1(GL.GetUniformLocation(program, new[] { "_MainTex", "VHS", "VHS2" }[i]), i);
                    }
                    if (translated) hlslcc!.Bind(c, misboundTexture);
                    else
                    {
                        GL.Uniform4(GL.GetUniformLocation(program, "_Time"), c.Seconds / 20, c.Seconds, c.Seconds * 2, c.Seconds * 3);
                        GL.Uniform1(GL.GetUniformLocation(program, "TRACKING"), c.Tracking);
                        GL.Uniform1(GL.GetUniformLocation(program, "CONTRAST"), 0f);
                        GL.Uniform1(GL.GetUniformLocation(program, "seconds"), c.Seconds);
                        GL.Uniform1(GL.GetUniformLocation(program, "tracking"), c.Tracking);
                        GL.Uniform1(GL.GetUniformLocation(program, "diagnostic"), (float)c.Diagnostic);
                    }
                    float[] fp = new float[Channels]; byte[] bytes = new byte[Channels];
                    for (int kind = 0; kind < 2; kind++)
                    {
                        GL.ActiveTexture(TextureUnit.Texture3); GL.BindTexture(TextureTarget.Texture2D, target);
                        GL.TexImage2D(TextureTarget.Texture2D, 0, kind == 0 ? PixelInternalFormat.Rgba32f : PixelInternalFormat.Rgba8, Width, Height, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
                        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target, 0);
                        Assert.That(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer), Is.EqualTo(FramebufferErrorCode.FramebufferComplete));
                        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                        if (kind == 0) GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.Float, fp);
                        else GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, bytes);
                    }
                    return new Frame(fp, bytes);
                }
                var rows = new List<object>();
                using var reader = new BinaryReader(File.OpenRead(outputPath));
                using var originalReader = new BinaryReader(File.OpenRead(originalOutputPath));
                Frame ReadFrame(BinaryReader input)
                {
                    float[] fp = new float[Channels]; for (int i = 0; i < fp.Length; i++) fp[i] = input.ReadSingle();
                    byte[] bytes = input.ReadBytes(Channels); Assert.That(bytes, Has.Length.EqualTo(Channels));
                    return new Frame(fp, bytes);
                }
                var baseline = new List<double>(); var timeControls = new List<double>(); var coordinateControls = new List<double>();
                var translatedRows = new List<object>();
                var translatedControlRows = new List<double[]>();
                string renderer = GL.GetString(StringName.Renderer);
                if (hlslcc is not null)
                {
                    Assert.That(originalNativeOutput, Does.Not.Contain("D3D11 WARP"));
                    Assert.That(renderer, Does.Not.Match("(?i)llvmpipe|softpipe|swiftshader|gdi generic|software"));
                }
                foreach (var c in cases)
                {
                    Frame d3d = ReadFrame(reader), windows = ReadFrame(originalReader);
                    Frame glReference = Render(equations, c);
                    Frame gl = c.Diagnostic == 0 ? Render(exported, c) : glReference;
                    var d3dMetrics = Compare(gl.Float, d3d.Float);
                    Metrics? originalD3dMetrics = c.Diagnostic == 0 ? Compare(gl.Float, windows.Float) : null;
                    Metrics? originalByteMetrics = c.Diagnostic == 0 ? Compare(gl.Unorm.Select(v => v / 255f).ToArray(), windows.Unorm.Select(v => v / 255f).ToArray()) : null;
                    Metrics? originalVsReference = c.Diagnostic == 0 ? Compare(windows.Float, d3d.Float) : null;
                    var equationMetrics = Compare(gl.Float, glReference.Float);
                    var byteMetrics = Compare(gl.Unorm.Select(v => v / 255f).ToArray(), d3d.Unorm.Select(v => v / 255f).ToArray());
                    var quantization = Compare(d3d.Float.Select(v => Math.Clamp(v, 0, 1)).ToArray(), d3d.Unorm.Select(v => v / 255f).ToArray());
                    Metrics? originalQuantization = c.Diagnostic == 0 ? Compare(windows.Float.Select(v => Math.Clamp(v, 0, 1)).ToArray(), windows.Unorm.Select(v => v / 255f).ToArray()) : null;
                    int? originalBelow = c.Diagnostic == 0 ? windows.Float.Count(v => v < 0) : null;
                    int? originalAbove = c.Diagnostic == 0 ? windows.Float.Count(v => v > 1) : null;
                    object? negative = null;
                    if (c.Diagnostic == 0 && !c.NativeCoordinates)
                    {
                        double timeMae = Compare(gl.Float, Render(exported, c with { Seconds = c.Seconds + 0.73f }).Float).Mae;
                        double coordinateMae = Compare(gl.Float, Render(flipped, c).Float).Mae;
                        baseline.Add(originalD3dMetrics!.Mae); timeControls.Add(timeMae); coordinateControls.Add(coordinateMae);
                        negative = new { timeMae, coordinateMae };
                        if (hlslcc is not null)
                        {
                            int program = hlslcc.LinkedProgram;
                            Frame translated = Render(program, c);
                            Metrics floatError = Compare(translated.Float, windows.Float);
                            Metrics byteError = Compare(translated.Unorm.Select(v => v / 255f).ToArray(), windows.Unorm.Select(v => v / 255f).ToArray());
                            double[] controls =
                            [
                                Compare(translated.Float, Render(program, c with { Seconds = c.Seconds + 0.73f }).Float).Mae,
                                Compare(translated.Float, Render(program, c with { Tracking = c.Tracking + 0.212f }).Float).Mae,
                                .. Enumerable.Range(0, 3).Select(slot => Compare(translated.Float, Render(program, c, slot).Float).Mae),
                            ];
                            translatedControlRows.Add(controls);
                            translatedRows.Add(new { c, floatError, byteError, linuxFloatError = originalD3dMetrics, linuxByteError = originalByteMetrics, controls });
                            Assert.That(floatError.NonFinite, Is.Zero);
                        }
                    }
                    int below = d3d.Float.Count(v => v < 0), above = d3d.Float.Count(v => v > 1);
                    rows.Add(new { c, originalD3dMetrics, originalByteMetrics, originalVsReference, originalQuantization, originalBelow, originalAbove, d3dMetrics, equationMetrics, byteMetrics, quantization, below, above, negative });
                    if (originalD3dMetrics is not null)
                    {
                        TestContext.Out.WriteLine($"ORIGINAL DXBC: MAE={originalD3dMetrics.Mae:F8}, max={originalD3dMetrics.Max:F8} at ({originalD3dMetrics.X},{originalD3dMetrics.Y},{originalD3dMetrics.Channel}), original/reference={originalVsReference!.Mae:F8}");
                        Assert.That(originalD3dMetrics.NonFinite, Is.Zero);
                        Assert.That(originalQuantization!.Max, Is.LessThanOrEqualTo(1.0 / 255 + 0.00001));
                    }
                    TestContext.Out.WriteLine($"{c}: GL/D3D MAE={d3dMetrics.Mae:F8} max={d3dMetrics.Max:F8} at ({d3dMetrics.X},{d3dMetrics.Y},{d3dMetrics.Channel}), GL/equations={equationMetrics.Mae:F8}, RGBA8 MAE={byteMetrics.Mae * 255:F5}, nonfinite={d3dMetrics.NonFinite}, clipped={below + above}");
                    Assert.That(d3dMetrics.NonFinite + equationMetrics.NonFinite, Is.Zero);
                    Assert.That(quantization.Max, Is.LessThanOrEqualTo(1.0 / 255 + 0.00001), "Allow one UNORM code for hardware conversion precision.");
                    if (c.Diagnostic == 0 && !c.NativeCoordinates)
                    {
                        Assert.That(originalD3dMetrics!.Mae, Is.LessThan(0.001), "Untouched Windows bytecode and original Linux shader must agree in aggregate at matching numerical UVs.");
                        Assert.That(d3dMetrics.Mae, Is.LessThan(0.001), "Matched numerical coordinates must agree in aggregate despite hash/floor discontinuities.");
                        Assert.That(equationMetrics.Mae, Is.LessThan(0.001), "Independent equations must agree with the untouched Legacy004 GLSL.");
                    }
                    if (c.Diagnostic == 2) Assert.That(d3dMetrics.Max, Is.LessThan(0.00002), "Identity sampling verifies source orientation and normalized sampling.");
                }
                Assert.That(reader.BaseStream.Position, Is.EqualTo(reader.BaseStream.Length));
                Assert.That(originalReader.BaseStream.Position, Is.EqualTo(originalReader.BaseStream.Length));
                Assert.That(timeControls.Average(), Is.GreaterThan(baseline.Average()), "Wrong clock must be detectable beyond cross-API numerical error.");
                Assert.That(coordinateControls.Average(), Is.GreaterThan(baseline.Average()), "UV negative control must expose coordinate errors.");
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                string report = JsonSerializer.Serialize(new
                {
                    capturedAtUtc = DateTimeOffset.UtcNow,
                    metricUnits = "Normalized RGBA, alpha always 1. RGB-only MAE = RGBA MAE * 4/3. X/Y positions are zero-based, bottom-up. DifferentPixels counts pixels with any channel error > 1/255.",
                    renderer = GL.GetString(StringName.Renderer), nativeOutput, originalNativeOutput,
                    originalDxbcHash = Hash(dxbcPath), originalDisassemblyMatchesAuditedAsm = true,
                    qualification = "Actual Windows DXBC extracted read-only from installed resources.assets and executed unchanged on D3D11. Also independently reconstructed equations on D3D11 and GL. Reference full precision constants from Linux export; ASM text truncates literals. Gamma/UNORM pipeline, no sRGB decode. Identical GL-generated mip bytes on both APIs. NativeCoordinates=true is a controlled top-down UV/texture convention, not a captured Unity camera contract.",
                    size = new[] { Width, Height }, sourceHash = Convert.ToHexString(SHA256.HashData(source)),
                    glsl = new { path = glslPath, hash = Hash(glslPath) },
                    textures = atlasPaths.Select(p => new { path = p, hash = Hash(p), metaHash = Hash(p + ".meta") }),
                    referenceHash = Hash(referencePath), baseline = baseline.Average(), timeNegativeControl = timeControls.Average(), coordinateNegativeControl = coordinateControls.Average(), rows,
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(scratch, "report.json"), report);
                TestContext.Out.WriteLine($"Report: {Path.Combine(scratch, "report.json")}");
                if (hlslcc is not null)
                {
                    string[] controlNames = ["wrongTime", "wrongTracking", "wrongT0", "wrongT1", "wrongT2"];
                    var controls = controlNames.Select((name, i) => (name, mae: translatedControlRows.Average(row => row[i])))
                        .ToDictionary(pair => pair.name, pair => pair.mae);
                    string hlslccReport = Path.Combine(scratch, "hlslcc-report.json");
                    File.WriteAllText(hlslccReport, JsonSerializer.Serialize(new
                    {
                        renderer, glVersion = GL.GetString(StringName.Version), originalNativeOutput,
                        originalDxbcHash = Hash(dxbcPath), originalLinuxGlslHash = Hash(glslPath),
                        sourceHash = Convert.ToHexString(SHA256.HashData(source)), hlslcc = hlslcc.Results,
                        controlNames, controls, rows = translatedRows,
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    TestContext.Out.WriteLine($"hlslcc report: {hlslccReport}");
                    Assert.That(translatedRows, Has.Count.EqualTo(9));
                    foreach (var control in controls)
                        Assert.That(control.Value, Is.GreaterThan(baseline.Average()), $"Binding negative control: {control.Key}");
                }
            }
            finally
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.UseProgram(0);
                foreach (int p in programs) GL.DeleteProgram(p);
                foreach (int t in textures) if (t != 0) GL.DeleteTexture(t);
                if (target != 0) GL.DeleteTexture(target);
                if (fbo != 0) GL.DeleteFramebuffer(fbo);
                if (vao != 0) GL.DeleteVertexArray(vao);
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
            foreach (string name in new[] { "input.bin", "output.bin", "original-output.bin", "original.dxbc", "original.dxbc.asm", "runner.exe", "runner.obj" })
                File.Delete(Path.Combine(scratch, name));
        }
    }

    private static Metrics Compare(float[] a, float[] b)
    {
        double sum = 0, squared = 0, max = 0; int worst = 0, nonFinite = 0, different = 0;
        for (int pixel = 0; pixel < a.Length / 4; pixel++)
        {
            bool changed = false;
            for (int ch = 0; ch < 4; ch++)
            {
                int i = pixel * 4 + ch;
                if (!float.IsFinite(a[i]) || !float.IsFinite(b[i])) { nonFinite++; continue; }
                double delta = Math.Abs((double)a[i] - b[i]); sum += delta; squared += delta * delta;
                if (delta > max) { max = delta; worst = i; }
                changed |= delta > 1.0 / 255;
            }
            if (changed) different++;
        }
        return new(sum / a.Length, Math.Sqrt(squared / a.Length), max, worst / 4 % Width, worst / 4 / Width, worst % 4, nonFinite, different);
    }

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SharpFAI.Player.Tests", "SharpFAI.Player.Tests.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("Run from the source checkout.");
    }

    private static string RunD3d(string root, string scratch, string shader, string input, string output)
    {
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        string install = Run(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath", root).Trim();
        Assert.That(install, Is.Not.Empty, "Install MSVC and Windows SDK to execute this explicit experiment.");
        string exe = Path.Combine(scratch, "runner.exe");
        string cpp = Path.Combine(root, "SharpFAI.Player.Tests", "Native", "LegacyVhsD3d.cpp");
        Run(Environment.GetEnvironmentVariable("ComSpec")!, $"/d /s /c \"\"{Path.Combine(install, "VC", "Auxiliary", "Build", "vcvars64.bat")}\" >nul && cl /nologo /EHsc /std:c++17 /O2 \"{cpp}\" /Fo\"{Path.Combine(scratch, "runner.obj")}\" /Fe\"{exe}\" /link d3d11.lib d3dcompiler.lib\"", root);
        return Run(exe, $"\"{shader}\" \"{input}\" \"{output}\"", root);
    }

    private static string Run(string command, string arguments, string cwd)
    {
        using var p = Process.Start(new ProcessStartInfo(command, arguments) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(entireProcessTree: true); Assert.Fail("Native helper timed out."); }
        string text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Assert.That(p.ExitCode, Is.Zero, text); return text;
    }

    private static string ToGlsl(string hlsl)
    {
        string body = hlsl[hlsl.IndexOf("float hashValue", StringComparison.Ordinal)..];
        body = body.Replace("float4 main(float2 uv : TEXCOORD0, float4 position : SV_POSITION) : SV_Target", "void main()", StringComparison.Ordinal)
            .Replace("float2", "vec2", StringComparison.Ordinal).Replace("float3", "vec3", StringComparison.Ordinal).Replace("float4", "vec4", StringComparison.Ordinal)
            .Replace("frac(", "fract(", StringComparison.Ordinal).Replace("saturate(", "sat(", StringComparison.Ordinal)
            .Replace("sourceImage.Sample(sourceSampler,", "texture(_MainTex,", StringComparison.Ordinal)
            .Replace("noiseImage.Sample(atlasSampler,", "texture(VHS,", StringComparison.Ordinal)
            .Replace("trackingImage.Sample(atlasSampler,", "texture(VHS2,", StringComparison.Ordinal);
        body = System.Text.RegularExpressions.Regex.Replace(body, @"return (vec4\([^;]+|texture\([^;]+);", "{ SV_Target0 = $1; return; }");
        return "#version 330\nuniform sampler2D _MainTex,VHS,VHS2; uniform float tracking,seconds,diagnostic; in vec2 vs_TEXCOORD0; out vec4 SV_Target0; float sat(float a){return clamp(a,0.0,1.0);}\n#define uv vs_TEXCOORD0\n" + body;
    }

    private static int Program(string fragment, Action<string>? beforeStage = null)
    {
        const string vertex = "#version 330\nout vec2 vs_TEXCOORD0; void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);vs_TEXCOORD0=p;gl_Position=vec4(p*2-1,0,1);}";
        int vs = GL.CreateShader(ShaderType.VertexShader), fs = GL.CreateShader(ShaderType.FragmentShader), program = GL.CreateProgram();
        try
        {
            foreach (var (id, text) in new[] { (vs, vertex), (fs, fragment) })
            {
                beforeStage?.Invoke(id == vs ? "vertex compile" : "fragment compile");
                GL.ShaderSource(id, text); GL.CompileShader(id); GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
                Assert.That(ok, Is.EqualTo(1), GL.GetShaderInfoLog(id));
            }
            beforeStage?.Invoke("link");
            GL.AttachShader(program, vs); GL.AttachShader(program, fs); GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            Assert.That(linked, Is.EqualTo(1), GL.GetProgramInfoLog(program)); return program;
        }
        catch { GL.DeleteProgram(program); throw; }
        finally { GL.DeleteShader(vs); GL.DeleteShader(fs); }
    }
}
