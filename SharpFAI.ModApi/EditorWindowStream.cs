namespace SharpFAI.ModApi;

public enum EditorWindowInputKind { Move, Button, Wheel, Key, Text, Reset }

/// <summary>Pointer coordinates are normalized to the client area. Buttons 0, 1 and 2 are left, middle and right.</summary>
public sealed record EditorWindowInput(EditorWindowInputKind Kind, double X = 0, double Y = 0,
    double DeltaX = 0, double DeltaY = 0, int Button = 0, bool Down = false,
    string? KeyCode = null, string? Text = null, bool? Ctrl = null, bool? Shift = null);

public enum EditorWindowFrameFormat { Rgba8, Jpeg }

/// <summary>Uncompressed RGBA8 frames store rows in top-to-bottom order.</summary>
public sealed record EditorWindowFrame(int Width, int Height, EditorWindowFrameFormat Format, byte[] Data);

/// <summary>Provides one exclusive session for capturing the visible editor and forwarding input.</summary>
public interface IEditorWindowStream
{
    IEditorWindowStreamSession? TryOpen();
}

public interface IEditorWindowStreamSession : IDisposable
{
    /// <summary>Requests the next completed window frame, including editor UI. Allow one outstanding request per session.</summary>
    ValueTask<EditorWindowFrame> CaptureAsync(CancellationToken cancellationToken,
        EditorWindowFrameFormat format = EditorWindowFrameFormat.Rgba8);

    /// <summary>Submits input in order. Keys use physical code names such as KeyA, Digit1 and ArrowLeft.</summary>
    ValueTask SendInputAsync(EditorWindowInput input, CancellationToken cancellationToken);
}
