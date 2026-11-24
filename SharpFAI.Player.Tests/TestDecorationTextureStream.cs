using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

[NonParallelizable]
[Apartment(ApartmentState.STA)]
public sealed class TestDecorationTextureStream
{
    [TestCase(true), TestCase(false), Explicit("Requires desktop OpenGL.")]
    public void UploadKeepsCurrentAndUpcomingTexturesAndReusesExpiredStorage(bool useImmutableStorage)
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new(16, 16), StartVisible = false, StartFocused = false,
                APIVersion = new(4, 1), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();

            if (useImmutableStorage && !DecorationTextureStream.SupportsImmutableStorage())
                Assert.Ignore("Immutable texture storage is unavailable.");

            using var stream = new DecorationTextureStream(useImmutableStorage: useImmutableStorage);
            var first = "first";
            var upcoming = "upcoming";
            var replacement = "replacement";
            ImageDecoder.RgbaImage red = Solid(255, 0, 0);
            ImageDecoder.RgbaImage green = Solid(0, 255, 0);
            ImageDecoder.RgbaImage blue = Solid(0, 0, 255);

            stream.BeginFrame(0);
            int firstHandle = stream.Get(first, red);
            Assert.That(stream.Prefetch(upcoming, green, .5), Is.True);
            AssertTexture(firstHandle, red);
            stream.BeginFrame(.1);
            stream.BeginFrame(.2);
            stream.BeginFrame(.3);
            int replacementHandle = stream.Get(replacement, blue);
            Assert.That(replacementHandle, Is.EqualTo(firstHandle));
            AssertTexture(replacementHandle, blue);
            Assert.That(stream.Prefetch(upcoming, green, .5), Is.False);
            AssertTexture(stream.Get(upcoming, green), green);

            var staged = "staged";
            var interrupted = "interrupted";
            var rows = new ImageDecoder.RgbaImage(2, 2,
                [0, 20, 0, 255, 0, 20, 0, 255, 0, 80, 0, 255, 0, 80, 0, 255]);
            Assert.That(stream.Prefetch(staged, rows, 1, 8, out bool ready), Is.True);
            Assert.That(ready, Is.False);
            Assert.That(stream.TryTake(staged, out _), Is.False);
            stream.BeginFrame(.4);
            Assert.That(stream.Prefetch(staged, rows, 1, 8, out ready), Is.True);
            Assert.That(ready, Is.True);
            AssertTexture(stream.Get(staged, rows), rows);

            Assert.That(stream.Prefetch(interrupted, rows, 1, 8, out ready), Is.True);
            Assert.That(ready, Is.False);
            stream.BeginFrame(.8);
            AssertTexture(stream.Get(interrupted, rows), rows);
            Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    private static ImageDecoder.RgbaImage Solid(byte red, byte green, byte blue) => new(2, 2,
        [red, green, blue, 255, red, green, blue, 255, red, green, blue, 255, red, green, blue, 255]);

    private static void AssertTexture(int handle, ImageDecoder.RgbaImage image)
    {
        GL.BindTexture(TextureTarget.Texture2D, handle);
        byte[] pixels = new byte[16];
        GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 1, GetTextureParameter.TextureWidth, out int mipWidth);
        Assert.Multiple(() =>
        {
            Assert.That(pixels, Is.EqualTo(image.Pixels));
            Assert.That(mipWidth, Is.Zero);
        });
    }
}
