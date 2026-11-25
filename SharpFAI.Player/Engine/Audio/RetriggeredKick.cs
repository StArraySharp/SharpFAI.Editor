namespace SharpFAI.Player.Engine.Audio;

internal sealed class RetriggeredKick
{
    private readonly record struct Hit(int Start, int Length, float Gain);

    private readonly float[] _kick;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly Hit[] _hits;

    private RetriggeredKick(float[] kick, int channels, int sampleRate, Hit[] hits)
    {
        _kick = kick;
        _channels = channels;
        _sampleRate = sampleRate;
        _hits = hits;
    }

    public static RetriggeredKick? Prepare(float[] kick, int channels, int sampleRate, ref int[] starts, out float[]? gains)
    {
        gains = null;
        int full = Math.Min(Math.Max(1, sampleRate * 48 / 1000), kick.Length / channels);
        int compact = Math.Min(sampleRate * 25 / 1000, full - 1);
        int first = 0;
        while (first + 1 < starts.Length && starts[first + 1] - starts[first] >= full)
            first++;
        if (first + 1 >= starts.Length)
            return null;

        List<Hit> shortHits = [];
        List<int> longStarts = [];
        List<float> longGains = [];
        for (int i = 0; i < starts.Length;)
        {
            int start = starts[i];
            int next = i + 1;
            while (next < starts.Length && starts[next] == start)
                next++;

            // Coincident hits use the same envelope and sum at the same sample.
            int count = next - i;
            int gap = next < starts.Length ? starts[next] - start : full;
            float blend = SmoothStep((full - gap) / (float)(full - compact));
            if (blend > 0f)
                shortHits.Add(new Hit(start, gap, count * blend));
            if (blend < 1f)
            {
                longStarts.Add(start);
                longGains.Add(count * (1f - blend));
            }
            i = next;
        }
        starts = longStarts.ToArray();
        gains = longGains.ToArray();
        return new RetriggeredKick(kick, channels, sampleRate, shortHits.ToArray());
    }

    public int FindFirstHit(int start, int end)
    {
        int lo = 0, hi = _hits.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_hits[mid].Start + _hits[mid].Length <= start)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo < _hits.Length && _hits[lo].Start < end ? lo : -1;
    }

    public void Mix(int first, int start, int end, Span<float> destination)
    {
        for (int i = first; i < _hits.Length && _hits[i].Start < end; i++)
        {
            Hit hit = _hits[i];
            float attack = Math.Min(_sampleRate * .00035f, hit.Length / 8f);
            float release = Math.Min(_sampleRate * .004f, hit.Length / 3f);
            // Skip the quiet lead-in of the kick when a full attack will not fit. Keep the
            // source stride at one: shortening a voice must not transpose the sample.
            int sourceFrames = _kick.Length / _channels;
            int attackOffset = Math.Min(_sampleRate * 45 / 10000, Math.Max(0, sourceFrames - hit.Length));
            float gapMs = 1000f * hit.Length / _sampleRate;
            int sourceStart = (int)MathF.Round(attackOffset * SmoothStep((12f - gapMs) / 10f));
            int from = Math.Max(start - hit.Start, 0);
            int to = Math.Min(end - hit.Start, hit.Length);
            for (int frame = from; frame < to; frame++)
            {
                // A one- or two-sample voice has no interior samples for a fade.
                float gain = hit.Length < 3 ? hit.Gain :
                    hit.Gain * SmoothStep(frame / attack) * SmoothStep((hit.Length - 1 - frame) / release);
                int offset = (hit.Start + frame - start) * _channels;
                int source = (sourceStart + frame) * _channels;
                for (int channel = 0; channel < _channels; channel++)
                    destination[offset + channel] += _kick[source + channel] * gain;
            }
        }
    }

    private static float SmoothStep(float value)
    {
        float t = Math.Clamp(value, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
