using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SharpFAI.Player;

internal sealed class ApplicationWindowModeController
{
    private WindowState _windowedState = WindowState.Normal;
    private bool _togglePending;

    public static bool IsToggleShortcut(KeyboardKeyEventArgs args) =>
        args.Alt && args.Key is Keys.Enter or Keys.KeyPadEnter;

    public void RequestToggle() => _togglePending = !_togglePending;

    public ApplicationWindowMode? ApplyPending(GameWindow window)
    {
        if (!_togglePending)
            return null;
        _togglePending = false;
        var mode = window.IsFullscreen ? ApplicationWindowMode.Windowed : ApplicationWindowMode.Fullscreen;
        Apply(window, mode);
        return mode;
    }

    public void Apply(GameWindow window, ApplicationWindowMode mode)
    {
        if (window.IsFullscreen)
        {
            if (mode == ApplicationWindowMode.Windowed)
                window.WindowState = _windowedState;
        }
        else if (mode == ApplicationWindowMode.Fullscreen)
        {
            _windowedState = window.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            window.WindowState = WindowState.Fullscreen;
        }
    }
}
