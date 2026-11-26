using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using Thrust.Audio;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SharpFAI.Player;

/// <summary>
/// Pipes rendered frames to ffmpeg and mixes the chart's audio onto the export timeline.
/// </summary>
/// <remarks>
/// <para>
/// 音画同步靠「同一个时钟」保证：画面在 <c>_playTime = index / fps</c> 上求值，音频则把音乐与
/// 打击音按同一个 0 点混成一条 PCM，并显式补上关卡 offset 的静音前导。ffmpeg 只是把两者封装
/// 到一起，不做任何时间伸缩。
/// </para>
/// <para>
/// Sync comes from using one clock: frames are evaluated at <c>playTime = index / fps</c> and the
/// audio is mixed onto the same zero point, with the level offset's silent lead-in baked in.
/// ffmpeg only muxes; it never rescales time.
/// </para>
/// </remarks>
internal static class VideoExporter
{
    /// <summary>一次导出请求。</summary>
    /// <param name="LevelPath">关卡文件。</param>
    /// <param name="OutputPath">输出视频。</param>
    /// <param name="Width">视频宽度（像素）。</param>
    /// <param name="Height">视频高度（像素）。</param>
    /// <param name="Fps">帧率。</param>
    /// <param name="KeepFrames">是否保序帧图片。</param>
    /// <param name="BitrateKbps">视频码率（kbps），0 表示用 CRF。</param>
    /// <param name="Zoom">正值固定为每世界单位的像素数，0 使用谱面的摄像机缩放。</param>
    /// <param name="HidePassedFloors">经过的砖块是否立即消失。</param>
    /// <param name="PreferHardware">优先使用可用的硬件编码器。</param>
    public readonly record struct Request(
        string LevelPath,
        string OutputPath,
        int Width,
        int Height,
        int Fps,
        bool KeepFrames,
        int BitrateKbps,
        float Zoom,
        bool HidePassedFloors,
        bool PreferHardware = true);

    /// <summary>
    /// 是否为视频文件。视频绝不能被当成音乐输入：ffmpeg 会把其中的音轨混进来，
    /// 结果就是「关卡没有音频，却听到了目录里某个 mp4 的声音」。
    /// Whether the path is a video. A video must never be used as the music input: ffmpeg would mix
    /// in its audio track, which is exactly how "the level has no song but I hear that mp4" happens.
    /// </summary>
    public static bool IsVideoPath(string? path) => LevelAssetPaths.IsVideoFile(path);

    /// <summary>
    /// 选定输出路径，并避开关卡自己引用的素材文件。
    /// Pick the output path without clobbering an asset the level itself references.
    /// </summary>
    public static string ResolveOutputPath(string levelPath, Level? level)
        => LevelAssetPaths.ResolveExportPath(levelPath, level);

    /// <summary>
    /// 把宽高对齐到偶数。<c>libx264</c> 的 <c>yuv420p</c> 要求 4:2:0 采样，宽高必须是偶数，
    /// 否则 ffmpeg 直接退出、管道随即断开。
    /// Snap the dimensions to even values. libx264's yuv420p uses 4:2:0 chroma, which requires even
    /// dimensions; odd ones make ffmpeg exit immediately and the pipe break.
    /// </summary>
    public static (int Width, int Height) AlignEven(int width, int height)
        => (Math.Max(2, width / 2 * 2), Math.Max(2, height / 2 * 2));

