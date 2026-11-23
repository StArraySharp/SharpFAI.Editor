using System.Runtime.InteropServices;
using ImGui4NET;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SharpFAI.Player;

// Use on the window's owning thread. No system keyboard layout or IME settings are changed.
internal sealed unsafe class WindowInputMethod(nint window) : IDisposable
{
    internal const uint InputLanguageChangeRequest = 0x0050;
    private bool _suppressed;
    private nint _previousContext;

    public static WindowInputMethod? Create(Window* window)
    {
        if (!OperatingSystem.IsWindows() || window == null)
            return null;

        nint handle = GLFW.GetWin32Window(window);
        return handle != 0 ? new WindowInputMethod(handle) : null;
    }

    public void Update(bool focused, bool selectingOrPlaying) =>
        Update(focused, selectingOrPlaying, HasTextFocus(ImGui.GetCurrentContext()));

    internal static bool HasTextFocus(ImGuiContext* context) =>
        context != null && context->ActiveId != 0 && context->ActiveId == context->InputTextState.ID;

    internal void Update(bool focused, bool selectingOrPlaying, bool textFocused)
    {
        bool suppress = focused && selectingOrPlaying && !textFocused;
        if (suppress == _suppressed)
            return;

        if (suppress)
        {
            if (SetWindowSubclass(window, &WindowProc, 0, 0) == 0)
                return;

            // Reject layout changes and detach composition; either measure alone misses some IMEs.
            _suppressed = true;
            _previousContext = ImmAssociateContext(window, 0);
        }
        else
        {
            Restore();
        }
    }

    public void Restore()
    {
        if (!_suppressed)
            return;

        _suppressed = false;
        RemoveWindowSubclass(window, &WindowProc, 0);
        if (_previousContext != 0)
        {
            ImmAssociateContext(window, _previousContext);
            _previousContext = 0;
        }
    }

    public void Dispose() => Restore();

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data) =>
        message == InputLanguageChangeRequest ? 0 : DefSubclassProc(window, message, wParam, lParam);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int SetWindowSubclass(nint window,
        delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint data);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int RemoveWindowSubclass(nint window,
        delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern nint ImmAssociateContext(nint window, nint context);
}
