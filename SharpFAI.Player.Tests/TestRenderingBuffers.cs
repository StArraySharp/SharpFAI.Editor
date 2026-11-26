using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Filters;
using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

public sealed class TestRenderingBuffers
{
    [Test, Explicit("Requires desktop OpenGL.")]
    public void MeshRefreshKeepsItsVertexAndIndexBindings() =>
        WithContext(() =>
        {
            using var uploader = new GlMeshUploader();
            using var mesh = new Mesh(new VertexDeclaration((VertexAttribute.Position, VertexFormat.Float2)));
            AddTriangle(1);
            uploader.Upload(mesh);
            Assert.That(uploader.TryGetHandle(mesh, out var handle), Is.True);
            int vao = handle.Vao;

            mesh.Clear();
            AddTriangle(2);
            uploader.Upload(mesh);
            GL.BindVertexArray(vao);
            Assert.That(handle.Vao, Is.EqualTo(vao));
            Assert.That(GL.GetInteger(GetPName.ElementArrayBufferBinding), Is.EqualTo(handle.Ebo));
            GL.GetVertexAttrib(0, VertexAttribParameter.ArrayEnabled, out int enabled);
            Assert.That(enabled, Is.EqualTo(1));
            Assert.That(ReadInts(BufferTarget.ElementArrayBuffer, 3), Is.EqualTo(new[] { 0, 2, 1 }));
            Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));

            void AddTriangle(uint second)
            {
                mesh.AppendVertex().SetVector2(VertexAttribute.Position, Vector2.Zero);
                mesh.AppendVertex().SetVector2(VertexAttribute.Position, Vector2.UnitX);
                mesh.AppendVertex().SetVector2(VertexAttribute.Position, Vector2.UnitY);
                mesh.AppendIndex(0);
                mesh.AppendIndex(second);
                mesh.AppendIndex(3 - second);
            }
        });

    [Test, Explicit("Requires desktop OpenGL.")]
    public void ParticleInstancesDrawTheRequestedAtlasFrames() =>
        WithContext(() =>
        {
            using var sprites = new SpriteRenderer();
            int texture = GL.GenTexture();
            try
            {
                GL.BindTexture(TextureTarget.Texture2D, texture);
                byte[] atlas = [255, 0, 0, 255, 0, 255, 0, 255];
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 2, 1, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, atlas);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.ClearColor(0, 0, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                SpriteRenderer.ParticleInstance[] particles =
                [
                    new() { Center = new(-.5f, 0), HalfSize = new(.25f), Tint = Vector4.One },
                    new() { Center = new(.5f, 0), HalfSize = new(.25f), Tint = Vector4.One, Rotation = .4f, Frame = 1 },
                ];
                sprites.DrawParticleInstances(Matrix4.Identity, particles, texture, 2, straightAlpha: true);

                byte[] pixel = new byte[4];
                GL.ReadPixels(16, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                Assert.That(pixel, Is.EqualTo(new byte[] { 255, 0, 0, 255 }));
                GL.ReadPixels(48, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
                Assert.That(pixel, Is.EqualTo(new byte[] { 0, 255, 0, 255 }));
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
            }
            finally
            {
                GL.DeleteTexture(texture);
            }
        });

    [Test, Explicit("Requires desktop OpenGL.")]
    public void ModDepthTextureKeepsSurfacesAndBackgroundSeparate() =>
        WithContext(() =>
        {
            using var depth = new ModSceneDepth();
            using var backend = new OpenGlFilterBackend();
            int mask = depth.Render(64, 64, () =>
            {
                int program = SpriteRenderer.LinkSources("""
                    #version 410 core
                    void main() {
                        vec2 xy = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                        gl_Position = vec4(xy.x - 1.0, xy.y * 2.0 - 1.0, -0.5, 1.0);
                    }
                    """, """
                    #version 410 core
                    out vec4 color;
                    void main() { color = vec4(1); }
                    """);
                int vao = GL.GenVertexArray();
                try
                {
                    GL.BindVertexArray(vao);
                    GL.UseProgram(program);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                }
                finally
                {
                    GL.DeleteProgram(program);
                    GL.DeleteVertexArray(vao);
                }
            });
            const string shader = """
                vec4 modEffect(vec2 uv, vec4 color) { return vec4(readDepth(uv), 0, 0, 1); }
                """;
            backend.ApplyModShader("depth", shader, default, 0, [], false, mask, false);
            Assert.That(ReadPixel(8, 24)[0], Is.InRange(62, 65));
            Assert.That(ReadPixel(57, 48)[0], Is.EqualTo(255));
            Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
        });

    [Test, Explicit("Requires desktop OpenGL.")]
    public void ModHistoryKeepsPriorSampleAndResetsOnRequest() =>
        WithContext(() =>
        {
            using var backend = new OpenGlFilterBackend();
            const string shader = """
                vec4 modEffect(vec2 uv, vec4 color) { return mix(color, readHistory(uv), 0.5); }
                """;
            Render(0, 1, 0);
            Assert.That(ReadPixel(32, 32)[0], Is.EqualTo(255));
            Render(1, 0, 1);
            var blended = ReadPixel(32, 32);
            Assert.That(blended[0], Is.InRange(126, 129));
            Assert.That(blended[1], Is.InRange(126, 129));
            Render(1, 0, 1);
            Assert.That(ReadPixel(32, 32), Is.EqualTo(blended));
            backend.ResetModHistory();
            Render(2, 0, 1);
            Assert.That(ReadPixel(32, 32), Is.EqualTo(new byte[] { 0, 255, 0, 255 }));
            Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));

            void Render(int time, float red, float green)
            {
                GL.ClearColor(red, green, 0, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                backend.ApplyModShader("history", shader, default, time, [], false, 0, true);
            }
        });

    private static byte[] ReadPixel(int x, int y)
    {
        byte[] pixel = new byte[4];
        GL.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
        return pixel;
    }

    private static void WithContext(Action test)
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(64, 64), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            GL.Viewport(0, 0, 64, 64);
            test();
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    private static unsafe int[] ReadInts(BufferTarget target, int count)
    {
        int[] values = new int[count];
        fixed (int* pointer = values)
            GL.GetBufferSubData(target, nint.Zero, count * sizeof(int), (nint)pointer);
        return values;
    }
}
