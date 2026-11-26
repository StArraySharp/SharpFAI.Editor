using Newtonsoft.Json.Linq;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Play;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Tests;

public class TestHitJudge
{
    [TestCase(-61, HitMargin.TooEarly)]
    [TestCase(-60, HitMargin.TooEarly)]
    [TestCase(-55, HitMargin.VeryEarly)]
    [TestCase(-40, HitMargin.EarlyPerfect)]
    [TestCase(0, HitMargin.Perfect)]
    [TestCase(30, HitMargin.Perfect)]
    [TestCase(40, HitMargin.LatePerfect)]
    [TestCase(55, HitMargin.VeryLate)]
    [TestCase(60, HitMargin.VeryLate)]
    [TestCase(61, HitMargin.TooLate)]
    public void UsesTheGamesStrictAngleBoundaries(double offset, HitMargin margin)
    {
        Assert.That(HitJudge.Judge(100.0 + offset, 100.0, true, 120.0), Is.EqualTo(margin));
        Assert.That(HitJudge.Judge(100.0 - offset, 100.0, false, 120.0), Is.EqualTo(margin));
    }

    [Test]
    public void PitchScalesTimeBasedWindowsWithoutScalingTheAngleFloors()
    {
        Assert.That(HitJudge.GetBoundaries(120.0, pitch: 1.5), Is.EqualTo(new HitBoundaries(60, 45, 30)));
        HitBoundaries fast = HitJudge.GetBoundaries(1000.0, pitch: 1.5);
        Assert.That(fast.Counted, Is.EqualTo(292.5));
        Assert.That(fast.Perfect, Is.EqualTo(135.0));
        Assert.That(fast.Pure, Is.EqualTo(112.5));
    }

    [Test]
    public void AccuracyUsesOriginalWeightsAndDeathDenominator()
    {
        HitStats stats = new();
        stats.Record(HitMargin.Perfect);
        stats.Record(HitMargin.EarlyPerfect);
        stats.Record(HitMargin.VeryLate);
        stats.Record(HitMargin.Miss);
        Assert.That(stats.Accuracy, Is.EqualTo(.4001).Within(1e-9));
    }
}

public class TestManualPlay
{
    [Test]
    public void RealTimeMovesForwardWithoutAHitAndThePlanetRemainsOnTheSameFloor()
    {
        ManualTrack track = Track();
        PlaySession session = Start(track);
        Advance(session, .54);
        Assert.That(session.Time, Is.EqualTo(.54));
        Assert.That(session.CurrentFloor, Is.Zero);
        Advance(session, .61, Press(.60));
        Assert.That(session.CurrentFloor, Is.EqualTo(1));
        Assert.That(session.LastHit!.Value.Margin, Is.EqualTo(HitMargin.LatePerfect));
        Assert.That(session.Time, Is.EqualTo(.61));
    }

    [Test]
    public void EarlierPressSnapsThePlanetWithoutSeekingTheMusic()
    {
        ManualTrack track = Track();
        PlaySession session = Start(track);
        Advance(session, .48, Press(.39));
        Assert.That(session.CurrentFloor, Is.EqualTo(1));
        Assert.That(session.LastHit!.Value.Time, Is.EqualTo(.39));
        Assert.That(session.LastHit!.Value.Margin, Is.EqualTo(HitMargin.EarlyPerfect));
        Assert.That(session.Time, Is.EqualTo(.48));
    }

    [Test]
    public void MissingTheDeadlineFailsWithoutAnInputOrAFloorAdvance()
    {
        ManualTrack track = Track();
        PlaySession session = Start(track);
        Advance(session, 1.2, Press(1.05));
        Assert.That(session.Failed, Is.True);
        Assert.That(session.Time, Is.EqualTo(1.0));
        Assert.That(session.CurrentFloor, Is.Zero);
        Assert.That(session.GetResult(1), Is.EqualTo(HitMargin.Miss));
    }

