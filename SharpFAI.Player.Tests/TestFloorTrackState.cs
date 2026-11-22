using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenTK.Mathematics;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using SharpFAI.Util;
using System.Collections.Generic;
using System.Linq;

namespace SharpFAI.Player.Tests;

/// <summary>
/// <see cref="FloorTrackStateResolver"/> checks: the <c>temp*</c> / live split the game uses for
/// <c>ColorTrack</c>, and the runtime colour animation.
/// </summary>
[TestFixture]
public class TestFloorTrackState
{
    /// <summary>Build a level with one floor per angle and the given actions, via a temp file.</summary>
    private static (Level Level, List<SharpFAI.Framework.Floor> Floors) Build(
        int floorCount,
        string settingsJson,
        params string[] actions)
    {
        JObject root = new()
        {
            ["angleData"] = new JArray(Enumerable.Repeat<object>(0, floorCount)),
            ["actions"] = new JArray(actions.Select(a => JObject.Parse(a))),
            ["pathData"] = new JArray(),
            ["decorations"] = new JArray(),
        };

        // Start from the full v15 settings block, then apply whatever the test overrides.
        // ApplyDefaults overwrites whatever is already present, so the overrides go on afterwards.
        JObject settings = PresetSettings.ApplyDefaults(new JObject());

        foreach (JProperty property in JObject.Parse(settingsJson).Properties())
        {
            settings[property.Name] = property.Value;
        }

        root["settings"] = settings;

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"SharpFAITrackState_{System.Guid.NewGuid():N}.adofai");

        System.IO.File.WriteAllText(path, root.ToString());

        try
        {
            Level level = new(path);
            List<SharpFAI.Framework.Floor> floors = level.CreateFloors();
            return (level, floors);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Test]
    public void LevelDefaultsApplyToEveryFloor()
    {
        (Level level, List<SharpFAI.Framework.Floor> floors) = Build(
            5,
            """
            { "trackColor": "ff0000ff", "secondaryTrackColor": "00ff00ff",
              "trackColorType": "Single", "trackStyle": "Neon" }
            """);

        FloorTrackState[] states = FloorTrackStateResolver.Resolve(level, floors);

        TestContext.Out.WriteLine($"floors={floors.Count} states={states.Length}");
        TestContext.Out.WriteLine($"styles={string.Join(",", states.Select(s => s.Style.Name).Distinct())}");

        // angleData always gains one extra floor for the level's end marker
        Assert.That(states.Length, Is.EqualTo(floors.Count));
        Assert.That(states.All(s => s.Style.Name == "Neon"), Is.True);
        Assert.That(states[0].Color1, Is.EqualTo(new Vector4(1f, 0f, 0f, 1f)));
        Assert.That(states[0].Color2, Is.EqualTo(new Vector4(0f, 1f, 0f, 1f)));
        Assert.That(states[0].Mode, Is.EqualTo(TrackColorMode.Single));
    }

    [Test]
    public void JustThisTileKeepsTheLiveValuesUntouched()
    {
        // The game paints each floor from the temp* copy and only publishes temp* to the live values
        // when justThisTile is false. At the end of every floor the game calls
        // MoveColorTrackTempValues, snapping temp* back to the live values, so the change stays
        // confined to the floor the event sits on.
        // `justThisTile` 只影响所在砖块：官方在每块砖末尾用 MoveColorTrackTempValues 把 temp* 刷回
        // live 值，所以砖块 3 恢复关卡默认样式。
        (Level level, List<SharpFAI.Framework.Floor> floors) = Build(
            5,
            """{ "trackColor": "ffffffff", "trackStyle": "Standard" }""",
            """{ "floor": 2, "eventType": "ColorTrack", "trackStyle": "Gems", "justThisTile": true }""");

        FloorTrackState[] states = FloorTrackStateResolver.Resolve(level, floors);

        Assert.That(states[2].Style.Name, Is.EqualTo("Gems"));
        Assert.That(states[1].Style.Name, Is.EqualTo("Standard"), "floors before the event are untouched");

        // temp* is reset at the end of floor 2, so floor 3 falls back to the level default
        Assert.That(states[3].Style.Name, Is.EqualTo("Standard"),
            "temp* 每砖末尾重置，后续砖块恢复 live 值");
    }

