using Newtonsoft.Json.Linq;

namespace SharpFAI.Editor;

internal sealed class EditorAutoBackup(string fallbackDirectory, TimeProvider clock)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private sealed record Target(EditorDocument Document, string? OriginalPath, string FileName);
    private sealed record Result(Target Target, int Version, string Destination);

    private Target? _target;
    private long _lastCheck;
    private int _backedUpVersion = -1;
    private string? _destination;
    private Task<Result?>? _write;

    public void Update(EditorDocument document)
    {
        CompleteWrite(wait: false);
        long now = clock.GetTimestamp();
        Target? target = _target;
        if (target is null || target.Document != document || target.OriginalPath != document.Path)
        {
            string name = Path.GetFileNameWithoutExtension(document.Path) ?? "untitled";
            if (name.Length > 100)
                name = name[..(char.IsHighSurrogate(name[99]) ? 99 : 100)];
            _target = target = new Target(document, document.Path, $"{name}.sharpfai-backup-{Guid.NewGuid():N}.adofai");
            _lastCheck = now;
            _backedUpVersion = -1;
            _destination = null;
        }

        if (_write is not null || clock.GetElapsedTime(_lastCheck, now) < Interval)
            return;
        _lastCheck = now;
        if (!document.Modified || document.ChangeVersion == _backedUpVersion)
            return;

        string? previous = _destination;
        int version = document.ChangeVersion;
        JObject snapshot = document.CaptureBackup();
        _write = Task.Run(() => Write(target, version, snapshot, previous));
    }

    internal void CompleteWrite(bool wait)
    {
        if (_write is null || !wait && !_write.IsCompleted)
            return;
        var result = _write.GetAwaiter().GetResult();
        _write = null;
        if (result is not null && ReferenceEquals(result.Target, _target))
        {
            _backedUpVersion = result.Version;
            _destination = result.Destination;
        }
    }

    private Result? Write(Target target, int version, JObject snapshot, string? previous)
    {
        string fallback = Path.Combine(fallbackDirectory, target.FileName);
        string? destination = previous ?? (target.OriginalPath is { } original
            ? Path.Combine(Path.GetDirectoryName(original)!, target.FileName) : null);
        if (destination is not null && destination != fallback)
        {
            try
            {
                EditorFileStorage.WriteChart(destination, snapshot, overwrite: destination == previous);
                return new Result(target, version, destination);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[Editor] Could not write the backup beside the chart: {error.Message}");
            }
        }

        try
        {
            Directory.CreateDirectory(fallbackDirectory);
            EditorFileStorage.WriteChart(fallback, snapshot, overwrite: fallback == previous);
            return new Result(target, version, fallback);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Editor] Could not write an automatic backup: {error.Message}");
            return null;
        }
    }
}
