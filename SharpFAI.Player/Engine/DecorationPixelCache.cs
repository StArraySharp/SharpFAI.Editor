using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

internal enum DecorationPixelStatus { Ready, Waiting, Unavailable }

/// <summary>Background decoding and a byte-bounded LRU of file-backed decoration pixels.</summary>
internal sealed class DecorationPixelCache(long capacity = 256L * 1024 * 1024)
{
    private sealed class Entry(string name, string path, int percent, DecorationImage layout)
    {
        public readonly string Name = name;
        public readonly string Path = path;
        public readonly int Percent = percent;
        public readonly DecorationImage Layout = layout;
        public DecorationImage? Pixels;
        public Box2? AlphaBounds;
        public bool Failed;
        public Task<DecorationImage?>? Decoding;
        public LinkedListNode<Entry>? LastUse;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _sources = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recent = new();
    private readonly Queue<string> _warnings = new();
    private long _bytes;
    private int _speculativeCount;
    private static readonly int PrefetchWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    public long CachedBytes { get { lock (_gate) return _bytes; } }

    // Sources are registered during chart construction, before the timeline is published.
    public void Add(string name, string path, int percent, DecorationImage layout) =>
        _sources.Add(name, new Entry(name, path, percent, layout));

    public DecorationPixelStatus Prefetch(string name, out DecorationImage pixels, bool currentFrame = false) =>
        Read(name, false, currentFrame, out pixels);

    public bool ReadRequired(string name, out DecorationImage pixels) =>
        Read(name, true, true, out pixels) == DecorationPixelStatus.Ready;

    private DecorationPixelStatus Read(string name, bool required, bool currentFrame, out DecorationImage pixels)
    {
        pixels = default;
        if (!_sources.TryGetValue(name, out var entry))
            return DecorationPixelStatus.Unavailable;

        Task<DecorationImage?> task;
        lock (_gate)
        {
            if (entry.Pixels is { } cached)
            {
                pixels = cached;
                _recent.Remove(entry.LastUse!);
                _recent.AddFirst(entry.LastUse!);
                return DecorationPixelStatus.Ready;
            }
            if (entry.Failed || (!required && entry.Layout.StorageBytes > capacity))
                return DecorationPixelStatus.Unavailable;
            if (entry.Decoding is null)
            {
                // Leave one worker slot for the actual target frame when lookahead is already busy.
                if (!required && _speculativeCount >= PrefetchWorkers + (currentFrame ? 1 : 0))
                    return DecorationPixelStatus.Waiting;
                if (!required)
                    _speculativeCount++;
                entry.Decoding = Task.Run(() => Decode(entry, speculative: !required));
            }
            task = entry.Decoding;
        }
        if (!required)
            return DecorationPixelStatus.Waiting;
        if (task.GetAwaiter().GetResult() is not { } decoded)
            return DecorationPixelStatus.Unavailable;
        pixels = decoded;
        return DecorationPixelStatus.Ready;
    }

    public Box2? AlphaBounds(string name)
    {
        if (!_sources.TryGetValue(name, out var entry))
            return null;
        lock (_gate) return entry.AlphaBounds;
    }

    public void DrainWarnings(List<string> destination)
    {
        lock (_gate)
            while (_warnings.TryDequeue(out string? warning))
                destination.Add(warning);
    }

    private DecorationImage? Decode(Entry entry, bool speculative)
    {
        DecorationImage? decoded = null;
        string? failure = null;
        try
        {
            var image = DecorationImage.ReadFile(entry.Path, entry.Percent);
            if (image.LayoutWidth != entry.Layout.LayoutWidth || image.LayoutHeight != entry.Layout.LayoutHeight)
                throw new InvalidDataException("图片尺寸在关卡加载后发生变化。");
            decoded = image;
        }
        catch (Exception error) when (IsReadError(error))
        {
            failure = error.Message;
        }
        finally
        {
            lock (_gate)
            {
                if (decoded is { } image)
                {
                    entry.AlphaBounds = image.AlphaBounds;
                    long bytes = image.Pixels.LongLength;
                    if (bytes <= capacity)
                    {
                        while (_bytes + bytes > capacity && _recent.Last is { } oldest)
                        {
                            var evicted = oldest.Value;
                            _bytes -= evicted.Pixels!.Value.Pixels.LongLength;
                            evicted.Pixels = null;
                            evicted.LastUse = null;
                            _recent.RemoveLast();
                        }
                        entry.Pixels = image;
                        entry.LastUse = _recent.AddFirst(entry);
                        _bytes += bytes;
                    }
                }
                if (failure is not null)
                {
                    entry.Failed = true;
                    _warnings.Enqueue($"无法读取装饰贴图 {entry.Name}：{failure}");
                }
                entry.Decoding = null;
                if (speculative)
                    _speculativeCount--;
            }
        }
        return decoded;
    }

    internal static bool IsReadError(Exception error) => error is IOException or InvalidDataException or ArgumentException or
        UnauthorizedAccessException or SixLabors.ImageSharp.ImageFormatException or NotSupportedException;
}
