using System.Runtime;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace SharpFAI.Editor;

internal static class Program
{
    private const string Usage =
        "SharpFAI.Editor [level.adofai | levels.zip] [--properties LevelEditorProperties.txt] [--language en|zh-Hans] [--no-audio] [--no-mods]";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is [SharpFAI.Player.PlaybackAudio.ProbeArgument])
            return SharpFAI.Player.PlaybackAudio.RunProbe();
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(Usage);
            Console.WriteLine(SharpFAI.Player.PlayerLauncher.Usage);
            return 0;
        }

        try
        {
            return args is ["--player", .. var playerArgs]
                ? SharpFAI.Player.PlayerLauncher.Run(playerArgs, typeof(Program).Assembly,
                    disabled => new Mods.ModHost(SharpFAI.ModApi.UiHost.Player, safeMode: disabled))
                : RunEditor(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int RunEditor(string[] args)
    {
        string? levelPath = null;
        string? propertiesPath = null;
        string? language = null;
        bool noAudio = false;
        bool noMods = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--properties" && i + 1 < args.Length)
                propertiesPath = args[++i];
            else if (args[i] == "--language" && i + 1 < args.Length)
                language = args[++i];
            else if (args[i] == "--no-audio")
                noAudio = true;
            else if (args[i] == "--no-mods")
                noMods = true;
            else if (!args[i].StartsWith('-') && levelPath is null)
                levelPath = args[i];
            else
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }

        SharpFAI.Player.PlaybackAudio.Configure(typeof(Program).Assembly, noAudio);
        // Favor background collections when large charts keep many objects live.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        if (language is not null)
            L.Language = System.Globalization.CultureInfo.GetCultureInfo(language);
        EditorCatalog catalog = new(propertiesPath);
        LevelArchive? archiveChoices = null;
        if (levelPath is not null && LevelArchive.IsZip(levelPath))
        {
            LevelArchive archive = LevelArchive.Extract(levelPath);
            if (archive.Levels.Count == 1)
                levelPath = Path.Combine(archive.DirectoryPath, archive.Levels[0]);
            else
            {
                archiveChoices = archive;
                levelPath = null;
            }
        }
        EditorDocument document = new(levelPath);
        using EditorWindow window = new(new NativeWindowSettings
        {
            ClientSize = new Vector2i(1440, 860),
            MinimumClientSize = new Vector2i(960, 620),
            Title = "SharpFAI Editor",
            Icon = SharpFAI.Player.ApplicationIcon.Load(),
            APIVersion = new Version(4, 1),
            Profile = ContextProfile.Core,
            Vsync = VSyncMode.On,
        }, catalog, document, language) { DisableMods = noMods };
        if (archiveChoices is not null)
            window.ChooseArchiveLevel(archiveChoices);
        window.Run();
        return 0;
    }
}
