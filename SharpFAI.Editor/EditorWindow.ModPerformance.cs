using System.Diagnostics;
using SharpFAI.ModApi;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private sealed class ModPerformanceJob(ChartRevision revision, ChartTimeRange range, int slowest, CancellationToken token)
    {
        internal ChartRevision Revision { get; } = revision;
        internal ChartTimeRange Range { get; } = range;
        internal CancellationToken Token { get; } = token;
        internal long StartedAt { get; } = Stopwatch.GetTimestamp();
        internal bool Sampling { get; set; }
        internal PlaybackPerformance Metrics { get; } = new();
        internal List<ChartPerformanceFrame> Frames { get; } = [];
        internal TaskCompletionSource<ChartPerformanceReport> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _seenFrames;

        internal void Collect()
        {
            if (_seenFrames == Metrics.RecordedFrames) return;
            _seenFrames = Metrics.RecordedFrames;
            var sample = Metrics[Metrics.Count - 1];
            if (sample.Time < Range.Start || sample.Time > Range.End) return;
            var stages = sample.Stages;
            Frames.Add(new(sample.Time, sample.Floor, sample.Interval, stages.TextureUpload, stages.DecodeWait,
                stages.Particles, stages.Filters));
        }

        internal ChartPerformanceReport Report()
        {
            if (Frames.Count == 0) throw new InvalidOperationException("No gameplay frames were presented in the interval.");
            var sorted = Frames.OrderBy(frame => frame.FrameMs).ToArray();
            return new(Revision, Range, sorted.Length, sorted.Average(frame => (double)frame.FrameMs),
                sorted[(int)Math.Ceiling(sorted.Length * .95) - 1].FrameMs,
                sorted[(int)Math.Ceiling(sorted.Length * .99) - 1].FrameMs, sorted[^1].FrameMs,
                sorted.Reverse().Take(slowest).ToArray());
        }
    }

    private ModPerformanceJob? _modPerformanceJob;

    private sealed partial class ModEditorInspection
    {
        public async ValueTask<ChartPerformanceReport> ProfileAsync(ChartRevision revision, ChartTimeRange range, int slowest,
            CancellationToken token)
        {
            var job = await ui.Dispatch(() =>
            {
                CheckRevision(revision);
                RequireTiming();
                ValidateRange(range, editor._playback!.Duration);
                if (slowest is < 1 or > 100 || range.End - range.Start > 60)
                    throw new ArgumentException("Select at most 60 seconds and request 1 through 100 slow frames.");
                if (editor._modPerformanceJob is not null || editor._videoExport?.Request is not null ||
                    editor._modFrameJob is not null || editor._playback.IsPlaying || editor._playbackView == PlaybackView.Manual)
                    throw new InvalidOperationException("Pause playback and wait for the current capture or export before profiling.");
                editor.SwitchPlaybackView(PlaybackView.Auto);
                if (!editor._startPlayback)
                    throw new InvalidOperationException("The editor could not start automatic playback.");
                editor._playbackFloor = null;
                return editor._modPerformanceJob = new(revision, range, slowest, token);
            }, token) ?? throw new InvalidOperationException("The editor is closing.");
            return await job.Completion.Task.WaitAsync(token);
        }
    }

    private void BeginModPerformanceSample()
    {
        if (_modPerformanceJob is not { Sampling: false } job || !HasCurrentPlayback ||
            _playback is not { IsPlaying: true } renderer || _document.ModRevision != job.Revision)
            return;
        renderer.Seek(job.Range.Start);
        if (!renderer.IsPlaying) renderer.Play();
        job.Sampling = true;
    }

    private void AdvanceModPerformance()
    {
        if (_modPerformanceJob is not { } job) return;
        if (job.Token.IsCancellationRequested)
        {
            EndModPerformance();
            return;
        }
        if (_document.ModRevision != job.Revision || _playbackView is not (PlaybackView.Auto or PlaybackView.Preview) ||
            Stopwatch.GetElapsedTime(job.StartedAt).TotalSeconds > 180 || job.Frames.Count >= 120_000)
        {
            EndModPerformance(new InvalidOperationException("Profiling did not finish within the playback or sampling limits."));
            return;
        }
        if (!job.Sampling) return;
        job.Collect();
        if (_playback!.PlayTime >= job.Range.End)
        {
            try { job.Completion.TrySetResult(job.Report()); }
            catch (InvalidOperationException error) { job.Completion.TrySetException(error); }
            EndModPerformance();
        }
        else if (!_playback.IsPlaying)
            EndModPerformance(new InvalidOperationException("Playback stopped before the end of the profiling interval."));
    }

    private void EndModPerformance(Exception? error = null)
    {
        if (_modPerformanceJob is not { } job) return;
        _modPerformanceJob = null;
        if (_document.ModRevision == job.Revision && _playbackView == PlaybackView.Auto)
            PauseToPreview();
        if (!job.Completion.Task.IsCompleted)
        {
            if (error is null) job.Completion.TrySetCanceled();
            else job.Completion.TrySetException(error);
        }
    }
}
