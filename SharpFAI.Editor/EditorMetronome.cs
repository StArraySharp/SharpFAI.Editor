using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor;

internal sealed class EditorMetronome(EditorBeatClock clock, EditorTimeSignatures signatures)
{
    private static readonly float[] BeatClick = CreateClick(1300, .24f);
    private static readonly float[] DownbeatClick = CreateClick(1950, .38f);

    // The mixer requests arbitrary blocks, including a block starting partway through a click after a seek.
    public void Render(long firstFrame, double step, double offset, Span<float> samples)
    {
        samples.Clear();
        int cursor = 0;
        var changes = signatures.Changes;
        while (cursor < samples.Length)
        {
            double time = (firstFrame + cursor + 1e-6) * step + offset;
            double beat = clock.BeatAt(time);
            if (!double.IsFinite(beat)) break;
            if (beat < 0)
            {
                cursor = (int)Math.Clamp(FrameAtBeat(0) - firstFrame, cursor + 1, samples.Length);
                continue;
            }

            int index = signatures.IndexAt(beat);
            var change = changes[index];
            double length = change.Signature.BeatLength;
            double ordinal = Math.Max(0, Math.Floor((beat - change.Beat) / length));
            double nextBeat = change.Beat + (ordinal + 1) * length;
            if (index + 1 < changes.Count)
                nextBeat = Math.Min(nextBeat, changes[index + 1].Beat);
            int end = (int)Math.Clamp(FrameAtBeat(nextBeat) - firstFrame, cursor + 1, samples.Length);

            double onset = FrameAtBeat(change.Beat + ordinal * length) - firstFrame;
            var click = ordinal % change.Signature.Numerator == 0 ? DownbeatClick : BeatClick;
            int start = (int)Math.Clamp(onset, cursor, end);
            int age = (int)Math.Clamp(start - onset, 0, click.Length);
            int count = Math.Min(end - start, click.Length - age);
            click.AsSpan(age, count).CopyTo(samples[start..]);
            cursor = end;
        }

        double FrameAtBeat(double beat) => Math.Ceiling((clock.TimeAt(beat) - offset) / step - 1e-6);
    }

    private static float[] CreateClick(double frequency, float gain)
    {
        int rate = RealtimeModMixer.Format.SampleRate;
        var pulse = new float[rate * 30 / 1000];
        for (int i = 0; i < pulse.Length; i++)
        {
            double time = i / (double)rate;
            double fade = 1 - i / (double)pulse.Length;
            pulse[i] = (float)(Math.Sin(2 * Math.PI * frequency * time) *
                Math.Min(1, time * 1000) * fade * fade * fade * gain);
        }
        return pulse;
    }
}
