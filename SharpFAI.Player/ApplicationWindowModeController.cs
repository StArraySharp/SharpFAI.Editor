using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SharpFAI.Player;

internal sealed class ApplicationWindowModeController
{
    private sealed record WindowedPlacement(Vector2i Location, Vector2i Size, WindowBorder Border, WindowState State);

    private WindowedPlacement? _windowed;
    private bool _togglePending;

    public bool IsFullscreen => _windowed is not null;

    public static bool IsToggleShortcut(KeyboardKeyEventArgs args) =>
        args.Alt && args.Key is Keys.Enter or Keys.KeyPadEnter;

    public void RequestToggle() => _togglePending = !_togglePending;

    public ApplicationWindowMode? ApplyPending(GameWindow window)
    {
        if (!_togglePending)
            return null;
        _togglePending = false;
        var mode = IsFullscreen ? ApplicationWindowMode.Windowed : ApplicationWindowMode.Fullscreen;
        Apply(window, mode);
        return IsFullscreen ? ApplicationWindowMode.Fullscreen : ApplicationWindowMode.Windowed;
    }

    public void Apply(GameWindow window, ApplicationWindowMode mode)
    {
        if (mode == ApplicationWindowMode.Windowed)
        {
            if (_windowed is not { } placement)
                return;
            _windowed = null;
            window.WindowState = WindowState.Normal;
            window.WindowBorder = placement.Border;
            window.ClientLocation = placement.Location;
            window.ClientSize = placement.Size;
            window.WindowState = placement.State;
            return;
        }

        if (mode != ApplicationWindowMode.Fullscreen || IsFullscreen ||
            (Monitors.GetMonitorFromWindow(window) ?? Monitors.GetPrimaryMonitor()) is not { } monitor)
            return;

        WindowState state = window.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        window.WindowState = WindowState.Normal;
        _windowed = new(window.ClientLocation, window.ClientSize, window.WindowBorder, state);

        // A desktop window covering the monitor keeps normal focus and stacking behavior.
        window.WindowBorder = WindowBorder.Hidden;
        window.ClientLocation = monitor.ClientArea.Min;
        window.ClientSize = monitor.ClientArea.Size;
    }
}
