using System.Text;
using ImGui4NET;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.ModApi;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly EditorWindowStream _windowStream = new();
    private EditorWindowStream.Session? _remoteSession;
    private bool _remoteInputActive;
    private bool _receivedRemoteInput;
    private readonly HashSet<Keys> _remoteKeys = [];
    private int _remoteButtons;

    private void FeedRemoteInput(ImGuiIO* io)
    {
        _receivedRemoteInput = false;
        var session = _windowStream.Current;
        if (session != _remoteSession)
        {
            ReleaseRemoteInput();
            _remoteSession = session;
        }
        if (session is null) return;

        for (int i = 0; i < 128 && session.TryRead(out var input); i++)
        {
            _receivedRemoteInput = true;
            if (input.Kind == EditorWindowInputKind.Reset)
            {
                ReleaseRemoteInput();
                continue;
            }
            if (!_remoteInputActive)
            {
                _remoteInputActive = true;
                ImGuiIO.AddFocusEvent(io, 1);
            }
            if (input.Kind is EditorWindowInputKind.Move or EditorWindowInputKind.Button or EditorWindowInputKind.Wheel)
                ImGuiIO.AddMousePosEvent(io, (float)(input.X * ClientSize.X), (float)(input.Y * ClientSize.Y));
            switch (input.Kind)
            {
                case EditorWindowInputKind.Button:
                    int index = input.Button switch { 1 => 2, 2 => 1, _ => 0 };
                    int mask = 1 << index;
                    if (input.Down == ((_remoteButtons & mask) != 0)) break;
                    _remoteButtons ^= mask;
                    ImGuiIO.AddMouseButtonEvent(io, index, (byte)(input.Down ? 1 : 0));
                    FeedPlaybackMouse((MouseButton)index, input.Down);
                    break;
                case EditorWindowInputKind.Wheel:
                    KeyModifiers modifiers = RemoteModifiers();
                    FeedEditorWheel(io, (float)input.DeltaX, (float)input.DeltaY,
                        input.Ctrl ?? modifiers.HasFlag(KeyModifiers.Control),
                        input.Shift ?? modifiers.HasFlag(KeyModifiers.Shift));
                    break;
                case EditorWindowInputKind.Key:
                    FeedRemoteKey(input.KeyCode!, input.Down, input.Ctrl, input.Shift);
                    break;
                case EditorWindowInputKind.Text:
                    foreach (Rune rune in input.Text!.EnumerateRunes())
                        if (!Rune.IsControl(rune) || rune.Value is '\n' or '\t')
                            ImGuiIO.AddInputCharacter(io, (uint)rune.Value);
                    break;
            }
        }
    }

    private void FeedRemoteKey(string code, bool down, bool? ctrl, bool? shift)
    {
        Keys key = RemoteKey(code);
        if (key == Keys.Unknown) return;
        bool changed = down ? _remoteKeys.Add(key) : _remoteKeys.Remove(key);
        if (!changed && (down || key is not (Keys.LeftControl or Keys.RightControl or Keys.LeftShift or Keys.RightShift))) return;
        KeyModifiers modifiers = RemoteModifiers();
        if (ctrl is bool control)
            modifiers = control ? modifiers | KeyModifiers.Control : modifiers & ~KeyModifiers.Control;
        if (shift is bool shifted)
            modifiers = shifted ? modifiers | KeyModifiers.Shift : modifiers & ~KeyModifiers.Shift;
        var args = new KeyboardKeyEventArgs(key, 0, modifiers, false);
        FeedEditorKey(args, down);
    }

    private void ReleaseRemoteInput()
    {
        if (!_remoteInputActive) return;
        _remoteInputActive = false;
        ImGuiIO* io = ImGui.GetIO();
        foreach (Keys key in _remoteKeys)
            FeedKey(new KeyboardKeyEventArgs(key, 0, 0, false), false);
        _remoteKeys.Clear();
        for (int i = 0; i < 3; i++)
            if ((_remoteButtons & (1 << i)) != 0)
                ImGuiIO.AddMouseButtonEvent(io, i, 0);
        _remoteButtons = 0;
        ImGuiIO.AddMousePosEvent(io, -float.MaxValue, -float.MaxValue);
        ResetCanvasGesture();
        _dragDecoration = null;
        ReleasePlaybackInput();
    }

    private KeyModifiers RemoteModifiers()
    {
        KeyModifiers modifiers = 0;
        if (_remoteKeys.Contains(Keys.LeftControl) || _remoteKeys.Contains(Keys.RightControl)) modifiers |= KeyModifiers.Control;
        if (_remoteKeys.Contains(Keys.LeftShift) || _remoteKeys.Contains(Keys.RightShift)) modifiers |= KeyModifiers.Shift;
        if (_remoteKeys.Contains(Keys.LeftAlt) || _remoteKeys.Contains(Keys.RightAlt)) modifiers |= KeyModifiers.Alt;
        if (_remoteKeys.Contains(Keys.LeftSuper) || _remoteKeys.Contains(Keys.RightSuper)) modifiers |= KeyModifiers.Super;
        return modifiers;
    }

    private static Keys RemoteKey(string code)
    {
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z')
            return Keys.A + (code[3] - 'A');
        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] is >= '0' and <= '9')
            return Keys.D0 + (code[5] - '0');
        return code switch
        {
            "ArrowLeft" => Keys.Left, "ArrowRight" => Keys.Right,
            "ArrowUp" => Keys.Up, "ArrowDown" => Keys.Down,
            "PageUp" => Keys.PageUp, "PageDown" => Keys.PageDown,
            "BracketLeft" => Keys.LeftBracket, "BracketRight" => Keys.RightBracket,
            "Semicolon" => Keys.Semicolon, "Minus" => Keys.Minus, "Equal" => Keys.Equal,
            "Comma" => Keys.Comma, "Period" => Keys.Period, "Slash" => Keys.Slash, "Backquote" => Keys.GraveAccent,
            "Home" => Keys.Home, "End" => Keys.End, "Delete" => Keys.Delete, "Backspace" => Keys.Backspace,
            "Tab" => Keys.Tab, "Enter" => Keys.Enter, "Escape" => Keys.Escape, "Space" => Keys.Space,
            "F5" => Keys.F5, "F7" => Keys.F7,
            "NumpadSubtract" => Keys.KeyPadSubtract, "NumpadAdd" => Keys.KeyPadAdd, "NumpadEnter" => Keys.KeyPadEnter,
            "ControlLeft" => Keys.LeftControl, "ControlRight" => Keys.RightControl,
            "ShiftLeft" => Keys.LeftShift, "ShiftRight" => Keys.RightShift,
            "AltLeft" => Keys.LeftAlt, "AltRight" => Keys.RightAlt,
            "MetaLeft" => Keys.LeftSuper, "MetaRight" => Keys.RightSuper,
            _ => Keys.Unknown
        };
    }
}
