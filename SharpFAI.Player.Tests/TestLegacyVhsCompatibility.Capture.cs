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
    [Test, Explicit("Requires local RenderDoc exports in tmp-compare/legacy004-capture and desktop GL.")]
    [NonParallelizable, Apartment(ApartmentState.STA)]
    public void CapturedSourceTexturesAndClockCharacterizeLegacy004()
    {
        string root = FindRoot(), output = Path.Combine(root, "tmp-compare", "legacy004-capture");
        string[] inputs = File.ReadAllLines(Path.Combine(root, "SharpFAI.Player", "obj", "Debug", "net10.0", "AdofaiFilters.inputs"));
        string Resolve(string resource) => inputs.Single(l => l.Split('|')[1] == resource).Split('|')[2];
        string shaderPath = Resolve("Resources.Shaders.Filters/CameraFilterPackLegacy/Real_VHS.glsl");
        string original = UnityShaderAdapter.ImportFullscreenFragment(File.ReadAllText(shaderPath));
        string[] pngPaths = [Resolve("Resources.Filters.CameraFilterPack_VHS1.png"), Resolve("Resources.Filters.CameraFilterPack_VHS2.png")];
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        int program = 0, vao = 0, fbo = 0, target = 0;
        int[] textures = new int[7];
        var rows = new List<object>();
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
                program = Program(original);
                vao = GL.GenVertexArray(); GL.BindVertexArray(vao);
                GL.Disable(EnableCap.Blend); GL.Disable(EnableCap.Dither); GL.Disable(EnableCap.FramebufferSrgb);
                fbo = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                target = GL.GenTexture(); GL.ActiveTexture(TextureUnit.Texture5); GL.BindTexture(TextureTarget.Texture2D, target);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Width, Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target, 0);
                Assert.That(GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer), Is.EqualTo(FramebufferErrorCode.FramebufferComplete));
                GL.Viewport(0, 0, Width, Height);
                foreach (string directory in Directory.GetDirectories(output, "1-frame*"))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "contract.json")));
                    var contract = json.RootElement;
                    Assert.That(contract.GetProperty("pixelShaderSha256").GetString(), Is.EqualTo("33cf78551e79600eeb9df378eecf02095f30c9e7019fdfbf4faf6c9fa57c4c57"));
                    float[] time = contract.GetProperty("cb1").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    float tracking = contract.GetProperty("cb0")[8].GetSingle(), contrast = contract.GetProperty("cb0")[9].GetSingle();
                    byte[] captured = File.ReadAllBytes(Path.Combine(directory, "output0-mip0.bin"));
                    byte[] native = File.ReadAllBytes(Path.Combine(directory, "native.rgba"));
                    Assert.That(native, Is.EqualTo(captured), "The captured post-VS quad, constants and BC1 bytes reproduce the capture exactly on D3D11.");
                    for (int i = 0; i < 3; i++)
                    {
                        if (textures[i] != 0) GL.DeleteTexture(textures[i]);
                        textures[i] = GL.GenTexture(); GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, textures[i]);
                        byte[] raw = File.ReadAllBytes(Path.Combine(directory, $"input{i}-mip0.bin"));
                        if (i == 0) GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1280, 800, 0, PixelFormat.Rgba, PixelType.UnsignedByte, raw);
                        else GL.CompressedTexImage2D(TextureTarget.Texture2D, 0, (InternalFormat)0x83F1, 2048, 2048, 0, raw.Length, raw);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapNearest);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)(i == 0 ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)(i == 0 ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        if (textures[i + 3] != 0) GL.DeleteTexture(textures[i + 3]);
                        GL.ActiveTexture(TextureUnit.Texture3 + i);
                        // Keep raw top-down bytes at numeric UV zero, matching the capture and BC1 uploads.
                        textures[i + 3] = ImageDecoder.Upload(ImageDecoder.DecodeFile(pngPaths[i]));
                        GL.BindTexture(TextureTarget.Texture2D, textures[i + 3]);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapNearest);
                        if (textures[i + 5] != 0) GL.DeleteTexture(textures[i + 5]);
                        GL.ActiveTexture(TextureUnit.Texture5 + i);
                        textures[i + 5] = ImageDecoder.Upload(ImageDecoder.DecodeFile(Path.Combine(directory, $"input{i + 1}.png")), generateMipmaps: false);
                    }
                    byte[] Render(bool png, bool mipmaps, float offset = 0, bool wrongTracking = false, bool decodedCapture = false)
                    {
                        GL.UseProgram(program);
                        for (int i = 0; i < 3; i++)
                        {
                            GL.ActiveTexture(TextureUnit.Texture0 + i);
                            GL.BindTexture(TextureTarget.Texture2D, textures[decodedCapture && i > 0 ? i + 4 : png && i > 0 ? i + 2 : i]);
                            if (png && i > 0) GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, mipmaps ? 11 : 0);
                            GL.Uniform1(GL.GetUniformLocation(program, new[] { "_MainTex", "VHS", "VHS2" }[i]), i);
                        }
                        GL.Uniform4(GL.GetUniformLocation(program, "_Time"), time[0] + offset / 20, time[1] + offset, time[2] + offset * 2, time[3] + offset * 3);
                        GL.Uniform1(GL.GetUniformLocation(program, "TRACKING"), wrongTracking ? tracking + 0.212f : tracking);
                        GL.Uniform1(GL.GetUniformLocation(program, "CONTRAST"), contrast);
                        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                        byte[] result = new byte[Channels]; GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, result);
                        return result;
                    }
                    Metrics Error(byte[] pixels) => Compare(pixels.Select(v => v / 255f).ToArray(), captured.Select(v => v / 255f).ToArray());
                    byte[] matched = Render(false, false);
                    File.WriteAllBytes(Path.Combine(directory, "linux-captured-bc1.rgba"), matched);
                    var exactInputs = Error(matched);
                    var decodedCapture = Error(Render(false, false, decodedCapture: true));
                    var pngNoMips = Error(Render(true, false));
                    var pngWithMips = Error(Render(true, true));
                    for (int i = 0; i < 2; i++)
                    {
                        var image = ImageDecoder.DecodeFile(pngPaths[i]);
                        ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
                        GL.ActiveTexture(TextureUnit.Texture3 + i); GL.BindTexture(TextureTarget.Texture2D, textures[i + 3]);
                        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, image.Width, image.Height, PixelFormat.Rgba, PixelType.UnsignedByte, image.Pixels);
                        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                    }
                    var flippedPngNoMips = Error(Render(true, false));
                    var flippedPngWithMips = Error(Render(true, true));
                    var wrongTime = Error(Render(false, false, 0.73f));
                    var wrongTracking = Error(Render(false, false, wrongTracking: true));
                    rows.Add(new { capture = Path.GetFileName(directory), time, tracking, contrast, nativeExact = true, exactInputs, decodedCapture, pngNoMips, pngWithMips, flippedPngNoMips, flippedPngWithMips, wrongTime, wrongTracking });
                    TestContext.Out.WriteLine($"{Path.GetFileName(directory)}: BC1={exactInputs.Mae:G8}, PNG/no-mips={pngNoMips.Mae:G8}, PNG/mips={pngWithMips.Mae:G8}, wrong-time={wrongTime.Mae:G8}, max={exactInputs.Max:G8}");
                    Assert.That(exactInputs.NonFinite, Is.Zero);
                    Assert.That(exactInputs.Mae, Is.LessThan(0.001), "Matched captured resources and clock bound the remaining cross-API numerical error.");
                    Assert.That(decodedCapture.Mae, Is.EqualTo(exactInputs.Mae), "BC1 sampling and RenderDoc-decoded RGBA must give identical aggregate error.");
                    Assert.That(flippedPngNoMips.Mae, Is.GreaterThan(exactInputs.Mae), "The existing exported PNG differs from the captured runtime texture even after row alignment.");
                    Assert.That(wrongTime.Mae, Is.GreaterThan(exactInputs.Mae));
                    Assert.That(wrongTracking.Mae, Is.GreaterThan(exactInputs.Mae));
                    Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                }
                Assert.That(rows, Has.Count.EqualTo(4));
                File.WriteAllText(Path.Combine(output, "controlled-results.json"), JsonSerializer.Serialize(new
                {
                    renderer = GL.GetString(StringName.Renderer), shaderPath,
                    qualification = "Exact captured BC1 bytes, source RGBA8 and full _Time vector. GL raw rows and numerical UVs match D3D top-down convention. Native reproduction is byte-exact for every capture. Metrics are normalized RGBA; coordinates in the report refer to capture top-left rows.", rows,
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.UseProgram(0);
                if (program != 0) GL.DeleteProgram(program);
                foreach (int texture in textures) if (texture != 0) GL.DeleteTexture(texture);
                if (target != 0) GL.DeleteTexture(target);
                if (fbo != 0) GL.DeleteFramebuffer(fbo);
                if (vao != 0) GL.DeleteVertexArray(vao);
            }
        }
        finally { GLFWProvider.CheckForMainThread = checkMainThread; }
    }
}
