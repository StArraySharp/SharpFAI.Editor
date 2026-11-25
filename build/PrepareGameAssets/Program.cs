namespace SharpFAI.Build;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine("PrepareGameAssets [--game-dir <ADOFAI installation or _Data directory>]");
            Console.WriteLine("PrepareGameAssets --download-tools");
            Console.WriteLine("PrepareGameAssets --export-zip [path]");
            Console.WriteLine("PrepareGameAssets --import-zip [path]");
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        try
        {
            if (args is ["--download-tools"])
            {
                ToolCache.PrepareAsync(FindWorkspace(), checkLatest: true, cancellation.Token).GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--export-zip" or "--import-zip", ..] && args.Length <= 2)
            {
                bool export = args[0] == "--export-zip";
                string? archive = args.Length == 2 ? args[1] : SelectArchive(export);
                if (archive is null) return 0;
                string workspace = FindWorkspace();
                if (export)
                    PreparedAssetArchive.Export(workspace, archive, cancellation.Token);
                else
                    PreparedAssetArchive.Import(workspace, archive, cancellation.Token);
                return 0;
            }

            string? gameDirectory = args switch
            {
                [] => SelectDirectory(),
                ["--game-dir", string directory] => directory,
                _ => throw new ArgumentException("Use --help to see the available resource preparation commands."),
            };
            if (gameDirectory is null)
                return 0;

            GameAssetPreparation.RunAsync(FindWorkspace(), gameDirectory, cancellation.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Resource preparation cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                                   System.ComponentModel.Win32Exception or HttpRequestException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? SelectDirectory()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the A Dance of Fire and Ice installation directory",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        return picker.ShowDialog() == DialogResult.OK ? picker.SelectedPath : null;
    }

    private static string? SelectArchive(bool export)
    {
        using FileDialog picker = export ? new SaveFileDialog { OverwritePrompt = false } : new OpenFileDialog();
        picker.Title = export ? "Export prepared game resources" : "Import prepared game resources";
        picker.Filter = "ZIP archives (*.zip)|*.zip";
        picker.DefaultExt = "zip";
        return picker.ShowDialog() == DialogResult.OK ? picker.FileName : null;
    }

    private static string FindWorkspace()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpFAI.Editor.slnx")))
                    return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Run PrepareGameAssets from the SharpFAI.Editor workspace.");
    }
}
