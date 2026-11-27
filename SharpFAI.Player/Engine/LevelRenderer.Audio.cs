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
    /// Background side: decode the music and synthesize the hitsound (CPU only, no OpenAL calls).
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
        bool reuseHitsound = false, Func<string, string>? prepareMusic = null)
    {
        if (settings.SkipAudio)
            return default;

        string? musicPath = LevelAudioLocator.Find(level, levelPath);
        if (musicPath is not null && prepareMusic is not null)
            musicPath = prepareMusic(musicPath);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DecodedAudio? music = reuse is { } prior && string.Equals(musicPath, prior.MusicPath, StringComparison.Ordinal)
            ? prior.Music : musicPath is null ? null : AudioDecoder.Decode(musicPath);
        markPhase?.Invoke("解码音乐", clock.Elapsed.TotalMilliseconds);

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
                _music.LoadDecoded(AtPlaybackRate(music));
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
                _music = musicClip = UpdateEditorClip(musicClip, music is null ? null : AtPlaybackRate(music));
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

    private static AudioClip UpdateEditorClip(AudioClip clip, DecodedAudio? audio)
    {
        if (audio is not null)
        {
            clip.LoadDecoded(audio);
        }
        else if (clip.IsLoaded)
        {
            // AudioClip has no unload operation. Replace only the clip whose PCM was removed.
            clip.Dispose();
            return new AudioClip { Name = clip.Name };
        }
        return clip;
    }

    private void SetAudioData(string? musicPath, DecodedAudio? music, DecodedAudio? hitsound)
    {
        // Retain the original PCM and path for export, even without OpenAL.
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

    private DecodedAudio AtPlaybackRate(DecodedAudio source) => _pitch == 1.0 ? source : new DecodedAudio
    {
        Pcm = source.Pcm,
        Channels = source.Channels,
        SampleRate = Math.Max(1, (int)Math.Round(source.SampleRate * _pitch)),
    };

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

        bool wasPlaying = _isPlaying;
        double position = _playTime;

        DecodedAudio? decoded = AudioDecoder.Decode(wavPath);
        if (decoded is null || decoded.Pcm.Length == 0 || !clip.LoadDecoded(decoded))
        {
            return false;
        }

        clip.Volume = _hitsoundVolume * _hitsoundOutputGain;
        _audioHitsound = decoded;

        double target = (position + HitsoundClockShift) / _pitch;
        clip.Seek(Math.Max(0.0, target));
        if (wasPlaying && target >= 0.0 && target < clip.Duration)
            clip.Play();

        UpdateAudioDuration();
        _audioNeedsResync = true;
        return true;
    }

    /// <summary>
    /// Sync audio to the playback clock. Audio state only changes on play/pause/seek;
    /// during normal playback we merely top up the queue, because seeking mid-stream cuts
    /// the buffered audio and sounds stretched.
    /// </summary>
    private void SyncAudio()
    {
        if (WaitingForSeekFrame) return;
        if (SyncModAudio()) return;
        if (!_audioAvailable || _progressScrubbing)
        {
            return;
        }

        if (_music is { } music)
        {
            float gain = PlaybackMusicGain;
            if (music.Volume != gain) music.Volume = gain;
        }
        SyncClip(_music, _musicEnabled, AudioClockShift);
        SyncClip(_hitsound, _hitsoundEnabled, HitsoundClockShift);

        // Clear the flag once both clips have been aligned
        _audioNeedsResync = false;
    }

    /// <summary>
    /// Aligns a clip to the playback clock using its offset in chart seconds.
    /// </summary>
    private void SyncClip(AudioClip? clip, bool enabled, double shift)
    {
        if (clip is not { IsLoaded: true })
        {
            return;
        }

        if (!enabled)
        {
            clip.Pause();
            return;
        }

        double target = (_playTime + shift) / _pitch;
        if (!_isPlaying || target < 0.0 || target >= clip.Duration)
        {
            if (clip.IsPlaying)
            {
                clip.Pause();
            }

            return;
        }

        if (!clip.IsPlaying)
        {
            clip.Seek(target);
            clip.Play();
            clip.Update();
            return;
        }

        if (_audioNeedsResync)
        {
            clip.Seek(target);
        }

        clip.Update();
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

    private float _musicOutputGain = 1f;
    private float _hitsoundOutputGain = 1f;
    private float PlaybackMusicGain => _musicVolume * _musicOutputGain *
        (ManualAttemptActive ? PlaySession.PauseStartMusicGain : 1);

    // Local monitoring gains are independent of the chart and the export mix.
    public void SetPlaybackOutputVolumes(int master, int music, int hitsound)
    {
        float gain = Math.Clamp(master, 0, 100) / 100f;
        _musicOutputGain = gain * (Math.Clamp(music, 0, 100) / 100f);
        _hitsoundOutputGain = gain * (Math.Clamp(hitsound, 0, 100) / 100f);
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

        if (_hitsound is { IsLoaded: true })
        {
            _hitsound.Seek(Math.Max(0.0, _playTime + HitsoundClockShift) / _pitch);
        }
    }
}
