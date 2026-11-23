using SharpFAI.Player.Engine.Audio;

namespace SharpFAI.Player.Tests;

public class TestDenseHitsoundMixer
{
    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(2, false)]
    [TestCase(2, true)]
    public void OverlapSaveMatchesDirectMixAcrossBlockBoundaries(int channels, bool weighted)
    {
        const int frames = 7;
        const int start = 200;
        const int count = 16;
        int[] notes = [195, 197, 200, 200, 207, 215];
        float[] kick = Enumerable.Range(0, frames * channels).Select(i => MathF.Sin(i * .7f) / 8f).ToArray();
        float[] gains = weighted ? [.5f, .125f, .3f, .2f, .8f, 1f] : [];
        float[] actual = new float[count * channels];
        new DenseHitsoundMixer(kick, channels, size: 32).Mix(notes, start, actual, gains);

        float[] expected = new float[actual.Length];
        for (int i = 0; i < notes.Length; i++)
        {
            int note = notes[i];
            float gain = weighted ? gains[i] : 1f;
            for (int frame = Math.Max(note, start); frame < Math.Min(note + frames, start + count); frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    expected[(frame - start) * channels + channel] += kick[(frame - note) * channels + channel] * gain;
                }
            }
        }
        Assert.That(actual, Is.EqualTo(expected).Within(1e-6));
    }
}
