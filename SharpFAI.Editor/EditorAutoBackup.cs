using System.Diagnostics;

namespace SharpFAI.Editor;

internal sealed class EditorAutoBackup(string fallbackDirectory, TimeProvider clock)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromMilliseconds(2);
    private sealed record Target(EditorDocument Document, string? OriginalPath, string FileName);
    private sealed record Result(Target Target, int Version, string Destination);
    private sealed record Capture(int Version, IEnumerator<(string Text, bool Last)> Chunks) : IDisposable
    {
        public List<string> Data { get; } = [];
        public void Dispose() => Chunks.Dispose();
    }

    private Target? _target;
    private long _lastCheck;
    private int _backedUpVersion = -1;
    private string? _destination;
    private Task<Result?>? _write;
    private Capture? _capture;

    public void Update(EditorDocument document)
    {
        CompleteWrite(wait: false);
        long now = clock.GetTimestamp();
        Target? target = _target;
        if (target is null || target.Document != document || target.OriginalPath != document.Path)
        {
            DiscardCapture();
            string name = Path.GetFileNameWithoutExtension(document.Path) ?? "untitled";
            if (name.Length > 100)
                name = name[..(char.IsHighSurrogate(name[99]) ? 99 : 100)];
            _target = target = new Target(document, document.Path, $"{name}.sharpfai-backup-{Guid.NewGuid():N}.adofai");
            _lastCheck = now;
            _backedUpVersion = -1;
            _destination = null;
        }

        if (_write is not null)
            return;
        if (_capture is { } capture && capture.Version != document.ChangeVersion)
            DiscardCapture();
        if (_capture is null)
        {
            if (clock.GetElapsedTime(_lastCheck, now) < Interval) return;
            if (document.ChangeVersion == _backedUpVersion || !document.HasChangesToBackUp())
            {
                _lastCheck = now;
                return;
            }
            _capture = new(document.ChangeVersion, document.BackupChunks().GetEnumerator());
        }
        CaptureSnapshot(target, CaptureBudget);
    }

    internal void CompleteWrite(bool wait)
    {
        if (wait && _capture is { } capture && _target is { } target)
        {
            if (target.Document.ChangeVersion == capture.Version && target.Document.Path == target.OriginalPath)
                CaptureSnapshot(target, TimeSpan.MaxValue);
            else DiscardCapture();
        }
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

    private void CaptureSnapshot(Target target, TimeSpan budget)
    {
        var capture = _capture!;
        long started = Stopwatch.GetTimestamp();
        while (capture.Chunks.MoveNext())
        {
            var (text, last) = capture.Chunks.Current;
            capture.Data.Add(text);
            if (last) break;
            if (Stopwatch.GetElapsedTime(started) >= budget) return;
        }

        DiscardCapture();
        _lastCheck = clock.GetTimestamp();
        string? previous = _destination;
        _write = Task.Run(() => Write(target, capture.Version, capture.Data, previous));
    }

    private void DiscardCapture()
    {
        _capture?.Dispose();
        _capture = null;
    }

    private Result? Write(Target target, int version, IReadOnlyList<string> snapshot, string? previous)
    {
        string fallback = Path.Combine(fallbackDirectory, target.FileName);
        string? destination = previous ?? (target.OriginalPath is { } original
            ? Path.Combine(Path.GetDirectoryName(original)!, target.FileName) : null);
        if (destination is not null && destination != fallback)
        {
            try
            {
                Save(destination);
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
            Save(fallback);
            return new Result(target, version, fallback);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Editor] Could not write an automatic backup: {error.Message}");
            return null;
        }

        void Save(string path) => EditorFileStorage.WriteAtomically(path, writer =>
        {
            foreach (string chunk in snapshot) writer.Write(chunk);
        }, overwrite: path == previous);
    }
}
