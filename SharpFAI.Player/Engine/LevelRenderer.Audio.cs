using SharpFAI.Player.Engine.Audio;
using Thrust.Audio;
using SharpFAI.Framework;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{

// 音频：解码结果接管、混音来源、音量与对时。
// Audio: decoded sources, export mixing inputs, volume and sync.

    internal readonly record struct EditorAudioReuse(string? MusicPath, DecodedAudio? Music, DecodedAudio? Hitsound, double Rate = 1.0);

    /// <summary>
    /// Background side: probe the music stream and synthesize the hitsound (CPU only, no OpenAL calls).
    /// The caller may set <paramref name="reuseHitsound"/> when the timing and hitsound settings are unchanged.
    /// </summary>
    internal static (string? MusicPath, DecodedAudio? Music, DecodedAudio? Hitsound) BuildAudio(
        Level level,
        string levelPath,
        List<Floor> floors,
        PlaybackTimeline timeline,
        LevelPlaybackSettings settings,
        Action<string, double>? markPhase = null,
        EditorAudioReuse? reuse = null,
        bool reuseHitsound = false, Func<string, string>? prepareMusic = null, CancellationToken cancellation = default)
    {
        if (settings.SkipAudio)
            return default;

        string? musicPath = LevelAudioLocator.Find(level, levelPath);
        if (musicPath is not null && prepareMusic is not null)
            musicPath = prepareMusic(musicPath);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DecodedAudio? music = reuse is { } prior && string.Equals(musicPath, prior.MusicPath, StringComparison.Ordinal)
            ? prior.Music : musicPath is null ? null : AudioDecoder.OpenStream(musicPath, cancellation);
        markPhase?.Invoke("读取音乐信息", clock.Elapsed.TotalMilliseconds);

        clock.Restart();
        DecodedAudio? hitsound = null;
        if (!string.Equals(settings.Hitsound, "None", StringComparison.OrdinalIgnoreCase))
        {
            hitsound = reuseHitsound && reuse is { } previous && previous.Rate == timeline.Pitch &&
                ReferenceEquals(music, previous.Music)
                ? previous.Hitsound : SynthesizeHitsound(floors, timeline);
        }
        markPhase?.Invoke("合成打击音", clock.Elapsed.TotalMilliseconds);
        return (musicPath, music, hitsound);
    }

    private bool _audioBackendReady;
    private bool _bufferingNativeAudio;
    private AudioClip? _bufferingMusic;
    private AudioClip? _bufferingHitsound;
    private string? _musicLoadWarning;
    private string? _hitsoundPresetWarning;

    /// <summary>
    /// Main thread: hand the decoded audio to the OpenAL sources.
    /// </summary>
    private void ApplyAudio(LevelLoadResult result)
    {
        StopModAudio();
        SetAudioData(result.MusicPath, result.Music, result.Hitsound);
        UpdateHitsoundPresetWarning();

        _audioBackendReady = EnableAudioOutput && !_settings.SkipAudio && PlaybackAudio.Initialize();
        if (_audioBackendReady)
        {
            _music = new AudioClip { Name = "Music", Volume = PlaybackMusicGain };
            _hitsound = new AudioClip { Name = "Hitsound", Volume = _hitsoundVolume * _hitsoundOutputGain };
            if (result.Music is { } music)
                _music.LoadDecoded(music, _pitch);
            if (result.Hitsound is { } hitsound)
                _hitsound.LoadDecoded(hitsound);
        }

        UpdateAudioStatus(_audioBackendReady);
    }

    /// <summary>
    /// Main thread: apply audio after installing the new settings and timeline (including pitch).
    /// Changed clips are stopped and rewound; the caller restores the chart playhead and playback
    /// intent through seek/restart as needed. Unchanged clips retain their stream and position unless
    /// the caller explicitly realigns them. Gain-only edits never reset the stream.
    /// </summary>
    private void ApplyEditorAudio(string? musicPath, DecodedAudio? music, DecodedAudio? hitsound, double previousPitch)
    {
        bool pitchChanged = previousPitch != _pitch;
        bool musicChanged = !ReferenceEquals(_audioMusic, music) || (pitchChanged && music is not null);
        bool hitsoundChanged = !ReferenceEquals(_audioHitsound, hitsound);
        if (_modAudio is not null && (musicChanged || hitsoundChanged || pitchChanged))
            _audioNeedsResync = true;
        SetAudioData(musicPath, music, hitsound);
        UpdateHitsoundPresetWarning();

        bool backendReady = _audioBackendReady && AudioEngine.IsInitialized;
        if (backendReady && _music is { } musicClip && _hitsound is { } hitsoundClip)
        {
            if (musicChanged)
                _music = musicClip = UpdateEditorClip(musicClip, music, _pitch);
            if (hitsoundChanged)
                _hitsound = hitsoundClip = UpdateEditorClip(hitsoundClip, hitsound);

            float musicGain = PlaybackMusicGain;
            if (musicClip.Volume != musicGain)
                musicClip.Volume = musicGain;
            float hitsoundGain = _hitsoundVolume * _hitsoundOutputGain;
            if (hitsoundClip.Volume != hitsoundGain)
                hitsoundClip.Volume = hitsoundGain;
        }

        UpdateAudioStatus(backendReady);
    }

    private static AudioClip UpdateEditorClip(AudioClip clip, DecodedAudio? audio, double rate = 1.0)
    {
        if (audio is not null && clip.LoadDecoded(audio, rate))
            return clip;
        // Clear the previous track even when its replacement could not be loaded.
        clip.Dispose();
        return new AudioClip { Name = clip.Name };
    }

    private void SetAudioData(string? musicPath, DecodedAudio? music, DecodedAudio? hitsound)
    {
        // Retain the sources for export, even without OpenAL.
        if (!ReferenceEquals(_audioMusic, music))
        {
            _trackAudioWindow?.Dispose();
            _trackAudioWindow = null;
            _trackAudioFailed = false;
        }
        _audioMusicPath = musicPath;
        _audioMusic = music;
        _audioHitsound = hitsound;
        _musicVolume = _settings.MusicVolume;
        _hitsoundVolume = _settings.HitsoundVolume;
        UpdateAudioDuration();
    }

    private void UpdateHitsoundPresetWarning()
    {
        string? warning = !_settings.SkipAudio && !string.Equals(_settings.Hitsound, "Kick", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_settings.Hitsound, "None", StringComparison.OrdinalIgnoreCase)
            ? $"打击音预设 {_settings.Hitsound} 暂无素材，使用 Kick" : null;
        SetAudioWarning(ref _hitsoundPresetWarning, warning);
    }

    private void UpdateAudioStatus(bool backendReady)
    {
        if (!backendReady)
        {
            _audioAvailable = false;
            _audioStatus = _settings.SkipAudio ? "已跳过音频加载" : "OpenAL 不可用";
            SetAudioWarning(ref _musicLoadWarning, null);
            return;
        }

        bool musicLoaded = _music?.IsLoaded == true;
        bool hitsoundLoaded = _hitsound?.IsLoaded == true;
        string? warning = _audioMusicPath switch
        {
            null => "未找到音乐文件 / No music file found",
            _ when musicLoaded => null,
            // Offline export decodes additional formats through ffmpeg before mixing.
            string path when LevelAudioLocator.IsDecodable(path) => $"音乐加载失败 / Music failed to load: {path}",
            string path => $"实时播放不支持此音乐格式（导出仍可混入）/ Music format requires ffmpeg for export: {path}",
        };
        SetAudioWarning(ref _musicLoadWarning, warning);
        _audioAvailable = musicLoaded || hitsoundLoaded;
        _audioStatus = (musicLoaded, hitsoundLoaded) switch
        {
            (true, true) => $"音乐 + 打击音（{_music!.Duration:0.0}s）",
            (true, false) => "仅音乐",
            (false, true) => "仅打击音",
            _ => "未找到音频文件",
        };
    }

    private void SetAudioWarning(ref string? previous, string? warning)
    {
        if (previous is not null && previous != warning)
            _warnings.Remove(previous);
        if (warning is not null && !_warnings.Contains(warning))
            _warnings.Add(warning);
        previous = warning;
    }

    private static DecodedAudio? SynthesizeHitsound(List<Floor> floors, PlaybackTimeline timeline)
    {
        if (floors.Count == 0)
            return null;

        try
        {
            return HitsoundSynth.SynthesizeTimeline(timeline.EntryTimes, timeline.Pitch, floors: floors);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audio] 打击音合成失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>Hot-swap the hitsound.</summary>
    public bool ReloadHitsound(string wavPath)
    {
        if (_settings.SkipAudio || _hitsound is not { } clip || !File.Exists(wavPath))
        {
            return false;
        }

        DecodedAudio? decoded = AudioDecoder.OpenStream(wavPath);
        if (decoded is null || !clip.LoadDecoded(decoded))
        {
            return false;
        }

        clip.Volume = _hitsoundVolume * _hitsoundOutputGain;
        _audioHitsound = decoded;

        UpdateAudioDuration();
        _audioNeedsResync = true;
        return true;
    }

    /// <summary>
    /// Fill the audio queues and tell the chart clock when it can advance.
    /// </summary>
    private bool SyncAudio()
    {
        if (WaitingForSeekFrame) return false;
        bool? mixedAudioReady = SyncModAudio();
        if (mixedAudioReady.HasValue || !_audioAvailable || _progressScrubbing)
        {
            ClearNativeAudioBuffering();
            return mixedAudioReady ?? true;
        }

        if (_music is { } music)
        {
            float gain = PlaybackMusicGain;
            if (music.Volume != gain) music.Volume = gain;
        }
        double musicTime = (_playTime + AudioClockShift) / _pitch;
        double hitTime = (_playTime + HitsoundClockShift) / _pitch;
        AudioClip? musicClip = ActiveClip(_music, _musicEnabled, musicTime);
        AudioClip? hitsoundClip = ActiveClip(_hitsound, _hitsoundEnabled, hitTime);
        if (musicClip is null && hitsoundClip is null)
        {
            ClearNativeAudioBuffering();
            _audioNeedsResync = false;
            return true;
        }

        if (!_audioNeedsResync && !_bufferingNativeAudio)
        {
            musicClip?.UpdateBuffers();
            hitsoundClip?.UpdateBuffers();
            // A naturally finished track does not hold the other track at its last sample.
            if (musicClip?.IsAtEnd == true) musicClip = null;
            if (hitsoundClip?.IsAtEnd == true) hitsoundClip = null;
            if (musicClip?.IsOutputPlaying != false && hitsoundClip?.IsOutputPlaying != false)
                return true;
        }

        if (_audioNeedsResync || !_bufferingNativeAudio ||
            (musicClip is not null && !ReferenceEquals(musicClip, _bufferingMusic)) ||
            (hitsoundClip is not null && !ReferenceEquals(hitsoundClip, _bufferingHitsound)))
        {
            // Both tracks share this chart time. Starting either one before the other has decoded
            // leaves a permanent offset even once both OpenAL sources are producing samples.
            musicClip?.PreparePlayback(musicTime);
            hitsoundClip?.PreparePlayback(hitTime);
            _bufferingNativeAudio = true;
            _bufferingMusic = musicClip;
            _bufferingHitsound = hitsoundClip;
            _audioNeedsResync = false;
        }

        bool musicReady = musicClip?.BufferForStart() != false;
        bool hitsoundReady = hitsoundClip?.BufferForStart() != false;
        if ((!musicReady && musicClip!.IsLoaded) || (!hitsoundReady && hitsoundClip!.IsLoaded))
            return false;

        AudioClip.PlayPrepared(musicReady ? musicClip : null, hitsoundReady ? hitsoundClip : null);
        ClearNativeAudioBuffering();
        // Playback was started just now. The incoming tick's delta belongs to the buffering interval.
        return false;
    }

    private AudioClip? ActiveClip(AudioClip? clip, bool enabled, double time)
    {
        if (clip is { IsLoaded: true } && _isPlaying && enabled && time >= 0 && time < clip.Duration &&
            (_audioNeedsResync || !clip.IsAtEnd))
            return clip;
        clip?.Pause();
        return null;
    }

    private void ClearNativeAudioBuffering()
    {
        _bufferingNativeAudio = false;
        _bufferingMusic = _bufferingHitsound = null;
    }

    /// <summary>
    /// 离线导出用的音频来源：音乐路径与合成好的打击音 PCM。
    /// Audio sources for offline export: the music file path and the synthesised hitsound PCM.
    /// </summary>
    /// <remarks>
    /// 视频导出不走 OpenAL，而是把两条音轨混成一个 WAV 再交给 ffmpeg，这样音画都在
    /// 「文件位置时钟」上，天然同步。
    /// Video export bypasses OpenAL and hands a mixed WAV to ffmpeg, keeping picture and sound on the
    /// same file-position clock so they stay in sync.
    /// </remarks>
    public (string? MusicPath, DecodedAudio? Music, DecodedAudio? Hitsound) AudioSources
        => (_audioMusicPath, _audioMusic, _audioHitsound);

    /// <summary>
    /// 音乐音量，导出混音时使用。
    /// Music volume, used when mixing the export audio.
    /// </summary>
    public float MusicVolumeExport => _musicVolume;

    /// <summary>导出时将谱面打击音增益翻倍 / Double the chart hitsound gain for the export mix.</summary>
    public float HitsoundVolumeExport => _hitsoundVolume * 2f;

    private float _playbackOutputGain = 1f;
    private float _musicOutputGain = 1f;
    private float _hitsoundOutputGain = 1f;
    private float PlaybackMusicGain => _musicVolume * _musicOutputGain *
        (ManualAttemptActive ? PlaySession.PauseStartMusicGain : 1);

    // Local monitoring gains are independent of the chart and the export mix.
    public void SetPlaybackOutputVolumes(int master, int music, int hitsound)
    {
        _playbackOutputGain = Math.Clamp(master, 0, 100) / 100f;
        _musicOutputGain = _playbackOutputGain * (Math.Clamp(music, 0, 100) / 100f);
        _hitsoundOutputGain = _playbackOutputGain * (Math.Clamp(hitsound, 0, 100) / 100f);
        _music?.Volume = PlaybackMusicGain;
        _hitsound?.Volume = _hitsoundVolume * _hitsoundOutputGain;
    }

    /// <summary>Music volume.</summary>
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = Math.Clamp(value, 0f, 1f);
            _music?.Volume = PlaybackMusicGain;
        }
    }

    /// <summary>Hitsound volume.</summary>
    public float HitsoundVolume
    {
        get => _hitsoundVolume;
        set
        {
            _hitsoundVolume = Math.Clamp(value, 0f, 1f);
            _hitsound?.Volume = _hitsoundVolume * _hitsoundOutputGain;
        }
    }

    /// <summary>Manual hitsound offset against the music, in milliseconds.</summary>
    public float HitsoundShiftMs => _hitsoundShiftMs;

    private const int MaxAudioDelayMs = 1000;
    private int _audioDelayMs;

    private double AudioClockShift => -_audioDelayMs / 1000.0 * _pitch;
    private double HitsoundClockShift => AudioClockShift + _hitsoundShiftMs / 1000.0;

    internal int AudioDelayMs
    {
        get => _audioDelayMs;
        set
        {
            int delay = Math.Clamp(value, -MaxAudioDelayMs, MaxAudioDelayMs);
            if (delay == _audioDelayMs)
                return;

            _audioDelayMs = delay;
            _audioNeedsResync = true;
            _floorVisualTime = double.NaN;
            UpdateAudioDuration();
        }
    }

    internal double ExportDuration => AudioTimelineDuration(0.0, 0.0);

    private void UpdateAudioDuration() => _duration = AudioTimelineDuration(AudioClockShift, HitsoundClockShift);

    private double AudioTimelineDuration(double musicShift, double hitsoundShift)
    {
        double musicEnd = _audioMusic is { } music ? music.Duration - musicShift : 0.0;
        double hitsoundEnd = _audioHitsound is { } hitsound ? hitsound.Duration * _pitch - hitsoundShift : 0.0;
        return Math.Max(_levelDuration, Math.Max(musicEnd, hitsoundEnd));
    }

    /// <summary>
    /// Shift the hitsound against the music by moving the clip itself, since the times are baked in.
    /// </summary>
    public void SetHitsoundShift(float milliseconds)
    {
        _hitsoundShiftMs = Math.Clamp(milliseconds, -300f, 300f);
        _audioNeedsResync = true;
        UpdateAudioDuration();
    }
}
