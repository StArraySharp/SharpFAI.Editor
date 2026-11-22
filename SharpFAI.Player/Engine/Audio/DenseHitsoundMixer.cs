using System.Buffers;
using System.Numerics;

namespace SharpFAI.Player.Engine.Audio;

/// <summary>Overlap-save convolution for blocks where direct sample placement would be more expensive.</summary>
internal sealed class DenseHitsoundMixer
{
    private readonly int _size;
    private readonly int _history;
    private readonly int _channels;
    private readonly int[] _reverse;
    private readonly Complex[] _roots;
    private readonly Complex[] _kernel;

    public DenseHitsoundMixer(float[] kick, int channels, int size)
    {
        _size = size;
        _channels = channels;
        int frames = kick.Length / channels;
        _history = frames - 1;
        _reverse = new int[size];
        _roots = new Complex[size / 2];
        _kernel = new Complex[size];
        for (int i = 1; i < size; i++)
        {
            _reverse[i] = (_reverse[i >> 1] >> 1) | ((i & 1) * (size >> 1));
        }
        for (int i = 0; i < _roots.Length; i++)
        {
            (double sine, double cosine) = Math.SinCos(-Math.Tau * i / size);
            _roots[i] = new Complex(cosine, sine);
        }
        for (int i = 0; i < frames; i++)
        {
            _kernel[i] = new Complex(kick[i * channels], channels == 2 ? kick[i * channels + 1] : 0);
        }
        Transform(_kernel, inverse: false);
    }

    public void Mix(ReadOnlySpan<int> notes, int start, Span<float> destination)
    {
        Complex[] buffer = ArrayPool<Complex>.Shared.Rent(_size);
        try
        {
            Span<Complex> spectrum = buffer.AsSpan(0, _size);
            spectrum.Clear();
            int first = start - _history;
            foreach (int note in notes)
            {
                spectrum[note - first] += Complex.One;
            }

            Transform(spectrum, inverse: false);
            for (int i = 0; i < _size; i++)
            {
                spectrum[i] *= _kernel[i];
            }
            Transform(spectrum, inverse: true);

            double normalizer = 1.0 / _size;
            int frames = destination.Length / _channels;
            for (int i = 0; i < frames; i++)
            {
                Complex sample = spectrum[_history + i];
                destination[i * _channels] = (float)(sample.Real * normalizer);
                if (_channels == 2)
                {
                    destination[i * _channels + 1] = (float)(sample.Imaginary * normalizer);
                }
            }
        }
        finally
        {
            ArrayPool<Complex>.Shared.Return(buffer);
        }
    }

    private void Transform(Span<Complex> data, bool inverse)
    {
        for (int i = 0; i < data.Length; i++)
        {
            int other = _reverse[i];
            if (other > i)
            {
                (data[i], data[other]) = (data[other], data[i]);
            }
        }

        for (int width = 2; width <= _size; width *= 2)
        {
            int half = width / 2;
            int stride = _size / width;
            for (int start = 0; start < _size; start += width)
            {
                for (int i = 0; i < half; i++)
                {
                    Complex root = _roots[i * stride];
                    Complex even = data[start + i];
                    Complex odd = data[start + i + half] * (inverse ? Complex.Conjugate(root) : root);
                    data[start + i] = even + odd;
                    data[start + i + half] = even - odd;
                }
            }
        }
    }
}
