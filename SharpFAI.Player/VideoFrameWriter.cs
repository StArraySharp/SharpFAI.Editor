using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;

namespace SharpFAI.Player;

internal sealed class VideoFrameWriter : IDisposable
{
    private readonly VideoExporter.EncoderSession? _encoder;
    private readonly BlockingCollection<byte[]> _frames;
    private readonly Thread _worker;
    private readonly string? _framesDirectory;
    private readonly string? _stagingDirectory;
    private volatile Exception? _error;
    private volatile bool _cancelled;
    private bool _finished;
    private int _writtenFrames;

    public VideoFrameWriter(VideoExporter.EncoderSession? encoder, string? framesDirectory, int width, int height)
    {
        _encoder = encoder;
        _framesDirectory = framesDirectory;
        if (framesDirectory is not null)
        {
            _stagingDirectory = Path.Combine(Path.GetDirectoryName(framesDirectory)!, $".sharpfai-frames-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_stagingDirectory);
        }
        int frameBytes = encoder?.FrameBytes ?? checked(width * height * 4);
        _frames = new BlockingCollection<byte[]>(Math.Clamp((32 * 1024 * 1024) / frameBytes, 1, 4));
        _worker = new Thread(() =>
        {
            foreach (byte[] pixels in _frames.GetConsumingEnumerable())
            {
                try
                {
                    if (_cancelled || _error is not null)
                        continue;

                    _encoder?.WriteFrame(pixels.AsSpan(0, frameBytes));
                    if (_stagingDirectory is not null)
                    {
                        string path = Path.Combine(_stagingDirectory, $"frame{_writtenFrames:D6}.png");
                        FrameCapture.SaveRgba(path, pixels, width, height, alreadyFlipped: false);
                    }
                    _writtenFrames++;
                }
                catch (Exception ex)
                {
                    _error = ex;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                }
            }
        })
        {
            IsBackground = true,
            Name = "VideoFrameWriter",
        };
        _worker.Start();
    }

    public bool Failed => _error is not null;

    public void Enqueue(byte[] pixels) => _frames.Add(pixels);

    public void Finish()
    {
        _frames.CompleteAdding();
        _worker.Join();
        _encoder?.Finish(publish: _error is null);
        if (_error is { } error)
            throw new InvalidOperationException(error.Message, error);
        if (_framesDirectory is { } destination && _stagingDirectory is { } staging)
            PublishFrames(staging, destination);
        _finished = true;
    }

    private void PublishFrames(string staging, string destination)
    {
        if (!Directory.Exists(destination))
        {
            Directory.Move(staging, destination);
            return;
        }

        foreach (string file in Directory.EnumerateFiles(staging))
            File.Move(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (string file in Directory.EnumerateFiles(destination, "frame*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length >= 11 && name.StartsWith("frame", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                index >= _writtenFrames && name.Equals($"frame{index:D6}", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        }
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _cancelled = true;
            _encoder?.Cancel();
            _frames.CompleteAdding();
            _worker.Join();
        }
        _encoder?.Dispose();
        _frames.Dispose();
        if (_stagingDirectory is { } staging && Directory.Exists(staging))
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
