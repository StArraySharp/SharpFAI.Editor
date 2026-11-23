using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Audio;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Tests;

public class TestOpeningTimeline
{
    [TestCase(0, .75)]
    [TestCase(90, .50)]
    [TestCase(-90, 1.00)]
    public void CountdownPreservesTheFirstSweepOnTheMusicClock(int angle, double firstHit)
    {
        Level level = Chart(angle, offset: 750, separateCountdown: true);
        List<Floor> floors = level.CreateFloors();
        LevelRenderer.PlaybackTimeline timeline = LevelRenderer.BuildTimeline(level, floors);

        Assert.That(timeline.EntryTimes[1], Is.EqualTo(firstHit).Within(1e-7));
        Assert.That(timeline.EntryTimes[2], Is.EqualTo(1.25).Within(1e-7));
        Assert.That(HitsoundSynth.HitsoundTimes(timeline.EntryTimes)[0], Is.EqualTo(timeline.EntryTimes[1]));
        Assert.That(timeline.SweepStart[0] + timeline.SweepDelta[0], Is.EqualTo(angle).Within(1e-7));

        double radians = (timeline.SweepStart[0] + timeline.SweepDelta[0]) * Math.PI / 180;
        var landing = new System.Numerics.Vector2((float)Math.Cos(radians), (float)Math.Sin(radians))
            * (Floor.length * 2);
        Assert.That(System.Numerics.Vector2.Distance(landing, floors[1].position - floors[0].position),
            Is.LessThan(.001f));
    }

    [Test]
    public void CountdownInTheMusicKeepsTheFullChartLeadIn()
    {
        Level level = Chart(90, offset: 750, separateCountdown: false);
        LevelRenderer.PlaybackTimeline timeline = LevelRenderer.BuildTimeline(level, level.CreateFloors());

        Assert.That(timeline.EntryTimes[1], Is.EqualTo(2.50).Within(1e-7));
        Assert.That(HitsoundSynth.HitsoundTimes(timeline.EntryTimes)[0], Is.EqualTo(2.50).Within(1e-7));
    }

    [Test]
    public void LandingBeforeTheClipDoesNotAddAHitAtSampleZero()
    {
        Level level = Chart(90, offset: 100, separateCountdown: true);
        LevelRenderer.PlaybackTimeline timeline = LevelRenderer.BuildTimeline(level, level.CreateFloors());

        Assert.That(timeline.EntryTimes[1], Is.EqualTo(-.15).Within(1e-7));
        Assert.That(timeline.EntryTimes[2], Is.EqualTo(.60).Within(1e-7));
        Assert.That(HitsoundSynth.HitsoundTimes(timeline.EntryTimes)[0], Is.EqualTo(.60).Within(1e-7));
    }

    private static Level Chart(int angle, int offset, bool separateCountdown) => new(new Dictionary<string, object>
    {
        ["angleData"] = new JArray(angle, 0, 0),
        ["settings"] = new JObject
        {
            ["bpm"] = 120,
            ["pitch"] = 150,
            ["countdownTicks"] = 4,
            ["offset"] = offset,
            ["separateCountdownTime"] = separateCountdown,
        },
        ["actions"] = new JArray(),
    });
}
