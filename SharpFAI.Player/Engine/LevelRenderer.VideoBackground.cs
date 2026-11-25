namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    private VideoBackgroundDecoder? _backgroundVideo;
    private double _videoCountdownLead;

    private void SetBackdrop(BackdropTimeline? backdrop)
    {
        var video = backdrop?.Video;
        if (_backdrop?.Video != video)
        {
            _backgroundVideo?.Dispose();
            _backgroundVideo = video is null ? null : new VideoBackgroundDecoder(video);
            _background?.ClearVideo();
        }
        _backdrop = backdrop;
        if (backdrop?.NeedsBackgroundRenderer == true) _background ??= new BackgroundRenderer();
    }

    private double BackgroundVideoTime(VideoBackgroundSource video) =>
        video.TimeAt(_playTime, _offsetSeconds, _videoCountdownLead, _settings.VideoOffset, _settings.LoopVideo);

    private bool DrawBackgroundVideo(BackgroundRenderer renderer)
    {
        if (_backdrop?.Video is not { } video || _backgroundVideo is not { } decoder) return false;
        double time = BackgroundVideoTime(video);
        using var frame = decoder.Read(Math.Max(0, time), _offlineSubSample && time >= 0, _isPlaying);
        if (decoder.Error is { } error)
        {
            _warnings.Add(error);
            decoder.Dispose();
            _backgroundVideo = null;
            renderer.ClearVideo();
            if (_offlineSubSample) throw new InvalidOperationException(error);
            return false;
        }
        if (frame is not null) renderer.UploadVideo(video, frame.Pixels);
        if (time < 0 || !decoder.HasFrame) return false;
        renderer.DrawVideo(_camera, video.Aspect);
        DrawCalls++;
        return true;
    }
}
