using System.Buffers;
using System.Collections.Concurrent;

namespace SharpFAI.Player;

internal sealed class VideoFrameWriter : IDisposable
{
    private readonly VideoExporter.EncoderSession? _encoder;
    private readonly BlockingCollection<byte[]> _frames;
    private readonly Thread _worker;
    private volatile Exception? _error;
    private volatile bool _cancelled;
    private bool _finished;

    public VideoFrameWriter(VideoExporter.EncoderSession? encoder, string? framesDirectory, int width, int height)
    {
        _encoder = encoder;
        int frameBytes = encoder?.FrameBytes ?? checked(width * height * 4);
        _frames = new BlockingCollection<byte[]>(Math.Clamp((32 * 1024 * 1024) / frameBytes, 1, 4));
        _worker = new Thread(() =>
        {
            int index = 0;
            foreach (byte[] pixels in _frames.GetConsumingEnumerable())
            {
                try
                {
                    if (_cancelled || _error is not null)
                        continue;

                    _encoder?.WriteFrame(pixels.AsSpan(0, frameBytes));
                    if (framesDirectory is not null)
                    {
                        string path = Path.Combine(framesDirectory, $"frame{index:D6}.png");
                        FrameCapture.SaveRgba(path, pixels, width, height, alreadyFlipped: false);
                    }
                    index++;
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
        _finished = true;
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
    }
}
