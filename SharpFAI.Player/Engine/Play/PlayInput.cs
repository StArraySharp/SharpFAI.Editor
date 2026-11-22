using System.Runtime.InteropServices;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SharpFAI.Player.Engine.Play;

public sealed class PlayInput
{
    public readonly record struct KeyEvent(double Time, bool Pressed, int Code);

    private readonly HashSet<int> _held = [];
    private readonly List<KeyEvent> _pending = [];

    public bool Enabled { get; set; }
    public double MarginMultiplier { get; set; } = 1.0;
    public int PendingCount => _pending.Count;
    internal ReadOnlySpan<KeyEvent> Pending => CollectionsMarshal.AsSpan(_pending);

    public bool IsHitKey(Keys key) => key is not (Keys.Escape or Keys.Unknown);

    public bool Push(Keys key, bool pressed, double time) =>
        IsHitKey(key) && Push((int)key, pressed, time);

    public bool Push(MouseButton button, bool pressed, double time) =>
        (button is MouseButton.Left or MouseButton.Right) && Push(1024 + (int)button, pressed, time);

    private bool Push(int code, bool pressed, double time)
    {
        if (!Enabled || !(pressed ? _held.Add(code) : _held.Remove(code)))
            return false;

        _pending.Add(new KeyEvent(time, pressed, code));
        return pressed;
    }

    public void Clear() => _pending.Clear();

    public void ReleaseAll(double? time = null)
    {
        if (Enabled && time is double timestamp)
        {
            foreach (int code in _held)
                _pending.Add(new KeyEvent(timestamp, false, code));
        }
        _held.Clear();
    }
}