    [Test]
    public void NoFailRecordsTheMissAndAllowsTheNextTileToBeHitNormally()
    {
        PlaySession session = Start(Track());
        session.NoFail = true;

        Advance(session, .69);
        Assert.That(session.Failed, Is.False);
        Assert.That(session.CurrentFloor, Is.EqualTo(1));
        Assert.That(session.GetResult(1), Is.EqualTo(HitMargin.Miss));
        Assert.That(session.Stats[HitMargin.Miss], Is.EqualTo(1));
        Advance(session, 1.01, Press(1.0));
        Assert.That(session.GetResult(2), Is.EqualTo(HitMargin.Perfect));
    }

    [Test]
    public void RepeatedEarlyPressesOverloadButOnePressDoesNot()
    {
        ManualTrack track = Track();
        PlaySession session = Start(track);
        Advance(session, .10, Press(.10));
        Assert.That(session.Failed, Is.False);
        Assert.That(session.LastHit!.Value.Margin, Is.EqualTo(HitMargin.TooEarly));
        Advance(session, .20, Press(.12), Press(.15));
        Assert.That(session.Failed, Is.True);
        Assert.That(session.LastHit!.Value.Margin, Is.EqualTo(HitMargin.Overload));
    }

    [Test]
    public void NoFailRecordsAnEarlyOverloadWithoutAdvancingTheTile()
    {
        PlaySession session = Start(Track());
        session.NoFail = true;

        Advance(session, .2, Press(.1), Press(.12), Press(.15));
        Assert.That(session.Failed, Is.False);
        Assert.That(session.CurrentFloor, Is.Zero);
        Assert.That(session.Stats[HitMargin.Overload], Is.EqualTo(1));
        Advance(session, .51, Press(.5));
        Assert.That(session.GetResult(1), Is.EqualTo(HitMargin.Perfect));
    }

    [Test]
    public void OnePressCrossesTheMidspinAndItsSuccessor()
    {
        ManualTrack track = Track(entries: [0, .5, .5, 1.0], midspin: 1);
        PlaySession session = Start(track);
        Advance(session, .52, Press(.50));
        Assert.That(session.CurrentFloor, Is.EqualTo(2));
        Assert.That(session.GetResult(1), Is.EqualTo(HitMargin.Perfect));
        Assert.That(session.GetResult(2), Is.EqualTo(HitMargin.Perfect));
        Assert.That(session.ErrorMeter.Count, Is.EqualTo(1));
    }

    [Test]
    public void ErrorMeterUsesTheWindowFromTheHitFloorAndClearsOnRestart()
    {
        ManualTrack track = Track(events: [new ScaleMarginEvent { Floor = 1, Scale = 50 }]);
        PlaySession session = Start(track);
        Advance(session, .48, Press(.475));
        var tick = session.ErrorMeter[0];
        Assert.Multiple(() =>
        {
            Assert.That(session.ErrorMeter.Count, Is.EqualTo(1));
            Assert.That(tick.Offset, Is.EqualTo(-.3f).Within(.00001));
            Assert.That(tick.OpacityAt(tick.Time + 1.5), Is.EqualTo(.75f));
        });
        session.Start(track, 0, 0);
        Assert.That(session.ErrorMeter.Count, Is.Zero);
        Assert.That(session.ErrorMeter.HandAt(2), Is.Zero);
    }

    [Test]
    public void ErrorMeterSmoothsTheAverageAndKeepsOnlyRecentTicks()
    {
        var meter = new HitErrorMeter();
        var bounds = HitJudge.GetBoundaries(120);
        meter.Add(30, bounds, 0);
        Assert.That(meter.HandAt(.125), Is.EqualTo(.0875f).Within(.00001));
        for (int i = 1; i <= HitErrorMeter.Capacity; i++)
            meter.Add(-90, bounds, i);
        Assert.Multiple(() =>
        {
            Assert.That(meter.Count, Is.EqualTo(HitErrorMeter.Capacity));
            Assert.That(meter[0].Time, Is.EqualTo(1));
            Assert.That(meter[0].Offset, Is.InRange(-1.051f, -.999f));
            Assert.That(meter.HandAt(HitErrorMeter.Capacity + .25), Is.EqualTo(.1f).Within(.00001));
            Assert.That(meter[0].OpacityAt(4), Is.Zero);
        });
    }

