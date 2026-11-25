using SharpFAI.Serialization;
using Thrust.Audio;

namespace SharpFAI.Player.Engine;

/// <summary>
/// 定位关卡音乐文件：只认真实声明的那一首，绝不扫描目录去「猜」。
/// Locates the level music: only the song the level actually declares, never a guess based on what
/// happens to sit in the folder.
/// </summary>
/// <remarks>
/// <para>
/// 早期实现会在 <c>songFilename</c> 为空或文件缺失时扫描整个关卡目录，取第一个 <c>.wav</c>/<c>.ogg</c>。
/// 那会让编辑器静默播放别人的曲子：关卡目录里常见其他版本的音轨、打击音采样，或者背景视频附带的
/// 音轨。官方 <c>scnGame.ReloadSong</c> 在 <c>songFilename</c> 为空时就是「没有歌」，这里保持一致。
/// </para>
/// <para>
/// The old implementation scanned the whole level folder and took the first `.wav`/`.ogg` whenever
/// `songFilename` was empty or missing. That silently played someone else's track: level folders
/// routinely hold other versions of the song, hitsound samples, or the audio track shipped with a
/// background video. The official `scnGame.ReloadSong` treats an empty `songFilename` as "no song",
/// which is what this does too.
/// </para>
/// </remarks>
public static class LevelAudioLocator
{
    /// <summary>可作为音乐读取的扩展名 / Extensions that may be treated as music.</summary>
    /// <remarks>
    /// 包含解码器不支持的 <c>.m4a</c>/<c>.flac</c>：实时播放会失败并给出警告，但视频导出
    /// 仍可以用 ffmpeg 解码后混音，所以路径是有效的。
    /// Includes `.m4a`/`.flac` even though the decoder cannot read them: live playback reports a
    /// warning, but export can use ffmpeg to decode the source for mixing.
    /// </remarks>
    public static readonly string[] AudioExtensions = [.. AudioDecoder.SupportedExtensions, ".m4a", ".flac"];

    /// <summary>视频扩展名：绝不能被当成音频 / Video extensions, which must never be read as audio.</summary>
    public static readonly string[] VideoExtensions = LevelAssetPaths.VideoExtensions;

    /// <summary>是否为可接受的音频文件 / Whether the path looks like an acceptable audio file.</summary>
    public static bool IsAudioFile(string path)
    {
        string extension = Path.GetExtension(path);

        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析关卡音乐路径。没有声明或声明文件不存在时返回 <c>null</c>。
    /// Resolve the level music path; returns null when nothing is declared or the declared file is gone.
    /// </summary>
    /// <param name="level">已解析的关卡 / The parsed level.</param>
    /// <param name="levelPath">关卡文件路径 / Level file path.</param>
    public static string? Find(Level level, string levelPath)
    {
        ArgumentNullException.ThrowIfNull(level);

        string directory = Path.GetDirectoryName(Path.GetFullPath(levelPath)) ?? string.Empty;

        string declared;

        try
        {
            declared = level.GetSetting<string>("songFilename");
        }
        catch (Exception)
        {
            // songFilename 未声明（或不是字符串）：官方此时就是无音乐
            // No songFilename (or not a string): the official game plays no music either
            return null;
        }

        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        // 声明的是相对文件名时，按关卡目录解析，避免依赖进程工作目录
        // The declared name is usually relative; resolve it against the level folder so the process
        // working directory can never change the answer
        string full;
        try { full = Path.GetFullPath(declared, directory); }
        catch (ArgumentException) { return null; }

        if (File.Exists(full) && IsAudioFile(full))
        {
            return full;
        }

        // 同一主文件名换一个音频扩展名（声明 song.mp3、实际给了 song.ogg）
        // Same stem with another audio extension (song.mp3 declared, song.ogg shipped)
        foreach (string extension in AudioExtensions)
        {
            string candidate = Path.ChangeExtension(full, extension);

            if (string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(candidate) && IsAudioFile(candidate))
            {
                return candidate;
            }
        }

        // 声明了但文件名完全不对：不再猜，直接报告缺失
        // Declared, but no such file: report it as missing rather than guessing
        return null;
    }

    /// <summary>
    /// 该扩展名能否被内置解码器读取（决定是否需要警告）。
    /// Whether the built-in decoder can read this extension, which decides whether to warn.
    /// </summary>
    public static bool IsDecodable(string path)
        => AudioDecoder.IsSupported(path);
}
