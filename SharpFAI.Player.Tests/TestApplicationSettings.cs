namespace SharpFAI.Player.Tests;

public sealed class TestApplicationSettings
{
    private DirectoryInfo _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("sharpfai-settings-");

    [TearDown]
    public void TearDown() => _directory.Delete(recursive: true);

    [Test]
    public void SaveAndReloadPreservesPreferencesAndReplacesPreviousFile()
    {
        string path = Path.Combine(_directory.FullName, "settings.json");
        var store = new ApplicationSettingsStore(path, ApplicationSettings.PlayerDefaults);
        var settings = new ApplicationSettings
        {
            VSync = true, WindowMode = ApplicationWindowMode.Fullscreen, FrameLimit = 144, ShowFrameRate = false,
            FirePlanetColor = "#AD15E0", IcePlanetColor = "#39FF88",
            VisibleDecorations = DecorationKinds.Image | DecorationKinds.Planet,
            MasterVolume = 50, MusicVolume = 70, HitsoundVolume = 25, SkipAudio = true, Language = "zh-Hans",
            DecorationTexturePercent = 50, PlanetTrailPercent = 175,
            CameraFilters = false, ScreenEffects = false, ScreenFlash = false, CameraShake = false,
            ShowSelectionStatistics = false, SelectionStatistics = SelectionStatisticsFields.Beats,
            ExcludeLastSelectedTileFromTiming = true, FineTrackRotation = false, TrackRotationStep = 45f,
            SyncTrackEventPositions = true,
        };

        Assert.That(store.TrySave(ApplicationSettings.PlayerDefaults), Is.True);
        Assert.That(store.TrySave(settings), Is.True);
        Assert.That(new ApplicationSettingsStore(path, ApplicationSettings.PlayerDefaults).Load(), Is.EqualTo(settings));
        Assert.That(_directory.GetFiles().Select(file => file.Name), Is.EqualTo(new[] { "settings.json" }));
    }

    [Test]
    public void InvalidValuesAreNormalizedOnLoad()
    {
        string path = Path.Combine(_directory.FullName, "settings.json");
        File.WriteAllText(path, """
            {"WindowMode":99,"FrameLimit":10,"MasterVolume":-3,"MusicVolume":101,"HitsoundVolume":63,"Language":"bad",
             "DecorationTexturePercent":0,"PlanetTrailPercent":250,
             "FirePlanetColor":"a123c9","IcePlanetColor":"invalid","VisibleDecorations":257}
            """);
        var store = new ApplicationSettingsStore(path, ApplicationSettings.PlayerDefaults);

        Assert.That(store.Load(), Is.EqualTo(ApplicationSettings.PlayerDefaults with
        {
            MasterVolume = 0, MusicVolume = 100, HitsoundVolume = 63,
            PlanetTrailPercent = 200,
            FirePlanetColor = "#A123C9", VisibleDecorations = DecorationKinds.Image,
        }));
    }

    [Test]
    public void OldWhitePlanetPreferenceMigratesToIndividualColors()
    {
        string path = Path.Combine(_directory.FullName, "settings.json");
        File.WriteAllText(path, """{"WhitePlanetBodies":true,"FirePlanetColor":"#123456"}""");
        var store = new ApplicationSettingsStore(path, ApplicationSettings.PlayerDefaults);

        var settings = store.Load();
        Assert.That(settings.FirePlanetColor, Is.EqualTo("#123456"));
        Assert.That(settings.IcePlanetColor, Is.EqualTo("#FFFFFF"));
        Assert.That(store.TrySave(settings), Is.True);
        Assert.That(File.ReadAllText(path), Does.Not.Contain("WhitePlanetBodies"));
    }

    [Test]
    public void MissingAndMalformedFilesUseApplicationDefaults()
    {
        string path = Path.Combine(_directory.FullName, "settings.json");
        var store = new ApplicationSettingsStore(path, ApplicationSettings.EditorDefaults);
        Assert.That(store.Load(), Is.EqualTo(ApplicationSettings.EditorDefaults));

        File.WriteAllText(path, "{");
        Assert.That(store.Load(), Is.EqualTo(ApplicationSettings.EditorDefaults));
    }

    [TestCase(true, 0, 120, 120)]
    [TestCase(true, 240, 120, 120)]
    [TestCase(true, 60, 120, 60)]
    [TestCase(true, 0, 144, 144)]
    [TestCase(true, 0, 0, 0)]
    [TestCase(true, 60, 0, 60)]
    [TestCase(false, 240, 120, 240)]
    [TestCase(false, 0, 120, 0)]
    public void VSyncUsesTheDisplayRefreshRateAndRespectsLowerLimits(bool vSync, int manualLimit, int refreshRate, int expected)
    {
        var settings = new ApplicationSettings { VSync = vSync, FrameLimit = manualLimit };
        Assert.That(settings.EffectiveFrameLimit(refreshRate), Is.EqualTo(expected));
    }

    [Test]
    public void FailedSaveDoesNotLeaveATemporaryFile()
    {
        string path = _directory.CreateSubdirectory("settings.json").FullName;
        var store = new ApplicationSettingsStore(path, ApplicationSettings.EditorDefaults);
        Assert.That(store.TrySave(ApplicationSettings.PlayerDefaults), Is.False);
        Assert.That(_directory.GetFiles(), Is.Empty);
        Assert.That(Directory.Exists(path), Is.True);
    }
}
