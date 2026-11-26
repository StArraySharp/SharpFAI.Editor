using System.IO.Compression;
using System.Text.Json;
using NUnit.Framework;
using SharpFAI.Editor.Mods;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor.Tests;

public sealed class ModSettingsTests
{
    private const string ModId = "com.sharpfai.web-mirror";
    private const string PackageName = "SharpFAI.WebMirror.sharpfaimod";
    private const string SettingsName = "SharpFAI.WebMirror.Settings.json";
    private static string PackagePath => Path.Combine(AppContext.BaseDirectory, "ModFixtures", PackageName);

    [Test]
    public async Task PanelLoadsAndSavesOnlyMarkedMembersAndClampsRanges()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-settings-").AsScope();
        await using var host = new ModHost(UiHost.Editor, root.Path);
        var owner = new ModActivation(host, ModPackage.ReadArchive(PackagePath), Path.Combine(root.Path, "shadow"));
        try
        {
            string path = Path.Combine(root.Path, SettingsName);
            File.WriteAllText(path, """{"_speed":99,"Mode":"Off","Ignored":"discard","Session":false,"OtherVersion":8}""");
            var mod = new FixtureSettings();
            var panel = new ModSettingsPanel(mod, path, owner.Scope);
            panel.Load();
            Assert.That(mod.Speed, Is.EqualTo(8));
            Assert.That(mod.Mode, Is.EqualTo(FixtureSettings.PlayMode.Off));
            Assert.That(mod.Session, Is.True);
            Assert.That(mod.Ignored, Is.EqualTo("initial"));
            Assert.That(mod.Calls, Is.Zero);
            panel.Apply(new Dictionary<string, object?>
            {
                ["_speed"] = 5,
                ["Mode"] = FixtureSettings.PlayMode.On,
                ["Session"] = false,
            });
            Assert.That(mod.Calls, Is.EqualTo(1));
            Assert.That(mod.Session, Is.False);
            using var written = JsonDocument.Parse(File.ReadAllText(path));
            Assert.That(written.RootElement.GetProperty("_speed").GetInt32(), Is.EqualTo(5));
            Assert.That(written.RootElement.GetProperty("Mode").GetString(), Is.EqualTo("On"));
            Assert.That(written.RootElement.GetProperty("OtherVersion").GetInt32(), Is.EqualTo(8));
            Assert.That(written.RootElement.TryGetProperty("Session", out _), Is.False);
            Assert.That(written.RootElement.TryGetProperty("Ignored", out _), Is.False);
        }
        finally { await owner.StopAsync(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DropInLoadsSidecarBeforeActivationWithoutChangingTrust(bool unpacked)
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-settings-").AsScope();
        using var windowStream = new EditorWindowStream();
        string archive = Path.Combine(root.Path, PackageName);
        string settings = Path.Combine(root.Path, SettingsName);
        if (unpacked) ZipFile.ExtractToDirectory(PackagePath, archive);
        else File.Copy(PackagePath, archive);
        File.WriteAllText(settings, """{"AutoStart":false,"FramesPerSecond":10}""");
        var package = unpacked ? ModPackage.Read(archive, development: false, isDropIn: true) : ModPackage.ReadArchive(archive);
        Assert.That(package.SettingsPath, Is.EqualTo(settings));
        await using (var host = CreateHost(root.Path, windowStream))
        {
            await host.InitializeAsync();
            await host.EnableAsync(ModId);
            AssertStoppedWithSettings(host);
        }

        File.WriteAllText(settings, """{"AutoStart":false,"FramesPerSecond":15}""");
        await using var restarted = CreateHost(root.Path, windowStream);
        await restarted.InitializeAsync();
        AssertStoppedWithSettings(restarted);
    }

    [Test]
    public async Task RemovingInstalledPackagePreservesSettingsBesideTheVersionDirectory()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-settings-").AsScope();
        using var windowStream = new EditorWindowStream();
        await using var host = CreateHost(root.Path, windowStream);
        await host.InstallAsync(PackagePath);
        string parent = Path.Combine(root.Path, "packages", ModId);
        string version = Directory.GetDirectories(parent).Single();
        string config = Path.Combine(parent, SettingsName);
        File.WriteAllText(config, """{"AutoStart":false}""");
        Assert.That(ModPackage.Read(version, development: false).SettingsPath, Is.EqualTo(config));
        await host.EnableAsync(ModId);
        AssertStoppedWithSettings(host);
        await host.RemoveAsync(ModId);
        Assert.That(Directory.Exists(version), Is.False);
        Assert.That(File.Exists(config), Is.True);
        await host.InstallAsync(PackagePath);
        await host.EnableAsync(ModId);
        AssertStoppedWithSettings(host);
    }

    private static ModHost CreateHost(string root, EditorWindowStream windowStream) => new(UiHost.Editor, root)
    {
        ExternalService = type => type == typeof(IEditorWindowStream) ? windowStream : null,
    };

    private static void AssertStoppedWithSettings(ModHost host)
    {
        Assert.That(host.Notices, Is.Empty);
        Assert.That(host.Entries<RegisteredSettings>(UiHost.Editor).Single().IsRegistered, Is.True);
        var window = host.Entries<RegisteredWindow>(UiHost.Editor).Single().Value.Create();
        try
        {
            var nodes = window.Build(new(new(null, new(PlaybackMode.Edit, 0, 0, 0, false)), false));
            Assert.That(nodes.OfType<UiButton>().Single(node => node.Id == "start").Enabled, Is.True);
            Assert.That(nodes.OfType<UiButton>().Single(node => node.Id == "stop").Enabled, Is.False);
        }
        finally { window.DisposeAsync().GetAwaiter().GetResult(); }
    }

    private sealed class FixtureSettings : IModSettings
    {
        public enum PlayMode { Off, On }

        [ModSettingLabel("Speed"), ModSettingRange(1, 8)]
        private int _speed = 3;
        public int Speed => _speed;
        [ModSetting]
        public PlayMode Mode { get; private set; } = PlayMode.On;
        [ModSettingNoSave]
        public bool Session { get; private set; } = true;
        [ModSetting, ModSettingIgnore]
        public string Ignored { get; private set; } = "initial";
        public int Calls { get; private set; }
        public void OnSettingsChanged() => Calls++;
    }
}