    [Test]
    public void HoldOnlyRespondsToTheKeyThatStartedIt()
    {
        ManualTrack track = Track(hold: 1);
        PlaySession session = Start(track);
        Advance(session, .51, Press(.50, 1));
        Assert.That(session.Holding, Is.True);
        Advance(session, .83, new PlayInput.KeyEvent(.55, false, 2));
        Assert.That(session.Failed, Is.False);
        Assert.That(session.Holding, Is.True);
        Advance(session, 1.03, new PlayInput.KeyEvent(.98, false, 1));
        Assert.That(session.GetResult(2), Is.EqualTo(HitMargin.Perfect));
    }

    [Test]
    public void ReleasingAHoldEarlyFailsImmediately()
    {
        ManualTrack track = Track(hold: 1);
        PlaySession session = Start(track);
        Advance(session, .70, Press(.50), new PlayInput.KeyEvent(.68, false, 1));
        Assert.That(session.ReleasedHoldEarly, Is.True);
        Assert.That(session.Failed, Is.True);
        Assert.That(session.Time, Is.EqualTo(.68));
    }

    [Test]
    public void ReleasingAHoldEarlyInNoFailRecordsAMissAndContinues()
    {
        PlaySession session = Start(Track(hold: 1));
        session.NoFail = true;

        Advance(session, .70, Press(.50), new PlayInput.KeyEvent(.68, false, 1));
        Assert.That(session.Failed, Is.False);
        Assert.That(session.Holding, Is.False);
        Assert.That(session.CurrentFloor, Is.EqualTo(2));
        Assert.That(session.GetResult(2), Is.EqualTo(HitMargin.Miss));
    }

    [Test]
    public void OpeningCountdownUsesTheAuthoredTicksAndSignedSongClock()
    {
        ManualTrack track = Track(entries: [-1.5, .5, 1, 1.5], countdownTicks: 3);
        PlaySession session = new();

        session.Start(track, 0);
        Assert.That(session.Time, Is.EqualTo(-1.5));
        Assert.That(session.Countdown, Is.EqualTo(2));
        Advance(session, -1);
        Assert.That(session.Countdown, Is.EqualTo(1));
        Advance(session, -.5);
        Assert.That(session.Countdown, Is.Zero);
        Advance(session, 0);
        Assert.That(session.Countdown, Is.Null);
    }

    [Test]
    public void CheckpointRetryRecountsAndDiscardsPressesBeforeGo()
    {
        ManualTrack track = Track(entries: [0, .5, 1, 1.5, 2, 2.5]);
        PlaySession session = new() { NoFail = true };

        session.Start(track, 4);
        Assert.That(session.Time, Is.EqualTo(.5));
        Assert.That(session.CurrentFloor, Is.EqualTo(4));
        Assert.That(session.Countdown, Is.EqualTo(3));
        Advance(session, 2, Press(.6), Press(.65), Press(.7));
        Assert.That(session.Stats.Total, Is.Zero);
        Assert.That(session.Countdown, Is.Zero);
        Assert.That(session.NoFail, Is.True);

        session.Start(track, 4, 2.0);
        Assert.That(session.Countdown, Is.Null);
    }

    [Test]
    public void CountdownAndPauseLockDiscardPressesBeforeTheInputWindow()
    {
        ManualTrack track = Track(entries: [-1, .5, 1.5, 2], pauseLock: .7);
        PlaySession session = Start(track, time: -1);
        Advance(session, .1, Press(-.6));
        Assert.That(session.LastHit, Is.Null);
        Advance(session, 1.3, Press(.5), Press(.9));
        Assert.That(session.Stats.Total, Is.EqualTo(1));
        Assert.That(session.CurrentFloor, Is.EqualTo(1));
        Advance(session, 1.51, Press(1.5));
        Assert.That(session.CurrentFloor, Is.EqualTo(2));
    }

