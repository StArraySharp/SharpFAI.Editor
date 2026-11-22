using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

/// <summary>
/// 关卡目录里的素材路径规则：什么是视频、导出文件不能占用哪个名字。
/// Rules about the assets in a level folder: what counts as a video, and which names an export must
/// leave alone.
/// </summary>
public static class LevelAssetPaths
{
    /// <summary>视频扩展名 / Video extensions.</summary>
    public static readonly string[] VideoExtensions =
        [".mp4", ".webm", ".mov", ".avi", ".mkv", ".flv", ".wmv", ".m4v", ".mpg", ".mpeg"];

    /// <summary>
    /// 是否为视频文件。视频带着音轨，既不能当音乐播放，也不能当 ffmpeg 的音频输入。
    /// Whether the path is a video. Videos carry an audio track, so they are neither music nor a
    /// valid ffmpeg audio input.
    /// </summary>
    public static bool IsVideoFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string extension;

        try
        {
            extension = Path.GetExtension(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>可能指向本地素材的设置项 / Settings that can point at a local asset.</summary>
    private static readonly string[] AssetKeys = ["songFilename", "bgVideo", "bgImage", "previewImage"];

    /// <summary>
    /// 选定导出视频的输出路径，并避开关卡自己引用的素材文件。
    /// Pick the export path without clobbering an asset the level itself references.
    /// </summary>
    /// <remarks>
    /// 关卡目录里常见背景视频、预览图和歌曲。如果 <c>level.mp4</c> 正好就是关卡引用的背景视频，
    /// <c>ffmpeg -y</c> 会把它原地覆盖，关卡随后就再也加载不到那段视频了。
    /// Level folders commonly hold a background video, preview image and song. If `level.mp4` happens
    /// to be the background video the level references, `ffmpeg -y` overwrites it in place and the
    /// level can never load that video again.
    /// </remarks>
    public static string ResolveExportPath(string levelPath, Level? level)
    {
        string full = Path.GetFullPath(levelPath);
        string directory = Path.GetDirectoryName(full) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(full);

        string candidate = Path.Combine(directory, stem + ".mp4");

        if (!File.Exists(candidate) || !IsReferencedAsset(candidate, level))
        {
            return candidate;
        }

        // 名字被关卡引用了：换一个不会撞车的名字
        // The name belongs to a level asset, so pick one that cannot collide
        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string alternative = Path.Combine(directory, $"{stem}-export{suffix}.mp4");

            if (!File.Exists(alternative))
            {
                return alternative;
            }
        }

        return candidate;
    }

    /// <summary>该文件是否被关卡设置引用 / Whether a level setting references this file.</summary>
    private static bool IsReferencedAsset(string path, Level? level)
    {
        if (level is null)
        {
            return false;
        }

        string name = Path.GetFileName(path);

        foreach (string key in AssetKeys)
        {
            string declared;

            try
            {
                if (!level.HasSetting(key))
                {
                    continue;
                }

                declared = level.GetSetting<string>(key);
            }
            catch (Exception)
            {
                // 类型异常就当没声明 / A type problem means "not declared"
                continue;
            }

            if (string.IsNullOrWhiteSpace(declared))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(declared), name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
