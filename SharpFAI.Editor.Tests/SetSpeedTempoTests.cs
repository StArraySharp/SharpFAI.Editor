using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Editor.Tests;

public sealed class SetSpeedTempoTests
{
    [TestCase("Bpm", "beatsPerMinute", 150)]
    [TestCase("Multiplier", "bpmMultiplier", 1.25)]
    public void CornerConvertsToTheEventsExistingSpeedMode(string mode, string field, double expected)
    {
        var chart = Chart(90, pitch: 100,
            $$"""{"floor":1,"eventType":"SetSpeed","speedType":"{{mode}}","beatsPerMinute":120,"bpmMultiplier":1}""");
        CheckConversion(chart, 0, 300, field, expected);
    }

    [Test]
    public void SpeedOffsetAccountsForTimeAtThePreviousBpmAndRejectsUnreachableTargets()
    {
        var chart = Chart(0, pitch: 100,
            """{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":240,"angleOffset":90}""");
        CheckConversion(chart, 0, 180, "beatsPerMinute", 360);
        var target = (JObject)chart.actions[0]!;
        Assert.That(SetSpeedTempo.TryConvert(Timing(chart), 1, 1, [target], target, 300, out _), Is.False);
    }

    [Test]
    public void ConversionAccountsForPitchAndThePauseOnTheTile()
    {
        var chart = Chart(0, pitch: 50,
            """{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":120}""",
            """{"floor":1,"eventType":"Pause","duration":1}""");
        CheckConversion(chart, 0, 60, "beatsPerMinute", 240);
    }

    [Test]
    public void AReplacedSpeedEventCannotChangeTheTilesEffectiveBpm()
    {
        var chart = Chart(0, pitch: 100,
            """{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":120}""",
            """{"floor":1,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":240}""");
        var events = chart.actions.Cast<JObject>().ToArray();
        Assert.That(SetSpeedTempo.TryConvert(Timing(chart), 1, 1, events, events[0], 180, out _), Is.False);
        CheckConversion(chart, 1, 180, "beatsPerMinute", 180);
    }

    private static void CheckConversion(Level chart, int eventIndex, double goal, string field, double expected)
    {
        var timing = Timing(chart);
        var target = (JObject)chart.actions[eventIndex]!;
        Assert.That(SetSpeedTempo.TryConvert(timing, 1, timing.Pitch, chart.actions.Cast<JObject>().ToArray(),
            target, goal, out var result), Is.True);
        Assert.That(result.Key, Is.EqualTo(field));
        Assert.That(result.Value, Is.EqualTo(expected).Within(.0001));
        target[result.Key] = result.Value;
        Assert.That(Timing(chart.CreateSnapshot()).TempoAt(1)!.Value.Effective, Is.EqualTo(goal).Within(.0001));
        Assert.That(target.ContainsKey("effectiveBpm"), Is.False);
    }

    private static LevelRenderer.PlaybackTimeline Timing(Level chart) =>
        LevelRenderer.BuildTimeline(chart, chart.CreateFloors(), editor: true);

    private static Level Chart(int angle, int pitch, params string[] actions) => new(new Dictionary<string, object>
    {
        ["angleData"] = new JArray(0, angle, angle),
        ["settings"] = new JObject { ["bpm"] = 120, ["pitch"] = pitch, ["countdownTicks"] = 1 },
        ["actions"] = new JArray(actions.Select(JObject.Parse)),
    });
}