    [Test]
    public void NonLocalColorTrackCarriesOver()
    {
        // Without justThisTile the event publishes the live values for every later floor
        (Level level, List<SharpFAI.Framework.Floor> floors) = Build(
            5,
            """{ "trackColor": "ffffffff", "trackStyle": "Standard" }""",
            """{ "floor": 2, "eventType": "ColorTrack", "trackStyle": "Neon", "justThisTile": false }""");

        FloorTrackState[] states = FloorTrackStateResolver.Resolve(level, floors);

        Assert.That(states[1].Style.Name, Is.EqualTo("Standard"));
        Assert.That(states[2].Style.Name, Is.EqualTo("Neon"));
        Assert.That(states[4].Style.Name, Is.EqualTo("Neon"));
    }

    [Test]
    public void TheImaginaryLegacyFloor779IsGems()
    {
        // The chart's one Style event sits on floor 779 with `justThisTile`, so only that tile is a
        // gem and its neighbours keep the level's Standard style. This is the regression the report
        // was about, so it is pinned here.
        (Level level, List<SharpFAI.Framework.Floor> floors) = Build(
            800,
            """
            { "trackColor": "debb7b", "secondaryTrackColor": "ffffff",
              "trackColorType": "Single", "trackStyle": "Standard" }
            """,
            """
            { "floor": 779, "eventType": "ColorTrack", "trackColorType": "Switch",
              "trackColor": "00ff07ff", "secondaryTrackColor": "ffffffff",
              "trackColorAnimDuration": 0.01, "trackColorPulse": "Backward",
              "trackPulseLength": 100, "trackStyle": "Gems", "justThisTile": true }
            """);

        FloorTrackState[] states = FloorTrackStateResolver.Resolve(level, floors);

        Assert.That(states[779].Style.Name, Is.EqualTo("Gems"));
        Assert.That(states[779].Style.Sprite, Is.True, "Gems is drawn as a sprite quad");
        Assert.That(states[779].Mode, Is.EqualTo(TrackColorMode.Switch));
        Assert.That(states[779].Color1, Is.EqualTo(new Vector4(0f, 1f, 0x07 / 255f, 1f)));
        Assert.That(states[779].PulseMode, Is.EqualTo(2), "Backward pulse");

        // The event is justThisTile, so only floor 779 is a gem: the game calls
        // MoveColorTrackTempValues at the end of every floor, snapping the temp* values back to the
        // live ones. Nothing before 779 changes and 780 keeps the level default.
        // `justThisTile` 只影响 779：官方在每块砖末尾用 MoveColorTrackTempValues 把 temp* 刷回
        // live 值，所以砖块 780 恢复关卡默认样式，不会继承宝石样式。
        Assert.That(states[778].Style.Name, Is.EqualTo("Standard"));
        Assert.That(states[780].Style.Name, Is.EqualTo("Standard"),
            "justThisTile 只影响所在砖块，temp* 每砖末尾都会被重置");
    }

