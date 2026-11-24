using OpenTK.Windowing.Common.Input;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Player;

internal static class ApplicationIcon
{
    internal static WindowIcon? Load()
    {
        if (!OperatingSystem.IsWindows() || EmbeddedAssets.Read("Resources.SharpFAI.png") is not { } png)
            return null;

        var image = ImageDecoder.Decode(png);
        return new WindowIcon(new Image(image.Width, image.Height, image.Pixels));
    }
}
