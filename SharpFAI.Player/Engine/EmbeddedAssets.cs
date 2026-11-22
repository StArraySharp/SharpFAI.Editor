using System.Reflection;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

/// <summary>
/// The editor's own embedded-asset reader. The engine library ships no resources, so textures and
/// shaders are the host's job and this lives in the executable; pixel work reuses Thrust's
/// <see cref="ImageDecoder"/>.
/// </summary>
public static class EmbeddedAssets
{
    private static readonly Assembly Assembly = typeof(EmbeddedAssets).Assembly;

    /// <summary>
    /// Read a resource from this assembly by suffix, returning null when it is missing.
    /// </summary>
    public static byte[]? Read(string suffix)
    {
        foreach (string candidate in Assembly.GetManifestResourceNames())
        {
            if (!candidate.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            using Stream? stream = Assembly.GetManifestResourceStream(candidate);

            if (stream is null)
            {
                continue;
            }

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Flip pixel rows in place; see <see cref="ImageDecoder.FlipVertically"/>.
    /// </summary>
    public static void FlipVertically(byte[] pixels, int width, int height)
        => ImageDecoder.FlipVertically(pixels, width, height);
}
