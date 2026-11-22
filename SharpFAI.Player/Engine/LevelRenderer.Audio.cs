using SharpFAI.Player.Engine.Audio;
using Thrust.Audio;
using SharpFAI.Framework;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{

// 音频：解码结果接管、混音来源、音量与对时。
// Audio: decoded sources, export mixing inputs, volume and sync.

    /// <summary>
    /// Background side: decode the music and synthesize the hitsound (CPU only, no OpenAL calls).
    /// </summary>
    private static (string? MusicPath, DecodedAudio? Music, DecodedAudio? Hitsound) BuildAudio(
        Level level,
        string levelPath,
        List<Floor> floors,
        PlaybackTimeline timeline,
        LevelPlaybackSettings settings,
        Action<string, double>? markPhase = null)
    {
        string directory = Path.GetDirectoryName(levelPath) ?? string.Empty;

        // .wav
        // Music: prefer the song filename from the level settings, accepting .ogg and .wav
        string? musicPath = LevelAudioLocator.Find(level, levelPath);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DecodedAudio? music = musicPath is null ? null : AudioDecoder.Decode(musicPath);
        markPhase?.Invoke("解码音乐", clock.Elapsed.TotalMilliseconds);

        clock.Restart();
        DecodedAudio? hitsound = settings.SkipHitsound ||
            string.Equals(settings.Hitsound, "None", StringComparison.OrdinalIgnoreCase)
            ? null : SynthesizeHitsound(level, directory, floors, timeline, music);
        markPhase?.Invoke("合成打击音", clock.Elapsed.TotalMilliseconds);
        return (musicPath, music, hitsound);
    }

    /// <summary>
    /// Main thread: hand the decoded audio to the OpenAL sources.
    /// </summary>
    private void ApplyAudio(LevelLoadResult result)
    {
        // 导出混音需要原始 PCM，先留一份引用（OpenAL 不可用时也能导出）
        // Keep the raw PCM for export mixing; it is needed even when OpenAL is unavailable
        _audioMusicPath = result.MusicPath;
        _audioMusic = result.Music;
        _audioHitsound = result.Hitsound;
        _musicVolume = _settings.MusicVolume;
        _hitsoundVolume = _settings.HitsoundVolume;
        UpdateAudioDuration();

        if (!string.Equals(_settings.Hitsound, "Kick", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_settings.Hitsound, "None", StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add($"打击音预设 {_settings.Hitsound} 暂无素材，使用 Kick");
        }

        if (!AudioEngine.Initialize())
        {
            _audioAvailable = false;
            _audioStatus = "OpenAL 不可用";
            return;
        }

        _music = new AudioClip { Name = "Music", Volume = _musicVolume };
        _hitsound = new AudioClip { Name = "Hitsound", Volume = _hitsoundVolume };
        bool musicLoaded = result.Music is not null && _music.LoadDecoded(AtPlaybackRate(result.Music));

        if (result.MusicPath is null)
        {
            _warnings.Add("未找到音乐文件 / No music file found");
        }
        else if (!musicLoaded)
        {
            // 明确区分「格式不支持」与「解码失败」：前者导出仍可用，后者是真的坏了
            // Tell "unsupported format" apart from "failed to decode": the former still exports, the
            // latter is genuinely broken
            _warnings.Add(LevelAudioLocator.IsDecodable(result.MusicPath)
                ? $"音乐加载失败 / Music failed to load: {result.MusicPath}"
                : $"音乐格式不受支持（导出仍可混入）/ Unsupported music format, export still muxes it: {result.MusicPath}");
        }

        bool hitsoundLoaded = result.Hitsound is not null && _hitsound.LoadDecoded(AtPlaybackRate(result.Hitsound));

        _audioAvailable = musicLoaded || hitsoundLoaded;
        _audioStatus = (musicLoaded, hitsoundLoaded) switch
        {
            (true, true) => $"音乐 + 打击音（{_music.Duration:0.0}s）",
            (true, false) => "仅音乐",
            (false, true) => "仅打击音",
            _ => "未找到音频文件",
        };

    }

    private DecodedAudio AtPlaybackRate(DecodedAudio source) => _pitch == 1.0 ? source : new DecodedAudio
    {
        Pcm = source.Pcm,
        Channels = source.Channels,
        SampleRate = Math.Max(1, (int)Math.Round(source.SampleRate * _pitch)),
    };

    /// <summary>
    /// Synthesize the hitsound from the embedded kick at the level's note times (CPU only, safe on a
    /// worker thread). Results are cached per level path. When consecutive tiles land on the exact
    /// same instant, only the first is kept: stacking two samples on one frame just doubles the
    /// level and combs the transient, and it only ever sounds like a single louder hit.
    /// </summary>
    private static DecodedAudio? SynthesizeHitsound(
        Level level,
        string directory,
        List<Floor> floors,
        PlaybackTimeline timeline,
        DecodedAudio? music)
    {
        if (floors.Count == 0)
        {
            return null;
        }

        try
        {
            List<double> noteTimes = HitsoundTimes(timeline.EntryTimes);
            if (noteTimes.Count == 0)
            {
                return null;
            }

            // Match the song length so hitsounds cover the whole track
            double totalDuration = Math.Max(timeline.LevelDuration, music?.Duration ?? 0.0);
            if (totalDuration <= 0.0)
            {
                totalDuration = timeline.LevelDuration;
            }

            double tail = noteTimes.Count > 0 ? noteTimes[^1] : 0.0;
            totalDuration = Math.Max(totalDuration, tail + 0.5);

            HashCode timing = new();
            timing.Add(totalDuration);
            foreach (double time in noteTimes)
            {
                timing.Add(time);
            }

            string cacheKey = $"{level.pathToLevel ?? directory}:{timing.ToHashCode():X8}";
            return HitsoundSynth.Synthesize(cacheKey, noteTimes, totalDuration, 0.85f);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Audio] 打击音合成失败: {ex.Message}");
            return null;
        }
    }

    internal static List<double> HitsoundTimes(ReadOnlySpan<double> entryTimes)
    {
        List<double> times = new(entryTimes.Length);
        for (int i = 1; i < entryTimes.Length; i++)
        {
            double time = entryTimes[i];
            if (time >= 0.0 && (times.Count == 0 || Math.Abs(time - times[^1]) >= 1e-6))
            {
                times.Add(time);
            }
        }

        return times;
    }

    /// <summary>Hot-swap the hitsound.</summary>
    public bool ReloadHitsound(string wavPath)
    {
        if (!File.Exists(wavPath))
        {
            return false;
        }

        bool wasPlaying = _isPlaying;
        double position = _playTime;

        DecodedAudio? decoded = AudioDecoder.Decode(wavPath);
        if (decoded is null || decoded.Pcm.Length == 0 || !_hitsound.LoadDecoded(AtPlaybackRate(decoded)))
        {
            return false;
        }

        _hitsound.Volume = _hitsoundVolume;
        _audioHitsound = decoded;

        double target = (position + HitsoundClockShift) / _pitch;
        _hitsound.Seek(Math.Max(0.0, target));
        if (wasPlaying && target >= 0.0 && target < _hitsound.Duration)
            _hitsound.Play();

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
        if (!_audioAvailable)
        {
            return;
        }

        SyncClip(_music, _musicEnabled, AudioClockShift);
        SyncClip(_hitsound, _hitsoundEnabled, HitsoundClockShift);

        // Clear the flag once both clips have been aligned
        _audioNeedsResync = false;
    }

    /// <summary>
    /// Aligns a clip to the playback clock using its offset in chart seconds.
    /// </summary>
    private void SyncClip(AudioClip clip, bool enabled, double shift)
    {
        if (!clip.IsLoaded)
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

    /// <summary>打击音音量，导出混音时使用 / Hitsound volume, used when mixing the export audio.</summary>
    public float HitsoundVolumeExport => _hitsoundVolume;

    /// <summary>Music volume.</summary>
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = Math.Clamp(value, 0f, 1f);
            _music.Volume = _musicVolume;
        }
    }

    /// <summary>Hitsound volume.</summary>
    public float HitsoundVolume
    {
        get => _hitsoundVolume;
        set
        {
            _hitsoundVolume = Math.Clamp(value, 0f, 1f);
            _hitsound.Volume = _hitsoundVolume;
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
        double hitsoundEnd = _audioHitsound is { } hitsound ? hitsound.Duration - hitsoundShift : 0.0;
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

        if (_hitsound.IsLoaded)
        {
            _hitsound.Seek(Math.Max(0.0, _playTime + HitsoundClockShift) / _pitch);
        }
    }
}
