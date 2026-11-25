using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace SharpFAI.Player.Engine;

internal sealed class VideoBackgroundDecoder(VideoBackgroundSource source) : IDisposable
{
    internal sealed record Frame(double Time, byte[] Pixels) : IDisposable
    {
        public void Dispose() => ArrayPool<byte>.Shared.Return(Pixels);
    }

    private const double Epsilon = 1e-7;
    private DecodeWindow? _window;
    private double _requested;
    private double _shownTime;
    private bool _waitingToSeek;
    private Frame? _candidate;
    public bool HasFrame { get; private set; }
    public string? Error { get; private set; }

    public Frame? Read(double time, bool exact, bool advancing = false)
    {
        if (!double.IsFinite(time) || time < 0 || Error is not null)
        {
            Reset();
            return null;
        }

        double sampled = _candidate?.Time ?? _shownTime;
        bool jump = _window is null || _waitingToSeek || time < sampled - Epsilon ||
            Math.Abs(time - _requested) > .5 || advancing && HasFrame && time - _shownTime > .75 &&
            (source.Duration <= 0 || time < source.Duration);
        if (jump)
        {
            HasFrame = false;
            if (!exact && _window is { } busy && Stopwatch.GetElapsedTime(busy.Started).TotalMilliseconds < 70)
            {
                _waitingToSeek = true;
                return null;
            }
            Start(time);
        }
        _requested = time;

        Frame? latest = _candidate;
        _candidate = null;
        bool targetReached = false;
        try
        {
            while (true)
            {
                var window = _window!;
                if (!window.Frames.TryPeek(out var frame))
                {
                    if (!window.Frames.Completion.IsCompleted)
                    {
                        if (!exact) break;
                        if (window.Frames.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15))
                            .GetAwaiter().GetResult()) continue;
                    }
                    targetReached = true;
                    if (!HasFrame && latest is null && window.Error is null && window.Accurate && window.Seek > 0)
                    {
                        Start(time, accurate: false);
                        if (exact) continue;
                    }
                    break;
                }
                if (frame.Time > time + Epsilon)
                {
                    // Long variable-frame-rate stills can span the accurate seek's entire
                    // preroll. In that case decode from the preceding keyframe.
                    if (!HasFrame && latest is null && window.Accurate && window.Seek > 0)
                    {
                        Start(time, accurate: false);
                        if (exact) continue;
                    }
                    targetReached = true;
                    break;
                }
                if (!window.Frames.TryRead(out frame)) continue;
                latest?.Dispose();
                latest = frame;
            }
        }
        catch (TimeoutException)
        {
            latest?.Dispose();
            Error = "背景视频解码超时 / Background video timed out";
            Reset();
            return null;
        }

        if (_window?.Error is { } error)
            Error = error;
        if (latest is { } match)
        {
            if (!HasFrame && !targetReached && time - match.Time > Math.Max(.035, 1.25 / source.FrameRate))
            {
                _candidate = match;
                return null;
            }
            _shownTime = match.Time;
            HasFrame = true;
        }
        return latest;
    }

    private void Start(double time, bool accurate = true)
    {
        Reset();
        double seek = Math.Max(0, time - Math.Max(.06, 2 / source.FrameRate));
        _window = new DecodeWindow(source, seek, accurate);
        _shownTime = double.NegativeInfinity;
    }

    public void Reset()
    {
        _window?.Dispose();
        _window = null;
        _candidate?.Dispose();
        _candidate = null;
        _waitingToSeek = false;
        HasFrame = false;
    }

    public void Dispose() => Reset();

    private sealed class DecodeWindow : IDisposable
    {
        private const int BufferedFrames = 3;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private string? _error;

        public DecodeWindow(VideoBackgroundSource source, double seek, bool accurate)
        {
            Seek = seek;
            Accurate = accurate;
            var queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(BufferedFrames)
                { SingleReader = true, SingleWriter = true });
            Frames = queue.Reader;
            _worker = Task.Run(() => Decode(source, queue.Writer, _stop.Token));
        }

        public long Started { get; } = Stopwatch.GetTimestamp();
        public double Seek { get; }
        public bool Accurate { get; }
        public ChannelReader<Frame> Frames { get; }
        public string? Error => Volatile.Read(ref _error);

        private async Task Decode(VideoBackgroundSource source, ChannelWriter<Frame> queue, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                List<string> args = ["-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
                    "-protocol_whitelist", "file,pipe", "-threads", "4", "-filter_threads", "2"];
                double seek = Seek + source.Start - source.InputStart;
                if (seek > 0) args.AddRange(["-ss", seek.ToString("F8", CultureInfo.InvariantCulture)]);
                if (!Accurate) args.Add("-noaccurate_seek");
                args.AddRange(["-copyts", "-i", source.Path, "-map", "0:V:0", "-an", "-sn", "-dn",
                    "-vf", "format=rgba,showinfo=checksum=0", "-fps_mode", "passthrough",
                    "-c:v", "rawvideo", "-threads", "1", "-f", "rawvideo", "pipe:1"]);
                using Process process = VideoBackgroundSource.StartProcess(source.Decoder, args);
                using var stop = token.Register(static p => VideoBackgroundSource.Kill((Process)p!), process);
                var timestamps = Channel.CreateUnbounded<double>(new UnboundedChannelOptions
                    { SingleReader = true, SingleWriter = true });
                Task<string> diagnostics = ReadTimestamps(process.StandardError, timestamps.Writer, token);
                int frameSize = source.FrameBytes;
                try
                {
                    Stream pipe = process.StandardOutput.BaseStream;
                    while (true)
                    {
                        byte[]? pixels = ArrayPool<byte>.Shared.Rent(frameSize);
                        try
                        {
                            int length = await pipe.ReadAtLeastAsync(pixels.AsMemory(0, frameSize), frameSize, false, token);
                            if (length == 0) break;
                            if (length != frameSize) throw new InvalidDataException("Incomplete background video frame");
                            double pts = await timestamps.Reader.ReadAsync(token);
                            await queue.WriteAsync(new Frame(pts - source.Start, pixels), token);
                            pixels = null;
                        }
                        finally
                        {
                            if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
                        }
                    }
                    await process.WaitForExitAsync(token);
                    if (process.ExitCode != 0) throw new InvalidDataException(await diagnostics);
                }
                finally
                {
                    VideoBackgroundSource.Kill(process);
                    await diagnostics;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.ComponentModel.Win32Exception or
                InvalidOperationException or OperationCanceledException)
            {
                if (!token.IsCancellationRequested) Volatile.Write(ref _error, error.Message);
            }
            finally
            {
                queue.TryComplete();
            }
        }

        private static async Task<string> ReadTimestamps(StreamReader reader, ChannelWriter<double> queue, CancellationToken token)
        {
            string message = "Background video decoder failed";
            double timeBase = 0;
            try
            {
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    if (line.StartsWith("[Parsed_showinfo_", StringComparison.Ordinal))
                    {
                        if (VideoBackgroundSource.TryFraction(Field(line, "config in time_base:").TrimEnd(','), '/', out double scale))
                            timeBase = scale;
                        else if (timeBase > 0 && long.TryParse(Field(line, " pts:"), CultureInfo.InvariantCulture, out long pts))
                            queue.TryWrite(pts * timeBase);
                        else if (double.TryParse(Field(line, " pts_time:"), CultureInfo.InvariantCulture, out double seconds))
                            queue.TryWrite(seconds);
                    }
                    else if (!string.IsNullOrWhiteSpace(line))
                        message = line;
                }
            }
            finally
            {
                queue.TryComplete();
            }
            return message;
        }

        private static ReadOnlySpan<char> Field(string line, string key)
        {
            int offset = line.IndexOf(key, StringComparison.Ordinal);
            if (offset < 0) return default;
            var tail = line.AsSpan(offset + key.Length).TrimStart();
            int end = tail.IndexOf(' ');
            return end >= 0 ? tail[..end] : tail;
        }

        public void Dispose() => _ = StopAsync();

        private async Task StopAsync()
        {
            await _stop.CancelAsync();
            try { await _worker; }
            finally
            {
                while (Frames.TryRead(out var frame)) frame.Dispose();
                _stop.Dispose();
            }
        }
    }
}
