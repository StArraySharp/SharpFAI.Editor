using System.Runtime;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace SharpFAI.Editor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? levelPath = null;
        string? propertiesPath = null;
        string? language = null;
        bool webUi = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--properties" && i + 1 < args.Length)
                propertiesPath = args[++i];
            else if (args[i] == "--language" && i + 1 < args.Length)
                language = args[++i];
            else if (args[i] == "--web-ui")
                webUi = true;
            else if (!args[i].StartsWith('-') && levelPath is null)
                levelPath = args[i];
            else
            {
                Console.Error.WriteLine("SharpFAI.Editor [level.adofai | levels.zip] [--properties LevelEditorProperties.txt] [--language en|zh-Hans] [--web-ui]");
                return 2;
            }
        }

        try
        {
            // Large charts can keep millions of live objects. Favor background collections
            // during interactive playback, as the standalone player does.
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
            }, catalog, document, language);
            if (archiveChoices is not null)
                window.ChooseArchiveLevel(archiveChoices);
            using EditorWebMirror? mirror = webUi ? EditorWebMirror.Start() : null;
            window.WebMirror = mirror;
            window.Run();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