    [Test]
    public void BackwardPulseOffsetGrowsWithTheFloorIndex()
    {
        // Backward: 1 / pulseLength * (seqID % pulseLength) * duration
        FloorTrackState state = new(
            TrackStyleInfo.Neon,
            Vector4.Zero,
            Vector4.One,
            TrackColorMode.Blink,
            PulseMode: 2,
            AnimDuration: 1f,
            PulseLength: 10f,
            StartOfColorChange: 0,
            GlowMultiplier: 1f,
            BeatsBehind: float.PositiveInfinity);

        Assert.That(state.TimeOffset(0), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(state.TimeOffset(5), Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(state.TimeOffset(9), Is.EqualTo(0.9f).Within(1e-6f));
        Assert.That(state.TimeOffset(10), Is.EqualTo(0f).Within(1e-6f), "wraps at pulseLength");
    }

    [Test]
    public void SwitchFlipsAtHalfTheDuration()
    {
        FloorTrackState state = new(
            TrackStyleInfo.Neon,
            new Vector4(1f, 0f, 0f, 1f),
            new Vector4(0f, 1f, 0f, 1f),
            TrackColorMode.Switch,
            PulseMode: 0,
            AnimDuration: 1f,
            PulseLength: 10f,
            StartOfColorChange: 0,
            GlowMultiplier: 1f,
            BeatsBehind: float.PositiveInfinity);

        Assert.That(state.EvaluateColor(0, 0f), Is.EqualTo(new Vector4(1f, 0f, 0f, 1f)));
        Assert.That(state.EvaluateColor(0, 0.49f), Is.EqualTo(new Vector4(1f, 0f, 0f, 1f)));
        Assert.That(state.EvaluateColor(0, 0.51f), Is.EqualTo(new Vector4(0f, 1f, 0f, 1f)));
        Assert.That(state.EvaluateColor(0, 1.51f), Is.EqualTo(new Vector4(0f, 1f, 0f, 1f)));
    }

    [Test]
    public void GlowPingPongsBetweenTheTwoColours()
    {
        FloorTrackState state = new(
            TrackStyleInfo.Neon,
            new Vector4(0f, 0f, 0f, 1f),
            new Vector4(1f, 1f, 1f, 1f),
            TrackColorMode.Glow,
            PulseMode: 0,
            AnimDuration: 1f,
            PulseLength: 10f,
            StartOfColorChange: 0,
            GlowMultiplier: 1f,
            BeatsBehind: float.PositiveInfinity);

        // (1 - cos(2*pi*t/d)) / 2 hits 0 at t = 0 and 1 at the half period
        Assert.That(state.EvaluateColor(0, 0f).X, Is.EqualTo(0f).Within(1e-4f));
        Assert.That(state.EvaluateColor(0, 0.5f).X, Is.EqualTo(1f).Within(1e-4f));
        Assert.That(state.EvaluateColor(0, 1f).X, Is.EqualTo(0f).Within(1e-4f));
    }

    [Test]
    public void RainbowCyclesTheHueAndKeepsValue()
    {
        FloorTrackState state = new(
            TrackStyleInfo.Neon,
            new Vector4(1f, 0f, 0f, 1f),
            Vector4.One,
            TrackColorMode.Rainbow,
            PulseMode: 0,
            AnimDuration: 1f,
            PulseLength: 10f,
            StartOfColorChange: 0,
            GlowMultiplier: 1f,
            BeatsBehind: float.PositiveInfinity);

        // Red at t = 0, green at a third of the cycle, blue at two thirds
        Vector4 red = state.EvaluateColor(0, 0f);
        Vector4 green = state.EvaluateColor(0, 1f / 3f);
        Vector4 blue = state.EvaluateColor(0, 2f / 3f);

        Assert.That(red.X, Is.GreaterThan(0.99f));
        Assert.That(green.Y, Is.GreaterThan(0.99f));
        Assert.That(blue.Z, Is.GreaterThan(0.99f));
        Assert.That(red.W, Is.EqualTo(1f), "alpha follows the first colour");
    }

    [Test]
    public void StripesAlternateWithTheChangeAnchor()
    {
        (Level level, List<SharpFAI.Framework.Floor> floors) = Build(
            5,
            """{ "trackColor": "ff0000ff", "secondaryTrackColor": "0000ffff" }""",
            """
            { "floor": 1, "eventType": "ColorTrack", "trackColorType": "Stripes",
              "trackColor": "ff0000ff", "secondaryTrackColor": "0000ffff", "justThisTile": false }
            """);

        FloorTrackState[] states = FloorTrackStateResolver.Resolve(level, floors);

        // Anchor is floor 1, so even offsets use colour 1 and odd ones colour 2
        Assert.That(states[1].Color1, Is.EqualTo(new Vector4(1f, 0f, 0f, 1f)));
        Assert.That(states[2].Color1, Is.EqualTo(new Vector4(0f, 0f, 1f, 1f)));
        Assert.That(states[3].Color1, Is.EqualTo(new Vector4(1f, 0f, 0f, 1f)));
    }

    [Test]
    public void GemsStyleKeepsTheStandardShadow()
    {
        // SetTrackStyle only overrides _Sprite and the tile texture for Gems; the border texture and
        // shadow fall through to the default branch
        Assert.That(TrackStyleInfo.Gems.Sprite, Is.True);
        Assert.That(TrackStyleInfo.Gems.EdgeTextureSuffix, Is.EqualTo("Edge.png"));
        Assert.That(TrackStyleInfo.Gems.ShadowTune, Is.EqualTo(1.23f));
        Assert.That(TrackStyleInfo.Gems.TileTextureSuffix, Is.EqualTo("Gem.png"));
    }

    [Test]
    public void AllSixOfficialStylesAreKnown()
    {
        string[] official = ["Standard", "Neon", "NeonLight", "Basic", "Gems", "Minimal"];

        foreach (string name in official)
        {
            Assert.That(TrackStyleInfo.Parse(name).Name, Is.EqualTo(name),
                $"{name} must resolve to itself, not fall back to Standard");
        }
    }
}
