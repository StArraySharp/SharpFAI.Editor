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

            string? gameDirectory = args switch
            {
                [] => SelectDirectory(),
                ["--game-dir", string directory] => directory,
                _ => throw new ArgumentException("Use --game-dir <path>, --download-tools, or run without arguments to open the directory picker."),
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
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
