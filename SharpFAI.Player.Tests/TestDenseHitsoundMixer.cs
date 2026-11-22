using SharpFAI.Player.Engine.Audio;

namespace SharpFAI.Player.Tests;

public class TestDenseHitsoundMixer
{
    [TestCase(1)]
    [TestCase(2)]
    public void OverlapSaveMatchesDirectMixAcrossBlockBoundaries(int channels)
    {
        const int frames = 7;
        const int start = 200;
        const int count = 16;
        int[] notes = [195, 197, 200, 200, 207, 215];
        float[] kick = Enumerable.Range(0, frames * channels).Select(i => MathF.Sin(i * .7f) / 8f).ToArray();
        float[] actual = new float[count * channels];
        new DenseHitsoundMixer(kick, channels, size: 32).Mix(notes, start, actual);

        float[] expected = new float[actual.Length];
        foreach (int note in notes)
        {
            for (int frame = Math.Max(note, start); frame < Math.Min(note + frames, start + count); frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    expected[(frame - start) * channels + channel] += kick[(frame - note) * channels + channel];
                }
            }
        }
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6));
    }
}
