using System.Text.Json;

namespace SharpFAI.ModApi;

public sealed record ChartEditChange(int Operation, string Kind, int? Floor, int? Index,
    JsonElement? Before, JsonElement? After, string? Key = null);
public sealed record ChartEditPreview(ChartRevision Revision, bool Valid, int? RejectedOperation,
    string? Error, IReadOnlyList<ChartEditChange> Changes);
public sealed record ChartEditCommit(ChartRevision Revision, EditResult Result);
public sealed record ChartFileResult(string Path, ChartRevision Revision, bool Modified);
public sealed record ChartTempo(int Floor, double AuthoredBpm, double? EffectiveBpm, double Pitch);
public sealed record ChartSpeedConversion(ChartTempo Tempo, int EventIndex, string Field, double Value, double TargetBpm);
public sealed record ChartTimeRange(double Start, double End);
public sealed record ChartSelection(ChartRevision Revision, ChartTimeRange? Range, bool Looping);
public enum ChartSelectionAction { Read, Set, Clear, Loop, StopLoop }
public sealed record ChartPerformanceFrame(double Seconds, int Floor, float FrameMs,
    float TextureUploadMs, float DecodeWaitMs, float ParticlesMs, float FiltersMs);
public sealed record ChartPerformanceReport(ChartRevision Revision, ChartTimeRange Range, int Frames,
    double MeanMs, float P95Ms, float P99Ms, float MaxMs, IReadOnlyList<ChartPerformanceFrame> Slowest);
public sealed record ChartFrameDifference(ChartFrameState Current, ChartFrameState? Reference, int Width, int Height,
    int ChangedPixels, int MaxChannelDifference, double MeanChannelDifference,
    bool? CameraChanged, bool? FiltersChanged, byte[] DifferencePng);
public sealed record ChartVideoOptions(string OutputPath, int Width, int Height, int Fps, double PrerollSeconds,
    bool PreferHardware, bool Overwrite);
public sealed record ChartVideoJob(Guid Id, ChartRevision Revision, string OutputPath, string State, int Frame,
    int FrameCount, double Progress, string? Error);

/// <summary>Runs editor workflows against the explicitly requested revision.</summary>
public interface IEditorWorkflow
{
    ValueTask<ChartEditPreview> PreviewEditAsync(ChartRevision revision, ChartEditRequest edit, CancellationToken token);
    ValueTask<ChartEditCommit> CommitEditAsync(ChartRevision revision, ChartEditRequest edit, CancellationToken token);
    ValueTask<ChartFileResult> OpenAsync(ChartRevision revision, string path, bool discardUnsaved, CancellationToken token);
    ValueTask<ChartFileResult> SaveAsync(ChartRevision revision, string path, bool overwrite, CancellationToken token);
    ValueTask<ChartTempo> ReadTempoAsync(ChartRevision revision, int floor, CancellationToken token);
    ValueTask<ChartSpeedConversion> ConvertTempoAsync(ChartRevision revision, int floor, int eventIndex,
        double targetBpm, CancellationToken token);
    ValueTask<ChartSelection> SelectAsync(ChartRevision revision, ChartSelectionAction action, ChartTimeRange? range,
        CancellationToken token);
    ValueTask<ChartPerformanceReport> ProfileAsync(ChartRevision revision, ChartTimeRange range, int slowest, CancellationToken token);
    ValueTask<ChartFrameDifference> CompareAsync(FrameCaptureRequest frame, ChartEditRequest? proposedEdit,
        string? referencePng, CancellationToken token);
    ValueTask<ChartVideoJob> ExportAsync(ChartRevision revision, ChartVideoOptions options, CancellationToken token);
    ValueTask<ChartVideoJob> VideoStatusAsync(Guid job, bool cancel, CancellationToken token);
}
