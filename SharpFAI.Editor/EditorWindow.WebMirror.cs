using System.Text;
using ImGui4NET;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    internal EditorWebMirror? WebMirror { get; set; }
    private EditorWebMirror.Connection? _webClient;
    private bool _webInputActive;
    private bool _receivedWebInput;
    private readonly HashSet<Keys> _webKeys = [];
    private int _webButtons;

    private void FeedWebInput(ImGuiIO* io)
    {
        _receivedWebInput = false;
        var client = WebMirror?.Client;
        if (client != _webClient)
        {
            ReleaseWebInput();
            _webClient = client;
        }
        if (client is null) return;

        for (int i = 0; i < 128 && client.Input.Reader.TryRead(out var input); i++)
        {
            _receivedWebInput = true;
            if (input.Type == "reset")
            {
                ReleaseWebInput();
                continue;
            }
            if (!_webInputActive)
            {
                _webInputActive = true;
                ImGuiIO.AddFocusEvent(io, 1);
            }
            if (input.Type is "move" or "button" or "wheel")
                ImGuiIO.AddMousePosEvent(io, (float)(input.X * ClientSize.X), (float)(input.Y * ClientSize.Y));
            switch (input.Type)
            {
                case "button":
                    int index = input.Button switch { 1 => 2, 2 => 1, _ => 0 };
                    int mask = 1 << index;
                    if (input.Down == ((_webButtons & mask) != 0)) break;
                    _webButtons ^= mask;
                    ImGuiIO.AddMouseButtonEvent(io, index, (byte)(input.Down ? 1 : 0));
                    FeedPlaybackMouse((MouseButton)index, input.Down);
                    break;
                case "wheel":
                    KeyModifiers modifiers = WebModifiers();
                    FeedEditorWheel(io, (float)input.Dx, (float)input.Dy,
                        input.Ctrl ?? modifiers.HasFlag(KeyModifiers.Control),
                        input.Shift ?? modifiers.HasFlag(KeyModifiers.Shift));
                    break;
                case "key":
                    FeedWebKey(input.Code!, input.Down, input.Ctrl, input.Shift);
                    break;
                case "text":
                    foreach (Rune rune in input.Text!.EnumerateRunes())
                        if (!Rune.IsControl(rune) || rune.Value is '\n' or '\t')
                            ImGuiIO.AddInputCharacter(io, (uint)rune.Value);
                    break;
            }
        }
    }

    private void FeedWebKey(string code, bool down, bool? ctrl, bool? shift)
    {
        Keys key = BrowserKey(code);
        if (key == Keys.Unknown) return;
        bool changed = down ? _webKeys.Add(key) : _webKeys.Remove(key);
        if (!changed && (down || key is not (Keys.LeftControl or Keys.RightControl or Keys.LeftShift or Keys.RightShift))) return;
        KeyModifiers modifiers = WebModifiers();
        if (ctrl is bool control)
            modifiers = control ? modifiers | KeyModifiers.Control : modifiers & ~KeyModifiers.Control;
        if (shift is bool shifted)
            modifiers = shifted ? modifiers | KeyModifiers.Shift : modifiers & ~KeyModifiers.Shift;
        var args = new KeyboardKeyEventArgs(key, 0, modifiers, false);
        FeedEditorKey(args, down);
    }

    private void ReleaseWebInput()
    {
        if (!_webInputActive) return;
        _webInputActive = false;
        ImGuiIO* io = ImGui.GetIO();
        foreach (Keys key in _webKeys)
            FeedKey(new KeyboardKeyEventArgs(key, 0, 0, false), false);
        _webKeys.Clear();
        for (int i = 0; i < 3; i++)
            if ((_webButtons & (1 << i)) != 0)
                ImGuiIO.AddMouseButtonEvent(io, i, 0);
        _webButtons = 0;
        ImGuiIO.AddMousePosEvent(io, -float.MaxValue, -float.MaxValue);
        ResetCanvasGesture();
        _dragDecoration = null;
        ReleasePlaybackInput();
    }

    private KeyModifiers WebModifiers()
    {
        KeyModifiers modifiers = 0;
        if (_webKeys.Contains(Keys.LeftControl) || _webKeys.Contains(Keys.RightControl)) modifiers |= KeyModifiers.Control;
        if (_webKeys.Contains(Keys.LeftShift) || _webKeys.Contains(Keys.RightShift)) modifiers |= KeyModifiers.Shift;
        if (_webKeys.Contains(Keys.LeftAlt) || _webKeys.Contains(Keys.RightAlt)) modifiers |= KeyModifiers.Alt;
        if (_webKeys.Contains(Keys.LeftSuper) || _webKeys.Contains(Keys.RightSuper)) modifiers |= KeyModifiers.Super;
        return modifiers;
    }

    private static Keys BrowserKey(string code)
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
