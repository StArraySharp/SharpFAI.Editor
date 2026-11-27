using System.Numerics;
using System.Runtime.InteropServices;
using Thrust.Audio;

namespace SharpFAI.Editor.Widgets;

internal sealed class AudioMonitorAnalysis
{
    internal const int SampleCount = 4096;
    internal const int MinFrequency = 20;
    internal const float MinimumLevel = -90;

    internal readonly record struct Peak(float Frequency, float Level);

    private readonly float[] _window = new float[SampleCount];
    private readonly int[] _bitReversal = new int[SampleCount];
    private readonly Complex[] _roots = new Complex[SampleCount / 2];
    private readonly Complex[] _fft = new Complex[SampleCount];
    private readonly float[] _levels = new float[SampleCount / 2 + 1];
    private readonly List<Peak> _peaks = [];
    private readonly double _windowSum;

    internal ReadOnlySpan<float> Levels => _levels;
    internal IReadOnlyList<Peak> Peaks => _peaks;

    public AudioMonitorAnalysis()
    {
        for (int i = 0; i < _window.Length; i++)
        {
            _window[i] = .5f - .5f * MathF.Cos(2 * MathF.PI * i / (SampleCount - 1));
            _windowSum += _window[i];
            // Reverse the FFT index bits using the previously computed index for i / 2.
            _bitReversal[i] = (_bitReversal[i / 2] >> 1) | ((i & 1) * (SampleCount / 2));
        }
        for (int i = 0; i < _roots.Length; i++)
            _roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / SampleCount);
    }

    internal void Analyze(DecodedAudio audio, double seconds)
    {
        var pcm = MemoryMarshal.Cast<byte, short>(audio.Pcm);
        int frameCount = pcm.Length / audio.Channels;
        int center = (int)Math.Clamp(Math.Round(seconds * audio.SampleRate), 0, frameCount);
        int first = center - SampleCount / 2;
        Array.Clear(_levels);
        for (int channel = 0; channel < audio.Channels; channel++)
        {
            for (int i = 0; i < SampleCount; i++)
            {
                int frame = first + i;
                float value = (uint)frame < (uint)frameCount ? pcm[frame * audio.Channels + channel] / 32768f : 0;
                _fft[_bitReversal[i]] = value * _window[i];
            }

            Transform();
            // Add channel power so tones remain visible even with opposite phases in stereo.
            for (int i = 0; i < _levels.Length; i++)
            {
                var bin = _fft[i];
                _levels[i] += (float)(bin.Real * bin.Real + bin.Imaginary * bin.Imaginary);
            }
        }

        double normalization = 4 / (_windowSum * _windowSum * audio.Channels);
        for (int i = 0; i < _levels.Length; i++)
        {
            double power = _levels[i] * normalization;
            if (i == 0 || i == _levels.Length - 1) power *= .25;
            _levels[i] = (float)(10 * Math.Log10(Math.Max(power, 1e-16)));
        }
        FindPeaks((float)audio.SampleRate / SampleCount);
    }

    private void Transform()
    {
        for (int half = 1; half < SampleCount; half *= 2)
        {
            int stride = SampleCount / (2 * half);
            for (int start = 0; start < SampleCount; start += 2 * half)
            {
                for (int i = 0; i < half; i++)
                {
                    int low = start + i, high = low + half;
                    Complex odd = _fft[high] * _roots[i * stride];
                    Complex even = _fft[low];
                    _fft[low] = even + odd;
                    _fft[high] = even - odd;
                }
            }
        }
    }

    private void FindPeaks(float hertzPerBin)
    {
        _peaks.Clear();
        for (int i = 1; i + 1 < _levels.Length; i++)
        {
            float left = _levels[i - 1], level = _levels[i], right = _levels[i + 1];
            if (level < MinimumLevel || level <= left || level < right) continue;
            // Interpolate between bins so frequencies are not restricted to FFT-bin centers.
            float offset = .5f * (left - right) / (left - 2 * level + right);
            float frequency = (i + offset) * hertzPerBin;
            if (frequency >= MinFrequency)
                _peaks.Add(new(frequency, level - .25f * (left - right) * offset));
        }
        _peaks.Sort(static (a, b) => b.Level.CompareTo(a.Level));
    }
}
