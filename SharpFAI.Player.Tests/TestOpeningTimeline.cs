using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Audio;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Play;
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

    [TestCase(false)]
    [TestCase(true)]
    public void OpeningTileTempoExcludesTheCountdownAndUsesPlaybackSpeed(bool separateCountdown)
    {
        Level level = Chart(90, offset: 750, separateCountdown);
        var floors = level.CreateFloors();
        var track = new ManualTrack(floors, LevelRenderer.BuildTimeline(level, floors), []);
        var tempo = track.TempoAt(0)!.Value;
        Assert.That(tempo.Authored, Is.EqualTo(120));
        Assert.That(tempo.Effective, Is.EqualTo(360).Within(.0001));
    }

    [Test]
    public void TileTempoUsesChartSpeedAndTheTimeToTheNextTile()
    {
        Level level = new(new Dictionary<string, object>
        {
            ["angleData"] = new JArray(0, 90, 90, 999, 90, 90),
            ["settings"] = new JObject { ["bpm"] = 120, ["pitch"] = 50, ["countdownTicks"] = 1 },
            ["actions"] = new JArray(
                JObject.Parse("""{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":180}"""),
                JObject.Parse("""{"floor":2,"eventType":"Pause","duration":1}""")),
        });
        var events = PlaybackEventBinder.BindForPlayback(level);
        var floors = level.CreateFloors();
        var track = new ManualTrack(floors, LevelRenderer.BuildTimeline(level, floors, events), events);

        var corner = track.TempoAt(1)!.Value;
        Assert.That(corner.Authored, Is.EqualTo(180));
        Assert.That(corner.Effective, Is.EqualTo(180).Within(1e-6));
        Assert.That(track.TempoAt(2)!.Value.Effective, Is.EqualTo(45).Within(1e-6));
        Assert.That(track.TempoAt(3)!.Value.Effective, Is.Null);
        Assert.That(track.TempoAt(track.LastFloor)!.Value.Effective, Is.Null);
        Assert.That(track.TempoAt(-1), Is.Null);
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
