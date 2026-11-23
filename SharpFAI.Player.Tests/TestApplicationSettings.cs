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
            VSync = true, FrameLimit = 144, ShowFrameRate = false,
            MasterVolume = 50, MusicVolume = 70, HitsoundVolume = 25, SkipAudio = true, Language = "zh-Hans",
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
        File.WriteAllText(path, """{"FrameLimit":10,"MasterVolume":-3,"MusicVolume":101,"HitsoundVolume":63,"Language":"bad"}""");
        var store = new ApplicationSettingsStore(path, ApplicationSettings.PlayerDefaults);

        Assert.That(store.Load(), Is.EqualTo(ApplicationSettings.PlayerDefaults with
        {
            MasterVolume = 0, MusicVolume = 100, HitsoundVolume = 63,
        }));
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
