using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Filters;
using SharpFAI.Player.Engine.Play;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using System.Numerics;
using Newtonsoft.Json;

namespace SharpFAI.Player.Tests;

public sealed class TestGeneratedFilters
{
    [TestCase("Distortion_Half_Sphere", "Distortion_float_Sphere")]
    [TestCase("Drawing_Halftone", "Drawing_floattone")]
    public void UnresolvedOriginalShaderNamesAreNotAliased(string component, string serialized)
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        string name = "CameraFilterPack_" + component;
        string shader = "CameraFilterPack/" + component;
        var resources = typeof(PlayerWindow).Assembly.GetManifestResourceNames();
        Assert.Multiple(() =>
        {
            Assert.That(GeneratedFilterRegistry.Factories.ContainsKey(name), Is.True);
            Assert.That(GeneratedFilterRegistry.ShaderNames[name], Is.EqualTo(shader));
            Assert.That(resources, Does.Not.Contain("Resources.Shaders.Filters/" + shader + ".glsl"));
            Assert.That(resources, Does.Contain("Resources.Shaders.Filters/CameraFilterPack/" + serialized + ".glsl"));
        });
    }

    [TestCase(0f)]
    [TestCase(100f)]
    [TestCase(-100f)]
    public void LegacyTunnelRendersPolarScreenIndependentlyOfIntensity(float intensity)
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var item = JsonConvert.DeserializeObject<CameraFilterEvent>(
            $$"""{"eventType":"SetFilter","filter":"Tunnel","enabled":true,"intensity":{{intensity}}}""")!;
        item.EventType = EventType.SetFilter;
        using var filters = new FilterTimeline([new(item, 0, new(0, 0, EventEnums.Ease.Linear))], false);
        var backend = new RecordingBackend();
        var entry = filters.Evaluate(backend, 0).Single();
        entry.Filter.Render(new Target(320, 180), new Target(320, 180));
        Assert.Multiple(() =>
        {
            Assert.That(entry.Name, Is.EqualTo("PolarScreen"));
            Assert.That(filters.ShaderNames(), Does.Contain("ADOFAI/PolarScreen"));
            Assert.That(backend.Resources, Does.Contain("ADOFAI/PolarScreen"));
            Assert.That(backend.Vectors["_ScreenResolution"], Is.EqualTo(new Vector4(320, 180, 0, 0)));
            Assert.That(backend.Draws.Single().Material, Is.True);
            Assert.That(typeof(PlayerWindow).Assembly.GetManifestResourceNames(),
                Does.Contain("Resources.Shaders.Filters/ADOFAI/PolarScreen.glsl"));
        });
    }

    [TestCase("Drawing_Paper", "Pencil_Correction", 1.1f, 0.76f)]
    [TestCase("TV_CompressionFX", "Parasite", 3f, 1f)]
    [TestCase("Pixel_Pixelisation", "_Pixelisation", 4f, 0f)]
    [TestCase("Edge_Neon", "EdgeWeight", 7.5f, 1f)]
    [TestCase("FX_8bits_gb", "Brightness", 0.2f, 0f)]
    [TestCase("Blur_Blurry", "FastFilter", 1f, 2f)]
    [TestCase("Blur_GaussianBlur", "Size", 1f, 10f)]
    public void CameraPrefabDefaultsSurviveLifecycleWithoutChangingNewInstances(
        string suffix, string field, float cameraValue, float scriptValue)
    {
        string name = "CameraFilterPackLegacy_" + suffix;
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var backend = new RecordingBackend();
        using var camera = GeneratedFilterRegistry.CameraFactories[name](backend);
        Assert.That(camera.GetParameter(field)!.Value.X, Is.EqualTo(cameraValue).Within(1e-6f));
        camera.Enable();
        camera.Render(new Target(128, 96), new Target(128, 96));
        Assert.That(camera.GetParameter(field)!.Value.X, Is.EqualTo(cameraValue).Within(1e-6f));
        using var added = GeneratedFilterRegistry.Factories[name](backend);
        Assert.That(added.GetParameter(field)!.Value.X, Is.EqualTo(scriptValue).Within(1e-6f));
    }

    [TestCase(0f, 0f)]
    [TestCase(10f, 0f)]
    [TestCase(30f, 2f)]
    [TestCase(50f, 2f)]
    [TestCase(100f, 5f)]
    [TestCase(200f, 10f)]
    public void LedUsesOriginalDefaultAndRoundedIntensity(float intensity, float expectedSize)
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var backend = new RecordingBackend();
        var binding = GeneratedFilterRegistry.Legacy["LED"];
        using var filter = GeneratedFilterRegistry.CameraFactories[binding.Component](backend);
        Assert.That(filter.GetParameter("Size")!.Value.X, Is.EqualTo(5f));
        binding.SetIntensity!(filter, intensity / 100f);
        filter.Enable();
        filter.Render(new Target(128, 96), new Target(128, 96));
        Assert.Multiple(() =>
        {
            Assert.That(backend.Resources, Does.Contain("CameraFilterPackLegacy/TV_LED"));
            Assert.That(backend.Floats["_Size"], Is.EqualTo(expectedSize));
            Assert.That(backend.Floats["_Distortion"], Is.EqualTo(1f));
        });
    }

    [Test]
    public void CameraDrawingUsesBlackPencilWithoutChangingNewInstances()
    {
        const string name = "CameraFilterPackLegacy_Drawing_Paper";
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var backend = new RecordingBackend();
        using var camera = GeneratedFilterRegistry.CameraFactories[name](backend);
        using var added = GeneratedFilterRegistry.Factories[name](backend);
        Assert.Multiple(() =>
        {
            Assert.That(camera.GetParameter("Pencil_Color"), Is.EqualTo(new Vector4(0, 0, 0, 1)));
            Assert.That(added.GetParameter("Pencil_Color"), Is.EqualTo(new Vector4(0.156f, 0.3f, 0.738f, 1)));
        });
    }

    [Test]
    public void ReadingEditorMetadataDoesNotResetLiveFilterControls()
    {
        const string name = "CameraFilterPackLegacy_Blur_GaussianBlur";
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var backend = new RecordingBackend();
        using var camera = GeneratedFilterRegistry.CameraFactories[name](backend);
        camera.Enable();
        using (var metadata = GeneratedFilterRegistry.MetadataFactories[name](backend))
            Assert.That(metadata.GetParameter("Size")!.Value.X, Is.EqualTo(10f));
        camera.Render(new Target(128, 96), new Target(128, 96));
        Assert.That(camera.GetParameter("Size")!.Value.X, Is.EqualTo(1f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FirstAdvancedEventOverridesComponentDefaultsOnBothCameraPlanes(bool background)
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var item = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter":"CameraFilterPackLegacy_Edge_Neon","enabled":true}
            """)!;
        var scheduled = new ScheduledEvent<CameraFilterEvent>(item, 0,
            new TrackTiming(0, 0, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([scheduled], legacyTween: false, background: background);
        var filter = filters.Evaluate(new RecordingBackend(), 0).Single().Filter;
        Assert.That(filter.GetParameter("EdgeWeight")!.Value.X, Is.EqualTo(0f));
    }

    [TestCase("CameraFilterPack_Color_Invert", "_Fade", false)]
    [TestCase("CameraFilterPack_Drawing_Paper", "Pencil_Color", true)]
    [TestCase("CameraFilterPack_AAA_SuperHexagon", "center", false)]
    public void FirstAdvancedEventUsesOriginalNeutralFloatColorAndVectorValues(string name, string field, bool color)
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var item = new CameraFilterEvent { Filter = name, EventType = EventType.SetFilterAdvanced };
        using var filters = new FilterTimeline([new(item, 0, new(0, 0, EventEnums.Ease.Linear))], false);
        var filter = filters.Evaluate(new RecordingBackend(), 0).Single().Filter;
        Assert.That(filter.GetParameter(field), Is.EqualTo(color ? Vector4.One : Vector4.Zero));
    }

    [Test]
    public void SubsequentAdvancedEventsKeepOmittedValuesAndReenableTweensFromNeutral()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        ScheduledEvent<CameraFilterEvent> Event(string fields, double start, double duration = 0) => new(
            JsonConvert.DeserializeObject<CameraFilterEvent>(
                "{\"filter\":\"CameraFilterPack_Color_Invert\"," + fields + "}")!,
            0, new(start, duration, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([
            Event("\"filter__Fade\":60", 0), Event("\"enabled\":true", 1),
            Event("\"enabled\":false", 2), Event("\"enabled\":true", 3, 2)], false);
        var backend = new RecordingBackend();
        float Fade(double time) => filters.Evaluate(backend, time).Single().Filter.GetParameter("_Fade")!.Value.X;
        Assert.That(Fade(0), Is.EqualTo(.6f).Within(1e-6f));
        Assert.That(Fade(1), Is.EqualTo(.6f).Within(1e-6f));
        Assert.That(filters.Evaluate(backend, 2), Is.Empty);
        Assert.That(Fade(3), Is.EqualTo(0f));
        Assert.That(Fade(4), Is.EqualTo(.3f).Within(1e-6f));
        Assert.That(Fade(5), Is.EqualTo(.6f).Within(1e-6f));
        Assert.That(Fade(0), Is.EqualTo(.6f).Within(1e-6f));
    }

    [Test]
    public void AdvancedDisableOthersRestartsTheCurrentFiltersTweenFromNeutral()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        ScheduledEvent<CameraFilterEvent> Event(float fade, double start, double duration, bool disableOthers) => new(
            JsonConvert.DeserializeObject<CameraFilterEvent>(
                $$"""{"filter":"CameraFilterPack_Color_Invert","filter__Fade":{{fade}},"disableOthers":{{(disableOthers ? "true" : "false")}}}""")!,
            0, new(start, duration, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([Event(60, 0, 0, false), Event(100, 1, 2, true)], false);
        var backend = new RecordingBackend();
        Assert.That(filters.Evaluate(backend, 0).Single().Filter.GetParameter("_Fade")!.Value.X, Is.EqualTo(.6f).Within(1e-6f));
        Assert.That(filters.Evaluate(backend, 1).Single().Filter.GetParameter("_Fade")!.Value.X, Is.EqualTo(0f));
        Assert.That(filters.Evaluate(backend, 2).Single().Filter.GetParameter("_Fade")!.Value.X, Is.EqualTo(.5f).Within(1e-6f));
    }

    [Test]
    public void AdvancedDisableOthersKeepsLegacyFiltersAndCompletesThePreviousFieldTween()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        var legacy = new CameraFilterEvent { EventType = EventType.SetFilter, Filter = "Grayscale" };
        var first = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter":"CameraFilterPack_Color_Invert","filterProperties":{"filter__Fade":100}}
            """)!;
        var reset = new CameraFilterEvent
        {
            EventType = EventType.SetFilterAdvanced, Filter = first.Filter, DisableOthers = true,
        };
        first.EventType = EventType.SetFilterAdvanced;
        using var filters = new FilterTimeline([
            new(legacy, 0, new(0, 0, EventEnums.Ease.Linear)),
            new(first, 0, new(0, 4, EventEnums.Ease.Linear)),
            new(reset, 0, new(2, 2, EventEnums.Ease.Linear))], false);
        var backend = new RecordingBackend();

        var active = filters.Evaluate(backend, 2);
        Assert.That(active.Select(entry => entry.Name), Is.EqualTo(new[] { "CameraFilterPackLegacy_Color_GrayScale", first.Filter }));
        Assert.That(active[1].Filter.GetParameter("_Fade")!.Value.X, Is.Zero);
        Assert.That(filters.Evaluate(backend, 4)[1].Filter.GetParameter("_Fade")!.Value.X, Is.EqualTo(1f));
    }

    [Test]
    public void DisabledAdvancedEventInitializesWithoutResettingComponentFields()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        ScheduledEvent<CameraFilterEvent> Event(bool enabled, double start) => new(
            new CameraFilterEvent { Filter = "CameraFilterPack_Color_Invert", FilterEnabled = enabled },
            0, new(start, 0, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([Event(false, 0), Event(true, 1)], false);
        var backend = new RecordingBackend();
        Assert.That(filters.Evaluate(backend, 0), Is.Empty);
        Assert.That(filters.Evaluate(backend, 1).Single().Filter.GetParameter("_Fade")!.Value.X, Is.EqualTo(1f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AdvancedFilterValuesKeepTheirAuthoredUnitsAndChannels(bool nested)
    {
        const string fields = """{"filter_Amount":25,"filter_Downsample":4.9,"filter_Offset":[0.25,-2],"filter_Tint":"FF000080"}""";
        var item = JsonConvert.DeserializeObject<CameraFilterEvent>(nested ? $$"""{"filterProperties":{{fields}}}""" : fields)!;
        Assert.Multiple(() =>
        {
            Assert.That(item.Parameter("Amount", FilterParameterKind.Float), Is.EqualTo(new Vector4(0.25f, 0, 0, 0)));
            Assert.That(item.Parameter("Downsample", FilterParameterKind.Integer), Is.EqualTo(new Vector4(4, 0, 0, 0)));
            Assert.That(item.Parameter("Offset", FilterParameterKind.Vector2), Is.EqualTo(new Vector4(0.25f, -2, 0, 0)));
            Assert.That(item.Parameter("Tint", FilterParameterKind.Color), Is.EqualTo(new Vector4(1, 0, 0, 128f / 255f)));
        });
    }

    [TestCase("\"filter_Size\": 50, \"filter_Smooth\": 1", 0.01f)]
    [TestCase("{\"filter_Smooth\": 1}", 0.01f)]
    [TestCase("  ", null)]
    [TestCase("not JSON", null)]
    public void StringFilterPropertiesKeepValidParametersWithoutOverridingFlatValues(string encoded, float? smooth)
    {
        var item = JsonConvert.DeserializeObject<CameraFilterEvent>(
            $$"""{"filter_Size":25,"filterProperties":{{JsonConvert.ToString(encoded)}}}""")!;
        Assert.That(item.Parameter("Size", FilterParameterKind.Float)?.X, Is.EqualTo(0.25f));
        Assert.That(item.Parameter("Smooth", FilterParameterKind.Float)?.X, Is.EqualTo(smooth));
    }

    [Test]
    public void MixedFilterValuesPreferFlatValuesAndHonorDisabledFields()
    {
        var item = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter_Size":25,"filterProperties":{"filter_Size":50,"filter_Smooth":1},"disabled":{"filter_Smooth":true}}
            """)!;
        Assert.Multiple(() =>
        {
            Assert.That(item.Parameter("Size", FilterParameterKind.Float)?.X, Is.EqualTo(0.25f));
            Assert.That(item.Parameter("Smooth", FilterParameterKind.Float), Is.Null);
        });
    }

    [Test]
    public void VhsUsesOriginalResourceNamesContrastAndTemporaryTarget()
    {
        const string name = "CameraFilterPack_Real_VHS";
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        Assert.That(GeneratedFilterRegistry.Factories.TryGetValue(name, out var create), Is.True);

        var backend = new RecordingBackend();
        using var filter = create!(backend);
        filter.Enable();
        filter.SetParameter("Constrast", new Vector4(0.75f, 0f, 0f, 0f));
        filter.Render(new Target(128, 96), new Target(128, 96));

        Assert.Multiple(() =>
        {
            Assert.That(backend.Resources, Is.EqualTo(new[]
                { "CameraFilterPack/Real_VHS", "CameraFilterPack_VHS1", "CameraFilterPack_VHS2" }));
            Assert.That(backend.Textures, Is.EqualTo(new[] { "VHS", "VHS2" }));
            Assert.That(backend.Floats["CONTRAST"], Is.EqualTo(0.25f));
            Assert.That(backend.Rented, Is.EqualTo((382, 576, 0)));
            Assert.That(backend.Temporary!.Sampling, Is.EqualTo(FilterSampling.Trilinear));
            Assert.That(backend.Draws, Is.EqualTo(new[] { (128, 382, true, -1), (382, 128, false, -1) }));
            Assert.That(backend.Released, Is.SameAs(backend.Temporary));
        });
    }

    [Test]
    public void EditingFilterParametersReusesTimingWithoutChangingTheOriginalTimeline()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Generated filters unavailable.");
        var before = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter":"CameraFilterPackLegacy_TV_LED","filter_Size":5,"duration":0}
            """)!;
        before.SourceIndex = 2;
        var after = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter":"CameraFilterPackLegacy_TV_LED","filter_Size":9,"duration":0}
            """)!;
        after.SourceIndex = 2;
        var floors = new[] { new SharpFAI.Framework.Floor(0, 180, default) { bpm = 120 } };
        using var original = new FilterTimeline([new(before, 0, new(0, 0, EventEnums.Ease.Linear))], false);
        using var edited = original.Edit(new Dictionary<int, CameraFilterEvent> { [2] = after }, floors, [120]);
        Assert.That(edited, Is.Not.Null);
        var originalFilter = original.Evaluate(new RecordingBackend(), 0).Single().Filter;
        Assert.That(originalFilter.GetParameter("Size")!.Value.X, Is.EqualTo(5));
        var editedFilter = edited!.Evaluate(new RecordingBackend(), 0).Single().Filter;
        Assert.That(editedFilter.GetParameter("Size")!.Value.X, Is.EqualTo(9));
        after.AngleOffset = 90;
        Assert.That(original.Edit(new Dictionary<int, CameraFilterEvent> { [2] = after }, floors, [120]), Is.Null);
    }

    [Test]
    public void AdvancedOmittedIntegerStartsAtZeroAndPreservesArithmeticFailure()
    {
        const string name = "CameraFilterPack_Blur_Movie";
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        Assert.That(GeneratedFilterRegistry.Factories.TryGetValue(name, out var create), Is.True);

        var item = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter":"CameraFilterPack_Blur_Movie","filter_Radius":100}
            """)!;
        var scheduled = new ScheduledEvent<CameraFilterEvent>(item, 0,
            new TrackTiming(0, 1, EventEnums.Ease.Linear));
        var backend = new RecordingBackend();
        using var filters = new FilterTimeline([scheduled], legacyTween: false);

        var active = filters.Evaluate(backend, 0).Single();
        Assert.Multiple(() =>
        {
            Assert.That(active.Filter.GetParameter("FastFilter")!.Value.X, Is.EqualTo(0f));
            Assert.Throws<DivideByZeroException>(() => active.Filter.Render(new Target(128, 96), new Target(128, 96)));
            Assert.That(backend.Temporary, Is.Null);
        });
    }

    [Test]
    public void ConditionalCameraFilterJoinsItsPlaneAtTheTriggerAndResetsOnRestart()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");

        var item = new CameraFilterEvent
        {
            EventType = EventType.SetFilterAdvanced,
            Filter = "CameraFilterPack_Color_Invert",
            EventTag = "hit",
            AngleOffset = 180,
            Plane = "1",
            TargetType = "Decoration",
        };
        PlaybackEvent[] events = [new ConditionalDecorationEvent { Perfect = "hit" }, item];
        Floor[] floors = [new(0, 180, default)];
        double[] times = [0], bpm = [120];
        using var front = FilterTimeline.Build(events, floors, times, bpm, false);
        using var filters = FilterTimeline.Build(events, floors, times, bpm, false, background: true)!;
        var triggers = ImageDecorationTimeline.Build(events, floors, times, bpm, null, [], CancellationToken.None)!;
        triggers.BindCameraFilters(floors, bpm, front, filters);
        var backend = new RecordingBackend();
        Assert.That(front, Is.Null);
        Assert.That(filters.HasDueEvent(1), Is.False);
        triggers.TriggerCondition(0, HitMargin.Perfect, 1.5);
        Assert.That(filters.Evaluate(backend, 1.99), Is.Empty);
        Assert.That(filters.Evaluate(backend, 2).Single().Name, Is.EqualTo(item.Filter));
        triggers.ResetTriggers();
        Assert.That(filters.Evaluate(backend, 3), Is.Empty);
    }

    [Test]
    public void AdvancedIntegerParameterTweensFromNeutralAndPreservesZeroFailure()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        Assert.That(GeneratedFilterRegistry.Factories.TryGetValue("CameraFilterPack_Blur_Movie", out var create), Is.True);

        var item = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter":"CameraFilterPack_Blur_Movie","filter_FastFilter":8}
            """)!;
        var scheduled = new ScheduledEvent<CameraFilterEvent>(item, 0,
            new TrackTiming(0, 2, EventEnums.Ease.Linear));
        var backend = new RecordingBackend();
        using var filters = new FilterTimeline([scheduled], legacyTween: false);

        var active = filters.Evaluate(backend, 0).Single();
        Assert.Multiple(() =>
        {
            Assert.That(active.Filter.GetParameter("FastFilter")!.Value.X, Is.EqualTo(0f));
            Assert.Throws<DivideByZeroException>(() => active.Filter.Render(new Target(128, 96), new Target(128, 96)));
        });
        filters.Evaluate(backend, 1);
        Assert.That(active.Filter.GetParameter("FastFilter")!.Value.X, Is.EqualTo(4f).Within(1e-6f));
        filters.Evaluate(backend, 2);
        Assert.That(active.Filter.GetParameter("FastFilter")!.Value.X, Is.EqualTo(8f).Within(1e-6f));
    }

    [Test]
    public void FastFilterFiltersRenderWithTheirDefaults()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");

        int checkedFilters = 0;
        foreach (var (name, create) in GeneratedFilterRegistry.Factories)
        {
            var backend = new RecordingBackend();
            using var filter = create(backend);
            filter.Enable();
            if (!filter.Parameters.Contains("FastFilter"))
                continue;

            checkedFilters++;
            Assert.That(filter.GetParameter("FastFilter")!.Value.X, Is.GreaterThanOrEqualTo(1f), name);
            Assert.DoesNotThrow(() => filter.Render(new Target(128, 96), new Target(128, 96)), name);
        }
        Assert.That(checkedFilters, Is.GreaterThan(0));
    }

    [Test]
    public void AllGeneratedFiltersRenderWithTheirDefaults()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");

        var failures = new List<string>();
        foreach (var (name, create) in GeneratedFilterRegistry.Factories)
        {
            try
            {
                var backend = new RecordingBackend();
                using var filter = create(backend);
                filter.Enable();
                filter.Render(new Target(128, 96), new Target(128, 96));
            }
            catch (Exception error)
            {
                failures.Add(name + ": " + error.GetType().Name + ": " + error.Message);
            }
        }
        Assert.That(failures, Is.Empty);
    }

    [TestCase("CameraFilterPack_Blur_Movie")]
    [TestCase("CameraFilterPack_Blur_Blurry")]
    [TestCase("CameraFilterPack_Blur_Tilt_Shift_Hole")]
    public void ExplicitZeroFastFilterPreservesOriginalArithmeticFailure(string name)
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        Assert.That(GeneratedFilterRegistry.Factories.ContainsKey(name), Is.True);

        var item = JsonConvert.DeserializeObject<CameraFilterEvent>("""
            {"filter_FastFilter":0}
            """)!;
        item.Filter = name;
        var scheduled = new ScheduledEvent<CameraFilterEvent>(item, 0,
            new TrackTiming(0, 1, EventEnums.Ease.Linear));
        var backend = new RecordingBackend();
        using var filters = new FilterTimeline([scheduled], legacyTween: false);

        var active = filters.Evaluate(backend, 1).Single();
        Assert.That(active.Filter.GetParameter("FastFilter")!.Value.X, Is.EqualTo(0f));
        Assert.Throws<DivideByZeroException>(() => active.Filter.Render(new Target(128, 96), new Target(128, 96)));
    }

    [Test]
    public void LegacyTweenCompletesBeforeReplacementAndRewinds()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        Assert.That(GeneratedFilterRegistry.Legacy.ContainsKey("Grayscale"), Is.True);

        var backend = new RecordingBackend();
        ScheduledEvent<CameraFilterEvent> Start(double time, float intensity) => new(
            new CameraFilterEvent
            {
                EventType = EventType.SetFilter,
                Filter = "Grayscale",
                FilterEnabled = true,
                Intensity = intensity,
            }, 0, new TrackTiming(time, 2, EventEnums.Ease.Linear));

        using var filters = new FilterTimeline([Start(0, 100), Start(1, 20)], legacyTween: false);
        float IntensityAt(double time) => filters.Evaluate(backend, time).Single().Filter.GetParameter("Intensity")!.Value.X;

        Assert.Multiple(() =>
        {
            Assert.That(IntensityAt(0.5), Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(IntensityAt(1), Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(IntensityAt(2), Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(IntensityAt(0.5), Is.EqualTo(0.25f).Within(1e-6f));
        });
    }

    [Test]
    public void DurationDefersLegacyOnAndOffUntilTheirFirstTweenSample()
    {
        ScheduledEvent<CameraFilterEvent> Event(double start, bool enabled) => new(
            new CameraFilterEvent { EventType = EventType.SetFilter, Filter = "17", FilterEnabled = enabled },
            0, new TrackTiming(start, 1, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([Event(0, true), Event(2, false)], false);
        var backend = new RecordingBackend();
        Assert.That(filters.Evaluate(backend, 0), Is.Empty);
        Assert.That(filters.HasDueEvent(0.5), Is.True);
        var motion = (CameraMotionBlurFilter)filters.Evaluate(backend, 0.5).Single().Filter;
        Assert.That(motion.VelocityScale, Is.EqualTo(.1875f));
        Assert.That(filters.Evaluate(backend, 2).Count, Is.EqualTo(1));
        Assert.That(filters.Evaluate(backend, 2.1), Is.Empty);
    }

    [Test]
    public void InactiveTimelineSleepsUntilTheNextEventAndReplaysOnRewind()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        ScheduledEvent<CameraFilterEvent> Event(double time, bool enabled) => new(
            new CameraFilterEvent { EventType = EventType.SetFilterAdvanced,
                Filter = "CameraFilterPack_Color_Invert", FilterEnabled = enabled },
            0, new TrackTiming(time, 0, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([Event(1, true), Event(2, false), Event(5, true)], false);
        var backend = new RecordingBackend();
        Assert.That(filters.HasDueEvent(0), Is.False);
        Assert.That(filters.HasDueEvent(1), Is.True);
        Assert.That(filters.Evaluate(backend, 1), Has.Count.EqualTo(1));
        Assert.That(filters.HasDueEvent(1.5), Is.True);
        Assert.That(filters.Evaluate(backend, 2), Is.Empty);
        Assert.That(filters.HasDueEvent(4), Is.False);
        Assert.That(filters.HasDueEvent(5), Is.True);
        Assert.That(filters.Evaluate(backend, 5), Has.Count.EqualTo(1));
        Assert.That(filters.HasDueEvent(0), Is.True);
        Assert.That(filters.Evaluate(backend, 0), Is.Empty);
        Assert.That(filters.HasDueEvent(0), Is.False);
        Assert.That(filters.Evaluate(backend, 1), Has.Count.EqualTo(1));
    }

    [Test]
    public void DisableOthersUpdatesCachedMembershipAndKeepsAuthoredOrder()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");
        const string first = "CameraFilterPack_Color_Invert", second = "CameraFilterPack_Blur_Movie";
        ScheduledEvent<CameraFilterEvent> Event(string name, double time, bool disableOthers = false) => new(
            new CameraFilterEvent { EventType = EventType.SetFilterAdvanced, Filter = name,
                FilterEnabled = true, DisableOthers = disableOthers },
            0, new TrackTiming(time, 1, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([Event(first, 0), Event(second, 1), Event(second, 2, true), Event(first, 3)], false);
        var backend = new RecordingBackend();
        Assert.That(filters.Evaluate(backend, .5).Select(entry => entry.Name), Is.EqualTo(new[] { first }));
        Assert.That(filters.Evaluate(backend, 1.5).Select(entry => entry.Name), Is.EqualTo(new[] { first, second }));
        Assert.That(filters.Evaluate(backend, 2.5).Select(entry => entry.Name), Is.EqualTo(new[] { second }));
        Assert.That(filters.Evaluate(backend, 3.5).Select(entry => entry.Name), Is.EqualTo(new[] { first, second }));
        Assert.That(filters.Evaluate(backend, .5).Select(entry => entry.Name), Is.EqualTo(new[] { first }));
    }

    [Test]
    public void LegacyFiltersUseCameraComponentOrder()
    {
        if (GeneratedFilterRegistry.Coverage.Length == 0)
            Assert.Ignore("Build with AdofaiGameDir to exercise generated filters.");

        ScheduledEvent<CameraFilterEvent> Enable(string name) => new(
            new CameraFilterEvent { EventType = EventType.SetFilter, Filter = name, FilterEnabled = true },
            0, new TrackTiming(0, 0, EventEnums.Ease.Linear));
        using var filters = new FilterTimeline([Enable("Contrast"), Enable("Blizzard"), Enable("Sepia")], false);
        var passes = filters.Evaluate(new RecordingBackend(), 0);
        int bloomOrder = CameraPassOrder.Legacy(VideoBloomPass.ShaderName, 0);

        Assert.Multiple(() =>
        {
            Assert.That(passes.Select(pass => pass.Name), Is.EqualTo(new[]
            {
                "CameraFilterPackLegacy_Color_Sepia",
                "CameraFilterPackLegacy_Blizzard",
                "CameraFilterPackLegacy_Color_Contrast",
            }));
            Assert.That(bloomOrder, Is.GreaterThan(passes[1].Order).And.LessThan(passes[2].Order));
        });
    }

    private sealed class Target(int width, int height) : IFilterTarget
    {
        public int Width => width;
        public int Height => height;
        public FilterSampling Sampling { get; set; }
    }

    private sealed class RecordingBackend : IFilterBackend, IFilterMaterial, IFilterShader
    {
        public List<string> Resources { get; } = [];
        public List<string> Textures { get; } = [];
        public Dictionary<string, float> Floats { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Vector4> Vectors { get; } = new(StringComparer.Ordinal);
        public List<(int Source, int Destination, bool Material, int Pass)> Draws { get; } = [];
        public (int Width, int Height, int Depth) Rented { get; private set; }
        public Target? Temporary { get; private set; }
        public IFilterTarget? Released { get; private set; }
        public float DeltaTime => 0.02f;
        public int ScreenWidth => 128;
        public int ScreenHeight => 96;

        public IFilterShader FindShader(string name)
        {
            Resources.Add(name);
            return this;
        }

        public IFilterTexture LoadTexture(string name)
        {
            Resources.Add(name);
            return new Target(8, 8);
        }

        public IFilterMaterial CreateMaterial(IFilterShader shader) => this;
        public void ReleaseMaterial(IFilterMaterial material) { }
        public IFilterTarget RentTarget(int width, int height, int depthBits)
        {
            Rented = (width, height, depthBits);
            return Temporary = new Target(width, height);
        }

        public void ReleaseTarget(IFilterTarget target) => Released = target;
        public void Blit(IFilterTarget source, IFilterTarget destination, IFilterMaterial? material = null, int pass = -1) =>
            Draws.Add((source.Width, destination.Width, material is not null, pass));
        public void SetFloat(string name, float value) => Floats[name] = value;
        public void SetInt(string name, int value) => Floats[name] = value;
        public void SetVector(string name, Vector4 value) => Vectors[name] = value;
        public void SetTexture(string name, IFilterTexture? value) => Textures.Add(name);
    }
}
