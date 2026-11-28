using System.Diagnostics;
using System.Reflection;
using OpenTK.Audio.OpenAL;
using SharpFAI.Player.Engine;
using SharpFAI.Player.ModSupport;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public sealed class TestPlaybackAudioSync
{
    private const int SampleRate = 48000;
    private const int Frames = SampleRate * 8;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Test]
    [Explicit("Requires an OpenAL output device. ALSOFT_DRIVERS=null works for unattended tests.")]
    public void EditorMetronomeStartsOnTheMixedClockEvenWithoutMusicOrHitsounds()
    {
        using var renderer = new LevelRenderer();
        Assert.That(AudioEngine.Initialize(), Is.True);
        typeof(LevelRenderer).GetField("_audioBackendReady", PrivateInstance)!.SetValue(renderer, true);
        typeof(LevelRenderer).GetField("_isPlaying", PrivateInstance)!.SetValue(renderer, true);
        typeof(LevelRenderer).GetField("_playTime", PrivateInstance)!.SetValue(renderer, 2d);
        long firstClickBlock = -1;
        renderer.SetEditorMetronome((firstFrame, step, offset, samples) =>
        {
            Interlocked.CompareExchange(ref firstClickBlock, firstFrame, -1);
            samples.Fill(.2f);
        });

        var sync = typeof(LevelRenderer).GetMethod("SyncAudio", PrivateInstance)!;
        Assert.That(sync.Invoke(renderer, null), Is.False);
        var timer = Stopwatch.StartNew();
        while (sync.Invoke(renderer, null) is false && timer.Elapsed < TimeSpan.FromSeconds(3))
            Thread.Sleep(4);

        var stream = (RealtimeModAudioStream?)typeof(LevelRenderer).GetField("_modAudio", PrivateInstance)!.GetValue(renderer);
        Assert.That(stream?.TryGetPosition(out _), Is.True);
        Assert.That(Interlocked.Read(ref firstClickBlock), Is.EqualTo(2 * RealtimeModMixer.Format.SampleRate));
        renderer.SetEditorMetronome(null);
        Assert.That(sync.Invoke(renderer, null), Is.True);
        Assert.That(typeof(LevelRenderer).GetField("_modAudio", PrivateInstance)!.GetValue(renderer), Is.Null);
    }

    [Test]
    [Explicit("Requires an OpenAL output device. ALSOFT_DRIVERS=null works for unattended tests.")]
    public void StreamedMusicAndHitsoundsStartTogetherAfterASeekAndAnUnderrun()
    {
        using var decoderReady = new ManualResetEventSlim();
        using var renderer = new LevelRenderer();
        using var music = new AudioClip();
        using var hitsound = new AudioClip();
        try
        {
            var open = typeof(DecodedAudio).GetConstructor(PrivateInstance, null,
                [typeof(int), typeof(int), typeof(long), typeof(Func<AudioFrameReader>)], null)!;
            var audio = (DecodedAudio)open.Invoke([1, SampleRate, (long)Frames,
                (Func<AudioFrameReader>)(() =>
                {
                    if (!decoderReady.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test decoder was not released.");
                    return new SilentFrames();
                })]);
            Assert.That(music.LoadDecoded(audio), Is.True);
            Assert.That(hitsound.LoadDecoded(new DecodedAudio
                { Pcm = new byte[Frames * sizeof(short)], Channels = 1, SampleRate = SampleRate }), Is.True);

            SetField("_audioAvailable", true);
            SetField("_isPlaying", true);
            SetField("_music", music);
            SetField("_hitsound", hitsound);
            SetField("_hitsoundShiftMs", 80f);
            SetField("_playTime", 2d);

            bool Sync() => (bool)typeof(LevelRenderer).GetMethod("SyncAudio", PrivateInstance)!.Invoke(renderer, null)!;
            Assert.That(Sync(), Is.False);
            Assert.That(music.IsOutputPlaying || hitsound.IsOutputPlaying, Is.False);

            // A second seek replaces queues that were prepared but never started.
            SetField("_playTime", 3d);
            SetField("_audioNeedsResync", true);
            Assert.That(Sync(), Is.False);
            Assert.That(hitsound.Position, Is.EqualTo(3.08).Within(.001));
            StartAndVerify();

            // Stop the music with unplayed data remaining to exercise the underrun path.
            decoderReady.Reset();
            int source = (int)typeof(AudioClip).GetField("_source", PrivateInstance)!.GetValue(music)!;
            AL.SourceStop(source);
            Assert.That(Sync(), Is.False);
            Assert.That(music.IsOutputPlaying || hitsound.IsOutputPlaying, Is.False);
            StartAndVerify();
            Assert.That(AL.GetError(), Is.EqualTo(ALError.NoError));

            void StartAndVerify()
            {
                decoderReady.Set();
                var clock = Stopwatch.StartNew();
                while (!music.IsOutputPlaying && clock.Elapsed < TimeSpan.FromSeconds(3))
                {
                    Sync();
                    Thread.Sleep(4);
                }
                Assert.That(music.IsOutputPlaying && hitsound.IsOutputPlaying, Is.True);
                Assert.That(hitsound.Position - music.Position, Is.EqualTo(.08).Within(.02));
            }
        }
        finally
        {
            decoderReady.Set();
        }

        void SetField(string name, object value) =>
            typeof(LevelRenderer).GetField(name, PrivateInstance)!.SetValue(renderer, value);
    }

    private sealed class SilentFrames : AudioFrameReader
    {
        private long _position;
        public override long Position => _position;
        public override void Seek(long frame) => _position = frame;
        public override int Read(Span<short> samples)
        {
            int count = (int)Math.Min(samples.Length, Frames - _position);
            samples[..count].Clear();
            _position += count;
            return count;
        }
        public override void Dispose() { }
    }
}
