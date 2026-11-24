using System.Text.RegularExpressions;

namespace SharpFAI.Player.Engine.Filters;

internal readonly struct FilterTextureSampling(bool mipmaps, int filterMode, int wrapU, int wrapV)
{
    internal static readonly FilterTextureSampling Default = new(true, 1, 0, 0);

    public bool Mipmaps { get; } = mipmaps;
    public int FilterMode { get; } = filterMode;
    public int WrapU { get; } = wrapU;
    public int WrapV { get; } = wrapV;

    internal static FilterTextureSampling Parse(string metadata)
    {
        return new(Read("enableMipMap", 1) != 0, Read("filterMode", 1), Read("wrapU", 0), Read("wrapV", 0));

        int Read(string name, int fallback)
        {
            var value = Regex.Match(metadata, @"(?m)^\s*" + name + @":[ \t]*(-?\d+)");
            return value.Success && int.TryParse(value.Groups[1].Value, out int number) ? number : fallback;
        }
    }
}