    [Test]
    public void StartingOnPauseGatesInputsAndFadesMusicUntilTheEndOfThePause()
    {
        ManualTrack track = Track(entries: [0, .5, 1, 5.5, 6],
            pauseStarts: new() { [2] = new PauseStartTiming(5, .5, 6) });
        var session = new PlaySession();
        session.Start(track, 2);
        Assert.That(session.Time, Is.EqualTo(2));
        Assert.That(session.CurrentFloor, Is.EqualTo(2));
        Assert.That(session.PauseStartMusicGain, Is.Zero);
        Advance(session, 3.5, Press(3.25));
        Assert.That(session.PauseStartMusicGain, Is.EqualTo(.5f));
        Assert.That(session.Stats.Total, Is.Zero);
        Advance(session, 5, Press(4.99));
        Assert.That(session.PauseStartMusicGain, Is.EqualTo(1));
        Assert.That(session.Stats.Total, Is.Zero);
        Advance(session, 5.5, Press(5.5));
        Assert.That(session.GetResult(3), Is.EqualTo(HitMargin.Perfect));

        session.Start(track, 2, seekTime: 4.5);
        Assert.That(session.Time, Is.EqualTo(4.5));
        Assert.That(session.PauseStartMusicGain, Is.EqualTo(1));
    }

    [Test]
    public void AutoplayScaleMarginAndMultitapUseTheAuthoredTargetFloor()
    {
        Level level = new(new Dictionary<string, object>
        {
            ["angleData"] = new JArray(0, 0, 0, 0),
            ["settings"] = new JObject { ["bpm"] = 120 },
            ["actions"] = JArray.Parse("""
                [
                  {"floor":1,"eventType":"AutoPlayTiles","enabled":true},
                  {"floor":2,"eventType":"AutoPlayTiles","enabled":false},
                  {"floor":2,"eventType":"ScaleMargin","scale":50},
                  {"floor":2,"eventType":"Multitap","taps":2}
                ]
                """)
        });
        ManualTrack track = Track(events: PlaybackEventBinder.BindForPlayback(level));
        PlaySession session = Start(track);
        Advance(session, .6);
        Assert.That(session.GetResult(1), Is.EqualTo(HitMargin.Auto));
        Assert.That(session.GetBoundaries(1.0).Counted, Is.EqualTo(30.0));
        Advance(session, 1.01, Press(1.0));
        Assert.That(session.CurrentFloor, Is.EqualTo(1));
        Advance(session, 1.02, Press(1.015, 2));
        Assert.That(session.GetResult(2), Is.EqualTo(HitMargin.Perfect));
    }

    [Test]
    public void AChartSafetyTileAdvancesAfterAMissWithoutEndingTheAttempt()
    {
        ManualTrack track = Track(events: [new AutoPlayTilesEvent { Floor = 0, Safe = true }]);
        PlaySession session = Start(track);
        Advance(session, .71);
        Assert.That(session.Failed, Is.False);
        Assert.That(session.CurrentFloor, Is.EqualTo(1));
        Assert.That(session.GetResult(1), Is.EqualTo(HitMargin.Miss));
    }

    [TestCase(false, 7)]
    [TestCase(true, 8)]
    public void OverlappingSameFramePressesUseTheSeparateMultipressOverload(bool noFail, int lastFloor)
    {
        ManualTrack track = Track(entries: Enumerable.Range(0, 13).Select(i => i * .1).ToArray(), bpm: 600);
        PlaySession session = Start(track);
        session.NoFail = noFail;
        for (int pair = 0; pair < 4; pair++)
        {
            double hit = .155 + pair * .2;
            Advance(session, hit - .004);
            session.Advance(hit + .004, hit - .004, hit + .004, [Press(hit), Press(hit, 2)], 1.0);
        }
        Assert.That(session.CurrentFloor, Is.EqualTo(lastFloor));
        Assert.That(session.Stats[HitMargin.Overload], Is.EqualTo(1));
        Assert.That(session.Failed, Is.EqualTo(!noFail));
    }

