using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenTK.Audio.OpenAL;

namespace SharpFAI.Player.ModSupport;

/// <summary>Produces bounded PCM blocks on a worker and queues them on the OpenAL owner thread.</summary>
internal sealed class RealtimeModAudioStream : IDisposable
{
    private const int BufferCount = 6;
    private const int Frames = RealtimeModMixer.BlockFrames;
    private readonly int _source;
    private readonly int[] _buffers = new int[BufferCount];
    private readonly Queue<int> _available = new(BufferCount);
    private readonly ConcurrentQueue<short[]> _ready = new();
    private readonly Channel<short[]> _free = Channel.CreateBounded<short[]>(BufferCount);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _producer;
    private long _gains;
    private long _endFrame;
    private bool _started;
    private bool _disposed;

    public RealtimeModAudioStream(IReadOnlyList<ModRegistration<RealtimeAudioHook>> effects,
        ModMixSources sources, double seconds, ModMixGains gains)
    {
        _endFrame = Math.Max(0, (long)Math.Round(seconds * RealtimeModMixer.Format.SampleRate));
        SetGains(gains);
        _source = AL.GenSource();
        try
        {
            if (_source == 0) throw new InvalidOperationException("OpenAL could not allocate a DSP source.");
            AL.Source(_source, ALSourceb.Looping, false);
            for (int i = 0; i < BufferCount; i++)
            {
                _buffers[i] = AL.GenBuffer();
                if (_buffers[i] == 0) throw new InvalidOperationException("OpenAL could not allocate a DSP buffer.");
                _available.Enqueue(_buffers[i]);
                _free.Writer.TryWrite(new short[Frames * 2]);
            }
        }
        catch
        {
            DeleteBuffers();
            _cancellation.Dispose();
            throw;
        }
        long first = _endFrame;
        _producer = Task.Run(() => Produce(effects, sources, first, _cancellation.Token));
    }

    public bool Failed => _producer.IsFaulted;

    public void SetGains(ModMixGains gains)
    {
        long bits = (uint)BitConverter.SingleToInt32Bits(gains.Music) |
                    ((long)BitConverter.SingleToInt32Bits(gains.Hitsounds) << 32);
        Interlocked.Exchange(ref _gains, bits);
    }

    private ModMixGains ReadGains()
    {
        long bits = Interlocked.Read(ref _gains);
        return new(BitConverter.Int32BitsToSingle((int)bits), BitConverter.Int32BitsToSingle((int)(bits >> 32)));
    }

    private async Task Produce(IReadOnlyList<ModRegistration<RealtimeAudioHook>> effects,
        ModMixSources sources, long firstFrame, CancellationToken token)
    {
        using var mixer = new RealtimeModMixer(effects, sources, firstFrame, offline: false);
        try
        {
            while (!token.IsCancellationRequested)
            {
                short[] samples = await _free.Reader.ReadAsync(token).ConfigureAwait(false);
                mixer.Render(firstFrame, samples, ReadGains());
                _ready.Enqueue(samples);
                firstFrame += Frames;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public unsafe void Pump()
    {
        AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out int processed);
        while (processed-- > 0)
        {
            int buffer = AL.SourceUnqueueBuffer(_source);
            if (buffer != 0) _available.Enqueue(buffer);
        }
        while (_available.Count > 0 && _ready.TryDequeue(out var samples))
        {
            int buffer = _available.Dequeue();
            fixed (short* samplesPointer = samples)
                AL.BufferData(buffer, ALFormat.Stereo16, (nint)samplesPointer,
                    samples.Length * sizeof(short), RealtimeModMixer.Format.SampleRate);
            AL.SourceQueueBuffer(_source, buffer);
            _free.Writer.TryWrite(samples);
            _endFrame += Frames;
        }
        AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
        if ((ALSourceState)state != ALSourceState.Playing && _available.Count <= BufferCount - 2)
        {
            AL.SourcePlay(_source);
            _started = true;
        }
    }

    public bool TryGetPosition(out double seconds)
    {
        seconds = 0;
        if (!_started || _available.Count == BufferCount) return false;
        AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
        if ((ALSourceState)state != ALSourceState.Playing) return false;
        AL.GetSource(_source, ALGetSourcei.SampleOffset, out int offset);
        seconds = (_endFrame - (BufferCount - _available.Count) * Frames + offset) /
                  (double)RealtimeModMixer.Format.SampleRate;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        DeleteBuffers();
        _ = _producer.ContinueWith(task =>
        {
            _ = task.Exception;
            _cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void DeleteBuffers()
    {
        if (_source != 0)
        {
            AL.SourceStop(_source);
            AL.DeleteSource(_source);
        }
        foreach (int buffer in _buffers)
            if (buffer != 0) AL.DeleteBuffer(buffer);
    }
}
