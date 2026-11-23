using Thrust.Audio;

namespace SharpFAI.Player.Engine.Audio;

internal sealed class HitsoundPcmCache(long maxBytes, int maxEntries)
{
    internal readonly record struct Key(string Identity, float Volume, long? RateBits = null);
    private sealed record Entry(Key Key, DecodedAudio Audio);

    private readonly Lock _gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = new();
    private readonly Dictionary<Key, Lazy<DecodedAudio?>> _pending = [];
    private long _bytes;

    public DecodedAudio? GetOrAdd(Key key, Func<DecodedAudio?> synthesize)
    {
        Lazy<DecodedAudio?> work;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _recent.Remove(entry);
                _recent.AddFirst(entry);
                return entry.Value.Audio;
            }

            if (!_pending.TryGetValue(key, out work!))
                _pending.Add(key, work = new Lazy<DecodedAudio?>(synthesize));
        }

        DecodedAudio? audio = null;
        try
        {
            return audio = work.Value;
        }
        finally
        {
            lock (_gate)
            {
                // Only the first finisher publishes; Clear() also detaches in-flight work.
                if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, work))
                {
                    _pending.Remove(key);
                    if (audio is not null && audio.Pcm.LongLength <= maxBytes && maxEntries > 0)
                    {
                        long cost = audio.Pcm.LongLength;
                        while (_bytes > maxBytes - cost || _entries.Count >= maxEntries)
                        {
                            Entry oldest = _recent.Last!.Value;
                            _recent.RemoveLast();
                            _entries.Remove(oldest.Key);
                            _bytes -= oldest.Audio.Pcm.LongLength;
                        }

                        _entries.Add(key, _recent.AddFirst(new Entry(key, audio)));
                        _bytes += cost;
                    }
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recent.Clear();
            _pending.Clear();
            _bytes = 0;
        }
    }
}
