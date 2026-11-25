using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SharpFAI.Player.Engine;

internal sealed record VideoBackgroundSource(string Path, string Decoder, int Width, int Height,
    double Aspect, double Duration, double Start, double InputStart, double FrameRate)
{
    public int FrameBytes => checked(Width * Height * 4);

    public static VideoBackgroundSource? Open(string? directory, string? name, VideoBackgroundSource? previous,
        List<string> warnings, CancellationToken token)
    {
        if (directory is null || string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            string path = System.IO.Path.GetFullPath(name.Trim().Replace('\\', System.IO.Path.DirectorySeparatorChar), directory);
            if (string.Equals(previous?.Path, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return previous;
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            string decoder = VideoExporter.FindFfmpeg() ?? throw new InvalidOperationException("找不到 ffmpeg / ffmpeg not found");
            token.ThrowIfCancellationRequested();
            Mp4Container.Video? mp4;
            try { mp4 = Mp4Container.ReadVideo(path); }
            catch (Exception parseError) when (parseError is not (OperationCanceledException or OutOfMemoryException))
            {
                // Unusual or damaged MP4-family files can still be readable by the decoder.
                mp4 = null;
            }
            token.ThrowIfCancellationRequested();
            if (mp4 is { } video)
            {
                ValidateDimensions(video.Width, video.Height);
                return new(path, decoder, video.Width, video.Height, video.Aspect, video.Duration,
                    video.Start, video.InputStart, video.FrameRate);
            }

            string? probe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(decoder)!,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (!File.Exists(probe)) probe = VideoExporter.FindMediaTool("ffprobe");
            if (probe is null) throw new InvalidOperationException("找不到 ffprobe / ffprobe not found");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using Process process = StartProcess(probe, ["-v", "error", "-protocol_whitelist", "file,pipe",
                "-select_streams", "V:0", "-show_entries",
                "stream=width,height,sample_aspect_ratio,r_frame_rate,duration,start_time:stream_side_data=rotation:format=duration,start_time",
                "-of", "json", path]);
            using var stop = deadline.Token.Register(static p => Kill((Process)p!), process);
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            string detail = error.GetAwaiter().GetResult().Trim();
            if (process.ExitCode != 0) throw new InvalidDataException(detail);
            using var json = JsonDocument.Parse(output.GetAwaiter().GetResult());
            var stream = json.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
            if (stream.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("无视频轨 / no video stream");
            json.RootElement.TryGetProperty("format", out var format);
            int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
            ValidateDimensions(width, height);
            double aspect = (double)width / height * Fraction(stream, "sample_aspect_ratio", ':', 1);
            if (stream.TryGetProperty("side_data_list", out var sideData))
                foreach (var item in sideData.EnumerateArray())
                    if (item.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetDouble()) % 180 == 90)
                    {
                        (width, height) = (height, width);
                        aspect = 1 / aspect;
                        break;
                    }
            double inputStart = Number(format, "start_time", 0);
            double start = Number(stream, "start_time", inputStart);
            return new(path, decoder, width, height, aspect,
                Number(stream, "duration", Number(format, "duration", 0)), start, inputStart,
                Fraction(stream, "r_frame_rate", '/', 30));
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is (IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or
            KeyNotFoundException or FormatException or OverflowException or System.ComponentModel.Win32Exception or OperationCanceledException))
        {
            warnings.Add($"背景视频加载失败 / Background video unavailable ({name}): {error.Message}");
            return null;
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height * 4 > Array.MaxLength)
            throw new InvalidDataException("无效视频尺寸 / invalid dimensions");
    }

    public double TimeAt(double musicTime, double musicOffset, double countdown, double videoOffset, bool loop)
    {
        double time = musicTime - musicOffset - countdown + videoOffset;
        if (time < 0 || Duration <= 0) return time;
        return loop ? time % Duration : Math.Min(time, Duration);
    }

    private static double Number(JsonElement item, string key, double fallback) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) &&
        double.TryParse(value.ToString(), CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number : fallback;

    private static double Fraction(JsonElement item, string key, char delimiter, double fallback) =>
        item.TryGetProperty(key, out var value) && TryFraction(value.ToString(), delimiter, out double result)
            ? result : fallback;

    internal static bool TryFraction(ReadOnlySpan<char> input, char delimiter, out double result)
    {
        result = 0;
        int split = input.IndexOf(delimiter);
        if (split < 0 || !long.TryParse(input[..split], CultureInfo.InvariantCulture, out long first) ||
            !long.TryParse(input[(split + 1)..], CultureInfo.InvariantCulture, out long second) || first <= 0 || second <= 0)
            return false;
        result = (double)first / second;
        return true;
    }

    internal static Process StartProcess(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException(executable);
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
