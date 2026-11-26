using System.IO.Compression;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor.Mods;
using SharpFAI.ModApi;
using SharpFAI.Player.Engine;
using SharpFAI.Player.ModSupport;
using ModRuntime = SharpFAI.Editor.Mods.ModHost;

namespace SharpFAI.Editor.Tests;

public sealed class ModHostTests
{
    private const string Base = "com.sharpfai.example.base";
    private const string Tools = "com.sharpfai.example.tools";

    [TestCase(UiHost.Editor)]
    [TestCase(UiHost.Player)]
    public async Task StartupCreatesMissingModSearchDirectory(UiHost target)
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        string search = Path.Combine(root.Path, "Mods");
        await using var host = new ModRuntime(target, search);
        Assert.That(Directory.Exists(search), Is.False);
        await host.InitializeAsync();
        Assert.That(Directory.Exists(search), Is.True);
        Assert.That(host.Catalog, Is.Empty);
    }

    [Test]
    public async Task InstalledDependencyCascadesEnableReloadAndDisable()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        await using var host = new ModRuntime(UiHost.Editor, root.Path);
        await host.InitializeAsync();
        await host.InstallAsync(Package("Tools"));
        Assert.ThrowsAsync<InvalidOperationException>(() => host.EnableAsync(Tools));
        await host.InstallAsync(Package("Base"));
        Assert.That(host.Catalog.All(entry => !entry.Enabled), Is.True);
        await host.EnableAsync(Tools);
        Assert.That(host.Catalog.Where(entry => entry.Enabled).Select(entry => entry.Manifest.Id),
            Is.EqualTo(new[] { Base, Tools }));
        Assert.That(host.Catalog.Single(entry => entry.Manifest.Id == Base).Requested, Is.False);
        Assert.That(host.Notices.Any(notice => notice.Message.Contains("Activated with Example Base")), Is.True);
        var old = host.Entries<RegisteredWindow>(UiHost.Editor).Single();
        int generation = host.Generation;
        await host.ReloadAsync(Base);
        Assert.That(host.Generation, Is.GreaterThan(generation));
        Assert.That(host.Entries<RegisteredWindow>(UiHost.Editor).Single(), Is.Not.SameAs(old));
        Assert.That(old.Acquire(), Is.Null);
        await host.DisableAsync(Base);
        Assert.That(host.Catalog.All(entry => !entry.Enabled && !entry.Requested), Is.True);
        Assert.That(host.Entries<RegisteredWindow>(UiHost.Editor), Is.Empty);
    }

    [Test]
    public async Task MenuAttributesRegisterLocalizedPathsAndInvokeTheModInstance()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        await using var host = new ModRuntime(UiHost.Editor, root.Path);
        await host.InstallAsync(Package("Base"));
        await host.InstallAsync(Package("Tools"));
        var windows = new TrackingWindows();
        host.WindowOperations = windows;
        host.Culture = () => System.Globalization.CultureInfo.GetCultureInfo("zh-Hans");
        await host.EnableAsync(Tools);

        var menu = host.Entries<MenuBarMenuDescriptor>(UiHost.Editor).Single();
        Assert.That(menu.Text(menu.Value.TitleKey), Is.EqualTo("谱面工具"));
        var item = host.Entries<MenuContribution>(UiHost.Editor).Single(entry => entry.Value.Target.Slot == MenuSlot.Tools);
        Assert.That(item.Value.Submenu, Is.EqualTo(new[] { "menu" }));
        var command = host.Entries<RegisteredCommand>(UiHost.Editor).Single(entry => entry.Descriptor.Id == item.Value.CommandId);
        Assert.That(command.Text(command.Value.Descriptor.TitleKey), Is.EqualTo("打开谱面分析"));
        await command.Value.Run(new(null, new(PlaybackMode.Edit, 0, 0, 0, false)), default);
        Assert.That(windows.Opened, Is.EqualTo(new ToolWindowId(Tools + "/analysis")));
        Assert.That(host.Entries<RegisteredCommand>(UiHost.Player), Is.Empty);
        Assert.That(host.Entries<RealtimeAudioHook>(UiHost.Export), Has.Count.EqualTo(1));
        await host.DisableAsync(Tools);
        Assert.That(host.Entries<MenuContribution>(UiHost.Editor), Is.Empty);
    }

    [Test]
    public async Task LegacyManifestDeclarationsAreIgnoredAndOnlyTheEditorCanEnableMods()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        string source = Path.Combine(root.Path, "Example.Base");
        ZipFile.ExtractToDirectory(Package("Base"), source);
        string manifest = Path.Combine(source, "Example.Base.json");
        var content = JObject.Parse(File.ReadAllText(manifest));
        content["hosts"] = new JArray("player");
        content["requiredCapabilities"] = new JArray("example.unavailable");
        content["optionalCapabilities"] = new JArray("example.optional");
        File.WriteAllText(manifest, content.ToString());
        await using var player = new ModRuntime(UiHost.Player, root.Path);
        await player.InitializeAsync();
        Assert.That(player.Catalog.Single().Unavailable, Is.Not.Null);
        Assert.ThrowsAsync<InvalidOperationException>(() => player.EnableAsync(Base));

        await using var editor = new ModRuntime(UiHost.Editor, root.Path);
        await editor.InitializeAsync();
        await editor.EnableAsync(Base);
        Assert.That(editor.Catalog.Single().Enabled, Is.True);
    }

    [Test]
    public async Task ServiceLookupUsesSdkTypesAndReportsMissingRequiredServices()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        await using var host = new ModRuntime(UiHost.Editor, root.Path);
        var activation = new ModActivation(host, ModPackage.ReadArchive(Package("Base")), root.Path);
        try
        {
            IModContext context = activation.Scope;
            Assert.That(context.GetRequiredService<IToolWindowRegistry>(), Is.SameAs(context.GetService<IToolWindowRegistry>()));
            Assert.That(context.GetService<IUiDialogs>(), Is.Null);
            Assert.That(context.GetService<IPlayerCommands>(), Is.Null);
            Assert.That(context.GetService<object>(), Is.Null);
            Assert.That(() => context.GetRequiredService<IUiDialogs>(),
                Throws.InvalidOperationException.With.Message.Contains(nameof(IUiDialogs)).And.Message.Contains(Base));
        }
        finally { await activation.StopAsync(); }
    }

    private sealed class TrackingWindows : IModWindowOperations
    {
        public ToolWindowId? Opened { get; private set; }
        public ToolWindowState State(ToolWindowId id) => default;
        public ValueTask<WindowResult> OpenAsync(ToolWindowId id, CancellationToken cancellationToken)
        {
            Opened = id;
            return ValueTask.FromResult(WindowResult.Opened);
        }
        public ValueTask<WindowResult> CloseAsync(ToolWindowId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(WindowResult.Unavailable);
    }

    [Test]
    public async Task SearchDirectoryLoadsArchivesAndUnpackedModsWithPersistentApproval()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        string archive = Path.Combine(root.Path, "Example.Tools.sharpfaimod");
        string unpacked = Path.Combine(root.Path, "Example.Base.sharpfaimod");
        File.Copy(Package("Tools"), archive);
        await using (var host = new ModRuntime(UiHost.Editor, root.Path))
        {
            await host.InitializeAsync();
            Assert.That(host.Catalog.Select(item => item.Manifest.Id), Is.EqualTo(new[] { Tools }));
            Assert.ThrowsAsync<InvalidOperationException>(() => host.EnableAsync(Tools));
            ZipFile.ExtractToDirectory(Package("Base"), unpacked);
            await host.RefreshAsync();
            Assert.That(host.Catalog.Select(item => item.Manifest.Id), Is.EqualTo(new[] { Base, Tools }));
            Assert.That(host.Catalog.All(item => !item.Enabled && !item.CanRemove), Is.True);
            await host.EnableAsync(Tools);
            Assert.That(host.Catalog.All(item => item.Enabled), Is.True);
            Assert.That(host.Entries<RegisteredWindow>(UiHost.Editor), Has.Count.EqualTo(1));
        }
        await using var restarted = new ModRuntime(UiHost.Editor, root.Path);
        await restarted.InitializeAsync();
        Assert.That(restarted.Catalog.All(item => item.Enabled), Is.True);
        File.Delete(archive);
        await restarted.RefreshAsync();
        Assert.That(restarted.Catalog.Single(item => item.Manifest.Id == Tools).Enabled, Is.True);
        await restarted.DisableAsync(Tools);
        Assert.That(restarted.Catalog.Select(item => item.Manifest.Id), Is.EqualTo(new[] { Base }));
        Assert.That(File.Exists(Path.Combine(unpacked, "Example.Base.dll")), Is.True);
    }

    [Test]
    public async Task DisablingAModDoesNotRequireOtherActivePackagesToRemainOnDisk()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        string baseArchive = Path.Combine(root.Path, "Example.Base.sharpfaimod");
        File.Copy(Package("Base"), baseArchive);
        File.Copy(Package("Tools"), Path.Combine(root.Path, "Example.Tools.sharpfaimod"));
        await using var host = new ModRuntime(UiHost.Editor, root.Path);
        await host.InitializeAsync();
        await host.EnableAsync(Base);
        await host.EnableAsync(Tools);

        File.Delete(baseArchive);
        await host.DisableAsync(Tools);

        Assert.That(host.Catalog.Where(entry => entry.Enabled).Select(entry => entry.Manifest.Id), Is.EqualTo(new[] { Base }));
        Assert.That(host.Entries<MessageEndpoint>(UiHost.Editor), Has.Count.EqualTo(1));
        await host.DisableAsync(Base);
        Assert.That(host.Entries<MessageEndpoint>(UiHost.Editor), Is.Empty);
    }

    [Test]
    public async Task FailedHotReloadKeepsRunningVersionAndChangedFilesNeedTrustAtStartup()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        string development = Path.Combine(root.Path, "development");
        ZipFile.ExtractToDirectory(Package("Base"), development);
        string manifest = Path.Combine(development, "Example.Base.json");
        await using (var host = new ModRuntime(UiHost.Editor, Path.Combine(root.Path, "store")))
        {
            await host.InitializeAsync();
            await host.AddDevelopmentAsync(manifest);
            await host.EnableAsync(Base);
            var old = host.Entries<MessageEndpoint>(UiHost.Editor).Single();
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("BaseMod", "MissingMod", StringComparison.Ordinal));
            Assert.ThrowsAsync<TypeLoadException>(() => host.ReloadAsync(Base));
            Assert.That(host.Entries<MessageEndpoint>(UiHost.Editor).Single(), Is.SameAs(old));
            Assert.That(old.IsRegistered, Is.True);
        }
        await using var restarted = new ModRuntime(UiHost.Editor, Path.Combine(root.Path, "store"));
        await restarted.InitializeAsync();
        Assert.That(restarted.Entries<MessageEndpoint>(UiHost.Editor), Is.Empty);
        Assert.That(restarted.Notices.Any(notice => notice.Message.Contains("needs approval")), Is.True);
    }

    [Test]
    public async Task PackageInstallRejectsEscapingEntries()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        string archive = Path.Combine(root.Path, "invalid.sharpfaimod");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry("../escape").Open())
            stream.WriteByte(1);
        await using var host = new ModRuntime(UiHost.Editor, Path.Combine(root.Path, "store"));
        Assert.ThrowsAsync<InvalidDataException>(() => host.InstallAsync(archive));
        Assert.That(host.Catalog, Is.Empty);
        Assert.That(File.Exists(Path.Combine(root.Path, "store", "packages", "escape")), Is.False);
    }

    [Test]
    public void ModEditIsAtomicAndUndoesInOneStep()
    {
        var chart = new EditorDocument();
        ChartRevision revision = chart.ModRevision;
        var valid = new SetChartSetting("bpm", JsonSerializer.SerializeToElement(155));
        Assert.That(chart.ApplyModEdits(revision, new("Failed edit", [valid, new DeleteAngles(900, 1)])),
            Is.EqualTo(EditResult.Invalid));
        Assert.That(chart.ModRevision, Is.EqualTo(revision));
        Assert.That(chart.CanUndo, Is.False);
        Assert.That(chart.ApplyModEdits(revision, new("Two changes", [valid, new InsertAngles(chart.LastFloor, [90])])),
            Is.EqualTo(EditResult.Applied));
        Assert.That(chart.ModView(0, false).ReadSetting("bpm")!.Value.GetDouble(), Is.EqualTo(155));
        chart.Undo();
        Assert.That(chart.CanUndo, Is.False);
        Assert.That(chart.Modified, Is.False);
        chart.Redo();
        Assert.That(chart.ModView(0, false).ReadSetting("bpm")!.Value.GetDouble(), Is.EqualTo(155));
    }

    [Test]
    public void CustomEventRestoresAndMovesWithItsAnchor()
    {
        var chart = new EditorDocument();
        chart.InsertTiles(chart.LastFloor, [0, 90]);
        int floor = chart.LastFloor;
        chart.AddModEvent("example.test/flash", new("Flash", 1, []), floor);
        chart.Undo();
        Assert.That(chart.ModEvents, Is.Null);
        chart.Redo();
        Assert.That(chart.ModEvents!.Count, Is.EqualTo(1));
        chart.InsertTiles(floor - 1, [180]);
        Assert.That(chart.ModEvents![0]!["anchor"]!["floor"]!.Value<int>(), Is.EqualTo(floor + 1));
        chart.DeleteTiles(floor + 1, floor + 1);
        Assert.That(chart.ModEvents, Is.Empty);
        chart.Undo();
        Assert.That(chart.ModEvents![0]!["anchor"]!["floor"]!.Value<int>(), Is.EqualTo(floor + 1));
    }

    [Test]
    public async Task PreprocessorCacheReadsOnlyDeclaredFieldsAndClearsOnReload()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        await using var host = new ModRuntime(UiHost.Editor, root.Path);
        await host.InstallAsync(Package("Base"));
        await host.InstallAsync(Package("Tools"));
        await host.EnableAsync(Tools);
        using var renderer = new LevelRenderer { Mods = host, ModTarget = UiHost.Editor };
        var chart = SharpFAI.Serialization.Level.CreateNewLevel();
        string path = Path.Combine(root.Path, "test.adofai");
        var entry = host.Entries<RegisteredWindow>(UiHost.Editor).Single();
        await using var panel = entry.Value.Create();
        Assert.That(renderer.CanUpdateModEditorRevision, Is.True);
        renderer.PrepareModChart(chart, path, default);
        Assert.That(Count(panel), Is.EqualTo(1));
        chart.PutSetting("artist", "Unrelated");
        renderer.PrepareModChart(chart, path, default);
        Assert.That(Count(panel), Is.EqualTo(1));
        chart.PutSetting("bpm", 170);
        renderer.PrepareModChart(chart, path, default);
        Assert.That(Count(panel), Is.EqualTo(2));
        await host.ReloadAsync(Base);
        await using var reloaded = host.Entries<RegisteredWindow>(UiHost.Editor).Single().Value.Create();
        renderer.PrepareModChart(chart, path, default);
        Assert.That(Count(reloaded), Is.EqualTo(1));

        static int Count(IToolWindow window)
        {
            var rows = window.Build(new(new(null, new(PlaybackMode.Edit, 0, 0, 0, false)), false));
            return int.Parse(((UiText)rows[^1]).Value.Split(' ')[^1]);
        }
    }

    [Test]
    public async Task FlatPackageCompatExportWritesNativeFlashAndPreservesSource()
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mods-").AsScope();
        await using var host = new ModRuntime(UiHost.Editor, root.Path);
        using (var zip = ZipFile.OpenRead(Package("Tools")))
            Assert.That(zip.Entries.Select(entry => entry.FullName),
                Is.EquivalentTo(new[] { "Example.Tools.dll", "Example.Tools.json" }));
        await host.InstallAsync(Package("Base"));
        await host.InstallAsync(Package("Tools"));
        await host.EnableAsync(Tools);
        var document = new EditorDocument();
        document.InsertTiles(document.LastFloor, [0, 90]);
        var type = host.Entries<CustomEventCompiler>(UiHost.Editor).Single();
        document.AddModEvent(type.Descriptor.Id, type.Value.Schema!, 1);
        document.AddModEvent("example.missing/unknown", new("Unknown", 1, []), 2);
        var source = document.SnapshotForPlayback();
        var outcome = await CompatibleChartExport.CreateAsync(source,
            host.Entries<CompatibilityConverter>(UiHost.Editor), default);
        Assert.That(outcome.Converted, Is.EqualTo(1));
        Assert.That(outcome.Notices.Any(item => item.Contains("example.missing/unknown")), Is.True);
        Assert.That(source.root["sharpFaiMods"]?["events"], Has.Count.EqualTo(2));
        Assert.That(outcome.Level.root["sharpFaiMods"], Is.Null);
        string output = Path.Combine(root.Path, "native.adofai");
        EditorFileStorage.WriteChart(output, outcome.Level.root, overwrite: false);
        var reopened = new SharpFAI.Serialization.Level(output);
        var flash = reopened.actions.OfType<JObject>().Single(item => item.Value<string>("eventType") == "Flash");
        Assert.That(flash.Value<int>("floor"), Is.EqualTo(1));
        Assert.That(flash.Value<double>("duration"), Is.GreaterThan(0));
        Assert.That(reopened.angles.Count, Is.EqualTo(source.angles.Count));
    }

    private static string Package(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory,
        "ModFixtures", $"Example.{name}.sharpfaimod");
}

internal static class ModTestDirectory
{
    internal static Scope AsScope(this DirectoryInfo directory) => new(directory.FullName);

    internal sealed class Scope(string path) : IDisposable
    {
        public string Path => path;
        public void Dispose()
        {
            if (path.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                System.IO.Path.GetFileName(path).StartsWith("sharpfai-mods-", StringComparison.Ordinal) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
