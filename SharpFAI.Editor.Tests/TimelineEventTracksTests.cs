using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor.Tests;

public sealed class TimelineEventTracksTests
{
    [Test]
    public void TracksFilterIndependentlyAndUseTheAuthoredTriggerTime()
    {
        var chart = new EditorDocument();
        var camera = JObject.Parse("""{"floor":2,"eventType":"MoveCamera","angleOffset":90}""");
        var filter = JObject.Parse("""{"floor":2,"eventType":"SetFilter","angleOffset":180,"disabled":{"angleOffset":true},"active":false}""");
        chart.Add(JObject.Parse("""{"floor":1,"eventType":"SetSpeed"}"""), decoration: false);
        chart.Add(camera, decoration: false);
        chart.Add(filter, decoration: false);
        chart.Add(JObject.Parse("""{"floor":0,"eventType":"AddDecoration"}"""), decoration: true);
        var timing = new LevelRenderer.PlaybackTimeline { EntryTimes = [-.5, 0, .5], FloorBpms = [120, 120, 240] };

        var full = TimelineEventTracks.Build(chart, timing, showTiming: true, showEffects: true, duration: 2);
        Assert.That(full.Select(track => track.Label), Is.EqualTo(new[]
            { "TimelineTimingEvents", "TimelineCameraEvents", "Decorations", "TimelineEffectEvents" }));
        Assert.That(full[0].Events.Single().Type, Is.EqualTo("SetSpeed"));
        Assert.That(full[1].Events.Single().Time, Is.EqualTo(.625));
        Assert.That(full[1].Events.Single().Source, Is.SameAs(camera));
        Assert.That(full[2].Events.Single().Time, Is.EqualTo(0));
        Assert.That(full[3].Events.Single().Time, Is.EqualTo(.5));
        Assert.That(full[3].Events.Single().Enabled, Is.False);
        Assert.That(TimelineEventTracks.Build(chart, timing, true, false, 2), Has.Length.EqualTo(1));
        Assert.That(TimelineEventTracks.Build(chart, timing, false, true, 2), Has.Length.EqualTo(3));
        Assert.That(TimelineEventTracks.Build(chart, timing, false, false, 2), Is.Empty);
    }
}