    [Test]
    public void RetryCheckpointIsArmedOnlyWhenItsTileIsHit()
    {
        ManualTrack track = Track(entries: [0, .5, 1, 1.5, 2],
            events: [new CheckpointEvent { Floor = 1, TileOffset = 1 }]);
        PlaySession session = Start(track);
        Advance(session, .48);
        Assert.That(session.CheckpointFloor, Is.Zero);
        Advance(session, .52, Press(.50));
        Advance(session, 1.6);
        Assert.That(session.Failed, Is.True);
        Assert.That(session.CheckpointFloor, Is.EqualTo(2));
    }

    private static ManualTrack Track(double[]? entries = null, int midspin = -1, int hold = -1,
        double pauseLock = 0, PlaybackEvent[]? events = null, double bpm = 120, int countdownTicks = 4,
        Dictionary<int, PauseStartTiming>? pauseStarts = null)
    {
        entries ??= [0, .5, 1, 1.5];
        Floor[] floors = Enumerable.Range(0, entries.Length)
            .Select(i => new Floor(0, 180, default) { isMidspin = i == midspin }).ToArray();
        LevelRenderer.PlaybackTimeline timeline = new()
        {
            EntryTimes = entries,
            CountdownTicks = countdownTicks,
            FloorBpms = Enumerable.Repeat(bpm, entries.Length).ToArray(),
            PlanetTrack = Enumerable.Range(0, entries.Length)
                .Select(i => new PlanetTrackFloor(1, 2, 0, 1, 2, i == hold ? 1 : -1, 0, false)).ToArray(),
            PauseLocks = new Dictionary<int, double> { [1] = pauseLock },
            PauseStarts = pauseStarts,
        };
        return new ManualTrack(floors, timeline, events ?? []);
    }

    private static PlaySession Start(ManualTrack track, double time = 0)
    {
        PlaySession session = new();
        session.Start(track, 0, time);
        return session;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DecorationHitboxFailureRespectsNoFail(bool noFail)
    {
        var session = Start(Track());
        session.NoFail = noFail;
        Assert.That(session.FailByHitbox(), Is.EqualTo(!noFail));
        Assert.That(session.FailedByHitbox, Is.EqualTo(!noFail));
    }

    private static PlayInput.KeyEvent Press(double time, int code = 1) => new(time, true, code);

    private static void Advance(PlaySession session, double target, params PlayInput.KeyEvent[] events) =>
        session.Advance(target, session.Time, target, events, 1.0);
}

public class TestPlayInput
{
    [Test]
    public void OpenTkKeysFilterEscAndRepeatAndRetainTimestampedReleases()
    {
        PlayInput input = new() { Enabled = true };
        Assert.That(input.Push(Keys.Escape, true, 1), Is.False);
        Assert.That(input.Push(Keys.A, true, 2), Is.True);
        Assert.That(input.Push(Keys.A, true, 3), Is.False);
        Assert.That(input.Push(Keys.A, false, 4), Is.False);
        Assert.That(input.Push(Keys.A, true, 5), Is.True);
        Assert.That(input.Push(MouseButton.Left, true, 6), Is.True);

        var events = input.Pending.ToArray();
        Assert.That(events.Select(item => item.Time), Is.EqualTo(new double[] { 2, 4, 5, 6 }));
        Assert.That(events.Select(item => item.Pressed), Is.EqualTo(new[] { true, false, true, true }));
        Assert.That(events[0].Code, Is.EqualTo(events[1].Code));
        Assert.That(events[3].Code, Is.Not.EqualTo(events[0].Code));
    }

    [Test]
    public void DisabledInputDoesNotQueueEventsAndFocusLossClearsHeldKeys()
    {
        PlayInput input = new();
        Assert.That(input.Push(Keys.A, true, 1), Is.False);
        Assert.That(input.PendingCount, Is.Zero);
        input.Enabled = true;
        Assert.That(input.Push(Keys.A, true, 2), Is.True);
        input.Clear();
        input.ReleaseAll();
        Assert.That(input.Push(Keys.A, true, 3), Is.True);
    }
}