    /// <summary>
    /// Encode raw frames streamed to ffmpeg.
    /// </summary>
    public static EncoderSession StartEncoder(
        Request request,
        int width,
        int height,
        string? audioPath,
        string ffmpegPath,
        bool canPackNv12,
        Action<string>? log)
    {
        string codec = request.PreferHardware ? FindHardwareEncoder(ffmpegPath, width, height, request.Fps) ?? "libx264"
            : "libx264";
        bool packedNv12 = canPackNv12;
        log?.Invoke($"video encoder: {codec} · {(packedNv12 ? "GPU NV12" : "RGBA")}");

        List<string> arguments =
        [
            "-y",
            "-hide_banner",
            "-loglevel", "warning",

            "-f", "rawvideo",
            "-pixel_format", packedNv12 ? "nv12" : "rgba",
            "-video_size", $"{width}x{height}",
            "-framerate", request.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        if (packedNv12)
            arguments.AddRange(["-colorspace", "bt709", "-color_range", "tv"]);
        arguments.AddRange(["-i", "pipe:0"]);

        // 视频文件永远不能作为音频输入：ffmpeg 会取它的音轨，听起来就像「误读了目录里的 mp4」
        // A video must never be an audio input: ffmpeg would take its soundtrack, which is exactly
        // how "it read the mp4 in the folder" feels
        bool hasAudio = audioPath is not null && File.Exists(audioPath) && !IsVideoPath(audioPath);

        if (hasAudio)
        {
            arguments.AddRange(["-i", audioPath!]);
        }

        if (!packedNv12)
            // Raw OpenGL rows are bottom-up; convert full-range RGB to limited-range BT.709 after flipping.
            arguments.AddRange(["-vf", "vflip,scale=in_range=pc:out_color_matrix=bt709:out_range=tv",
                "-filter_threads", "2"]);
        arguments.AddRange(["-colorspace", "bt709", "-color_range", "tv"]);
        AddVideoOptions(arguments, codec, request.BitrateKbps, packedNv12);

        if (hasAudio)
        {
            // 音频已经是最终混音，直接编码为 AAC
            // The audio is already the final mix; encode it as AAC
            arguments.AddRange([
                "-c:a", "aac",
                "-b:a", "192k",
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-af", "apad",
                "-shortest",
            ]);
        }

        string output = Path.GetFullPath(request.OutputPath);
        string temporaryOutput = Path.Combine(Path.GetDirectoryName(output)!,
            $".sharpfai-{Guid.NewGuid():N}.mp4");
        arguments.AddRange(["-f", "mp4", temporaryOutput]);

        ProcessStartInfo info = new(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 ffmpeg / Failed to start ffmpeg");

        return new EncoderSession(process, width, height, packedNv12, temporaryOutput, output);
    }

    private static void AddVideoOptions(List<string> arguments, string codec, int bitrateKbps, bool packedNv12 = false)
    {
        arguments.AddRange(["-c:v", codec, "-pix_fmt", !packedNv12 && codec == "libx264" ? "yuv420p" : "nv12"]);
        switch (codec)
        {
            case "h264_nvenc":
                arguments.AddRange(["-preset", "p2", "-rc", "vbr"]);
                if (bitrateKbps <= 0)
                    arguments.AddRange(["-cq", "18", "-b:v", "0"]);
                break;
            case "h264_qsv":
                arguments.AddRange(["-preset", "veryfast"]);
                if (bitrateKbps <= 0)
                    arguments.AddRange(["-global_quality", "18"]);
                break;
            case "h264_amf":
                arguments.AddRange(["-quality", "speed"]);
                if (bitrateKbps <= 0)
                    arguments.AddRange(["-rc", "cqp", "-qp_i", "18", "-qp_p", "18"]);
                break;
            default:
                arguments.AddRange(["-preset", "veryfast", "-tune", "animation", "-threads", "2",
                    "-x264-params", "rc-lookahead=10:sync-lookahead=0:bframes=2"]);
                if (bitrateKbps <= 0)
                    arguments.AddRange(["-crf", "18"]);
                break;
        }

        if (bitrateKbps > 0)
            arguments.AddRange(["-b:v", FormattableString.Invariant($"{bitrateKbps}k")]);
    }

    private static string? FindHardwareEncoder(string ffmpegPath, int width, int height, int fps)
    {
        foreach (string codec in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
        {
            List<string> arguments = ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                FormattableString.Invariant($"color=s={width}x{height}:r={Math.Max(1, fps)}"), "-frames:v", "1"];
            AddVideoOptions(arguments, codec, 0);
            arguments.AddRange(["-f", "null", "-"]);
            ProcessStartInfo info = new(ffmpegPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);

            using Process? process = Process.Start(info);
            if (process is null)
                continue;

            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                continue;
            }
            _ = errors.GetAwaiter().GetResult();
            if (process.ExitCode == 0)
                return codec;
        }

        return null;
    }

    /// <summary>序列帧文件名模式（仅在保留序列帧时使用）。</summary>
    public const string FrameFilePattern = "frame%06d.png";

    /// <summary>
    /// 一个正在运行的 ffmpeg 编码会话：写像素、等结束。
    /// A running ffmpeg encode session: write pixels, then wait for completion.
    /// </summary>
    public sealed class EncoderSession : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _input;
        private readonly string _temporaryOutput;
        private readonly string _output;

        internal EncoderSession(Process process, int width, int height, bool packedNv12,
            string temporaryOutput, string output)
        {
            _process = process;
            _input = process.StandardInput.BaseStream;
            _temporaryOutput = temporaryOutput;
            _output = output;

            // 后台读干 stderr，否则管道写满后 ffmpeg 会阻塞
            // Drain stderr on a worker, otherwise ffmpeg blocks once the pipe fills
            _stderrReader = new Thread(() =>
            {
                try
                {
                    _stderr = process.StandardError.ReadToEnd();
                }
                catch (Exception ex)
                {
                    _stderr = ex.Message;
                }
            })
            {
                IsBackground = true,
                Name = "ffmpeg-stderr",
            };

            _stderrReader.Start();

            PackedNv12 = packedNv12;
            FrameBytes = packedNv12 ? checked(width * height * 3 / 2) : checked(width * height * 4);
        }

        private readonly Thread _stderrReader;
        private volatile string _stderr = string.Empty;
        public bool PackedNv12 { get; }
        public int FrameBytes { get; }
        public void WriteFrame(ReadOnlySpan<byte> pixels)
        {
            if (pixels.Length != FrameBytes)
            {
                throw new ArgumentException(
                    $"帧字节数 {pixels.Length} 与期望的 {FrameBytes} 不符 / Frame size mismatch",
                    nameof(pixels));
            }

            _input.Write(pixels);
        }

        public void Cancel()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit();
                }
            }
            catch (InvalidOperationException)
            {
                // ffmpeg exited while cancellation was in progress.
            }
        }

        /// <summary>关闭输入并等待 ffmpeg 退出。</summary>
        public void Finish(bool publish, Action<string, string, CancellationToken>? postprocess = null,
            CancellationToken token = default)
        {
            try
            {
                _input.Flush();
                _input.Close();
            }
            catch (Exception)
            {
                // 进程可能已因错误退出 / The process may already have exited
            }

            _process.WaitForExit();
            _stderrReader.Join(TimeSpan.FromSeconds(5));

            if (_process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg 退出码 {_process.ExitCode}：{_stderr}");
            }

            if (publish)
            {
                token.ThrowIfCancellationRequested();
                string packagedOutput = _temporaryOutput + ".mux";
                try
                {
                    Mp4Container.WriteExport(_temporaryOutput, packagedOutput);
                    postprocess?.Invoke(packagedOutput, _output, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(packagedOutput, _output, overwrite: true);
                }
                finally
                {
                    TryDelete(packagedOutput);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _input.Close();
            }
            catch (Exception)
            {
                // ignored
            }

            _process.Dispose();
            TryDelete(_temporaryOutput);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 在 PATH 或常见位置查找 ffmpeg。
    /// Locate ffmpeg on PATH or in the usual install locations.
    /// </summary>
    public static string? FindFfmpeg() => FindMediaTool("ffmpeg");

    internal static string? FindMediaTool(string name)
    {
        string executable = OperatingSystem.IsWindows() ? name + ".exe" : name;

        string bundled = Path.Combine(AppContext.BaseDirectory, executable);
        if (File.Exists(bundled)) return bundled;

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), executable);

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // 非法路径直接跳过 / Skip malformed PATH entries
                }
            }
        }

        // Windows 上常见的安装位置
        // Common Windows install locations
        foreach (string root in new[] { @"C:\ffmpeg\bin", @"C:\ffmpeg" })
        {
            string candidate = Path.Combine(root, executable);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 把音乐与打击音按同一个 0 点混成一条 16 位立体声 WAV。
    /// Mix the music and hitsound onto one zero point as a 16-bit stereo WAV.
    /// </summary>
    /// <param name="music">音乐 PCM；可为 null。</param>
    /// <param name="hitsound">打击音 PCM；可为 null。</param>
    /// <param name="musicVolume">音乐音量 0..1。</param>
    /// <param name="hitsoundVolume">打击音的线性增益，0 表示静音，1 表示原音量。</param>
    /// <param name="totalSeconds">视频总时长（秒）；混音不足处补静音。</param>
    /// <param name="path">输出 WAV。</param>
    /// <param name="playbackRate">Music playback rate.</param>
    /// <param name="hitsoundRate">Hitsound playback rate; pass 1 for PCM already on the output clock.</param>
    internal static DecodedAudio DecodeExportAudio(string ffmpeg, string path, double seconds)
    {
        const int rate = DecodedAudio.TargetSampleRate;
        const int channels = 2;
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] arguments = ["-nostdin", "-v", "error", "-i", path, "-map", "0:a:0", "-vn",
            "-ar", rate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ac", "2",
            "-t", FormattableString.Invariant($"{seconds:F6}"), "-f", "s16le", "pipe:1"];
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var pcm = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(pcm);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Could not decode music for export: {error.GetAwaiter().GetResult().Trim()}");
        return new DecodedAudio { Pcm = pcm.ToArray(), Channels = channels, SampleRate = rate };
    }

    public static void WriteMixedWav(
        DecodedAudio? music,
        DecodedAudio? hitsound,
        float musicVolume,
        float hitsoundVolume,
        double totalSeconds,
        string path,
        double playbackRate = 1.0,
        double? hitsoundRate = null, Func<DecodedAudio, DecodedAudio>? processMix = null, DecodedAudio? realtimeMix = null)
    {
        if (realtimeMix is not null)
        {
            var pcm = processMix?.Invoke(realtimeMix) ?? realtimeMix;
            using var writer = BeginWav(path, pcm.Pcm.Length, pcm.SampleRate, pcm.Channels);
            writer.Write(pcm.Pcm);
            return;
        }
        // 混音统一到 48 kHz 立体声：两条轨采样率可能不同，重采样在这里一次做完
        // Mix at 48 kHz stereo: the two tracks may differ in rate, so resample once here
        const int Rate = 48000;
        const int Channels = 2;

        int sampleCount = (int)Math.Ceiling(Math.Max(totalSeconds, 0.0) * Rate);
        sampleCount = Math.Max(sampleCount, 1);

        float[] mix = new float[sampleCount * Channels];

        Accumulate(music, musicVolume, mix, Rate, Channels, playbackRate);
        Accumulate(hitsound, hitsoundVolume, mix, Rate, Channels, hitsoundRate ?? playbackRate);

        WriteWav(path, mix, Rate, Channels, processMix);
    }

    /// <summary>把一条音轨线性重采样后叠加进混音缓冲。</summary>
    private static void Accumulate(
        DecodedAudio? source,
        float volume,
        float[] destination,
        int rate,
        int channels,
        double playbackRate)
    {
        if (source is null || source.Pcm.Length == 0 || volume <= 0f)
        {
            return;
        }

        int sourceRate = Math.Max(source.SampleRate, 1);
        int sourceChannels = Math.Max(source.Channels, 1);
        int sourceFrames = source.Pcm.Length / (sourceChannels * sizeof(short));
        int destinationFrames = destination.Length / channels;

        if (sourceFrames == 0)
        {
            return;
        }

        // 用「源帧位置」做线性插值，避免整段重采样产生额外分配
        // Linear-interpolate on the source frame position, with no intermediate resample buffer
        double step = (double)sourceRate / rate * playbackRate;
        double sourcePosition = 0.0;

        ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(source.Pcm);

        for (int frame = 0; frame < destinationFrames && sourcePosition < sourceFrames; frame++)
        {
            int index = (int)sourcePosition;

            if (index >= sourceFrames)
            {
                break;
            }

            int next = Math.Min(index + 1, sourceFrames - 1);
            float fraction = (float)(sourcePosition - index);

            for (int channel = 0; channel < channels; channel++)
            {
                int sourceChannel = Math.Min(channel, sourceChannels - 1);

                float a = samples[index * sourceChannels + sourceChannel] / 32768f;
                float b = samples[next * sourceChannels + sourceChannel] / 32768f;
                float value = a + (b - a) * fraction;

                destination[frame * channels + channel] += value * volume;
            }

            sourcePosition += step;
        }
    }

    /// <summary>写 16 位 PCM WAV。</summary>
    private static void WriteWav(string path, float[] mix, int rate, int channels,
        Func<DecodedAudio, DecodedAudio>? processMix)
    {
        int frameCount = mix.Length / channels;
        int dataBytes = frameCount * channels * sizeof(short);
        using var writer = BeginWav(path, dataBytes, rate, channels);

        if (processMix is not null)
        {
            var pcm = new byte[dataBytes];
            for (int index = 0; index < mix.Length; index++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * sizeof(short)), Quantize(mix[index]));
            writer.Write(processMix(new DecodedAudio { Pcm = pcm, SampleRate = rate, Channels = channels }).Pcm);
            return;
        }

        Span<byte> block = stackalloc byte[8192];
        const int samplesPerBlock = 8192 / sizeof(short);
        for (int first = 0; first < mix.Length; first += samplesPerBlock)
        {
            int count = Math.Min(samplesPerBlock, mix.Length - first);
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(block[(i * sizeof(short))..], Quantize(mix[first + i]));
            }
            writer.Write(block[..(count * sizeof(short))]);
        }

        static short Quantize(float value) => (short)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f);
    }

    private static BinaryWriter BeginWav(string path, int dataBytes, int rate, int channels)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var writer = new BinaryWriter(File.Create(path));
        try
        {
            writer.Write("RIFF"u8);
            writer.Write(checked(36 + dataBytes));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(rate);
            writer.Write(rate * channels * sizeof(short));
            writer.Write((short)(channels * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            return writer;
        }
        catch { writer.Dispose(); throw; }
    }
}

