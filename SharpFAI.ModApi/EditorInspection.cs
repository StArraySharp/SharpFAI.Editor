using System.Text.Json;

namespace SharpFAI.ModApi;

public sealed record ChartContext(ChartRevision Revision, string? FileName, bool Modified, int LastFloor,
    int? SelectedFloor, int? SelectedDecoration, int DecorationCount, PlaybackState Playback,
    bool TimingReady, IReadOnlyDictionary<string, JsonElement> Settings);

public sealed record ChartPosition(int Floor, double Seconds, double QuarterNote, double Measure, double BeatInMeasure,
    double Bpm, int Numerator, int Denominator);
public sealed record ChartFloor(int Floor, double? IncomingAngle, ChartPosition? Position);
public sealed record ChartEventCursor(int Floor, int Index);
public sealed record ChartEventItem(int Floor, int Index, JsonElement Data);
public sealed record ChartEventPage(ChartRevision Revision, IReadOnlyList<ChartEventItem> Items, ChartEventCursor? Next);
public sealed record ChartDecorationItem(int Index, JsonElement Data);
public sealed record ChartDecorationPage(ChartRevision Revision, IReadOnlyList<ChartDecorationItem> Items, int? NextIndex);
public sealed record ChartDiagnostics(ChartRevision Revision, bool RendererCurrent, string? LoadError,
    IReadOnlyList<string> RendererWarnings);
public sealed record ChartFieldSchema(string Key, string Type, JsonElement? Default, bool Optional, string? Unit,
    double? Minimum, double? Maximum, IReadOnlyList<string>? Options);
public sealed record ChartEventSchema(string Type, bool Decoration, bool AllowFirstFloor, IReadOnlyList<ChartFieldSchema> Fields);

/// <summary>Reads the live editor document, including unsaved changes. Returned data is detached from the editor.</summary>
public interface IEditorInspection
{
    ValueTask<ChartContext> GetChartAsync(CancellationToken cancellationToken);
    ValueTask<ChartPosition> LocateAsync(ChartRevision expected, int? floor, double? seconds, double? quarterNote,
        CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ChartFloor>> ReadFloorsAsync(ChartRevision expected, int firstFloor, int count,
        CancellationToken cancellationToken);
    ValueTask<ChartEventPage> ReadEventsAsync(ChartRevision expected, ChartEventCursor start, int lastFloor,
        string? type, string? tag, int limit, CancellationToken cancellationToken);
    ValueTask<ChartDecorationPage> ReadDecorationsAsync(ChartRevision expected, int startIndex, string? type,
        string? tag, int limit, CancellationToken cancellationToken);
    ValueTask<ChartDiagnostics> GetDiagnosticsAsync(ChartRevision expected, CancellationToken cancellationToken);
    ValueTask<ChartEventSchema?> GetEventSchemaAsync(string type, CancellationToken cancellationToken);
}

public sealed record FrameCaptureRequest(ChartRevision Revision, int? Floor = null, double? Seconds = null,
    int Width = 1280, int Height = 720, double PrerollSeconds = 0, int PrerollFps = 60, bool IncludeImage = true);
public sealed record ChartCamera(float X, float Y, float Rotation, float Zoom, bool Visible);
public sealed record ChartFilter(string Layer, string Name, IReadOnlyDictionary<string, float[]> Parameters);
public sealed record ChartFrameState(int Floor, double Seconds, ChartCamera Camera, IReadOnlyList<ChartFilter> Filters,
    int DrawCalls, long DrawnTriangles);
public sealed record ChartFrameCapture(ChartRevision Revision, int Width, int Height, ChartFrameState State, byte[]? Png);

/// <summary>Renders a chart revision offscreen without changing the editor's camera, playhead or audio.</summary>
public interface IEditorFrameCapture
{
    ValueTask<ChartFrameCapture> CaptureAsync(FrameCaptureRequest request, CancellationToken cancellationToken);
}
