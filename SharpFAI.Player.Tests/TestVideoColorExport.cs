using System.Diagnostics;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace SharpFAI.Player.Tests;

[NonParallelizable]
[Explicit("Requires ffmpeg, ffprobe and an OpenGL context for the GPU case.")]
public class TestVideoColorExport
{
    private const int Width = 320;
    private const int Height = 128;
    private static readonly byte[][] Colors =
    [
        [0, 0, 0], [255, 255, 255], [128, 128, 128], [255, 0, 0], [0, 255, 0],
        [0, 0, 255], [255, 255, 0], [255, 0, 255], [0, 25, 0], [255, 239, 0],
    ];

    [TestCase(false)]
    [TestCase(true)]
    [Apartment(ApartmentState.STA)]
    public void ActualEncoderRoundTripsBt709LimitedColorBlocks(bool gpu)
    {
        string ffmpeg = VideoExporter.FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg is required.");
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SharpFAI.Player.Tests", "SharpFAI.Player.Tests.csproj")))
            root = root.Parent;
        Assert.That(root, Is.Not.Null, "Keep generated videos inside the repository.");
        string directory = Path.Combine(root!.FullName, "SharpFAI.Player.Tests", "bin", "video-color-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] rgba = MakeBottomUpRgba();
            byte[] input = gpu ? PackOnGpu(rgba) : rgba;
            string output = Path.Combine(directory, "blocks.mp4");
            List<string> log = [];
            var request = new VideoExporter.Request("", output, Width, Height, 10, false, 0, 0, false, PreferHardware: false);
            using (var encoder = VideoExporter.StartEncoder(request, Width, Height, null, ffmpeg, gpu, log.Add))
            {
                for (int i = 0; i < 3; i++) encoder.WriteFrame(input);
                encoder.Finish(publish: true);
            }
            TestContext.Out.WriteLine(string.Join(Environment.NewLine, log));
            using var metadata = JsonDocument.Parse(Run("ffprobe", "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=color_space,color_range", "-of", "json", output));
            var stream = metadata.RootElement.GetProperty("streams")[0];
            Assert.That(stream.GetProperty("color_space").GetString(), Is.EqualTo("bt709"));
            Assert.That(stream.GetProperty("color_range").GetString(), Is.EqualTo("tv"));
            byte[] decoded = Run(ffmpeg, "-v", "error", "-i", output, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1");
            Assert.That(decoded, Has.Length.EqualTo(Width * Height * 3));
            int maxError = 0;
            for (int i = 0; i < Colors.Length; i++)
            {
                byte[] actual = Sample(decoded, i);
                TestContext.Out.WriteLine($"{(gpu ? "GPU" : "CPU")} RGB {string.Join(',', Colors[i])} -> {string.Join(',', actual)}");
                for (int c = 0; c < 3; c++) maxError = Math.Max(maxError, Math.Abs(actual[c] - Colors[i][c]));
            }
            Assert.That(maxError, Is.LessThanOrEqualTo(5), "Known RGB blocks must survive actual encoding and automatic tagged decoding.");
            byte[] yuv = Run(ffmpeg, "-v", "error", "-i", output, "-frames:v", "1", "-pix_fmt", "yuv420p", "-f", "rawvideo", "pipe:1");
            Assert.That(yuv[32 * Width + 32], Is.InRange(15, 17), "Limited-range black Y.");
            Assert.That(yuv[32 * Width + 96], Is.InRange(234, 236), "Limited-range white Y.");
            byte[] wrong = Run(ffmpeg, "-v", "error", "-i", output, "-vf", "scale=in_color_matrix=bt601:in_range=tv:out_range=pc,format=rgb24",
                "-frames:v", "1", "-f", "rawvideo", "pipe:1");
            byte[] magenta = Sample(wrong, 7);
            TestContext.Out.WriteLine($"Wrong BT.601 decode of BT.709 magenta: {string.Join(',', magenta)}");
            Assert.That(Math.Abs(magenta[0] - 255), Is.GreaterThan(10), "The negative control must detect a matrix mismatch.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] MakeBottomUpRgba()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            byte[] color = Colors[(Height - 1 - y) / 64 * 5 + x / 64];
            int offset = (y * Width + x) * 4;
            color.CopyTo(pixels, offset);
            pixels[offset + 3] = 255;
        }
        return pixels;
    }

    private static byte[] PackOnGpu(byte[] rgba)
    {
        bool checkMainThread = GLFWProvider.CheckForMainThread;
        GLFWProvider.CheckForMainThread = false;
        try
        {
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new Vector2i(Width, Height), StartVisible = false, StartFocused = false,
                APIVersion = new Version(3, 3), Profile = ContextProfile.Core,
            });
            window.MakeCurrent();
            int texture = GL.GenTexture();
            try
            {
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Width, Height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                using var converter = new VideoColorConverter(Width, Height);
                converter.Pack(texture);
                byte[] nv12 = new byte[Width * Height * 3 / 2];
                GL.ReadPixels(0, 0, converter.Width, converter.Height, PixelFormat.Bgra, PixelType.UnsignedInt8888Reversed, nv12);
                Assert.That(GL.GetError(), Is.EqualTo(ErrorCode.NoError));
                return nv12;
            }
            finally
            {
                GL.DeleteTexture(texture);
            }
        }
        finally
        {
            GLFWProvider.CheckForMainThread = checkMainThread;
        }
    }

    private static byte[] Sample(byte[] pixels, int block)
    {
        int offset = ((block / 5 * 64 + 32) * Width + block % 5 * 64 + 32) * 3;
        return pixels[offset..(offset + 3)];
    }

    private static byte[] Run(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, stderr.GetAwaiter().GetResult());
        return output.ToArray();
    }
}
