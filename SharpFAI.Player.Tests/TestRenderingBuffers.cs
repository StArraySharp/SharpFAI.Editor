using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
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
